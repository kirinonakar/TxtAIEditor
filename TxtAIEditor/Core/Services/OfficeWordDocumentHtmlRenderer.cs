using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWordDocumentHtmlRenderer
    {
        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            XDocument? document = await TryLoadXmlEntryAsync(archive, "word/document.xml").ConfigureAwait(false);
            if (document == null)
            {
                return BuildErrorHtml(
                    getString("OfficeViewerDocxStructureError", "Could not read the DOCX document structure."));
            }

            IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                archive,
                "word/_rels/document.xml.rels",
                "word").ConfigureAwait(false);

            XElement? body = document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? document.Root;
            if (body == null)
            {
                return BuildErrorHtml(
                    getString("OfficeViewerNoBody", "No document body to display."));
            }

            var content = new StringBuilder();
            foreach (XElement block in body.Elements())
            {
                AppendBlockHtml(content, archive, relationships, block);
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(
                    getString("OfficeViewerNoContent", "No content to display."));
            }

            return BuildDocumentHtml(Path.GetFileName(filePath), content.ToString());
        }

        private static void AppendBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement block)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(BuildParagraphHtml(archive, relationships, block));
                    break;
                case "tbl":
                    builder.Append(BuildTableHtml(archive, relationships, block));
                    break;
                default:
                    foreach (XElement child in block.Elements())
                    {
                        AppendBlockHtml(builder, archive, relationships, child);
                    }

                    break;
            }
        }

        private static string BuildParagraphHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement paragraph)
        {
            var content = new StringBuilder();
            foreach (XElement child in paragraph.Elements())
            {
                AppendInlineHtml(content, archive, relationships, child);
            }

            return content.Length == 0
                ? "<p class=\"doc-paragraph empty-paragraph\"></p>"
                : "<p class=\"doc-paragraph\">" + content + "</p>";
        }

        private static void AppendInlineHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "r":
                    AppendRunHtml(builder, archive, relationships, element);
                    break;
                case "hyperlink":
                case "ins":
                case "smartTag":
                case "sdt":
                case "sdtContent":
                    foreach (XElement child in element.Elements())
                    {
                        AppendInlineHtml(builder, archive, relationships, child);
                    }

                    break;
                case "tab":
                    builder.Append('\t');
                    break;
                case "br":
                case "cr":
                    builder.Append("<br>");
                    break;
                case "drawing":
                case "pict":
                    AppendImagesHtml(builder, archive, relationships, element);
                    break;
                default:
                    foreach (XElement child in element.Elements())
                    {
                        AppendInlineHtml(builder, archive, relationships, child);
                    }

                    break;
            }
        }

        private static void AppendRunHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement run)
        {
            XElement? properties = run.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
            string style = BuildRunStyle(properties);
            foreach (XElement child in run.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "t":
                        AppendStyledText(builder, child.Value, style);
                        break;
                    case "tab":
                        builder.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        builder.Append("<br>");
                        break;
                    case "drawing":
                    case "pict":
                        AppendImagesHtml(builder, archive, relationships, child);
                        break;
                }
            }
        }

        private static string BuildRunStyle(XElement? properties)
        {
            if (properties == null)
            {
                return string.Empty;
            }

            var styles = new List<string>();
            if (properties.Elements().Any(e => e.Name.LocalName == "b"))
            {
                styles.Add("font-weight:700");
            }

            if (properties.Elements().Any(e => e.Name.LocalName == "i"))
            {
                styles.Add("font-style:italic");
            }

            if (properties.Elements().Any(e => e.Name.LocalName == "u"))
            {
                styles.Add("text-decoration:underline");
            }

            XElement? color = properties.Elements().FirstOrDefault(e => e.Name.LocalName == "color");
            string colorValue = GetAttributeValue(color, "val");
            if (Regex.IsMatch(colorValue, "^[0-9A-Fa-f]{6}$"))
            {
                styles.Add("color:#" + colorValue);
            }

            return string.Join(';', styles);
        }

        private static void AppendStyledText(StringBuilder builder, string text, string style)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(style))
            {
                builder.Append(Html(text));
                return;
            }

            builder.Append("<span style=\"")
                .Append(Html(style))
                .Append("\">")
                .Append(Html(text))
                .Append("</span>");
        }

        private static void AppendImagesHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement element)
        {
            var relationshipIds = element.Descendants()
                .Where(e => e.Name.LocalName == "blip" || e.Name.LocalName == "imagedata")
                .Select(e => GetAttributeValue(e, "embed"))
                .Concat(element.Descendants()
                    .Where(e => e.Name.LocalName == "blip" || e.Name.LocalName == "imagedata")
                    .Select(e => GetAttributeValue(e, "id")))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string relationshipId in relationshipIds)
            {
                if (!relationships.TryGetValue(relationshipId, out string? imagePath))
                {
                    continue;
                }

                string? dataUri = TryReadImageDataUri(archive, imagePath);
                if (string.IsNullOrWhiteSpace(dataUri))
                {
                    continue;
                }

                builder.Append("<figure class=\"doc-image\"><img src=\"")
                    .Append(Html(dataUri))
                    .Append("\" alt=\"\"></figure>");
            }
        }

        private static string BuildTableHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement table)
        {
            if (!HasTableContent(table))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append("<div class=\"doc-table-wrap\"><table class=\"doc-table\"><tbody>");
            foreach (XElement row in table.Elements().Where(e => e.Name.LocalName == "tr"))
            {
                builder.Append("<tr>");
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    string colspan = ReadGridSpan(cell);
                    builder.Append("<td");
                    if (!string.IsNullOrWhiteSpace(colspan))
                    {
                        builder.Append(" colspan=\"")
                            .Append(Html(colspan))
                            .Append('"');
                    }

                    builder.Append('>');
                    int before = builder.Length;
                    foreach (XElement child in cell.Elements())
                    {
                        AppendBlockHtml(builder, archive, relationships, child);
                    }

                    if (builder.Length == before)
                    {
                        builder.Append("&nbsp;");
                    }

                    builder.Append("</td>");
                }

                builder.Append("</tr>");
            }

            builder.Append("</tbody></table></div>");
            return builder.ToString();
        }

        private static bool HasTableContent(XElement table)
        {
            return table.Descendants().Any(element =>
                element.Name.LocalName is "drawing" or "pict" ||
                (element.Name.LocalName == "t" && !string.IsNullOrWhiteSpace(element.Value)));
        }

        private static string ReadGridSpan(XElement cell)
        {
            XElement? gridSpan = cell.Descendants().FirstOrDefault(e => e.Name.LocalName == "gridSpan");
            string value = GetAttributeValue(gridSpan, "val");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) && span > 1
                ? span.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value
                ?? string.Empty;
        }
    }
}
