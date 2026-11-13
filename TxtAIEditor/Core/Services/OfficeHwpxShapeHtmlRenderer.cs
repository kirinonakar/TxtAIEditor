using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxStyleCatalog;
using static TxtAIEditor.Core.Services.OfficeHwpxTextHtmlRenderer;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxShapeHtmlRenderer
    {
        private readonly struct HwpxPoint
        {
            public HwpxPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private readonly struct HwpxMatrix
        {
            public HwpxMatrix(double e1, double e2, double e3, double e4, double e5, double e6)
            {
                E1 = e1;
                E2 = e2;
                E3 = e3;
                E4 = e4;
                E5 = e5;
                E6 = e6;
            }

            public static HwpxMatrix Identity { get; } = new(1, 0, 0, 0, 1, 0);

            public double E1 { get; }
            public double E2 { get; }
            public double E3 { get; }
            public double E4 { get; }
            public double E5 { get; }
            public double E6 { get; }

            public HwpxPoint Transform(HwpxPoint point)
            {
                return new HwpxPoint(
                    (E1 * point.X) + (E2 * point.Y) + E3,
                    (E4 * point.X) + (E5 * point.Y) + E6);
            }

            public static HwpxMatrix Multiply(HwpxMatrix outer, HwpxMatrix inner)
            {
                return new HwpxMatrix(
                    (outer.E1 * inner.E1) + (outer.E2 * inner.E4),
                    (outer.E1 * inner.E2) + (outer.E2 * inner.E5),
                    (outer.E1 * inner.E3) + (outer.E2 * inner.E6) + outer.E3,
                    (outer.E4 * inner.E1) + (outer.E5 * inner.E4),
                    (outer.E4 * inner.E2) + (outer.E5 * inner.E5),
                    (outer.E4 * inner.E3) + (outer.E5 * inner.E6) + outer.E6);
            }
        }

        internal static bool TryAppend(
            StringBuilder builder,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            XElement container)
        {
            var shapes = container.Elements()
                .Where(element => element.Name.LocalName is "rect" or "line")
                .ToList();
            if (shapes.Count == 0 ||
                !TryReadHwpxSize(container, out double containerWidth, out double containerHeight))
            {
                return false;
            }

            var vectors = new StringBuilder();
            var textLayers = new StringBuilder();
            int renderedShapeCount = 0;
            foreach (XElement shape in shapes)
            {
                HwpxMatrix transform = ReadHwpxRenderingTransform(shape);
                XElement? lineShape = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "lineShape");
                string strokeColor = HwpxShapeStrokeColor(lineShape);
                double strokeWidth = Math.Max(40, ReadHwpxDoubleAttribute(lineShape, "width"));

                if (shape.Name.LocalName == "rect" &&
                    TryReadHwpxRectanglePoints(shape, transform, out IReadOnlyList<HwpxPoint>? points))
                {
                    ReadHwpxBounds(points, out double left, out double top, out double width, out double height);
                    AppendHwpxRectangleVector(
                        vectors,
                        left,
                        top,
                        width,
                        height,
                        strokeColor,
                        strokeWidth,
                        GetAttributeValue(lineShape, "style"));
                    renderedShapeCount++;

                    XElement? drawText = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "drawText");
                    string text = BuildHwpxShapeTextHtml(
                        drawText,
                        shape,
                        characterStyles,
                        paragraphStyles,
                        out bool usesPositionedLineLayout);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AppendHwpxShapeTextLayer(
                            textLayers,
                            shape,
                            drawText,
                            text,
                            left,
                            top,
                            width,
                            height,
                            containerWidth,
                            containerHeight,
                            usesPositionedLineLayout);
                    }
                }
                else if (shape.Name.LocalName == "line" &&
                    TryReadHwpxLinePoints(shape, transform, out HwpxPoint start, out HwpxPoint end))
                {
                    AppendHwpxLineVector(
                        vectors,
                        start,
                        end,
                        strokeColor,
                        strokeWidth,
                        GetAttributeValue(lineShape, "style"),
                        GetAttributeValue(lineShape, "headStyle"),
                        GetAttributeValue(lineShape, "tailStyle"));
                    renderedShapeCount++;
                }
            }

            if (renderedShapeCount == 0)
            {
                return false;
            }

            builder.Append("<span class=\"hwpx-group-shape\" style=\"aspect-ratio:")
                .Append(CssNumber(containerWidth))
                .Append('/')
                .Append(CssNumber(containerHeight))
                .Append("\"><svg class=\"hwpx-group-shape-vectors\" viewBox=\"0 0 ")
                .Append(CssNumber(containerWidth))
                .Append(' ')
                .Append(CssNumber(containerHeight))
                .Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">")
                .Append(vectors)
                .Append("</svg>")
                .Append(textLayers)
                .Append("</span>");
            return true;
        }

        private static string BuildHwpxShapeTextHtml(
            XElement? drawText,
            XElement shape,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            out bool usesPositionedLineLayout)
        {
            usesPositionedLineLayout = false;
            if (drawText == null)
            {
                return string.Empty;
            }

            var paragraphs = drawText.Descendants()
                .Where(element => element.Name.LocalName == "p")
                .ToList();
            ReadHwpxTextMargins(
                drawText,
                out double marginLeft,
                out double marginRight,
                out double marginTop,
                out double marginBottom);
            bool hasShapeSize = TryReadHwpxSize(shape, out double shapeWidth, out double shapeHeight);
            double textWidth = shapeWidth - marginLeft - marginRight;
            double textHeight = shapeHeight - marginTop - marginBottom;
            usesPositionedLineLayout =
                hasShapeSize &&
                textWidth > 0 &&
                textHeight > 0 &&
                paragraphs
                    .Where(paragraph => paragraph.Descendants()
                        .Any(element => element.Name.LocalName == "t" && !string.IsNullOrEmpty(element.Value)))
                    .All(paragraph => paragraph.Descendants()
                        .Count(element => element.Name.LocalName == "lineseg") == 1);

            var text = new StringBuilder();
            foreach (XElement paragraph in paragraphs)
            {
                var paragraphContent = new StringBuilder();
                IReadOnlyDictionary<XText, string> textValues = GetHwpxRenderedTextValues(paragraph);
                foreach (XNode node in paragraph.DescendantNodes())
                {
                    if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                    {
                        string value = textValues.TryGetValue(textNode, out string? renderedText)
                            ? renderedText
                            : textNode.Value;
                        AppendStyledText(
                            paragraphContent,
                            value,
                            GetHwpxTextStyle(textNode, characterStyles));
                    }
                    else if (node is XElement element && element.Name.LocalName is "lineBreak" or "br" or "cr")
                    {
                        paragraphContent.Append("<br>");
                    }
                }

                if (paragraphContent.Length == 0)
                {
                    continue;
                }

                text.Append("<span class=\"hwpx-group-shape-paragraph");
                string paragraphStyle = GetHwpxParagraphStyle(paragraph, paragraphStyles);
                if (usesPositionedLineLayout)
                {
                    text.Append(" hwpx-group-shape-line");
                    XElement lineSegment = paragraph.Descendants()
                        .First(element => element.Name.LocalName == "lineseg");
                    double left = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "horzpos") / textWidth * 100.0,
                        0.0,
                        100.0);
                    double top = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "vertpos") / textHeight * 100.0,
                        0.0,
                        100.0);
                    double width = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "horzsize") / textWidth * 100.0,
                        0.0,
                        100.0 - left);
                    double height = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "vertsize") / textHeight * 100.0,
                        0.0,
                        100.0 - top);
                    paragraphStyle = string.Join(
                        ';',
                        new[]
                        {
                            paragraphStyle,
                            "left:" + CssPercent(left),
                            "top:" + CssPercent(top),
                            "width:" + CssPercent(width),
                            "height:" + CssPercent(height),
                            "line-height:1"
                        }.Where(style => !string.IsNullOrWhiteSpace(style)));
                }

                text.Append('"');
                AppendStyleAttribute(text, paragraphStyle);
                text.Append('>')
                    .Append(paragraphContent)
                    .Append("</span>");
            }

            return text.ToString();
        }

        private static void AppendHwpxShapeTextLayer(
            StringBuilder builder,
            XElement shape,
            XElement? drawText,
            string text,
            double left,
            double top,
            double width,
            double height,
            double containerWidth,
            double containerHeight,
            bool usesPositionedLineLayout)
        {
            XElement? subList = drawText?.Descendants().FirstOrDefault(element => element.Name.LocalName == "subList");
            string verticalAlignment = GetAttributeValue(subList, "vertAlign").ToUpperInvariant();
            string justifyContent = verticalAlignment switch
            {
                "TOP" => "flex-start",
                "BOTTOM" => "flex-end",
                _ => "center"
            };
            if (TryReadHwpxSize(shape, out double shapeWidth, out double shapeHeight))
            {
                ReadHwpxTextMargins(
                    drawText,
                    out double marginLeft,
                    out double marginRight,
                    out double marginTop,
                    out double marginBottom);
                double horizontalScale = width / shapeWidth;
                double verticalScale = height / shapeHeight;
                left += marginLeft * horizontalScale;
                top += marginTop * verticalScale;
                width = Math.Max(0, width - ((marginLeft + marginRight) * horizontalScale));
                height = Math.Max(0, height - ((marginTop + marginBottom) * verticalScale));
            }

            double leftPercent = Math.Clamp(left / containerWidth * 100.0, 0.0, 100.0);
            double topPercent = Math.Clamp(top / containerHeight * 100.0, 0.0, 100.0);
            double widthPercent = Math.Clamp(width / containerWidth * 100.0, 0.0, 100.0 - leftPercent);
            double heightPercent = Math.Clamp(height / containerHeight * 100.0, 0.0, 100.0 - topPercent);

            builder.Append("<span class=\"hwpx-group-shape-text");
            if (usesPositionedLineLayout)
            {
                builder.Append(" hwpx-group-shape-text-positioned");
            }

            builder.Append("\" style=\"left:")
                .Append(CssPercent(leftPercent))
                .Append(";top:")
                .Append(CssPercent(topPercent))
                .Append(";width:")
                .Append(CssPercent(widthPercent))
                .Append(";height:")
                .Append(CssPercent(heightPercent))
                .Append(";justify-content:")
                .Append(justifyContent)
                .Append("\">")
                .Append(text)
                .Append("</span>");
        }

        private static void ReadHwpxTextMargins(
            XElement? drawText,
            out double left,
            out double right,
            out double top,
            out double bottom)
        {
            XElement? textMargin = drawText?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "textMargin");
            left = ReadHwpxDoubleAttribute(textMargin, "left");
            right = ReadHwpxDoubleAttribute(textMargin, "right");
            top = ReadHwpxDoubleAttribute(textMargin, "top");
            bottom = ReadHwpxDoubleAttribute(textMargin, "bottom");
        }

        private static void AppendHwpxRectangleVector(
            StringBuilder builder,
            double x,
            double y,
            double width,
            double height,
            string strokeColor,
            double strokeWidth,
            string lineStyle)
        {
            string normalizedStyle = lineStyle.ToUpperInvariant();
            if (normalizedStyle is "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM")
            {
                double lineWidth = Math.Max(35, strokeWidth / 3.0);
                AppendHwpxSvgRectangle(builder, x, y, width, height, strokeColor, lineWidth, string.Empty);
                double inset = Math.Max(110, strokeWidth * 0.9);
                if (width > inset * 2 && height > inset * 2)
                {
                    AppendHwpxSvgRectangle(
                        builder,
                        x + inset,
                        y + inset,
                        width - (inset * 2),
                        height - (inset * 2),
                        strokeColor,
                        lineWidth,
                        string.Empty);
                }

                return;
            }

            string dashArray = normalizedStyle is "DASH" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH"
                ? CssNumber(strokeWidth * 3.0) + ' ' + CssNumber(strokeWidth * 2.0)
                : normalizedStyle == "DOT"
                    ? CssNumber(strokeWidth) + ' ' + CssNumber(strokeWidth * 1.8)
                    : string.Empty;
            AppendHwpxSvgRectangle(builder, x, y, width, height, strokeColor, strokeWidth, dashArray);
        }

        private static void AppendHwpxSvgRectangle(
            StringBuilder builder,
            double x,
            double y,
            double width,
            double height,
            string strokeColor,
            double strokeWidth,
            string dashArray)
        {
            builder.Append("<rect x=\"")
                .Append(CssNumber(x))
                .Append("\" y=\"")
                .Append(CssNumber(y))
                .Append("\" width=\"")
                .Append(CssNumber(width))
                .Append("\" height=\"")
                .Append(CssNumber(height))
                .Append("\" fill=\"none\" stroke=\"")
                .Append(strokeColor)
                .Append("\" stroke-width=\"")
                .Append(CssNumber(strokeWidth))
                .Append('"');
            if (!string.IsNullOrWhiteSpace(dashArray))
            {
                builder.Append(" stroke-dasharray=\"").Append(dashArray).Append('"');
            }

            builder.Append(" />");
        }

        private static void AppendHwpxLineVector(
            StringBuilder builder,
            HwpxPoint start,
            HwpxPoint end,
            string strokeColor,
            double strokeWidth,
            string lineStyle,
            string headStyle,
            string tailStyle)
        {
            builder.Append("<line x1=\"")
                .Append(CssNumber(start.X))
                .Append("\" y1=\"")
                .Append(CssNumber(start.Y))
                .Append("\" x2=\"")
                .Append(CssNumber(end.X))
                .Append("\" y2=\"")
                .Append(CssNumber(end.Y))
                .Append("\" stroke=\"")
                .Append(strokeColor)
                .Append("\" stroke-width=\"")
                .Append(CssNumber(strokeWidth))
                .Append("\" stroke-linecap=\"square\"");
            string normalizedStyle = lineStyle.ToUpperInvariant();
            if (normalizedStyle is "DASH" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH")
            {
                builder.Append(" stroke-dasharray=\"")
                    .Append(CssNumber(strokeWidth * 3.0))
                    .Append(' ')
                    .Append(CssNumber(strokeWidth * 2.0))
                    .Append('"');
            }

            builder.Append(" />");
            if (!tailStyle.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) &&
                !tailStyle.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                AppendHwpxArrowHead(builder, start, end, strokeColor, strokeWidth);
            }

            if (!headStyle.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) &&
                !headStyle.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                AppendHwpxArrowHead(builder, end, start, strokeColor, strokeWidth);
            }
        }

        private static void AppendHwpxArrowHead(
            StringBuilder builder,
            HwpxPoint start,
            HwpxPoint end,
            string fillColor,
            double strokeWidth)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0)
            {
                return;
            }

            double unitX = dx / length;
            double unitY = dy / length;
            double arrowLength = Math.Max(700, strokeWidth * 7.0);
            double halfWidth = Math.Max(320, strokeWidth * 3.2);
            var baseCenter = new HwpxPoint(end.X - (unitX * arrowLength), end.Y - (unitY * arrowLength));
            var upper = new HwpxPoint(baseCenter.X - (unitY * halfWidth), baseCenter.Y + (unitX * halfWidth));
            var lower = new HwpxPoint(baseCenter.X + (unitY * halfWidth), baseCenter.Y - (unitX * halfWidth));
            var notch = new HwpxPoint(
                end.X - (unitX * arrowLength * 0.64),
                end.Y - (unitY * arrowLength * 0.64));

            builder.Append("<polygon points=\"")
                .Append(CssNumber(end.X)).Append(',').Append(CssNumber(end.Y)).Append(' ')
                .Append(CssNumber(upper.X)).Append(',').Append(CssNumber(upper.Y)).Append(' ')
                .Append(CssNumber(notch.X)).Append(',').Append(CssNumber(notch.Y)).Append(' ')
                .Append(CssNumber(lower.X)).Append(',').Append(CssNumber(lower.Y))
                .Append("\" fill=\"")
                .Append(fillColor)
                .Append("\" />");
        }

        private static bool TryReadHwpxRectanglePoints(
            XElement rectangle,
            HwpxMatrix transform,
            out IReadOnlyList<HwpxPoint> points)
        {
            var transformedPoints = new List<HwpxPoint>();
            foreach (string pointName in new[] { "pt0", "pt1", "pt2", "pt3" })
            {
                XElement? point = rectangle.Elements().FirstOrDefault(element => element.Name.LocalName == pointName);
                if (!TryReadHwpxPoint(point, out HwpxPoint value))
                {
                    points = Array.Empty<HwpxPoint>();
                    return false;
                }

                transformedPoints.Add(transform.Transform(value));
            }

            points = transformedPoints;
            return true;
        }

        private static bool TryReadHwpxLinePoints(
            XElement line,
            HwpxMatrix transform,
            out HwpxPoint start,
            out HwpxPoint end)
        {
            XElement? startPoint = line.Elements().FirstOrDefault(element => element.Name.LocalName == "startPt");
            XElement? endPoint = line.Elements().FirstOrDefault(element => element.Name.LocalName == "endPt");
            if (!TryReadHwpxPoint(startPoint, out HwpxPoint sourceStart) ||
                !TryReadHwpxPoint(endPoint, out HwpxPoint sourceEnd))
            {
                start = default;
                end = default;
                return false;
            }

            start = transform.Transform(sourceStart);
            end = transform.Transform(sourceEnd);
            if (GetAttributeValue(line, "isReverseHV") == "1")
            {
                (start, end) = (end, start);
            }

            return true;
        }

        private static bool TryReadHwpxPoint(XElement? element, out HwpxPoint point)
        {
            bool hasX = double.TryParse(
                GetAttributeValue(element, "x"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double x);
            bool hasY = double.TryParse(
                GetAttributeValue(element, "y"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double y);
            point = new HwpxPoint(x, y);
            return hasX && hasY;
        }

        private static HwpxMatrix ReadHwpxRenderingTransform(XElement shape)
        {
            XElement? renderingInfo = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "renderingInfo");
            HwpxMatrix transform = HwpxMatrix.Identity;
            if (renderingInfo == null)
            {
                return transform;
            }

            foreach (XElement matrixElement in renderingInfo.Elements().Where(element =>
                element.Name.LocalName is "transMatrix" or "scaMatrix" or "rotMatrix"))
            {
                var matrix = new HwpxMatrix(
                    ReadHwpxDoubleAttribute(matrixElement, "e1"),
                    ReadHwpxDoubleAttribute(matrixElement, "e2"),
                    ReadHwpxDoubleAttribute(matrixElement, "e3"),
                    ReadHwpxDoubleAttribute(matrixElement, "e4"),
                    ReadHwpxDoubleAttribute(matrixElement, "e5"),
                    ReadHwpxDoubleAttribute(matrixElement, "e6"));
                transform = HwpxMatrix.Multiply(transform, matrix);
            }

            return transform;
        }

        private static void ReadHwpxBounds(
            IReadOnlyList<HwpxPoint> points,
            out double left,
            out double top,
            out double width,
            out double height)
        {
            left = points.Min(point => point.X);
            top = points.Min(point => point.Y);
            width = points.Max(point => point.X) - left;
            height = points.Max(point => point.Y) - top;
        }

        private static string HwpxShapeStrokeColor(XElement? lineShape)
        {
            string color = GetAttributeValue(lineShape, "color");
            if (color.Equals("#000000", StringComparison.OrdinalIgnoreCase) ||
                !IsCssColor(color))
            {
                return "currentColor";
            }

            return color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)
                ? "transparent"
                : Html(color);
        }
    }
}
