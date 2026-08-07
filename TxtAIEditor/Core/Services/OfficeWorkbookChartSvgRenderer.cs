using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

using static TxtAIEditor.Core.Services.OfficeWorkbookCellFormatter;
using static TxtAIEditor.Core.Services.OfficeWorkbookPackageUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWorkbookChartSvgRenderer
    {
        internal static async Task<string?> BuildWorkbookChartSvgAsync(
            ZipArchive archive,
            string chartPath,
            IReadOnlyList<string> themeColors,
            bool use1904Dates)
        {
            try
            {
                string? svg = OfficePresentationChartSvgRenderer.TryBuild(
                    archive,
                    chartPath,
                    themeColors);
                if (!string.IsNullOrWhiteSpace(svg))
                {
                    return svg;
                }
            }
            catch
            {
                // Fall back to a simple chart for chart types not handled by the presentation renderer.
            }

            XDocument? chartDocument = await TryLoadXmlEntryAsync(archive, chartPath).ConfigureAwait(false);
            return BuildWorkbookFallbackChartSvg(chartDocument, themeColors, use1904Dates);
        }

        private static string? BuildWorkbookFallbackChartSvg(
            XDocument? chartDocument,
            IReadOnlyList<string> themeColors,
            bool use1904Dates)
        {
            XElement? plotArea = chartDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "plotArea");
            XElement? chart = plotArea?.Elements().FirstOrDefault(e =>
                e.Name.LocalName is
                    "areaChart" or "barChart" or "bubbleChart" or "doughnutChart" or
                    "lineChart" or "pie3DChart" or "pieChart" or "radarChart" or
                    "scatterChart" or "stockChart");
            if (chart == null)
            {
                return null;
            }

            if (chart.Name.LocalName == "scatterChart")
            {
                return BuildWorkbookScatterChartSvg(
                    chartDocument,
                    chart,
                    themeColors,
                    use1904Dates);
            }

            List<ViewerChartSeries> series = chart.Elements()
                .Where(e => e.Name.LocalName == "ser")
                .Select(ReadWorkbookChartSeries)
                .Where(item => item.Values.Any(value => value.HasValue))
                .ToList();
            if (series.Count == 0)
            {
                return null;
            }

            int categoryCount = series.Max(item => Math.Max(item.Categories.Count, item.Values.Count));
            if (categoryCount <= 0)
            {
                return null;
            }

            List<string> categories = Enumerable.Range(0, categoryCount)
                .Select(index => series
                    .Select(item => index < item.Categories.Count ? item.Categories[index] : string.Empty)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                    (index + 1).ToString(CultureInfo.InvariantCulture))
                .ToList();
            List<double> values = series.SelectMany(item => item.Values)
                .Where(value => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                .Select(value => value!.Value)
                .ToList();
            if (values.Count == 0)
            {
                return null;
            }

            const double svgHeight = 520;
            const double plotLeft = 78;
            const double plotTop = 66;
            const double plotWidth = 832;
            const double plotHeight = 350;
            double minimum = Math.Min(0, values.Min());
            double maximum = Math.Max(0, values.Max());
            if (maximum <= minimum)
            {
                maximum = minimum + Math.Max(1, Math.Abs(minimum) * .1);
            }

            double zeroY = plotTop + ((maximum / (maximum - minimum)) * plotHeight);
            double categoryWidth = plotWidth / categoryCount;
            double groupWidth = categoryWidth * .72;
            double barWidth = groupWidth / Math.Max(1, series.Count);
            string title = ReadWorkbookChartTitle(chartDocument);
            string[] colors = { "#2864DC", "#16A46C", "#7656D6", "#D97706", "#DC3E42", "#0891B2" };
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 520\" role=\"img\" aria-label=\"")
                .Append(Html(title))
                .Append("\"><rect width=\"960\" height=\"520\" fill=\"#FFFFFF\"/>");
            if (!string.IsNullOrWhiteSpace(title))
            {
                svg.Append("<text x=\"480\" y=\"30\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"20\" font-weight=\"600\" fill=\"#111827\">")
                    .Append(Html(title))
                    .Append("</text>");
            }

            for (int gridIndex = 0; gridIndex <= 5; gridIndex++)
            {
                double ratio = gridIndex / 5.0;
                double y = plotTop + (ratio * plotHeight);
                double value = maximum - (ratio * (maximum - minimum));
                svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                    .Append(FormatInvariant(y)).Append("\" x2=\"")
                    .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                    .Append(FormatInvariant(y)).Append("\" stroke=\"#D9E0EA\" stroke-width=\"1\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(plotLeft - 10)).Append("\" y=\"")
                    .Append(FormatInvariant(y + 4)).Append("\" text-anchor=\"end\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                    .Append(Html(FormatInvariant(value))).Append("</text>");
            }

            svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(plotTop)).Append("\" x2=\"").Append(FormatInvariant(plotLeft))
                .Append("\" y2=\"").Append(FormatInvariant(plotTop + plotHeight))
                .Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>");
            svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(zeroY)).Append("\" x2=\"")
                .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                .Append(FormatInvariant(zeroY)).Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>");

            for (int categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
            {
                double categoryLeft = plotLeft + (categoryIndex * categoryWidth) + ((categoryWidth - groupWidth) / 2);
                for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    double? value = categoryIndex < series[seriesIndex].Values.Count
                        ? series[seriesIndex].Values[categoryIndex]
                        : null;
                    if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                    {
                        continue;
                    }

                    double valueY = plotTop + ((maximum - value.Value) / (maximum - minimum) * plotHeight);
                    double y = Math.Min(valueY, zeroY);
                    double height = Math.Max(1, Math.Abs(zeroY - valueY));
                    svg.Append("<rect x=\"").Append(FormatInvariant(categoryLeft + (seriesIndex * barWidth)))
                        .Append("\" y=\"").Append(FormatInvariant(y)).Append("\" width=\"")
                        .Append(FormatInvariant(Math.Max(1, barWidth - 2))).Append("\" height=\"")
                        .Append(FormatInvariant(height)).Append("\" rx=\"2\" fill=\"")
                        .Append(colors[seriesIndex % colors.Length]).Append("\"/>");
                }

                if (categoryIndex % Math.Max(1, (int)Math.Ceiling(categoryCount / 16.0)) == 0)
                {
                    double labelX = plotLeft + (categoryIndex * categoryWidth) + (categoryWidth / 2);
                    svg.Append("<text x=\"").Append(FormatInvariant(labelX)).Append("\" y=\"")
                        .Append(FormatInvariant(plotTop + plotHeight + 22))
                        .Append("\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                        .Append(Html(TrimWorkbookChartLabel(categories[categoryIndex])))
                        .Append("</text>");
                }
            }

            double legendX = plotLeft;
            double legendY = svgHeight - 44;
            for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                string label = string.IsNullOrWhiteSpace(series[seriesIndex].Name)
                    ? (seriesIndex + 1).ToString(CultureInfo.InvariantCulture)
                    : series[seriesIndex].Name;
                svg.Append("<rect x=\"").Append(FormatInvariant(legendX)).Append("\" y=\"")
                    .Append(FormatInvariant(legendY - 10)).Append("\" width=\"12\" height=\"12\" rx=\"2\" fill=\"")
                    .Append(colors[seriesIndex % colors.Length]).Append("\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(legendX + 18)).Append("\" y=\"")
                    .Append(FormatInvariant(legendY)).Append("\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"12\" fill=\"#334155\">")
                    .Append(Html(TrimWorkbookChartLabel(label, 26))).Append("</text>");
                legendX += 130;
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        private static string? BuildWorkbookScatterChartSvg(
            XDocument? chartDocument,
            XElement chart,
            IReadOnlyList<string> themeColors,
            bool use1904Dates)
        {
            List<XElement> seriesElements = chart.Elements()
                .Where(e => e.Name.LocalName == "ser")
                .ToList();
            List<ViewerChartSeries> series = seriesElements
                .Select(ReadWorkbookChartSeries)
                .Where(item => item.Values.Any(value => value.HasValue))
                .ToList();
            if (series.Count == 0)
            {
                return null;
            }

            List<double> xValues = new();
            List<double> yValues = new();
            for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                ViewerChartSeries item = series[seriesIndex];
                int pointCount = Math.Max(item.XValues.Count, item.Values.Count);
                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    double x = pointIndex < item.XValues.Count && item.XValues[pointIndex].HasValue
                        ? item.XValues[pointIndex]!.Value
                        : pointIndex;
                    double? y = pointIndex < item.Values.Count ? item.Values[pointIndex] : null;
                    if (!double.IsNaN(x) && !double.IsInfinity(x) &&
                        y.HasValue && !double.IsNaN(y.Value) && !double.IsInfinity(y.Value))
                    {
                        xValues.Add(x);
                        yValues.Add(y.Value);
                    }
                }
            }

            if (xValues.Count == 0 || yValues.Count == 0)
            {
                return null;
            }

            List<XElement> axes = chartDocument?.Descendants()
                .Where(e => e.Name.LocalName == "valAx")
                .ToList() ?? new List<XElement>();
            XElement? xAxis = axes.FirstOrDefault(axis =>
                axis.Elements().FirstOrDefault(e => e.Name.LocalName == "axPos")?.Attribute("val")?.Value is "b" or "t") ??
                axes.FirstOrDefault();
            XElement? yAxis = axes.FirstOrDefault(axis =>
                axis.Elements().FirstOrDefault(e => e.Name.LocalName == "axPos")?.Attribute("val")?.Value is "l" or "r") ??
                axes.Skip(1).FirstOrDefault();

            double xMinimum = ReadWorkbookChartAxisLimit(xAxis, "min") ?? xValues.Min();
            double xMaximum = ReadWorkbookChartAxisLimit(xAxis, "max") ?? xValues.Max();
            double yMinimum = ReadWorkbookChartAxisLimit(yAxis, "min") ?? Math.Min(0, yValues.Min());
            double yMaximum = ReadWorkbookChartAxisLimit(yAxis, "max") ?? Math.Max(0, yValues.Max());
            if (xMaximum <= xMinimum)
            {
                xMaximum = xMinimum + Math.Max(1, Math.Abs(xMinimum) * .1);
            }

            if (yMaximum <= yMinimum)
            {
                yMaximum = yMinimum + Math.Max(1, Math.Abs(yMinimum) * .1);
            }

            const double svgHeight = 520;
            const double plotLeft = 78;
            const double plotTop = 62;
            const double plotWidth = 832;
            const double plotHeight = 354;
            string xFormatCode = xAxis?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "numFmt")
                ?.Attribute("formatCode")?.Value ?? string.Empty;
            string scatterStyle = chart.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "scatterStyle")
                ?.Attribute("val")?.Value ?? "lineMarker";
            bool showLine = !scatterStyle.Equals("marker", StringComparison.OrdinalIgnoreCase);
            bool showMarkers = scatterStyle.Equals("marker", StringComparison.OrdinalIgnoreCase) ||
                scatterStyle.Equals("lineMarker", StringComparison.OrdinalIgnoreCase) ||
                scatterStyle.Equals("smoothMarker", StringComparison.OrdinalIgnoreCase);
            string title = ReadWorkbookChartTitle(chartDocument);
            string[] palette = { "#2864DC", "#16A46C", "#7656D6", "#D97706", "#DC3E42", "#0891B2" };
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 520\" role=\"img\" aria-label=\"")
                .Append(Html(title))
                .Append("\"><rect width=\"960\" height=\"520\" fill=\"#FFFFFF\"/>");
            if (!string.IsNullOrWhiteSpace(title))
            {
                svg.Append("<text x=\"480\" y=\"30\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"20\" font-weight=\"600\" fill=\"#111827\">")
                    .Append(Html(title))
                    .Append("</text>");
            }

            for (int gridIndex = 0; gridIndex <= 5; gridIndex++)
            {
                double ratio = gridIndex / 5.0;
                double y = plotTop + (ratio * plotHeight);
                double value = yMaximum - (ratio * (yMaximum - yMinimum));
                svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                    .Append(FormatInvariant(y)).Append("\" x2=\"")
                    .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                    .Append(FormatInvariant(y)).Append("\" stroke=\"#D9E0EA\" stroke-width=\"1\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(plotLeft - 10)).Append("\" y=\"")
                    .Append(FormatInvariant(y + 4)).Append("\" text-anchor=\"end\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                    .Append(Html(FormatInvariant(value))).Append("</text>");

                double x = plotLeft + (ratio * plotWidth);
                string xLabel = FormatWorkbookChartAxisValue(
                    xMinimum + (ratio * (xMaximum - xMinimum)),
                    xFormatCode,
                    use1904Dates);
                svg.Append("<line x1=\"").Append(FormatInvariant(x)).Append("\" y1=\"")
                    .Append(FormatInvariant(plotTop)).Append("\" x2=\"")
                    .Append(FormatInvariant(x)).Append("\" y2=\"")
                    .Append(FormatInvariant(plotTop + plotHeight)).Append("\" stroke=\"#EEF2F7\" stroke-width=\"1\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(x)).Append("\" y=\"")
                    .Append(FormatInvariant(plotTop + plotHeight + 22)).Append("\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                    .Append(Html(TrimWorkbookChartLabel(xLabel, 16))).Append("</text>");
            }

            svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(plotTop + plotHeight)).Append("\" x2=\"")
                .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                .Append(FormatInvariant(plotTop + plotHeight)).Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>")
                .Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(plotTop)).Append("\" x2=\"").Append(FormatInvariant(plotLeft))
                .Append("\" y2=\"").Append(FormatInvariant(plotTop + plotHeight))
                .Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>");

            for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                ViewerChartSeries item = series[seriesIndex];
                string color = ReadWorkbookChartSeriesColor(
                    seriesElements[seriesIndex],
                    themeColors) ?? palette[seriesIndex % palette.Length];
                var points = new List<(double X, double Y)>();
                int pointCount = Math.Max(item.XValues.Count, item.Values.Count);
                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    double xValue = pointIndex < item.XValues.Count && item.XValues[pointIndex].HasValue
                        ? item.XValues[pointIndex]!.Value
                        : pointIndex;
                    double? yValue = pointIndex < item.Values.Count ? item.Values[pointIndex] : null;
                    if (!double.IsNaN(xValue) && !double.IsInfinity(xValue) &&
                        yValue.HasValue && !double.IsNaN(yValue.Value) && !double.IsInfinity(yValue.Value))
                    {
                        double x = plotLeft + ((xValue - xMinimum) / (xMaximum - xMinimum) * plotWidth);
                        double y = plotTop + ((yMaximum - yValue.Value) / (yMaximum - yMinimum) * plotHeight);
                        points.Add((Math.Clamp(x, plotLeft, plotLeft + plotWidth), Math.Clamp(y, plotTop, plotTop + plotHeight)));
                    }
                }

                if (showLine && points.Count >= 2)
                {
                    svg.Append("<polyline fill=\"none\" stroke=\"").Append(Html(color))
                        .Append("\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"");
                    foreach ((double x, double y) in points)
                    {
                        svg.Append(FormatInvariant(x)).Append(',').Append(FormatInvariant(y)).Append(' ');
                    }
                    svg.Append("\"/>");
                }

                if (showMarkers)
                {
                    foreach ((double x, double y) in points)
                    {
                        svg.Append("<circle cx=\"").Append(FormatInvariant(x)).Append("\" cy=\"")
                            .Append(FormatInvariant(y)).Append("\" r=\"3.5\" fill=\"#FFFFFF\" stroke=\"")
                            .Append(Html(color)).Append("\" stroke-width=\"2\"/>");
                    }
                }

                double legendX = plotLeft + (seriesIndex * 150);
                double legendY = svgHeight - 42;
                string label = string.IsNullOrWhiteSpace(item.Name)
                    ? (seriesIndex + 1).ToString(CultureInfo.InvariantCulture)
                    : item.Name.Trim();
                svg.Append("<line x1=\"").Append(FormatInvariant(legendX)).Append("\" y1=\"")
                    .Append(FormatInvariant(legendY - 4)).Append("\" x2=\"")
                    .Append(FormatInvariant(legendX + 16)).Append("\" y2=\"")
                    .Append(FormatInvariant(legendY - 4)).Append("\" stroke=\"")
                    .Append(Html(color)).Append("\" stroke-width=\"2.5\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(legendX + 22)).Append("\" y=\"")
                    .Append(FormatInvariant(legendY)).Append("\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"12\" fill=\"#334155\">")
                    .Append(Html(TrimWorkbookChartLabel(label, 26))).Append("</text>");
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        private static double? ReadWorkbookChartAxisLimit(XElement? axis, string name)
        {
            string? text = axis?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "scaling")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == name)
                ?.Attribute("val")?.Value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : null;
        }

        private static string FormatWorkbookChartAxisValue(
            double value,
            string formatCode,
            bool use1904Dates)
        {
            if (IsWorkbookDateFormat(formatCode) &&
                TryConvertExcelSerialDate(value, use1904Dates, out DateTime dateTime))
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return FormatInvariant(value);
        }

        private static string? ReadWorkbookChartSeriesColor(
            XElement series,
            IReadOnlyList<string> themeColors)
        {
            XElement? color = series.Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr");
            if (color == null)
            {
                return null;
            }

            string value = color.Attribute("val")?.Value ?? color.Attribute("lastClr")?.Value ?? string.Empty;
            if (color.Name.LocalName == "srgbClr" && Regex.IsMatch(value, "^[0-9A-Fa-f]{6,8}$"))
            {
                return "#" + (value.Length == 8 ? value.Substring(2) : value);
            }

            if (color.Name.LocalName == "sysClr" && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            if (color.Name.LocalName == "schemeClr")
            {
                int themeIndex = value.ToLowerInvariant() switch
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
                    "folhlink" => 11,
                    _ => -1
                };
                if (themeIndex >= 0 && themeIndex < themeColors.Count)
                {
                    return themeColors[themeIndex];
                }
            }

            return null;
        }

        private static ViewerChartSeries ReadWorkbookChartSeries(XElement series)
        {
            XElement? categorySource = series.Elements().FirstOrDefault(e => e.Name.LocalName is "cat" or "xVal");
            XElement? xValueSource = series.Elements().FirstOrDefault(e => e.Name.LocalName == "xVal");
            XElement? valueSource = series.Elements().FirstOrDefault(e => e.Name.LocalName is "val" or "yVal" or "bubbleSize");
            return new ViewerChartSeries
            {
                Name = series.Elements().FirstOrDefault(e => e.Name.LocalName == "tx")?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName is "v" or "t")?.Value ?? string.Empty,
                XValues = ReadWorkbookChartNumberPoints(xValueSource),
                Categories = ReadWorkbookChartTextPoints(categorySource),
                Values = ReadWorkbookChartNumberPoints(valueSource)
            };
        }

        private static IReadOnlyList<string> ReadWorkbookChartTextPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<string>();
            }

            var points = new SortedDictionary<int, string>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(point.Attribute("idx")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int readIndex)
                    ? readIndex
                    : fallbackIndex;
                points[index] = point.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string value = source.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                return string.IsNullOrEmpty(value) ? Array.Empty<string>() : new[] { value };
            }

            int count = Math.Max(points.Keys.Max() + 1, TryReadInt(source.Descendants().FirstOrDefault(e => e.Name.LocalName == "ptCount") ?? source, "val"));
            return Enumerable.Range(0, count)
                .Select(index => points.TryGetValue(index, out string? value) ? value : string.Empty)
                .ToList();
        }

        private static IReadOnlyList<double?> ReadWorkbookChartNumberPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<double?>();
            }

            var points = new SortedDictionary<int, double?>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(point.Attribute("idx")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int readIndex)
                    ? readIndex
                    : fallbackIndex;
                string? text = point.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                points[index] = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? value
                    : null;
                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string? text = source.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? new double?[] { value }
                    : Array.Empty<double?>();
            }

            int count = Math.Max(points.Keys.Max() + 1, TryReadInt(source.Descendants().FirstOrDefault(e => e.Name.LocalName == "ptCount") ?? source, "val"));
            return Enumerable.Range(0, count)
                .Select(index => points.TryGetValue(index, out double? value) ? value : null)
                .ToList();
        }

        internal static string ReadWorkbookChartTitle(XDocument? chartDocument)
        {
            XElement? title = chartDocument?.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
            if (title == null)
            {
                return string.Empty;
            }

            string text = string.Concat(title.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }

            string cachedTitle = title.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "v")
                ?.Value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cachedTitle))
            {
                return cachedTitle;
            }

            XElement? firstSeriesTitle = chartDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ser")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "tx");
            return firstSeriesTitle?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "v" or "t")
                ?.Value?.Trim() ?? string.Empty;
        }

        private static string TrimWorkbookChartLabel(string value, int maxLength = 18)
        {
            string text = value ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }

        private static string FormatInvariant(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
