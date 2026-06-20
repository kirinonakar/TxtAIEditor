using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeTextDocumentHtmlRenderer
    {
        private sealed class HwpxBinaryItem
        {
            public string Path { get; init; } = string.Empty;
            public string? MimeType { get; init; }
        }

        public static async Task<string> BuildWordAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            XDocument? document = await TryLoadXmlEntryAsync(archive, "word/document.xml").ConfigureAwait(false);
            if (document == null)
            {
                return BuildErrorHtml(getString("OfficeViewerDocxStructureError", "Could not read the DOCX document structure."));
            }

            IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                archive,
                "word/_rels/document.xml.rels",
                "word").ConfigureAwait(false);

            XElement? body = document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? document.Root;
            if (body == null)
            {
                return BuildErrorHtml(getString("OfficeViewerNoBody", "No document body to display."));
            }

            var content = new StringBuilder();
            foreach (XElement block in body.Elements())
            {
                AppendDocxBlockHtml(content, archive, relationships, block);
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoContent", "No content to display."));
            }

            return BuildDocumentHtml(Path.GetFileName(filePath), content.ToString());
        }

        public static async Task<string> BuildHwpxAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems = await LoadHwpxBinaryItemsAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> characterStyles = await LoadHwpxCharacterStylesAsync(archive).ConfigureAwait(false);
            var sectionEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            if (sectionEntries.Count == 0)
            {
                sectionEntries = archive.Entries
                    .Where(entry =>
                        entry.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FullName.EndsWith("/header.xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (sectionEntries.Count == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerHwpxStructureError", "Could not read the HWPX document structure."));
            }

            var content = new StringBuilder();
            foreach (ZipArchiveEntry sectionEntry in sectionEntries)
            {
                XDocument section = await LoadXmlEntryAsync(sectionEntry).ConfigureAwait(false);
                AppendHwpxChildrenHtml(content, archive, binaryItems, characterStyles, section.Root?.Elements() ?? Enumerable.Empty<XElement>());
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoContent", "No content to display."));
            }

            return BuildDocumentHtml(Path.GetFileName(filePath), content.ToString());
        }

        private static void AppendDocxBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement block)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(BuildDocxParagraphHtml(archive, relationships, block));
                    break;
                case "tbl":
                    builder.Append(BuildDocxTableHtml(archive, relationships, block));
                    break;
                default:
                    foreach (XElement child in block.Elements())
                    {
                        AppendDocxBlockHtml(builder, archive, relationships, child);
                    }

                    break;
            }
        }

        private static string BuildDocxParagraphHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement paragraph)
        {
            var content = new StringBuilder();
            foreach (XElement child in paragraph.Elements())
            {
                AppendDocxInlineHtml(content, archive, relationships, child);
            }

            return content.Length == 0
                ? "<p class=\"doc-paragraph empty-paragraph\"></p>"
                : "<p class=\"doc-paragraph\">" + content + "</p>";
        }

        private static void AppendDocxInlineHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "r":
                    AppendDocxRunHtml(builder, archive, relationships, element);
                    break;
                case "hyperlink":
                case "ins":
                case "smartTag":
                case "sdt":
                case "sdtContent":
                    foreach (XElement child in element.Elements())
                    {
                        AppendDocxInlineHtml(builder, archive, relationships, child);
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
                    AppendDocxImagesHtml(builder, archive, relationships, element);
                    break;
                default:
                    foreach (XElement child in element.Elements())
                    {
                        AppendDocxInlineHtml(builder, archive, relationships, child);
                    }

                    break;
            }
        }

        private static void AppendDocxRunHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement run)
        {
            XElement? properties = run.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
            string style = BuildDocxRunStyle(properties);
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
                        AppendDocxImagesHtml(builder, archive, relationships, child);
                        break;
                }
            }
        }

        private static string BuildDocxRunStyle(XElement? properties)
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

        private static void AppendDocxImagesHtml(
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

        private static string BuildDocxTableHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement table)
        {
            var builder = new StringBuilder();
            builder.Append("<div class=\"doc-table-wrap\"><table class=\"doc-table\"><tbody>");
            foreach (XElement row in table.Elements().Where(e => e.Name.LocalName == "tr"))
            {
                builder.Append("<tr>");
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    string colspan = ReadDocxGridSpan(cell);
                    builder.Append("<td");
                    if (!string.IsNullOrWhiteSpace(colspan))
                    {
                        builder.Append(" colspan=\"").Append(Html(colspan)).Append('"');
                    }

                    builder.Append('>');
                    int before = builder.Length;
                    foreach (XElement child in cell.Elements())
                    {
                        AppendDocxBlockHtml(builder, archive, relationships, child);
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

        private static string ReadDocxGridSpan(XElement cell)
        {
            XElement? gridSpan = cell.Descendants().FirstOrDefault(e => e.Name.LocalName == "gridSpan");
            string value = GetAttributeValue(gridSpan, "val");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) && span > 1
                ? span.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static void AppendHwpxChildrenHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IEnumerable<XElement> elements)
        {
            foreach (XElement element in elements)
            {
                AppendHwpxBlockHtml(builder, archive, binaryItems, characterStyles, element);
            }
        }

        private static void AppendHwpxBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            XElement block)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(BuildHwpxParagraphHtml(archive, binaryItems, characterStyles, block));
                    foreach (XElement table in block.Descendants().Where(e => e.Name.LocalName == "tbl"))
                    {
                        builder.Append(BuildHwpxTableHtml(archive, binaryItems, characterStyles, table));
                    }

                    break;
                case "tbl":
                    builder.Append(BuildHwpxTableHtml(archive, binaryItems, characterStyles, block));
                    break;
                default:
                    AppendHwpxChildrenHtml(builder, archive, binaryItems, characterStyles, block.Elements());
                    break;
            }
        }

        private static string BuildHwpxParagraphHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            XElement paragraph)
        {
            var content = new StringBuilder();
            var renderedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (IsInsideNestedElement(paragraph, node, "tbl"))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    AppendStyledText(content, textNode.Value, GetHwpxTextStyle(textNode, characterStyles));
                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "tab":
                        content.Append('\t');
                        break;
                    case "lineBreak":
                    case "br":
                    case "cr":
                        content.Append("<br>");
                        break;
                    case "nbSpace":
                        content.Append(' ');
                        break;
                    case "fwSpace":
                        content.Append("&#12288;");
                        break;
                    case "pic":
                    case "img":
                        AppendHwpxImageHtml(content, archive, binaryItems, element, renderedImages);
                        break;
                }
            }

            return content.Length == 0
                ? "<p class=\"doc-paragraph empty-paragraph\"></p>"
                : "<p class=\"doc-paragraph\">" + content + "</p>";
        }

        private static string BuildHwpxTableHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            XElement table)
        {
            var rows = table.Elements().Where(e => e.Name.LocalName == "tr").ToList();
            if (rows.Count == 0)
            {
                rows = table.Descendants().Where(e => e.Name.LocalName == "tr").ToList();
            }

            var builder = new StringBuilder();
            builder.Append("<div class=\"doc-table-wrap\"><table class=\"doc-table\"><tbody>");
            foreach (XElement row in rows)
            {
                builder.Append("<tr>");
                var cells = row.Elements().Where(e => e.Name.LocalName == "tc").ToList();
                if (cells.Count == 0)
                {
                    cells = row.Descendants().Where(e => e.Name.LocalName == "tc").ToList();
                }

                foreach (XElement cell in cells)
                {
                    string colspan = ReadPositiveIntegerAttribute(cell, "colSpan");
                    string rowspan = ReadPositiveIntegerAttribute(cell, "rowSpan");
                    builder.Append("<td");
                    if (!string.IsNullOrWhiteSpace(colspan))
                    {
                        builder.Append(" colspan=\"").Append(Html(colspan)).Append('"');
                    }

                    if (!string.IsNullOrWhiteSpace(rowspan))
                    {
                        builder.Append(" rowspan=\"").Append(Html(rowspan)).Append('"');
                    }

                    builder.Append('>');
                    int before = builder.Length;
                    AppendHwpxChildrenHtml(builder, archive, binaryItems, characterStyles, cell.Elements());
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

        private static void AppendHwpxImageHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            XElement element,
            ISet<string> renderedImages)
        {
            string imagePath = ResolveHwpxImagePath(element, binaryItems);
            if (string.IsNullOrWhiteSpace(imagePath) || !renderedImages.Add(imagePath))
            {
                return;
            }

            string? dataUri = TryReadImageDataUri(archive, imagePath);
            if (string.IsNullOrWhiteSpace(dataUri))
            {
                return;
            }

            builder.Append("<figure class=\"doc-image\"><img src=\"")
                .Append(Html(dataUri))
                .Append("\" alt=\"\"></figure>");
        }

        private static string GetHwpxTextStyle(XText textNode, IReadOnlyDictionary<string, string> characterStyles)
        {
            XElement? parent = textNode.Parent;
            while (parent != null)
            {
                if (parent.Name.LocalName == "run")
                {
                    string styleId = GetAttributeValue(parent, "charPrIDRef");
                    return characterStyles.TryGetValue(styleId, out string? style) ? style : string.Empty;
                }

                parent = parent.Parent;
            }

            return string.Empty;
        }

        private static string ResolveHwpxImagePath(XElement element, IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems)
        {
            foreach (string attributeName in new[] { "binaryItemIDRef", "binItemIDRef", "refID", "refId" })
            {
                string id = GetAttributeValue(element, attributeName);
                if (!string.IsNullOrWhiteSpace(id) && binaryItems.TryGetValue(id, out HwpxBinaryItem? item))
                {
                    return item.Path;
                }
            }

            foreach (string attributeName in new[] { "href", "path", "target" })
            {
                string path = GetAttributeValue(element, attributeName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return NormalizeHwpxBinaryPath(path);
                }
            }

            foreach (XElement child in element.Descendants())
            {
                string childPath = ResolveHwpxImagePath(child, binaryItems);
                if (!string.IsNullOrWhiteSpace(childPath))
                {
                    return childPath;
                }
            }

            return string.Empty;
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadHwpxCharacterStylesAsync(ZipArchive archive)
        {
            XDocument? header = await TryLoadXmlEntryAsync(archive, "Contents/header.xml").ConfigureAwait(false);
            if (header == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            IReadOnlyDictionary<string, string> fontNames = LoadHwpxFontNames(header);
            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in header.Descendants().Where(e => e.Name.LocalName == "charPr"))
            {
                string id = GetAttributeValue(element, "id");
                string style = BuildHwpxCharacterStyle(element, fontNames);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(style))
                {
                    styles[id] = style;
                }
            }

            return styles;
        }

        private static IReadOnlyDictionary<string, string> LoadHwpxFontNames(XDocument header)
        {
            XElement? fontFace = header.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "fontface" &&
                    GetAttributeValue(e, "lang").Equals("HANGUL", StringComparison.OrdinalIgnoreCase)) ??
                header.Descendants().FirstOrDefault(e => e.Name.LocalName == "fontface");

            if (fontFace == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return fontFace.Elements()
                .Where(e => e.Name.LocalName == "font")
                .Select(e => new
                {
                    Id = GetAttributeValue(e, "id"),
                    Face = GetAttributeValue(e, "face")
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Face))
                .ToDictionary(x => x.Id, x => x.Face, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildHwpxCharacterStyle(XElement charPr, IReadOnlyDictionary<string, string> fontNames)
        {
            var styles = new List<string>();

            string textColor = GetAttributeValue(charPr, "textColor");
            if (IsCssColor(textColor) && !IsDefaultTextColor(textColor))
            {
                styles.Add("color:" + textColor);
            }

            string shadeColor = GetAttributeValue(charPr, "shadeColor");
            if (IsCssColor(shadeColor) && !IsDefaultShadeColor(shadeColor))
            {
                styles.Add("background-color:" + shadeColor);
            }

            if (int.TryParse(GetAttributeValue(charPr, "height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) &&
                height > 0)
            {
                styles.Add("font-size:" + (height / 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "pt");
            }

            XElement? fontRef = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "fontRef");
            string fontId = GetAttributeValue(fontRef, "hangul");
            if (!string.IsNullOrWhiteSpace(fontId) && fontNames.TryGetValue(fontId, out string? face))
            {
                styles.Add("font-family:" + QuoteCssFontFamily(face) + ", \"Malgun Gothic\", sans-serif");
            }

            if (charPr.Elements().Any(e => e.Name.LocalName == "bold"))
            {
                styles.Add("font-weight:700");
            }

            if (charPr.Elements().Any(e => e.Name.LocalName == "italic"))
            {
                styles.Add("font-style:italic");
            }

            var decorations = new List<string>();
            XElement? underline = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "underline");
            if (underline != null && !GetAttributeValue(underline, "type").Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                decorations.Add("underline");
            }

            XElement? strikeout = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "strikeout");
            if (strikeout != null && !GetAttributeValue(strikeout, "shape").Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                decorations.Add("line-through");
            }

            if (decorations.Count > 0)
            {
                styles.Add("text-decoration:" + string.Join(' ', decorations));
            }

            XElement? spacing = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "spacing");
            if (int.TryParse(GetAttributeValue(spacing, "hangul"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int spacingValue) &&
                spacingValue != 0)
            {
                styles.Add("letter-spacing:" + (spacingValue / 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "em");
            }

            return string.Join(';', styles);
        }

        private static bool IsCssColor(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, "^#[0-9A-Fa-f]{6}$");
        }

        private static bool IsDefaultTextColor(string value)
        {
            return value.Equals("#000000", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultShadeColor(string value)
        {
            return value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteCssFontFamily(string value)
        {
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static async Task<IReadOnlyDictionary<string, HwpxBinaryItem>> LoadHwpxBinaryItemsAsync(ZipArchive archive)
        {
            var items = new Dictionary<string, HwpxBinaryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument? doc;
                try
                {
                    doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                foreach (XElement element in doc.Descendants().Where(e => e.Name.LocalName == "binItem"))
                {
                    string id = GetAttributeValue(element, "id");
                    string href = GetAttributeValue(element, "href");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
                    {
                        continue;
                    }

                    items[id] = new HwpxBinaryItem
                    {
                        Path = NormalizeHwpxBinaryPath(href),
                        MimeType = GetAttributeValue(element, "media-type")
                    };
                }
            }

            foreach (ZipArchiveEntry entry in archive.Entries.Where(e =>
                e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) &&
                IsSupportedImagePath(e.FullName)))
            {
                string fileName = Path.GetFileName(entry.FullName);
                string stem = Path.GetFileNameWithoutExtension(entry.FullName);
                AddHwpxBinaryItem(items, stem, entry.FullName);
                AddHwpxBinaryItem(items, fileName, entry.FullName);
                AddHwpxBinaryItem(items, entry.FullName, entry.FullName);
            }

            return items;
        }

        private static void AddHwpxBinaryItem(IDictionary<string, HwpxBinaryItem> items, string id, string path)
        {
            if (string.IsNullOrWhiteSpace(id) || items.ContainsKey(id))
            {
                return;
            }

            items[id] = new HwpxBinaryItem
            {
                Path = NormalizeZipPath(string.Empty, path),
                MimeType = GetImageMimeType(path)
            };
        }

        private static string NormalizeHwpxBinaryPath(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (path.Contains('/', StringComparison.Ordinal))
            {
                return NormalizeZipPath(string.Empty, path);
            }

            return "BinData/" + path;
        }

        private static bool IsSupportedImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInsideNestedElement(XElement root, XNode node, string localName)
        {
            XElement? parent = node.Parent;
            while (parent != null && !ReferenceEquals(parent, root))
            {
                if (parent.Name.LocalName == localName)
                {
                    return true;
                }

                parent = parent.Parent;
            }

            return false;
        }

        private static string ReadPositiveIntegerAttribute(XElement element, string attributeName)
        {
            string value = GetAttributeValue(element, attributeName);
            if (string.IsNullOrWhiteSpace(value))
            {
                XElement? cellSpan = element.Elements().FirstOrDefault(e => e.Name.LocalName == "cellSpan");
                value = GetAttributeValue(cellSpan, attributeName);
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 1
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value ?? string.Empty;
        }

        private static string BuildDocumentHtml(string title, string content)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(title)}}</title>
<style>
:root {
    color-scheme: light dark;
    --bg: #f4f6f8;
    --paper: #ffffff;
    --text: #111827;
    --muted: #667085;
    --line: #d8dee8;
    --table-head: #f1f4f8;
}
@media (prefers-color-scheme: dark) {
    :root {
        --bg: #15171b;
        --paper: #20242b;
        --text: #f4f6fb;
        --muted: #aab2c0;
        --line: #3a424f;
        --table-head: #2a3039;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: var(--bg); color: var(--text); font-family: "Segoe UI", "Malgun Gothic", Arial, sans-serif; }
body { padding: 28px 16px 44px; }
.page {
    width: min(920px, calc(100vw - 32px));
    min-height: calc(100vh - 72px);
    margin: 0 auto;
    padding: clamp(24px, 5vw, 56px);
    background: var(--paper);
    border: 1px solid var(--line);
    box-shadow: 0 18px 44px rgba(15, 23, 42, .12);
}
.doc-paragraph {
    margin: 0 0 .72em;
    line-height: 1.72;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}
.empty-paragraph { min-height: 1em; }
.doc-table-wrap {
    width: 100%;
    overflow-x: auto;
    margin: 1em 0;
}
.doc-table {
    width: 100%;
    border-collapse: collapse;
    table-layout: auto;
    color: var(--text);
}
.doc-table td {
    min-width: 72px;
    border: 1px solid var(--line);
    padding: 8px 10px;
    vertical-align: top;
    overflow-wrap: anywhere;
}
.doc-table tr:first-child td { background: color-mix(in srgb, var(--table-head) 72%, transparent); }
.doc-table .doc-paragraph { margin-bottom: .35em; line-height: 1.45; }
.doc-table .doc-paragraph:last-child { margin-bottom: 0; }
.doc-image {
    display: block;
    margin: .9em 0;
}
.doc-image img {
    display: block;
    max-width: 100%;
    height: auto;
    border: 1px solid color-mix(in srgb, var(--line) 72%, transparent);
}
@media (max-width: 640px) {
    body { padding: 0; }
    .page {
        width: 100%;
        min-height: 100vh;
        border-width: 0;
        padding: 22px 16px 32px;
        box-shadow: none;
    }
}
</style>
</head>
<body>
<main class="page">
{{content}}
</main>
</body>
</html>
""";
        }


        private static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        private static string? TryReadImageDataUri(ZipArchive archive, string imagePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(imagePath) ??
                archive.Entries.FirstOrDefault(candidate => string.Equals(candidate.FullName, imagePath, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length <= 0 || entry.Length > 15_000_000)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return "data:" + GetImageMimeType(imagePath) + ";base64," + Convert.ToBase64String(memory.ToArray());
        }

        private static string GetImageMimeType(string imagePath)
        {
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadRelationshipsAsync(
            ZipArchive archive,
            string relationshipPath,
            string basePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(relationshipPath);
            if (entry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument rels = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath(basePath, e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetRelationshipsPath(string partPath)
        {
            string directory = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
            string fileName = Path.GetFileName(partPath);
            return string.IsNullOrEmpty(directory)
                ? "_rels/" + fileName + ".rels"
                : directory + "/_rels/" + fileName + ".rels";
        }

        private static string NormalizeZipPath(string basePath, string target)
        {
            if (string.IsNullOrWhiteSpace(target) ||
                target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string combined = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : $"{basePath.TrimEnd('/')}/{target}";
            var parts = new List<string>();
            foreach (string part in combined.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    continue;
                }

                parts.Add(part);
            }

            return string.Join("/", parts);
        }

        private static async Task<ZipArchive> OpenArchiveAsync(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        private static async Task<XDocument?> TryLoadXmlEntryAsync(ZipArchive archive, string path)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path);
            return entry == null ? null : await LoadXmlEntryAsync(entry).ConfigureAwait(false);
        }

        private static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() => XDocument.Load(stream)).ConfigureAwait(false);
        }

        private static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

    }
}
