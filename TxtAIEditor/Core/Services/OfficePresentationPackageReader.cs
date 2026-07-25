using System;
using System.Collections.Generic;
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

        public static async Task<IReadOnlyList<string>> LoadThemeColorsAsync(ZipArchive archive)
        {
            XDocument? theme = await TryLoadXmlEntryAsync(archive, "ppt/theme/theme1.xml").ConfigureAwait(false);
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
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
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

            return "data:" + mime + ";base64," + Convert.ToBase64String(memory.ToArray());
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
