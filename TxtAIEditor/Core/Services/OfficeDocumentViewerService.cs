using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    public sealed class OfficeDocumentViewerService
    {
        private const long DefaultSlideWidthEmu = 9144000;
        private const long DefaultSlideHeightEmu = 5143500;
        private const double PresentationBaseWidthPx = 960;
        private readonly Func<string, string, string> _getString;

        public OfficeDocumentViewerService(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        private sealed class ViewerWorkbookSheet
        {
            public string Name { get; init; } = string.Empty;
            public List<List<ViewerWorkbookCell>> Rows { get; } = new();
        }

        private sealed class ViewerWorkbookCell
        {
            public string Value { get; init; } = string.Empty;
            public string? BackgroundColor { get; init; }
            public string? TextColor { get; init; }
            public bool Bold { get; init; }
            public bool Italic { get; init; }
        }

        private sealed class ViewerCellStyle
        {
            public string? BackgroundColor { get; init; }
            public string? TextColor { get; init; }
            public int NumberFormatId { get; init; }
            public string? NumberFormatCode { get; init; }
            public bool Bold { get; init; }
            public bool Italic { get; init; }
        }

        private sealed class HwpxBinaryItem
        {
            public string Path { get; init; } = string.Empty;
            public string? MimeType { get; init; }
        }

        private sealed class PresentationPlaceholderBounds
        {
            public string? Type { get; init; }
            public string? Index { get; init; }
            public string BoundsStyle { get; init; } = string.Empty;
        }

        private sealed class PresentationGroupTransform
        {
            public double X { get; init; }
            public double Y { get; init; }
            public double Cx { get; init; }
            public double Cy { get; init; }
            public double ChildX { get; init; }
            public double ChildY { get; init; }
            public double ChildCx { get; init; }
            public double ChildCy { get; init; }

            public double MapX(double x)
            {
                return X + ((x - ChildX) / Math.Max(1.0, ChildCx)) * Cx;
            }

            public double MapY(double y)
            {
                return Y + ((y - ChildY) / Math.Max(1.0, ChildCy)) * Cy;
            }

            public double MapCx(double cx)
            {
                return cx / Math.Max(1.0, ChildCx) * Cx;
            }

            public double MapCy(double cy)
            {
                return cy / Math.Max(1.0, ChildCy) * Cy;
            }
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildWordDocumentHtmlAsync(filePath, _getString).ConfigureAwait(false);
            }

            if (extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildHwpxDocumentHtmlAsync(filePath, _getString).ConfigureAwait(false);
            }

            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildPresentationHtmlAsync(filePath, _getString).ConfigureAwait(false);
            }

            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildWorkbookHtmlAsync(filePath, _getString).ConfigureAwait(false);
            }

            return BuildErrorHtml(_getString("OfficeViewerUnsupportedDocument", "Unsupported Office document."));
        }

        private static async Task<string> BuildWordDocumentHtmlAsync(string filePath, Func<string, string, string> getString)
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

        private static async Task<string> BuildHwpxDocumentHtmlAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems = await LoadHwpxBinaryItemsAsync(archive).ConfigureAwait(false);
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
                AppendHwpxChildrenHtml(content, archive, binaryItems, section.Root?.Elements() ?? Enumerable.Empty<XElement>());
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
            IEnumerable<XElement> elements)
        {
            foreach (XElement element in elements)
            {
                AppendHwpxBlockHtml(builder, archive, binaryItems, element);
            }
        }

        private static void AppendHwpxBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            XElement block)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(BuildHwpxParagraphHtml(archive, binaryItems, block));
                    foreach (XElement table in block.Descendants().Where(e => e.Name.LocalName == "tbl"))
                    {
                        builder.Append(BuildHwpxTableHtml(archive, binaryItems, table));
                    }

                    break;
                case "tbl":
                    builder.Append(BuildHwpxTableHtml(archive, binaryItems, block));
                    break;
                default:
                    AppendHwpxChildrenHtml(builder, archive, binaryItems, block.Elements());
                    break;
            }
        }

        private static string BuildHwpxParagraphHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
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
                    content.Append(Html(textNode.Value));
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
                    AppendHwpxChildrenHtml(builder, archive, binaryItems, cell.Elements());
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

            return items;
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

        private static async Task<string> BuildPresentationHtmlAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            XDocument? presentation = await TryLoadXmlEntryAsync(archive, "ppt/presentation.xml").ConfigureAwait(false);
            if (presentation == null)
            {
                return BuildErrorHtml(getString("OfficeViewerPptxStructureError", "Could not read the PPTX presentation structure."));
            }

            (long slideWidth, long slideHeight) = ReadSlideSize(presentation);
            IReadOnlyList<string> themeColors = await LoadPresentationThemeColorsAsync(archive).ConfigureAwait(false);
            List<string> slidePaths = await ReadPresentationSlidePathsAsync(archive, presentation).ConfigureAwait(false);
            if (slidePaths.Count == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoSlides", "No slides to display."));
            }

            var slides = new StringBuilder();
            for (int i = 0; i < slidePaths.Count; i++)
            {
                ZipArchiveEntry? slideEntry = archive.GetEntry(slidePaths[i]);
                if (slideEntry == null)
                {
                    continue;
                }

                XDocument slide = await LoadXmlEntryAsync(slideEntry).ConfigureAwait(false);
                IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                    archive,
                    GetRelationshipsPath(slidePaths[i]),
                    Path.GetDirectoryName(slidePaths[i])?.Replace('\\', '/') ?? string.Empty).ConfigureAwait(false);
                IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds = await LoadSlidePlaceholderBoundsAsync(
                    archive,
                    relationships,
                    slideWidth,
                    slideHeight,
                    PresentationBaseWidthPx,
                    PresentationBaseWidthPx * slideHeight / Math.Max(1.0, slideWidth)).ConfigureAwait(false);

                double baseHeightPx = PresentationBaseWidthPx * slideHeight / Math.Max(1.0, slideWidth);
                string background = ReadSlideBackground(slide, themeColors) ?? "#ffffff";
                slides.Append("<section class=\"slide\" style=\"--slide-ratio:")
                    .Append(FormatInvariant(slideWidth / (double)Math.Max(1, slideHeight)))
                    .Append(";--base-width:")
                    .Append(FormatInvariant(PresentationBaseWidthPx))
                    .Append(";--base-width-px:")
                    .Append(FormatInvariant(PresentationBaseWidthPx))
                    .Append("px;--base-height-px:")
                    .Append(FormatInvariant(baseHeightPx))
                    .Append("px")
                    .Append(";background:")
                    .Append(Html(background))
                    .Append("\">");
                slides.Append("<div class=\"slide-canvas\">");

                slides.Append("<div class=\"slide-number\">")
                    .Append(i + 1)
                    .Append(" / ")
                    .Append(slidePaths.Count)
                    .Append("</div>");

                foreach (string elementHtml in ReadSlideElements(archive, slide, relationships, themeColors, slideWidth, slideHeight, PresentationBaseWidthPx, baseHeightPx, placeholderBounds))
                {
                    slides.Append(elementHtml);
                }

                slides.Append("</div>");
                slides.Append("</section>");
            }

            if (slides.Length == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerSlideRenderError", "Could not render any slides."));
            }

            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(Path.GetFileName(filePath))}}</title>
<style>
:root {
    color-scheme: light dark;
    --app-bg: #f3f4f6;
    --slide-shadow: 0 16px 44px rgba(15, 23, 42, .18);
    --text: #111827;
    --muted: #667085;
}
@media (prefers-color-scheme: dark) {
    :root {
        --app-bg: #17181c;
        --slide-shadow: 0 18px 44px rgba(0, 0, 0, .42);
        --text: #f3f4f6;
        --muted: #a6adbb;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: var(--app-bg); color: var(--text); font-family: "Segoe UI", Arial, sans-serif; }
body { padding: 28px 16px 40px; }
.deck { display: flex; flex-direction: column; align-items: center; gap: 26px; }
.slide {
    position: relative;
    width: min(1120px, calc(100vw - 32px));
    aspect-ratio: var(--slide-ratio);
    overflow: hidden;
    box-shadow: var(--slide-shadow);
    border: 1px solid rgba(148, 163, 184, .35);
}
.slide-canvas {
    position: absolute;
    inset: 0 auto auto 0;
    width: var(--base-width-px);
    height: var(--base-height-px);
    transform-origin: top left;
}
.slide-number {
    position: absolute;
    right: 12px;
    bottom: 9px;
    z-index: 10;
    color: var(--muted);
    font: 12px/1.2 "Segoe UI", Arial, sans-serif;
    background: rgba(255, 255, 255, .72);
    border-radius: 999px;
    padding: 4px 8px;
}
@media (prefers-color-scheme: dark) {
    .slide-number { background: rgba(17, 24, 39, .66); }
}
.ppt-shape, .ppt-image, .ppt-table {
    position: absolute;
    overflow: hidden;
    transform-origin: center center;
}
.ppt-shape {
    display: block;
    color: #111827;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    padding: 4px 6px;
    line-height: 1.16;
}
.ppt-shape p { width: 100%; margin: 0 0 .24em; line-height: inherit; }
.ppt-shape p:last-child { margin-bottom: 0; }
.ppt-shape span { white-space: pre-wrap; }
.ppt-box { padding: 0; }
.ppt-image img {
    width: 100%;
    height: 100%;
    object-fit: fill;
    display: block;
}
.ppt-table table {
    width: 100%;
    height: 100%;
    border-collapse: collapse;
    table-layout: fixed;
    background: rgba(255, 255, 255, .88);
    color: #111827;
    font-size: clamp(9px, 1.35vw, 15px);
}
.ppt-table td {
    border: 1px solid rgba(31, 41, 55, .28);
    padding: .32em .45em;
    vertical-align: top;
    overflow-wrap: anywhere;
}
.ppt-table p {
    margin: 0 0 .2em;
    line-height: 1.16;
}
.ppt-table p:last-child { margin-bottom: 0; }
</style>
</head>
<body>
<main class="deck">
{{slides}}
</main>
<script>
function fitSlide(slide) {
    const canvas = slide.querySelector('.slide-canvas');
    if (!canvas) return;
    const baseWidth = parseFloat(getComputedStyle(slide).getPropertyValue('--base-width')) || 960;
    canvas.style.transform = `scale(${slide.clientWidth / baseWidth})`;
}
const observer = new ResizeObserver(entries => {
    for (const entry of entries) fitSlide(entry.target);
});
document.querySelectorAll('.slide').forEach(slide => {
    fitSlide(slide);
    observer.observe(slide);
});
</script>
</body>
</html>
""";
        }

        private static async Task<string> BuildWorkbookHtmlAsync(string filePath, Func<string, string, string> getString)
        {
            IReadOnlyList<ViewerWorkbookSheet> sheets = await ExtractWorkbookSheetsAsync(filePath).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoSheets", "No sheets to display."));
            }

            var sheetPayload = sheets.Select(sheet => new
            {
                name = sheet.Name,
                rows = sheet.Rows.Select(row => row.Select(cell => new
                {
                    value = cell.Value,
                    backgroundColor = cell.BackgroundColor,
                    textColor = cell.TextColor,
                    bold = cell.Bold,
                    italic = cell.Italic
                }).ToArray()).ToArray()
            }).ToArray();
            string sheetsJson = JsonSerializer.Serialize(sheetPayload);
            string emptySheetTextJson = JsonSerializer.Serialize(getString("OfficeViewerEmptySheet", "Empty sheet."));
            string rowsTextJson = JsonSerializer.Serialize(getString("OfficeViewerRowsLabel", "rows"));
            string columnsTextJson = JsonSerializer.Serialize(getString("OfficeViewerColumnsLabel", "columns"));
            string firstShownTextJson = JsonSerializer.Serialize(getString("OfficeViewerFirstRowsShownFormat", "first {0} shown"));
            string sheetAriaLabelJson = JsonSerializer.Serialize(getString("OfficeViewerSheetSelectorLabel", "Sheet"));

            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(Path.GetFileName(filePath))}}</title>
<style>
:root {
    color-scheme: light dark;
    --bg: #f6f7f9;
    --panel: #ffffff;
    --line: #d8dee8;
    --line-strong: #b8c2d2;
    --text: #111827;
    --muted: #667085;
    --header: #eef2f7;
    --accent: #2563eb;
}
@media (prefers-color-scheme: dark) {
    :root {
        --bg: #15171b;
        --panel: #1f232a;
        --line: #343b47;
        --line-strong: #4b5563;
        --text: #f4f6fb;
        --muted: #aab2c0;
        --header: #2a3039;
        --accent: #60a5fa;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; height: 100%; background: var(--bg); color: var(--text); font-family: "Segoe UI", Arial, sans-serif; }
body { display: flex; flex-direction: column; }
.toolbar {
    position: sticky;
    top: 0;
    z-index: 20;
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 52px;
    padding: 10px 14px;
    background: color-mix(in srgb, var(--panel) 94%, transparent);
    border-bottom: 1px solid var(--line);
    backdrop-filter: blur(12px);
}
select {
    max-width: min(460px, 70vw);
    min-width: 190px;
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 7px 32px 7px 10px;
    color: var(--text);
    background: var(--panel);
    font: 14px/1.2 "Segoe UI", Arial, sans-serif;
}
.meta { color: var(--muted); font-size: 13px; white-space: nowrap; }
.table-wrap { flex: 1; overflow: auto; padding: 0; }
table {
    border-collapse: separate;
    border-spacing: 0;
    background: var(--panel);
    color: var(--text);
    box-shadow: 0 10px 26px rgba(15, 23, 42, .08);
}
th, td {
    border-right: 1px solid var(--line);
    border-bottom: 1px solid var(--line);
    min-width: 96px;
    max-width: 360px;
    height: 30px;
    padding: 6px 8px;
    text-align: left;
    vertical-align: top;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 13px;
}
th {
    position: sticky;
    top: 0;
    z-index: 2;
    background: var(--header);
    color: var(--muted);
    font-weight: 600;
}
th.row-header {
    left: 0;
    z-index: 3;
    min-width: 54px;
    width: 54px;
    text-align: right;
    user-select: none;
    -webkit-user-select: none;
}
td.row-header {
    position: sticky;
    left: 0;
    z-index: 1;
    min-width: 54px;
    width: 54px;
    text-align: right;
    color: var(--muted);
    background: var(--header);
    font-weight: 600;
    user-select: none;
    -webkit-user-select: none;
}
.empty { padding: 28px; color: var(--muted); }
.truncated { color: var(--accent); }
</style>
</head>
<body>
<div class="toolbar">
    <select id="sheetSelect"></select>
    <span id="sheetMeta" class="meta"></span>
</div>
<div id="tableWrap" class="table-wrap"></div>
<script>
const sheets = {{sheetsJson}};
const emptySheetText = {{emptySheetTextJson}};
const rowsText = {{rowsTextJson}};
const columnsText = {{columnsTextJson}};
const firstShownText = {{firstShownTextJson}};
const sheetAriaLabel = {{sheetAriaLabelJson}};
const maxRows = 5000;
const select = document.getElementById('sheetSelect');
const meta = document.getElementById('sheetMeta');
const wrap = document.getElementById('tableWrap');
select.setAttribute('aria-label', sheetAriaLabel);

function colName(index) {
    let n = index + 1;
    let name = '';
    while (n > 0) {
        n--;
        name = String.fromCharCode(65 + (n % 26)) + name;
        n = Math.floor(n / 26);
    }
    return name;
}

function cell(tag, text, className) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    el.textContent = text;
    return el;
}

function valueOf(cell) {
    return cell && typeof cell === 'object' ? (cell.value ?? '') : (cell ?? '');
}

function applyCellStyle(td, cell) {
    if (!cell || typeof cell !== 'object') return;
    if (cell.backgroundColor) td.style.backgroundColor = cell.backgroundColor;
    if (cell.textColor) td.style.color = cell.textColor;
    if (cell.bold) td.style.fontWeight = '700';
    if (cell.italic) td.style.fontStyle = 'italic';
}

function renderSheet(index) {
    const sheet = sheets[index];
    const rows = sheet.rows || [];
    const visibleRows = rows.slice(0, maxRows);
    const columnCount = Math.max(1, ...visibleRows.map(row => row.length));
    wrap.textContent = '';

    if (!rows.length) {
        wrap.appendChild(cell('div', emptySheetText, 'empty'));
        meta.textContent = `0 ${rowsText}`;
        return;
    }

    const table = document.createElement('table');
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    headRow.appendChild(cell('th', '', 'row-header'));
    for (let c = 0; c < columnCount; c++) {
        headRow.appendChild(cell('th', colName(c)));
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    visibleRows.forEach((row, r) => {
        const tr = document.createElement('tr');
        tr.appendChild(cell('td', String(r + 1), 'row-header'));
        for (let c = 0; c < columnCount; c++) {
            const sourceCell = row[c] || null;
            const td = cell('td', valueOf(sourceCell));
            applyCellStyle(td, sourceCell);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);

    const firstShown = firstShownText.replace('{0}', maxRows.toLocaleString());
    meta.innerHTML = `${rows.length.toLocaleString()} ${rowsText} x ${columnCount.toLocaleString()} ${columnsText}` +
        (rows.length > maxRows ? ` <span class="truncated">${firstShown}</span>` : '');
}

sheets.forEach((sheet, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = sheet.name || `Sheet ${index + 1}`;
    select.appendChild(option);
});
select.addEventListener('change', () => renderSheet(Number(select.value || 0)));
renderSheet(0);
</script>
</body>
</html>
""";
        }

        private static async Task<IReadOnlyList<ViewerWorkbookSheet>> ExtractWorkbookSheetsAsync(string filePath)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyList<string> sharedStrings = await LoadWorkbookSharedStringsAsync(archive).ConfigureAwait(false);
            IReadOnlyList<string> themeColors = await LoadWorkbookThemeColorsAsync(archive).ConfigureAwait(false);
            IReadOnlyList<ViewerCellStyle> styles = await LoadWorkbookStylesAsync(archive, themeColors).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sheetNamesByPath = await LoadWorkbookSheetNamesByPathAsync(archive).ConfigureAwait(false);
            bool use1904Dates = await LoadWorkbookUses1904DatesAsync(archive).ConfigureAwait(false);

            var sheetEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var sheets = new List<ViewerWorkbookSheet>();
            for (int sheetIndex = 0; sheetIndex < sheetEntries.Count; sheetIndex++)
            {
                ZipArchiveEntry sheetEntry = sheetEntries[sheetIndex];
                string sheetName = sheetNamesByPath.TryGetValue(sheetEntry.FullName, out string? mappedName)
                    ? mappedName
                    : $"Sheet {sheetIndex + 1}";

                var sheet = new ViewerWorkbookSheet { Name = sheetName };
                XDocument sheetDoc = await LoadXmlEntryAsync(sheetEntry).ConfigureAwait(false);
                foreach (XElement rowElement in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    var row = new List<ViewerWorkbookCell>();
                    foreach (XElement cellElement in rowElement.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        int columnIndex = GetCellColumnIndex(cellElement);
                        if (columnIndex > 0)
                        {
                            while (row.Count < columnIndex - 1)
                            {
                                row.Add(new ViewerWorkbookCell());
                            }
                        }

                        ViewerCellStyle style = ReadWorkbookCellStyle(cellElement, styles);
                        row.Add(new ViewerWorkbookCell
                        {
                            Value = GetWorkbookCellText(cellElement, sharedStrings, style, use1904Dates),
                            BackgroundColor = style.BackgroundColor,
                            TextColor = style.TextColor,
                            Bold = style.Bold,
                            Italic = style.Italic
                        });
                    }

                    if (row.Any(cell =>
                        !string.IsNullOrWhiteSpace(cell.Value) ||
                        !string.IsNullOrWhiteSpace(cell.BackgroundColor) ||
                        !string.IsNullOrWhiteSpace(cell.TextColor)))
                    {
                        sheet.Rows.Add(row);
                    }
                }

                sheets.Add(sheet);
            }

            return sheets;
        }

        private static async Task<IReadOnlyList<string>> LoadWorkbookSharedStringsAsync(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            XDocument doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "si")
                .Select(item => string.Concat(item.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)))
                .ToList();
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadWorkbookSheetNamesByPathAsync(ZipArchive archive)
        {
            ZipArchiveEntry? workbookEntry = archive.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry == null || relsEntry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument workbook = await LoadXmlEntryAsync(workbookEntry).ConfigureAwait(false);
            XDocument rels = await LoadXmlEntryAsync(relsEntry).ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            var targetsById = rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath("xl", e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);

            var namesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
            {
                string name = sheet.Attribute("name")?.Value ?? string.Empty;
                string id = sheet.Attribute(relNs + "id")?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) &&
                    targetsById.TryGetValue(id, out string? targetPath))
                {
                    namesByPath[targetPath] = name;
                }
            }

            return namesByPath;
        }

        private static async Task<IReadOnlyList<ViewerCellStyle>> LoadWorkbookStylesAsync(
            ZipArchive archive,
            IReadOnlyList<string> themeColors)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/styles.xml");
            if (entry == null)
            {
                return Array.Empty<ViewerCellStyle>();
            }

            XDocument stylesDoc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            IReadOnlyDictionary<int, string> numberFormats = LoadWorkbookNumberFormats(stylesDoc);
            var fontStyles = stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "fonts")
                ?.Elements().Where(e => e.Name.LocalName == "font")
                .Select(font => new ViewerCellStyle
                {
                    TextColor = ReadWorkbookColor(font.Elements().FirstOrDefault(e => e.Name.LocalName == "color"), themeColors),
                    Bold = font.Elements().Any(e => e.Name.LocalName == "b"),
                    Italic = font.Elements().Any(e => e.Name.LocalName == "i")
                })
                .ToList() ?? new List<ViewerCellStyle>();

            var fillColors = stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "fills")
                ?.Elements().Where(e => e.Name.LocalName == "fill")
                .Select(fill => ReadWorkbookFillColor(fill, themeColors))
                .ToList() ?? new List<string?>();

            var result = new List<ViewerCellStyle>();
            foreach (XElement xf in stylesDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs")?.Elements().Where(e => e.Name.LocalName == "xf") ?? Enumerable.Empty<XElement>())
            {
                int fillId = TryReadInt(xf, "fillId");
                int fontId = TryReadInt(xf, "fontId");
                int numberFormatId = TryReadInt(xf, "numFmtId");
                ViewerCellStyle fontStyle = fontId >= 0 && fontId < fontStyles.Count
                    ? fontStyles[fontId]
                    : new ViewerCellStyle();

                result.Add(new ViewerCellStyle
                {
                    BackgroundColor = fillId >= 0 && fillId < fillColors.Count ? fillColors[fillId] : null,
                    TextColor = fontStyle.TextColor,
                    NumberFormatId = numberFormatId,
                    NumberFormatCode = numberFormats.TryGetValue(numberFormatId, out string? numberFormatCode) ? numberFormatCode : null,
                    Bold = fontStyle.Bold,
                    Italic = fontStyle.Italic
                });
            }

            return result;
        }

        private static IReadOnlyDictionary<int, string> LoadWorkbookNumberFormats(XDocument stylesDoc)
        {
            var formats = new Dictionary<int, string>
            {
                [0] = "General",
                [1] = "0",
                [2] = "0.00",
                [3] = "#,##0",
                [4] = "#,##0.00",
                [9] = "0%",
                [10] = "0.00%",
                [11] = "0.00E+00",
                [12] = "# ?/?",
                [13] = "# ??/??",
                [14] = "m/d/yy",
                [15] = "d-mmm-yy",
                [16] = "d-mmm",
                [17] = "mmm-yy",
                [18] = "h:mm AM/PM",
                [19] = "h:mm:ss AM/PM",
                [20] = "h:mm",
                [21] = "h:mm:ss",
                [22] = "m/d/yy h:mm",
                [37] = "#,##0;(#,##0)",
                [38] = "#,##0;[Red](#,##0)",
                [39] = "#,##0.00;(#,##0.00)",
                [40] = "#,##0.00;[Red](#,##0.00)",
                [45] = "mm:ss",
                [46] = "[h]:mm:ss",
                [47] = "mm:ss.0",
                [48] = "##0.0E+0",
                [49] = "@"
            };

            foreach (XElement numFmt in stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "numFmts")
                ?.Elements().Where(e => e.Name.LocalName == "numFmt") ?? Enumerable.Empty<XElement>())
            {
                int id = TryReadInt(numFmt, "numFmtId");
                string code = numFmt.Attribute("formatCode")?.Value ?? string.Empty;
                if (id >= 0 && !string.IsNullOrWhiteSpace(code))
                {
                    formats[id] = code;
                }
            }

            return formats;
        }

        private static async Task<bool> LoadWorkbookUses1904DatesAsync(ZipArchive archive)
        {
            XDocument? workbook = await TryLoadXmlEntryAsync(archive, "xl/workbook.xml").ConfigureAwait(false);
            XElement? workbookProperties = workbook?.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
            string value = workbookProperties?.Attribute("date1904")?.Value ?? string.Empty;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<IReadOnlyList<string>> LoadWorkbookThemeColorsAsync(ZipArchive archive)
        {
            return await LoadThemeColorsAsync(archive, "xl/theme/theme1.xml").ConfigureAwait(false);
        }

        private static async Task<IReadOnlyList<string>> LoadPresentationThemeColorsAsync(ZipArchive archive)
        {
            return await LoadThemeColorsAsync(archive, "ppt/theme/theme1.xml").ConfigureAwait(false);
        }

        private static async Task<IReadOnlyList<string>> LoadThemeColorsAsync(ZipArchive archive, string themePath)
        {
            XDocument? theme = await TryLoadXmlEntryAsync(archive, themePath).ConfigureAwait(false);
            XElement? clrScheme = theme?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
            if (clrScheme == null)
            {
                return Array.Empty<string>();
            }

            var order = new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
            var colors = new List<string>();
            foreach (string name in order)
            {
                XElement? item = clrScheme.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                string? color = item == null ? null : ReadThemeColor(item);
                colors.Add(color ?? "#000000");
            }

            return colors;
        }

        private static string? ReadThemeColor(XElement element)
        {
            XElement? srgb = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            if (!string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            XElement? sys = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "sysClr");
            value = sys?.Attribute("lastClr")?.Value;
            return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                ? "#" + value
                : null;
        }

        private static string? ReadWorkbookFillColor(XElement fill, IReadOnlyList<string> themeColors)
        {
            XElement? pattern = fill.Descendants().FirstOrDefault(e => e.Name.LocalName == "patternFill");
            string? patternType = pattern?.Attribute("patternType")?.Value;
            if (pattern == null ||
                string.Equals(patternType, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ReadWorkbookColor(pattern.Elements().FirstOrDefault(e => e.Name.LocalName == "fgColor"), themeColors) ??
                ReadWorkbookColor(pattern.Elements().FirstOrDefault(e => e.Name.LocalName == "bgColor"), themeColors);
        }

        private static ViewerCellStyle ReadWorkbookCellStyle(XElement cell, IReadOnlyList<ViewerCellStyle> styles)
        {
            int styleIndex = TryReadInt(cell, "s");
            return styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex]
                : new ViewerCellStyle();
        }

        private static string GetWorkbookCellText(
            XElement cell,
            IReadOnlyList<string> sharedStrings,
            ViewerCellStyle style,
            bool use1904Dates)
        {
            string type = cell.Attribute("t")?.Value ?? string.Empty;
            if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            }

            string rawValue = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(rawValue))
            {
                return string.Empty;
            }

            return type switch
            {
                "s" when int.TryParse(rawValue, out int index) && index >= 0 && index < sharedStrings.Count => sharedStrings[index],
                "b" => rawValue == "1" ? "TRUE" : "FALSE",
                "str" => rawValue,
                _ => FormatWorkbookCellValue(rawValue, style, use1904Dates)
            };
        }

        private static string FormatWorkbookCellValue(string rawValue, ViewerCellStyle style, bool use1904Dates)
        {
            string formatCode = style.NumberFormatCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(formatCode) ||
                formatCode.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericValue))
            {
                return rawValue;
            }

            if (IsWorkbookDateFormat(formatCode) &&
                TryConvertExcelSerialDate(numericValue, use1904Dates, out DateTime dateTime))
            {
                return FormatWorkbookDateValue(dateTime, numericValue, formatCode);
            }

            return FormatWorkbookNumberValue(numericValue, formatCode, rawValue);
        }

        private static bool IsWorkbookDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[ymdhHsS]", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(cleaned, @"[0#?](?:\.[0#?]+)?\s*%?");
        }

        private static bool TryConvertExcelSerialDate(double serial, bool use1904Dates, out DateTime dateTime)
        {
            dateTime = default;
            if (double.IsNaN(serial) || double.IsInfinity(serial))
            {
                return false;
            }

            try
            {
                dateTime = use1904Dates
                    ? new DateTime(1904, 1, 1).AddDays(serial)
                    : new DateTime(1899, 12, 30).AddDays(serial);
                return dateTime.Year >= 1 && dateTime.Year <= 9999;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatWorkbookDateValue(DateTime dateTime, double serial, string formatCode)
        {
            if (ShouldUseIsoDateFormat(formatCode))
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            string section = SelectWorkbookFormatSection(formatCode, serial);
            section = CleanWorkbookFormatSection(section);
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string token = match.Value.Trim('[', ']');
                return token.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? token
                    : string.Empty;
            });

            string dotNetFormat = ConvertExcelDateFormatToDotNet(section);
            if (string.IsNullOrWhiteSpace(dotNetFormat))
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }

            try
            {
                return dateTime.ToString(dotNetFormat, CultureInfo.CurrentCulture);
            }
            catch
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static bool ShouldUseIsoDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[yd]", RegexOptions.IgnoreCase);
        }

        private static string ConvertExcelDateFormatToDotNet(string format)
        {
            var builder = new StringBuilder();
            bool hasAmPm = format.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("A/P", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < format.Length;)
            {
                char ch = format[i];
                if (ch == '"')
                {
                    int end = format.IndexOf('"', i + 1);
                    string literal = end > i ? format.Substring(i + 1, end - i - 1) : string.Empty;
                    AppendDateLiteral(builder, literal);
                    i = end > i ? end + 1 : format.Length;
                    continue;
                }

                if (ch == '\\')
                {
                    if (i + 1 < format.Length)
                    {
                        AppendDateLiteral(builder, format.Substring(i + 1, 1));
                    }

                    i += 2;
                    continue;
                }

                if (ch == '_' || ch == '*')
                {
                    i += 2;
                    continue;
                }

                string remaining = format.Substring(i);
                if (remaining.StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 5;
                    continue;
                }

                if (remaining.StartsWith("A/P", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 3;
                    continue;
                }

                int runLength = CountRepeatedDateFormatChars(format, i);
                char lower = char.ToLowerInvariant(ch);
                switch (lower)
                {
                    case 'y':
                        builder.Append(runLength <= 2 ? "yy" : "yyyy");
                        i += runLength;
                        break;
                    case 'd':
                        builder.Append(runLength switch
                        {
                            1 => "d",
                            2 => "dd",
                            3 => "ddd",
                            _ => "dddd"
                        });
                        i += runLength;
                        break;
                    case 'h':
                        builder.Append(runLength <= 1 ? (hasAmPm ? "h" : "H") : (hasAmPm ? "hh" : "HH"));
                        i += runLength;
                        break;
                    case 's':
                        builder.Append(runLength <= 1 ? "s" : "ss");
                        i += runLength;
                        break;
                    case 'm':
                        bool minute = IsMinuteToken(format, i);
                        builder.Append(minute
                            ? (runLength <= 1 ? "m" : "mm")
                            : runLength switch
                            {
                                1 => "M",
                                2 => "MM",
                                3 => "MMM",
                                _ => "MMMM"
                            });
                        i += runLength;
                        break;
                    default:
                        AppendDateLiteral(builder, ch.ToString());
                        i++;
                        break;
                }
            }

            return builder.ToString();
        }

        private static int CountRepeatedDateFormatChars(string format, int start)
        {
            char ch = char.ToLowerInvariant(format[start]);
            int count = 0;
            while (start + count < format.Length &&
                char.ToLowerInvariant(format[start + count]) == ch)
            {
                count++;
            }

            return count;
        }

        private static bool IsMinuteToken(string format, int index)
        {
            int previous = FindPreviousDateFormatToken(format, index);
            int next = FindNextDateFormatToken(format, index);
            return (previous >= 0 && "hHsS".IndexOf(format[previous]) >= 0) ||
                (next >= 0 && "hHsS".IndexOf(format[next]) >= 0);
        }

        private static int FindPreviousDateFormatToken(string format, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static int FindNextDateFormatToken(string format, int index)
        {
            for (int i = index + 1; i < format.Length; i++)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static void AppendDateLiteral(StringBuilder builder, string literal)
        {
            foreach (char ch in literal)
            {
                if (ch == '\'')
                {
                    builder.Append("''");
                }
                else if (char.IsLetter(ch))
                {
                    builder.Append('\'').Append(ch).Append('\'');
                }
                else
                {
                    builder.Append(ch);
                }
            }
        }

        private static string FormatWorkbookNumberValue(double numericValue, string formatCode, string rawValue)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            bool usesNegativeSection = numericValue < 0 && sections.Count > 1;
            string section = CleanWorkbookFormatSection(SelectWorkbookFormatSection(formatCode, numericValue));
            if (string.IsNullOrWhiteSpace(section) ||
                section.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                section.Contains("/", StringComparison.Ordinal) && section.Contains("?", StringComparison.Ordinal))
            {
                return rawValue;
            }

            try
            {
                double valueToFormat = usesNegativeSection ? Math.Abs(numericValue) : numericValue;
                return valueToFormat.ToString(section, CultureInfo.CurrentCulture);
            }
            catch
            {
                return rawValue;
            }
        }

        private static string SelectWorkbookFormatSection(string formatCode, double value)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            if (sections.Count == 0)
            {
                return formatCode;
            }

            if (sections.Count == 1)
            {
                return sections[0];
            }

            if (value > 0)
            {
                return sections[0];
            }

            if (value < 0)
            {
                return sections.Count > 1 ? sections[1] : sections[0];
            }

            return sections.Count > 2 ? sections[2] : sections[0];
        }

        private static List<string> SplitWorkbookFormatSections(string formatCode)
        {
            var sections = new List<string>();
            var current = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    current.Append(ch);
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    current.Append(ch);
                    continue;
                }

                if (ch == ';' && !inQuote)
                {
                    sections.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            sections.Add(current.ToString());
            return sections;
        }

        private static string CleanWorkbookFormatSection(string section)
        {
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string value = match.Value.Trim('[', ']');
                return value.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? match.Value
                    : string.Empty;
            });
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = section.Replace("_-", string.Empty, StringComparison.Ordinal)
                .Replace("_)", string.Empty, StringComparison.Ordinal)
                .Replace("_(", string.Empty, StringComparison.Ordinal)
                .Replace("_ ", string.Empty, StringComparison.Ordinal);
            section = Regex.Replace(section, @"_.", string.Empty);
            section = Regex.Replace(section, @"\*.", string.Empty);
            return section.Trim();
        }

        private static string RemoveWorkbookFormatLiterals(string formatCode)
        {
            var builder = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (!inQuote)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static string? ReadWorkbookColor(XElement? colorElement, IReadOnlyList<string> themeColors)
        {
            if (colorElement == null)
            {
                return null;
            }

            string? rgb = colorElement.Attribute("rgb")?.Value;
            if (!string.IsNullOrWhiteSpace(rgb))
            {
                rgb = rgb.Trim();
                if (rgb.Length == 8)
                {
                    rgb = rgb.Substring(2);
                }

                if (Regex.IsMatch(rgb, "^[0-9A-Fa-f]{6}$"))
                {
                    return "#" + rgb;
                }
            }

            if (int.TryParse(colorElement.Attribute("indexed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexed))
            {
                return IndexedWorkbookColor(indexed);
            }

            if (int.TryParse(colorElement.Attribute("theme")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int themeIndex) &&
                themeIndex >= 0 &&
                themeIndex < themeColors.Count)
            {
                double tint = 0;
                _ = double.TryParse(colorElement.Attribute("tint")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tint);
                return ApplyTint(themeColors[themeIndex], tint);
            }

            return null;
        }

        private static string? IndexedWorkbookColor(int index)
        {
            string[] colors =
            {
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#808080",
                "#9999FF", "#993366", "#FFFFCC", "#CCFFFF", "#660066", "#FF8080", "#0066CC", "#CCCCFF",
                "#000080", "#FF00FF", "#FFFF00", "#00FFFF", "#800080", "#800000", "#008080", "#0000FF",
                "#00CCFF", "#CCFFFF", "#CCFFCC", "#FFFF99", "#99CCFF", "#FF99CC", "#CC99FF", "#FFCC99",
                "#3366FF", "#33CCCC", "#99CC00", "#FFCC00", "#FF9900", "#FF6600", "#666699", "#969696",
                "#003366", "#339966", "#003300", "#333300", "#993300", "#993366", "#333399", "#333333"
            };

            return index >= 0 && index < colors.Length ? colors[index] : null;
        }

        private static string ApplyTint(string hex, double tint)
        {
            if (string.IsNullOrEmpty(hex) || !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
            {
                return hex ?? "#000000";
            }

            string normalized = hex;
            int r = Convert.ToInt32(normalized.Substring(1, 2), 16);
            int g = Convert.ToInt32(normalized.Substring(3, 2), 16);
            int b = Convert.ToInt32(normalized.Substring(5, 2), 16);
            r = ApplyTintComponent(r, tint);
            g = ApplyTintComponent(g, tint);
            b = ApplyTintComponent(b, tint);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static int ApplyTintComponent(int value, double tint)
        {
            double adjusted = tint < 0
                ? value * (1 + tint)
                : value + (255 - value) * tint;
            return Math.Max(0, Math.Min(255, (int)Math.Round(adjusted)));
        }

        private static int GetCellColumnIndex(XElement cell)
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return 0;
            }

            int column = 0;
            foreach (char ch in reference)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    column = (column * 26) + (ch - 'A' + 1);
                    continue;
                }

                if (ch >= 'a' && ch <= 'z')
                {
                    column = (column * 26) + (ch - 'a' + 1);
                    continue;
                }

                break;
            }

            return column;
        }

        private static IEnumerable<string> ReadSlideElements(
            ZipArchive archive,
            XDocument slide,
            IReadOnlyDictionary<string, string> relationships,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds)
        {
            XElement? shapeTree = slide.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
            IEnumerable<XElement> elements = shapeTree?.Elements() ?? slide.Root?.Elements() ?? Enumerable.Empty<XElement>();

            foreach (XElement element in elements)
            {
                foreach (string elementHtml in ReadSlideElement(
                    archive,
                    element,
                    relationships,
                    themeColors,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    placeholderBounds,
                    null))
                {
                    yield return elementHtml;
                }
            }
        }

        private static IEnumerable<string> ReadSlideElement(
            ZipArchive archive,
            XElement element,
            IReadOnlyDictionary<string, string> relationships,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds,
            PresentationGroupTransform? groupTransform)
        {
            if (element.Name.LocalName == "grpSp")
            {
                PresentationGroupTransform? nextTransform = TryReadGroupTransform(element, groupTransform, out PresentationGroupTransform? readTransform)
                    ? readTransform
                    : groupTransform;
                foreach (XElement child in element.Elements().Where(e => e.Name.LocalName != "nvGrpSpPr" && e.Name.LocalName != "grpSpPr"))
                {
                    foreach (string childHtml in ReadSlideElement(
                        archive,
                        child,
                        relationships,
                        themeColors,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx,
                        placeholderBounds,
                        nextTransform))
                    {
                        yield return childHtml;
                    }
                }

                yield break;
            }

            if (element.Name.LocalName == "pic")
            {
                if (!TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, groupTransform, out string bounds))
                {
                    yield break;
                }

                string? relationshipId = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip")
                    ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId) ||
                    !relationships.TryGetValue(relationshipId, out string? imagePath))
                {
                    yield break;
                }

                string? dataUri = TryReadImageDataUri(archive, imagePath);
                if (string.IsNullOrEmpty(dataUri))
                {
                    yield break;
                }

                yield return "<div class=\"ppt-image\" style=\"" + bounds + "\"><img src=\"" + Html(dataUri) + "\"></div>";
                yield break;
            }

            if (element.Name.LocalName == "graphicFrame" && element.Descendants().Any(d => d.Name.LocalName == "tbl"))
            {
                if (!TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, groupTransform, out string bounds))
                {
                    yield break;
                }

                XElement? table = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "tbl");
                if (table == null)
                {
                    yield break;
                }

                string tableHtml = BuildTableHtml(table, themeColors, slideWidth, slideHeight, baseWidthPx, baseHeightPx);
                if (!string.IsNullOrWhiteSpace(tableHtml))
                {
                    yield return "<div class=\"ppt-table\" style=\"" + bounds + "\">" + tableHtml + "</div>";
                }

                yield break;
            }

            if (element.Name.LocalName != "sp")
            {
                yield break;
            }

            XElement? shapeProperties = element.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
            string boxStyle = ReadShapeBoxStyle(shapeProperties, themeColors);
            string boundsStyle = TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, groupTransform, placeholderBounds, out string readBounds)
                ? readBounds
                : "left:48px;top:27px;width:864px;height:auto;";
            string textHtml = BuildShapeTextHtml(element, themeColors, slideWidth, baseWidthPx);
            if (!string.IsNullOrWhiteSpace(textHtml))
            {
                yield return "<div class=\"ppt-shape\" style=\"" + boundsStyle + boxStyle + "\">" + textHtml + "</div>";
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(boxStyle))
            {
                yield return "<div class=\"ppt-shape ppt-box\" style=\"" + boundsStyle + boxStyle + "\"></div>";
            }
        }

        private static string BuildShapeTextHtml(
            XElement shape,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            var paragraphs = new StringBuilder();
            XElement? txBody = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (txBody == null)
            {
                return string.Empty;
            }

            foreach (XElement paragraph in txBody.Elements().Where(e => e.Name.LocalName == "p"))
            {
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                bool hasBullet = paragraph.Descendants().Any(e => e.Name.LocalName == "buChar" || e.Name.LocalName == "buAutoNum");
                string paragraphStyle = ReadParagraphStyle(paragraph, slideWidth, baseWidthPx);
                paragraphs.Append("<p");
                if (!string.IsNullOrWhiteSpace(paragraphStyle))
                {
                    paragraphs.Append(" style=\"").Append(Html(paragraphStyle)).Append('"');
                }

                paragraphs.Append('>');
                if (hasBullet)
                {
                    string bullet = paragraph.Descendants().FirstOrDefault(e => e.Name.LocalName == "buChar")?.Attribute("char")?.Value ?? "•";
                    paragraphs.Append("<span>").Append(Html(bullet)).Append(' ').Append("</span>");
                }

                paragraphs.Append(BuildParagraphRunsHtml(paragraph, themeColors, slideWidth, baseWidthPx));
                paragraphs.Append("</p>");
            }

            return paragraphs.ToString();
        }

        private static string BuildParagraphRunsHtml(
            XElement paragraph,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            var builder = new StringBuilder();
            XElement? defaultRunProperties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "defRPr");
            foreach (XElement element in paragraph.Elements())
            {
                if (element.Name.LocalName == "r" || element.Name.LocalName == "fld")
                {
                    XElement? runProperties = element.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr") ?? defaultRunProperties;
                    string runStyle = ReadRunTextStyle(runProperties, themeColors, slideWidth, baseWidthPx);
                    string text = string.Concat(element.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    builder.Append("<span");
                    if (!string.IsNullOrWhiteSpace(runStyle))
                    {
                        builder.Append(" style=\"").Append(Html(runStyle)).Append('"');
                    }

                    builder.Append('>').Append(Html(text)).Append("</span>");
                    continue;
                }

                if (element.Name.LocalName == "br" || element.Name.LocalName == "cr")
                {
                    builder.Append("<br>");
                }
            }

            if (builder.Length == 0)
            {
                builder.Append(Html(ReadParagraphText(paragraph)));
            }

            return builder.ToString();
        }

        private static string BuildTableHtml(
            XElement table,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            var builder = new StringBuilder("<table>");
            List<XElement> rows = table.Elements().Where(e => e.Name.LocalName == "tr").ToList();
            if (rows.Count == 0)
            {
                rows = table.Descendants().Where(e => e.Name.LocalName == "tr").ToList();
            }

            IReadOnlyList<long> columnWidths = ReadTableColumnWidths(table);
            if (columnWidths.Count > 0)
            {
                long totalWidth = Math.Max(1, columnWidths.Sum());
                builder.Append("<colgroup>");
                foreach (long width in columnWidths)
                {
                    builder.Append("<col style=\"width:")
                        .Append(FormatInvariant(width / (double)totalWidth * 100))
                        .Append("%\">");
                }

                builder.Append("</colgroup>");
            }

            builder.Append("<tbody>");
            long totalHeight = Math.Max(1, rows.Sum(row => Math.Max(0, ReadTableRowHeight(row))));
            foreach (XElement row in rows)
            {
                long rowHeight = ReadTableRowHeight(row);
                builder.Append("<tr");
                if (rowHeight > 0 && totalHeight > 1)
                {
                    builder.Append(" style=\"height:")
                        .Append(FormatInvariant(rowHeight / (double)totalHeight * 100))
                        .Append("%\"");
                }

                builder.Append('>');
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    if (IsMergedTableCellContinuation(cell))
                    {
                        continue;
                    }

                    string style = ReadTableCellStyle(cell, themeColors);
                    builder.Append("<td");
                    int colspan = ReadTableSpan(cell, "gridSpan", "colSpan");
                    int rowspan = ReadTableSpan(cell, "rowSpan", "vSpan");
                    if (colspan > 1)
                    {
                        builder.Append(" colspan=\"").Append(colspan).Append('"');
                    }

                    if (rowspan > 1)
                    {
                        builder.Append(" rowspan=\"").Append(rowspan).Append('"');
                    }

                    if (!string.IsNullOrWhiteSpace(style))
                    {
                        builder.Append(" style=\"").Append(Html(style)).Append('"');
                    }

                    builder.Append('>')
                        .Append(BuildTableCellTextHtml(cell, themeColors, slideWidth, baseWidthPx))
                        .Append("</td>");
                }

                builder.Append("</tr>");
            }

            builder.Append("</tbody></table>");
            return builder.ToString();
        }

        private static IReadOnlyList<long> ReadTableColumnWidths(XElement table)
        {
            return table.Descendants()
                .Where(e => e.Name.LocalName == "tblGrid")
                .Elements()
                .Where(e => e.Name.LocalName == "gridCol")
                .Select(col => TryReadLong(col, "w", out long width) ? width : 0)
                .Where(width => width > 0)
                .ToList();
        }

        private static long ReadTableRowHeight(XElement row)
        {
            return TryReadLong(row, "h", out long height) ? height : 0;
        }

        private static int ReadTableSpan(XElement cell, params string[] names)
        {
            foreach (string name in names)
            {
                if (int.TryParse(cell.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attributeSpan) &&
                    attributeSpan > 1)
                {
                    return attributeSpan;
                }

                XElement? spanElement = cell.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
                if (spanElement != null &&
                    int.TryParse(spanElement.Attribute("val")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int childSpan) &&
                    childSpan > 1)
                {
                    return childSpan;
                }
            }

            return 1;
        }

        private static bool IsMergedTableCellContinuation(XElement cell)
        {
            return IsTableMergeFlag(cell, "hMerge") || IsTableMergeFlag(cell, "vMerge");
        }

        private static bool IsTableMergeFlag(XElement cell, string name)
        {
            string value = cell.Attribute(name)?.Value ??
                cell.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Attribute("val")?.Value ??
                string.Empty;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildTableCellTextHtml(
            XElement cell,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            var builder = new StringBuilder();
            foreach (XElement paragraph in cell.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string paragraphStyle = ReadParagraphStyle(paragraph, slideWidth, baseWidthPx);
                builder.Append("<p");
                if (!string.IsNullOrWhiteSpace(paragraphStyle))
                {
                    builder.Append(" style=\"").Append(Html(paragraphStyle)).Append('"');
                }

                builder.Append('>')
                    .Append(BuildParagraphRunsHtml(paragraph, themeColors, slideWidth, baseWidthPx))
                    .Append("</p>");
            }

            return builder.ToString();
        }

        private static string ReadParagraphText(XElement paragraph)
        {
            var builder = new StringBuilder();
            foreach (XElement element in paragraph.Descendants())
            {
                switch (element.Name.LocalName)
                {
                    case "t":
                        builder.Append(element.Value);
                        break;
                    case "tab":
                        builder.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        builder.Append('\n');
                        break;
                }
            }

            return builder.ToString();
        }

        private static string ReadParagraphStyle(XElement paragraph, long slideWidth, double baseWidthPx)
        {
            XElement? paragraphProperties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            if (paragraphProperties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            string align = paragraphProperties.Attribute("algn")?.Value ?? string.Empty;
            string? cssAlign = align switch
            {
                "ctr" => "center",
                "r" => "right",
                "just" or "dist" => "justify",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(cssAlign))
            {
                style.Append("text-align:").Append(cssAlign).Append(';');
            }

            if (TryReadLong(paragraphProperties, "marL", out long marginLeft) && marginLeft > 0)
            {
                style.Append("padding-left:")
                    .Append(FormatInvariant(TextMarginToPixels(marginLeft, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            if (TryReadLong(paragraphProperties, "indent", out long indent) && indent != 0)
            {
                style.Append("text-indent:")
                    .Append(FormatInvariant(TextMarginToPixels(indent, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            return style.ToString();
        }

        private static string ReadRunTextStyle(
            XElement? runProperties,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            var style = new StringBuilder();
            double pixelsPerInch = baseWidthPx / (slideWidth / 914400.0);
            if (runProperties?.Attribute("sz")?.Value is string sizeValue &&
                int.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) &&
                size > 0)
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(size / 100.0 / 72.0 * pixelsPerInch))
                    .Append("px;");
            }

            if (string.Equals(runProperties?.Attribute("b")?.Value, "1", StringComparison.Ordinal))
            {
                style.Append("font-weight:700;");
            }

            if (string.Equals(runProperties?.Attribute("i")?.Value, "1", StringComparison.Ordinal))
            {
                style.Append("font-style:italic;");
            }

            if (string.Equals(runProperties?.Attribute("u")?.Value, "sng", StringComparison.OrdinalIgnoreCase))
            {
                style.Append("text-decoration:underline;");
            }

            string? color = ReadPresentationColor(runProperties, themeColors);
            if (!string.IsNullOrWhiteSpace(color))
            {
                style.Append("color:").Append(color).Append(';');
            }

            string? typeface = runProperties?.Descendants().FirstOrDefault(e => e.Name.LocalName == "latin")?.Attribute("typeface")?.Value;
            if (!string.IsNullOrWhiteSpace(typeface) && !typeface.Contains('+', StringComparison.Ordinal))
            {
                style.Append("font-family:'").Append(typeface.Replace("'", "\\'", StringComparison.Ordinal)).Append("','Segoe UI',Arial,sans-serif;");
            }

            return style.ToString();
        }

        private static string ReadShapeBoxStyle(XElement? shapeProperties, IReadOnlyList<string> themeColors)
        {
            if (shapeProperties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            XElement? fillElement = shapeProperties.Elements().FirstOrDefault(e => e.Name.LocalName == "solidFill");
            bool hasShapeNoFill = shapeProperties.Elements().Any(e => e.Name.LocalName == "noFill");
            string? fill = ReadPresentationColor(fillElement, themeColors);
            if (!string.IsNullOrWhiteSpace(fill) && !hasShapeNoFill)
            {
                style.Append("background:").Append(fill).Append(';');
            }

            XElement? line = shapeProperties.Elements().FirstOrDefault(e => e.Name.LocalName == "ln");
            if (line != null && !line.Descendants().Any(e => e.Name.LocalName == "noFill"))
            {
                string? lineColor = ReadPresentationColor(line, themeColors);
                if (!string.IsNullOrWhiteSpace(lineColor))
                {
                    double widthPx = 1;
                    if (TryReadLong(line, "w", out long width) && width > 0)
                    {
                        widthPx = Math.Max(.5, width / 12700.0);
                    }

                    style.Append("border:")
                        .Append(FormatInvariant(widthPx))
                        .Append("px solid ")
                        .Append(lineColor)
                        .Append(';');
                }
            }

            return style.ToString();
        }

        private static string ReadTableCellStyle(XElement cell, IReadOnlyList<string> themeColors)
        {
            XElement? properties = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "tcPr");
            if (properties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            string? fill = ReadPresentationColor(properties.Elements().FirstOrDefault(e => e.Name.LocalName == "solidFill"), themeColors);
            if (!string.IsNullOrWhiteSpace(fill))
            {
                style.Append("background:").Append(fill).Append(';');
            }

            string? borderColor = properties.Elements()
                .Where(e => e.Name.LocalName.StartsWith("ln", StringComparison.Ordinal))
                .Select(line => ReadPresentationColor(line, themeColors))
                .FirstOrDefault(color => !string.IsNullOrWhiteSpace(color));
            if (!string.IsNullOrWhiteSpace(borderColor))
            {
                style.Append("border-color:").Append(borderColor).Append(';');
            }

            string anchor = properties.Attribute("anchor")?.Value ?? string.Empty;
            string? verticalAlign = anchor switch
            {
                "ctr" => "middle",
                "b" => "bottom",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(verticalAlign))
            {
                style.Append("vertical-align:").Append(verticalAlign).Append(';');
            }

            return style.ToString();
        }

        private static string? ReadSlideBackground(XDocument slide, IReadOnlyList<string> themeColors)
        {
            XElement? bg = slide.Descendants().FirstOrDefault(e => e.Name.LocalName == "bg");
            return ReadPresentationColor(bg, themeColors);
        }

        private static async Task<IReadOnlyList<PresentationPlaceholderBounds>> LoadSlidePlaceholderBoundsAsync(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> slideRelationships,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            string? layoutPath = slideRelationships.Values.FirstOrDefault(path =>
                path.Contains("/slideLayouts/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("slideLayouts/", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(layoutPath))
            {
                return Array.Empty<PresentationPlaceholderBounds>();
            }

            var result = new List<PresentationPlaceholderBounds>();
            XDocument? layout = await TryLoadXmlEntryAsync(archive, layoutPath).ConfigureAwait(false);
            if (layout != null)
            {
                result.AddRange(ReadPlaceholderBoundsFromPart(layout, slideWidth, slideHeight, baseWidthPx, baseHeightPx));
            }

            IReadOnlyDictionary<string, string> layoutRelationships = await LoadRelationshipsAsync(
                archive,
                GetRelationshipsPath(layoutPath),
                Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty).ConfigureAwait(false);
            string? masterPath = layoutRelationships.Values.FirstOrDefault(path =>
                path.Contains("/slideMasters/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("slideMasters/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(masterPath))
            {
                XDocument? master = await TryLoadXmlEntryAsync(archive, masterPath).ConfigureAwait(false);
                if (master != null)
                {
                    result.AddRange(ReadPlaceholderBoundsFromPart(master, slideWidth, slideHeight, baseWidthPx, baseHeightPx));
                }
            }

            return result;
        }

        private static IEnumerable<PresentationPlaceholderBounds> ReadPlaceholderBoundsFromPart(
            XDocument part,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            XElement? shapeTree = part.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
            foreach (XElement element in shapeTree?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (!TryReadPlaceholderInfo(element, out string? type, out string? index) ||
                    !TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, out string bounds))
                {
                    continue;
                }

                yield return new PresentationPlaceholderBounds
                {
                    Type = type,
                    Index = index,
                    BoundsStyle = bounds
                };
            }
        }

        private static bool TryReadPlaceholderBounds(
            XElement element,
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds,
            out string bounds)
        {
            bounds = string.Empty;
            if (!TryReadPlaceholderInfo(element, out string? type, out string? index))
            {
                return false;
            }

            PresentationPlaceholderBounds? match = null;
            if (!string.IsNullOrWhiteSpace(index))
            {
                match = placeholderBounds.FirstOrDefault(item =>
                    string.Equals(item.Index, index, StringComparison.OrdinalIgnoreCase));
            }

            match ??= placeholderBounds.FirstOrDefault(item =>
                PlaceholderTypesMatch(item.Type, type));
            if (match == null && string.IsNullOrWhiteSpace(type))
            {
                match = placeholderBounds.FirstOrDefault(item =>
                    PlaceholderTypesMatch(item.Type, "body"));
            }

            if (match == null)
            {
                return false;
            }

            bounds = match.BoundsStyle;
            return true;
        }

        private static bool TryReadPlaceholderInfo(XElement element, out string? type, out string? index)
        {
            XElement? placeholder = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
            type = placeholder?.Attribute("type")?.Value;
            index = placeholder?.Attribute("idx")?.Value;
            return placeholder != null;
        }

        private static bool PlaceholderTypesMatch(string? candidate, string? requested)
        {
            candidate = NormalizePlaceholderType(candidate);
            requested = NormalizePlaceholderType(requested);
            return !string.IsNullOrWhiteSpace(candidate) &&
                !string.IsNullOrWhiteSpace(requested) &&
                string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizePlaceholderType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "body";
            }

            return type switch
            {
                "ctrTitle" => "title",
                "subTitle" => "subtitle",
                "obj" => "body",
                _ => type
            };
        }

        private static string? ReadPresentationColor(XElement? parent, IReadOnlyList<string> themeColors)
        {
            if (parent == null)
            {
                return null;
            }

            XElement? solidFill = parent.Name.LocalName == "solidFill"
                ? parent
                : parent.Descendants().FirstOrDefault(e => e.Name.LocalName == "solidFill");
            if (solidFill == null)
            {
                return null;
            }

            XElement? srgb = solidFill.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            string? color = !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                ? "#" + value
                : null;

            if (string.IsNullOrWhiteSpace(color))
            {
                XElement? scheme = solidFill.Descendants().FirstOrDefault(e => e.Name.LocalName == "schemeClr");
                string? schemeName = scheme?.Attribute("val")?.Value;
                color = ReadPresentationThemeColor(schemeName, themeColors);
            }

            return string.IsNullOrWhiteSpace(color)
                ? null
                : ApplyColorTransforms(color, solidFill);
        }

        private static string? ReadPresentationThemeColor(string? schemeName, IReadOnlyList<string> themeColors)
        {
            if (string.IsNullOrWhiteSpace(schemeName) || themeColors.Count == 0)
            {
                return null;
            }

            int index = schemeName switch
            {
                "bg1" or "lt1" => 0,
                "tx1" or "dk1" => 1,
                "bg2" or "lt2" => 2,
                "tx2" or "dk2" => 3,
                "accent1" => 4,
                "accent2" => 5,
                "accent3" => 6,
                "accent4" => 7,
                "accent5" => 8,
                "accent6" => 9,
                "hlink" => 10,
                "folHlink" => 11,
                _ => -1
            };

            return index >= 0 && index < themeColors.Count ? themeColors[index] : null;
        }

        private static string ApplyColorTransforms(string color, XElement parent)
        {
            if (string.IsNullOrWhiteSpace(color) || !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            {
                return color;
            }

            int r = Convert.ToInt32(color.Substring(1, 2), 16);
            int g = Convert.ToInt32(color.Substring(3, 2), 16);
            int b = Convert.ToInt32(color.Substring(5, 2), 16);
            double lumMod = ReadPercentageTransform(parent, "lumMod", 100000) / 100000.0;
            double lumOff = ReadPercentageTransform(parent, "lumOff", 0) / 100000.0;
            r = ApplyLumTransform(r, lumMod, lumOff);
            g = ApplyLumTransform(g, lumMod, lumOff);
            b = ApplyLumTransform(b, lumMod, lumOff);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static int ReadPercentageTransform(XElement parent, string localName, int fallback)
        {
            XElement? element = parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
            return element != null && int.TryParse(element.Attribute("val")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        private static int ApplyLumTransform(int value, double lumMod, double lumOff)
        {
            return Math.Max(0, Math.Min(255, (int)Math.Round((value * lumMod) + (255 * lumOff))));
        }

        private static double TextMarginToPixels(long value, long slideWidth, double baseWidthPx)
        {
            double pixelsPerInch = baseWidthPx / (slideWidth / 914400.0);
            return value / 1000.0 / 72.0 * pixelsPerInch;
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            out string bounds)
        {
            return TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, (IReadOnlyList<PresentationPlaceholderBounds>?)null, out bounds);
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            PresentationGroupTransform? groupTransform,
            out string bounds)
        {
            return TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, groupTransform, null, out bounds);
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            IReadOnlyList<PresentationPlaceholderBounds>? placeholderBounds,
            out string bounds)
        {
            return TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, null, placeholderBounds, out bounds);
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            PresentationGroupTransform? groupTransform,
            IReadOnlyList<PresentationPlaceholderBounds>? placeholderBounds,
            out string bounds)
        {
            bounds = string.Empty;
            if (!TryReadRawBounds(element, out long x, out long y, out long cx, out long cy, out int rotation) ||
                cx <= 0 ||
                cy <= 0)
            {
                if (placeholderBounds != null && TryReadPlaceholderBounds(element, placeholderBounds, out string inheritedBounds))
                {
                    bounds = inheritedBounds;
                    return true;
                }

                return false;
            }

            double mappedX = groupTransform?.MapX(x) ?? x;
            double mappedY = groupTransform?.MapY(y) ?? y;
            double mappedCx = groupTransform?.MapCx(cx) ?? cx;
            double mappedCy = groupTransform?.MapCy(cy) ?? cy;
            bounds = "left:" + Pixels(mappedX, slideWidth, baseWidthPx) +
                ";top:" + Pixels(mappedY, slideHeight, baseHeightPx) +
                ";width:" + Pixels(mappedCx, slideWidth, baseWidthPx) +
                ";height:" + Pixels(mappedCy, slideHeight, baseHeightPx) + ";";
            if (rotation != 0)
            {
                bounds += "transform:rotate(" + FormatInvariant(rotation / 60000.0) + "deg);";
            }

            return true;
        }

        private static bool TryReadGroupTransform(
            XElement group,
            PresentationGroupTransform? parent,
            out PresentationGroupTransform? transform)
        {
            transform = null;
            XElement? groupProperties = group.Elements().FirstOrDefault(e => e.Name.LocalName == "grpSpPr");
            XElement? xfrm = groupProperties?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? off = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "off");
            XElement? ext = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
            XElement? childOff = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "chOff");
            XElement? childExt = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "chExt");
            if (off == null || ext == null || childOff == null || childExt == null ||
                !TryReadLong(off, "x", out long x) ||
                !TryReadLong(off, "y", out long y) ||
                !TryReadLong(ext, "cx", out long cx) ||
                !TryReadLong(ext, "cy", out long cy) ||
                !TryReadLong(childOff, "x", out long childX) ||
                !TryReadLong(childOff, "y", out long childY) ||
                !TryReadLong(childExt, "cx", out long childCx) ||
                !TryReadLong(childExt, "cy", out long childCy) ||
                cx <= 0 ||
                cy <= 0 ||
                childCx <= 0 ||
                childCy <= 0)
            {
                return false;
            }

            double mappedX = parent?.MapX(x) ?? x;
            double mappedY = parent?.MapY(y) ?? y;
            double mappedCx = parent?.MapCx(cx) ?? cx;
            double mappedCy = parent?.MapCy(cy) ?? cy;
            transform = new PresentationGroupTransform
            {
                X = mappedX,
                Y = mappedY,
                Cx = mappedCx,
                Cy = mappedCy,
                ChildX = childX,
                ChildY = childY,
                ChildCx = childCx,
                ChildCy = childCy
            };
            return true;
        }

        private static bool TryReadRawBounds(XElement element, out long x, out long y, out long cx, out long cy, out int rotation)
        {
            x = 0;
            y = 0;
            cx = 0;
            cy = 0;
            rotation = 0;
            XElement? properties = element.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "spPr" ||
                e.Name.LocalName == "picPr" ||
                e.Name.LocalName == "grpSpPr");
            XElement? xfrm = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm") ??
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? off = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "off");
            XElement? ext = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
            rotation = TryReadInt(xfrm ?? element, "rot");
            return off != null &&
                ext != null &&
                TryReadLong(off, "x", out x) &&
                TryReadLong(off, "y", out y) &&
                TryReadLong(ext, "cx", out cx) &&
                TryReadLong(ext, "cy", out cy);
        }

        private static bool TryReadLong(XElement element, string attributeName, out long value)
        {
            value = 0;
            return long.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static int TryReadInt(XElement element, string attributeName)
        {
            return int.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : -1;
        }

        private static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        private static string Pixels(long value, long total, double baseSizePx)
        {
            return FormatInvariant(value / (double)Math.Max(1, total) * baseSizePx) + "px";
        }

        private static string Pixels(double value, long total, double baseSizePx)
        {
            return FormatInvariant(value / Math.Max(1.0, total) * baseSizePx) + "px";
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
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };

            return "data:" + mime + ";base64," + Convert.ToBase64String(memory.ToArray());
        }

        private static (long Width, long Height) ReadSlideSize(XDocument presentation)
        {
            XElement? size = presentation.Descendants().FirstOrDefault(e => e.Name.LocalName == "sldSz");
            if (size != null &&
                TryReadLong(size, "cx", out long width) &&
                TryReadLong(size, "cy", out long height) &&
                width > 0 &&
                height > 0)
            {
                return (width, height);
            }

            return (DefaultSlideWidthEmu, DefaultSlideHeightEmu);
        }

        private static async Task<List<string>> ReadPresentationSlidePathsAsync(ZipArchive archive, XDocument presentation)
        {
            IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                archive,
                "ppt/_rels/presentation.xml.rels",
                "ppt").ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            return presentation.Descendants()
                .Where(e => e.Name.LocalName == "sldId")
                .Select(e => e.Attribute(relNs + "id")?.Value ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id) && relationships.ContainsKey(id))
                .Select(id => relationships[id])
                .ToList();
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

        private static string FormatInvariant(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
