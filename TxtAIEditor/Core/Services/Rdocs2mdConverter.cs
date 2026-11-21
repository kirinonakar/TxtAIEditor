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

        private static readonly Lazy<string?> ResolvedExecutablePath = new Lazy<string?>(FindExecutablePath, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsAvailable => ResolvedExecutablePath.Value is not null;

        public static string? ExecutablePath => ResolvedExecutablePath.Value;

        public static bool SupportsExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !IsAvailable)
            {
                return false;
            }

            return SupportedExtensions.Contains(Path.GetExtension(filePath));
        }

        public static async Task<string> ConvertToMarkdownAsync(string filePath, CancellationToken cancellationToken = default)
        {
            string? executablePath = ResolvedExecutablePath.Value;
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(filePath))
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
            startInfo.ArgumentList.Add("md");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start rdocs2md.exe.");
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
