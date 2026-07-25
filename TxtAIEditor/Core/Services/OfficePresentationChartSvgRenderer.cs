using System;
using System.Collections.Generic;
using System.Globalization;
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
                e.Name.LocalName == "lineChart");
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
            string background = ReadPresentationColor(
                chartDocument?.Root?.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "spPr"),
                themeColors) ?? "#FFFFFF";
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
    }
}
