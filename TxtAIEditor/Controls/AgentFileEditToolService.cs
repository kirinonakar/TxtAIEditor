using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static TxtAIEditor.Controls.AgentTextContentUtilities;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentFileEditToolService
    {
        private readonly AgentWorkspaceFileResolver _workspace;
        private readonly Func<AgentFileEditPreview, Task<bool>> _confirmEditAsync;
        private readonly Func<string, Task> _notifyFileModifiedAsync;
        private readonly Func<AgentFileEditPreview, Task> _notifyFileEditCommittedAsync;

        public AgentFileEditToolService(
            AgentWorkspaceFileResolver workspace,
            Func<AgentFileEditPreview, Task<bool>> confirmEditAsync,
            Func<string, Task> notifyFileModifiedAsync,
            Func<AgentFileEditPreview, Task> notifyFileEditCommittedAsync)
        {
            _workspace = workspace;
            _confirmEditAsync = confirmEditAsync;
            _notifyFileModifiedAsync = notifyFileModifiedAsync;
            _notifyFileEditCommittedAsync = notifyFileEditCommittedAsync;
        }

        public async Task<string> CreateFileAsync(string path, string content)
        {
            string originalPath = path;
            bool wasRenamed = false;
            string fullPath = _workspace.ResolveInsideWorkspace(path);
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
                path = _workspace.RelativePath(fullPath).Replace('\\', '/');
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
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = string.Empty,
                NewContent = newContent,
                IsNewFile = true
            };

            if (!await _confirmEditAsync(preview))
            {
                return wasRenamed
                    ? $"create_file cancelled: {path} (Note: '{originalPath}' already existed, so it was renamed to '{path}')"
                    : $"create_file cancelled: {path}";
            }

            string finalRelativePath = _workspace.RelativePath(fullPath);
            await File.WriteAllTextAsync(fullPath, newContent);
            if (!File.Exists(fullPath))
            {
                return $"create_file failed: file was not found after write: {finalRelativePath}";
            }

            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);

            string result = wasRenamed
                ? $"created: {finalRelativePath} (Note: '{originalPath}' already existed, so the file was renamed to '{finalRelativePath}')"
                : $"created: {finalRelativePath}";
            return $"{result}\nfull_path: {fullPath}";
        }

        public async Task<string> ReplaceInFileAsync(string path, string oldText, string newText)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
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
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"replace_in_file cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"modified: {_workspace.RelativePath(fullPath)}";
        }

        public async Task<string> SearchReplaceAsync(
            string path,
            string searchText,
            string replacementText,
            bool useRegex,
            bool matchCase,
            bool wholeWord,
            int maxReplacements,
            int startLine,
            int endLine,
            int? allowedStartLine = null,
            int? allowedEndLine = null)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"search_replace failed: file not found: {path}";
            }

            if (string.IsNullOrEmpty(searchText))
            {
                return "search_replace failed: search text is empty.";
            }

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            string[] lines = content.Split('\n');

            if (startLine <= 0 && endLine <= 0)
            {
                startLine = allowedStartLine ?? 1;
                endLine = allowedEndLine ?? lines.Length;
            }
            else
            {
                if (startLine <= 0)
                {
                    startLine = 1;
                }

                if (endLine <= 0)
                {
                    endLine = lines.Length;
                }
            }

            if (allowedStartLine.HasValue && startLine < allowedStartLine.Value)
            {
                return $"search_replace failed: startLine {startLine} is outside the allowed range ({allowedStartLine.Value}-{allowedEndLine ?? lines.Length}).";
            }

            if (allowedEndLine.HasValue && endLine > allowedEndLine.Value)
            {
                return $"search_replace failed: endLine {endLine} is outside the allowed range ({allowedStartLine ?? 1}-{allowedEndLine.Value}).";
            }

            if (startLine < 1 || startLine > lines.Length)
            {
                return $"search_replace failed: startLine {startLine} is out of bounds (1-{lines.Length}).";
            }

            if (endLine < startLine || endLine > lines.Length)
            {
                return $"search_replace failed: endLine {endLine} is out of bounds (startLine-{lines.Length}).";
            }

            int startOffset = GetLineStartOffset(lines, startLine);
            int endOffset = GetLineEndOffset(lines, endLine);
            string targetText = content.Substring(startOffset, endOffset - startOffset);

            string pattern = useRegex ? searchText : Regex.Escape(searchText);
            if (wholeWord)
            {
                pattern = $@"\b(?:{pattern})\b";
            }

            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!matchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            string normalizedReplacement = NormalizeNewlines(replacementText);
            string replacedText;
            int replacementCount;

            try
            {
                var regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
                int matchCount = regex.Matches(targetText).Count;
                if (matchCount == 0)
                {
                    return $"search_replace failed: no matches found in {path} lines {startLine}-{endLine}.";
                }

                replacementCount = maxReplacements > 0
                    ? Math.Min(maxReplacements, matchCount)
                    : matchCount;

                if (useRegex)
                {
                    replacedText = maxReplacements > 0
                        ? regex.Replace(targetText, normalizedReplacement, maxReplacements)
                        : regex.Replace(targetText, normalizedReplacement);
                }
                else
                {
                    replacedText = maxReplacements > 0
                        ? regex.Replace(targetText, _ => normalizedReplacement, maxReplacements)
                        : regex.Replace(targetText, _ => normalizedReplacement);
                }
            }
            catch (ArgumentException ex)
            {
                return $"search_replace failed: invalid {(useRegex ? "regex" : "search")} pattern: {ex.Message}";
            }
            catch (RegexMatchTimeoutException)
            {
                return "search_replace failed: regex matching timed out.";
            }

            string updated = content.Substring(0, startOffset) + replacedText + content.Substring(endOffset);
            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("search_replace", fullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "search_replace",
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"search_replace cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);

            string replacementLabel = replacementCount == 1 ? "replacement" : "replacements";
            string regexLabel = useRegex ? " regex" : string.Empty;
            return $"modified: {_workspace.RelativePath(fullPath)} ({replacementCount}{regexLabel} {replacementLabel}, lines {startLine}-{endLine})";
        }

        public async Task<string> ReplaceRangeAsync(
            string path,
            int startLine,
            int endLine,
            string newText,
            string? expectedSnippet,
            int? allowedStartLine = null,
            int? allowedEndLine = null,
            List<string>? expectedStartLines = null,
            List<string>? expectedEndLines = null)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
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

            string? rangeAdjustmentNote = null;
            var targetLines = new List<string>();
            for (int i = startLine - 1; i <= endLine - 1; i++)
            {
                targetLines.Add(lines[i]);
            }
            string targetText = string.Join("\n", targetLines);

            bool LinesMatch(string actual, string expected)
            {
                if (string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    return true;
                }
                return string.Equals(
                    NormalizeWhitespaceForSnippetComparison(actual),
                    NormalizeWhitespaceForSnippetComparison(expected),
                    StringComparison.Ordinal);
            }

            int lineCount = endLine - startLine + 1;
            if (lineCount >= 5)
            {
                // Boundary verification

                // expectedStartLines
                if (expectedStartLines == null || expectedStartLines.Count != 2)
                {
                    return "replace_range failed: expectedStartLines is required and must have exactly 2 elements.";
                }
                if (!LinesMatch(lines[startLine - 1], expectedStartLines[0]))
                {
                    return $"replace_range failed: expectedStartLines[0] did not match line {startLine} of the file.";
                }
                if (!LinesMatch(lines[startLine], expectedStartLines[1]))
                {
                    return $"replace_range failed: expectedStartLines[1] did not match line {startLine + 1} of the file.";
                }

                // expectedEndLines
                if (expectedEndLines == null || expectedEndLines.Count != 2)
                {
                    return "replace_range failed: expectedEndLines is required and must have exactly 2 elements.";
                }
                if (!LinesMatch(lines[endLine - 2], expectedEndLines[0]))
                {
                    return $"replace_range failed: expectedEndLines[0] did not match line {endLine - 1} of the file.";
                }
                if (!LinesMatch(lines[endLine - 1], expectedEndLines[1]))
                {
                    return $"replace_range failed: expectedEndLines[1] did not match line {endLine} of the file.";
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(expectedSnippet))
                {
                    string normalizedExpected = TrimBoundaryNewlines(NormalizeNewlines(expectedSnippet));
                    if (!string.IsNullOrEmpty(normalizedExpected))
                    {
                        bool expectedMatchesFullRange =
                            string.Equals(targetText, normalizedExpected, StringComparison.Ordinal) ||
                            string.Equals(
                                NormalizeWhitespaceForSnippetComparison(targetText),
                                NormalizeWhitespaceForSnippetComparison(normalizedExpected),
                                StringComparison.Ordinal);

                        if (!expectedMatchesFullRange)
                        {
                            return $"replace_range failed: expectedSnippet did not exactly match the text in the requested range ({startLine}-{endLine}).";
                        }
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
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"replace_range cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"modified: {_workspace.RelativePath(fullPath)}{rangeAdjustmentNote}";
        }

        public async Task<string> ApplyPatchAsync(string path, string patchText)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
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
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = content,
                NewContent = updated
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"apply_patch cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"modified: {_workspace.RelativePath(fullPath)}";
        }

        public async Task<string> OverwriteFileAsync(string path, string content)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
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
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = isNewFile
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"overwrite_file cancelled: {path}";
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(newContent, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"overwritten: {_workspace.RelativePath(fullPath)}";
        }

        public async Task<string> AppendToFileAsync(string path, string content)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            string rawText = File.Exists(fullPath)
                ? await File.ReadAllTextAsync(fullPath)
                : string.Empty;
            string lineEnding = DetectLineEnding(rawText);

            string oldContent = NormalizeNewlines(rawText);
            string newContent = oldContent;
            if (!string.IsNullOrEmpty(oldContent) && !oldContent.EndsWith("\n"))
            {
                newContent += "\n";
            }
            newContent += NormalizeNewlines(content);

            bool isNewFile = !File.Exists(fullPath);
            if (!isNewFile && string.Equals(newContent, oldContent, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("append_to_file", fullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "append_to_file",
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = isNewFile
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"append_to_file cancelled: {path}";
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(newContent, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"appended: {_workspace.RelativePath(fullPath)}";
        }

        public async Task<string> MergeFilesAsync(string[] paths, string targetPath)
        {
            if (paths == null || paths.Length == 0)
            {
                return "merge_files failed: no source paths provided.";
            }
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return "merge_files failed: targetPath is empty.";
            }

            var mergedLines = new List<string>();
            string lineEnding = "\r\n";
            bool firstFile = true;

            string targetFullPath = _workspace.ResolveInsideWorkspace(targetPath);

            foreach (string path in paths)
            {
                string fullPath = _workspace.ResolveInsideWorkspace(path, allowOutside: true);
                if (!File.Exists(fullPath))
                {
                    return $"merge_files failed: source file not found: {path}";
                }

                string rawText = await File.ReadAllTextAsync(fullPath);
                if (firstFile)
                {
                    lineEnding = DetectLineEnding(rawText);
                    firstFile = false;
                }

                string normalized = NormalizeNewlines(rawText);
                if (!string.IsNullOrEmpty(normalized))
                {
                    mergedLines.Add(normalized);
                }
            }

            string newContent = string.Join("\n", mergedLines);
            string oldContent = File.Exists(targetFullPath)
                ? NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath))
                : string.Empty;

            bool isNewFile = !File.Exists(targetFullPath);
            if (!isNewFile && string.Equals(newContent, oldContent, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("merge_files", targetFullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "merge_files",
                RelativePath = _workspace.RelativePath(targetFullPath),
                FullPath = targetFullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = isNewFile
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"merge_files cancelled: {targetPath}";
            }

            string? dir = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(targetFullPath);
            return $"merged: {_workspace.RelativePath(targetFullPath)}";
        }

        public async Task<string> InsertIntoFileAsync(string path, string content, string before, string after)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"insert_to_file failed: file not found: {path}";
            }

            if (string.IsNullOrEmpty(content))
            {
                return "insert_to_file failed: content is empty.";
            }

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string fileContent = NormalizeNewlines(rawText);

            string normalizedContent = NormalizeNewlines(content);
            string normalizedBefore = NormalizeNewlines(before ?? string.Empty);
            string normalizedAfter = NormalizeNewlines(after ?? string.Empty);

            if (string.IsNullOrEmpty(normalizedBefore) && string.IsNullOrEmpty(normalizedAfter))
            {
                return "insert_to_file failed: at least one of before or after context must be provided.";
            }

            int insertIndex = FindInsertionPoint(fileContent, normalizedBefore, normalizedAfter);
            if (insertIndex < 0)
            {
                return "insert_to_file failed: could not find a unique insertion point matching the provided context lines.";
            }

            string insertionText = normalizedContent;
            if (insertIndex > 0 && fileContent[insertIndex - 1] != '\n')
            {
                insertionText = "\n" + insertionText;
            }
            if (!insertionText.EndsWith("\n"))
            {
                insertionText += "\n";
            }

            string updated = fileContent.Insert(insertIndex, insertionText);
            if (string.Equals(updated, fileContent, StringComparison.Ordinal))
            {
                return BuildUnchangedEditResult("insert_to_file", fullPath);
            }

            var preview = new AgentFileEditPreview
            {
                ActionName = "insert_to_file",
                RelativePath = _workspace.RelativePath(fullPath),
                FullPath = fullPath,
                OldContent = fileContent,
                NewContent = updated
            };

            if (!await _confirmEditAsync(preview))
            {
                return $"insert_to_file cancelled: {path}";
            }

            await File.WriteAllTextAsync(fullPath, RestoreLineEndings(updated, lineEnding));
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"inserted: {_workspace.RelativePath(fullPath)}";
        }

        private static int FindInsertionPoint(string fileContent, string before, string after)
        {
            string[] lines = fileContent.Split('\n');
            string[] beforeLines = string.IsNullOrEmpty(before) ? Array.Empty<string>() : before.Split('\n');
            string[] afterLines = string.IsNullOrEmpty(after) ? Array.Empty<string>() : after.Split('\n');

            List<int> FindCandidates(int mode)
            {
                var candidates = new List<int>();
                for (int i = 0; i <= lines.Length; i++)
                {
                    bool beforeMatch = true;
                    if (beforeLines.Length > 0)
                    {
                        int beforeStart = i - beforeLines.Length;
                        if (beforeStart < 0) continue;
                        for (int j = 0; j < beforeLines.Length; j++)
                        {
                            if (beforeStart + j >= lines.Length)
                            {
                                beforeMatch = false;
                                break;
                            }
                            string fileLine = lines[beforeStart + j];
                            string queryLine = beforeLines[j];
                            bool lineMatches = mode == 1
                                ? fileLine.TrimEnd() == queryLine.TrimEnd()
                                : fileLine.Trim() == queryLine.Trim();

                            if (!lineMatches)
                            {
                                beforeMatch = false;
                                break;
                            }
                        }
                    }

                    if (!beforeMatch) continue;

                    bool afterMatch = true;
                    if (afterLines.Length > 0)
                    {
                        if (i + afterLines.Length > lines.Length) continue;
                        for (int j = 0; j < afterLines.Length; j++)
                        {
                            string fileLine = lines[i + j];
                            string queryLine = afterLines[j];
                            bool lineMatches = mode == 1
                                ? fileLine.TrimEnd() == queryLine.TrimEnd()
                                : fileLine.Trim() == queryLine.Trim();

                            if (!lineMatches)
                            {
                                afterMatch = false;
                                break;
                            }
                        }
                    }

                    if (beforeMatch && afterMatch)
                    {
                        candidates.Add(i);
                    }
                }
                return candidates;
            }

            var candidateLineIndices = FindCandidates(1);
            if (candidateLineIndices.Count == 0)
            {
                candidateLineIndices = FindCandidates(2);
            }

            if (candidateLineIndices.Count != 1)
            {
                return -1;
            }

            int insertLineIndex = candidateLineIndices[0];
            int offset = 0;
            for (int i = 0; i < insertLineIndex; i++)
            {
                offset += lines[i].Length + 1;
            }

            if (offset > fileContent.Length)
            {
                offset = fileContent.Length;
            }

            return offset;
        }

        public async Task<string> SplitFileAsync(string path, List<AgentFileToolService.SplitRange>? ranges, int linesPerFile)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "split_file failed: path is empty.";
            }

            string fullPath = _workspace.ResolveInsideWorkspace(path, allowOutside: true);
            if (!File.Exists(fullPath))
            {
                return _workspace.BuildMissingFileMessage("split_file", path);
            }

            string rawText = await File.ReadAllTextAsync(fullPath);
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            string[] lines = content.Split('\n');

            var outputResults = new List<string>();

            if (ranges != null && ranges.Count > 0)
            {
                foreach (var range in ranges)
                {
                    if (string.IsNullOrWhiteSpace(range.Path))
                    {
                        return "split_file failed: one of the ranges is missing a target path.";
                    }

                    int start = Math.Max(1, range.StartLine);
                    int end = range.EndLine > 0 ? range.EndLine : (range.LineCount > 0 ? start + range.LineCount - 1 : lines.Length);

                    if (start > lines.Length)
                    {
                        return $"split_file failed: startLine {start} is out of bounds (1-{lines.Length}).";
                    }
                    if (end < start)
                    {
                        return $"split_file failed: endLine/lineCount is invalid for range targeting {range.Path}.";
                    }

                    int endIdx = Math.Min(end, lines.Length);
                    var rangeLines = new List<string>();
                    for (int i = start - 1; i < endIdx; i++)
                    {
                        rangeLines.Add(lines[i]);
                    }

                    string newContent = string.Join("\n", rangeLines);
                    string targetFullPath = _workspace.ResolveInsideWorkspace(range.Path);
                    string oldContent = File.Exists(targetFullPath)
                        ? NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath))
                        : string.Empty;

                    bool isNewFile = !File.Exists(targetFullPath);
                    if (isNewFile || !string.Equals(newContent, oldContent, StringComparison.Ordinal))
                    {
                        var preview = new AgentFileEditPreview
                        {
                            ActionName = "split_file",
                            RelativePath = _workspace.RelativePath(targetFullPath),
                            FullPath = targetFullPath,
                            OldContent = oldContent,
                            NewContent = newContent,
                            IsNewFile = isNewFile
                        };

                        if (!await _confirmEditAsync(preview))
                        {
                            outputResults.Add($"skipped (cancelled by user): {range.Path}");
                            continue;
                        }

                        string? dir = Path.GetDirectoryName(targetFullPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        await File.WriteAllTextAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding));
                        await _notifyFileEditCommittedAsync(preview);
                        await _notifyFileModifiedAsync(targetFullPath);
                        outputResults.Add($"created: {_workspace.RelativePath(targetFullPath)} (lines {start}-{endIdx})");
                    }
                    else
                    {
                        outputResults.Add($"unchanged: {_workspace.RelativePath(targetFullPath)}");
                    }
                }
            }
            else if (linesPerFile > 0)
            {
                int partNumber = 1;
                string baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
                string filenameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);
                string ext = Path.GetExtension(fullPath);

                for (int i = 0; i < lines.Length; i += linesPerFile)
                {
                    int chunkCount = Math.Min(linesPerFile, lines.Length - i);
                    var chunkLines = new List<string>();
                    for (int j = 0; j < chunkCount; j++)
                    {
                        chunkLines.Add(lines[i + j]);
                    }

                    string partFileName = $"{filenameWithoutExt}_part{partNumber}{ext}";
                    string partRelativePath = string.IsNullOrEmpty(baseDir)
                        ? partFileName
                        : Path.Combine(_workspace.RelativePath(baseDir), partFileName).Replace('\\', '/');

                    string targetFullPath = _workspace.ResolveInsideWorkspace(partRelativePath);
                    string newContent = string.Join("\n", chunkLines);
                    string oldContent = File.Exists(targetFullPath)
                        ? NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath))
                        : string.Empty;

                    bool isNewFile = !File.Exists(targetFullPath);
                    if (isNewFile || !string.Equals(newContent, oldContent, StringComparison.Ordinal))
                    {
                        var preview = new AgentFileEditPreview
                        {
                            ActionName = "split_file",
                            RelativePath = partRelativePath,
                            FullPath = targetFullPath,
                            OldContent = oldContent,
                            NewContent = newContent,
                            IsNewFile = isNewFile
                        };

                        if (!await _confirmEditAsync(preview))
                        {
                            outputResults.Add($"skipped (cancelled by user): {partRelativePath}");
                            partNumber++;
                            continue;
                        }

                        await File.WriteAllTextAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding));
                        await _notifyFileEditCommittedAsync(preview);
                        await _notifyFileModifiedAsync(targetFullPath);
                        outputResults.Add($"created: {partRelativePath} (lines {i + 1}-{i + chunkCount})");
                    }
                    else
                    {
                        outputResults.Add($"unchanged: {partRelativePath}");
                    }

                    partNumber++;
                }
            }
            else
            {
                return "split_file failed: must provide either ranges or linesPerFile argument.";
            }

            return "Split results:\n" + string.Join("\n", outputResults);
        }

        private string BuildUnchangedEditResult(string toolName, string fullPath)
        {
            return $"{toolName} unchanged: {_workspace.RelativePath(fullPath)} requested change is already applied; no additional edit was needed.";
        }

        private static int GetLineStartOffset(string[] lines, int oneBasedLine)
        {
            int offset = 0;
            for (int i = 0; i < oneBasedLine - 1; i++)
            {
                offset += lines[i].Length + 1;
            }

            return offset;
        }

        private static int GetLineEndOffset(string[] lines, int oneBasedLine)
        {
            int offset = GetLineStartOffset(lines, oneBasedLine);
            return offset + lines[oneBasedLine - 1].Length;
        }

        private static bool TryResolveNearbyExpectedSnippetRange(
            string[] lines,
            int startLine,
            int endLine,
            string normalizedExpected,
            int? allowedStartLine,
            int? allowedEndLine,
            out int adjustedStartLine,
            out int adjustedEndLine)
        {
            adjustedStartLine = startLine;
            adjustedEndLine = endLine;

            if (string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return false;
            }

            int requestedLineCount = endLine - startLine + 1;
            int expectedLineCount = CountNormalizedLines(normalizedExpected);
            if (expectedLineCount < Math.Max(2, requestedLineCount - 2))
            {
                return false;
            }

            int margin = Math.Min(10, Math.Max(3, Math.Abs(expectedLineCount - requestedLineCount) + 2));
            int windowStartLine = Math.Max(allowedStartLine ?? 1, startLine - margin);
            int windowEndLine = Math.Min(allowedEndLine ?? lines.Length, endLine + margin);
            if (windowEndLine - windowStartLine + 1 < expectedLineCount)
            {
                return false;
            }

            SnippetRangeResolution resolution = TryResolveUniqueExpectedSnippetLineRange(
                lines,
                windowStartLine,
                windowEndLine,
                normalizedExpected,
                false,
                out adjustedStartLine,
                out adjustedEndLine);
            if (resolution == SnippetRangeResolution.NotFound)
            {
                resolution = TryResolveUniqueExpectedSnippetLineRange(
                    lines,
                    windowStartLine,
                    windowEndLine,
                    normalizedExpected,
                    true,
                    out adjustedStartLine,
                    out adjustedEndLine);
            }

            return resolution == SnippetRangeResolution.Unique;
        }

        private enum SnippetRangeResolution
        {
            NotFound,
            Unique,
            Ambiguous
        }

        private static SnippetRangeResolution TryResolveUniqueExpectedSnippetLineRange(
            string[] lines,
            int windowStartLine,
            int windowEndLine,
            string normalizedExpected,
            bool allowWhitespaceOnlyDifference,
            out int matchStartLine,
            out int matchEndLine)
        {
            matchStartLine = 0;
            matchEndLine = 0;

            int expectedLineCount = CountNormalizedLines(normalizedExpected);
            string expectedComparable = allowWhitespaceOnlyDifference
                ? NormalizeWhitespaceForSnippetComparison(normalizedExpected)
                : normalizedExpected;

            for (int candidateStartLine = windowStartLine; candidateStartLine + expectedLineCount - 1 <= windowEndLine; candidateStartLine++)
            {
                string candidateText = string.Join("\n", lines.Skip(candidateStartLine - 1).Take(expectedLineCount));
                string candidateComparable = allowWhitespaceOnlyDifference
                    ? NormalizeWhitespaceForSnippetComparison(candidateText)
                    : candidateText;
                if (string.Equals(candidateComparable, expectedComparable, StringComparison.Ordinal))
                {
                    if (matchStartLine != 0)
                    {
                        matchStartLine = 0;
                        matchEndLine = 0;
                        return SnippetRangeResolution.Ambiguous;
                    }

                    matchStartLine = candidateStartLine;
                    matchEndLine = candidateStartLine + expectedLineCount - 1;
                }
            }

            return matchStartLine == 0
                ? SnippetRangeResolution.NotFound
                : SnippetRangeResolution.Unique;
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

        private sealed class PatchHunk
        {
            public int OldStart { get; set; }
            public int OldCount { get; set; }
            public int NewStart { get; set; }
            public int NewCount { get; set; }
            public List<string> Lines { get; } = new List<string>();
        }
    }
}
