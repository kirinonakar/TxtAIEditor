using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxPageLayout;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxStyleCatalog;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeHwpxTableHtmlRenderer
    {
        private readonly IReadOnlyDictionary<string, string> _paragraphStyles;
        private readonly IReadOnlyDictionary<string, string> _borderFillStyles;
        private readonly Action<StringBuilder, XElement, Queue<int>?> _appendBlock;

        internal OfficeHwpxTableHtmlRenderer(
            IReadOnlyDictionary<string, string> paragraphStyles,
            IReadOnlyDictionary<string, string> borderFillStyles,
            Action<StringBuilder, XElement, Queue<int>?> appendBlock)
        {
            _paragraphStyles = paragraphStyles;
            _borderFillStyles = borderFillStyles;
            _appendBlock = appendBlock;
        }

        internal string BuildHtml(XElement table, XElement? containingParagraph = null)
        {
            var rows = table.Elements().Where(e => e.Name.LocalName == "tr").ToList();
            if (rows.Count == 0)
            {
                rows = table.Descendants().Where(e => e.Name.LocalName == "tr").ToList();
            }

            var paragraphPageBreaks = ReadHwpxSingleCellPageBreaks(table, rows);
            var builder = new StringBuilder();
            bool hasColumnGrid = TryBuildHwpxTableTracks(
                table,
                "colCnt",
                "colAddr",
                "colSpan",
                "width",
                out IReadOnlyList<double> columnWidths);
            bool hasRowGrid = TryBuildHwpxTableTracks(
                table,
                "rowCnt",
                "rowAddr",
                "rowSpan",
                "height",
                out IReadOnlyList<double> rowHeights);
            string wrapperStyle = BuildHwpxTableWrapperStyle(table);
            string tableStyle = BuildHwpxTableStyle(table, containingParagraph, _paragraphStyles);
            builder.Append("<div class=\"doc-table-wrap hwpx-table-wrap\"");
            AppendStyleAttribute(builder, wrapperStyle);
            builder.Append("><table class=\"doc-table hwpx-table");
            if (paragraphPageBreaks != null)
            {
                builder.Append(" hwpx-split-table");
            }
            builder.Append('"');
            AppendStyleAttribute(builder, tableStyle);
            builder.Append('>');
            if (hasColumnGrid)
            {
                double totalWidth = columnWidths.Sum();
                builder.Append("<colgroup>");
                foreach (double columnWidth in columnWidths)
                {
                    builder.Append("<col style=\"width:")
                        .Append(CssPercent(columnWidth / totalWidth * 100.0))
                        .Append("\">");
                }

                builder.Append("</colgroup>");
            }

            builder.Append("<tbody>");
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                XElement row = rows[rowIndex];
                builder.Append("<tr");
                if (hasRowGrid && rowIndex < rowHeights.Count)
                {
                    builder.Append(" style=\"height:")
                        .Append(HwpxPoints(rowHeights[rowIndex]))
                        .Append('"');
                }

                builder.Append('>');
                var cells = row.Elements().Where(e => e.Name.LocalName == "tc").ToList();
                if (cells.Count == 0)
                {
                    cells = row.Descendants().Where(e => e.Name.LocalName == "tc").ToList();
                }

                foreach (XElement cell in cells)
                {
                    string colspan = ReadPositiveIntegerAttribute(cell, "colSpan");
                    string rowspan = ReadPositiveIntegerAttribute(cell, "rowSpan");
                    XElement? subList = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "subList");
                    builder.Append("<td");
                    if (GetAttributeValue(subList, "lineWrap") == "SQUEEZE")
                    {
                        builder.Append(" class=\"hwpx-squeeze-cell\"");
                    }
                    if (!string.IsNullOrWhiteSpace(colspan))
                    {
                        builder.Append(" colspan=\"").Append(Html(colspan)).Append('"');
                    }

                    if (!string.IsNullOrWhiteSpace(rowspan))
                    {
                        builder.Append(" rowspan=\"").Append(Html(rowspan)).Append('"');
                    }

                    string borderFillId = GetAttributeValue(cell, "borderFillIDRef");
                    if (string.IsNullOrWhiteSpace(borderFillId))
                    {
                        borderFillId = GetAttributeValue(table, "borderFillIDRef");
                    }

                    var cellStyles = new List<string>();
                    if (_borderFillStyles.TryGetValue(borderFillId, out string? borderFillStyle))
                    {
                        cellStyles.Add(borderFillStyle);
                    }

                    if (TryReadHwpxDimensions(cell, "cellSz", out double cellWidth, out double cellHeight))
                    {
                        if (!hasColumnGrid)
                        {
                            cellStyles.Add("width:" + HwpxPoints(cellWidth));
                        }

                        if (!hasRowGrid)
                        {
                            cellStyles.Add("height:" + HwpxPoints(cellHeight));
                        }
                    }

                    string cellPadding = BuildHwpxBoxSpacingStyle(cell, "cellMargin", "padding");
                    if (!string.IsNullOrWhiteSpace(cellPadding))
                    {
                        cellStyles.Add(cellPadding);
                    }

                    string verticalAlignment = GetAttributeValue(subList, "vertAlign").ToUpperInvariant();
                    cellStyles.Add("vertical-align:" + (verticalAlignment switch
                    {
                        "CENTER" => "middle",
                        "BOTTOM" => "bottom",
                        _ => "top"
                    }));
                    AppendStyleAttribute(builder, string.Join(';', cellStyles));

                    builder.Append('>');
                    int before = builder.Length;
                    if (subList != null && HasHwpxFloatingContainer(subList))
                    {
                        foreach (XElement cellElement in cell.Elements())
                        {
                            if (ReferenceEquals(cellElement, subList))
                            {
                                AppendCellContent(
                                    builder,
                                    subList.Elements().Where(element =>
                                        paragraphPageBreaks?.ContainsKey(element) == true ||
                                        !IsHwpxFloatingObjectSpacerParagraph(element)),
                                    paragraphPageBreaks);
                            }
                            else
                            {
                                _appendBlock(builder, cellElement, null);
                            }
                        }
                    }
                    else
                    {
                        AppendCellContent(
                            builder,
                            paragraphPageBreaks != null && subList != null ? subList.Elements() : cell.Elements(),
                            paragraphPageBreaks);
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

        private void AppendCellContent(
            StringBuilder builder,
            IEnumerable<XElement> elements,
            IReadOnlyDictionary<XElement, Queue<int>>? paragraphPageBreaks)
        {
            foreach (XElement element in elements)
            {
                Queue<int>? pageBreaks = null;
                if (paragraphPageBreaks != null && paragraphPageBreaks.TryGetValue(element, out pageBreaks) &&
                    pageBreaks.TryPeek(out int offset) && offset == 0)
                {
                    pageBreaks.Dequeue();
                    builder.Append(PageBreakHtml);
                }

                _appendBlock(builder, element, pageBreaks);
            }
        }

        private static IReadOnlyDictionary<XElement, Queue<int>>? ReadHwpxSingleCellPageBreaks(
            XElement table, IReadOnlyList<XElement> rows)
        {
            if (rows.Count != 1 || GetAttributeValue(table, "pageBreak") != "CELL" ||
                table.Ancestors().Any(element => element.Name.LocalName is "tbl" or "container"))
            {
                return null;
            }

            XElement[] cells = rows[0].Elements().Where(element => element.Name.LocalName == "tc").Take(2).ToArray();
            if (cells.Length != 1)
            {
                return null;
            }

            XElement? subList = cells[0].Elements().FirstOrDefault(element => element.Name.LocalName == "subList");
            if (subList == null)
            {
                return null;
            }

            // A split cell can report its full height while the table reports one fragment's height.
            // Use that evidence before treating a drop in the cell's saved position as a page break.
            bool useVerticalReset = TryReadHwpxDimensions(table, "sz", out _, out double tableHeight) &&
                TryReadHwpxDimensions(cells[0], "cellSz", out _, out double cellHeight) &&
                cellHeight > tableHeight;
            Dictionary<XElement, Queue<int>>? paragraphPageBreaks = null;
            double? previousVerticalPosition = null;
            foreach (XElement paragraph in subList.Elements().Where(element => element.Name.LocalName == "p"))
            {
                var breaks = new Queue<int>();
                AppendHwpxSavedPageBreaks(paragraph, useVerticalReset, ref previousVerticalPosition, breaks);
                if (breaks.Count > 0)
                {
                    paragraphPageBreaks ??= new Dictionary<XElement, Queue<int>>();
                    paragraphPageBreaks.Add(paragraph, breaks);
                }
            }

            return paragraphPageBreaks;
        }

        private static bool HasHwpxFloatingContainer(XElement subList)
        {
            return subList.Descendants().Any(element =>
            {
                if (element.Name.LocalName != "container")
                {
                    return false;
                }

                XElement? position = element.Elements()
                    .FirstOrDefault(child => child.Name.LocalName == "pos");
                return GetAttributeValue(position, "treatAsChar") == "0";
            });
        }

        private static bool IsHwpxFloatingObjectSpacerParagraph(XElement element)
        {
            if (element.Name.LocalName != "p")
            {
                return false;
            }

            return !element.Descendants().Any(descendant =>
                descendant.Name.LocalName is "tbl" or "container" or "pic" or "img" ||
                descendant.Name.LocalName == "t" && !string.IsNullOrWhiteSpace(descendant.Value));
        }

        private static bool TryBuildHwpxTableTracks(
            XElement table,
            string trackCountAttribute,
            string addressAttribute,
            string spanAttribute,
            string sizeAttribute,
            out IReadOnlyList<double> trackSizes)
        {
            if (!int.TryParse(
                    GetAttributeValue(table, trackCountAttribute),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int trackCount) ||
                trackCount <= 0)
            {
                trackSizes = Array.Empty<double>();
                return false;
            }

            var edges = Enumerable.Range(0, trackCount + 1)
                .Select(_ => new List<(int Target, double Delta)>())
                .ToArray();
            foreach (XElement cell in table.Descendants().Where(element =>
                element.Name.LocalName == "tc" &&
                !IsInsideNestedElement(table, element, "tbl")))
            {
                XElement? address = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellAddr");
                XElement? span = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellSpan");
                XElement? size = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellSz");
                if (!int.TryParse(
                        GetAttributeValue(address, addressAttribute),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int start) ||
                    !int.TryParse(
                        GetAttributeValue(span, spanAttribute),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int length) ||
                    !double.TryParse(
                        GetAttributeValue(size, sizeAttribute),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double extent) ||
                    start < 0 ||
                    length <= 0 ||
                    start + length > trackCount ||
                    extent <= 0)
                {
                    continue;
                }

                int end = start + length;
                edges[start].Add((end, extent));
                edges[end].Add((start, -extent));
            }

            var boundaries = new double?[trackCount + 1];
            var pending = new Queue<int>();
            boundaries[0] = 0;
            pending.Enqueue(0);
            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                foreach ((int target, double delta) in edges[current])
                {
                    if (boundaries[target].HasValue)
                    {
                        continue;
                    }

                    boundaries[target] = boundaries[current]!.Value + delta;
                    pending.Enqueue(target);
                }
            }

            if (boundaries.Any(boundary => !boundary.HasValue))
            {
                trackSizes = Array.Empty<double>();
                return false;
            }

            var sizes = new List<double>(trackCount);
            for (int index = 0; index < trackCount; index++)
            {
                double size = boundaries[index + 1]!.Value - boundaries[index]!.Value;
                if (size <= 0)
                {
                    trackSizes = Array.Empty<double>();
                    return false;
                }

                sizes.Add(size);
            }

            trackSizes = sizes;
            return true;
        }

        private static string BuildHwpxTableWrapperStyle(XElement table)
        {
            XElement? outMargin = table.Elements().FirstOrDefault(e => e.Name.LocalName == "outMargin");
            if (outMargin == null)
            {
                return string.Empty;
            }

            double top = ReadHwpxDoubleAttribute(outMargin, "top");
            double bottom = ReadHwpxDoubleAttribute(outMargin, "bottom");
            return "margin:" + HwpxPoints(top) + " 0 " + HwpxPoints(bottom);
        }

        private static string BuildHwpxTableStyle(
            XElement table,
            XElement? containingParagraph,
            IReadOnlyDictionary<string, string> paragraphStyles)
        {
            var styles = new List<string>();
            if (TryReadHwpxDimensions(table, "sz", out double width, out double height))
            {
                styles.Add("width:" + HwpxPoints(width));
                styles.Add("height:" + HwpxPoints(height));
            }

            double cellSpacing = ReadHwpxDoubleAttribute(table, "cellSpacing");
            if (cellSpacing > 0)
            {
                styles.Add("border-collapse:separate");
                styles.Add("border-spacing:" + HwpxPoints(cellSpacing));
            }
            else
            {
                styles.Add("border-collapse:collapse");
                styles.Add("border-spacing:0");
            }

            XElement? position = table.Elements().FirstOrDefault(e => e.Name.LocalName == "pos");
            string horizontalAlignment = GetAttributeValue(position, "horzAlign").ToUpperInvariant();
            bool treatAsCharacter = GetAttributeValue(position, "treatAsChar") == "1";
            string paragraphAlignment = GetHwpxHorizontalAlignment(containingParagraph, paragraphStyles);
            if (treatAsCharacter && !string.IsNullOrWhiteSpace(paragraphAlignment))
            {
                horizontalAlignment = paragraphAlignment;
            }

            double horizontalOffset = ReadHwpxSignedOffset(position, "horzOffset");
            switch (horizontalAlignment)
            {
                case "CENTER":
                    styles.Add("margin-left:auto");
                    styles.Add("margin-right:auto");
                    break;
                case "RIGHT":
                    styles.Add("margin-left:auto");
                    styles.Add("margin-right:0");
                    break;
                default:
                    styles.Add("margin-left:" + HwpxPoints(horizontalOffset));
                    styles.Add("margin-right:auto");
                    break;
            }

            return string.Join(';', styles);
        }

        private static string GetHwpxHorizontalAlignment(
            XElement? paragraph,
            IReadOnlyDictionary<string, string> paragraphStyles)
        {
            if (paragraph == null)
            {
                return string.Empty;
            }

            string style = GetHwpxParagraphStyle(paragraph, paragraphStyles);
            if (style.Contains("text-align:center", StringComparison.OrdinalIgnoreCase))
            {
                return "CENTER";
            }

            if (style.Contains("text-align:right", StringComparison.OrdinalIgnoreCase))
            {
                return "RIGHT";
            }

            if (style.Contains("text-align:left", StringComparison.OrdinalIgnoreCase))
            {
                return "LEFT";
            }

            return string.Empty;
        }

        private static double ReadHwpxSignedOffset(XElement? element, string attributeName)
        {
            string value = GetAttributeValue(element, attributeName);
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
            {
                return 0;
            }

            // HWPX stores position offsets as unsigned XML values even when the
            // source document uses a negative signed 32-bit offset.
            if (offset > int.MaxValue && offset <= uint.MaxValue)
            {
                offset -= 1L << 32;
            }

            return offset;
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
    }
}
