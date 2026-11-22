using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    /// <summary>
    /// Converts documents to Markdown with the optional rdocs2md.exe command line tool.
    /// The tool is used only when it exists beside the application or on PATH.
    /// </summary>
    public static class Rdocs2mdConverter
    {
        private const string ExecutableFileName = "rdocs2md.exe";
        private static readonly TimeSpan ConversionTimeout = TimeSpan.FromSeconds(180);

        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".hwp", ".hwpx", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx",
            ".odt", ".ods", ".odp", ".rtf", ".epub"
        };

        private static readonly HashSet<string> SpreadsheetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xls", ".xlsx", ".xlsm", ".ods"
        };

        private static readonly Lazy<string?> ResolvedExecutablePath = new Lazy<string?>(FindExecutablePathSafely, LazyThreadSafetyMode.ExecutionAndPublication);

        // Availability checks also run from static initializers, so they must never throw.
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    return ResolvedExecutablePath.Value is not null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"rdocs2md availability check failed: {ex.Message}");
                    return false;
                }
            }
        }

        public static string? ExecutablePath
        {
            get
            {
                try
                {
                    return ResolvedExecutablePath.Value;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"rdocs2md path resolution failed: {ex.Message}");
                    return null;
                }
            }
        }

        public static bool SupportsExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !IsAvailable)
            {
                return false;
            }

            try
            {
                return SupportedExtensions.Contains(Path.GetExtension(filePath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                Debug.WriteLine($"rdocs2md extension check failed for {filePath}: {ex.Message}");
                return false;
            }
        }

        public static async Task<string> ConvertToMarkdownAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return await ConvertAsync(filePath, "md", sheetName: null, sectionRange: null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Converts a document to a self-contained HTML document. The editor shows the
        /// result through its HTML live preview mode.
        /// </summary>
        public static async Task<string> ConvertToHtmlAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return await ConvertAsync(filePath, "html", sheetName: null, sectionRange: null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Converts a workbook to CSV. When <paramref name="sheetName"/> is set, only that worksheet is converted;
        /// <paramref name="sectionRange"/> selects 1-based sheet numbers instead (e.g. "3" or "2-4").
        /// </summary>
        public static async Task<string> ConvertToCsvAsync(string filePath, string? sheetName, string? sectionRange, CancellationToken cancellationToken = default)
        {
            return await ConvertAsync(filePath, "csv", sheetName, sectionRange, cancellationToken).ConfigureAwait(false);
        }

        public static bool IsSpreadsheetExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                return SpreadsheetExtensions.Contains(Path.GetExtension(filePath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                Debug.WriteLine($"rdocs2md spreadsheet check failed for {filePath}: {ex.Message}");
                return false;
            }
        }

        private static async Task<string> ConvertAsync(string filePath, string format, string? sheetName, string? sectionRange, CancellationToken cancellationToken)
        {
            string? executablePath = ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(filePath) || !SafeFileExists(executablePath))
            {
                return string.Empty;
            }

            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(filePath);
            startInfo.ArgumentList.Add("--stdout");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add(format);
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                startInfo.ArgumentList.Add("--sheet");
                startInfo.ArgumentList.Add(sheetName);
            }
            else if (!string.IsNullOrWhiteSpace(sectionRange))
            {
                startInfo.ArgumentList.Add("--pages");
                startInfo.ArgumentList.Add(sectionRange);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    Debug.WriteLine("rdocs2md.exe did not start; skipped document conversion.");
                    return string.Empty;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.WriteLine($"Failed to start rdocs2md.exe: {ex.Message}");
                return string.Empty;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            using var timeoutSource = new CancellationTokenSource(ConversionTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                ObserveFaults(outputTask, errorTask);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException($"rdocs2md conversion timed out after {ConversionTimeout.TotalSeconds:0} seconds: {filePath}");
            }

            string markdown = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? markdown.Trim() : error.Trim();
                throw new InvalidOperationException($"rdocs2md exited with code {process.ExitCode}: {Truncate(detail, 400)}");
            }

            return markdown;
        }

        private static string? FindExecutablePath()
        {
            foreach (string candidate in EnumerateCandidatePaths())
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    Debug.WriteLine($"Ignored rdocs2md candidate {candidate}: {ex.Message}");
                }
            }

            return null;
        }

        private static string? FindExecutablePathSafely()
        {
            try
            {
                return FindExecutablePath();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"rdocs2md lookup failed: {ex.Message}");
                return null;
            }
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to probe {path}: {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<string> EnumerateCandidatePaths()
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                yield return Path.Combine(baseDirectory, ExecutableFileName);
                yield return Path.Combine(baseDirectory, "Tools", ExecutableFileName);
            }

            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathVariable))
            {
                yield break;
            }

            foreach (string entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string directory = entry.Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                yield return Path.Combine(directory, ExecutableFileName);
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to stop rdocs2md: {ex.Message}");
            }
        }

        private static void ObserveFaults(params Task<string>[] tasks)
        {
            foreach (Task<string> task in tasks)
            {
                _ = task.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private static string Truncate(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
    }
}
