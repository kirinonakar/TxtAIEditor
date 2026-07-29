using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxStyleCatalog
    {
        internal static string GetHwpxParagraphStyle(
            XElement paragraph,
            IReadOnlyDictionary<string, string> paragraphStyles)
        {
            string styleId = GetAttributeValue(paragraph, "paraPrIDRef");
            return paragraphStyles.TryGetValue(styleId, out string? style) ? style : string.Empty;
        }

        internal static async Task<IReadOnlyDictionary<string, string>> LoadHwpxParagraphStylesAsync(ZipArchive archive)
        {
            XDocument? header = await TryLoadXmlEntryAsync(archive, "Contents/header.xml").ConfigureAwait(false);
            if (header == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement paragraphProperties in header.Descendants().Where(e => e.Name.LocalName == "paraPr"))
            {
                string id = GetAttributeValue(paragraphProperties, "id");
                var declarations = new List<string>();
                XElement? alignment = paragraphProperties.Elements().FirstOrDefault(e => e.Name.LocalName == "align");
                string horizontal = GetAttributeValue(alignment, "horizontal").ToUpperInvariant();
                string alignmentStyle = horizontal switch
                {
                    "RIGHT" => "text-align:right",
                    "CENTER" => "text-align:center",
                    "JUSTIFY" => "text-align:justify",
                    "DISTRIBUTE" or "DISTRIBUTE_SPACE" => "text-align:justify;text-align-last:justify",
                    "LEFT" => "text-align:left",
                    _ => string.Empty
                };
                if (!string.IsNullOrWhiteSpace(alignmentStyle))
                {
                    declarations.Add(alignmentStyle);
                }

                XElement? lineSpacing = paragraphProperties.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "lineSpacing");
                string lineSpacingType = GetAttributeValue(lineSpacing, "type");
                if (lineSpacingType.Equals("PERCENT", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(
                        GetAttributeValue(lineSpacing, "value"),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double lineSpacingPercent) &&
                    lineSpacingPercent > 0)
                {
                    declarations.Add("line-height:" + CssNumber(lineSpacingPercent / 100.0));
                }

                if (!string.IsNullOrWhiteSpace(id) && declarations.Count > 0)
                {
                    styles[id] = string.Join(';', declarations);
                }
            }

            return styles;
        }

        internal static async Task<IReadOnlyDictionary<string, string>> LoadHwpxBorderFillStylesAsync(ZipArchive archive)
        {
            XDocument? header = await TryLoadXmlEntryAsync(archive, "Contents/header.xml").ConfigureAwait(false);
            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (header == null)
            {
                return styles;
            }

            var edgeNames = new HashSet<string>(
                new[] { "leftBorder", "rightBorder", "topBorder", "bottomBorder" },
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement borderFill in header.Descendants().Where(e => e.Name.LocalName == "borderFill"))
            {
                string id = GetAttributeValue(borderFill, "id");
                var declarations = new List<string>();
                foreach ((string elementName, string cssSide) in new[]
                {
                    ("leftBorder", "left"),
                    ("rightBorder", "right"),
                    ("topBorder", "top"),
                    ("bottomBorder", "bottom")
                })
                {
                    XElement? edge = borderFill.Elements().FirstOrDefault(e =>
                        edgeNames.Contains(e.Name.LocalName) &&
                        e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
                    declarations.Add(BuildHwpxBorderDeclaration(cssSide, edge));
                }

                XElement? windowBrush = borderFill
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "winBrush");
                string faceColor = GetAttributeValue(windowBrush, "faceColor");
                if (IsCssColor(faceColor))
                {
                    declarations.Add("background-color:" + faceColor);
                }

                if (!string.IsNullOrWhiteSpace(id) && declarations.Count > 0)
                {
                    styles[id] = string.Join(';', declarations);
                }
            }

            return styles;
        }

        private static string BuildHwpxBorderDeclaration(string cssSide, XElement? edge)
        {
            string type = GetAttributeValue(edge, "type").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(type) || type == "NONE")
            {
                return "border-" + cssSide + ":none";
            }

            string cssStyle = type switch
            {
                "DOT" => "dotted",
                "DASH" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH" => "dashed",
                "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM" => "double",
                _ => "solid"
            };
            string width = NormalizeHwpxBorderWidth(GetAttributeValue(edge, "width"));
            string color = GetAttributeValue(edge, "color");
            if (!IsCssColor(color))
            {
                color = "#000000";
            }

            return "border-" + cssSide + ':' + width + ' ' + cssStyle + ' ' + color;
        }

        private static string NormalizeHwpxBorderWidth(string value)
        {
            Match match = Regex.Match(value ?? string.Empty, @"^\s*(\d+(?:\.\d+)?)\s*(mm|cm|pt|px)\s*$", RegexOptions.IgnoreCase);
            return match.Success
                ? match.Groups[1].Value + match.Groups[2].Value.ToLowerInvariant()
                : "0.12mm";
        }

        internal static async Task<IReadOnlyDictionary<string, string>> LoadHwpxCharacterStylesAsync(ZipArchive archive)
        {
            XDocument? header = await TryLoadXmlEntryAsync(archive, "Contents/header.xml").ConfigureAwait(false);
            if (header == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            IReadOnlyDictionary<string, string> fontNames = LoadHwpxFontNames(header);
            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in header.Descendants().Where(e => e.Name.LocalName == "charPr"))
            {
                string id = GetAttributeValue(element, "id");
                string style = BuildHwpxCharacterStyle(element, fontNames);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(style))
                {
                    styles[id] = style;
                }
            }

            return styles;
        }

        private static IReadOnlyDictionary<string, string> LoadHwpxFontNames(XDocument header)
        {
            XElement? fontFace = header.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "fontface" &&
                    GetAttributeValue(e, "lang").Equals("HANGUL", StringComparison.OrdinalIgnoreCase)) ??
                header.Descendants().FirstOrDefault(e => e.Name.LocalName == "fontface");

            if (fontFace == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return fontFace.Elements()
                .Where(e => e.Name.LocalName == "font")
                .Select(e => new
                {
                    Id = GetAttributeValue(e, "id"),
                    Face = GetAttributeValue(e, "face")
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Face))
                .ToDictionary(x => x.Id, x => x.Face, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildHwpxCharacterStyle(XElement charPr, IReadOnlyDictionary<string, string> fontNames)
        {
            var styles = new List<string>();

            string textColor = GetAttributeValue(charPr, "textColor");
            if (IsCssColor(textColor) && !IsDefaultTextColor(textColor))
            {
                styles.Add("color:" + textColor);
            }

            string shadeColor = GetAttributeValue(charPr, "shadeColor");
            if (IsCssColor(shadeColor) && !IsDefaultShadeColor(shadeColor))
            {
                styles.Add("background-color:" + shadeColor);
            }

            if (int.TryParse(GetAttributeValue(charPr, "height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) &&
                height > 0)
            {
                styles.Add("font-size:" + (height / 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "pt");
            }

            XElement? fontRef = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "fontRef");
            string fontId = GetAttributeValue(fontRef, "hangul");
            if (!string.IsNullOrWhiteSpace(fontId) && fontNames.TryGetValue(fontId, out string? face))
            {
                styles.Add("font-family:" + QuoteCssFontFamily(face) + ", \"Malgun Gothic\", sans-serif");
            }

            if (charPr.Elements().Any(e => e.Name.LocalName == "bold"))
            {
                styles.Add("font-weight:700");
            }

            if (charPr.Elements().Any(e => e.Name.LocalName == "italic"))
            {
                styles.Add("font-style:italic");
            }

            var decorations = new List<string>();
            XElement? underline = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "underline");
            if (underline != null && !GetAttributeValue(underline, "type").Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                decorations.Add("underline");
            }

            XElement? strikeout = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "strikeout");
            if (strikeout != null && !GetAttributeValue(strikeout, "shape").Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                decorations.Add("line-through");
            }

            if (decorations.Count > 0)
            {
                styles.Add("text-decoration:" + string.Join(' ', decorations));
            }

            XElement? spacing = charPr.Elements().FirstOrDefault(e => e.Name.LocalName == "spacing");
            if (int.TryParse(GetAttributeValue(spacing, "hangul"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int spacingValue) &&
                spacingValue != 0)
            {
                styles.Add("letter-spacing:" + (spacingValue / 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "em");
            }

            return string.Join(';', styles);
        }

        internal static bool IsCssColor(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, "^#[0-9A-Fa-f]{6}$");
        }

        private static bool IsDefaultTextColor(string value)
        {
            return value.Equals("#000000", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultShadeColor(string value)
        {
            return value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteCssFontFamily(string value)
        {
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value
                ?? string.Empty;
        }

        private static string CssNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
