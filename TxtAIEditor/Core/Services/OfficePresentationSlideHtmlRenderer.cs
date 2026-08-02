using System;
using System.Collections.Generic;
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
                    relationships)
                    .ConfigureAwait(false);
            if (slideThemeColors.Count > 0)
            {
                themeColors = slideThemeColors;
            }

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
                tableStyles))
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
            XDocument? tableStyles)
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
            XDocument? tableStyles,
            PresentationGroupTransform? groupTransform)
        {
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
                        nextTransform))
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

            if (element.Name.LocalName != "sp")
            {
                yield break;
            }

            XElement? shapeProperties = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spPr");
            string boxStyle = ReadShapeBoxStyle(shapeProperties, themeColors);
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
            string textHtml = OfficePresentationTextHtmlRenderer.BuildShapeTextHtml(
                element,
                themeColors,
                slideWidth,
                baseWidthPx);
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

        private static string ReadShapeBoxStyle(
            XElement? shapeProperties,
            IReadOnlyList<string> themeColors)
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
