using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficePresentationRenderingUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationSlideHtmlRenderer
    {
        private sealed class PresentationPlaceholderBounds
        {
            public string? Type { get; init; }
            public string? Index { get; init; }
            public string BoundsStyle { get; init; } = string.Empty;
        }

        private sealed class PresentationInheritedPart
        {
            public XDocument Document { get; init; } = null!;
            public IReadOnlyDictionary<string, string> Relationships { get; init; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        public static async Task<string?> BuildAsync(
            ZipArchive archive,
            string slidePath,
            int slideNumber,
            int slideCount,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            IReadOnlyList<string> themeColors,
            XDocument? tableStyles)
        {
            ZipArchiveEntry? slideEntry = archive.GetEntry(slidePath);
            if (slideEntry == null)
            {
                return null;
            }

            XDocument slide = await OfficePresentationPackageReader
                .LoadXmlEntryAsync(slideEntry)
                .ConfigureAwait(false);
            IReadOnlyDictionary<string, string> relationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(slidePath),
                    Path.GetDirectoryName(slidePath)?.Replace('\\', '/') ?? string.Empty)
                .ConfigureAwait(false);
            IReadOnlyList<string> slideThemeColors =
                await OfficePresentationPackageReader.LoadThemeColorsForSlideAsync(
                    archive,
                    relationships,
                    slide)
                    .ConfigureAwait(false);
            if (slideThemeColors.Count > 0)
            {
                themeColors = slideThemeColors;
            }

            IReadOnlyList<PresentationInheritedPart> inheritedParts =
                await LoadInheritedSlidePartsAsync(
                    archive,
                    relationships)
                    .ConfigureAwait(false);
            XElement? inheritedBodyStyle = inheritedParts
                .Select(part => part.Document.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "bodyStyle"))
                .FirstOrDefault(style => style != null);
            XElement? inheritedTitleStyle = inheritedParts
                .Select(part => part.Document.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "titleStyle"))
                .FirstOrDefault(style => style != null);

            double baseHeightPx =
                baseWidthPx * slideHeight / Math.Max(1.0, slideWidth);
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds =
                await LoadSlidePlaceholderBoundsAsync(
                    archive,
                    relationships,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx)
                .ConfigureAwait(false);
            string background = await ReadSlideBackgroundAsync(
                    archive,
                    slide,
                    relationships,
                    themeColors)
                .ConfigureAwait(false) ?? "#ffffff";

            var html = new StringBuilder();
            html.Append("<section class=\"slide\" style=\"--slide-ratio:")
                .Append(FormatInvariant(slideWidth / (double)Math.Max(1, slideHeight)))
                .Append(";--base-width:")
                .Append(FormatInvariant(baseWidthPx))
                .Append(";--base-width-px:")
                .Append(FormatInvariant(baseWidthPx))
                .Append("px;--base-height-px:")
                .Append(FormatInvariant(baseHeightPx))
                .Append("px;background:")
                .Append(Html(background))
                .Append("\"><div class=\"slide-canvas\"><div class=\"slide-number\">")
                .Append(slideNumber)
                .Append(" / ")
                .Append(slideCount)
                .Append("</div>");

            foreach (PresentationInheritedPart inheritedPart in inheritedParts)
            {
                foreach (string elementHtml in ReadInheritedSlideElements(
                    archive,
                    inheritedPart.Document,
                    inheritedPart.Relationships,
                    themeColors,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    tableStyles,
                    inheritedBodyStyle,
                    inheritedTitleStyle))
                {
                    html.Append(elementHtml);
                }
            }

            foreach (string elementHtml in ReadSlideElements(
                archive,
                slide,
                relationships,
                themeColors,
                slideWidth,
                slideHeight,
                baseWidthPx,
                baseHeightPx,
                placeholderBounds,
                tableStyles,
                inheritedBodyStyle,
                inheritedTitleStyle))
            {
                html.Append(elementHtml);
            }

            html.Append("</div></section>");
            return html.ToString();
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
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds,
            XDocument? tableStyles,
            XElement? inheritedBodyStyle,
            XElement? inheritedTitleStyle)
        {
            XElement? shapeTree = slide.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "spTree");
            IEnumerable<XElement> elements =
                shapeTree?.Elements() ??
                slide.Root?.Elements() ??
                Enumerable.Empty<XElement>();

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
                    tableStyles,
                    inheritedBodyStyle,
                    inheritedTitleStyle,
                    null))
                {
                    yield return elementHtml;
                }
            }
        }

        private static IEnumerable<string> ReadInheritedSlideElements(
            ZipArchive archive,
            XDocument part,
            IReadOnlyDictionary<string, string> relationships,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            XDocument? tableStyles,
            XElement? inheritedBodyStyle,
            XElement? inheritedTitleStyle)
        {
            XElement? shapeTree = part.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "spTree");
            foreach (XElement element in shapeTree?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (element.Name.LocalName is "nvGrpSpPr" or "grpSpPr")
                {
                    continue;
                }

                foreach (string elementHtml in ReadSlideElement(
                    archive,
                    element,
                    relationships,
                    themeColors,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    Array.Empty<PresentationPlaceholderBounds>(),
                    tableStyles,
                    inheritedBodyStyle,
                    inheritedTitleStyle,
                    null,
                    skipPlaceholders: true))
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
            XDocument? tableStyles,
            XElement? inheritedBodyStyle,
            XElement? inheritedTitleStyle,
            PresentationGroupTransform? groupTransform,
            bool skipPlaceholders = false)
        {
            if (skipPlaceholders && TryReadPlaceholderInfo(element, out _, out _))
            {
                yield break;
            }

            if (element.Name.LocalName == "grpSp")
            {
                PresentationGroupTransform? nextTransform =
                    TryReadGroupTransform(
                        element,
                        groupTransform,
                        out PresentationGroupTransform? readTransform)
                            ? readTransform
                            : groupTransform;
                foreach (XElement child in element.Elements().Where(e =>
                    e.Name.LocalName != "nvGrpSpPr" &&
                    e.Name.LocalName != "grpSpPr"))
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
                        tableStyles,
                        inheritedBodyStyle,
                        inheritedTitleStyle,
                        nextTransform,
                        skipPlaceholders))
                    {
                        yield return childHtml;
                    }
                }

                yield break;
            }

            if (element.Name.LocalName == "pic")
            {
                if (!TryReadBounds(
                    element,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    groupTransform,
                    out string bounds))
                {
                    yield break;
                }

                string? relationshipId = element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "blip")
                    ?.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "embed")
                    ?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId) ||
                    !relationships.TryGetValue(relationshipId, out string? imagePath))
                {
                    yield break;
                }

                string? dataUri =
                    OfficePresentationPackageReader.TryReadImageDataUri(archive, imagePath);
                if (string.IsNullOrEmpty(dataUri))
                {
                    yield break;
                }

                string pictureStyle = ReadPictureFrameStyle(element);
                string imageStyle = ReadPictureImageStyle(element);
                string alt = element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "cNvPr")
                    ?.Attribute("name")?.Value ?? string.Empty;
                yield return
                    "<div class=\"ppt-image\" style=\"" +
                    bounds +
                    pictureStyle +
                    "\"><img alt=\"" +
                    Html(alt) +
                    "\" src=\"" +
                    Html(dataUri) +
                    "\" style=\"" +
                    imageStyle +
                    "\"></div>";
                yield break;
            }

            if (element.Name.LocalName == "graphicFrame" &&
                element.Descendants().Any(descendant => descendant.Name.LocalName == "chart"))
            {
                if (!TryReadBounds(
                    element,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    groupTransform,
                    out string bounds))
                {
                    yield break;
                }

                string? relationshipId = element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "chart")
                    ?.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "id")
                    ?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId) ||
                    !relationships.TryGetValue(relationshipId, out string? chartPath))
                {
                    yield break;
                }

                string? chartSvg = OfficePresentationChartSvgRenderer.TryBuild(
                    archive,
                    chartPath,
                    themeColors);
                if (!string.IsNullOrWhiteSpace(chartSvg))
                {
                    yield return
                        "<div class=\"ppt-chart\" style=\"" +
                        bounds +
                        "\">" +
                        chartSvg +
                        "</div>";
                }

                yield break;
            }

            if (element.Name.LocalName == "graphicFrame" &&
                element.Descendants().Any(descendant => descendant.Name.LocalName == "tbl"))
            {
                if (!TryReadBounds(
                    element,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    groupTransform,
                    out string bounds))
                {
                    yield break;
                }

                XElement? table = element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "tbl");
                if (table == null)
                {
                    yield break;
                }

                string tableHtml = OfficePresentationTextHtmlRenderer.BuildTableHtml(
                    table,
                    themeColors,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx,
                    tableStyles);
                if (!string.IsNullOrWhiteSpace(tableHtml))
                {
                    yield return
                        "<div class=\"ppt-table\" style=\"" +
                        bounds +
                        "\">" +
                        tableHtml +
                        "</div>";
                }

                yield break;
            }

            if (element.Name.LocalName == "cxnSp" ||
                IsLineShape(element))
            {
                if (TryBuildConnectorSvg(
                        element,
                        themeColors,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx,
                        groupTransform,
                        out string connectorHtml))
                {
                    yield return connectorHtml;
                }

                yield break;
            }

            if (element.Name.LocalName != "sp")
            {
                yield break;
            }

            XElement? shapeProperties = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spPr");
            string boxStyle = ReadShapeBoxStyle(
                shapeProperties,
                themeColors,
                archive,
                relationships);
            string boundsStyle = TryReadBounds(
                element,
                slideWidth,
                slideHeight,
                baseWidthPx,
                baseHeightPx,
                groupTransform,
                placeholderBounds,
                out string readBounds)
                    ? readBounds
                    : "left:48px;top:27px;width:864px;height:auto;";
            XElement? textBody = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (textBody != null && themeColors.Count > 1)
            {
                boxStyle += "color:" + themeColors[1] + ";";
            }
            string textHtml = OfficePresentationTextHtmlRenderer.BuildShapeTextHtml(
                element,
                themeColors,
                slideWidth,
                baseWidthPx,
                inheritedBodyStyle,
                inheritedTitleStyle);
            if (!string.IsNullOrWhiteSpace(textHtml))
            {
                string textBoxStyle =
                    OfficePresentationTextHtmlRenderer.ReadTextBodyBoxStyle(
                        textBody,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx);
                yield return
                    "<div class=\"ppt-shape\" style=\"" +
                    boundsStyle +
                    boxStyle +
                    textBoxStyle +
                    "\"><div class=\"ppt-text\">" +
                    textHtml +
                    "</div></div>";
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(boxStyle))
            {
                yield return
                    "<div class=\"ppt-shape ppt-box\" style=\"" +
                    boundsStyle +
                    boxStyle +
                    "\"></div>";
            }
        }

        private static bool IsLineShape(XElement element)
        {
            if (element.Name.LocalName != "sp")
            {
                return false;
            }

            XElement? geometry = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "prstGeom");
            string? preset = geometry?.Attribute("prst")?.Value;
            return string.Equals(preset, "line", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preset, "straightConnector1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryBuildConnectorSvg(
            XElement element,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            PresentationGroupTransform? groupTransform,
            out string connectorHtml)
        {
            connectorHtml = string.Empty;
            if (!TryReadRawBounds(
                    element,
                    out long x,
                    out long y,
                    out long width,
                    out long height,
                    out int rotation))
            {
                return false;
            }

            XElement? shapeProperties = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName is "spPr" or "picPr" or "grpSpPr");
            XElement? transform = shapeProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "xfrm") ??
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? line = shapeProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ln");
            if (line == null || line.Descendants().Any(e => e.Name.LocalName == "noFill"))
            {
                return false;
            }

            string lineColor = ReadPresentationColor(line, themeColors) ?? "#000000";
            double lineWidthPx = 1;
            if (TryReadLong(line, "w", out long lineWidth) && lineWidth > 0)
            {
                lineWidthPx = Math.Max(.5, lineWidth / 12700.0);
            }

            double mappedX = groupTransform?.MapX(x) ?? x;
            double mappedY = groupTransform?.MapY(y) ?? y;
            double mappedWidth = groupTransform?.MapCx(width) ?? width;
            double mappedHeight = groupTransform?.MapCy(height) ?? height;
            double startX = mappedX;
            double startY = mappedY;
            double endX = mappedX + mappedWidth;
            double endY = mappedY + mappedHeight;
            if (ReadBooleanAttribute(transform, "flipH"))
            {
                (startX, endX) = (endX, startX);
            }

            if (ReadBooleanAttribute(transform, "flipV"))
            {
                (startY, endY) = (endY, startY);
            }

            if (rotation != 0)
            {
                double centerX = mappedX + mappedWidth / 2.0;
                double centerY = mappedY + mappedHeight / 2.0;
                (startX, startY) = RotatePoint(
                    startX,
                    startY,
                    centerX,
                    centerY,
                    rotation / 60000.0);
                (endX, endY) = RotatePoint(
                    endX,
                    endY,
                    centerX,
                    centerY,
                    rotation / 60000.0);
            }

            double startXPx = startX / Math.Max(1.0, slideWidth) * baseWidthPx;
            double startYPx = startY / Math.Max(1.0, slideHeight) * baseHeightPx;
            double endXPx = endX / Math.Max(1.0, slideWidth) * baseWidthPx;
            double endYPx = endY / Math.Max(1.0, slideHeight) * baseHeightPx;

            string? dashArray = ReadConnectorDashArray(line);
            string lineCap = ReadConnectorLineCap(line);
            string? headEnd = ReadConnectorEndType(line, "headEnd");
            string? tailEnd = ReadConnectorEndType(line, "tailEnd");
            string markerId = "ppt-connector-arrow-" +
                (element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "cNvPr")
                    ?.Attribute("id")?.Value ??
                 Math.Abs(element.GetHashCode()).ToString(CultureInfo.InvariantCulture));

            var svg = new StringBuilder();
            svg.Append("<svg class=\"ppt-connector\" aria-hidden=\"true\" viewBox=\"0 0 ")
                .Append(FormatInvariant(baseWidthPx))
                .Append(' ')
                .Append(FormatInvariant(baseHeightPx))
                .Append("\" style=\"left:0;top:0;width:")
                .Append(FormatInvariant(baseWidthPx))
                .Append("px;height:")
                .Append(FormatInvariant(baseHeightPx))
                .Append("px;\">");

            if (headEnd != null || tailEnd != null)
            {
                svg.Append("<defs><marker id=\"")
                    .Append(Html(markerId))
                    .Append("\" markerUnits=\"userSpaceOnUse\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"5\" orient=\"auto-start-reverse\" viewBox=\"0 0 10 10\">")
                    .Append(BuildConnectorMarkerPath(headEnd ?? tailEnd ?? "triangle", lineColor))
                    .Append("</marker></defs>");
            }

            svg.Append("<line x1=\"")
                .Append(FormatInvariant(startXPx))
                .Append("\" y1=\"")
                .Append(FormatInvariant(startYPx))
                .Append("\" x2=\"")
                .Append(FormatInvariant(endXPx))
                .Append("\" y2=\"")
                .Append(FormatInvariant(endYPx))
                .Append("\" stroke=\"")
                .Append(Html(lineColor))
                .Append("\" stroke-width=\"")
                .Append(FormatInvariant(lineWidthPx))
                .Append("\" stroke-linecap=\"")
                .Append(lineCap)
                .Append("\" fill=\"none\"");
            if (!string.IsNullOrWhiteSpace(dashArray))
            {
                svg.Append(" stroke-dasharray=\"")
                    .Append(dashArray)
                    .Append("\"");
            }

            if (headEnd != null)
            {
                svg.Append(" marker-start=\"url(#")
                    .Append(Html(markerId))
                    .Append(")\"");
            }

            if (tailEnd != null)
            {
                svg.Append(" marker-end=\"url(#")
                    .Append(Html(markerId))
                    .Append(")\"");
            }

            svg.Append(" /></svg>");
            connectorHtml = svg.ToString();
            return true;
        }

        private static bool ReadBooleanAttribute(XElement? element, string attributeName)
        {
            string? value = element?.Attribute(attributeName)?.Value;
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static (double X, double Y) RotatePoint(
            double x,
            double y,
            double centerX,
            double centerY,
            double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double offsetX = x - centerX;
            double offsetY = y - centerY;
            return (
                centerX + offsetX * cos - offsetY * sin,
                centerY + offsetX * sin + offsetY * cos);
        }

        private static string? ReadConnectorDashArray(XElement line)
        {
            string? dash = line.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "prstDash")
                ?.Attribute("val")?.Value;
            return dash?.ToLowerInvariant() switch
            {
                "dot" or "sysdot" => "1 4",
                "dash" or "sysdash" => "8 5",
                "dashdot" or "sysdashdot" => "8 5 1 5",
                "lgdash" => "12 5",
                "lgdashdot" => "12 5 1 5",
                "lgdashdotdot" => "12 5 1 5 1 5",
                "solid" or "sysdashdotdot" or null => null,
                _ => null
            };
        }

        private static string ReadConnectorLineCap(XElement line)
        {
            if (line.Descendants().Any(e => e.Name.LocalName == "round"))
            {
                return "round";
            }

            if (line.Descendants().Any(e => e.Name.LocalName == "square"))
            {
                return "square";
            }

            return "butt";
        }

        private static string? ReadConnectorEndType(XElement line, string elementName)
        {
            string? type = line.Elements()
                .FirstOrDefault(e => e.Name.LocalName == elementName)
                ?.Attribute("type")?.Value;
            return string.IsNullOrWhiteSpace(type) ||
                string.Equals(type, "none", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : type;
        }

        private static string BuildConnectorMarkerPath(string type, string color)
        {
            string path = type.ToLowerInvariant() switch
            {
                "stealth" => "M 0 0 L 10 5 L 0 10 L 3 5 Z",
                "diamond" => "M 0 5 L 5 0 L 10 5 L 5 10 Z",
                "oval" => "M 5 0 A 5 5 0 1 1 5 10 A 5 5 0 1 1 5 0 Z",
                _ => "M 0 0 L 10 5 L 0 10 Z"
            };
            return "<path d=\"" + path + "\" fill=\"" + Html(color) + "\" />";
        }

        private static string ReadShapeBoxStyle(
            XElement? shapeProperties,
            IReadOnlyList<string> themeColors,
            ZipArchive archive,
            IReadOnlyDictionary<string, string> relationships)
        {
            if (shapeProperties == null)
            {
                return string.Empty;
            }

            var style = new StringBuilder();
            XElement? fillElement = shapeProperties.Elements()
                .FirstOrDefault(e => e.Name.LocalName is
                    "solidFill" or "gradFill" or "pattFill" or "blipFill" or
                    "fillRef" or "noFill");
            bool hasShapeNoFill = shapeProperties.Elements()
                .Any(e => e.Name.LocalName == "noFill");
            string? fill = ReadPresentationFill(fillElement, themeColors);
            if (!string.IsNullOrWhiteSpace(fill) && !hasShapeNoFill)
            {
                style.Append("background:").Append(fill).Append(';');
            }

            if (fillElement?.Name.LocalName == "blipFill")
            {
                string? relationshipId = fillElement.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "blip")
                    ?.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "embed")
                    ?.Value;
                if (!string.IsNullOrWhiteSpace(relationshipId) &&
                    relationships.TryGetValue(relationshipId, out string? imagePath))
                {
                    string? imageDataUri =
                        OfficePresentationPackageReader.TryReadImageDataUri(
                            archive,
                            imagePath);
                    if (!string.IsNullOrWhiteSpace(imageDataUri))
                    {
                        style.Append("background-image:url(")
                            .Append(Html(imageDataUri))
                            .Append(");background-size:100% 100%;background-position:center;background-repeat:no-repeat;");
                    }
                }
            }

            string? customGeometryClipPath = ReadCustomGeometryClipPath(shapeProperties);
            if (!string.IsNullOrWhiteSpace(customGeometryClipPath))
            {
                style.Append("clip-path:")
                    .Append(customGeometryClipPath)
                    .Append(';');
            }

            XElement? line = shapeProperties.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ln");
            if (line != null &&
                !line.Descendants().Any(e => e.Name.LocalName == "noFill"))
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

        private static string? ReadCustomGeometryClipPath(XElement? shapeProperties)
        {
            XElement? customGeometry = shapeProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "custGeom");
            XElement? path = customGeometry?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "pathLst")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "path");
            if (path == null ||
                !TryReadLong(path, "w", out long pathWidth) ||
                !TryReadLong(path, "h", out long pathHeight) ||
                pathWidth <= 0 ||
                pathHeight <= 0)
            {
                return null;
            }

            var points = new List<(double X, double Y)>();
            (double X, double Y)? current = null;
            foreach (XElement command in path.Elements())
            {
                if (command.Name.LocalName is "moveTo" or "lnTo")
                {
                    if (TryReadGeometryPoint(command, out (double X, double Y) point))
                    {
                        points.Add(point);
                        current = point;
                    }

                    continue;
                }

                if (command.Name.LocalName == "cubicBezTo")
                {
                    List<(double X, double Y)> controlPoints = command.Elements()
                        .Where(e => e.Name.LocalName == "pt")
                        .Select(e =>
                            TryReadGeometryPoint(e, out (double X, double Y) point)
                                ? point
                                : (double.NaN, double.NaN))
                        .ToList();
                    if (current.HasValue && controlPoints.Count >= 3 &&
                        controlPoints.Take(3).All(point =>
                            !double.IsNaN(point.X) && !double.IsNaN(point.Y)))
                    {
                        (double X, double Y) start = current.Value;
                        (double X, double Y) firstControl = controlPoints[0];
                        (double X, double Y) secondControl = controlPoints[1];
                        (double X, double Y) end = controlPoints[2];
                        const int sampleCount = 10;
                        for (int sample = 1; sample <= sampleCount; sample++)
                        {
                            double t = sample / (double)sampleCount;
                            double inverseT = 1.0 - t;
                            points.Add((
                                inverseT * inverseT * inverseT * start.X +
                                3 * inverseT * inverseT * t * firstControl.X +
                                3 * inverseT * t * t * secondControl.X +
                                t * t * t * end.X,
                                inverseT * inverseT * inverseT * start.Y +
                                3 * inverseT * inverseT * t * firstControl.Y +
                                3 * inverseT * t * t * secondControl.Y +
                                t * t * t * end.Y));
                        }

                        current = end;
                    }

                    continue;
                }

                if (command.Name.LocalName == "quadBezTo")
                {
                    List<(double X, double Y)> controlPoints = command.Elements()
                        .Where(e => e.Name.LocalName == "pt")
                        .Select(e =>
                            TryReadGeometryPoint(e, out (double X, double Y) point)
                                ? point
                                : (double.NaN, double.NaN))
                        .ToList();
                    if (current.HasValue && controlPoints.Count >= 2 &&
                        controlPoints.Take(2).All(point =>
                            !double.IsNaN(point.X) && !double.IsNaN(point.Y)))
                    {
                        (double X, double Y) start = current.Value;
                        (double X, double Y) control = controlPoints[0];
                        (double X, double Y) end = controlPoints[1];
                        const int sampleCount = 8;
                        for (int sample = 1; sample <= sampleCount; sample++)
                        {
                            double t = sample / (double)sampleCount;
                            double inverseT = 1.0 - t;
                            points.Add((
                                inverseT * inverseT * start.X +
                                2 * inverseT * t * control.X +
                                t * t * end.X,
                                inverseT * inverseT * start.Y +
                                2 * inverseT * t * control.Y +
                                t * t * end.Y));
                        }

                        current = end;
                    }
                }
            }

            if (points.Count < 3)
            {
                return null;
            }

            var clipPath = new StringBuilder("polygon(");
            for (int index = 0; index < points.Count; index++)
            {
                if (index > 0)
                {
                    clipPath.Append(',');
                }

                (double x, double y) = points[index];
                clipPath.Append(FormatInvariant(
                        Math.Clamp(x / pathWidth * 100.0, 0, 100)))
                    .Append("% ")
                    .Append(FormatInvariant(
                        Math.Clamp(y / pathHeight * 100.0, 0, 100)))
                    .Append('%');
            }

            return clipPath.Append(')').ToString();
        }

        private static bool TryReadGeometryPoint(
            XElement command,
            out (double X, double Y) point)
        {
            XElement? pointElement = command.Name.LocalName == "pt"
                ? command
                : command.Elements().FirstOrDefault(e => e.Name.LocalName == "pt");
            if (pointElement != null &&
                TryReadLong(pointElement, "x", out long x) &&
                TryReadLong(pointElement, "y", out long y))
            {
                point = (x, y);
                return true;
            }

            point = (double.NaN, double.NaN);
            return false;
        }

        private static async Task<string?> ReadSlideBackgroundAsync(
            ZipArchive archive,
            XDocument slide,
            IReadOnlyDictionary<string, string> slideRelationships,
            IReadOnlyList<string> themeColors)
        {
            string? background = await ReadPresentationBackgroundAsync(
                archive,
                slide,
                slideRelationships,
                themeColors).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(background))
            {
                return background;
            }

            string? layoutPath = FindRelatedPart(slideRelationships, "slideLayouts");
            if (string.IsNullOrWhiteSpace(layoutPath))
            {
                return null;
            }

            IReadOnlyDictionary<string, string> layoutRelationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(layoutPath),
                    Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);

            XDocument? layout = await OfficePresentationPackageReader
                .TryLoadXmlEntryAsync(archive, layoutPath)
                .ConfigureAwait(false);
            background = layout == null
                ? null
                : await ReadPresentationBackgroundAsync(
                    archive,
                    layout,
                    layoutRelationships,
                    themeColors).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(background))
            {
                return background;
            }

            string? masterPath = FindRelatedPart(layoutRelationships, "slideMasters");
            if (string.IsNullOrWhiteSpace(masterPath))
            {
                return null;
            }

            XDocument? master = await OfficePresentationPackageReader
                .TryLoadXmlEntryAsync(archive, masterPath)
                .ConfigureAwait(false);
            if (master == null)
            {
                return null;
            }

            IReadOnlyDictionary<string, string> masterRelationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(masterPath),
                    Path.GetDirectoryName(masterPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);
            return await ReadPresentationBackgroundAsync(
                archive,
                master,
                masterRelationships,
                themeColors).ConfigureAwait(false);
        }

        private static string? FindRelatedPart(
            IReadOnlyDictionary<string, string> relationships,
            string partFolder)
        {
            return relationships.Values.FirstOrDefault(path =>
                path.Contains("/" + partFolder + "/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(partFolder + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string?> ReadPresentationBackgroundAsync(
            ZipArchive archive,
            XDocument part,
            IReadOnlyDictionary<string, string> relationships,
            IReadOnlyList<string> themeColors)
        {
            XElement? background = part.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "bg");
            XElement? fill = FindPresentationFill(background) ??
                background?.Descendants().FirstOrDefault(e => e.Name.LocalName == "bgRef");
            if (fill == null || fill.Name.LocalName == "noFill")
            {
                return null;
            }

            if (fill.Name.LocalName == "blipFill")
            {
                string? relationshipId = fill.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "blip")?
                    .Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName is "embed" or "link")?
                    .Value;
                if (!string.IsNullOrWhiteSpace(relationshipId) &&
                    relationships.TryGetValue(relationshipId, out string? imagePath))
                {
                    string? imageDataUri = OfficePresentationPackageReader.TryReadImageDataUri(
                        archive,
                        imagePath);
                    if (!string.IsNullOrWhiteSpace(imageDataUri))
                    {
                        return "url(" + imageDataUri + ") center / 100% 100% no-repeat";
                    }
                }

                return null;
            }

            return fill.Name.LocalName == "bgRef"
                ? ReadPresentationColor(fill, themeColors)
                : ReadPresentationFill(fill, themeColors);
        }

        private static async Task<IReadOnlyList<PresentationPlaceholderBounds>>
            LoadSlidePlaceholderBoundsAsync(
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
            XDocument? layout =
                await OfficePresentationPackageReader.TryLoadXmlEntryAsync(
                    archive,
                    layoutPath)
                .ConfigureAwait(false);
            if (layout != null)
            {
                result.AddRange(ReadPlaceholderBoundsFromPart(
                    layout,
                    slideWidth,
                    slideHeight,
                    baseWidthPx,
                    baseHeightPx));
            }

            IReadOnlyDictionary<string, string> layoutRelationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(layoutPath),
                    Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty)
                .ConfigureAwait(false);
            string? masterPath = layoutRelationships.Values.FirstOrDefault(path =>
                path.Contains("/slideMasters/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("slideMasters/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(masterPath))
            {
                XDocument? master =
                    await OfficePresentationPackageReader.TryLoadXmlEntryAsync(
                        archive,
                        masterPath)
                    .ConfigureAwait(false);
                if (master != null)
                {
                    result.AddRange(ReadPlaceholderBoundsFromPart(
                        master,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx));
                }
            }

            return result;
        }

        private static async Task<IReadOnlyList<PresentationInheritedPart>>
            LoadInheritedSlidePartsAsync(
                ZipArchive archive,
                IReadOnlyDictionary<string, string> slideRelationships)
        {
            string? layoutPath = FindRelatedPart(slideRelationships, "slideLayouts");
            if (string.IsNullOrWhiteSpace(layoutPath))
            {
                return Array.Empty<PresentationInheritedPart>();
            }

            IReadOnlyDictionary<string, string> layoutRelationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(layoutPath),
                    Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);
            string? masterPath = FindRelatedPart(layoutRelationships, "slideMasters");
            var parts = new List<PresentationInheritedPart>();
            if (!string.IsNullOrWhiteSpace(masterPath))
            {
                XDocument? master = await OfficePresentationPackageReader
                    .TryLoadXmlEntryAsync(archive, masterPath)
                    .ConfigureAwait(false);
                if (master != null)
                {
                    IReadOnlyDictionary<string, string> masterRelationships =
                        await OfficePresentationPackageReader.LoadRelationshipsAsync(
                            archive,
                            OfficePresentationPackageReader.GetRelationshipsPath(masterPath),
                            Path.GetDirectoryName(masterPath)?.Replace('\\', '/') ?? string.Empty)
                            .ConfigureAwait(false);
                    parts.Add(new PresentationInheritedPart
                    {
                        Document = master,
                        Relationships = masterRelationships
                    });
                }
            }

            XDocument? layout = await OfficePresentationPackageReader
                .TryLoadXmlEntryAsync(archive, layoutPath)
                .ConfigureAwait(false);
            if (layout != null)
            {
                parts.Add(new PresentationInheritedPart
                {
                    Document = layout,
                    Relationships = layoutRelationships
                });
            }

            return parts;
        }

        private static IEnumerable<PresentationPlaceholderBounds>
            ReadPlaceholderBoundsFromPart(
                XDocument part,
                long slideWidth,
                long slideHeight,
                double baseWidthPx,
                double baseHeightPx)
        {
            XElement? shapeTree = part.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "spTree");
            foreach (XElement element in shapeTree?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (!TryReadPlaceholderInfo(element, out string? type, out string? index) ||
                    !TryReadBounds(
                        element,
                        slideWidth,
                        slideHeight,
                        baseWidthPx,
                        baseHeightPx,
                        out string bounds))
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

        private static bool TryReadPlaceholderInfo(
            XElement element,
            out string? type,
            out string? index)
        {
            XElement? placeholder = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ph");
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

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            out string bounds)
        {
            return TryReadBounds(
                element,
                slideWidth,
                slideHeight,
                baseWidthPx,
                baseHeightPx,
                null,
                null,
                out bounds);
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
            return TryReadBounds(
                element,
                slideWidth,
                slideHeight,
                baseWidthPx,
                baseHeightPx,
                groupTransform,
                null,
                out bounds);
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
            if (!TryReadRawBounds(
                    element,
                    out long x,
                    out long y,
                    out long width,
                    out long height,
                    out int rotation) ||
                width <= 0 ||
                height <= 0)
            {
                if (placeholderBounds != null &&
                    TryReadPlaceholderBounds(
                        element,
                        placeholderBounds,
                        out string inheritedBounds))
                {
                    bounds = inheritedBounds;
                    return true;
                }

                return false;
            }

            double mappedX = groupTransform?.MapX(x) ?? x;
            double mappedY = groupTransform?.MapY(y) ?? y;
            double mappedWidth = groupTransform?.MapCx(width) ?? width;
            double mappedHeight = groupTransform?.MapCy(height) ?? height;
            bounds = "left:" + Pixels(mappedX, slideWidth, baseWidthPx) +
                ";top:" + Pixels(mappedY, slideHeight, baseHeightPx) +
                ";width:" + Pixels(mappedWidth, slideWidth, baseWidthPx) +
                ";height:" + Pixels(mappedHeight, slideHeight, baseHeightPx) +
                ";";
            if (rotation != 0)
            {
                bounds +=
                    "transform:rotate(" +
                    FormatInvariant(rotation / 60000.0) +
                    "deg);";
            }

            return true;
        }

        private static bool TryReadGroupTransform(
            XElement group,
            PresentationGroupTransform? parent,
            out PresentationGroupTransform? transform)
        {
            transform = null;
            XElement? groupProperties = group.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "grpSpPr");
            XElement? xfrm = groupProperties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? offset = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "off");
            XElement? extent = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ext");
            XElement? childOffset = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "chOff");
            XElement? childExtent = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "chExt");
            if (offset == null ||
                extent == null ||
                childOffset == null ||
                childExtent == null ||
                !TryReadLong(offset, "x", out long x) ||
                !TryReadLong(offset, "y", out long y) ||
                !TryReadLong(extent, "cx", out long width) ||
                !TryReadLong(extent, "cy", out long height) ||
                !TryReadLong(childOffset, "x", out long childX) ||
                !TryReadLong(childOffset, "y", out long childY) ||
                !TryReadLong(childExtent, "cx", out long childWidth) ||
                !TryReadLong(childExtent, "cy", out long childHeight) ||
                width <= 0 ||
                height <= 0 ||
                childWidth <= 0 ||
                childHeight <= 0)
            {
                return false;
            }

            transform = new PresentationGroupTransform
            {
                X = parent?.MapX(x) ?? x,
                Y = parent?.MapY(y) ?? y,
                Cx = parent?.MapCx(width) ?? width,
                Cy = parent?.MapCy(height) ?? height,
                ChildX = childX,
                ChildY = childY,
                ChildCx = childWidth,
                ChildCy = childHeight
            };
            return true;
        }

        private static bool TryReadRawBounds(
            XElement element,
            out long x,
            out long y,
            out long width,
            out long height,
            out int rotation)
        {
            x = 0;
            y = 0;
            width = 0;
            height = 0;
            rotation = 0;
            XElement? properties = element.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "spPr" ||
                e.Name.LocalName == "picPr" ||
                e.Name.LocalName == "grpSpPr");
            XElement? xfrm = properties?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "xfrm") ??
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? offset = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "off");
            XElement? extent = xfrm?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ext");
            rotation = TryReadInt(xfrm ?? element, "rot");
            return offset != null &&
                extent != null &&
                TryReadLong(offset, "x", out x) &&
                TryReadLong(offset, "y", out y) &&
                TryReadLong(extent, "cx", out width) &&
                TryReadLong(extent, "cy", out height);
        }

        private static string ReadPictureFrameStyle(XElement picture)
        {
            string? geometry = picture.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "prstGeom")
                ?.Attribute("prst")?.Value;
            return geometry switch
            {
                "ellipse" => "border-radius:50%;",
                "roundRect" => "border-radius:4%;",
                _ => string.Empty
            };
        }

        private static string ReadPictureImageStyle(XElement picture)
        {
            XElement? sourceRectangle = picture.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "srcRect");
            if (sourceRectangle == null)
            {
                return string.Empty;
            }

            int left = Math.Clamp(TryReadInt(sourceRectangle, "l"), 0, 99999);
            int top = Math.Clamp(TryReadInt(sourceRectangle, "t"), 0, 99999);
            int right = Math.Clamp(TryReadInt(sourceRectangle, "r"), 0, 99999);
            int bottom = Math.Clamp(TryReadInt(sourceRectangle, "b"), 0, 99999);
            int visibleWidth = Math.Max(1, 100000 - left - right);
            int visibleHeight = Math.Max(1, 100000 - top - bottom);
            if (left == 0 && top == 0 && right == 0 && bottom == 0)
            {
                return string.Empty;
            }

            return "position:absolute;" +
                "left:" +
                FormatInvariant(-left / (double)visibleWidth * 100.0) +
                "%;" +
                "top:" +
                FormatInvariant(-top / (double)visibleHeight * 100.0) +
                "%;" +
                "width:" +
                FormatInvariant(100000.0 / visibleWidth * 100.0) +
                "%;" +
                "height:" +
                FormatInvariant(100000.0 / visibleHeight * 100.0) +
                "%;";
        }
    }
}
