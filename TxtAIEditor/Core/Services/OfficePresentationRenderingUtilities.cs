using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

            XElement? solidFill = parent.Name.LocalName == "solidFill"
                ? parent
                : parent.Descendants().FirstOrDefault(e => e.Name.LocalName == "solidFill");
            if (solidFill == null)
            {
                return null;
            }

            XElement? srgb = solidFill.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            string? color = !string.IsNullOrWhiteSpace(value) &&
                Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                    ? "#" + value
                    : null;

            if (string.IsNullOrWhiteSpace(color))
            {
                XElement? scheme = solidFill.Descendants().FirstOrDefault(e => e.Name.LocalName == "schemeClr");
                color = ReadPresentationThemeColor(scheme?.Attribute("val")?.Value, themeColors);
            }

            if (string.IsNullOrWhiteSpace(color))
            {
                return null;
            }

            string transformedColor = ApplyColorTransforms(color, solidFill);
            int alpha = Math.Clamp(ReadPercentageTransform(solidFill, "alpha", 100000), 0, 100000);
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

        private static string? ReadPresentationThemeColor(
            string? schemeName,
            IReadOnlyList<string> themeColors)
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
            return $"#{red:X2}{green:X2}{blue:X2}";
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
