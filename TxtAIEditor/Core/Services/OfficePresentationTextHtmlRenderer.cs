using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficePresentationRenderingUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationTextHtmlRenderer
    {
        public static string BuildShapeTextHtml(
            XElement shape,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            var paragraphs = new StringBuilder();
            XElement? textBody = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (textBody == null)
            {
                return string.Empty;
            }

            double fontScale = ReadNormAutofitScale(textBody);
            double fallbackFontSizePx =
                PointsToPixels(ReadFallbackFontPoint(shape), slideWidth, baseWidthPx) * fontScale;

            foreach (XElement paragraph in textBody.Elements().Where(e => e.Name.LocalName == "p"))
            {
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                bool hasBullet = paragraph.Descendants()
                    .Any(e => e.Name.LocalName == "buChar" || e.Name.LocalName == "buAutoNum");
                string paragraphStyle = ReadParagraphStyle(paragraph, slideWidth, baseWidthPx);
                paragraphStyle += ReadParagraphDefaultRunStyle(
                    paragraph,
                    textBody,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale,
                    fallbackFontSizePx);
                paragraphs.Append("<p");
                if (!string.IsNullOrWhiteSpace(paragraphStyle))
                {
                    paragraphs.Append(" style=\"").Append(Html(paragraphStyle)).Append('"');
                }

                paragraphs.Append('>');
                if (hasBullet)
                {
                    string bullet = paragraph.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "buChar")
                        ?.Attribute("char")?.Value ?? "•";
                    paragraphs.Append("<span>").Append(Html(bullet)).Append(" </span>");
                }

                paragraphs.Append(BuildParagraphRunsHtml(
                    paragraph,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale,
                    textBody));
                paragraphs.Append("</p>");
            }

            return paragraphs.ToString();
        }

        public static string BuildTableHtml(
            XElement table,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            XDocument? tableStyles)
        {
            var builder = new StringBuilder("<table>");
            List<XElement> rows = table.Elements()
                .Where(e => e.Name.LocalName == "tr")
                .ToList();
            if (rows.Count == 0)
            {
                rows = table.Descendants()
                    .Where(e => e.Name.LocalName == "tr")
                    .ToList();
            }

            IReadOnlyList<long> columnWidths = ReadTableColumnWidths(table);
            int columnCount = Math.Max(
                columnWidths.Count,
                rows.Count == 0
                    ? 0
                    : rows.Max(row => row.Elements().Count(e => e.Name.LocalName == "tc")));
            XElement? tableStyle = ReadTableStyleDefinition(table, tableStyles);
            bool useBuiltInMediumStyle2 = tableStyle == null &&
                IsBuiltInMediumStyle2Accent1(table, tableStyles);
            XElement? tableProperties = table.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tblPr");
            bool firstRow = ReadBooleanAttribute(tableProperties, "firstRow");
            bool lastRow = ReadBooleanAttribute(tableProperties, "lastRow");
            bool bandRow = ReadBooleanAttribute(tableProperties, "bandRow");
            bool firstColumn = ReadBooleanAttribute(tableProperties, "firstCol");
            bool lastColumn = ReadBooleanAttribute(tableProperties, "lastCol");
            bool bandColumn = ReadBooleanAttribute(tableProperties, "bandCol");

            if (columnWidths.Count > 0)
            {
                int widthCount = Math.Max(columnWidths.Count, columnCount);
                long knownWidthTotal = columnWidths
                    .Where(width => width > 0)
                    .Sum();
                int missingWidthCount = widthCount - columnWidths.Count(width => width > 0);
                long fallbackWidth = missingWidthCount > 0
                    ? Math.Max(1, knownWidthTotal / missingWidthCount)
                    : 0;
                long totalWidth = Math.Max(
                    1,
                    knownWidthTotal + (fallbackWidth * missingWidthCount));
                builder.Append("<colgroup>");
                for (int index = 0; index < widthCount; index++)
                {
                    long width = index < columnWidths.Count && columnWidths[index] > 0
                        ? columnWidths[index]
                        : fallbackWidth;
                    builder.Append("<col style=\"width:")
                        .Append(FormatInvariant(width / (double)totalWidth * 100))
                        .Append("%\">");
                }

                builder.Append("</colgroup>");
            }

            builder.Append("<tbody>");
            List<long> rowHeights = rows
                .Select(ReadTableRowHeight)
                .ToList();
            long totalHeight = rowHeights.Sum(height => Math.Max(0, height));
            bool hasRowHeights = totalHeight > 0;
            if (!hasRowHeights)
            {
                totalHeight = Math.Max(1, rows.Count);
            }

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                XElement row = rows[rowIndex];
                long rowHeight = ReadTableRowHeight(row);
                builder.Append("<tr");
                if (hasRowHeights && rowHeight > 0)
                {
                    builder.Append(" style=\"height:")
                        .Append(FormatInvariant(rowHeight / (double)totalHeight * 100))
                        .Append("%\"");
                }
                else if (!hasRowHeights && rows.Count > 0)
                {
                    builder.Append(" style=\"height:")
                        .Append(FormatInvariant(100.0 / rows.Count))
                        .Append("%\"");
                }

                builder.Append('>');
                int columnIndex = 0;
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    if (IsMergedTableCellContinuation(cell))
                    {
                        columnIndex++;
                        continue;
                    }

                    bool isFirstColumn = firstColumn && columnIndex == 0;
                    bool isLastColumn = lastColumn &&
                        (columnCount <= 0 || columnIndex >= columnCount - 1);
                    int bandRowIndex = rowIndex - (firstRow ? 1 : 0);
                    int bandColumnIndex = columnIndex - (firstColumn ? 1 : 0);
                    string? bandRowName = bandRow && bandRowIndex >= 0
                        ? (bandRowIndex % 2 == 0 ? "band1H" : "band2H")
                        : null;
                    string? bandColumnName = bandColumn && bandColumnIndex >= 0
                        ? (bandColumnIndex % 2 == 0 ? "band1V" : "band2V")
                        : null;
                    string style = ReadTableCellStyle(
                        cell,
                        themeColors,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx,
                        tableStyle,
                        useBuiltInMediumStyle2,
                        firstRow && rowIndex == 0,
                        lastRow && rowIndex == rows.Count - 1,
                        bandRowName,
                        isFirstColumn,
                        isLastColumn,
                        bandColumnName);
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
                        .Append(BuildTableCellTextHtml(
                            cell,
                            themeColors,
                            slideWidth,
                            baseWidthPx))
                        .Append("</td>");

                    columnIndex += Math.Max(1, colspan);
                }

                builder.Append("</tr>");
            }

            builder.Append("</tbody></table>");
            return builder.ToString();
        }

        public static string ReadTextBodyBoxStyle(
            XElement? textBody,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            XElement? bodyProperties = textBody?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "bodyPr");
            long left = ReadInsetEmu(bodyProperties, "lIns", 91440);
            long right = ReadInsetEmu(bodyProperties, "rIns", 91440);
            long top = ReadInsetEmu(bodyProperties, "tIns", 45720);
            long bottom = ReadInsetEmu(bodyProperties, "bIns", 45720);
            var style = new StringBuilder("padding:")
                .Append(Pixels(top, slideHeight, baseHeightPx)).Append(' ')
                .Append(Pixels(right, slideWidth, baseWidthPx)).Append(' ')
                .Append(Pixels(bottom, slideHeight, baseHeightPx)).Append(' ')
                .Append(Pixels(left, slideWidth, baseWidthPx)).Append(';');

            string anchor = bodyProperties?.Attribute("anchor")?.Value ?? string.Empty;
            string? verticalAlign = anchor switch
            {
                "ctr" or "just" or "dist" => "center",
                "b" => "flex-end",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(verticalAlign))
            {
                style.Append("display:flex;flex-direction:column;justify-content:")
                    .Append(verticalAlign)
                    .Append(';');
            }

            return style.ToString();
        }

        private static string BuildParagraphRunsHtml(
            XElement paragraph,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            double fontScale = 1.0,
            XElement? textBody = null)
        {
            var builder = new StringBuilder();
            XElement? defaultRunProperties =
                ReadParagraphDefaultRunProperties(paragraph, textBody);
            foreach (XElement element in paragraph.Elements())
            {
                if (element.Name.LocalName == "r" || element.Name.LocalName == "fld")
                {
                    XElement? runProperties = element.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "rPr") ??
                        defaultRunProperties;
                    string runStyle = ReadRunTextStyle(
                        runProperties,
                        themeColors,
                        slideWidth,
                        baseWidthPx,
                        fontScale);
                    string text = string.Concat(
                        element.Descendants()
                            .Where(e => e.Name.LocalName == "t")
                            .Select(e => e.Value));
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

        private static IReadOnlyList<long> ReadTableColumnWidths(XElement table)
        {
            return table.Descendants()
                .Where(e => e.Name.LocalName == "tblGrid")
                .Elements()
                .Where(e => e.Name.LocalName == "gridCol")
                .Select(column => TryReadLong(column, "w", out long width) ? width : 0)
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
                if (int.TryParse(
                        cell.Attribute(name)?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int attributeSpan) &&
                    attributeSpan > 1)
                {
                    return attributeSpan;
                }

                XElement? spanElement = cell.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == name);
                if (spanElement != null &&
                    int.TryParse(
                        spanElement.Attribute("val")?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int childSpan) &&
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
                cell.Descendants().FirstOrDefault(e => e.Name.LocalName == name)
                    ?.Attribute("val")?.Value ??
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
            XElement? textBody = cell.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "txBody");
            double fontScale = textBody == null ? 1.0 : ReadNormAutofitScale(textBody);

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
                    .Append(BuildParagraphRunsHtml(
                        paragraph,
                        themeColors,
                        slideWidth,
                        baseWidthPx,
                        fontScale,
                        textBody))
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

        private static long ReadInsetEmu(
            XElement? bodyProperties,
            string attributeName,
            long fallback)
        {
            return bodyProperties != null &&
                TryReadLong(bodyProperties, attributeName, out long value) &&
                value >= 0
                    ? value
                    : fallback;
        }

        private static XElement? ReadParagraphDefaultRunProperties(
            XElement paragraph,
            XElement? textBody)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            XElement? defaultRunProperties = paragraphProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr") ??
                paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "endParaRPr");
            if (defaultRunProperties != null)
            {
                return defaultRunProperties;
            }

            int level = 0;
            if (paragraphProperties != null &&
                int.TryParse(
                    paragraphProperties.Attribute("lvl")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int readLevel) &&
                readLevel >= 0)
            {
                level = Math.Min(8, readLevel);
            }

            XElement? listStyle = textBody?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "lstStyle");
            return listStyle?.Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName ==
                    "lvl" + (level + 1).ToString(CultureInfo.InvariantCulture) + "pPr")
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr");
        }

        private static string ReadParagraphDefaultRunStyle(
            XElement paragraph,
            XElement? textBody,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            double fontScale,
            double fallbackFontSizePx)
        {
            XElement? defaultRunProperties =
                ReadParagraphDefaultRunProperties(paragraph, textBody);
            string style = ReadRunTextStyle(
                defaultRunProperties,
                themeColors,
                slideWidth,
                baseWidthPx,
                fontScale);
            if (!style.Contains("font-size:", StringComparison.OrdinalIgnoreCase) &&
                fallbackFontSizePx > 0)
            {
                style = "font-size:" +
                    FormatInvariant(fallbackFontSizePx) +
                    "px;" +
                    style;
            }

            return style;
        }

        private static double ReadFallbackFontPoint(XElement shape)
        {
            string? placeholderType = shape.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ph")
                ?.Attribute("type")?.Value;
            return NormalizePlaceholderType(placeholderType) switch
            {
                "title" => 44.0,
                "subtitle" => 32.0,
                "dt" or "sldNum" or "ftr" => 12.0,
                _ => 18.0
            };
        }

        private static double ReadNormAutofitScale(XElement textBody)
        {
            XElement? bodyProperties = textBody.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "bodyPr");
            XElement? normalAutofit = bodyProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "normAutofit");
            if (normalAutofit != null &&
                int.TryParse(
                    normalAutofit.Attribute("fontScale")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int fontScale) &&
                fontScale > 0)
            {
                return fontScale / 100000.0;
            }

            return 1.0;
        }

        private static string ReadParagraphStyle(
            XElement paragraph,
            long slideWidth,
            double baseWidthPx)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            if (paragraphProperties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            string align = paragraphProperties.Attribute("algn")?.Value ?? string.Empty;
            string? cssAlign = align switch
            {
                "l" => "left",
                "ctr" => "center",
                "r" => "right",
                "just" or "dist" or "thaiDist" or "justLow" => "justify",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(cssAlign))
            {
                style.Append("text-align:").Append(cssAlign).Append(';');
            }

            if (TryReadLong(paragraphProperties, "marL", out long marginLeft) &&
                marginLeft > 0)
            {
                style.Append("padding-left:")
                    .Append(FormatInvariant(
                        PointsToPixels(marginLeft / 1000.0, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            if (TryReadLong(paragraphProperties, "indent", out long indent) &&
                indent != 0)
            {
                style.Append("text-indent:")
                    .Append(FormatInvariant(
                        PointsToPixels(indent / 1000.0, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            return style.ToString();
        }

        private static string ReadRunTextStyle(
            XElement? runProperties,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            double fontScale = 1.0)
        {
            var style = new StringBuilder();
            if (runProperties?.Attribute("sz")?.Value is string sizeValue &&
                int.TryParse(
                    sizeValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int size) &&
                size > 0)
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(
                        PointsToPixels(size / 100.0, slideWidth, baseWidthPx) *
                        fontScale))
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

            if (string.Equals(
                runProperties?.Attribute("u")?.Value,
                "sng",
                StringComparison.OrdinalIgnoreCase))
            {
                style.Append("text-decoration:underline;");
            }

            string? color = ReadPresentationColor(runProperties, themeColors);
            if (!string.IsNullOrWhiteSpace(color))
            {
                style.Append("color:").Append(color).Append(';');
            }

            string? typeface = runProperties?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "latin")
                ?.Attribute("typeface")?.Value;
            if (!string.IsNullOrWhiteSpace(typeface) &&
                !typeface.Contains('+', StringComparison.Ordinal))
            {
                style.Append("font-family:'")
                    .Append(typeface.Replace("'", "\\'", StringComparison.Ordinal))
                    .Append("','Segoe UI',Arial,sans-serif;");
            }

            return style.ToString();
        }

        private static string ReadTableCellStyle(
            XElement cell,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            XElement? tableStyle,
            bool useBuiltInMediumStyle2,
            bool isFirstRow,
            bool isLastRow,
            string? bandRowName,
            bool isFirstColumn,
            bool isLastColumn,
            string? bandColumnName)
        {
            XElement? properties = cell.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tcPr");

            var style = new StringBuilder();
            XElement? styleRegion = ReadTableStyleRegion(
                tableStyle,
                isFirstRow,
                isLastRow,
                bandRowName,
                isFirstColumn,
                isLastColumn,
                bandColumnName);
            XElement? wholeTableRegion = tableStyle?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "wholeTbl");
            string? fill = ReadPresentationFill(
                ReadFillElement(styleRegion),
                themeColors);
            fill ??= ReadPresentationFill(
                ReadFillElement(wholeTableRegion),
                themeColors);
            bool hasNoFill = properties?.Elements()
                .Any(e => e.Name.LocalName == "noFill") == true;
            string? directFill = ReadPresentationFill(
                properties?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName is "solidFill" or "gradFill" or "pattFill" or "blipFill"),
                themeColors);
            string? builtInFill = useBuiltInMediumStyle2
                ? ReadBuiltInMediumStyle2Fill(themeColors, isFirstRow, bandRowName)
                : null;
            if (hasNoFill)
            {
                style.Append("background:transparent;");
            }
            else if (!string.IsNullOrWhiteSpace(directFill))
            {
                style.Append("background:").Append(directFill).Append(';');
            }
            else if (!string.IsNullOrWhiteSpace(builtInFill))
            {
                style.Append("background:").Append(builtInFill).Append(';');
            }
            else if (!string.IsNullOrWhiteSpace(fill))
            {
                style.Append("background:").Append(fill).Append(';');
            }

            string? textColor = ReadPresentationColor(
                styleRegion?.Descendants().FirstOrDefault(e => e.Name.LocalName == "tcTxStyle"),
                themeColors);
            textColor ??= ReadPresentationColor(
                wholeTableRegion?.Descendants().FirstOrDefault(e => e.Name.LocalName == "tcTxStyle"),
                themeColors);
            if (useBuiltInMediumStyle2)
            {
                textColor = isFirstRow ? "#ffffff" : "#1f1f1f";
            }
            if (!string.IsNullOrWhiteSpace(textColor))
            {
                style.Append("color:").Append(textColor).Append(';');
            }

            XElement? topLine = ReadTableLine(properties, styleRegion, wholeTableRegion, "lnT");
            XElement? rightLine = ReadTableLine(properties, styleRegion, wholeTableRegion, "lnR");
            XElement? bottomLine = ReadTableLine(properties, styleRegion, wholeTableRegion, "lnB");
            XElement? leftLine = ReadTableLine(properties, styleRegion, wholeTableRegion, "lnL");
            if (useBuiltInMediumStyle2 &&
                topLine == null &&
                rightLine == null &&
                bottomLine == null &&
                leftLine == null)
            {
                style.Append("border:1px solid #ffffff;");
            }

            AppendTableCellBorder(
                style,
                "top",
                topLine,
                themeColors,
                slideWidth,
                baseWidthPx);
            AppendTableCellBorder(
                style,
                "right",
                rightLine,
                themeColors,
                slideWidth,
                baseWidthPx);
            AppendTableCellBorder(
                style,
                "bottom",
                bottomLine,
                themeColors,
                slideWidth,
                baseWidthPx);
            AppendTableCellBorder(
                style,
                "left",
                leftLine,
                themeColors,
                slideWidth,
                baseWidthPx);

            if (properties != null)
            {
                long left = ReadCellInset(properties, "marL", "lIns", 91440);
                long right = ReadCellInset(properties, "marR", "rIns", 91440);
                long top = ReadCellInset(properties, "marT", "tIns", 45720);
                long bottom = ReadCellInset(properties, "marB", "bIns", 45720);
                style.Append("padding:")
                    .Append(Pixels(top, slideHeight, baseHeightPx)).Append(' ')
                    .Append(Pixels(right, slideWidth, baseWidthPx)).Append(' ')
                    .Append(Pixels(bottom, slideHeight, baseHeightPx)).Append(' ')
                    .Append(Pixels(left, slideWidth, baseWidthPx)).Append(';');
            }

            string anchor = properties?.Attribute("anchor")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(anchor))
            {
                anchor = cell.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "txBody")?
                    .Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "bodyPr")?
                    .Attribute("anchor")?.Value ?? string.Empty;
            }

            string? verticalAlign = anchor switch
            {
                "ctr" or "just" or "dist" => "middle",
                "b" => "bottom",
                "t" => "top",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(verticalAlign))
            {
                style.Append("vertical-align:").Append(verticalAlign).Append(';');
            }

            return style.ToString();
        }

        private static XElement? ReadTableStyleDefinition(
            XElement table,
            XDocument? tableStyles)
        {
            if (tableStyles == null)
            {
                return null;
            }

            string? styleId = ReadTableStyleId(table);
            if (string.IsNullOrWhiteSpace(styleId))
            {
                return null;
            }

            string normalizedStyleId = NormalizeTableStyleId(styleId);
            return tableStyles.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "tblStyle" &&
                    string.Equals(
                        NormalizeTableStyleId(e.Attribute("styleId")?.Value),
                        normalizedStyleId,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static string? ReadTableStyleId(XElement table)
        {
            return table.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tblPr")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tableStyleId")?
                .Value;
        }

        private static bool IsBuiltInMediumStyle2Accent1(
            XElement table,
            XDocument? tableStyles)
        {
            const string mediumStyle2Accent1Id =
                "5C22544A-7EE6-4342-B048-85BDC9FD1C3A";
            string normalizedStyleId = NormalizeTableStyleId(ReadTableStyleId(table));
            if (string.Equals(
                normalizedStyleId,
                mediumStyle2Accent1Id,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalizedDefaultId = NormalizeTableStyleId(
                tableStyles?.Root?.Attribute("def")?.Value);
            return string.Equals(
                normalizedStyleId,
                normalizedDefaultId,
                StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    normalizedDefaultId,
                    mediumStyle2Accent1Id,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string? ReadBuiltInMediumStyle2Fill(
            IReadOnlyList<string> themeColors,
            bool isFirstRow,
            string? bandRowName)
        {
            string accent1 = themeColors.Count > 4 ? themeColors[4] : "#5b9bd5";
            if (isFirstRow)
            {
                return accent1;
            }

            if (string.Equals(accent1, "#5b9bd5", StringComparison.OrdinalIgnoreCase))
            {
                return bandRowName switch
                {
                    "band1H" => "#d2deef",
                    "band2H" => "#eaeff7",
                    _ => null
                };
            }

            return bandRowName switch
            {
                "band1H" => BlendWithWhite(accent1, .73),
                "band2H" => BlendWithWhite(accent1, .90),
                _ => null
            };
        }

        private static string BlendWithWhite(string color, double whiteAmount)
        {
            if (string.IsNullOrWhiteSpace(color) ||
                !color.StartsWith("#", StringComparison.Ordinal) ||
                color.Length != 7 ||
                !int.TryParse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int red) ||
                !int.TryParse(color.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int green) ||
                !int.TryParse(color.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int blue))
            {
                return color;
            }

            double factor = Math.Clamp(whiteAmount, 0, 1);
            red = (int)Math.Round(red + ((255 - red) * factor));
            green = (int)Math.Round(green + ((255 - green) * factor));
            blue = (int)Math.Round(blue + ((255 - blue) * factor));
            return $"#{red:X2}{green:X2}{blue:X2}";
        }

        private static string NormalizeTableStyleId(string? value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Trim('{', '}');
        }

        private static bool ReadBooleanAttribute(XElement? element, string name)
        {
            string value = element?.Attribute(name)?.Value ?? string.Empty;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static XElement? ReadTableStyleRegion(
            XElement? tableStyle,
            bool isFirstRow,
            bool isLastRow,
            string? bandRowName,
            bool isFirstColumn,
            bool isLastColumn,
            string? bandColumnName)
        {
            if (tableStyle == null)
            {
                return null;
            }

            string[] regionNames =
            {
                isFirstRow ? "firstRow" : string.Empty,
                isLastRow ? "lastRow" : string.Empty,
                isFirstColumn ? "firstCol" : string.Empty,
                isLastColumn ? "lastCol" : string.Empty,
                bandRowName ?? string.Empty,
                bandColumnName ?? string.Empty,
                "wholeTbl"
            };
            foreach (string regionName in regionNames)
            {
                if (string.IsNullOrWhiteSpace(regionName))
                {
                    continue;
                }

                XElement? region = tableStyle.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == regionName);
                if (region != null)
                {
                    return region;
                }
            }

            return null;
        }

        private static XElement? ReadFillElement(XElement? element)
        {
            if (element == null)
            {
                return null;
            }

            return element.Name.LocalName is "solidFill" or "gradFill" or "pattFill" or "blipFill" or "fillRef"
                ? element
                : element.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName is "solidFill" or "gradFill" or "pattFill" or "blipFill" or "fillRef");
        }

        private static XElement? ReadTableLine(
            XElement? cellProperties,
            XElement? styleRegion,
            XElement? wholeTableRegion,
            string lineName)
        {
            XElement? directLine = cellProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == lineName);
            if (directLine != null)
            {
                return directLine;
            }

            XElement? styleBorders = styleRegion?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "tcBdr");
            XElement? line = styleBorders?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == lineName);
            if (line != null)
            {
                return line;
            }

            XElement? wholeTableBorders = wholeTableRegion?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "tcBdr");
            return wholeTableBorders?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == lineName);
        }

        private static void AppendTableCellBorder(
            StringBuilder style,
            string side,
            XElement? line,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx)
        {
            if (line == null)
            {
                return;
            }

            string property = "border-" + side;
            if (line.Name.LocalName == "noFill" ||
                line.Descendants().Any(e => e.Name.LocalName == "noFill"))
            {
                style.Append(property).Append(":none;");
                return;
            }

            double widthPx = 1;
            if (TryReadLong(line, "w", out long width) && width > 0)
            {
                widthPx = Math.Max(.5, width / (double)Math.Max(1, slideWidth) * baseWidthPx);
            }

            string? color = ReadPresentationColor(line, themeColors);
            string borderStyle = ReadTableBorderStyle(line);
            style.Append(property)
                .Append(':')
                .Append(FormatInvariant(widthPx))
                .Append("px ")
                .Append(borderStyle)
                .Append(' ')
                .Append(string.IsNullOrWhiteSpace(color) ? "currentColor" : color)
                .Append(';');
        }

        private static string ReadTableBorderStyle(XElement line)
        {
            string value = line.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "prstDash")?
                .Attribute("val")?.Value ?? string.Empty;
            return value.ToLowerInvariant() switch
            {
                "dash" or "dashsys" or "lgdash" or "lgdashsys" or "sysdash" => "dashed",
                "dot" or "dotsys" or "lgdashdot" => "dotted",
                "dashdot" or "dashdotdot" or "lgdashdotdot" => "dashed",
                _ => "solid"
            };
        }

        private static long ReadCellInset(
            XElement properties,
            string primaryName,
            string alternateName,
            long fallback)
        {
            return TryReadLong(properties, primaryName, out long value) && value >= 0
                ? value
                : ReadInsetEmu(properties, alternateName, fallback);
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
    }
}
