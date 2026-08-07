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

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWorkbookPackageUtilities
    {
        internal static string? ReadWorkbookColor(XElement? colorElement, IReadOnlyList<string> themeColors)
        {
            if (colorElement == null)
            {
                return null;
            }

            string? rgb = colorElement.Attribute("rgb")?.Value;
            if (!string.IsNullOrWhiteSpace(rgb))
            {
                rgb = rgb.Trim();
                if (rgb.Length == 8)
                {
                    rgb = rgb.Substring(2);
                }

                if (Regex.IsMatch(rgb, "^[0-9A-Fa-f]{6}$"))
                {
                    return "#" + rgb;
                }
            }

            if (int.TryParse(colorElement.Attribute("indexed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexed))
            {
                return IndexedWorkbookColor(indexed);
            }

            if (int.TryParse(colorElement.Attribute("theme")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int themeIndex) &&
                themeIndex >= 0 &&
                themeIndex < themeColors.Count)
            {
                double tint = 0;
                _ = double.TryParse(colorElement.Attribute("tint")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tint);
                return ApplyTint(themeColors[themeIndex], tint);
            }

            return null;
        }

        internal static string? IndexedWorkbookColor(int index)
        {
            string[] colors =
            {
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#808080",
                "#9999FF", "#993366", "#FFFFCC", "#CCFFFF", "#660066", "#FF8080", "#0066CC", "#CCCCFF",
                "#000080", "#FF00FF", "#FFFF00", "#00FFFF", "#800080", "#800000", "#008080", "#0000FF",
                "#00CCFF", "#CCFFFF", "#CCFFCC", "#FFFF99", "#99CCFF", "#FF99CC", "#CC99FF", "#FFCC99",
                "#3366FF", "#33CCCC", "#99CC00", "#FFCC00", "#FF9900", "#FF6600", "#666699", "#969696",
                "#003366", "#339966", "#003300", "#333300", "#993300", "#993366", "#333399", "#333333"
            };

            return index >= 0 && index < colors.Length ? colors[index] : null;
        }

        internal static string ApplyTint(string hex, double tint)
        {
            if (string.IsNullOrEmpty(hex) || !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
            {
                return hex ?? "#000000";
            }

            string normalized = hex;
            int r = Convert.ToInt32(normalized.Substring(1, 2), 16);
            int g = Convert.ToInt32(normalized.Substring(3, 2), 16);
            int b = Convert.ToInt32(normalized.Substring(5, 2), 16);
            r = ApplyTintComponent(r, tint);
            g = ApplyTintComponent(g, tint);
            b = ApplyTintComponent(b, tint);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        internal static int ApplyTintComponent(int value, double tint)
        {
            double adjusted = tint < 0
                ? value * (1 + tint)
                : value + (255 - value) * tint;
            return Math.Max(0, Math.Min(255, (int)Math.Round(adjusted)));
        }

        internal static int GetCellColumnIndex(XElement cell)
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return 0;
            }

            int column = 0;
            foreach (char ch in reference)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    column = (column * 26) + (ch - 'A' + 1);
                    continue;
                }

                if (ch >= 'a' && ch <= 'z')
                {
                    column = (column * 26) + (ch - 'a' + 1);
                    continue;
                }

                break;
            }

            return column;
        }

        internal static int TryReadInt(XElement element, string attributeName)
        {
            return int.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : -1;
        }

        internal static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        internal static string NormalizeZipPath(string basePath, string target)
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


        internal static async Task<ZipArchive> OpenArchiveAsync(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        internal static async Task<XDocument?> TryLoadXmlEntryAsync(ZipArchive archive, string path)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path);
            return entry == null ? null : await LoadXmlEntryAsync(entry).ConfigureAwait(false);
        }

        internal static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() => XDocument.Load(stream)).ConfigureAwait(false);
        }

        internal static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
