using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    public sealed class DocumentTextExtractionService
    {
        public async Task<string> ExtractTextAsync(string filePath, int maxChars, IProgress<int>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxChars <= 0)
            {
                return string.Empty;
            }

            progress?.Report(1);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            string text = extension switch
            {
                ".pdf" => await ExtractPdfTextAsync(filePath, maxChars, progress).ConfigureAwait(false),
                ".docx" => await ExtractDocxTextAsync(filePath, maxChars, progress).ConfigureAwait(false),
                ".pptx" => await ExtractPptxTextAsync(filePath, maxChars, progress).ConfigureAwait(false),
                ".xlsx" => await ExtractXlsxTextAsync(filePath, maxChars, progress).ConfigureAwait(false),
                _ => string.Empty
            };

            progress?.Report(98);
            return Truncate(NormalizeExtractedText(text), maxChars);
        }

        public static bool IsSupportedExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> ExtractPdfTextAsync(string filePath, int maxChars, IProgress<int>? progress)
        {
            return await TryExtractPdfWithPdftotextAsync(filePath, maxChars, progress).ConfigureAwait(false);
        }

        private static async Task<string> TryExtractPdfWithPdftotextAsync(string filePath, int maxChars, IProgress<int>? progress)
        {
            string executable = ResolveExecutablePath("pdftotext");
            if (string.Equals(executable, "pdftotext", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.StartInfo.ArgumentList.Add("-layout");
            process.StartInfo.ArgumentList.Add("-enc");
            process.StartInfo.ArgumentList.Add("UTF-8");
            process.StartInfo.ArgumentList.Add(filePath);
            process.StartInfo.ArgumentList.Add(tempPath);

            try
            {
                progress?.Report(5);
                process.Start();
            }
            catch
            {
                TryDelete(tempPath);
                return string.Empty;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();

            int reported = 5;
            while (!waitTask.IsCompleted)
            {
                await Task.Delay(500).ConfigureAwait(false);
                reported = Math.Min(90, reported + 5);
                progress?.Report(reported);
            }

            await waitTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(tempPath))
            {
                TryDelete(tempPath);
                return string.Empty;
            }

            progress?.Report(95);
            string text = await ReadTextFilePrefixAsync(tempPath, maxChars).ConfigureAwait(false);
            TryDelete(tempPath);
            return text;
        }

        private static async Task<string> ReadTextFilePrefixAsync(string filePath, int maxChars)
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[Math.Min(maxChars, 81920)];
            var builder = new StringBuilder(Math.Min(maxChars, 81920));
            while (builder.Length < maxChars)
            {
                int remaining = maxChars - builder.Length;
                int read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }

            return builder.ToString();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string ResolveExecutablePath(string fileName)
        {
            string? pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return fileName;
            }

            string searchName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName + ".exe";

            foreach (string directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), searchName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return fileName;
        }

        private static async Task<string> ExtractDocxTextAsync(string filePath, int maxChars, IProgress<int>? progress)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            ZipArchiveEntry? entry = archive.GetEntry("word/document.xml");
            if (entry == null)
            {
                return string.Empty;
            }

            XDocument doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            progress?.Report(50);
            var builder = new StringBuilder();

            foreach (XElement paragraph in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                AppendParagraphText(builder, paragraph);
                AppendLimited(builder, "\n", maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            progress?.Report(95);
            return builder.ToString();
        }

        private static async Task<string> ExtractPptxTextAsync(string filePath, int maxChars, IProgress<int>? progress)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            var slideEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var builder = new StringBuilder();
            int slideNumber = 1;
            for (int slideIndex = 0; slideIndex < slideEntries.Count; slideIndex++)
            {
                ZipArchiveEntry slideEntry = slideEntries[slideIndex];
                progress?.Report(5 + (int)((slideIndex / (double)Math.Max(1, slideEntries.Count)) * 90));
                XDocument slide = await LoadXmlEntryAsync(slideEntry).ConfigureAwait(false);
                AppendLimitedLine(builder, $"[Slide {slideNumber}]", maxChars);

                foreach (XElement paragraph in slide.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    int before = builder.Length;
                    AppendParagraphText(builder, paragraph);
                    if (builder.Length > before)
                    {
                        AppendLimited(builder, "\n", maxChars);
                    }

                    if (builder.Length >= maxChars)
                    {
                        break;
                    }
                }

                AppendLimited(builder, "\n", maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }

                slideNumber++;
            }

            progress?.Report(95);
            return builder.ToString();
        }

        private static async Task<string> ExtractXlsxTextAsync(string filePath, int maxChars, IProgress<int>? progress)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyList<string> sharedStrings = await LoadSharedStringsAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sheetNamesByPath = await LoadSheetNamesByPathAsync(archive).ConfigureAwait(false);

            var sheetEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var builder = new StringBuilder();
            int fallbackSheetNumber = 1;
            for (int sheetIndex = 0; sheetIndex < sheetEntries.Count; sheetIndex++)
            {
                ZipArchiveEntry sheetEntry = sheetEntries[sheetIndex];
                progress?.Report(5 + (int)((sheetIndex / (double)Math.Max(1, sheetEntries.Count)) * 90));
                string sheetName = sheetNamesByPath.TryGetValue(sheetEntry.FullName, out string? mappedName)
                    ? mappedName
                    : $"Sheet {fallbackSheetNumber}";
                AppendLimitedLine(builder, $"[{sheetName}]", maxChars);

                XDocument sheet = await LoadXmlEntryAsync(sheetEntry).ConfigureAwait(false);
                foreach (XElement row in sheet.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    var values = new List<string>();
                    foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        values.Add(GetCellText(cell, sharedStrings));
                    }

                    if (values.Any(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        AppendLimitedLine(builder, string.Join("\t", values), maxChars);
                    }

                    if (builder.Length >= maxChars)
                    {
                        break;
                    }
                }

                AppendLimited(builder, "\n", maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }

                fallbackSheetNumber++;
            }

            progress?.Report(95);
            return builder.ToString();
        }

        private static async Task<ZipArchive> OpenArchiveAsync(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        private static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() => XDocument.Load(stream)).ConfigureAwait(false);
        }

        private static void AppendParagraphText(StringBuilder builder, XElement paragraph)
        {
            foreach (XElement child in paragraph.Descendants())
            {
                switch (child.Name.LocalName)
                {
                    case "t":
                        builder.Append(child.Value);
                        break;
                    case "tab":
                        builder.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        builder.Append('\n');
                        break;
                }
            }
        }

        private static async Task<IReadOnlyList<string>> LoadSharedStringsAsync(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            XDocument doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "si")
                .Select(ReadSharedStringItem)
                .ToList();
        }

        private static string ReadSharedStringItem(XElement item)
        {
            var builder = new StringBuilder();
            foreach (XElement textElement in item.Descendants().Where(e => e.Name.LocalName == "t"))
            {
                builder.Append(textElement.Value);
            }

            return builder.ToString();
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadSheetNamesByPathAsync(ZipArchive archive)
        {
            ZipArchiveEntry? workbookEntry = archive.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry == null || relsEntry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument workbook = await LoadXmlEntryAsync(workbookEntry).ConfigureAwait(false);
            XDocument rels = await LoadXmlEntryAsync(relsEntry).ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            var targetsById = rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath("xl", e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);

            var namesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
            {
                string name = sheet.Attribute("name")?.Value ?? string.Empty;
                string id = sheet.Attribute(relNs + "id")?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) &&
                    targetsById.TryGetValue(id, out string? targetPath))
                {
                    namesByPath[targetPath] = name;
                }
            }

            return namesByPath;
        }

        private static string GetCellText(XElement cell, IReadOnlyList<string> sharedStrings)
        {
            string type = cell.Attribute("t")?.Value ?? string.Empty;
            if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            }

            string rawValue = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(rawValue))
            {
                return string.Empty;
            }

            return type switch
            {
                "s" when int.TryParse(rawValue, out int index) && index >= 0 && index < sharedStrings.Count => sharedStrings[index],
                "b" => rawValue == "1" ? "TRUE" : "FALSE",
                _ => rawValue
            };
        }

        private static string NormalizeZipPath(string basePath, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
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

        private static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int number)
                ? number
                : int.MaxValue;
        }

        private static void AppendLimitedLine(StringBuilder builder, string value, int maxChars)
        {
            AppendLimited(builder, value, maxChars);
            AppendLimited(builder, "\n", maxChars);
        }

        private static void AppendLimited(StringBuilder builder, string value, int maxChars)
        {
            if (builder.Length >= maxChars || string.IsNullOrEmpty(value))
            {
                return;
            }

            int remaining = maxChars - builder.Length;
            builder.Append(value.Length <= remaining ? value : value.Substring(0, remaining));
        }

        private static string NormalizeExtractedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            normalized = Regex.Replace(normalized, @"[ \t\f\v]+", " ");
            normalized = Regex.Replace(normalized, @" *\n *", "\n");
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
            return normalized.Trim();
        }

        private static string Truncate(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
    }
}
