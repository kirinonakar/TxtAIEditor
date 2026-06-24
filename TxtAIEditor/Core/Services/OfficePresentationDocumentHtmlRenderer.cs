using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficePresentationDocumentHtmlRenderer
    {
        private const long DefaultSlideWidthEmu = 9144000;
        private const long DefaultSlideHeightEmu = 5143500;
        private const double PresentationBaseWidthPx = 960;

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

        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
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

            double fontScale = ReadNormAutofitScale(txBody);

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

                paragraphs.Append(BuildParagraphRunsHtml(paragraph, themeColors, slideWidth, baseWidthPx, fontScale));
                paragraphs.Append("</p>");
            }

            return paragraphs.ToString();
        }

        private static string BuildParagraphRunsHtml(
            XElement paragraph,
            IReadOnlyList<string> themeColors,
            long slideWidth,
            double baseWidthPx,
            double fontScale = 1.0)
        {
            var builder = new StringBuilder();
            XElement? defaultRunProperties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "defRPr");
            foreach (XElement element in paragraph.Elements())
            {
                if (element.Name.LocalName == "r" || element.Name.LocalName == "fld")
                {
                    XElement? runProperties = element.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr") ?? defaultRunProperties;
                    string runStyle = ReadRunTextStyle(runProperties, themeColors, slideWidth, baseWidthPx, fontScale);
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
            double baseWidthPx,
            double fontScale = 1.0)
        {
            // 셀 내부에 txBody가 있으면 normAutofit fontScale을 읽어 적용
            XElement? txBody = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (txBody != null)
            {
                fontScale = ReadNormAutofitScale(txBody);
            }

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
                    .Append(BuildParagraphRunsHtml(paragraph, themeColors, slideWidth, baseWidthPx, fontScale))
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

        /// <summary>
        /// Reads the normAutofit fontScale from a txBody element.
        /// PowerPoint uses this to automatically shrink text so it fits inside the shape.
        /// When normAutofit is present, fontScale (default 100000 = 100%) controls the actual
        /// rendered size as a percentage of the original font size.
        /// </summary>
        private static double ReadNormAutofitScale(XElement txBody)
        {
            XElement? bodyPr = txBody.Elements().FirstOrDefault(e => e.Name.LocalName == "bodyPr");
            if (bodyPr == null)
            {
                return 1.0;
            }

            XElement? normAutofit = bodyPr.Elements().FirstOrDefault(e => e.Name.LocalName == "normAutofit");
            if (normAutofit == null)
            {
                return 1.0;
            }

            // fontScale is in thousandths of a percent; 100000 = 100%
            if (int.TryParse(normAutofit.Attribute("fontScale")?.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int fontScale) &&
                fontScale > 0)
            {
                return fontScale / 100000.0;
            }

            return 1.0;
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
            double baseWidthPx,
            double fontScale = 1.0)
        {
            var style = new StringBuilder();
            double pixelsPerInch = baseWidthPx / (slideWidth / 914400.0);
            if (runProperties?.Attribute("sz")?.Value is string sizeValue &&
                int.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) &&
                size > 0)
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(size / 100.0 / 72.0 * pixelsPerInch * fontScale))
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
