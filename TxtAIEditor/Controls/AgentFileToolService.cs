using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class AgentFileToolService
    {
        private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".next", "dist", "build"
        };

        private readonly Func<string> _workspaceRootProvider;

        public AgentFileToolService(Func<string> workspaceRootProvider)
        {
            _workspaceRootProvider = workspaceRootProvider;
        }

        public string WorkspaceRoot => ResolveWorkspaceRoot();

        public async Task<string> ListFilesAsync(string? glob, int maxResults)
        {
            string root = ResolveWorkspaceRoot();
            int limit = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 1, 300);
            var matches = EnumerateWorkspaceFiles(root)
                .Where(path => GlobMatches(RelativePath(root, path), glob))
                .Take(limit)
                .Select(path => RelativePath(root, path))
                .ToList();

            await Task.CompletedTask;
            return matches.Count == 0
                ? "No files matched."
                : string.Join(Environment.NewLine, matches);
        }

        public async Task<string> SearchTextAsync(string query, string? glob, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "search_text failed: query is empty.";
            }

            string root = ResolveWorkspaceRoot();
            int limit = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 1, 300);
            var results = new List<string>();

            foreach (string filePath in EnumerateWorkspaceFiles(root).Where(path => GlobMatches(RelativePath(root, path), glob)))
            {
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(filePath);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        string preview = lines[i].Trim();
                        if (preview.Length > 220)
                        {
                            preview = preview.Substring(0, 220) + "...";
                        }

                        results.Add($"{RelativePath(root, filePath)}:{i + 1}: {preview}");
                        if (results.Count >= limit)
                        {
                            return string.Join(Environment.NewLine, results);
                        }
                    }
                }
            }

            return results.Count == 0
                ? "No matches found."
                : string.Join(Environment.NewLine, results);
        }

        public async Task<string> ReadFileAsync(string path, int startLine, int lineCount)
        {
            string fullPath = ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"read_file failed: file not found: {path}";
            }

            int start = Math.Max(1, startLine <= 0 ? 1 : startLine);
            int count = Math.Clamp(lineCount <= 0 ? 160 : lineCount, 1, 600);
            string[] lines = await File.ReadAllLinesAsync(fullPath);

            if (start > lines.Length)
            {
                return $"read_file: startLine {start} is beyond EOF ({lines.Length} lines).";
            }

            var builder = new StringBuilder();
            int end = Math.Min(lines.Length, start + count - 1);
            for (int lineNumber = start; lineNumber <= end; lineNumber++)
            {
                builder.Append(lineNumber.ToString().PadLeft(5));
                builder.Append(" | ");
                builder.AppendLine(lines[lineNumber - 1]);
            }

            return builder.ToString();
        }

        public async Task<string> RunRgAsync(string arguments, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "run_rg failed: arguments are empty.";
            }

            return await RunProcessAsync("rg", arguments, ResolveWorkspaceRoot(), timeoutMs <= 0 ? 10000 : timeoutMs);
        }

        public async Task<string> RunPowerShellAsync(string command, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return "run_powershell failed: command is empty.";
            }

            if (LooksDestructive(command))
            {
                return "run_powershell blocked: destructive commands are not executed automatically by Agent.";
            }

            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var profile = TerminalShellProfile.Resolve("PowerShell");
            string shellPath = profile.ExecutablePath;
            return await RunProcessAsync(shellPath, $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}", ResolveWorkspaceRoot(), timeoutMs <= 0 ? 10000 : timeoutMs);
        }

        public async Task<string> CreateFileAsync(string path, string content)
        {
            string fullPath = ResolveInsideWorkspace(path);
            if (File.Exists(fullPath))
            {
                return $"create_file failed: file already exists: {path}";
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, NormalizeNewlines(content));
            return $"created: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        public async Task<string> ReplaceInFileAsync(string path, string oldText, string newText)
        {
            string fullPath = ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"replace_in_file failed: file not found: {path}";
            }

            if (string.IsNullOrEmpty(oldText))
            {
                return "replace_in_file failed: oldText is empty.";
            }

            string content = await File.ReadAllTextAsync(fullPath);
            int index = content.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
            {
                return "replace_in_file failed: oldText was not found exactly.";
            }

            string updated = content.Remove(index, oldText.Length).Insert(index, newText ?? string.Empty);
            await File.WriteAllTextAsync(fullPath, updated);
            return $"modified: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        public async Task<string> OverwriteFileAsync(string path, string content)
        {
            string fullPath = ResolveInsideWorkspace(path);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, NormalizeNewlines(content));
            return $"overwritten: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        private static async Task<string> RunProcessAsync(string fileName, string arguments, string workingDirectory, int timeoutMs)
        {
            var output = new StringBuilder();
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return $"{fileName} failed to start: {ex.Message}";
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task exitTask = process.WaitForExitAsync();

            Task completed = await Task.WhenAny(exitTask, Task.Delay(Math.Clamp(timeoutMs, 1000, 60000)));
            if (completed != exitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return $"{fileName} timed out after {timeoutMs}ms.";
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                output.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.AppendLine("[stderr]");
                output.AppendLine(stderr.TrimEnd());
            }

            output.AppendLine($"[exit_code] {process.ExitCode}");

            string text = output.ToString();
            return text.Length > 20000 ? text.Substring(0, 20000) + "\n[output truncated]" : text;
        }

        private string ResolveWorkspaceRoot()
        {
            string root = _workspaceRootProvider();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private string ResolveInsideWorkspace(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Path is empty.");
            }

            string root = ResolveWorkspaceRoot();
            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            string rootWithSlash = root + Path.DirectorySeparatorChar;
            if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path escapes workspace root: {path}");
            }

            return candidate;
        }

        private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<string> subdirs;
                IEnumerable<string> files;

                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
                }

                foreach (string subdir in subdirs)
                {
                    string name = Path.GetFileName(subdir);
                    if (!ExcludedDirectoryNames.Contains(name))
                    {
                        pending.Push(subdir);
                    }
                }
            }
        }

        private static bool GlobMatches(string relativePath, string? glob)
        {
            if (string.IsNullOrWhiteSpace(glob) || glob == "*" || glob == "**/*")
            {
                return true;
            }

            string normalizedPath = relativePath.Replace('\\', '/');
            string normalizedGlob = glob.Replace('\\', '/');
            string pattern = "^" + Regex.Escape(normalizedGlob)
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";

            return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase);
        }

        private static bool LooksDestructive(string command)
        {
            string normalized = command.ToLowerInvariant();
            string[] blocked =
            {
                "remove-item", " rm ", " del ", " erase ", " rmdir ", " rd ",
                "git reset --hard", "git clean", "format-volume", "clear-recyclebin",
                "stop-computer", "restart-computer", "set-executionpolicy"
            };

            return blocked.Any(token => normalized.Contains(token, StringComparison.Ordinal));
        }

        private static string RelativePath(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string NormalizeNewlines(string? content)
        {
            return (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
