using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationPresetGeometry
    {
        public static string ReadStyle(XElement shapeProperties)
        {
            XElement? geometry = shapeProperties.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "prstGeom");
            string? preset = geometry?.Attribute("prst")?.Value;
            if (geometry == null)
            {
                return string.Empty;
            }

            XElement? extent = shapeProperties.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "xfrm")?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ext");
            double width = ReadDimension(extent, "cx");
            double height = ReadDimension(extent, "cy");
            double shortSide = Math.Min(width, height);
            double Adjust(string name, double fallback, double maximum) =>
                Math.Clamp(ReadAdjustment(geometry, name, fallback), 0, maximum);

            switch (preset?.ToLowerInvariant())
            {
                case "ellipse":
                    return "border-radius:50%;";
                case "roundrect":
                    double radius = shortSide * Adjust("adj", 16667, 50000) / 100000;
                    return "border-radius:" + Number(radius / width * 100) + "% / " +
                        Number(radius / height * 100) + "%;";
                case "rightarrow":
                    double shoulder = 100 - shortSide / width *
                        Adjust("adj2", 50000, 100000 * width / shortSide) / 1000;
                    double top = 50 - Adjust("adj1", 50000, 100000) / 2000;
                    return Polygon(new[] { (0.0, top), (shoulder, top), (shoulder, 0.0),
                        (100.0, 50.0), (shoulder, 100.0), (shoulder, 100 - top), (0.0, 100 - top) });
                case "pentagon":
                    double horizontal = ReadAdjustment(geometry, "hf", 105146) / 2000;
                    double vertical = ReadAdjustment(geometry, "vf", 110557) / 2000;
                    double dx1 = horizontal * Math.Cos(Math.PI / 10);
                    double dx2 = horizontal * Math.Cos(17 * Math.PI / 10);
                    double y1 = vertical * (1 - Math.Sin(Math.PI / 10));
                    double y2 = vertical * (1 - Math.Sin(17 * Math.PI / 10));
                    return Polygon(new[] { (50 - dx1, y1), (50.0, 0.0),
                        (50 + dx1, y1), (50 + dx2, y2), (50 - dx2, y2) });
                case "donut":
                    double thickness = shortSide * Adjust("adj", 25000, 50000) / 100000;
                    var ring = new List<(double X, double Y)>();
                    AddEllipse(ring, 50, 50, 0, 2 * Math.PI);
                    // A closed inner contour and retraced bridge leave a transparent hole.
                    AddEllipse(ring, 50 - thickness / width * 100,
                        50 - thickness / height * 100, 0, -2 * Math.PI);
                    return Polygon(ring);
                case "pie":
                    double start = Adjust("adj1", 0, 21599999) / 60000 * Math.PI / 180;
                    double end = Adjust("adj2", 16200000, 21599999) / 60000 * Math.PI / 180;
                    // DrawingML angles are radial angles, not ellipse parameters.
                    double startParameter = Math.Atan2(width * Math.Sin(start), height * Math.Cos(start));
                    double endParameter = Math.Atan2(width * Math.Sin(end), height * Math.Cos(end));
                    double sweep = endParameter - startParameter;
                    if (sweep <= 0)
                    {
                        sweep += 2 * Math.PI;
                    }

                    var sector = new List<(double X, double Y)> { (50, 50) };
                    AddEllipse(sector, 50, 50, startParameter, sweep);
                    return Polygon(sector);
                default:
                    return string.Empty;
            }
        }

        private static void AddEllipse(List<(double X, double Y)> points,
            double radiusX, double radiusY, double start, double sweep)
        {
            // Percentage coordinates keep the outline correct at every slide zoom level.
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * 64 / Math.PI));
            for (int i = 0; i <= segments; i++)
            {
                double angle = start + sweep * i / segments;
                points.Add((50 + radiusX * Math.Cos(angle), 50 + radiusY * Math.Sin(angle)));
            }
        }

        private static string Polygon(IEnumerable<(double X, double Y)> points) =>
            "clip-path:polygon(evenodd," + string.Join(",", points.Select(point =>
                Number(point.X) + "% " + Number(point.Y) + "%")) + ");";

        private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        private static double ReadDimension(XElement? extent, string name) =>
            double.TryParse(extent?.Attribute(name)?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value > 0
                ? value : 1;

        private static double ReadAdjustment(XElement geometry, string name, double fallback)
        {
            string? formula = geometry.Elements().FirstOrDefault(e => e.Name.LocalName == "avLst")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "gd" &&
                    e.Attribute("name")?.Value == name)?.Attribute("fmla")?.Value;
            string[]? parts = formula?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts is { Length: 2 } && parts[0] == "val" &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                double.IsFinite(value) ? value : fallback;
        }
    }
}
