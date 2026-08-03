using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficePresentationRenderingUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePresentationPackageReader
    {
        private const long DefaultSlideWidthEmu = 9144000;
        private const long DefaultSlideHeightEmu = 5143500;
        private const int MaxMetafileRenderDimension = 4096;
        private static readonly object MetafileRenderLock = new();

        public static async Task<ZipArchive> OpenArchiveAsync(string filePath)
        {
            var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                useAsync: true);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        public static async Task<XDocument?> TryLoadXmlEntryAsync(ZipArchive archive, string path)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path);
            return entry == null ? null : await LoadXmlEntryAsync(entry).ConfigureAwait(false);
        }

        public static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() => XDocument.Load(stream)).ConfigureAwait(false);
        }

        public static XDocument? TryLoadXmlEntry(
            ZipArchive archive,
            string path,
            long maximumLength)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path) ??
                archive.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length <= 0 || entry.Length > maximumLength)
            {
                return null;
            }

            using Stream stream = entry.Open();
            return XDocument.Load(stream);
        }

        public static (long Width, long Height) ReadSlideSize(XDocument presentation)
        {
            XElement? size = presentation.Descendants().FirstOrDefault(e => e.Name.LocalName == "sldSz");
            if (size != null &&
                TryReadLong(size, "cx", out long width) &&
                TryReadLong(size, "cy", out long height) &&
                width > 0 &&
                height > 0)
            {
                return (width, height);
            }

            return (DefaultSlideWidthEmu, DefaultSlideHeightEmu);
        }

        public static Task<IReadOnlyList<string>> LoadThemeColorsAsync(ZipArchive archive)
        {
            return LoadThemeColorsAsync(archive, "ppt/theme/theme1.xml");
        }

        public static async Task<IReadOnlyList<string>> LoadThemeColorsAsync(
            ZipArchive archive,
            string themePath)
        {
            XDocument? theme = await TryLoadXmlEntryAsync(
                archive,
                string.IsNullOrWhiteSpace(themePath)
                    ? "ppt/theme/theme1.xml"
                    : themePath).ConfigureAwait(false);
            XElement? colorScheme = theme?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
            if (colorScheme == null)
            {
                return Array.Empty<string>();
            }

            string[] order =
            {
                "lt1",
                "dk1",
                "lt2",
                "dk2",
                "accent1",
                "accent2",
                "accent3",
                "accent4",
                "accent5",
                "accent6",
                "hlink",
                "folHlink"
            };
            var colors = new List<string>();
            foreach (string name in order)
            {
                XElement? item = colorScheme.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                colors.Add(item == null ? "#000000" : ReadThemeColor(item) ?? "#000000");
            }

            return colors;
        }

        public static async Task<IReadOnlyList<string>> LoadThemeColorsForSlideAsync(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> slideRelationships,
            XDocument? slide = null)
        {
            string? layoutPath = FindRelationshipTarget(slideRelationships, "slideLayouts");
            XDocument? layout = null;
            IReadOnlyDictionary<string, string> layoutRelationships =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(layoutPath))
            {
                layout = await TryLoadXmlEntryAsync(archive, layoutPath)
                    .ConfigureAwait(false);
                layoutRelationships = await LoadRelationshipsAsync(
                    archive,
                    GetRelationshipsPath(layoutPath),
                    Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);
            }

            string? masterPath = FindRelationshipTarget(layoutRelationships, "slideMasters");
            XDocument? master = null;
            IReadOnlyDictionary<string, string> masterRelationships =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(masterPath))
            {
                master = await TryLoadXmlEntryAsync(archive, masterPath)
                    .ConfigureAwait(false);
                masterRelationships = await LoadRelationshipsAsync(
                    archive,
                    GetRelationshipsPath(masterPath),
                    Path.GetDirectoryName(masterPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);
            }

            string? themePath = masterRelationships.Values.FirstOrDefault(path =>
                path.Contains("/theme/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("theme/", StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<string> themeColors = await LoadThemeColorsAsync(
                archive,
                themePath ?? "ppt/theme/theme1.xml").ConfigureAwait(false);
            IReadOnlyDictionary<string, string>? colorMap =
                ReadPresentationColorMap(slide) ??
                ReadPresentationColorMap(layout) ??
                ReadPresentationColorMap(master);
            return ApplyPresentationColorMap(themeColors, colorMap);
        }

        public static async Task<List<string>> ReadSlidePathsAsync(
            ZipArchive archive,
            XDocument presentation)
        {
            IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                archive,
                "ppt/_rels/presentation.xml.rels",
                "ppt").ConfigureAwait(false);
            XNamespace relationshipNamespace =
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            return presentation.Descendants()
                .Where(e => e.Name.LocalName == "sldId")
                .Select(e => e.Attribute(relationshipNamespace + "id")?.Value ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id) && relationships.ContainsKey(id))
                .Select(id => relationships[id])
                .ToList();
        }

        public static async Task<IReadOnlyDictionary<string, string>> LoadRelationshipsAsync(
            ZipArchive archive,
            string relationshipPath,
            string basePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(relationshipPath);
            if (entry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument relationships = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return relationships.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath(basePath, e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    !string.IsNullOrWhiteSpace(item.Target))
                .ToDictionary(
                    item => item.Id,
                    item => item.Target,
                    StringComparer.OrdinalIgnoreCase);
        }

        public static IReadOnlyDictionary<string, string> LoadRelationships(
            ZipArchive archive,
            string relationshipPath,
            string basePath)
        {
            XDocument? relationships = TryLoadXmlEntry(
                archive,
                relationshipPath,
                1_000_000);
            if (relationships == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return relationships.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath(basePath, e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    !string.IsNullOrWhiteSpace(item.Target))
                .ToDictionary(
                    item => item.Id,
                    item => item.Target,
                    StringComparer.OrdinalIgnoreCase);
        }

        public static string GetRelationshipsPath(string partPath)
        {
            string directory = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
            string fileName = Path.GetFileName(partPath);
            return string.IsNullOrEmpty(directory)
                ? "_rels/" + fileName + ".rels"
                : directory + "/_rels/" + fileName + ".rels";
        }

        public static string? TryReadImageDataUri(ZipArchive archive, string imagePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(imagePath) ??
                archive.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName, imagePath, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length <= 0 || entry.Length > 15_000_000)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension is ".wmf" or ".emf")
            {
                byte[]? pngBytes = ConvertMetafileToPngBytes(bytes, imagePath);
                return pngBytes == null || pngBytes.Length == 0
                    ? null
                    : "data:image/png;base64," + Convert.ToBase64String(pngBytes);
            }

            string mime = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };

            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }

        private static byte[]? ConvertMetafileToPngBytes(byte[] metafileBytes, string imagePath)
        {
            try
            {
                lock (MetafileRenderLock)
                {
                    using var source = new MemoryStream(metafileBytes, writable: false);
                    using var metafile = new Metafile(source);

                    int sourceWidth = Math.Max(1, metafile.Width);
                    int sourceHeight = Math.Max(1, metafile.Height);
                    double scale = Math.Min(
                        1.0,
                        MaxMetafileRenderDimension /
                        (double)Math.Max(sourceWidth, sourceHeight));
                    int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                    int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(metafile, new Rectangle(0, 0, width, height));
                    }

                    using var output = new MemoryStream();
                    bitmap.Save(output, ImageFormat.Png);
                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to convert embedded presentation metafile '{imagePath}' to PNG: {ex.Message}");
                return null;
            }
        }

        private static string? FindRelationshipTarget(
            IReadOnlyDictionary<string, string> relationships,
            string partFolder)
        {
            return relationships.Values.FirstOrDefault(path =>
                path.Contains("/" + partFolder + "/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(partFolder + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyDictionary<string, string>? ReadPresentationColorMap(
            XDocument? part)
        {
            if (part == null)
            {
                return null;
            }

            XElement? map = part.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "overrideClrMapping") ??
                part.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrMap");
            if (map == null)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XAttribute attribute in map.Attributes())
            {
                if (attribute.Name.LocalName is
                    "bg1" or "tx1" or "bg2" or "tx2" or
                    "accent1" or "accent2" or "accent3" or "accent4" or
                    "accent5" or "accent6" or "hlink" or "folHlink")
                {
                    result[attribute.Name.LocalName] = attribute.Value;
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static IReadOnlyList<string> ApplyPresentationColorMap(
            IReadOnlyList<string> themeColors,
            IReadOnlyDictionary<string, string>? colorMap)
        {
            if (colorMap == null || themeColors.Count == 0)
            {
                return themeColors;
            }

            string[] semanticNames =
            {
                "bg1",
                "tx1",
                "bg2",
                "tx2",
                "accent1",
                "accent2",
                "accent3",
                "accent4",
                "accent5",
                "accent6",
                "hlink",
                "folHlink"
            };
            var mappedColors = new List<string>(semanticNames.Length);
            foreach (string semanticName in semanticNames)
            {
                string mappedName = colorMap.TryGetValue(semanticName, out string? value) &&
                    !string.IsNullOrWhiteSpace(value)
                    ? value
                    : semanticName;
                int index = ReadThemeColorIndex(mappedName);
                mappedColors.Add(index >= 0 && index < themeColors.Count
                    ? themeColors[index]
                    : "#000000");
            }

            return mappedColors;
        }

        private static int ReadThemeColorIndex(string value)
        {
            return value.Trim().ToLowerInvariant() switch
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
        }

        private static string? ReadThemeColor(XElement element)
        {
            XElement? srgb = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            if (!string.IsNullOrWhiteSpace(value) &&
                Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            XElement? system = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "sysClr");
            value = system?.Attribute("lastClr")?.Value;
            return !string.IsNullOrWhiteSpace(value) &&
                Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                    ? "#" + value
                    : null;
        }

        private static string NormalizeZipPath(string basePath, string target)
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
            foreach (string part in combined.Split(
                new[] { '/' },
                StringSplitOptions.RemoveEmptyEntries))
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
    }
}
