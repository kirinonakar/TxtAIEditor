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

            XElement? colorElement = parent.Name.LocalName is
                "srgbClr" or "schemeClr" or "sysClr" or "prstClr"
                    ? parent
                    : FindPresentationColorElement(parent);
            string? color = ReadPresentationColorValue(colorElement, themeColors);

            if (string.IsNullOrWhiteSpace(color))
            {
                return null;
            }

            string transformedColor = ApplyColorTransforms(color, colorElement ?? parent);
            int alpha = ReadAlphaTransform(colorElement ?? parent);
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

        private static XElement? FindPresentationColorElement(XElement parent)
        {
            if (parent.Name.LocalName == "solidFill")
            {
                return parent.Elements().FirstOrDefault(IsPresentationColorElement);
            }

            XElement? directSolidFill = parent.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "solidFill");
            if (directSolidFill != null)
            {
                return directSolidFill.Elements()
                    .FirstOrDefault(IsPresentationColorElement);
            }

            XElement? directColor = parent.Elements()
                .FirstOrDefault(IsPresentationColorElement);
            if (directColor != null)
            {
                return directColor;
            }

            XElement? referenceColor = parent.Elements()
                .Where(e => e.Name.LocalName is
                    "fontRef" or "lnRef" or "fillRef" or "effectRef")
                .SelectMany(e => e.Elements())
                .FirstOrDefault(IsPresentationColorElement);
            if (referenceColor != null)
            {
                return referenceColor;
            }

            XElement? nestedSolidFill = parent.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "solidFill");
            return nestedSolidFill?.Elements()
                .FirstOrDefault(IsPresentationColorElement);
        }

        private static bool IsPresentationColorElement(XElement element)
        {
            return element.Name.LocalName is
                "srgbClr" or "schemeClr" or "sysClr" or "prstClr";
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

            double red = Convert.ToInt32(color.Substring(1, 2), 16) / 255.0;
            double green = Convert.ToInt32(color.Substring(3, 2), 16) / 255.0;
            double blue = Convert.ToInt32(color.Substring(5, 2), 16) / 255.0;

            foreach (XElement transform in parent.Elements())
            {
                switch (transform.Name.LocalName)
                {
                    case "hue":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            ReadAngleTransform(transform),
                            saturation,
                            luminance);
                        break;
                    }
                    case "hueOff":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            NormalizeHue(hue + ReadAngleTransform(transform)),
                            saturation,
                            luminance);
                        break;
                    }
                    case "hueMod":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            NormalizeHue(hue * ReadPercentageTransform(transform, 1)),
                            saturation,
                            luminance);
                        break;
                    }
                    case "sat":
                    {
                        (double hue, _, double luminance) = RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            ReadPercentageTransform(transform, 0),
                            luminance);
                        break;
                    }
                    case "satOff":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation + ReadPercentageTransform(transform, 0),
                            luminance);
                        break;
                    }
                    case "satMod":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation * ReadPercentageTransform(transform, 1),
                            luminance);
                        break;
                    }
                    case "lum":
                    {
                        (double hue, double saturation, _) = RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation,
                            ReadPercentageTransform(transform, 0));
                        break;
                    }
                    case "lumOff":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation,
                            luminance + ReadPercentageTransform(transform, 0));
                        break;
                    }
                    case "lumMod":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation,
                            luminance * ReadPercentageTransform(transform, 1));
                        break;
                    }
                    case "tint":
                    {
                        double factor = Math.Clamp(
                            ReadPercentageTransform(transform, 0),
                            0,
                            1);
                        red += (1 - red) * factor;
                        green += (1 - green) * factor;
                        blue += (1 - blue) * factor;
                        break;
                    }
                    case "shade":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            hue,
                            saturation,
                            luminance * Math.Clamp(
                                ReadPercentageTransform(transform, 1),
                                0,
                                1));
                        break;
                    }
                    case "red":
                        red = ReadPercentageTransform(transform, 0);
                        break;
                    case "redOff":
                        red += ReadPercentageTransform(transform, 0);
                        break;
                    case "redMod":
                        red *= ReadPercentageTransform(transform, 1);
                        break;
                    case "green":
                        green = ReadPercentageTransform(transform, 0);
                        break;
                    case "greenOff":
                        green += ReadPercentageTransform(transform, 0);
                        break;
                    case "greenMod":
                        green *= ReadPercentageTransform(transform, 1);
                        break;
                    case "blue":
                        blue = ReadPercentageTransform(transform, 0);
                        break;
                    case "blueOff":
                        blue += ReadPercentageTransform(transform, 0);
                        break;
                    case "blueMod":
                        blue *= ReadPercentageTransform(transform, 1);
                        break;
                    case "comp":
                    {
                        (double hue, double saturation, double luminance) =
                            RgbToHsl(red, green, blue);
                        (red, green, blue) = HslToRgb(
                            NormalizeHue(hue + 180),
                            saturation,
                            luminance);
                        break;
                    }
                    case "inv":
                        red = 1 - red;
                        green = 1 - green;
                        blue = 1 - blue;
                        break;
                    case "gray":
                    {
                        double gray = (red * .299) + (green * .587) + (blue * .114);
                        red = gray;
                        green = gray;
                        blue = gray;
                        break;
                    }
                }

                red = Math.Clamp(red, 0, 1);
                green = Math.Clamp(green, 0, 1);
                blue = Math.Clamp(blue, 0, 1);
            }

            return $"#{ToColorByte(red):X2}{ToColorByte(green):X2}{ToColorByte(blue):X2}";
        }

        private static int ReadAlphaTransform(XElement parent)
        {
            double alpha = 1;
            foreach (XElement transform in parent.Elements())
            {
                switch (transform.Name.LocalName)
                {
                    case "alpha":
                        alpha = ReadPercentageTransform(transform, 1);
                        break;
                    case "alphaMod":
                        alpha *= ReadPercentageTransform(transform, 1);
                        break;
                    case "alphaOff":
                        alpha += ReadPercentageTransform(transform, 0);
                        break;
                }
            }

            return Math.Clamp((int)Math.Round(alpha * 100000), 0, 100000);
        }

        private static double ReadPercentageTransform(XElement transform, double fallback)
        {
            return double.TryParse(
                    transform.Attribute("val")?.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value)
                ? value / 100000.0
                : fallback;
        }

        private static double ReadAngleTransform(XElement transform)
        {
            return NormalizeHue(
                double.TryParse(
                    transform.Attribute("val")?.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value)
                    ? value / 60000.0
                    : 0);
        }

        private static (double Hue, double Saturation, double Luminance) RgbToHsl(
            double red,
            double green,
            double blue)
        {
            double max = Math.Max(red, Math.Max(green, blue));
            double min = Math.Min(red, Math.Min(green, blue));
            double delta = max - min;
            double luminance = (max + min) / 2;
            if (delta < double.Epsilon)
            {
                return (0, 0, luminance);
            }

            double saturation = luminance > .5
                ? delta / (2 - max - min)
                : delta / (max + min);
            double hue = max == red
                ? ((green - blue) / delta) % 6
                : max == green
                    ? ((blue - red) / delta) + 2
                    : ((red - green) / delta) + 4;
            return (NormalizeHue(hue * 60), saturation, luminance);
        }

        private static (double Red, double Green, double Blue) HslToRgb(
            double hue,
            double saturation,
            double luminance)
        {
            hue = NormalizeHue(hue) / 360.0;
            saturation = Math.Clamp(saturation, 0, 1);
            luminance = Math.Clamp(luminance, 0, 1);
            if (saturation < double.Epsilon)
            {
                return (luminance, luminance, luminance);
            }

            double q = luminance < .5
                ? luminance * (1 + saturation)
                : luminance + saturation - (luminance * saturation);
            double p = (2 * luminance) - q;
            return (
                HueToRgb(p, q, hue + (1.0 / 3)),
                HueToRgb(p, q, hue),
                HueToRgb(p, q, hue - (1.0 / 3)));
        }

        private static double HueToRgb(double p, double q, double hue)
        {
            if (hue < 0)
            {
                hue += 1;
            }

            if (hue > 1)
            {
                hue -= 1;
            }

            if (hue < 1.0 / 6)
            {
                return p + ((q - p) * 6 * hue);
            }

            if (hue < .5)
            {
                return q;
            }

            if (hue < 2.0 / 3)
            {
                return p + ((q - p) * ((2.0 / 3) - hue) * 6);
            }

            return p;
        }

        private static double NormalizeHue(double hue)
        {
            double normalized = hue % 360;
            return normalized < 0 ? normalized + 360 : normalized;
        }

        private static int ToColorByte(double value)
        {
            return Math.Clamp((int)Math.Round(value * 255), 0, 255);
        }
    }
}
