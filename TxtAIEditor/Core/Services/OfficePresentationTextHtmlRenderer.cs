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
        private sealed class PresentationBulletMarker
        {
            public string Text { get; init; } = "•";
            public string? Typeface { get; init; }
            public string? Color { get; init; }
            public double SizeScale { get; init; } = 1.0;
            public bool IsTriangle { get; init; }
        }

        public static string BuildShapeTextHtml(
            XElement shape,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            XElement? inheritedBodyStyle = null,
            XElement? inheritedTitleStyle = null)
        {
            var paragraphs = new StringBuilder();
            XElement? textBody = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (textBody == null)
            {
                return string.Empty;
            }

            XElement? effectiveTextStyle = ShouldUseInheritedTitleStyle(shape)
                ? inheritedTitleStyle
                : ShouldUseInheritedBodyStyle(shape)
                    ? inheritedBodyStyle
                    : null;
            double fontScale = ReadNormAutofitScale(textBody);
            double fallbackFontSizePx =
                PointsToPixels(ReadFallbackFontPoint(shape), slideWidth, baseWidthPx) * fontScale;

            List<XElement> paragraphElements = textBody.Elements()
                .Where(e => e.Name.LocalName == "p")
                .ToList();
            int paragraphIndex = 0;
            foreach (XElement paragraph in paragraphElements)
            {
                paragraphIndex++;
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (paragraphIndex < paragraphElements.Count)
                    {
                        string emptyParagraphStyle = ReadParagraphStyle(
                            paragraph,
                            textBody,
                            effectiveTextStyle,
                            slideWidth,
                            baseWidthPx,
                            fontScale) +
                            ReadParagraphDefaultRunStyle(
                                paragraph,
                                textBody,
                                themeColors,
                                slideWidth,
                                baseWidthPx,
                                fontScale,
                                fallbackFontSizePx,
                                effectiveTextStyle) +
                            ReadExplicitParagraphFontSizeStyle(
                                paragraph,
                                textBody,
                                effectiveTextStyle,
                                slideWidth,
                                baseWidthPx,
                                fontScale);
                        AppendEmptyParagraphHtml(paragraphs, emptyParagraphStyle);
                    }

                    continue;
                }

                PresentationBulletMarker? bullet = ReadParagraphBullet(
                    paragraph,
                    textBody,
                    effectiveTextStyle,
                    themeColors,
                    paragraphIndex);
                string paragraphStyle = ReadParagraphStyle(
                    paragraph,
                    textBody,
                    effectiveTextStyle,
                    slideWidth,
                    baseWidthPx,
                    fontScale);
                paragraphStyle += ReadParagraphDefaultRunStyle(
                    paragraph,
                    textBody,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale,
                    fallbackFontSizePx,
                    effectiveTextStyle);
                paragraphStyle += ReadExplicitParagraphFontSizeStyle(
                    paragraph,
                    textBody,
                    effectiveTextStyle,
                    slideWidth,
                    baseWidthPx,
                    fontScale);
                paragraphs.Append("<p");
                if (!string.IsNullOrWhiteSpace(paragraphStyle))
                {
                    paragraphs.Append(" style=\"").Append(Html(paragraphStyle)).Append('"');
                }

                paragraphs.Append('>');
                if (bullet != null)
                {
                    AppendBulletHtml(paragraphs, bullet);
                }

                paragraphs.Append(BuildParagraphRunsHtml(
                    paragraph,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale,
                    textBody,
                    effectiveTextStyle));
                paragraphs.Append("</p>");
            }

            return paragraphs.ToString();
        }

        private static void AppendEmptyParagraphHtml(
            StringBuilder builder,
            string paragraphStyle)
        {
            builder.Append("<p");
            if (!string.IsNullOrWhiteSpace(paragraphStyle))
            {
                builder.Append(" style=\"")
                    .Append(Html(paragraphStyle))
                    .Append('"');
            }

            builder.Append("><br></p>");
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
            XElement? textBody = null,
            XElement? inheritedBodyStyle = null)
        {
            var builder = new StringBuilder();
            XElement? defaultRunProperties =
                ReadParagraphDefaultRunProperties(
                    paragraph,
                    textBody,
                    inheritedBodyStyle);
            XElement? inheritedDefaultRunProperties =
                ReadParagraphLevelDefaultRunProperties(
                    paragraph,
                    textBody,
                    inheritedBodyStyle);
            string defaultRunStyle = ReadRunTextStyle(
                    inheritedDefaultRunProperties,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale) +
                ReadRunTextStyle(
                    defaultRunProperties,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale);
            foreach (XElement element in paragraph.Elements())
            {
                if (element.Name.LocalName == "r" || element.Name.LocalName == "fld")
                {
                    XElement? runProperties = element.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "rPr");
                    string runStyle = defaultRunStyle + ReadRunTextStyle(
                        runProperties,
                        themeColors,
                        slideWidth,
                        baseWidthPx,
                        fontScale);
                    string text = string.Concat(
                        element.Descendants()
                            .Where(e => e.Name.LocalName == "t")
                            .Select(e => e.Value));
                    text = NormalizePresentationText(text, runProperties);
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
                    continue;
                }

                if (element.Descendants().Any(e => e.Name.LocalName == "oMath"))
                {
                    string mathHtml = BuildMathHtml(element);
                    if (!string.IsNullOrEmpty(mathHtml))
                    {
                        builder.Append(mathHtml);
                    }
                }
            }

            if (builder.Length == 0)
            {
                builder.Append(Html(NormalizePresentationText(
                    ReadParagraphText(paragraph),
                    null)));
            }

            return builder.ToString();
        }

        private static string BuildMathHtml(XElement mathElement)
        {
            XElement? mathRoot = mathElement.Name.LocalName == "oMath"
                ? mathElement
                : mathElement.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "oMath");
            if (mathRoot == null)
            {
                return string.Empty;
            }

            string content = BuildMathNodesHtml(mathRoot.Elements());
            return string.IsNullOrEmpty(content)
                ? string.Empty
                : "<span class=\"ppt-math\">" + content + "</span>";
        }

        private static string BuildMathNodesHtml(IEnumerable<XElement> elements)
        {
            var builder = new StringBuilder();
            foreach (XElement element in elements)
            {
                switch (element.Name.LocalName)
                {
                    case "t":
                        builder.Append(Html(NormalizePresentationText(element.Value, null)));
                        break;

                    case "r":
                        builder.Append(BuildMathNodesHtml(
                            element.Elements().Where(child => child.Name.LocalName == "t")));
                        break;

                    case "sSup":
                        AppendMathSuperscriptHtml(builder, element);
                        break;

                    case "sSub":
                        AppendMathSubscriptHtml(builder, element);
                        break;

                    case "sSubSup":
                        AppendMathSubSuperscriptHtml(builder, element);
                        break;

                    case "f":
                        AppendMathFractionHtml(builder, element);
                        break;

                    default:
                        builder.Append(BuildMathNodesHtml(element.Elements()));
                        break;
                }
            }

            return builder.ToString();
        }

        private static void AppendMathSuperscriptHtml(
            StringBuilder builder,
            XElement structure)
        {
            XElement? baseElement = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "e");
            XElement? superscript = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "sup");
            builder.Append("<span class=\"ppt-math-sup\"><span class=\"ppt-math-base\">")
                .Append(BuildMathNodesHtml(baseElement?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span><span class=\"ppt-math-exponent\">")
                .Append(BuildMathNodesHtml(superscript?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span></span>");
        }

        private static void AppendMathSubscriptHtml(
            StringBuilder builder,
            XElement structure)
        {
            XElement? baseElement = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "e");
            XElement? subscript = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "sub");
            builder.Append("<span class=\"ppt-math-sub\"><span class=\"ppt-math-base\">")
                .Append(BuildMathNodesHtml(baseElement?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span><span class=\"ppt-math-subscript\">")
                .Append(BuildMathNodesHtml(subscript?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span></span>");
        }

        private static void AppendMathSubSuperscriptHtml(
            StringBuilder builder,
            XElement structure)
        {
            XElement? baseElement = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "e");
            XElement? subscript = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "sub");
            XElement? superscript = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "sup");
            builder.Append("<span class=\"ppt-math-subsup\"><span class=\"ppt-math-base\">")
                .Append(BuildMathNodesHtml(baseElement?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span><span class=\"ppt-math-subscript\">")
                .Append(BuildMathNodesHtml(subscript?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span><span class=\"ppt-math-exponent\">")
                .Append(BuildMathNodesHtml(superscript?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span></span>");
        }

        private static void AppendMathFractionHtml(
            StringBuilder builder,
            XElement structure)
        {
            XElement? numerator = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "num");
            XElement? denominator = structure.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "den");
            builder.Append("<span class=\"ppt-math-fraction\"><span class=\"ppt-math-numerator\">")
                .Append(BuildMathNodesHtml(numerator?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span><span class=\"ppt-math-denominator\">")
                .Append(BuildMathNodesHtml(denominator?.Elements() ?? Enumerable.Empty<XElement>()))
                .Append("</span></span>");
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
            int paragraphIndex = 0;
            foreach (XElement paragraph in cell.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                paragraphIndex++;
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                PresentationBulletMarker? bullet = ReadParagraphBullet(
                    paragraph,
                    textBody,
                    null,
                    themeColors,
                    paragraphIndex);
                string paragraphStyle = ReadParagraphStyle(
                    paragraph,
                    textBody,
                    null,
                    slideWidth,
                    baseWidthPx,
                    fontScale);
                builder.Append("<p");
                if (!string.IsNullOrWhiteSpace(paragraphStyle))
                {
                    builder.Append(" style=\"").Append(Html(paragraphStyle)).Append('"');
                }

                builder.Append('>');
                if (bullet != null)
                {
                    AppendBulletHtml(builder, bullet);
                }

                builder
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

        private static PresentationBulletMarker? ReadParagraphBullet(
            XElement paragraph,
            XElement? textBody,
            XElement? inheritedBodyStyle,
            IReadOnlyList<string> themeColors,
            int paragraphIndex)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            XElement? levelProperties = ReadParagraphLevelProperties(
                textBody,
                inheritedBodyStyle,
                ReadParagraphLevel(paragraph));
            XElement? bullet = paragraphProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName is
                    "buNone" or "buChar" or "buAutoNum");
            if (bullet?.Name.LocalName == "buNone")
            {
                return null;
            }

            bullet ??= ReadParagraphLevelProperties(
                    textBody,
                    inheritedBodyStyle,
                    ReadParagraphLevel(paragraph))
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName is "buChar" or "buAutoNum");
            if (bullet == null)
            {
                return null;
            }

            if (bullet.Name.LocalName == "buChar")
            {
                string marker = bullet.Attribute("char")?.Value ?? "•";
                string? typeface = ReadBulletTypeface(bullet, levelProperties);
                bool isTriangle = marker.Any(char.IsControl) &&
                    !string.IsNullOrWhiteSpace(typeface) &&
                    typeface.Contains("Wingdings 3", StringComparison.OrdinalIgnoreCase);
                if (marker.Any(char.IsControl) && !isTriangle)
                {
                    marker = "•";
                }

                return new PresentationBulletMarker
                {
                    Text = string.IsNullOrEmpty(marker) ? "•" : marker,
                    Typeface = typeface,
                    Color = ReadBulletColor(levelProperties, bullet, themeColors),
                    SizeScale = ReadBulletSizeScale(levelProperties),
                    IsTriangle = isTriangle
                };
            }

            int startAt = 1;
            if (int.TryParse(
                    bullet.Attribute("startAt")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int readStartAt))
            {
                startAt = readStartAt;
            }

            return new PresentationBulletMarker
            {
                Text = (startAt + Math.Max(0, paragraphIndex - 1))
                    .ToString(CultureInfo.InvariantCulture) + "."
            };
        }

        private static string? ReadBulletTypeface(
            XElement bullet,
            XElement? levelProperties)
        {
            return levelProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "buFont")
                ?.Attribute("typeface")?.Value ??
                bullet.Parent?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "buFont")
                    ?.Attribute("typeface")?.Value;
        }

        private static string? ReadBulletColor(
            XElement? levelProperties,
            XElement bullet,
            IReadOnlyList<string> themeColors)
        {
            XElement? levelColor = levelProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "buClr");
            string? color = ReadPresentationColor(levelColor, themeColors);
            if (!string.IsNullOrWhiteSpace(color))
            {
                return color;
            }

            XElement? directColor = bullet.Parent?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "buClr");
            return ReadPresentationColor(directColor, themeColors);
        }

        private static double ReadBulletSizeScale(XElement? levelProperties)
        {
            XElement? size = levelProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "buSzPct");
            return size != null &&
                TryReadLong(size, "val", out long value) &&
                value > 0
                ? Math.Clamp(value / 100000.0, 0.2, 3.0)
                : 1.0;
        }

        private static void AppendBulletHtml(
            StringBuilder builder,
            PresentationBulletMarker bullet)
        {
            var style = new StringBuilder();
            if (bullet.IsTriangle)
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(bullet.SizeScale * 100.0))
                    .Append("%;");
            }

            builder.Append("<span class=\"ppt-bullet\"");
            if (!string.IsNullOrWhiteSpace(bullet.Typeface))
            {
                style.Append("font-family:'")
                    .Append(bullet.Typeface.Replace("'", "\\'", StringComparison.Ordinal))
                    .Append("';");
            }

            if (!string.IsNullOrWhiteSpace(bullet.Color))
            {
                style.Append("color:")
                    .Append(bullet.Color)
                    .Append(';');
            }

            if (style.Length > 0)
            {
                builder.Append(" style=\"")
                    .Append(Html(style.ToString()))
                    .Append('"');
            }

            builder.Append('>');
            if (bullet.IsTriangle)
            {
                builder.Append("<span class=\"ppt-bullet-triangle\"></span>");
            }
            else
            {
                builder.Append(EncodeBulletText(bullet.Text));
            }

            builder.Append(" </span>");
        }

        private static string EncodeBulletText(string text)
        {
            var builder = new StringBuilder();
            foreach (char character in text)
            {
                if (char.IsControl(character))
                {
                    builder.Append("&#x")
                        .Append(((int)character).ToString("X", CultureInfo.InvariantCulture))
                        .Append(';');
                }
                else
                {
                    builder.Append(Html(character.ToString()));
                }
            }

            return builder.ToString();
        }

        private static string NormalizePresentationText(
            string text,
            XElement? runProperties)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            bool usesWingdingsSymbol = runProperties?.Descendants()
                .Any(element =>
                    element.Name.LocalName == "sym" &&
                    (element.Attribute("typeface")?.Value ?? string.Empty)
                        .Contains("Wingdings", StringComparison.OrdinalIgnoreCase)) == true;
            if (!usesWingdingsSymbol && !text.Contains('\uF0E0'))
            {
                return text;
            }

            return text.Replace('\uF0E0', '→');
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
            XElement? textBody,
            XElement? inheritedBodyStyle = null)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            XElement? explicitRunProperties = paragraphProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr") ??
                null;
            if (HasRunVisualProperties(explicitRunProperties))
            {
                return explicitRunProperties;
            }

            XElement? endParagraphRunProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "endParaRPr");
            if (HasRunVisualProperties(endParagraphRunProperties))
            {
                return endParagraphRunProperties;
            }

            return ReadParagraphLevelProperties(
                    textBody,
                    inheritedBodyStyle,
                    ReadParagraphLevel(paragraph))
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr");
        }

        private static XElement? ReadParagraphLevelDefaultRunProperties(
            XElement paragraph,
            XElement? textBody,
            XElement? inheritedBodyStyle = null)
        {
            return ReadParagraphLevelProperties(
                    textBody,
                    inheritedBodyStyle,
                    ReadParagraphLevel(paragraph))
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr");
        }

        private static bool HasRunVisualProperties(XElement? runProperties)
        {
            if (runProperties == null)
            {
                return false;
            }

            return runProperties.Attributes().Any(attribute =>
                    attribute.Name.LocalName is not "lang" and
                    not "altLang" and
                    not "smtClean") ||
                runProperties.Descendants().Any();
        }

        private static int ReadParagraphLevel(XElement paragraph)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            return paragraphProperties != null &&
                int.TryParse(
                    paragraphProperties.Attribute("lvl")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int level) &&
                level >= 0
                    ? Math.Min(8, level)
                    : 0;
        }

        private static XElement? ReadParagraphLevelProperties(
            XElement? textBody,
            XElement? inheritedBodyStyle,
            int level)
        {
            XElement? listStyle = textBody?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "lstStyle");
            XElement? localProperties = listStyle?.Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName ==
                    "lvl" + (Math.Clamp(level, 0, 8) + 1)
                        .ToString(CultureInfo.InvariantCulture) +
                    "pPr");
            return localProperties ?? inheritedBodyStyle?.Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName ==
                    "lvl" + (Math.Clamp(level, 0, 8) + 1)
                        .ToString(CultureInfo.InvariantCulture) +
                    "pPr");
        }

        private static string ReadParagraphDefaultRunStyle(
            XElement paragraph,
            XElement? textBody,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            double fontScale,
            double fallbackFontSizePx,
            XElement? inheritedBodyStyle = null)
        {
            XElement? defaultRunProperties =
                ReadParagraphDefaultRunProperties(
                    paragraph,
                    textBody,
                    inheritedBodyStyle);
            XElement? inheritedDefaultRunProperties =
                ReadParagraphLevelDefaultRunProperties(
                    paragraph,
                    textBody,
                    inheritedBodyStyle);
            string style = ReadRunTextStyle(
                    inheritedDefaultRunProperties,
                    themeColors,
                    slideWidth,
                    baseWidthPx,
                    fontScale) +
                ReadRunTextStyle(
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

        private static string ReadExplicitParagraphFontSizeStyle(
            XElement paragraph,
            XElement? textBody,
            XElement? inheritedBodyStyle,
            long slideWidth,
            double baseWidthPx,
            double fontScale)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            IEnumerable<XElement> runProperties = paragraph.Elements()
                .Where(e => e.Name.LocalName is "r" or "fld")
                .SelectMany(e => e.Elements())
                .Where(e => e.Name.LocalName == "rPr");
            List<int> sizes = runProperties
                .Select(properties => properties.Attribute("sz")?.Value)
                .Where(value => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _))
                .Select(value => int.Parse(
                    value!,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture))
                .Where(size => size > 0)
                .ToList();

            XElement? endParagraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "endParaRPr");
            if (endParagraphProperties?.Attribute("sz")?.Value is string endSizeValue &&
                int.TryParse(
                    endSizeValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int endSize) &&
                endSize > 0)
            {
                sizes.Add(endSize);
            }

            if (sizes.Count == 0)
            {
                return string.Empty;
            }

            double points = sizes.Max() / 100.0;
            var style = new StringBuilder("font-size:");
            style.Append(
                FormatInvariant(
                    PointsToPixels(points, slideWidth, baseWidthPx) * fontScale) +
                "px;");

            XElement? levelProperties = ReadParagraphLevelProperties(
                textBody,
                inheritedBodyStyle,
                ReadParagraphLevel(paragraph));
            XElement? inheritedDefaultRunProperties = levelProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "defRPr");
            if (inheritedDefaultRunProperties?.Attribute("sz")?.Value is string inheritedSizeValue &&
                int.TryParse(
                    inheritedSizeValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int inheritedSize) &&
                inheritedSize > sizes.Max() &&
                paragraphProperties?.Elements().Any(e => e.Name.LocalName == "lnSpc") != true &&
                levelProperties?.Elements().Any(e => e.Name.LocalName == "lnSpc") != true)
            {
                style.Append("line-height:1.2;");
            }

            return style.ToString();
        }

        private static bool ShouldUseInheritedBodyStyle(XElement shape)
        {
            XElement? placeholder = shape.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ph");
            if (placeholder == null)
            {
                return false;
            }

            string? type = placeholder.Attribute("type")?.Value;
            return string.IsNullOrWhiteSpace(type) ||
                string.Equals(type, "body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "text", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUseInheritedTitleStyle(XElement shape)
        {
            XElement? placeholder = shape.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ph");
            if (placeholder == null)
            {
                return false;
            }

            string? type = placeholder.Attribute("type")?.Value;
            return string.Equals(type, "title", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "ctrTitle", StringComparison.OrdinalIgnoreCase);
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
            XElement? textBody,
            XElement? inheritedBodyStyle,
            long slideWidth,
            double baseWidthPx,
            double fontScale = 1.0)
        {
            XElement? paragraphProperties = paragraph.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pPr");
            XElement? levelProperties = ReadParagraphLevelProperties(
                textBody,
                inheritedBodyStyle,
                ReadParagraphLevel(paragraph));

            var style = new StringBuilder();
            string align = paragraphProperties?.Attribute("algn")?.Value ??
                levelProperties?.Attribute("algn")?.Value ??
                string.Empty;
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

            long marginLeft = 0;
            bool hasMarginLeft = paragraphProperties != null &&
                TryReadLong(paragraphProperties, "marL", out marginLeft);
            if (!hasMarginLeft && levelProperties != null)
            {
                hasMarginLeft = TryReadLong(levelProperties, "marL", out marginLeft);
            }

            if (hasMarginLeft &&
                marginLeft > 0)
            {
                style.Append("padding-left:")
                    .Append(FormatInvariant(
                        PointsToPixels(marginLeft / 12700.0, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            long indent = 0;
            bool hasIndent = paragraphProperties != null &&
                TryReadLong(paragraphProperties, "indent", out indent);
            if (!hasIndent && levelProperties != null)
            {
                hasIndent = TryReadLong(levelProperties, "indent", out indent);
            }

            if (hasIndent &&
                indent != 0)
            {
                style.Append("text-indent:")
                    .Append(FormatInvariant(
                        PointsToPixels(indent / 12700.0, slideWidth, baseWidthPx)))
                    .Append("px;");
            }

            AppendParagraphSpacing(
                style,
                paragraphProperties,
                levelProperties,
                "spcBef",
                "margin-top",
                slideWidth,
                baseWidthPx,
                fontScale);
            AppendParagraphSpacing(
                style,
                paragraphProperties,
                levelProperties,
                "spcAft",
                "margin-bottom",
                slideWidth,
                baseWidthPx,
                fontScale);
            AppendParagraphLineSpacing(
                style,
                paragraphProperties,
                levelProperties,
                slideWidth,
                baseWidthPx,
                fontScale);

            return style.ToString();
        }

        private static void AppendParagraphSpacing(
            StringBuilder style,
            XElement? paragraphProperties,
            XElement? levelProperties,
            string spacingName,
            string cssProperty,
            long slideWidth,
            double baseWidthPx,
            double fontScale)
        {
            XElement? spacing = paragraphProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == spacingName) ??
                levelProperties?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == spacingName);
            if (spacing == null)
            {
                return;
            }

            XElement? percentage = spacing.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spcPct");
            if (percentage != null &&
                TryReadLong(percentage, "val", out long percentageValue))
            {
                style.Append(cssProperty)
                    .Append(':')
                    .Append(FormatInvariant(percentageValue / 100000.0))
                    .Append("em;");
                return;
            }

            XElement? points = spacing.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spcPts");
            if (points != null &&
                TryReadLong(points, "val", out long pointValue))
            {
                double pointSpacing = pointValue / 100.0 * fontScale;
                style.Append(cssProperty)
                    .Append(':')
                    .Append(FormatInvariant(
                        PointsToPixels(pointSpacing, slideWidth, baseWidthPx)))
                    .Append("px;");
            }
        }

        private static void AppendParagraphLineSpacing(
            StringBuilder style,
            XElement? paragraphProperties,
            XElement? levelProperties,
            long slideWidth,
            double baseWidthPx,
            double fontScale)
        {
            XElement? lineSpacing = paragraphProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "lnSpc") ??
                levelProperties?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "lnSpc");
            if (lineSpacing == null)
            {
                return;
            }

            XElement? percentage = lineSpacing.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spcPct");
            if (percentage != null &&
                TryReadLong(percentage, "val", out long percentageValue))
            {
                style.Append("line-height:")
                    .Append(FormatInvariant(percentageValue / 100000.0))
                    .Append(';');
                return;
            }

            XElement? points = lineSpacing.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spcPts");
            if (points != null &&
                TryReadLong(points, "val", out long pointValue))
            {
                double pointSpacing = pointValue / 100.0 * fontScale;
                style.Append("line-height:")
                    .Append(FormatInvariant(
                        PointsToPixels(pointSpacing, slideWidth, baseWidthPx)))
                    .Append("px;");
            }
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

            if (runProperties?.Attribute("baseline")?.Value is string baselineValue &&
                long.TryParse(
                    baselineValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long baseline) &&
                baseline != 0)
            {
                style.Append("font-size:70%;vertical-align:")
                    .Append(FormatInvariant(baseline / 1000.0))
                    .Append("%;");
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
            AppendTableCellBaseFontStyle(
                style,
                cell,
                slideWidth,
                baseWidthPx);
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

        private static void AppendTableCellBaseFontStyle(
            StringBuilder style,
            XElement cell,
            long slideWidth,
            double baseWidthPx)
        {
            XElement? textBody = cell.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (textBody == null)
            {
                return;
            }

            XElement? paragraph = textBody.Descendants()
                .Where(e => e.Name.LocalName == "p")
                .FirstOrDefault(e => e.Descendants().Any(child =>
                    child.Name.LocalName is "t" or "br" or "cr"));
            XElement? runProperties = paragraph?
                .Elements()
                .Where(e => e.Name.LocalName is "r" or "fld")
                .SelectMany(e => e.Elements())
                .FirstOrDefault(e => e.Name.LocalName == "rPr");
            if (paragraph != null)
            {
                runProperties ??= ReadParagraphDefaultRunProperties(paragraph, textBody);
            }
            runProperties ??= textBody.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "rPr");
            if (runProperties == null)
            {
                return;
            }

            double fontScale = ReadNormAutofitScale(textBody);
            if (int.TryParse(
                    runProperties.Attribute("sz")?.Value,
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

            string? typeface = runProperties.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "latin")?
                .Attribute("typeface")?.Value;
            if (!string.IsNullOrWhiteSpace(typeface) &&
                !typeface.Contains('+', StringComparison.Ordinal))
            {
                style.Append("font-family:'")
                    .Append(typeface.Replace("'", "\\'", StringComparison.Ordinal))
                    .Append("','Segoe UI',Arial,sans-serif;");
            }
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
