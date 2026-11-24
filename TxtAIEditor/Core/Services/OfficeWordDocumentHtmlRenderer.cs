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
using static TxtAIEditor.Core.Services.OfficeHtmlEncoder;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWordDocumentHtmlRenderer
    {
        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OfficeLegacyPackage.OpenAsync(filePath).ConfigureAwait(false);
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

            XDocument? styles = await TryLoadXmlEntryAsync(archive, "word/styles.xml").ConfigureAwait(false);
            var paragraphStyles = new Dictionary<string, XElement>(StringComparer.Ordinal);
            string defaultStyleId = string.Empty;
            foreach (XElement style in styles?.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (style.Name.LocalName == "style" && GetAttributeValue(style, "type") == "paragraph")
                {
                    string id = GetAttributeValue(style, "styleId");
                    paragraphStyles[id] = style;
                    if (GetAttributeValue(style, "default") is "1" or "true" or "on")
                    {
                        defaultStyleId = id;
                    }
                }
            }

            List<XElement> blocks = EnumerateWordBlocks(body).ToList();
            bool hasSavedPagination = blocks.Any(block => block.Name.LocalName == "p" &&
                block.Descendants().Any(element => element.Name.LocalName == "lastRenderedPageBreak"));
            var sections = new Queue<XElement>(blocks
                .Select(block => GetDirectProperty(GetDirectProperty(block, "pPr"), "sectPr"))
                .OfType<XElement>());
            if (GetDirectProperty(body, "sectPr") is XElement finalSection)
            {
                sections.Enqueue(finalSection);
            }

            var content = new StringBuilder();
            bool startNewSectionPage = false;
            foreach (XElement block in blocks)
            {
                XElement? properties = GetDirectProperty(block, "pPr");
                bool breakBefore = startNewSectionPage ||
                    (block.Name.LocalName == "p" && HasWordPageBreakBefore(properties, paragraphStyles, defaultStyleId));
                if (breakBefore && content.Length > 0 && !EndsWithPageBreak(content))
                {
                    content.Append(PageBreakHtml);
                }

                AppendBlockHtml(content, archive, relationships, block, documentFlow: true);
                startNewSectionPage = false;
                if (GetDirectProperty(properties, "sectPr") != null)
                {
                    sections.Dequeue();
                    XElement? sectionType = sections.TryPeek(out XElement? nextSection)
                        ? GetDirectProperty(nextSection, "type")
                        : null;
                    startNewSectionPage = GetAttributeValue(sectionType, "val") is not ("continuous" or "nextColumn");
                }
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(
                    getString("OfficeViewerNoContent", "No content to display."));
            }

            string? pageWidth = null;
            string? pagePadding = null;
            string? pageHeight = null;
            if (TryReadWordPageLayout(document, out string documentPageWidth, out string documentPagePadding, out string documentPageHeight))
            {
                pageWidth = documentPageWidth;
                pagePadding = documentPagePadding;
                pageHeight = documentPageHeight;
            }

            bool legacy = Path.GetExtension(filePath).Equals(".doc", StringComparison.OrdinalIgnoreCase);
            string pageParts = legacy
                ? await BuildPagePartsAsync(archive, relationships, body.Element(body.Name.Namespace + "sectPr")).ConfigureAwait(false)
                : string.Empty;
            XDocument? propertiesDocument = legacy
                ? await TryLoadXmlEntryAsync(archive, "docProps/app.xml").ConfigureAwait(false)
                : null;
            bool savedSinglePage = propertiesDocument?.Root?.Elements()
                .FirstOrDefault(property => property.Name.LocalName == "Pages")?.Value == "1";
            return BuildDocumentHtml(
                Path.GetFileName(filePath),
                content.ToString(),
                pageWidth,
                pagePadding,
                pageHeight,
                hasSavedPagination,
                paginateWord: legacy,
                wordPageParts: pageParts,
                savedWordSinglePage: savedSinglePage);
        }

        private static async Task<string> BuildPagePartsAsync(ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships, XElement? section)
        {
            var parts = new StringBuilder();
            foreach (XElement reference in section?.Elements() ?? Enumerable.Empty<XElement>())
            {
                string kind = reference.Name.LocalName switch
                {
                    "headerReference" => "header",
                    "footerReference" => "footer",
                    _ => string.Empty
                };
                string variant = GetAttributeValue(reference, "type");
                if (kind.Length == 0 || variant is not ("default" or "even" or "first") ||
                    !relationships.TryGetValue(GetAttributeValue(reference, "id"), out string? path)) continue;

                XDocument? part = await TryLoadXmlEntryAsync(archive, path).ConfigureAwait(false);
                if (part?.Root == null) continue;
                var images = await LoadRelationshipsAsync(archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(path), "word").ConfigureAwait(false);

                parts.Append("<template data-doc-page-part=\"").Append(kind).Append('-').Append(variant).Append("\"><")
                    .Append(kind).Append(" class=\"doc-page-part\">");
                foreach (XElement block in EnumerateWordBlocks(part.Root))
                {
                    AppendBlockHtml(parts, archive, images, block);
                }
                parts.Append("</").Append(kind).Append("></template>");
            }
            return parts.ToString();
        }

        private static bool TryReadWordPageLayout(
            XDocument document,
            out string pageWidth,
            out string pagePadding,
            out string pageHeight)
        {
            pageWidth = string.Empty;
            pagePadding = string.Empty;
            pageHeight = string.Empty;

            XElement? sectionProperties = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "sectPr");
            XElement? pageSize = GetDirectProperty(sectionProperties, "pgSz");
            if (!TryReadWordTwips(pageSize, "w", out int widthTwips) ||
                !TryReadWordTwips(pageSize, "h", out int heightTwips) ||
                widthTwips <= 0 ||
                heightTwips <= 0)
            {
                return false;
            }

            string orientation = GetAttributeValue(pageSize, "orient");
            if (orientation.Equals("landscape", StringComparison.OrdinalIgnoreCase) && widthTwips < heightTwips ||
                orientation.Equals("portrait", StringComparison.OrdinalIgnoreCase) && widthTwips > heightTwips)
            {
                (widthTwips, heightTwips) = (heightTwips, widthTwips);
            }

            pageWidth = WordTwipsToCssPoints(widthTwips);
            pageHeight = WordTwipsToCssPoints(heightTwips);

            XElement? pageMargins = GetDirectProperty(sectionProperties, "pgMar");
            if (TryReadWordTwips(pageMargins, "top", out int topTwips) &&
                TryReadWordTwips(pageMargins, "right", out int rightTwips) &&
                TryReadWordTwips(pageMargins, "bottom", out int bottomTwips) &&
                TryReadWordTwips(pageMargins, "left", out int leftTwips))
            {
                pagePadding = string.Join(
                    ' ',
                    WordTwipsToCssPoints(topTwips),
                    WordTwipsToCssPoints(rightTwips),
                    WordTwipsToCssPoints(bottomTwips),
                    WordTwipsToCssPoints(leftTwips));
            }

            return true;
        }

        private static bool TryReadWordTwips(XElement? element, string attributeName, out int twips)
        {
            return int.TryParse(
                GetAttributeValue(element, attributeName),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out twips);
        }

        private static string WordTwipsToCssPoints(int twips)
        {
            return (twips / 20.0).ToString("0.###", CultureInfo.InvariantCulture) + "pt";
        }

        private static IEnumerable<XElement> EnumerateWordBlocks(XElement container)
        {
            foreach (XElement child in container.Elements())
            {
                if (child.Name.LocalName is "p" or "tbl")
                {
                    yield return child;
                    continue;
                }

                foreach (XElement block in EnumerateWordBlocks(child))
                {
                    yield return block;
                }
            }
        }

        private static bool HasWordPageBreakBefore(
            XElement? properties, IReadOnlyDictionary<string, XElement> styles, string defaultStyleId)
        {
            XElement? pageBreak = GetDirectProperty(properties, "pageBreakBefore");
            string styleId = GetAttributeValue(GetDirectProperty(properties, "pStyle"), "val");
            if (string.IsNullOrEmpty(styleId))
            {
                styleId = defaultStyleId;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pageBreak == null && styleId.Length > 0 &&
                   styles.TryGetValue(styleId, out XElement? style) && visited.Add(styleId))
            {
                pageBreak = GetDirectProperty(GetDirectProperty(style, "pPr"), "pageBreakBefore");
                styleId = GetAttributeValue(GetDirectProperty(style, "basedOn"), "val");
            }

            return pageBreak != null && GetAttributeValue(pageBreak, "val") is not ("0" or "false" or "off");
        }

        private static bool EndsWithPageBreak(StringBuilder builder)
        {
            return builder.Length >= PageBreakHtml.Length &&
                builder.ToString(builder.Length - PageBreakHtml.Length, PageBreakHtml.Length) == PageBreakHtml;
        }

        private static void AppendBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement block,
            bool documentFlow = false)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    AppendParagraphHtml(builder, archive, relationships, block, documentFlow);
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

        private static void AppendParagraphHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement paragraph,
            bool documentFlow)
        {
            string paragraphStyle = BuildParagraphStyle(paragraph);
            var content = new StringBuilder();
            bool suppressLeadingCachedBreak = builder.Length == 0 || EndsWithPageBreak(builder);
            foreach (XElement child in paragraph.Elements())
            {
                if (child.Name.LocalName == "pPr")
                {
                    continue;
                }

                AppendInlineHtml(content, archive, relationships, child, documentFlow, suppressLeadingCachedBreak);
            }

            int trailingBreaks = 0;
            while (EndsWithPageBreak(content))
            {
                content.Length -= PageBreakHtml.Length;
                trailingBreaks++;
            }
            string styleAttribute = string.IsNullOrWhiteSpace(paragraphStyle)
                ? string.Empty
                : " style=\"" + Html(paragraphStyle) + "\"";
            if (content.Length > 0)
            {
                builder.Append("<p class=\"doc-paragraph\"").Append(styleAttribute).Append('>')
                    .Append(content).Append("</p>");
            }
            else if (trailingBreaks == 0)
            {
                builder.Append("<p class=\"doc-paragraph empty-paragraph\"").Append(styleAttribute).Append("></p>");
            }

            for (int i = 0; i < trailingBreaks; i++)
            {
                builder.Append(PageBreakHtml);
            }
        }

        private static string BuildParagraphStyle(XElement paragraph)
        {
            XElement? paragraphProperties = GetDirectProperty(paragraph, "pPr");
            XElement? justification = GetDirectProperty(paragraphProperties, "jc");
            string value = GetAttributeValue(justification, "val").ToLowerInvariant();
            string? alignment = value switch
            {
                "center" => "center",
                "right" or "end" => "right",
                "both" or "justify" => "justify",
                "distribute" => "justify",
                "left" or "start" => "left",
                _ => null
            };

            var styles = new List<string>();
            AppendStyle(styles, BuildRunStyle(GetDirectProperty(paragraphProperties, "rPr")));
            if (alignment != null) styles.Add("text-align:" + alignment);
            if (value == "distribute") styles.Add("text-align-last:justify");

            XElement? spacing = GetDirectProperty(paragraphProperties, "spacing");
            XElement? indentation = GetDirectProperty(paragraphProperties, "ind");
            AppendMeasurement(styles, spacing, "before", "margin-top");
            AppendMeasurement(styles, spacing, "after", "margin-bottom");
            if (TryReadWordTwips(spacing, "line", out int line) && line > 0)
            {
                string height = GetAttributeValue(spacing, "lineRule") switch
                {
                    "exact" => WordTwipsToCssPoints(line),
                    "atLeast" => "max(1em," + WordTwipsToCssPoints(line) + ")",
                    _ => (line / 240.0).ToString("0.###", CultureInfo.InvariantCulture)
                };
                styles.Add("line-height:" + height);
            }
            AppendMeasurement(styles, indentation, "left", "margin-left");
            AppendMeasurement(styles, indentation, "right", "margin-right");
            AppendMeasurement(styles, indentation, "firstLine", "text-indent");
            return string.Join(';', styles);
        }

        private static void AppendMeasurement(List<string> styles, XElement? properties, string attribute, string css)
        {
            if (TryReadWordTwips(properties, attribute, out int value))
            {
                styles.Add(css + ":" + WordTwipsToCssPoints(value));
            }
        }

        private static void AppendInlineHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement element,
            bool documentFlow,
            bool suppressLeadingCachedBreak)
        {
            switch (element.Name.LocalName)
            {
                case "r":
                    AppendRunHtml(builder, archive, relationships, element, documentFlow, suppressLeadingCachedBreak);
                    break;
                case "hyperlink":
                case "ins":
                case "smartTag":
                case "sdt":
                case "sdtContent":
                    foreach (XElement child in element.Elements())
                    {
                        AppendInlineHtml(builder, archive, relationships, child, documentFlow, suppressLeadingCachedBreak);
                    }

                    break;
                case "tab":
                    builder.Append('\t');
                    break;
                case "br":
                case "cr":
                case "lastRenderedPageBreak":
                    AppendWordBreak(builder, element, documentFlow, suppressLeadingCachedBreak);
                    break;
                case "drawing":
                case "pict":
                    AppendImagesHtml(builder, archive, relationships, element);
                    break;
                default:
                    foreach (XElement child in element.Elements())
                    {
                        AppendInlineHtml(builder, archive, relationships, child, documentFlow, suppressLeadingCachedBreak);
                    }

                    break;
            }
        }

        private static void AppendRunHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement run,
            bool documentFlow,
            bool suppressLeadingCachedBreak)
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
                    case "lastRenderedPageBreak":
                        AppendWordBreak(builder, child, documentFlow, suppressLeadingCachedBreak);
                        break;
                    case "drawing":
                    case "pict":
                        AppendImagesHtml(builder, archive, relationships, child);
                        break;
                }
            }
        }

        private static void AppendWordBreak(
            StringBuilder builder, XElement element, bool documentFlow, bool suppressLeadingCachedBreak)
        {
            if (element.Name.LocalName == "lastRenderedPageBreak")
            {
                if (documentFlow && !(builder.Length == 0 && suppressLeadingCachedBreak) && !EndsWithPageBreak(builder))
                {
                    builder.Append(PageBreakHtml);
                }
                return;
            }

            builder.Append(documentFlow && GetAttributeValue(element, "type") == "page" ? PageBreakHtml : "<br>");
        }

        private static string BuildRunStyle(XElement? properties)
        {
            if (properties == null)
            {
                return string.Empty;
            }

            var styles = new List<string>();
            XElement? fonts = GetDirectProperty(properties, "rFonts");
            string ascii = GetAttributeValue(fonts, "ascii");
            string eastAsian = GetAttributeValue(fonts, "eastAsia");
            string fontFamilies = string.Join(',', new[] { ascii, eastAsian }.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(font => Regex.IsMatch(font, @"^[\p{L}\p{M}\p{N} ._+\-]{1,128}$"))
                .Select(font => "'" + font + "'"));
            if (fontFamilies.Length > 0) styles.Add("font-family:" + fontFamilies + ",serif");
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

            XElement? size = properties.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "sz") ??
                properties.Elements().FirstOrDefault(e => e.Name.LocalName == "szCs");
            if (int.TryParse(
                    GetAttributeValue(size, "val"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int halfPoints) &&
                halfPoints > 0)
            {
                styles.Add("font-size:" +
                    (halfPoints / 2.0).ToString("0.###", CultureInfo.InvariantCulture) +
                    "pt");
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
            if (TryAppendPositionedShape(builder, archive, relationships, element)) return;
            XElement? extent = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "extent");
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

                builder.Append("<span class=\"doc-image\"><img src=\"").Append(Html(dataUri)).Append("\" alt=\"\"");
                if (TryReadEmus(extent, "cx", out long width) && width > 0)
                {
                    string style = "width:" + EmusToPoints(width);
                    if (TryReadEmus(extent, "cy", out long height) && height > 0)
                        style += ";aspect-ratio:" + width.ToString(CultureInfo.InvariantCulture) + "/" +
                            height.ToString(CultureInfo.InvariantCulture) + ";object-fit:fill";
                    AppendStyleAttribute(builder, style);
                }
                builder.Append("></span>");
            }
        }

        private static bool TryAppendPositionedShape(StringBuilder builder, ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships, XElement element)
        {
            XElement? anchor = GetDirectProperty(element, "anchor");
            XElement? textbox = GetDirectProperty(anchor, "txbxContent");
            XElement? line = GetDirectProperty(anchor, "ln");
            if (textbox == null && line == null) return false;
            XElement? extent = GetDirectProperty(anchor, "extent");
            XElement? horizontal = GetDirectProperty(anchor, "positionH");
            XElement? vertical = GetDirectProperty(anchor, "positionV");
            if (!TryReadEmus(extent, "cx", out long width) || !TryReadEmus(extent, "cy", out long height) ||
                !long.TryParse(GetDirectProperty(horizontal, "posOffset")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long left) ||
                !long.TryParse(GetDirectProperty(vertical, "posOffset")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long top)) return false;

            string verticalReference = GetAttributeValue(vertical, "relativeFrom");
            string style = "left:" + EmusToPoints(left) + ";top:" + EmusToPoints(top) +
                ";width:" + EmusToPoints(width) + ";height:" + EmusToPoints(height);
            if (TryReadEmus(line, "w", out long stroke) && stroke > 0)
                style += ";border:" + EmusToPoints(stroke) + " solid currentColor";
            builder.Append("<template class=\"doc-positioned-drawing\" data-horizontal=\"")
                .Append(GetAttributeValue(horizontal, "relativeFrom") == "page" ? "page" : "margin").Append("\" data-vertical=\"")
                .Append(verticalReference is "paragraph" or "page" ? verticalReference : "margin").Append("\"><div class=\"doc-positioned-shape\"");
            AppendStyleAttribute(builder, style);
            builder.Append('>');
            if (textbox != null)
            {
                foreach (XElement block in EnumerateWordBlocks(textbox)) AppendBlockHtml(builder, archive, relationships, block);
            }
            builder.Append("</div></template>");
            return true;
        }

        private static bool TryReadEmus(XElement? element, string name, out long value) =>
            long.TryParse(GetAttributeValue(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private static string EmusToPoints(long value) => (value / 12700.0).ToString("0.###", CultureInfo.InvariantCulture) + "pt";

        private static string BuildTableHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships,
            XElement table)
        {
            if (!HasTableContent(table))
            {
                return string.Empty;
            }

            WordTableLayout layout = BuildTableLayout(table);
            XElement? tableProperties = GetDirectProperty(table, "tblPr");
            XElement? tableBorders = GetDirectProperty(tableProperties, "tblBorders");
            int[] columnWidths = ReadFixedTableColumns(table, tableProperties);
            int tableWidth = columnWidths.Sum();
            var builder = new StringBuilder();
            builder.Append("<div class=\"doc-table-wrap\"><table class=\"doc-table");
            if (tableWidth > 0) builder.Append(" word-fixed-table");
            builder.Append('"');
            AppendStyleAttribute(builder, BuildTableStyle(tableProperties, tableWidth));
            builder.Append('>');
            if (tableWidth > 0)
            {
                builder.Append("<colgroup>");
                foreach (int width in columnWidths)
                {
                    builder.Append("<col style=\"width:")
                        .Append((width * 100.0 / tableWidth).ToString("0.#####", CultureInfo.InvariantCulture))
                        .Append("%\">");
                }
                builder.Append("</colgroup>");
            }
            builder.Append("<tbody>");
            for (int rowIndex = 0; rowIndex < layout.Rows.Count; rowIndex++)
            {
                XElement row = layout.Rows[rowIndex];
                XElement? rowProperties = GetDirectProperty(row, "trPr");
                builder.Append("<tr");
                AppendStyleAttribute(builder, BuildRowStyle(rowProperties));
                builder.Append('>');
                AppendOmittedTableColumns(builder, ReadOmittedTableColumns(rowProperties, "gridBefore"));
                foreach (WordTableCellLayout cell in layout.CellsByRow[rowIndex])
                {
                    builder.Append("<td");
                    if (cell.ColumnSpan > 1)
                    {
                        builder.Append(" colspan=\"")
                            .Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture))
                            .Append('"');
                    }

                    if (cell.RowSpan > 1)
                    {
                        builder.Append(" rowspan=\"")
                            .Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture))
                            .Append('"');
                    }

                    AppendStyleAttribute(
                        builder,
                        BuildCellStyle(
                            cell,
                            tableBorders,
                            layout.Rows.Count,
                            layout.ColumnCount));
                    builder.Append('>');
                    int before = builder.Length;
                    foreach (XElement child in cell.Element.Elements())
                    {
                        if (child.Name.LocalName == "tcPr")
                        {
                            continue;
                        }

                        AppendBlockHtml(builder, archive, relationships, child);
                    }

                    if (builder.Length == before)
                    {
                        builder.Append("&nbsp;");
                    }

                    builder.Append("</td>");
                }

                AppendOmittedTableColumns(builder, ReadOmittedTableColumns(rowProperties, "gridAfter"));
                builder.Append("</tr>");
            }

            builder.Append("</tbody></table></div>");
            return builder.ToString();
        }

        private static WordTableLayout BuildTableLayout(XElement table)
        {
            var rows = table.Elements()
                .Where(e => e.Name.LocalName == "tr")
                .ToList();
            var cellsByRow = new List<List<WordTableCellLayout>>(rows.Count);
            var activeVerticalMerges = new Dictionary<int, WordTableCellLayout>();
            int columnCount = 0;

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                XElement row = rows[rowIndex];
                var renderedCells = new List<WordTableCellLayout>();
                var nextVerticalMerges = new Dictionary<int, WordTableCellLayout>();
                WordTableCellLayout? previousCell = null;
                XElement? rowProperties = GetDirectProperty(row, "trPr");
                int columnIndex = ReadOmittedTableColumns(rowProperties, "gridBefore");

                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    int columnSpan = ReadGridSpanValue(cell);
                    WordMergeKind verticalMerge = ReadMergeKind(cell, "vMerge");
                    if (verticalMerge == WordMergeKind.Continue &&
                        activeVerticalMerges.TryGetValue(columnIndex, out WordTableCellLayout? mergedCell))
                    {
                        mergedCell.RowSpan++;
                        mergedCell.LastRowElement = cell;
                        AddActiveVerticalMerge(nextVerticalMerges, mergedCell);
                        columnIndex = mergedCell.ColumnIndex + mergedCell.ColumnSpan;
                        previousCell = null;
                        continue;
                    }

                    WordMergeKind horizontalMerge = ReadMergeKind(cell, "hMerge");
                    if (horizontalMerge == WordMergeKind.Continue &&
                        previousCell != null &&
                        previousCell.RowIndex == rowIndex &&
                        previousCell.ColumnIndex + previousCell.ColumnSpan == columnIndex)
                    {
                        previousCell.ColumnSpan += columnSpan;
                        if (nextVerticalMerges.Values.Any(value => ReferenceEquals(value, previousCell)))
                        {
                            AddActiveVerticalMerge(nextVerticalMerges, previousCell);
                        }

                        columnIndex += columnSpan;
                        continue;
                    }

                    var renderedCell = new WordTableCellLayout(cell, rowIndex, columnIndex, columnSpan);
                    renderedCells.Add(renderedCell);
                    if (verticalMerge == WordMergeKind.Restart)
                    {
                        AddActiveVerticalMerge(nextVerticalMerges, renderedCell);
                    }

                    previousCell = renderedCell;
                    columnIndex += columnSpan;
                }

                columnCount = Math.Max(columnCount, columnIndex + ReadOmittedTableColumns(rowProperties, "gridAfter"));
                cellsByRow.Add(renderedCells);
                activeVerticalMerges = nextVerticalMerges;
            }

            return new WordTableLayout(rows, cellsByRow, Math.Max(1, columnCount));
        }

        private static int ReadOmittedTableColumns(XElement? rowProperties, string name) =>
            int.TryParse(GetAttributeValue(GetDirectProperty(rowProperties, name), "val"),
                NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? Math.Min(512, count) : 0;

        private static void AppendOmittedTableColumns(StringBuilder builder, int count)
        {
            if (count > 0)
            {
                builder.Append("<td aria-hidden=\"true\" style=\"border:0;padding:0;min-width:0\" colspan=\"")
                    .Append(count.ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            }
        }

        private static void AddActiveVerticalMerge(
            IDictionary<int, WordTableCellLayout> activeMerges,
            WordTableCellLayout cell)
        {
            for (int offset = 0; offset < cell.ColumnSpan; offset++)
            {
                activeMerges[cell.ColumnIndex + offset] = cell;
            }
        }

        private static int[] ReadFixedTableColumns(XElement table, XElement? tableProperties)
        {
            if (GetAttributeValue(GetDirectProperty(tableProperties, "tblLayout"), "type") != "fixed" ||
                GetDirectProperty(table, "tblGrid") is not { } grid) return [];

            var widths = new List<int>();
            foreach (XElement column in grid.Elements().Where(element => element.Name.LocalName == "gridCol"))
            {
                if (!TryReadWordTwips(column, "w", out int width) || width is <= 0 or > 65535 || widths.Count >= 512) return [];
                widths.Add(width);
            }
            return widths.ToArray();
        }

        private static string BuildTableStyle(XElement? tableProperties, int gridWidth)
        {
            var styles = new List<string>();
            AppendStyle(styles, ReadShadingStyle(tableProperties));
            if (gridWidth > 0)
            {
                string width = WordTwipsToCssPoints(gridWidth);
                if (TryReadWordTwips(GetDirectProperty(tableProperties, "tblInd"), "w", out int indent) && indent != 0)
                {
                    string margin = WordTwipsToCssPoints(indent);
                    styles.Add("margin-left:" + margin);
                    if (indent > 0) width = "min(" + width + ",max(0px,100% - " + margin + "))";
                }
                styles.Add("width:" + width);
            }

            XElement? borders = GetDirectProperty(tableProperties, "tblBorders");
            foreach (string edge in new[] { "top", "right", "bottom", "left" })
            {
                AppendStyle(styles, ReadBorderStyle(borders, edge));
            }

            return string.Join(';', styles);
        }

        private static string BuildRowStyle(XElement? rowProperties)
        {
            var styles = new List<string>();
            AppendStyle(styles, ReadShadingStyle(rowProperties));
            AppendMeasurement(styles, GetDirectProperty(rowProperties, "trHeight"), "val", "height");
            return string.Join(';', styles);
        }

        private static string BuildCellStyle(
            WordTableCellLayout cell,
            XElement? tableBorders,
            int rowCount,
            int columnCount)
        {
            XElement? cellProperties = GetDirectProperty(cell.Element, "tcPr");
            XElement? cellBorders = GetDirectProperty(cellProperties, "tcBorders");
            var styles = new List<string>();
            AppendStyle(styles, ReadShadingStyle(cellProperties));
            XElement? margins = GetDirectProperty(cellProperties, "tcMar");
            foreach (string side in new[] { "top", "right", "bottom", "left" })
            {
                XElement? margin = GetBorderElement(margins, side);
                if (GetAttributeValue(margin, "type") == "dxa")
                {
                    AppendMeasurement(styles, margin, "w", "padding-" + side);
                }
            }

            XElement? verticalAlignment = GetDirectProperty(cellProperties, "vAlign");
            string alignment = GetAttributeValue(verticalAlignment, "val").ToLowerInvariant();
            if (alignment is "center" or "middle")
            {
                styles.Add("vertical-align:middle");
            }
            else if (alignment is "bottom")
            {
                styles.Add("vertical-align:bottom");
            }

            bool hasExplicitBorders = cellBorders != null || tableBorders != null;
            foreach (string edge in new[] { "top", "right", "bottom", "left" })
            {
                XElement? continuationBorders = edge == "bottom"
                    ? GetDirectProperty(GetDirectProperty(cell.LastRowElement, "tcPr"), "tcBorders")
                    : null;
                string? borderStyle = ReadBorderStyle(continuationBorders, edge) ?? ReadBorderStyle(cellBorders, edge);
                if (borderStyle == null)
                {
                    string tableEdge = GetTableBorderEdge(cell, edge, rowCount, columnCount);
                    borderStyle = ReadBorderStyle(tableBorders, tableEdge, edge);
                }

                if (borderStyle != null)
                {
                    styles.Add(borderStyle);
                }
                else if (hasExplicitBorders)
                {
                    styles.Add("border-" + edge + ":0");
                }
            }

            return string.Join(';', styles);
        }

        private static string GetTableBorderEdge(
            WordTableCellLayout cell,
            string edge,
            int rowCount,
            int columnCount)
        {
            return edge switch
            {
                "top" when cell.RowIndex == 0 => "top",
                "bottom" when cell.RowIndex + cell.RowSpan >= rowCount => "bottom",
                "left" when cell.ColumnIndex == 0 => "left",
                "right" when cell.ColumnIndex + cell.ColumnSpan >= columnCount => "right",
                "top" or "bottom" => "insideH",
                "left" or "right" => "insideV",
                _ => edge
            };
        }

        private static string? ReadShadingStyle(XElement? properties)
        {
            XElement? shading = GetDirectProperty(properties, "shd");
            if (shading == null)
            {
                return null;
            }

            string pattern = GetAttributeValue(shading, "val").ToLowerInvariant();
            if (pattern is "nil" or "none")
            {
                return "background-color:transparent";
            }

            string fill = GetAttributeValue(shading, "fill");
            if (TryNormalizeHexColor(fill, out string normalizedFill))
            {
                return "background-color:#" + normalizedFill;
            }

            if (pattern == "solid" &&
                TryNormalizeHexColor(GetAttributeValue(shading, "color"), out string normalizedColor))
            {
                return "background-color:#" + normalizedColor;
            }

            return fill.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "background-color:transparent"
                : null;
        }

        private static string? ReadBorderStyle(
            XElement? borders,
            string edge,
            string? cssEdge = null)
        {
            XElement? border = GetBorderElement(borders, edge);
            if (border == null)
            {
                return null;
            }

            string property = "border-" + (cssEdge ?? edge);
            string value = GetAttributeValue(border, "val").ToLowerInvariant();
            if (value is "nil" or "none")
            {
                return property + ":0";
            }

            string width = ReadBorderWidth(border);
            string borderKind = value switch
            {
                "double" or "triple" => "double",
                "dotted" or "dotdash" or "dotdotdash" => "dotted",
                "dashed" or "dashsmallgap" or "dashdotstroked" or "dashdotdot" => "dashed",
                _ => "solid"
            };
            string color = ReadBorderColor(border);
            return property + ":" + width + " " + borderKind + " " + color;
        }

        private static XElement? GetBorderElement(XElement? borders, string edge)
        {
            if (borders == null)
            {
                return null;
            }

            string[] candidates = edge switch
            {
                "left" => new[] { "left", "start" },
                "right" => new[] { "right", "end" },
                _ => new[] { edge }
            };
            return borders.Elements()
                .FirstOrDefault(element => candidates.Contains(element.Name.LocalName, StringComparer.Ordinal));
        }

        private static string ReadBorderWidth(XElement border)
        {
            string sizeValue = GetAttributeValue(border, "sz");
            if (int.TryParse(
                    sizeValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int size) &&
                size > 0)
            {
                return (size / 8.0).ToString("0.###", CultureInfo.InvariantCulture) + "pt";
            }

            return "1px";
        }

        private static string ReadBorderColor(XElement border)
        {
            string value = GetAttributeValue(border, "color");
            return TryNormalizeHexColor(value, out string normalizedColor)
                ? "#" + normalizedColor
                : "currentColor";
        }

        private static bool TryNormalizeHexColor(string value, out string normalizedColor)
        {
            normalizedColor = string.Empty;
            value = value.Trim().TrimStart('#');
            if (!Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return false;
            }

            normalizedColor = value.ToUpperInvariant();
            return true;
        }

        private static void AppendStyle(List<string> styles, string? style)
        {
            if (!string.IsNullOrWhiteSpace(style))
            {
                styles.Add(style);
            }
        }

        private static void AppendStyleAttribute(StringBuilder builder, string style)
        {
            if (string.IsNullOrWhiteSpace(style))
            {
                return;
            }

            builder.Append(" style=\"")
                .Append(Html(style))
                .Append('"');
        }

        private static bool HasTableContent(XElement table)
        {
            return table.Descendants().Any(element =>
                element.Name.LocalName is "drawing" or "pict" ||
                (element.Name.LocalName == "t" && !string.IsNullOrWhiteSpace(element.Value)));
        }

        private static int ReadGridSpanValue(XElement cell)
        {
            XElement? cellProperties = GetDirectProperty(cell, "tcPr");
            XElement? gridSpan = GetDirectProperty(cellProperties, "gridSpan");
            string value = GetAttributeValue(gridSpan, "val");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) && span > 0
                ? span
                : 1;
        }

        private static WordMergeKind ReadMergeKind(XElement cell, string propertyName)
        {
            XElement? cellProperties = GetDirectProperty(cell, "tcPr");
            XElement? merge = GetDirectProperty(cellProperties, propertyName);
            if (merge == null)
            {
                return WordMergeKind.None;
            }

            return string.Equals(
                GetAttributeValue(merge, "val"),
                "restart",
                StringComparison.OrdinalIgnoreCase)
                ? WordMergeKind.Restart
                : WordMergeKind.Continue;
        }

        private static XElement? GetDirectProperty(XElement? parent, string localName)
        {
            return parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value
                ?? string.Empty;
        }

        private enum WordMergeKind
        {
            None,
            Restart,
            Continue
        }

        private sealed class WordTableLayout
        {
            public WordTableLayout(
                IReadOnlyList<XElement> rows,
                IReadOnlyList<List<WordTableCellLayout>> cellsByRow,
                int columnCount)
            {
                Rows = rows;
                CellsByRow = cellsByRow;
                ColumnCount = columnCount;
            }

            public IReadOnlyList<XElement> Rows { get; }

            public IReadOnlyList<List<WordTableCellLayout>> CellsByRow { get; }

            public int ColumnCount { get; }
        }

        private sealed class WordTableCellLayout
        {
            public WordTableCellLayout(XElement element, int rowIndex, int columnIndex, int columnSpan)
            {
                Element = element;
                RowIndex = rowIndex;
                ColumnIndex = columnIndex;
                ColumnSpan = columnSpan;
            }

            public XElement Element { get; }

            public int RowIndex { get; }

            public int ColumnIndex { get; }

            public int ColumnSpan { get; set; }

            public int RowSpan { get; set; } = 1;
            public XElement? LastRowElement { get; set; }
        }
    }
}
