using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeBinaryData;

namespace TxtAIEditor.Core.Services
{
    // Chart substreams store series separately from their links to worksheet data (BRAI).
    internal static class OfficeBinaryWorkbookChart
    {
        private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        internal static XElement? Read(ReadOnlySpan<byte> data, Func<int, bool> referencesCurrentSheet,
            Func<int, int, string?> readCell, CancellationToken cancellationToken)
        {
            var series = new List<Series>();
            Series? current = null;
            string? type = null;
            string? title = null;
            string? inlineText = null;
            int depth = 0;
            int seriesDepth = -1;
            bool horizontal = false;
            bool stacked = false;
            for (int offset = 0; offset < data.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int id = U16(data, offset);
                ReadOnlySpan<byte> record = Slice(data, offset + 4, U16(data, offset + 2));
                offset += 4 + record.Length;
                switch (id)
                {
                    case 0x1003: // Series
                        current = new Series();
                        series.Add(current);
                        seriesDepth = depth;
                        break;
                    case 0x1033: depth++; break;
                    case 0x1034:
                        if (--depth <= seriesDepth)
                        {
                            current = null;
                            seriesDepth = -1;
                        }
                        break;
                    case 0x1051 when current != null && record.Length >= 8: // BRAI
                        if (record[0] <= 2 && record[1] == 2)
                        {
                            current.Sources[record[0]] = ReadReference(record, referencesCurrentSheet, readCell, cancellationToken);
                        }
                        break;
                    case 0x100D when record.Length >= 4: // SeriesText
                        int count = record[2];
                        int stride = (record[3] & 1) != 0 ? 2 : 1;
                        inlineText = XmlText((stride == 2 ? Encoding.Unicode : Encoding.Latin1)
                            .GetString(Slice(record, 4, count * stride)));
                        if (current != null && current.Name == null) current.Name = inlineText;
                        break;
                    case 0x1027 when record.Length >= 2 && U16(record, 0) == 1: title = inlineText; break;
                    case 0x1017 when type == null && record.Length >= 6:
                        type = "barChart";
                        horizontal = (U16(record, 4) & 1) != 0;
                        stacked = (U16(record, 4) & 2) != 0;
                        break;
                    case 0x1018: type ??= "lineChart"; break;
                    case 0x1019: type ??= record.Length >= 4 && U16(record, 2) != 0 ? "doughnutChart" : "pieChart"; break;
                    case 0x101A: type ??= "areaChart"; break;
                    case 0x101B: type ??= "scatterChart"; break;
                }
            }
            if (type == null) return null;
            var chart = new XElement(C + type);
            if (type == "barChart")
            {
                chart.Add(new XElement(C + "barDir", new XAttribute("val", horizontal ? "bar" : "col")),
                    new XElement(C + "grouping", new XAttribute("val", stacked ? "stacked" : "clustered")));
            }
            else if (type == "scatterChart")
            {
                chart.Add(new XElement(C + "scatterStyle", new XAttribute("val", "marker")));
            }

            foreach (Series item in series)
            {
                if (item.Sources[1] is not { Count: > 0 } values ||
                    !values.Any(value => IsNumber(value))) continue;
                var element = new XElement(C + "ser", new XElement(C + "tx",
                    new XElement(C + "v", item.Name ?? item.Sources[0]?.FirstOrDefault() ?? string.Empty)));
                bool scatter = type == "scatterChart";
                if (item.Sources[2] is { Count: > 0 } labels) element.Add(Points(scatter ? "xVal" : "cat", labels, scatter));
                element.Add(Points(scatter ? "yVal" : "val", values, numeric: true));
                chart.Add(element);
            }
            if (chart.Element(C + "ser") == null) return null;
            return new XElement(C + "chartSpace", new XElement(C + "chart",
                title == null ? null : new XElement(C + "title", new XElement(C + "tx", new XElement(C + "v", title))),
                new XElement(C + "plotArea", chart)));
        }

        private static List<string?> ReadReference(ReadOnlySpan<byte> record, Func<int, bool> referencesCurrentSheet,
            Func<int, int, string?> readCell, CancellationToken cancellationToken)
        {
            int length = U16(record, 6);
            ReadOnlySpan<byte> formula = Slice(record, 8, length);
            if (length < 7 || !referencesCurrentSheet(U16(formula, 1))) return [];
            int firstRow = U16(formula, 3);
            int firstCol;
            int lastRow = firstRow;
            int lastCol;
            switch (formula[0] & 0x1F)
            {
                case 0x1A when length == 7: firstCol = lastCol = U16(formula, 5) & 255; break; // PtgRef3d
                case 0x1B when length == 11: // PtgArea3d
                    lastRow = U16(formula, 5);
                    firstCol = U16(formula, 7) & 255;
                    lastCol = U16(formula, 9) & 255;
                    break;
                default: return [];
            }
            var result = new List<string?>();
            for (int row = firstRow; row <= lastRow && result.Count < 4096; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int col = firstCol; col <= lastCol && result.Count < 4096; col++) result.Add(readCell(row, col));
            }
            return result;
        }

        private static XElement Points(string name, List<string?> values, bool numeric) =>
            new(C + name, new XElement(C + (numeric ? "numLit" : "strLit"),
                new XElement(C + "ptCount", new XAttribute("val", values.Count)),
                values.Select((value, index) => value != null && (!numeric || IsNumber(value))
                    ? new XElement(C + "pt", new XAttribute("idx", index), new XElement(C + "v", value)) : null)));

        private static bool IsNumber(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result);

        private sealed class Series
        {
            internal string? Name { get; set; }
            internal List<string?>?[] Sources { get; } = new List<string?>?[3];
        }
    }
}
