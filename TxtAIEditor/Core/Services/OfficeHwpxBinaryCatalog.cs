using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;

namespace TxtAIEditor.Core.Services
{
    internal sealed class HwpxBinaryItem
    {
        public string Path { get; init; } = string.Empty;
        public string? MimeType { get; init; }
    }

    internal static class OfficeHwpxBinaryCatalog
    {
        internal static async Task<IReadOnlyDictionary<string, HwpxBinaryItem>> LoadHwpxBinaryItemsAsync(ZipArchive archive)
        {
            var items = new Dictionary<string, HwpxBinaryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument? doc;
                try
                {
                    doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                foreach (XElement element in doc.Descendants().Where(e => e.Name.LocalName == "binItem"))
                {
                    string id = GetAttributeValue(element, "id");
                    string href = GetAttributeValue(element, "href");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
                    {
                        continue;
                    }

                    items[id] = new HwpxBinaryItem
                    {
                        Path = NormalizeHwpxBinaryPath(href),
                        MimeType = GetAttributeValue(element, "media-type")
                    };
                }
            }

            foreach (ZipArchiveEntry entry in archive.Entries.Where(e =>
                e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) &&
                IsSupportedImagePath(e.FullName)))
            {
                string fileName = Path.GetFileName(entry.FullName);
                string stem = Path.GetFileNameWithoutExtension(entry.FullName);
                AddHwpxBinaryItem(items, stem, entry.FullName);
                AddHwpxBinaryItem(items, fileName, entry.FullName);
                AddHwpxBinaryItem(items, entry.FullName, entry.FullName);
            }

            return items;
        }

        private static void AddHwpxBinaryItem(IDictionary<string, HwpxBinaryItem> items, string id, string path)
        {
            if (string.IsNullOrWhiteSpace(id) || items.ContainsKey(id))
            {
                return;
            }

            items[id] = new HwpxBinaryItem
            {
                Path = NormalizeZipPath(string.Empty, path),
                MimeType = GetImageMimeType(path)
            };
        }

        internal static string NormalizeHwpxBinaryPath(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (path.Contains('/', StringComparison.Ordinal))
            {
                return NormalizeZipPath(string.Empty, path);
            }

            return "BinData/" + path;
        }

        private static bool IsSupportedImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value
                ?? string.Empty;
        }
    }
}
