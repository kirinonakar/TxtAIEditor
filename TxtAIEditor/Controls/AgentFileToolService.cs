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
        public Func<string, Task<bool>>? ConfirmPowerShellAsync { get; set; }
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
            if (string.IsNullOrWhiteSpace(path))
            {
                return "read_file failed: path is empty. Provide the exact file path from the user task or list_files.";
            }

            string fullPath = ResolveInsideWorkspace(path, allowOutside: true);
            if (!File.Exists(fullPath))
            {
                return BuildMissingFileMessage("read_file", path);
            }

            int start = Math.Max(1, startLine <= 0 ? 1 : startLine);
            int count = Math.Clamp(lineCount <= 0 ? 160 : lineCount, 1, 5000);

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
                }
            }

            if (start > currentLine && currentLine > 0)
            {
                return $"read_file: startLine {start} is beyond EOF ({currentLine} lines).";
            }

            var builder = new StringBuilder();
            int lastRead = start + readLines.Count - 1;
            builder.AppendLine($"[File: {path} | Lines {start} to {lastRead} of {currentLine} total lines]");
            for (int i = 0; i < readLines.Count; i++)
            {
                int lineNumber = start + i;
                builder.Append(lineNumber.ToString().PadLeft(5));
                builder.Append(" | ");
                builder.AppendLine(readLines[i]);
            }

            if (lastRead < currentLine)
            {
                builder.AppendLine($"[... {currentLine - lastRead} more lines. Use read_file with startLine={lastRead + 1} and larger lineCount if needed to read more ...]");
            }

            return builder.ToString();
        }

        private string BuildMissingFileMessage(string toolName, string path)
        {
            var builder = new StringBuilder();
            builder.Append($"{toolName} failed: file not found: {path}");

            var suggestions = SuggestWorkspaceFiles(path, 10).ToList();
            if (suggestions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Possible workspace matches. Preserve exact non-English file names; do not translate them:");
                foreach (string suggestion in suggestions)
                {
                    builder.AppendLine($"- {suggestion}");
                }
            }

            return builder.ToString();
        }

        private IEnumerable<string> SuggestWorkspaceFiles(string path, int maxResults)
        {
            string root = ResolveWorkspaceRoot();
            string requestedName = Path.GetFileName(path);
            string requestedExtension = Path.GetExtension(path);
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in EnumerateWorkspaceFiles(root))
            {
                string relative = RelativePath(root, filePath);
                if (string.Equals(Path.GetFileName(filePath), requestedName, StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(relative))
                {
                    yield return relative;
                    if (yielded.Count >= maxResults) yield break;
                }
            }

            if (string.IsNullOrWhiteSpace(requestedExtension))
            {
                yield break;
            }

            foreach (string filePath in EnumerateWorkspaceFiles(root))
            {
                string relative = RelativePath(root, filePath);
                if (string.Equals(Path.GetExtension(filePath), requestedExtension, StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(relative))
                {
                    yield return relative;
                    if (yielded.Count >= maxResults) yield break;
                }
            }
        }

        public async Task<string> RunRgAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "run_rg failed: arguments are empty.";
            }

            string resolvedRg = ResolveExecutablePath("rg");
            string workspaceRoot = ResolveWorkspaceRoot();

            string result = await RunProcessAsync(resolvedRg, arguments, workspaceRoot, timeoutMs <= 0 ? 10000 : timeoutMs, cancellationToken);

            if (result.Contains("failed to start") || result.Contains("timed out after"))
            {
                string query = ExtractQueryFromRgArguments(arguments);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    string fallbackResult = await SearchTextAsync(query, null, 80);
                    return $"[run_rg failed: fell back to search_text for query \"{query}\"]\n{fallbackResult}";
                }
            }

            return result;
        }

        public async Task<string> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(command))
            {
                return "run_powershell failed: command is empty.";
            }

            if (!IsClearlySafePowerShell(command))
            {
                if (ConfirmPowerShellAsync == null || !await ConfirmPowerShellAsync(command))
                {
                    return "run_powershell cancelled by user.";
                }
            }

            // Normalize command line endings to CRLF for Windows PowerShell compatibility
            string normalizedCommand = command.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

            // Force UTF-8 for PowerShell and Python subprocesses so Korean text printed
            // through redirected stdout/stderr is decoded consistently by the agent pane.
            string utf8Command = "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
                "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8 } catch {}; " +
                "$env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'; " +
                normalizedCommand;
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Command));
            
            var profile = TerminalShellProfile.Resolve("PowerShell");
            string shellPath = profile.ExecutablePath;

            var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8"
            };

            return await RunProcessAsync(
                shellPath,
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                ResolveWorkspaceRoot(),
                timeoutMs <= 0 ? 10000 : timeoutMs,
                cancellationToken,
                Encoding.UTF8,
                environmentVariables);
        }

        public async Task<string> CreateFileAsync(string path, string content)
        {
            string originalPath = path;
            bool wasRenamed = false;
            string fullPath = ResolveInsideWorkspace(path);
            if (File.Exists(fullPath))
            {
                wasRenamed = true;
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                string filenameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
                string extension = Path.GetExtension(fullPath);

                int counter = 1;
                string newFullPath;
                do
                {
                    string newFilename = $"{filenameWithoutExtension} ({counter}){extension}";
                    newFullPath = Path.Combine(directory, newFilename);
                    counter++;
                } while (File.Exists(newFullPath));

                fullPath = newFullPath;
                path = RelativePath(ResolveWorkspaceRoot(), fullPath).Replace('\\', '/');
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
                return wasRenamed
                    ? $"create_file cancelled: {path} (Note: '{originalPath}' already existed, so it was renamed to '{path}')"
                    : $"create_file cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, newContent);
            await NotifyFileModifiedAsync(fullPath);

            string finalRelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath);
            return wasRenamed
                ? $"created: {finalRelativePath} (Note: '{originalPath}' already existed, so the file was renamed to '{finalRelativePath}')"
                : $"created: {finalRelativePath}";
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

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            string normalizedOldText = NormalizeNewlines(oldText);
            string normalizedNewText = NormalizeNewlines(newText);
            
            int index = content.IndexOf(normalizedOldText, StringComparison.Ordinal);
            int matchLength = normalizedOldText.Length;

            if (index < 0)
            {
                // Fallback to relaxed line-by-line matching
                string[] lines = content.Split('\n');
                string[] oldLines = normalizedOldText.Split('\n');
                
                var lineIndices = new int[lines.Length];
                int currentIdx = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    lineIndices[i] = currentIdx;
                    currentIdx += lines[i].Length + 1;
                }

                bool MatchesAt(int startIdx, int mode)
                {
                    if (startIdx + oldLines.Length > lines.Length) return false;
                    for (int k = 0; k < oldLines.Length; k++)
                    {
                        string fileLine = lines[startIdx + k];
                        string queryLine = oldLines[k];
                        if (mode == 1)
                        {
                            if (fileLine.TrimEnd() != queryLine.TrimEnd()) return false;
                        }
                        else if (mode == 2)
                        {
                            if (fileLine.Trim() != queryLine.Trim()) return false;
                        }
                    }
                    return true;
                }

                var matchesMode1 = new List<int>();
                for (int i = 0; i <= lines.Length - oldLines.Length; i++)
                {
                    if (MatchesAt(i, 1))
                    {
                        matchesMode1.Add(i);
                    }
                }

                if (matchesMode1.Count == 1)
                {
                    int matchLineIdx = matchesMode1[0];
                    index = lineIndices[matchLineIdx];
                    int endLineIdx = matchLineIdx + oldLines.Length - 1;
                    matchLength = lineIndices[endLineIdx] + lines[endLineIdx].Length - index;
                }
                else if (matchesMode1.Count == 0)
                {
                    var matchesMode2 = new List<int>();
                    for (int i = 0; i <= lines.Length - oldLines.Length; i++)
                    {
                        if (MatchesAt(i, 2))
                        {
                            matchesMode2.Add(i);
                        }
                    }

                    if (matchesMode2.Count == 1)
                    {
                        int matchLineIdx = matchesMode2[0];
                        index = lineIndices[matchLineIdx];
                        int endLineIdx = matchLineIdx + oldLines.Length - 1;
                        matchLength = lineIndices[endLineIdx] + lines[endLineIdx].Length - index;
                    }
                }

                if (index < 0)
                {
                    return "replace_in_file failed: oldText was not found exactly.";
                }
            }

            string updated = content.Remove(index, matchLength).Insert(index, normalizedNewText);
            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("replace_in_file", fullPath);
            }

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

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await NotifyFileModifiedAsync(fullPath);
            return $"modified: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        public async Task<string> ReplaceRangeAsync(string path, int startLine, int endLine, string newText, string? expectedSnippet)
        {
            string fullPath = ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"replace_range failed: file not found: {path}";
            }

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            string[] lines = content.Split('\n');

            if (startLine < 1 || startLine > lines.Length)
            {
                return $"replace_range failed: startLine {startLine} is out of bounds (1-{lines.Length}).";
            }
            if (endLine < startLine || endLine > lines.Length)
            {
                return $"replace_range failed: endLine {endLine} is out of bounds (startLine-{lines.Length}).";
            }

            var targetLines = new List<string>();
            for (int i = startLine - 1; i <= endLine - 1; i++)
            {
                targetLines.Add(lines[i]);
            }
            string targetText = string.Join("\n", targetLines);

            if (!string.IsNullOrEmpty(expectedSnippet))
            {
                string normalizedExpected = NormalizeNewlines(expectedSnippet);
                if (!targetText.Contains(normalizedExpected, StringComparison.Ordinal))
                {
                    string cleanTarget = Regex.Replace(targetText, @"\s+", " ").Trim();
                    string cleanExpected = Regex.Replace(normalizedExpected, @"\s+", " ").Trim();
                    if (!cleanTarget.Contains(cleanExpected, StringComparison.Ordinal))
                    {
                        return $"replace_range failed: expectedSnippet was not found in the target line range ({startLine}-{endLine}).";
                    }
                }
            }

            var beforeLines = lines.Take(startLine - 1);
            var afterLines = lines.Skip(endLine);
            string updated = string.Join("\n", beforeLines);
            if (startLine - 1 > 0)
            {
                updated += "\n";
            }
            updated += NormalizeNewlines(newText);
            if (afterLines.Any())
            {
                updated += "\n" + string.Join("\n", afterLines);
            }

            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("replace_range", fullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "replace_range",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await ConfirmEditAsync(preview))
            {
                return $"replace_range cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await NotifyFileModifiedAsync(fullPath);
            return $"modified: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        private class PatchHunk
        {
            public int OldStart { get; set; }
            public int OldCount { get; set; }
            public int NewStart { get; set; }
            public int NewCount { get; set; }
            public List<string> Lines { get; } = new List<string>();
        }

        public async Task<string> ApplyPatchAsync(string path, string patchText)
        {
            string fullPath = ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"apply_patch failed: file not found: {path}";
            }

            if (string.IsNullOrWhiteSpace(patchText))
            {
                return "apply_patch failed: patch content is empty.";
            }

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            List<string> lines = content.Split('\n').ToList();

            string[] patchLines = NormalizeNewlines(patchText).Split('\n');
            var hunkHeaderRegex = new Regex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);
            var hunks = new List<PatchHunk>();
            PatchHunk? currentHunk = null;

            foreach (string line in patchLines)
            {
                var match = hunkHeaderRegex.Match(line);
                if (match.Success)
                {
                    int oldStart = int.Parse(match.Groups[1].Value);
                    int oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                    int newStart = int.Parse(match.Groups[3].Value);
                    int newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;

                    currentHunk = new PatchHunk
                    {
                        OldStart = oldStart,
                        OldCount = oldCount,
                        NewStart = newStart,
                        NewCount = newCount
                    };
                    hunks.Add(currentHunk);
                }
                else if (currentHunk != null)
                {
                    if (line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' '))
                    {
                        currentHunk.Lines.Add(line);
                    }
                }
            }

            if (hunks.Count == 0)
            {
                return "apply_patch failed: no valid hunks found in patch.";
            }

            // Apply hunks in descending order of OldStart to avoid line shifts
            var sortedHunks = hunks.OrderByDescending(h => h.OldStart).ToList();
            foreach (var hunk in sortedHunks)
            {
                int matchIndex = FindHunkMatch(lines, hunk);
                if (matchIndex < 0)
                {
                    return $"apply_patch failed: could not match hunk starting at line {hunk.OldStart} in file {path}.";
                }

                int fileLinesConsumed = 0;
                var replacementLines = new List<string>();
                foreach (string hunkLine in hunk.Lines)
                {
                    if (hunkLine.StartsWith(' '))
                    {
                        replacementLines.Add(hunkLine.Substring(1));
                        fileLinesConsumed++;
                    }
                    else if (hunkLine.StartsWith('-'))
                    {
                        fileLinesConsumed++;
                    }
                    else if (hunkLine.StartsWith('+'))
                    {
                        replacementLines.Add(hunkLine.Substring(1));
                    }
                }

                lines.RemoveRange(matchIndex, fileLinesConsumed);
                lines.InsertRange(matchIndex, replacementLines);
            }

            string updated = string.Join("\n", lines);
            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("apply_patch", fullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "apply_patch",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await ConfirmEditAsync(preview))
            {
                return $"apply_patch cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await NotifyFileModifiedAsync(fullPath);
            return $"modified: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        private int FindHunkMatch(List<string> lines, PatchHunk hunk)
        {
            int expectedIdx = hunk.OldStart - 1;
            if (expectedIdx >= 0 && expectedIdx < lines.Count && IsHunkMatch(lines, expectedIdx, hunk))
            {
                return expectedIdx;
            }

            int maxWindow = 200;
            for (int offset = 1; offset <= maxWindow; offset++)
            {
                int up = expectedIdx - offset;
                if (up >= 0 && up < lines.Count && IsHunkMatch(lines, up, hunk))
                {
                    return up;
                }
                int down = expectedIdx + offset;
                if (down >= 0 && down < lines.Count && IsHunkMatch(lines, down, hunk))
                {
                    return down;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (Math.Abs(i - expectedIdx) > maxWindow && IsHunkMatch(lines, i, hunk))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsHunkMatch(List<string> lines, int fileIndex, PatchHunk hunk)
        {
            int fileLineIdx = fileIndex;
            foreach (string hunkLine in hunk.Lines)
            {
                if (hunkLine.StartsWith(' ') || hunkLine.StartsWith('-'))
                {
                    if (fileLineIdx >= lines.Count)
                    {
                        return false;
                    }
                    string expectedText = hunkLine.Substring(1);
                    if (lines[fileLineIdx] != expectedText)
                    {
                        return false;
                    }
                    fileLineIdx++;
                }
            }
            return true;
        }


        public async Task<string> OverwriteFileAsync(string path, string content)
        {
            string fullPath = ResolveInsideWorkspace(path);
            string rawText = File.Exists(fullPath)
                ? await File.ReadAllTextAsync(fullPath)
                : string.Empty;
            string lineEnding = DetectLineEnding(rawText);
            
            string oldContent = NormalizeNewlines(rawText);
            string newContent = NormalizeNewlines(content);
            bool isNewFile = !File.Exists(fullPath);
            if (!isNewFile && string.Equals(newContent, oldContent, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("overwrite_file", fullPath);
            }
 
            var preview = new AgentFileEditPreview
            {
                ActionName = "overwrite_file",
                RelativePath = RelativePath(ResolveWorkspaceRoot(), fullPath),
                FullPath = fullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = isNewFile
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
 
            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(newContent, lineEnding));
            await NotifyFileModifiedAsync(fullPath);
            return $"overwritten: {RelativePath(ResolveWorkspaceRoot(), fullPath)}";
        }

        private string BuildUnchangedEditResult(string toolName, string fullPath)
        {
            return $"{toolName} unchanged: {RelativePath(ResolveWorkspaceRoot(), fullPath)} requested change is already applied; no additional edit was needed.";
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static Encoding GetSystemAnsiEncoding()
        {
            try
            {
                int acp = (int)GetACP();
                if (acp > 0)
                {
                    return Encoding.GetEncoding(acp);
                }
            }
            catch
            {
            }
            return Encoding.UTF8;
        }

        private static string DecodeBytes(byte[] bytes, Encoding preferredEncoding)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (TextEncodingService.IsValidUtf8(bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            try
            {
                var systemEncoding = GetSystemAnsiEncoding();
                return systemEncoding.GetString(bytes);
            }
            catch
            {
                return preferredEncoding.GetString(bytes);
            }
        }

        private static string ParseCliXml(string clixml)
        {
            if (string.IsNullOrWhiteSpace(clixml) || !clixml.StartsWith("#< CLIXML"))
            {
                return clixml;
            }

            try
            {
                string xmlContent = clixml.Substring("#< CLIXML".Length).Trim();
                var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
                var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                
                var lines = new List<string>();

                var sElements = doc.Descendants(ns + "S").ToList();
                foreach (var s in sElements)
                {
                    string? sAttr = s.Attribute("S")?.Value;
                    string? nAttr = s.Attribute("N")?.Value;

                    if (sAttr == "Error" || sAttr == "Warning")
                    {
                        lines.Add(DecodeCliXmlString(s.Value));
                    }
                    else if (nAttr == "Message")
                    {
                        lines.Add(DecodeCliXmlString(s.Value));
                    }
                }

                if (lines.Count > 0)
                {
                    return string.Join("", lines).Trim();
                }

                var toStrings = doc.Descendants(ns + "ToString").Select(x => x.Value).ToList();
                if (toStrings.Count > 0)
                {
                    return string.Join(Environment.NewLine, toStrings.Select(DecodeCliXmlString)).Trim();
                }
            }
            catch
            {
            }

            try
            {
                var matches = Regex.Matches(clixml, @"<S\b[^>]*>([^<]*)</S>");
                var fallbackLines = new List<string>();
                foreach (Match m in matches)
                {
                    string val = m.Groups[1].Value;
                    fallbackLines.Add(DecodeCliXmlString(val));
                }
                if (fallbackLines.Count > 0)
                {
                    return string.Join("", fallbackLines).Trim();
                }
            }
            catch {}

            return clixml;
        }

        private static string DecodeCliXmlString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string decoded = Regex.Replace(input, @"_x([0-9a-fA-F]{4})_", m => 
            {
                return ((char)Convert.ToUInt16(m.Groups[1].Value, 16)).ToString();
            });
            decoded = Regex.Replace(decoded, @"\x1B\[[0-9;]*[a-zA-Z]", "");
            return decoded;
        }

        private static async Task<string> RunProcessAsync(
            string fileName, 
            string arguments, 
            string workingDirectory, 
            int timeoutMs, 
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null)
        {
            var output = new StringBuilder();
            using var process = new Process();
            var encoding = outputEncoding ?? Encoding.UTF8;
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding
            };

            if (environmentVariables != null)
            {
                foreach (var pair in environmentVariables)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            process.StartInfo = startInfo;

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

            Task<byte[]> stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
            Task<byte[]> stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream, cancellationToken);
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

            string stdout = DecodeBytes(await stdoutTask, encoding);
            string stderr = ParseCliXml(DecodeBytes(await stderrTask, encoding));

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

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            return memoryStream.ToArray();
        }

        private string ResolveWorkspaceRoot()
        {
            string root = _workspaceRootProvider();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string fullPath = Path.GetFullPath(root);
            try
            {
                var dirInfo = new DirectoryInfo(fullPath);
                if (dirInfo.Exists)
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        fullPath = target.FullName;
                    }
                }
            }
            catch
            {
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ResolveExecutablePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            string candidate = string.Empty;
            if (Path.IsPathRooted(fileName))
            {
                candidate = fileName;
            }
            else
            {
                string? pathValue = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathValue))
                {
                    string searchName = fileName;
                    if (!searchName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        searchName += ".exe";
                    }

                    foreach (string directory in pathValue.Split(Path.PathSeparator))
                    {
                        if (string.IsNullOrWhiteSpace(directory))
                        {
                            continue;
                        }

                        try
                        {
                            string path = Path.Combine(directory.Trim(), searchName);
                            if (File.Exists(path))
                            {
                                candidate = path;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
            {
                return fileName;
            }

            try
            {
                var fileInfo = new FileInfo(candidate);
                if (fileInfo.Exists)
                {
                    var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        return target.FullName;
                    }
                }
            }
            catch
            {
            }

            return candidate;
        }

        private static string ExtractQueryFromRgArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return string.Empty;
            }

            var match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+""([^""]+)""");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+'([^']+)'");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+(\S+)");
            if (match.Success) return match.Groups[1].Value;

            var quotedMatches = Regex.Matches(arguments, @"""([^""]+)""");
            if (quotedMatches.Count > 0)
            {
                foreach (Match m in quotedMatches)
                {
                    string val = m.Groups[1].Value;
                    if (!val.StartsWith('-'))
                    {
                        return val;
                    }
                }
            }

            quotedMatches = Regex.Matches(arguments, @"'([^']+)'");
            if (quotedMatches.Count > 0)
            {
                foreach (Match m in quotedMatches)
                {
                    string val = m.Groups[1].Value;
                    if (!val.StartsWith('-'))
                    {
                        return val;
                    }
                }
            }

            string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string token = tokens[i];
                if (!token.StartsWith('-'))
                {
                    if (i > 0 && (tokens[i - 1] == "-g" || tokens[i - 1] == "-t" || tokens[i - 1] == "--type" || tokens[i - 1] == "-e"))
                    {
                        continue;
                    }
                    return token;
                }
            }

            string cleaned = Regex.Replace(arguments, @"-[a-zA-Z0-9\-]+", "").Trim();
            cleaned = cleaned.Replace("\"", "").Replace("'", "").Trim();
            return cleaned;
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
                .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";

            return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase);
        }

        private static bool IsClearlySafePowerShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string normalized = command.Trim().ToLowerInvariant();

            // Semicolon, double ampersand, pipeline, redirects, backticks, or subexpressions make it not "clearly safe"
            string[] riskyOperators = { ";", "&&", "||", "|", ">", ">>", "$(", "@(", "&" };
            if (riskyOperators.Any(op => normalized.Contains(op, StringComparison.Ordinal)))
            {
                return false;
            }

            string[] safePrefixes =
            {
                "get-childitem",
                "gci",
                "dir",
                "ls",
                "get-content",
                "gc",
                "select-string",
                "test-path",
                "get-item",
                "get-command",
                "where.exe",
                "git status",
                "git diff",
                "git log",
                "dotnet --info",
                "dotnet build",
                "dotnet test"
            };

            // Make sure it starts with a safe prefix and has a boundary (like space or end of string)
            bool startsWithSafePrefix = safePrefixes.Any(prefix =>
            {
                if (normalized == prefix) return true;
                if (normalized.StartsWith(prefix + " ", StringComparison.Ordinal)) return true;
                return false;
            });

            if (!startsWithSafePrefix)
            {
                return false;
            }

            // Double check it doesn't contain any risky content
            string[] risky =
            {
                "set-content", "add-content", "out-file",
                "new-item", "remove-item", "move-item", "rename-item", "copy-item",
                "invoke-expression", "iex", "invoke-webrequest", "iwr", "curl",
                "start-process", "cmd ", "cmd.exe", "powershell", "pwsh",
                "reg ", "schtasks", "icacls", "takeown"
            };

            if (risky.Any(x => normalized.Contains(x, StringComparison.Ordinal)))
            {
                return false;
            }

            return true;
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

        private static string DetectLineEnding(string text)
        {
            return text.Contains("\r\n") ? "\r\n" : "\n";
        }

        private static string RestoreLineEndings(string text, string lineEnding)
        {
            return lineEnding == "\r\n" ? text.Replace("\n", "\r\n") : text;
        }
    }
}
