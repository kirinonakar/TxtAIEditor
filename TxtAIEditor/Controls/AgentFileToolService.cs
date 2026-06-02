using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class AgentFileEditPreview
    {
        public string ActionName { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string OldContent { get; init; } = string.Empty;
        public string NewContent { get; init; } = string.Empty;
        public bool IsNewFile { get; init; }
    }

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

        public Func<AgentFileEditPreview, Task<bool>>? ConfirmFileEditAsync { get; set; }
        public Func<string, Task>? FileModifiedAsync { get; set; }

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
            string fullPath = ResolveInsideWorkspace(path, allowOutside: true);
            if (!File.Exists(fullPath))
            {
                return $"read_file failed: file not found: {path}";
            }

            int start = Math.Max(1, startLine <= 0 ? 1 : startLine);
            int count = Math.Clamp(lineCount <= 0 ? 160 : lineCount, 1, 600);

            var readLines = new List<string>();
            int currentLine = 0;
            int endLine = start + count - 1;

            using (var reader = new StreamReader(fullPath, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    currentLine++;
                    if (currentLine >= start && currentLine <= endLine)
                    {
                        readLines.Add(line);
                    }
                    if (currentLine > endLine)
                    {
                        break;
                    }
                }
            }

            if (start > currentLine && currentLine > 0)
            {
                return $"read_file: startLine {start} is beyond EOF ({currentLine} lines).";
            }

            var builder = new StringBuilder();
            for (int i = 0; i < readLines.Count; i++)
            {
                int lineNumber = start + i;
                builder.Append(lineNumber.ToString().PadLeft(5));
                builder.Append(" | ");
                builder.AppendLine(readLines[i]);
            }

            return builder.ToString();
        }

        public async Task<string> RunRgAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "run_rg failed: arguments are empty.";
            }

            return await RunProcessAsync("rg", arguments, ResolveWorkspaceRoot(), timeoutMs <= 0 ? 10000 : timeoutMs, cancellationToken);
        }

        public async Task<string> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(command))
            {
                return "run_powershell failed: command is empty.";
            }

            if (LooksDestructive(command))
            {
                return "run_powershell blocked: destructive commands are not executed automatically by Agent.";
            }

            string utf8Command = $"$OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; {command}";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Command));
            var profile = TerminalShellProfile.Resolve("PowerShell");
            string shellPath = profile.ExecutablePath;
            return await RunProcessAsync(shellPath, $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}", ResolveWorkspaceRoot(), timeoutMs <= 0 ? 10000 : timeoutMs, cancellationToken);
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

            string newContent = NormalizeNewlines(content);
            var preview = new AgentFileEditPreview
            {
                ActionName = "create_file",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = string.Empty,
                NewContent = newContent,
                IsNewFile = true
            };

            if (!await ConfirmEditAsync(preview))
            {
                return $"create_file cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, newContent);
            await NotifyFileModifiedAsync(fullPath);
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

            string content = NormalizeNewlines(await File.ReadAllTextAsync(fullPath));
            string normalizedOldText = NormalizeNewlines(oldText);
            string normalizedNewText = NormalizeNewlines(newText);
            int index = content.IndexOf(normalizedOldText, StringComparison.Ordinal);
            if (index < 0)
            {
                return "replace_in_file failed: oldText was not found exactly.";
            }

            string updated = content.Remove(index, normalizedOldText.Length).Insert(index, normalizedNewText);
            var preview = new AgentFileEditPreview
            {
                ActionName = "replace_in_file",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await ConfirmEditAsync(preview))
            {
                return $"replace_in_file cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, updated);
            await NotifyFileModifiedAsync(fullPath);
            return $"modified: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        public async Task<string> OverwriteFileAsync(string path, string content)
        {
            string fullPath = ResolveInsideWorkspace(path);
            string oldContent = File.Exists(fullPath)
                ? NormalizeNewlines(await File.ReadAllTextAsync(fullPath))
                : string.Empty;
            string newContent = NormalizeNewlines(content);

            var preview = new AgentFileEditPreview
            {
                ActionName = "overwrite_file",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = !File.Exists(fullPath)
            };

            if (!await ConfirmEditAsync(preview))
            {
                return $"overwrite_file cancelled: {path}";
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, newContent);
            await NotifyFileModifiedAsync(fullPath);
            return $"overwritten: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }


        private async Task<bool> ConfirmEditAsync(AgentFileEditPreview preview)
        {
            if (ConfirmFileEditAsync == null)
            {
                return true;
            }

            return await ConfirmFileEditAsync(preview);
        }

        private async Task NotifyFileModifiedAsync(string fullPath)
        {
            if (FileModifiedAsync != null)
            {
                await FileModifiedAsync(fullPath);
            }
        }

        private static async Task<string> RunProcessAsync(string fileName, string arguments, string workingDirectory, int timeoutMs, CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"{fileName} failed to start: {ex.Message}";
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task exitTask = process.WaitForExitAsync(cancellationToken);

            try
            {
                Task completed = await Task.WhenAny(exitTask, Task.Delay(Math.Clamp(timeoutMs, 1000, 60000), cancellationToken));
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

                await exitTask;
            }
            catch (OperationCanceledException)
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

                throw;
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

        private string ResolveInsideWorkspace(string path, bool allowOutside = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Path is empty.");
            }

            string root = ResolveWorkspaceRoot();
            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            if (IsInsideRoot(root, candidate))
            {
                return candidate;
            }

            if (allowOutside && Path.IsPathRooted(path) && File.Exists(candidate))
            {
                return candidate;
            }

            throw new InvalidOperationException($"Path escapes workspace root: {path}");
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
            return IsInsideRoot(root, path)
                ? Path.GetRelativePath(root, path).Replace('\\', '/')
                : path;
        }

        private static bool IsInsideRoot(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path);
            string rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeNewlines(string? content)
        {
            return (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
