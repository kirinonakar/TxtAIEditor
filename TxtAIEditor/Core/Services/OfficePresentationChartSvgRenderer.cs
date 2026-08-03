using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficePresentationRenderingUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationChartSvgRenderer
    {
        private sealed class PresentationChartSeries
        {
            public string Name { get; init; } = string.Empty;
            public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
            public IReadOnlyList<double?> Values { get; init; } = Array.Empty<double?>();
            public string Color { get; init; } = "#2864DC";
            public IReadOnlyDictionary<int, string> PointColors { get; init; } =
                new Dictionary<int, string>();
        }

        private sealed class PresentationChartUserShape
        {
            public double X { get; init; }
            public double Y { get; init; }
            public double Width { get; init; }
            public double Height { get; init; }
            public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
            public double FontSize { get; init; } = 21;
            public string FontFamily { get; init; } = "Arial";
        }

        public static string? TryBuild(
            ZipArchive archive,
            string chartPath,
            IReadOnlyList<string> themeColors)
        {
            XDocument? chartDocument = OfficePresentationPackageReader.TryLoadXmlEntry(
                archive,
                chartPath,
                5_000_000);
            XElement? plotArea = chartDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "plotArea");
            XElement? chart = plotArea?.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "barChart" ||
                e.Name.LocalName == "lineChart" ||
                e.Name.LocalName == "pieChart" ||
                e.Name.LocalName == "pie3DChart");
            if (plotArea == null || chart == null)
            {
                return null;
            }

            List<PresentationChartSeries> series = chart.Elements()
                .Where(e => e.Name.LocalName == "ser")
                .Select((element, index) => ReadChartSeries(element, index, themeColors))
                .Where(item => item.Values.Count > 0)
                .ToList();
            if (series.Count == 0)
            {
                return null;
            }

            XDocument? chartUserShapes = TryLoadChartUserShapes(
                archive,
                chartPath,
                chartDocument);

            int categoryCount = series.Max(item =>
                Math.Max(item.Categories.Count, item.Values.Count));
            if (categoryCount <= 0)
            {
                return null;
            }

            List<string> categories = Enumerable.Range(0, categoryCount)
                .Select(index => series
                    .Select(item =>
                        index < item.Categories.Count
                            ? item.Categories[index]
                            : null)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                    (index + 1).ToString(CultureInfo.InvariantCulture))
                .ToList();
            string categoryFormatCode = plotArea.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "catAx")?
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "numFmt")
                ?.Attribute("formatCode")?.Value ??
                chart.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "numCache")?
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "formatCode")
                    ?.Value ??
                string.Empty;
            if (IsChartDateFormat(categoryFormatCode))
            {
                bool use1904Dates = IsPresentationBooleanTrue(
                    chartDocument?.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "date1904")
                        ?.Attribute("val")?.Value);
                categories = categories
                    .Select(value => FormatChartCategoryDate(value, use1904Dates))
                    .ToList();
            }

            List<double> values = series.SelectMany(item => item.Values)
                .Where(value =>
                    value.HasValue &&
                    !double.IsNaN(value.Value) &&
                    !double.IsInfinity(value.Value))
                .Select(value => value!.Value)
                .ToList();
            if (values.Count == 0)
            {
                return null;
            }

            string background = ReadPresentationColor(
                chartDocument?.Root?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "spPr"),
                themeColors) ?? "#FFFFFF";
            if (chart.Name.LocalName is "pieChart" or "pie3DChart")
            {
                bool varyColors = IsPresentationBooleanTrue(
                    chart.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "varyColors")
                        ?.Attribute("val")?.Value);
                bool showPercent = chart.Descendants()
                    .Where(e => e.Name.LocalName == "showPercent")
                    .Any(e => IsPresentationBooleanTrue(e.Attribute("val")?.Value));
                return BuildPieChartSvg(
                    series,
                    categories,
                    showPercent,
                    varyColors,
                    background,
                    chart.Name.LocalName == "pie3DChart",
                    themeColors,
                    chartUserShapes);
            }

            XElement? valueAxis = plotArea.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "valAx");
            double minimum = ReadChartAxisLimit(valueAxis, "min") ??
                Math.Min(0.0, values.Min());
            double maximum = ReadChartAxisLimit(valueAxis, "max") ??
                Math.Max(0.0, values.Max());
            if (maximum <= minimum)
            {
                maximum = minimum + Math.Max(1.0, Math.Abs(minimum) * 0.1);
            }

            string numberFormat = valueAxis?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "numFmt")
                ?.Attribute("formatCode")?.Value ??
                chart.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "formatCode")
                    ?.Value ??
                string.Empty;
            bool percentage = numberFormat.Contains('%', StringComparison.Ordinal);
            bool horizontalBars = chart.Name.LocalName == "barChart" &&
                string.Equals(
                    chart.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "barDir")
                        ?.Attribute("val")?.Value,
                    "bar",
                    StringComparison.OrdinalIgnoreCase);

            return BuildChartSvg(
                chart.Name.LocalName,
                horizontalBars,
                series,
                categories,
                minimum,
                maximum,
                percentage,
                background);
        }

        private static XDocument? TryLoadChartUserShapes(
            ZipArchive archive,
            string chartPath,
            XDocument? chartDocument)
        {
            string? relationshipId = chartDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "userShapes")
                ?.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "id")
                ?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId))
            {
                return null;
            }

            string basePath = Path.GetDirectoryName(chartPath)?.Replace('\\', '/') ?? string.Empty;
            IReadOnlyDictionary<string, string> relationships =
                OfficePresentationPackageReader.LoadRelationships(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(chartPath),
                    basePath);
            return relationships.TryGetValue(relationshipId, out string? userShapesPath)
                ? OfficePresentationPackageReader.TryLoadXmlEntry(
                    archive,
                    userShapesPath,
                    5_000_000)
                : null;
        }

        private static PresentationChartSeries ReadChartSeries(
            XElement series,
            int seriesIndex,
            IReadOnlyList<string> themeColors)
        {
            XElement? categorySource = series.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "cat" || e.Name.LocalName == "xVal");
            XElement? valueSource = series.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "val" || e.Name.LocalName == "yVal");
            XElement? seriesProperties = series.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "spPr");
            string fallbackColor = GetChartFallbackColor(seriesIndex, themeColors);
            var pointColors = new Dictionary<int, string>();
            foreach (XElement point in series.Elements().Where(e => e.Name.LocalName == "dPt"))
            {
                XElement? indexElement = point.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "idx");
                if (indexElement != null &&
                    int.TryParse(
                        indexElement.Attribute("val")?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int index) &&
                    index >= 0)
                {
                    string? color = ReadPresentationColor(point, themeColors);
                    if (!string.IsNullOrWhiteSpace(color))
                    {
                        pointColors[index] = color;
                    }
                }
            }

            XElement? title = series.Elements().FirstOrDefault(e => e.Name.LocalName == "tx");
            string name = title?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "v")
                ?.Value ?? string.Empty;
            return new PresentationChartSeries
            {
                Name = name,
                Categories = ReadChartTextPoints(categorySource),
                Values = ReadChartNumberPoints(valueSource),
                Color = ReadPresentationColor(seriesProperties, themeColors) ?? fallbackColor,
                PointColors = pointColors
            };
        }

        private static IReadOnlyList<string> ReadChartTextPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<string>();
            }

            var points = new SortedDictionary<int, string>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(
                    point.Attribute("idx")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int readIndex)
                        ? readIndex
                        : fallbackIndex;
                string? value = point.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "v")
                    ?.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    points[index] = value;
                }

                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string? directValue = source.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "v")
                    ?.Value;
                return string.IsNullOrEmpty(directValue)
                    ? Array.Empty<string>()
                    : new[] { directValue };
            }

            int count = Math.Max(
                points.Keys.Max() + 1,
                TryReadInt(
                    source.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "ptCount") ??
                        source,
                    "val"));
            return Enumerable.Range(0, count)
                .Select(index =>
                    points.TryGetValue(index, out string? value)
                        ? value
                        : string.Empty)
                .ToList();
        }

        private static IReadOnlyList<double?> ReadChartNumberPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<double?>();
            }

            var points = new SortedDictionary<int, double?>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(
                    point.Attribute("idx")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int readIndex)
                        ? readIndex
                        : fallbackIndex;
                string? text = point.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "v")
                    ?.Value;
                points[index] = double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value)
                        ? value
                        : null;
                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string? directValue = source.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "v")
                    ?.Value;
                return double.TryParse(
                    directValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value)
                        ? new double?[] { value }
                        : Array.Empty<double?>();
            }

            int count = Math.Max(
                points.Keys.Max() + 1,
                TryReadInt(
                    source.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "ptCount") ??
                        source,
                    "val"));
            return Enumerable.Range(0, count)
                .Select(index =>
                    points.TryGetValue(index, out double? value)
                        ? value
                        : null)
                .ToList();
        }

        private static double? ReadChartAxisLimit(XElement? valueAxis, string name)
        {
            string? text = valueAxis?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == name)
                ?.Attribute("val")?.Value;
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                    ? value
                    : null;
        }

        private static string GetChartFallbackColor(
            int index,
            IReadOnlyList<string> themeColors)
        {
            int themeIndex = 4 + index % 6;
            if (themeIndex >= 0 &&
                themeIndex < themeColors.Count &&
                Regex.IsMatch(themeColors[themeIndex], "^#[0-9A-Fa-f]{6}$"))
            {
                return themeColors[themeIndex];
            }

            string[] palette =
            {
                "#2864DC",
                "#16A46C",
                "#7656D6",
                "#D97706",
                "#DC3E42",
                "#0891B2"
            };
            return palette[index % palette.Length];
        }

        private static bool IsPresentationBooleanTrue(string? value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPieChartSvg(
            IReadOnlyList<PresentationChartSeries> series,
            IReadOnlyList<string> categories,
            bool showPercent,
            bool varyColors,
            string background,
            bool isThreeDimensional,
            IReadOnlyList<string> themeColors,
            XDocument? chartUserShapes)
        {
            const double centerX = 360;
            const double centerY = 300;
            const double radius = 208;
            PresentationChartSeries dataSeries = series[0];
            int pointCount = Math.Max(categories.Count, dataSeries.Values.Count);
            var values = Enumerable.Range(0, pointCount)
                .Select(index =>
                {
                    double? value = index < dataSeries.Values.Count
                        ? dataSeries.Values[index]
                        : null;
                    return value.HasValue &&
                        !double.IsNaN(value.Value) &&
                        !double.IsInfinity(value.Value)
                            ? Math.Max(0, value.Value)
                            : 0;
                })
                .ToList();
            double total = values.Sum();
            if (total <= double.Epsilon)
            {
                return string.Empty;
            }

            List<string> labels = Enumerable.Range(0, pointCount)
                .Select(index => index < categories.Count &&
                    !string.IsNullOrWhiteSpace(categories[index])
                        ? categories[index]
                        : (index + 1).ToString(CultureInfo.InvariantCulture))
                .ToList();
            string ariaLabel = string.Join(", ", labels.Select((label, index) =>
                label + " " + FormatPiePercent(values[index], total)));

            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1000 600\" role=\"img\" aria-label=\"")
                .Append(Html(ariaLabel))
                .Append("\"><defs><filter id=\"pie-shadow\" x=\"-30%\" y=\"-30%\" width=\"160%\" height=\"180%\"><feDropShadow dx=\"0\" dy=\"8\" stdDeviation=\"8\" flood-color=\"#1F2937\" flood-opacity=\".28\"/></filter></defs><rect width=\"1000\" height=\"600\" fill=\"")
                .Append(Html(background))
                .Append("\"/>");

            if (isThreeDimensional)
            {
                svg.Append("<g transform=\"translate(0,16)\" opacity=\".55\">");
                AppendPieSlices(
                    svg,
                    dataSeries,
                    values,
                    total,
                    varyColors,
                    themeColors,
                    centerX,
                    centerY,
                    radius);
                svg.Append("</g>");
            }

            svg.Append("<g filter=\"url(#pie-shadow)\">");
            var labelsSvg = new StringBuilder();
            double startAngle = -Math.PI / 2;
            for (int index = 0; index < values.Count; index++)
            {
                double value = values[index];
                if (value <= double.Epsilon)
                {
                    continue;
                }

                double sweep = value / total * Math.PI * 2;
                double endAngle = startAngle + sweep;
                string color = dataSeries.PointColors.TryGetValue(
                    index,
                    out string? pointColor)
                        ? pointColor
                        : varyColors
                            ? GetChartFallbackColor(index, themeColors)
                            : dataSeries.Color;
                string path = BuildPieSlicePath(
                    centerX,
                    centerY,
                    radius,
                    startAngle,
                    endAngle);
                svg.Append("<path d=\"")
                    .Append(path)
                    .Append("\" fill=\"")
                    .Append(Html(color))
                    .Append("\" stroke=\"#FFFFFF\" stroke-width=\"3\" stroke-linejoin=\"round\"/>");

                double fraction = value / total;
                if (showPercent && fraction >= .035)
                {
                    double labelAngle = startAngle + sweep / 2;
                    double labelRadius = radius * .64;
                    double labelX = centerX + Math.Cos(labelAngle) * labelRadius;
                    double labelY = centerY + Math.Sin(labelAngle) * labelRadius;
                    labelsSvg.Append("<text x=\"")
                        .Append(FormatInvariant(labelX))
                        .Append("\" y=\"")
                        .Append(FormatInvariant(labelY))
                        .Append("\" dy=\".35em\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"26\" font-weight=\"700\" fill=\"#20222B\" stroke=\"#FFFFFF\" stroke-opacity=\".9\" stroke-width=\"2\" stroke-linejoin=\"round\" paint-order=\"stroke fill\">")
                        .Append(Html(FormatPiePercent(value, total)))
                        .Append("</text>");
                }

                startAngle = endAngle;
            }

            svg.Append("</g>").Append(labelsSvg);
            AppendChartUserShapeText(svg, chartUserShapes);
            const double legendX = 650;
            const double legendTop = 84;
            const double legendRowHeight = 76;
            for (int index = 0; index < labels.Count; index++)
            {
                double value = values[index];
                string color = dataSeries.PointColors.TryGetValue(
                    index,
                    out string? pointColor)
                        ? pointColor
                        : varyColors
                            ? GetChartFallbackColor(index, themeColors)
                            : dataSeries.Color;
                double y = legendTop + index * legendRowHeight;
                svg.Append("<rect x=\"")
                    .Append(FormatInvariant(legendX))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(y))
                    .Append("\" width=\"22\" height=\"22\" rx=\"4\" fill=\"")
                    .Append(Html(color))
                    .Append("\"/>");
                svg.Append("<text x=\"")
                    .Append(FormatInvariant(legendX + 36))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(y + 18))
                    .Append("\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"20\" fill=\"#465264\">")
                    .Append(Html(labels[index]))
                    .Append("</text>");
                svg.Append("<text x=\"")
                    .Append(FormatInvariant(legendX + 36))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(y + 43))
                    .Append("\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"17\" fill=\"#6B7280\">")
                    .Append(Html(FormatPiePercent(value, total)))
                    .Append("</text>");
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        private static void AppendChartUserShapeText(
            StringBuilder svg,
            XDocument? chartUserShapes)
        {
            if (chartUserShapes == null)
            {
                return;
            }

            foreach (XElement anchor in chartUserShapes.Descendants()
                .Where(e => e.Name.LocalName == "relSizeAnchor"))
            {
                XElement? from = anchor.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "from");
                XElement? to = anchor.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "to");
                if (!TryReadDouble(from?.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "x"),
                        out double fromX) ||
                    !TryReadDouble(from?.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "y"),
                        out double fromY) ||
                    !TryReadDouble(to?.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "x"),
                        out double toX) ||
                    !TryReadDouble(to?.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "y"),
                        out double toY))
                {
                    continue;
                }

                XElement? textBody = anchor.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "txBody");
                if (textBody == null)
                {
                    continue;
                }

                List<string> lines = textBody.Elements()
                    .Where(e => e.Name.LocalName == "p")
                    .Select(paragraph => string.Concat(
                        paragraph.Descendants()
                            .Where(e => e.Name.LocalName == "t")
                            .Select(text => text.Value)))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (lines.Count == 0)
                {
                    continue;
                }

                var userShape = new PresentationChartUserShape
                {
                    X = Math.Clamp(fromX * 1000, 0, 1000),
                    Y = Math.Clamp(fromY * 600, 0, 600),
                    Width = Math.Max(1, (toX - fromX) * 1000),
                    Height = Math.Max(1, (toY - fromY) * 600),
                    Lines = lines,
                    FontSize = ReadChartUserShapeFontSize(textBody),
                    FontFamily = ReadChartUserShapeFontFamily(textBody)
                };
                double lineHeight = userShape.FontSize * 1.16;
                svg.Append("<text x=\"")
                    .Append(FormatInvariant(userShape.X + 4))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(userShape.Y + userShape.FontSize))
                    .Append("\" font-family=\"")
                    .Append(Html(userShape.FontFamily))
                    .Append("\" font-size=\"")
                    .Append(FormatInvariant(userShape.FontSize))
                    .Append("\" font-weight=\"400\" fill=\"#20222B\" text-anchor=\"start\">");
                for (int lineIndex = 0; lineIndex < userShape.Lines.Count; lineIndex++)
                {
                    svg.Append("<tspan x=\"")
                        .Append(FormatInvariant(userShape.X + 4))
                        .Append("\" dy=\"")
                        .Append(FormatInvariant(lineIndex == 0 ? 0 : lineHeight))
                        .Append("\">")
                        .Append(Html(userShape.Lines[lineIndex]))
                        .Append("</tspan>");
                }

                svg.Append("</text>");
            }
        }

        private static double ReadChartUserShapeFontSize(XElement textBody)
        {
            XElement? runProperties = textBody.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "rPr" &&
                    e.Attribute("sz") != null);
            if (runProperties != null &&
                int.TryParse(
                    runProperties.Attribute("sz")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int size) &&
                size > 0)
            {
                return Math.Clamp(size / 100.0 * 1.5, 14, 32);
            }

            return 21;
        }

        private static string ReadChartUserShapeFontFamily(XElement textBody)
        {
            string? typeface = textBody.Descendants()
                .Where(e => e.Name.LocalName == "latin")
                .Select(e => e.Attribute("typeface")?.Value)
                .FirstOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value) &&
                    !value.Contains('+', StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(typeface) ? "Arial" : typeface;
        }

        private static bool TryReadDouble(XElement? element, out double value)
        {
            return double.TryParse(
                element?.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static void AppendPieSlices(
            StringBuilder svg,
            PresentationChartSeries dataSeries,
            IReadOnlyList<double> values,
            double total,
            bool varyColors,
            IReadOnlyList<string> themeColors,
            double centerX,
            double centerY,
            double radius)
        {
            double startAngle = -Math.PI / 2;
            for (int index = 0; index < values.Count; index++)
            {
                double value = values[index];
                if (value <= double.Epsilon)
                {
                    continue;
                }

                double endAngle = startAngle + value / total * Math.PI * 2;
                string color = dataSeries.PointColors.TryGetValue(
                    index,
                    out string? pointColor)
                        ? pointColor
                        : varyColors
                            ? GetChartFallbackColor(index, themeColors)
                            : dataSeries.Color;
                svg.Append("<path d=\"")
                    .Append(BuildPieSlicePath(
                        centerX,
                        centerY,
                        radius,
                        startAngle,
                        endAngle))
                    .Append("\" fill=\"")
                    .Append(Html(color))
                    .Append("\" stroke=\"#FFFFFF\" stroke-width=\"2\"/>");
                startAngle = endAngle;
            }
        }

        private static string BuildPieSlicePath(
            double centerX,
            double centerY,
            double radius,
            double startAngle,
            double endAngle)
        {
            double sweep = Math.Max(0, endAngle - startAngle);
            if (sweep >= Math.PI * 2 - .000001)
            {
                return "M " + FormatInvariant(centerX) + " " +
                    FormatInvariant(centerY - radius) +
                    " A " + FormatInvariant(radius) + " " +
                    FormatInvariant(radius) + " 0 1 1 " +
                    FormatInvariant(centerX) + " " +
                    FormatInvariant(centerY + radius) +
                    " A " + FormatInvariant(radius) + " " +
                    FormatInvariant(radius) + " 0 1 1 " +
                    FormatInvariant(centerX) + " " +
                    FormatInvariant(centerY - radius) + " Z";
            }

            double startX = centerX + Math.Cos(startAngle) * radius;
            double startY = centerY + Math.Sin(startAngle) * radius;
            double endX = centerX + Math.Cos(endAngle) * radius;
            double endY = centerY + Math.Sin(endAngle) * radius;
            int largeArcFlag = sweep > Math.PI ? 1 : 0;
            return "M " + FormatInvariant(centerX) + " " +
                FormatInvariant(centerY) + " L " +
                FormatInvariant(startX) + " " +
                FormatInvariant(startY) + " A " +
                FormatInvariant(radius) + " " + FormatInvariant(radius) +
                " 0 " + largeArcFlag + " 1 " +
                FormatInvariant(endX) + " " + FormatInvariant(endY) + " Z";
        }

        private static string FormatPiePercent(double value, double total)
        {
            return (value / total * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string BuildChartSvg(
            string chartType,
            bool horizontalBars,
            IReadOnlyList<PresentationChartSeries> series,
            IReadOnlyList<string> categories,
            double minimum,
            double maximum,
            bool percentage,
            string background)
        {
            const double width = 1000;
            const double height = 600;
            double left = horizontalBars ? 220 : 72;
            const double right = 30;
            double top = series.Count > 1 ? 55 : 24;
            double bottom = horizontalBars ? 45 : 86;
            double plotWidth = width - left - right;
            double plotHeight = height - top - bottom;
            double range = Math.Max(0.0000001, maximum - minimum);
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1000 600\" role=\"img\" aria-label=\"")
                .Append(Html(string.Join(", ", series.Select(item => item.Name))))
                .Append("\"><rect width=\"1000\" height=\"600\" fill=\"")
                .Append(Html(background))
                .Append("\"/>");

            if (series.Count > 1)
            {
                double legendX = left;
                foreach (PresentationChartSeries item in series)
                {
                    svg.Append("<rect x=\"")
                        .Append(FormatInvariant(legendX))
                        .Append("\" y=\"18\" width=\"18\" height=\"18\" rx=\"3\" fill=\"")
                        .Append(Html(item.Color))
                        .Append("\"/><text x=\"")
                        .Append(FormatInvariant(legendX + 26))
                        .Append("\" y=\"33\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"18\" fill=\"#5F6B7D\">")
                        .Append(Html(item.Name))
                        .Append("</text>");
                    legendX += Math.Max(130, item.Name.Length * 14 + 58);
                }
            }

            const int tickCount = 5;
            if (horizontalBars)
            {
                BuildHorizontalBars(
                    svg,
                    series,
                    categories,
                    minimum,
                    range,
                    percentage,
                    left,
                    top,
                    plotWidth,
                    plotHeight,
                    tickCount);
            }
            else
            {
                BuildVerticalChart(
                    svg,
                    chartType,
                    series,
                    categories,
                    minimum,
                    range,
                    percentage,
                    left,
                    top,
                    plotWidth,
                    plotHeight,
                    tickCount);
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        private static void BuildHorizontalBars(
            StringBuilder svg,
            IReadOnlyList<PresentationChartSeries> series,
            IReadOnlyList<string> categories,
            double minimum,
            double range,
            bool percentage,
            double left,
            double top,
            double plotWidth,
            double plotHeight,
            int tickCount)
        {
            for (int tick = 0; tick <= tickCount; tick++)
            {
                double value = minimum + range * tick / tickCount;
                double x = left + plotWidth * tick / tickCount;
                svg.Append("<line x1=\"")
                    .Append(FormatInvariant(x))
                    .Append("\" y1=\"")
                    .Append(FormatInvariant(top))
                    .Append("\" x2=\"")
                    .Append(FormatInvariant(x))
                    .Append("\" y2=\"")
                    .Append(FormatInvariant(top + plotHeight))
                    .Append("\" stroke=\"#DCE2EC\" stroke-width=\"1\"/><text x=\"")
                    .Append(FormatInvariant(x))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(top + plotHeight + 30))
                    .Append("\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"17\" fill=\"#5F6B7D\">")
                    .Append(Html(FormatChartNumber(value, percentage)))
                    .Append("</text>");
            }

            double groupHeight = plotHeight / Math.Max(1, categories.Count);
            double barHeight = Math.Max(
                2,
                groupHeight * 0.7 / Math.Max(1, series.Count));
            double baselineX = left + (0 - minimum) / range * plotWidth;
            baselineX = Math.Clamp(baselineX, left, left + plotWidth);
            for (int categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
            {
                double centerY = top + groupHeight * (categoryIndex + 0.5);
                svg.Append("<text x=\"")
                    .Append(FormatInvariant(left - 14))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(centerY + 6))
                    .Append("\" text-anchor=\"end\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"18\" fill=\"#5F6B7D\">")
                    .Append(Html(categories[categoryIndex]))
                    .Append("</text>");
                for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    PresentationChartSeries item = series[seriesIndex];
                    double? readValue =
                        categoryIndex < item.Values.Count
                            ? item.Values[categoryIndex]
                            : null;
                    if (!readValue.HasValue)
                    {
                        continue;
                    }

                    double valueX =
                        left + (readValue.Value - minimum) / range * plotWidth;
                    valueX = Math.Clamp(valueX, left, left + plotWidth);
                    double y =
                        centerY - groupHeight * 0.35 + barHeight * seriesIndex;
                    string color = item.PointColors.TryGetValue(
                        categoryIndex,
                        out string? pointColor)
                            ? pointColor
                            : item.Color;
                    svg.Append("<rect x=\"")
                        .Append(FormatInvariant(Math.Min(baselineX, valueX)))
                        .Append("\" y=\"")
                        .Append(FormatInvariant(y))
                        .Append("\" width=\"")
                        .Append(FormatInvariant(Math.Max(1, Math.Abs(valueX - baselineX))))
                        .Append("\" height=\"")
                        .Append(FormatInvariant(barHeight))
                        .Append("\" rx=\"3\" fill=\"")
                        .Append(Html(color))
                        .Append("\"/>");
                }
            }
        }

        private static void BuildVerticalChart(
            StringBuilder svg,
            string chartType,
            IReadOnlyList<PresentationChartSeries> series,
            IReadOnlyList<string> categories,
            double minimum,
            double range,
            bool percentage,
            double left,
            double top,
            double plotWidth,
            double plotHeight,
            int tickCount)
        {
            for (int tick = 0; tick <= tickCount; tick++)
            {
                double value = minimum + range * tick / tickCount;
                double y = top + plotHeight - plotHeight * tick / tickCount;
                svg.Append("<line x1=\"")
                    .Append(FormatInvariant(left))
                    .Append("\" y1=\"")
                    .Append(FormatInvariant(y))
                    .Append("\" x2=\"")
                    .Append(FormatInvariant(left + plotWidth))
                    .Append("\" y2=\"")
                    .Append(FormatInvariant(y))
                    .Append("\" stroke=\"#DCE2EC\" stroke-width=\"1\"/><text x=\"")
                    .Append(FormatInvariant(left - 12))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(y + 6))
                    .Append("\" text-anchor=\"end\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"17\" fill=\"#5F6B7D\">")
                    .Append(Html(FormatChartNumber(value, percentage)))
                    .Append("</text>");
            }

            double groupWidth = plotWidth / Math.Max(1, categories.Count);
            bool rotateLabels =
                categories.Count > 8 ||
                categories.Any(value => value.Length > 8);
            for (int categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
            {
                double centerX = left + groupWidth * (categoryIndex + 0.5);
                double labelY = top + plotHeight + 28;
                svg.Append("<text x=\"")
                    .Append(FormatInvariant(centerX))
                    .Append("\" y=\"")
                    .Append(FormatInvariant(labelY))
                    .Append("\"");
                if (rotateLabels)
                {
                    svg.Append(" transform=\"rotate(-38 ")
                        .Append(FormatInvariant(centerX))
                        .Append(' ')
                        .Append(FormatInvariant(labelY))
                        .Append(")\" text-anchor=\"end\"");
                }
                else
                {
                    svg.Append(" text-anchor=\"middle\"");
                }

                svg.Append(" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"17\" fill=\"#5F6B7D\">")
                    .Append(Html(categories[categoryIndex]))
                    .Append("</text>");
            }

            if (chartType == "lineChart")
            {
                BuildLineSeries(
                    svg,
                    series,
                    categories.Count,
                    minimum,
                    range,
                    left,
                    top,
                    groupWidth,
                    plotHeight);
                return;
            }

            BuildVerticalBars(
                svg,
                series,
                categories.Count,
                minimum,
                range,
                left,
                top,
                groupWidth,
                plotHeight);
        }

        private static void BuildLineSeries(
            StringBuilder svg,
            IReadOnlyList<PresentationChartSeries> series,
            int categoryCount,
            double minimum,
            double range,
            double left,
            double top,
            double groupWidth,
            double plotHeight)
        {
            foreach (PresentationChartSeries item in series)
            {
                var path = new StringBuilder();
                for (int categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
                {
                    double? value =
                        categoryIndex < item.Values.Count
                            ? item.Values[categoryIndex]
                            : null;
                    if (!value.HasValue)
                    {
                        continue;
                    }

                    double x = left + groupWidth * (categoryIndex + 0.5);
                    double y =
                        top + plotHeight -
                        (value.Value - minimum) / range * plotHeight;
                    y = Math.Clamp(y, top, top + plotHeight);
                    path.Append(path.Length == 0 ? "M " : " L ")
                        .Append(FormatInvariant(x))
                        .Append(' ')
                        .Append(FormatInvariant(y));
                }

                svg.Append("<path d=\"")
                    .Append(path)
                    .Append("\" fill=\"none\" stroke=\"")
                    .Append(Html(item.Color))
                    .Append("\" stroke-width=\"5\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
                for (int categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
                {
                    double? value =
                        categoryIndex < item.Values.Count
                            ? item.Values[categoryIndex]
                            : null;
                    if (!value.HasValue)
                    {
                        continue;
                    }

                    double x = left + groupWidth * (categoryIndex + 0.5);
                    double y =
                        top + plotHeight -
                        (value.Value - minimum) / range * plotHeight;
                    y = Math.Clamp(y, top, top + plotHeight);
                    svg.Append("<circle cx=\"")
                        .Append(FormatInvariant(x))
                        .Append("\" cy=\"")
                        .Append(FormatInvariant(y))
                        .Append("\" r=\"6\" fill=\"")
                        .Append(Html(item.Color))
                        .Append("\"/>");
                }
            }
        }

        private static void BuildVerticalBars(
            StringBuilder svg,
            IReadOnlyList<PresentationChartSeries> series,
            int categoryCount,
            double minimum,
            double range,
            double left,
            double top,
            double groupWidth,
            double plotHeight)
        {
            double barWidth = Math.Max(
                2,
                groupWidth * 0.72 / Math.Max(1, series.Count));
            double baselineY =
                top + plotHeight - (0 - minimum) / range * plotHeight;
            baselineY = Math.Clamp(baselineY, top, top + plotHeight);
            for (int categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
            {
                double groupLeft =
                    left + groupWidth * categoryIndex + groupWidth * 0.14;
                for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    PresentationChartSeries item = series[seriesIndex];
                    double? value =
                        categoryIndex < item.Values.Count
                            ? item.Values[categoryIndex]
                            : null;
                    if (!value.HasValue)
                    {
                        continue;
                    }

                    double valueY =
                        top + plotHeight -
                        (value.Value - minimum) / range * plotHeight;
                    valueY = Math.Clamp(valueY, top, top + plotHeight);
                    string color = item.PointColors.TryGetValue(
                        categoryIndex,
                        out string? pointColor)
                            ? pointColor
                            : item.Color;
                    svg.Append("<rect x=\"")
                        .Append(FormatInvariant(groupLeft + barWidth * seriesIndex))
                        .Append("\" y=\"")
                        .Append(FormatInvariant(Math.Min(baselineY, valueY)))
                        .Append("\" width=\"")
                        .Append(FormatInvariant(barWidth))
                        .Append("\" height=\"")
                        .Append(FormatInvariant(Math.Max(1, Math.Abs(valueY - baselineY))))
                        .Append("\" rx=\"3\" fill=\"")
                        .Append(Html(color))
                        .Append("\"/>");
                }
            }
        }

        private static string FormatChartNumber(double value, bool percentage)
        {
            if (percentage)
            {
                double percentValue =
                    Math.Abs(value) <= 1.0000001 ? value * 100.0 : value;
                return percentValue.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            }

            double absolute = Math.Abs(value);
            return value.ToString(
                absolute >= 100 ? "0" : "0.##",
                CultureInfo.InvariantCulture);
        }

        private static bool IsChartDateFormat(string formatCode)
        {
            if (string.IsNullOrWhiteSpace(formatCode) ||
                formatCode.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string cleaned = Regex.Replace(formatCode, "\"[^\"]*\"", string.Empty);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?i)(?<!\\)[ymdh]");
        }

        private static string FormatChartCategoryDate(string value, bool use1904Dates)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial) ||
                double.IsNaN(serial) ||
                double.IsInfinity(serial))
            {
                return value;
            }

            try
            {
                DateTime date = use1904Dates
                    ? new DateTime(1904, 1, 1).AddDays(serial)
                    : new DateTime(1899, 12, 30).AddDays(serial);
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch
            {
                return value;
            }
        }
    }
}
