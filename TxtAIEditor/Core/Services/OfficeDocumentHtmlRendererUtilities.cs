using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeDocumentHtmlRendererUtilities
    {
        internal static string BuildDocumentHtml(string title, string content)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(title)}}</title>
<style>
:root {
    color-scheme: light dark;
    --bg: #f4f6f8;
    --paper: #ffffff;
    --text: #111827;
    --muted: #667085;
    --line: #d8dee8;
    --table-head: #f1f4f8;
}
@media (prefers-color-scheme: dark) {
    :root {
        --bg: #15171b;
        --paper: #20242b;
        --text: #f4f6fb;
        --muted: #aab2c0;
        --line: #3a424f;
        --table-head: #2a3039;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: var(--bg); color: var(--text); font-family: "Segoe UI", "Malgun Gothic", Arial, sans-serif; }
body { padding: 28px 16px 44px; }
.page {
    width: min(920px, calc(100vw - 32px));
    min-height: calc(100vh - 72px);
    margin: 0 auto;
    padding: clamp(24px, 5vw, 56px);
    background: var(--paper);
    border: 1px solid var(--line);
    box-shadow: 0 18px 44px rgba(15, 23, 42, .12);
}
.doc-paragraph {
    margin: 0 0 .72em;
    line-height: 1.72;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}
.empty-paragraph { min-height: 1em; }
.doc-table-wrap {
    width: 100%;
    overflow-x: auto;
    margin: 1em 0;
}
.doc-table {
    width: 100%;
    border-collapse: collapse;
    table-layout: auto;
    color: var(--text);
}
.doc-table td {
    min-width: 72px;
    border: 0;
    padding: 8px 10px;
    vertical-align: top;
    overflow-wrap: anywhere;
}
.doc-table .doc-paragraph { margin-bottom: .35em; line-height: 1.45; }
.doc-table .doc-paragraph:last-child { margin-bottom: 0; }
.doc-table.hwpx-table {
    width: auto;
    table-layout: fixed;
}
.doc-table.hwpx-table td {
    min-width: 0;
    padding: 0;
}
.doc-table.hwpx-table .doc-paragraph {
    margin: 0;
}
.doc-image {
    display: block;
    margin: .9em 0;
}
.doc-image img {
    display: block;
    max-width: 100%;
    height: auto;
}
.hwpx-image {
    max-width: none;
    margin: 0;
}
.hwpx-image img {
    width: 100%;
    max-width: none;
    height: 100%;
}
.hwpx-layered-image {
    position: relative;
    display: block;
    width: 100%;
    margin: .9em 0;
    overflow: hidden;
}
.hwpx-layered-image > img {
    position: absolute;
    inset: 0;
    display: block;
    width: 100%;
    height: 100%;
}
.hwpx-layered-text {
    position: absolute;
    display: flex;
    align-items: center;
    justify-content: center;
    overflow: hidden;
    line-height: 1.2;
    text-align: center;
    white-space: pre-wrap;
}
.hwpx-group-shape {
    position: relative;
    display: block;
    width: 100%;
    margin: 0;
    overflow: hidden;
    color: var(--text);
}
.hwpx-group-shape-vectors {
    position: absolute;
    inset: 0;
    display: block;
    width: 100%;
    height: 100%;
    overflow: visible;
}
.hwpx-group-shape-text {
    position: absolute;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    color: var(--text);
    white-space: pre-wrap;
}
.hwpx-group-shape-text-positioned {
    display: block;
    overflow: visible;
}
.hwpx-group-shape-paragraph {
    display: block;
    width: 100%;
    margin: 0;
    line-height: 1.24;
}
.hwpx-group-shape-line {
    position: absolute;
    display: block;
    white-space: nowrap;
}
@media (max-width: 640px) {
    body { padding: 0; }
    .page {
        width: 100%;
        min-height: 100vh;
        border-width: 0;
        padding: 22px 16px 32px;
        box-shadow: none;
    }
}
</style>
</head>
<body>
<main class="page">
{{content}}
</main>
</body>
</html>
""";
        }


        internal static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        internal static string? TryReadImageDataUri(ZipArchive archive, string imagePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(imagePath) ??
                archive.Entries.FirstOrDefault(candidate => string.Equals(candidate.FullName, imagePath, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length <= 0 || entry.Length > 15_000_000)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            byte[] bytes = memory.ToArray();
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension is ".tif" or ".tiff")
            {
                byte[]? pngBytes = ConvertTiffBytesToPngBytes(bytes);
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                }
            }

            return "data:" + GetImageMimeType(imagePath) + ";base64," + Convert.ToBase64String(bytes);
        }

        private static byte[]? ConvertTiffBytesToPngBytes(byte[] tiffBytes)
        {
            try
            {
                return Task.Run(async () =>
                {
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(tiffBytes);
                        await writer.StoreAsync();
                    }
                    stream.Seek(0);

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                    using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    bool isBgra8 = softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                                   softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied;
                    using SoftwareBitmap convertedBitmap = isBgra8
                        ? softwareBitmap
                        : SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    using var output = new InMemoryRandomAccessStream();
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetSoftwareBitmap(convertedBitmap);
                    await encoder.FlushAsync();
                    output.Seek(0);

                    byte[] pngBytes = new byte[output.Size];
                    using var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(pngBytes);
                    return pngBytes;
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to convert embedded DOCX Tiff to PNG: {ex.Message}");
                return null;
            }
        }

        internal static string GetImageMimeType(string imagePath)
        {
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".svg" => "image/svg+xml",
                ".tif" or ".tiff" => "image/tiff",
                _ => "image/png"
            };
        }

        internal static async Task<IReadOnlyDictionary<string, string>> LoadRelationshipsAsync(
            ZipArchive archive,
            string relationshipPath,
            string basePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(relationshipPath);
            if (entry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument rels = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath(basePath, e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetRelationshipsPath(string partPath)
        {
            string directory = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
            string fileName = Path.GetFileName(partPath);
            return string.IsNullOrEmpty(directory)
                ? "_rels/" + fileName + ".rels"
                : directory + "/_rels/" + fileName + ".rels";
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

        internal static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }

        internal static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

    }
}
