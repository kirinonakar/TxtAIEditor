using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    public sealed class ExtractedSpreadsheetSheet
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string CsvContent { get; init; } = string.Empty;
    }

    public sealed class DocumentTextExtractionService
    {
        private static readonly ConcurrentDictionary<int, Process> RunningPdftotextProcesses = new();

        public static void KillRunningPdftotextProcesses()
        {
            foreach (Process process in RunningPdftotextProcesses.Values.ToList())
            {
                TryKillProcess(process);
            }
        }

        public async Task<string> ExtractTextAsync(
            string filePath,
            int maxChars,
            IProgress<int>? progress = null,
            bool normalize = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxChars <= 0)
            {
                return string.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            string text = extension switch
            {
                ".pdf" => await ExtractPdfTextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false),
                ".docx" => await ExtractDocxTextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false),
                ".pptx" => await ExtractPptxTextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false),
                ".xlsx" => await ExtractXlsxTextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false),
                ".hwpx" => await ExtractHwpxTextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false),
                _ => string.Empty
            };

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(98);
            return Truncate(normalize ? NormalizeExtractedText(text) : text, maxChars);
        }

        public static bool IsSupportedExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> ExtractPdfTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            string text = await TryExtractPdfWithPdftotextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await new PdfTextExtractionService()
                .ExtractTextAsync(filePath, maxChars, progress)
                .ConfigureAwait(false);
        }

        private static async Task<string> TryExtractPdfWithPdftotextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string executable = ResolveExecutablePath("pdftotext");
            if (string.Equals(executable, "pdftotext", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

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
            process.StartInfo.ArgumentList.Add("-");

            try
            {
                progress?.Report(5);
                process.Start();
                RegisterPdftotextProcess(process);
            }
            catch
            {
                return string.Empty;
            }

            try
            {
                Task<string> stdoutTask = ReadTextPrefixAsync(process.StandardOutput, maxChars, cancellationToken);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task waitTask = process.WaitForExitAsync();

                int reported = 5;
                while (!waitTask.IsCompleted)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    reported = Math.Min(90, reported + 5);
                    progress?.Report(reported);
                }

                await waitTask.ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return string.Empty;
                }

                progress?.Report(95);
                return stdout;
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }
            finally
            {
                UnregisterPdftotextProcess(process);
            }
        }

        private static async Task<string> ReadTextPrefixAsync(TextReader reader, int maxChars, CancellationToken cancellationToken)
        {
            char[] buffer = new char[Math.Min(Math.Max(maxChars, 1), 81920)];
            var builder = new StringBuilder(Math.Min(maxChars, 81920));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (builder.Length < maxChars)
                {
                    int remaining = maxChars - builder.Length;
                    builder.Append(buffer, 0, Math.Min(read, remaining));
                }
            }

            return builder.ToString();
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        private static void RegisterPdftotextProcess(Process process)
        {
            try
            {
                RunningPdftotextProcesses[process.Id] = process;
            }
            catch
            {
            }
        }

        private static void UnregisterPdftotextProcess(Process process)
        {
            try
            {
                if (process.Id > 0)
                {
                    RunningPdftotextProcesses.TryRemove(process.Id, out _);
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

        private static async Task<string> ExtractDocxTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();
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

        private static async Task<string> ExtractPptxTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry slideEntry = slideEntries[slideIndex];
                progress?.Report(5 + (int)((slideIndex / (double)Math.Max(1, slideEntries.Count)) * 90));
                XDocument slide = await LoadXmlEntryAsync(slideEntry).ConfigureAwait(false);
                AppendLimitedLine(builder, $"[Slide {slideNumber}]", maxChars);

                foreach (XElement paragraph in slide.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

        private static async Task<string> ExtractXlsxTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            IReadOnlyList<ExtractedSpreadsheetSheet> sheets = await ExtractXlsxSheetsAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return string.Empty;
            }

            if (sheets.Count == 1)
            {
                return sheets[0].CsvContent;
            }

            var builder = new StringBuilder();
            foreach (ExtractedSpreadsheetSheet sheet in sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLimitedLine(builder, $"# Sheet: {EscapeCsvComment(sheet.Name)}", maxChars);
                AppendLimited(builder, sheet.CsvContent, maxChars);
                AppendLimited(builder, "\n", maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            return builder.ToString();
        }

        private static async Task<string> ExtractHwpxTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            var sectionEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            if (sectionEntries.Count == 0)
            {
                sectionEntries = archive.Entries
                    .Where(entry =>
                        entry.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FullName.EndsWith("/header.xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var builder = new StringBuilder();
            for (int sectionIndex = 0; sectionIndex < sectionEntries.Count; sectionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry sectionEntry = sectionEntries[sectionIndex];
                progress?.Report(5 + (int)((sectionIndex / (double)Math.Max(1, sectionEntries.Count)) * 90));

                XDocument section = await LoadXmlEntryAsync(sectionEntry).ConfigureAwait(false);
                foreach (XElement paragraph in section.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int before = builder.Length;
                    AppendHwpxParagraphText(builder, paragraph, maxChars);
                    if (builder.Length > before)
                    {
                        AppendLimited(builder, "\n", maxChars);
                    }

                    if (builder.Length >= maxChars)
                    {
                        break;
                    }
                }

                if (builder.Length >= maxChars)
                {
                    break;
                }

                AppendLimited(builder, "\n", maxChars);
            }

            progress?.Report(95);
            return builder.ToString();
        }

        public static async Task<IReadOnlyList<ExtractedSpreadsheetSheet>> ExtractXlsxSheetsAsync(string filePath, int maxChars, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyList<string> sharedStrings = await LoadSharedStringsAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sheetNamesByPath = await LoadSheetNamesByPathAsync(archive).ConfigureAwait(false);

            var sheetEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var sheets = new List<ExtractedSpreadsheetSheet>();
            int fallbackSheetNumber = 1;
            for (int sheetIndex = 0; sheetIndex < sheetEntries.Count; sheetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry sheetEntry = sheetEntries[sheetIndex];
                progress?.Report(5 + (int)((sheetIndex / (double)Math.Max(1, sheetEntries.Count)) * 90));
                string sheetName = sheetNamesByPath.TryGetValue(sheetEntry.FullName, out string? mappedName)
                    ? mappedName
                    : $"Sheet {fallbackSheetNumber}";

                var builder = new StringBuilder();
                XDocument sheet = await LoadXmlEntryAsync(sheetEntry).ConfigureAwait(false);
                foreach (XElement row in sheet.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new List<string>();
                    foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int columnIndex = GetCellColumnIndex(cell);
                        if (columnIndex > 0)
                        {
                            while (values.Count < columnIndex - 1)
                            {
                                values.Add(string.Empty);
                            }
                        }

                        values.Add(GetCellText(cell, sharedStrings));
                    }

                    if (values.Any(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        AppendLimitedLine(builder, string.Join(",", values.Select(EscapeCsvField)), maxChars);
                    }

                    if (builder.Length >= maxChars)
                    {
                        break;
                    }
                }

                sheets.Add(new ExtractedSpreadsheetSheet
                {
                    Index = sheetIndex + 1,
                    Name = sheetName,
                    CsvContent = builder.ToString().TrimEnd('\r', '\n')
                });

                fallbackSheetNumber++;
            }

            progress?.Report(95);
            return sheets;
        }

        private static string EscapeCsvField(string value)
        {
            value ??= string.Empty;
            bool mustQuote = value.Contains(',', StringComparison.Ordinal) ||
                value.Contains('"', StringComparison.Ordinal) ||
                value.Contains('\n') ||
                value.Contains('\r') ||
                value.StartsWith(' ') ||
                value.EndsWith(' ');

            return mustQuote
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;
        }

        private static string EscapeCsvComment(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }

        private static int GetCellColumnIndex(XElement cell)
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return 0;
            }

            int column = 0;
            foreach (char ch in reference)
            {
                if (ch < 'A' || ch > 'Z')
                {
                    if (ch >= 'a' && ch <= 'z')
                    {
                        column = (column * 26) + (ch - 'a' + 1);
                        continue;
                    }

                    break;
                }

                column = (column * 26) + (ch - 'A' + 1);
            }

            return column;
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

        private static void AppendHwpxParagraphText(StringBuilder builder, XElement paragraph, int maxChars)
        {
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (builder.Length >= maxChars)
                {
                    break;
                }

                if (IsInsideNestedElement(paragraph, node, "tbl"))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    AppendLimited(builder, textNode.Value, maxChars);
                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "tab":
                        AppendLimited(builder, "\t", maxChars);
                        break;
                    case "lineBreak":
                    case "br":
                    case "cr":
                        AppendLimited(builder, "\n", maxChars);
                        break;
                    case "nbSpace":
                        AppendLimited(builder, " ", maxChars);
                        break;
                    case "fwSpace":
                        AppendLimited(builder, "\u3000", maxChars);
                        break;
                }
            }
        }

        private static bool IsInsideNestedElement(XElement root, XNode node, string localName)
        {
            XElement? parent = node.Parent;
            while (parent != null && !ReferenceEquals(parent, root))
            {
                if (parent.Name.LocalName == localName)
                {
                    return true;
                }

                parent = parent.Parent;
            }

            return false;
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
