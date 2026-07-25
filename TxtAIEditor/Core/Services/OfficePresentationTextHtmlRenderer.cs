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
            double baseWidthPx)
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
            long totalHeight = Math.Max(
                1,
                rows.Sum(row => Math.Max(0, ReadTableRowHeight(row))));
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
                        .Append(BuildTableCellTextHtml(
                            cell,
                            themeColors,
                            slideWidth,
                            baseWidthPx))
                        .Append("</td>");
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
            return "padding:" + Pixels(top, slideHeight, baseHeightPx) + " " +
                Pixels(right, slideWidth, baseWidthPx) + " " +
                Pixels(bottom, slideHeight, baseHeightPx) + " " +
                Pixels(left, slideWidth, baseWidthPx) + ";";
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
                "ctr" => "center",
                "r" => "right",
                "just" or "dist" => "justify",
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
            IReadOnlyList<string> themeColors)
        {
            XElement? properties = cell.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tcPr");
            if (properties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            string? fill = ReadPresentationColor(
                properties.Elements().FirstOrDefault(e => e.Name.LocalName == "solidFill"),
                themeColors);
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
