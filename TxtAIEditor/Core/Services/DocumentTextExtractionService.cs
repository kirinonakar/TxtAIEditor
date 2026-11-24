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
using System.Xml;
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
        // Bound the number of document models held by the built-in readers at once.
        private static readonly SemaphoreSlim FolderSearchParserSlots = new(4);
        private static readonly SemaphoreSlim FolderSearchConverterSlots = new(4);

        // Documents converted with rdocs2md before the built-in extractors run. PDF keeps its
        // pdftotext and built-in parser fallbacks, so it resolves as rdocs2md > pdftotext > built-in.
        private static readonly HashSet<string> Rdocs2mdPreferredExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".hwp", ".hwpx", ".doc", ".ppt", ".xls", ".epub"
        };

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
            CancellationToken cancellationToken = default,
            bool skipProtectedDocuments = false,
            bool preferRdocs2mdForOpenXml = false)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxChars <= 0)
            {
                return string.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            // HWP 5 is an OLE compound document, so the Office encryption probe below would treat it
            // as an encrypted container. PDF text is also unreliable to rebuild by hand. Try rdocs2md
            // first so both formats stay searchable, and let PDF fall back to pdftotext and the
            // built-in parser when the tool is missing or fails.
            bool preferRdocs2md = Rdocs2mdPreferredExtensions.Contains(extension) ||
                (preferRdocs2mdForOpenXml && extension is (".docx" or ".pptx" or ".xlsx"));
            string? markdown = preferRdocs2md
                ? await TryConvertWithRdocs2mdAsync(filePath, cancellationToken).ConfigureAwait(false)
                : null;
            if (markdown is not null)
            {
                progress?.Report(98);
                return Truncate(normalize ? NormalizeExtractedText(markdown) : markdown, maxChars);
            }

            string text = extension == ".pdf"
                ? await ExtractPdfTextAsync(filePath, maxChars, progress, cancellationToken, skipProtectedDocuments).ConfigureAwait(false)
                : await ExtractWithBuiltInParserAsync(filePath, extension, maxChars, progress, cancellationToken, skipProtectedDocuments).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(98);
            return Truncate(normalize ? NormalizeExtractedText(text) : text, maxChars);
        }

        internal async Task<TextReader?> OpenFolderSearchTextAsync(string filePath, int maxChars, DocumentSearchTextCache cache,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension is not (".hwp" or ".epub"))
            {
                try
                {
                    TextReader? reader = await cache.OpenTextAsync(filePath, maxChars, converterPath: null, FolderSearchParserSlots,
                        (path, writer, token) => WriteBuiltInSearchTextAsync(path, extension, maxChars, writer, token),
                        cancellationToken).ConfigureAwait(false);
                    if (reader is not null)
                    {
                        return reader;
                    }
                }
                catch (Exception ex) when (extension != ".pdf" && ex is (InvalidDataException or XmlException or EndOfStreamException or NotSupportedException))
                {
                    TextReader? fallback = await TryOpenFolderSearchMarkdownAsync(filePath, maxChars, cache, cancellationToken).ConfigureAwait(false);
                    if (fallback is null)
                    {
                        throw;
                    }

                    return fallback;
                }
            }

            TextReader? markdown = await TryOpenFolderSearchMarkdownAsync(filePath, maxChars, cache, cancellationToken).ConfigureAwait(false);
            if (markdown is not null || extension != ".pdf")
            {
                return markdown;
            }

            string executable = ResolveExecutablePath("pdftotext");
            return executable == "pdftotext" ? null : await cache.OpenTextAsync(filePath, maxChars, executable, FolderSearchConverterSlots,
                (path, writer, token) => TryWritePdfWithPdftotextAsync(executable, path, maxChars, writer, progress: null, token),
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> WriteBuiltInSearchTextAsync(string filePath, string extension, int maxChars, TextWriter writer,
            CancellationToken cancellationToken)
        {
            string text = await ExtractWithBuiltInParserAsync(filePath, extension, maxChars, progress: null, cancellationToken,
                skipProtectedDocuments: true, limitSpreadsheetText: true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Limit each write so the UTF-8 encoder does not need an output-sized buffer.
            int length = Math.Min(maxChars, text.Length);
            for (int offset = 0; offset < length; offset += 8192)
            {
                await writer.WriteAsync(text.AsMemory(offset, Math.Min(8192, length - offset)), cancellationToken).ConfigureAwait(false);
            }
            return true;
        }

        private static Task<string> ExtractWithBuiltInParserAsync(
            string filePath, string extension, int maxChars, IProgress<int>? progress,
            CancellationToken cancellationToken, bool skipProtectedDocuments, bool limitSpreadsheetText = false)
        {
            if (skipProtectedDocuments && extension is not (".pdf" or ".doc" or ".ppt" or ".xls") &&
                OfficeLegacyPackage.HasCompoundFileSignature(filePath, cancellationToken))
            {
                throw OfficeLegacyPackage.Encrypted();
            }

            return extension switch
            {
                ".pdf" => new PdfTextExtractionService().ExtractTextAsync(filePath, maxChars, progress, cancellationToken, skipProtectedDocuments),
                ".doc" or ".docx" => ExtractDocxTextAsync(filePath, maxChars, progress, cancellationToken),
                ".ppt" or ".pptx" => ExtractPptxTextAsync(filePath, maxChars, progress, cancellationToken),
                ".xls" or ".xlsx" => ExtractXlsxTextAsync(filePath, maxChars, progress, cancellationToken, limitSpreadsheetText),
                ".hwpx" => ExtractHwpxTextAsync(filePath, maxChars, progress, cancellationToken),
                _ => Task.FromResult(string.Empty)
            };
        }

        public static bool IsSupportedExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ||
                (extension.Equals(".hwp", StringComparison.OrdinalIgnoreCase) && Rdocs2mdConverter.IsAvailable) ||
                (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) && Rdocs2mdConverter.IsAvailable) ||
                extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<TextReader?> TryOpenFolderSearchMarkdownAsync(string filePath, int maxChars,
            DocumentSearchTextCache cache, CancellationToken cancellationToken)
        {
            if (Rdocs2mdConverter.ExecutablePath is not string converterPath)
            {
                return null;
            }

            try
            {
                return await cache.OpenTextAsync(filePath, maxChars, converterPath, FolderSearchConverterSlots,
                    (path, writer, token) => Rdocs2mdConverter.WriteMarkdownAsync(path, writer, maxChars, token), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"rdocs2md conversion failed for {filePath}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string?> TryConvertWithRdocs2mdAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!Rdocs2mdConverter.IsAvailable) return null;
            try
            {
                string markdown = await Rdocs2mdConverter.ConvertToMarkdownAsync(filePath, cancellationToken).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(markdown) ? null : markdown;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"rdocs2md conversion failed for {filePath}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> ExtractPdfTextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken, bool skipProtectedDocuments)
        {
            string text = await TryExtractPdfWithPdftotextAsync(filePath, maxChars, progress, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await ExtractWithBuiltInParserAsync(filePath, ".pdf", maxChars, progress, cancellationToken, skipProtectedDocuments)
                .ConfigureAwait(false);
        }

        private static async Task<string> TryExtractPdfWithPdftotextAsync(string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string executable = ResolveExecutablePath("pdftotext");
            if (executable == "pdftotext")
            {
                return string.Empty;
            }

            using var output = new StringWriter();
            return await TryWritePdfWithPdftotextAsync(executable, filePath, maxChars, output, progress, cancellationToken).ConfigureAwait(false)
                ? output.ToString() : string.Empty;
        }

        private static async Task<bool> TryWritePdfWithPdftotextAsync(string executable, string filePath, int maxChars, TextWriter output,
            IProgress<int>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                return false;
            }

            using var pipeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var diagnostic = new StringWriter();
            Task<bool> stdoutTask = CopyTextPrefixAsync(process.StandardOutput, output, maxChars, pipeCancellation.Token);
            Task<bool> stderrTask = CopyTextPrefixAsync(process.StandardError, diagnostic, 4096, pipeCancellation.Token);
            Task waitTask = process.WaitForExitAsync(pipeCancellation.Token);
            try
            {
                int reported = 5;
                while (!waitTask.IsCompleted)
                {
                    if (await Task.WhenAny(waitTask, Task.Delay(500, cancellationToken)).ConfigureAwait(false) == waitTask)
                    {
                        break;
                    }
                    if (stdoutTask.IsFaulted) await stdoutTask.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    reported = Math.Min(90, reported + 5);
                    progress?.Report(reported);
                }

                await waitTask.ConfigureAwait(false);
                bool hasText = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    string stderr = diagnostic.ToString();
                    if (stderr.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                        stderr.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Encrypted PDF cannot be searched without a password.");
                    }
                    return false;
                }

                progress?.Report(95);
                return hasText;
            }
            catch
            {
                TryKillProcess(process);
                pipeCancellation.Cancel();
                await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }
            finally
            {
                UnregisterPdftotextProcess(process);
            }
        }

        internal static async Task<bool> CopyTextPrefixAsync(TextReader reader, TextWriter writer, int maxChars,
            CancellationToken cancellationToken)
        {
            char[] buffer = new char[8192];
            int remaining = maxChars;
            bool hasText = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                int count = Math.Min(read, remaining);
                if (count > 0)
                {
                    hasText |= !buffer.AsSpan(0, count).Trim().IsEmpty;
                    await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                }
            }

            return hasText;
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
            using ZipArchive archive = await OfficeLegacyPackage.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
            ZipArchiveEntry? entry = archive.GetEntry("word/document.xml");
            if (entry == null)
            {
                return string.Empty;
            }

            XDocument doc = await LoadXmlEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            progress?.Report(50);
            var builder = new StringBuilder();

            foreach (XElement paragraph in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendParagraphText(builder, paragraph, maxChars);
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
            using ZipArchive archive = await OfficeLegacyPackage.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
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
                XDocument slide = await LoadXmlEntryAsync(slideEntry, cancellationToken).ConfigureAwait(false);
                AppendLimitedLine(builder, $"[Slide {slideNumber}]", maxChars);

                foreach (XElement paragraph in slide.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int before = builder.Length;
                    AppendParagraphText(builder, paragraph, maxChars);
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

        private static async Task<string> ExtractXlsxTextAsync(string filePath, int maxChars, IProgress<int>? progress,
            CancellationToken cancellationToken, bool limitTotalText)
        {
            var (sheets, sheetCount) = await ReadXlsxSheetsAsync(filePath, maxChars, progress, cancellationToken, limitTotalText).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return string.Empty;
            }

            if (sheetCount == 1)
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
            using ZipArchive archive = await OfficeLegacyPackage.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
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

                XDocument section = await LoadXmlEntryAsync(sectionEntry, cancellationToken).ConfigureAwait(false);
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
            var (sheets, _) = await ReadXlsxSheetsAsync(filePath, maxChars, progress, cancellationToken, limitTotalText: false).ConfigureAwait(false);
            return sheets;
        }

        private static async Task<(IReadOnlyList<ExtractedSpreadsheetSheet> Sheets, int SheetCount)> ReadXlsxSheetsAsync(
            string filePath, int maxChars, IProgress<int>? progress, CancellationToken cancellationToken, bool limitTotalText)
        {
            using ZipArchive archive = await OfficeLegacyPackage.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> sharedStrings = await LoadSharedStringsAsync(archive, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sheetNamesByPath = await LoadSheetNamesByPathAsync(archive, cancellationToken).ConfigureAwait(false);

            var sheetEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var sheets = new List<ExtractedSpreadsheetSheet>();
            int remainingChars = maxChars;
            int fallbackSheetNumber = 1;
            for (int sheetIndex = 0; sheetIndex < sheetEntries.Count; sheetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry sheetEntry = sheetEntries[sheetIndex];
                progress?.Report(5 + (int)((sheetIndex / (double)Math.Max(1, sheetEntries.Count)) * 90));
                string sheetName = sheetNamesByPath.TryGetValue(sheetEntry.FullName, out string? mappedName)
                    ? mappedName
                    : $"Sheet {fallbackSheetNumber}";

                if (limitTotalText && sheetEntries.Count > 1)
                {
                    remainingChars = Math.Max(0, remainingChars - "# Sheet: ".Length - EscapeCsvComment(sheetName).Length - 1);
                }
                int sheetMaxChars = limitTotalText ? remainingChars : maxChars;

                var builder = new StringBuilder();
                XDocument sheet = await LoadXmlEntryAsync(sheetEntry, cancellationToken).ConfigureAwait(false);
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
                        AppendLimitedLine(builder, string.Join(",", values.Select(EscapeCsvField)), sheetMaxChars);
                    }

                    if (builder.Length >= sheetMaxChars)
                    {
                        break;
                    }
                }

                string csv = builder.ToString().TrimEnd('\r', '\n');
                sheets.Add(new ExtractedSpreadsheetSheet
                {
                    Index = sheetIndex + 1,
                    Name = sheetName,
                    CsvContent = csv
                });

                if (limitTotalText)
                {
                    remainingChars -= csv.Length + 1;
                    if (remainingChars <= 0) break;
                }

                fallbackSheetNumber++;
            }

            progress?.Report(95);
            return (sheets, sheetEntries.Count);
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

        private static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
        {
            using Stream stream = entry.Open();
            return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }

        private static void AppendParagraphText(StringBuilder builder, XElement paragraph, int maxChars)
        {
            foreach (XElement child in paragraph.Descendants())
            {
                if (builder.Length >= maxChars) break;
                switch (child.Name.LocalName)
                {
                    case "t":
                        AppendLimited(builder, child.Value, maxChars);
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

        private static async Task<IReadOnlyList<string>> LoadSharedStringsAsync(ZipArchive archive, CancellationToken cancellationToken)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            XDocument doc = await LoadXmlEntryAsync(entry, cancellationToken).ConfigureAwait(false);
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

        private static async Task<IReadOnlyDictionary<string, string>> LoadSheetNamesByPathAsync(ZipArchive archive, CancellationToken cancellationToken)
        {
            ZipArchiveEntry? workbookEntry = archive.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry == null || relsEntry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument workbook = await LoadXmlEntryAsync(workbookEntry, cancellationToken).ConfigureAwait(false);
            XDocument rels = await LoadXmlEntryAsync(relsEntry, cancellationToken).ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            var targetsById = rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = OfficePackagePath.NormalizeZipPath("xl", e.Attribute("Target")?.Value ?? string.Empty)
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
