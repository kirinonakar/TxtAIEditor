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

            WordTableLayout layout = BuildTableLayout(table);
            XElement? tableProperties = GetDirectProperty(table, "tblPr");
            XElement? tableBorders = GetDirectProperty(tableProperties, "tblBorders");
            var builder = new StringBuilder();
            builder.Append("<div class=\"doc-table-wrap\"><table class=\"doc-table\"");
            AppendStyleAttribute(builder, BuildTableStyle(tableProperties));
            builder.Append("><tbody>");
            for (int rowIndex = 0; rowIndex < layout.Rows.Count; rowIndex++)
            {
                XElement row = layout.Rows[rowIndex];
                XElement? rowProperties = GetDirectProperty(row, "trPr");
                builder.Append("<tr");
                AppendStyleAttribute(builder, BuildRowStyle(rowProperties));
                builder.Append('>');
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
                int columnIndex = 0;

                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    int columnSpan = ReadGridSpanValue(cell);
                    WordMergeKind verticalMerge = ReadMergeKind(cell, "vMerge");
                    if (verticalMerge == WordMergeKind.Continue &&
                        activeVerticalMerges.TryGetValue(columnIndex, out WordTableCellLayout? mergedCell))
                    {
                        mergedCell.RowSpan++;
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

                columnCount = Math.Max(columnCount, columnIndex);
                cellsByRow.Add(renderedCells);
                activeVerticalMerges = nextVerticalMerges;
            }

            return new WordTableLayout(rows, cellsByRow, Math.Max(1, columnCount));
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

        private static string BuildTableStyle(XElement? tableProperties)
        {
            var styles = new List<string>();
            AppendStyle(styles, ReadShadingStyle(tableProperties));

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
                string? borderStyle = ReadBorderStyle(cellBorders, edge);
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
                element.Name.LocalName is "shd" or "tblBorders" or "tcBorders" or "gridSpan" or "hMerge" or "vMerge" ||
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
        }
    }
}
