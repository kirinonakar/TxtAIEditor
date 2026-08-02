using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationRenderingUtilities
    {
        public static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        public static string FormatInvariant(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static bool TryReadLong(XElement element, string attributeName, out long value)
        {
            value = 0;
            return long.TryParse(
                element.Attribute(attributeName)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        public static int TryReadInt(XElement element, string attributeName)
        {
            return int.TryParse(
                element.Attribute(attributeName)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : -1;
        }

        public static string Pixels(long value, long total, double baseSizePx)
        {
            return FormatInvariant(value / (double)Math.Max(1, total) * baseSizePx) + "px";
        }

        public static string Pixels(double value, long total, double baseSizePx)
        {
            return FormatInvariant(value / Math.Max(1.0, total) * baseSizePx) + "px";
        }

        public static double PointsToPixels(double points, long slideWidth, double baseWidthPx)
        {
            double pixelsPerInch = baseWidthPx / (slideWidth / 914400.0);
            return points / 72.0 * pixelsPerInch;
        }

        public static string? ReadPresentationColor(
            XElement? parent,
            IReadOnlyList<string> themeColors)
        {
            if (parent == null)
            {
                return null;
            }

            XElement? colorContainer = parent.Name.LocalName is
                "solidFill" or "srgbClr" or "schemeClr" or "sysClr" or "prstClr"
                    ? parent
                    : parent.Descendants().FirstOrDefault(e => e.Name.LocalName == "solidFill");

            if (colorContainer == null)
            {
                colorContainer = parent;
            }

            XElement? colorElement = colorContainer.Name.LocalName is
                "srgbClr" or "schemeClr" or "sysClr" or "prstClr"
                    ? colorContainer
                    : colorContainer.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr");
            string? color = ReadPresentationColorValue(colorElement, themeColors);

            if (string.IsNullOrWhiteSpace(color))
            {
                return null;
            }

            string transformedColor = ApplyColorTransforms(color, colorElement ?? colorContainer);
            int alpha = ReadAlphaTransform(colorElement ?? colorContainer);
            if (alpha == 0)
            {
                return null;
            }

            if (alpha < 100000 && Regex.IsMatch(transformedColor, "^#[0-9A-Fa-f]{6}$"))
            {
                int red = Convert.ToInt32(transformedColor.Substring(1, 2), 16);
                int green = Convert.ToInt32(transformedColor.Substring(3, 2), 16);
                int blue = Convert.ToInt32(transformedColor.Substring(5, 2), 16);
                return "rgba(" +
                    red.ToString(CultureInfo.InvariantCulture) + "," +
                    green.ToString(CultureInfo.InvariantCulture) + "," +
                    blue.ToString(CultureInfo.InvariantCulture) + "," +
                    FormatInvariant(alpha / 100000.0) + ")";
            }

            return transformedColor;
        }

        public static XElement? FindPresentationFill(XElement? parent)
        {
            if (parent == null)
            {
                return null;
            }

            if (parent.Name.LocalName is
                "solidFill" or "gradFill" or "pattFill" or "blipFill" or "fillRef" or "noFill")
            {
                return parent;
            }

            return parent.Descendants().FirstOrDefault(e =>
                e.Name.LocalName is
                    "solidFill" or "gradFill" or "pattFill" or "blipFill" or "fillRef" or "noFill");
        }

        public static string? ReadPresentationFill(
            XElement? parent,
            IReadOnlyList<string> themeColors)
        {
            XElement? fill = FindPresentationFill(parent);
            if (fill == null || fill.Name.LocalName is "noFill" or "blipFill")
            {
                return null;
            }

            if (fill.Name.LocalName == "gradFill")
            {
                return ReadGradientFill(fill, themeColors);
            }

            return ReadPresentationColor(fill, themeColors);
        }

        private static string? ReadGradientFill(
            XElement gradientFill,
            IReadOnlyList<string> themeColors)
        {
            XElement? stopList = gradientFill.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "gsLst");
            if (stopList == null)
            {
                return null;
            }

            var stops = new List<(long Position, string Color)>();
            foreach (XElement stop in stopList.Elements().Where(e => e.Name.LocalName == "gs"))
            {
                string? color = ReadPresentationColor(stop, themeColors);
                if (string.IsNullOrWhiteSpace(color))
                {
                    continue;
                }

                long position = TryReadLong(stop, "pos", out long readPosition)
                    ? Math.Clamp(readPosition, 0, 100000)
                    : stops.Count == 0 ? 0 : 100000;
                stops.Add((position, color));
            }

            if (stops.Count == 0)
            {
                return null;
            }

            stops.Sort((left, right) => left.Position.CompareTo(right.Position));
            XElement? path = gradientFill.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "path");
            string gradientName = path == null ? "linear-gradient" : "radial-gradient";
            var builder = new StringBuilder(gradientName + "(");
            if (path == null)
            {
                long angleValue = TryReadLong(
                    gradientFill.Elements().FirstOrDefault(e => e.Name.LocalName == "lin") ?? gradientFill,
                    "ang",
                    out long readAngle)
                        ? readAngle
                        : 0;
                double cssAngle = (angleValue / 60000.0 + 90.0) % 360.0;
                if (cssAngle < 0)
                {
                    cssAngle += 360.0;
                }

                builder.Append(FormatInvariant(cssAngle)).Append("deg, ");
            }

            for (int index = 0; index < stops.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                (long position, string color) = stops[index];
                builder.Append(color)
                    .Append(' ')
                    .Append(FormatInvariant(position / 1000.0))
                    .Append('%');
            }

            builder.Append(')');
            return builder.ToString();
        }

        private static string? ReadPresentationColorValue(
            XElement? colorElement,
            IReadOnlyList<string> themeColors)
        {
            if (colorElement == null)
            {
                return null;
            }

            string? value = colorElement.Attribute("val")?.Value;
            if (colorElement.Name.LocalName == "srgbClr" &&
                !string.IsNullOrWhiteSpace(value) &&
                Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            if (colorElement.Name.LocalName == "sysClr")
            {
                value = colorElement.Attribute("lastClr")?.Value;
                return !string.IsNullOrWhiteSpace(value) &&
                    Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                        ? "#" + value
                        : null;
            }

            if (colorElement.Name.LocalName == "schemeClr")
            {
                return ReadPresentationThemeColor(value, themeColors);
            }

            return value?.ToLowerInvariant() switch
            {
                "black" => "#000000",
                "white" => "#ffffff",
                "red" => "#ff0000",
                "green" => "#008000",
                "blue" => "#0000ff",
                "yellow" => "#ffff00",
                "cyan" => "#00ffff",
                "magenta" => "#ff00ff",
                "gray" or "grey" => "#808080",
                "orange" => "#ffa500",
                "purple" => "#800080",
                "brown" => "#a52a2a",
                _ => null
            };
        }

        private static string? ReadPresentationThemeColor(
            string? schemeName,
            IReadOnlyList<string> themeColors)
        {
            if (string.IsNullOrWhiteSpace(schemeName) || themeColors.Count == 0)
            {
                return null;
            }

            int index = schemeName.Trim().ToLowerInvariant() switch
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

            return index >= 0 && index < themeColors.Count ? themeColors[index] : null;
        }

        private static string ApplyColorTransforms(string color, XElement parent)
        {
            if (string.IsNullOrWhiteSpace(color) ||
                !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            {
                return color;
            }

            int red = Convert.ToInt32(color.Substring(1, 2), 16);
            int green = Convert.ToInt32(color.Substring(3, 2), 16);
            int blue = Convert.ToInt32(color.Substring(5, 2), 16);
            double lumMod = ReadPercentageTransform(parent, "lumMod", 100000) / 100000.0;
            double lumOff = ReadPercentageTransform(parent, "lumOff", 0) / 100000.0;
            red = ApplyLumTransform(red, lumMod, lumOff);
            green = ApplyLumTransform(green, lumMod, lumOff);
            blue = ApplyLumTransform(blue, lumMod, lumOff);

            int tint = ReadPercentageTransform(parent, "tint", 0);
            if (tint > 0)
            {
                double tintFactor = Math.Clamp(tint / 100000.0, 0, 1);
                red = (int)Math.Round(red + ((255 - red) * tintFactor));
                green = (int)Math.Round(green + ((255 - green) * tintFactor));
                blue = (int)Math.Round(blue + ((255 - blue) * tintFactor));
            }

            int shade = ReadPercentageTransform(parent, "shade", 100000);
            if (shade < 100000)
            {
                double shadeFactor = Math.Clamp(shade / 100000.0, 0, 1);
                red = (int)Math.Round(red * shadeFactor);
                green = (int)Math.Round(green * shadeFactor);
                blue = (int)Math.Round(blue * shadeFactor);
            }

            return $"#{red:X2}{green:X2}{blue:X2}";
        }

        private static int ReadAlphaTransform(XElement parent)
        {
            int alpha = ReadPercentageTransform(parent, "alpha", 100000);
            int alphaMod = ReadPercentageTransform(parent, "alphaMod", 100000);
            int alphaOff = ReadPercentageTransform(parent, "alphaOff", 0);
            return Math.Clamp(
                (int)Math.Round(alpha * (alphaMod / 100000.0) + alphaOff),
                0,
                100000);
        }

        private static int ReadPercentageTransform(XElement parent, string localName, int fallback)
        {
            XElement? element = parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
            return element != null &&
                int.TryParse(
                    element.Attribute("val")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value)
                ? value
                : fallback;
        }

        private static int ApplyLumTransform(int value, double lumMod, double lumOff)
        {
            return Math.Max(0, Math.Min(255, (int)Math.Round((value * lumMod) + (255 * lumOff))));
        }
    }
}
