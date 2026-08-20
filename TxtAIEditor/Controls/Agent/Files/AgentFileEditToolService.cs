using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;
using static TxtAIEditor.Controls.AgentTextContentUtilities;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentFileEditToolService
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private sealed class TextFileSnapshot
        {
            public string Content { get; init; } = string.Empty;
            public Encoding Encoding { get; init; } = Utf8NoBom;
        }

        private sealed class FilePatchSection
        {
            public string Path { get; init; } = string.Empty;
            public string PatchText { get; init; } = string.Empty;
        }

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
            await WriteTextFileAsync(fullPath, newContent, Utf8NoBom);
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

            TextFileSnapshot file = await ReadTextFileAsync(fullPath);
            string rawText = file.Content;
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

            await WriteTextFileAsync(fullPath, RestoreLineEndings(updated, lineEnding), file.Encoding);
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
            string? expectedStartLine,
            string? expectedEndLine,
            int? allowedStartLine = null,
            int? allowedEndLine = null)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            if (!File.Exists(fullPath))
            {
                return $"replace_range failed: file not found: {path}";
            }

            TextFileSnapshot file = await ReadTextFileAsync(fullPath);
            string rawText = file.Content;
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            string[] lines = content.Split('\n');
            List<string> startBoundaryLines = SplitExpectedBoundaryLines(expectedStartLine);
            List<string> endBoundaryLines = SplitExpectedBoundaryLines(expectedEndLine);

            string WithFailureContext(string message)
            {
                return AppendEditFailureContext(
                    message,
                    path,
                    lines,
                    startLine,
                    endLine);
            }

            if (startBoundaryLines.Count == 0)
            {
                return WithFailureContext(
                    "replace_range failed: expectedStartLine is required and must contain at least one line.");
            }

            if (endBoundaryLines.Count == 0)
            {
                return WithFailureContext(
                    "replace_range failed: expectedEndLine is required and must contain at least one line.");
            }

            if (startLine < 1)
            {
                return WithFailureContext(
                    $"replace_range failed: startLine {startLine} must be at least 1.");
            }

            if (endLine < 1)
            {
                return WithFailureContext(
                    $"replace_range failed: endLine {endLine} must be at least 1.");
            }

            if (endLine < startLine)
            {
                return WithFailureContext(
                    $"replace_range failed: endLine {endLine} must be greater than or equal to startLine {startLine}.");
            }

            int requestedStartLine = startLine;
            int requestedEndLine = endLine;
            if (!TryResolveExpectedBoundaryRange(
                    lines,
                    requestedStartLine,
                    requestedEndLine,
                    startBoundaryLines,
                    endBoundaryLines,
                    allowedStartLine,
                    allowedEndLine,
                    out int adjustedStartLine,
                    out int adjustedEndLine))
            {
                return WithFailureContext(
                    $"replace_range failed: expectedStartLine and expectedEndLine did not identify a unique range near {requestedStartLine}-{requestedEndLine} within ±3 lines.");
            }

            startLine = adjustedStartLine;
            endLine = adjustedEndLine;
            string? rangeAdjustmentNote = startLine == requestedStartLine && endLine == requestedEndLine
                ? null
                : $" (range adjusted from {requestedStartLine}-{requestedEndLine} to {startLine}-{endLine})";

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

            await WriteTextFileAsync(fullPath, RestoreLineEndings(updated, lineEnding), file.Encoding);
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"modified: {_workspace.RelativePath(fullPath)}{rangeAdjustmentNote}";
        }

        private static List<string> SplitExpectedBoundaryLines(string? boundaryText)
        {
            string normalizedBoundary = TrimBoundaryNewlines(NormalizeNewlines(boundaryText));
            if (string.IsNullOrEmpty(normalizedBoundary))
            {
                return new List<string>();
            }

            return normalizedBoundary.Split('\n').ToList();
        }

        private static bool TryResolveExpectedBoundaryRange(
            string[] lines,
            int requestedStartLine,
            int requestedEndLine,
            IReadOnlyList<string> startBoundaryLines,
            IReadOnlyList<string> endBoundaryLines,
            int? allowedStartLine,
            int? allowedEndLine,
            out int adjustedStartLine,
            out int adjustedEndLine)
        {
            adjustedStartLine = requestedStartLine;
            adjustedEndLine = requestedEndLine;

            int minimumAllowedLine = Math.Max(1, allowedStartLine ?? 1);
            int maximumAllowedLine = Math.Min(lines.Length, allowedEndLine ?? lines.Length);
            if (minimumAllowedLine > maximumAllowedLine)
            {
                return false;
            }

            const int adjustmentLimit = 3;
            long startWindowMin = Math.Max(
                minimumAllowedLine,
                (long)requestedStartLine - adjustmentLimit);
            long startWindowMax = Math.Min(
                (long)maximumAllowedLine - startBoundaryLines.Count + 1,
                (long)requestedStartLine + adjustmentLimit);
            long endWindowMin = Math.Max(
                (long)minimumAllowedLine + endBoundaryLines.Count - 1,
                (long)requestedEndLine - adjustmentLimit);
            long endWindowMax = Math.Min(
                maximumAllowedLine,
                (long)requestedEndLine + adjustmentLimit);

            var matchingStartLines = new List<int>();
            if (startWindowMin <= startWindowMax)
            {
                for (int candidateStartLine = (int)startWindowMin;
                     candidateStartLine <= startWindowMax;
                     candidateStartLine++)
                {
                    if (BoundaryLinesMatch(lines, candidateStartLine, startBoundaryLines))
                    {
                        matchingStartLines.Add(candidateStartLine);
                    }
                }
            }

            var matchingEndLines = new List<int>();
            if (endWindowMin <= endWindowMax)
            {
                for (int candidateEndLine = (int)endWindowMin;
                     candidateEndLine <= endWindowMax;
                     candidateEndLine++)
                {
                    int boundaryStartLine = candidateEndLine - endBoundaryLines.Count + 1;
                    if (BoundaryLinesMatch(lines, boundaryStartLine, endBoundaryLines))
                    {
                        matchingEndLines.Add(candidateEndLine);
                    }
                }
            }

            var candidates = new List<(int StartLine, int EndLine, long Distance)>();
            foreach (int candidateStartLine in matchingStartLines)
            {
                foreach (int candidateEndLine in matchingEndLines)
                {
                    if (candidateStartLine + startBoundaryLines.Count - 1 > candidateEndLine ||
                        candidateEndLine - endBoundaryLines.Count + 1 < candidateStartLine)
                    {
                        continue;
                    }

                    long distance = Math.Abs((long)candidateStartLine - requestedStartLine) +
                        Math.Abs((long)candidateEndLine - requestedEndLine);
                    candidates.Add((candidateStartLine, candidateEndLine, distance));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            long bestDistance = candidates.Min(candidate => candidate.Distance);
            var bestCandidates = candidates
                .Where(candidate => candidate.Distance == bestDistance)
                .ToList();
            if (bestCandidates.Count != 1)
            {
                return false;
            }

            adjustedStartLine = bestCandidates[0].StartLine;
            adjustedEndLine = bestCandidates[0].EndLine;
            return true;
        }

        private static bool BoundaryLinesMatch(
            string[] lines,
            int oneBasedStartLine,
            IReadOnlyList<string> expectedLines)
        {
            int startIndex = oneBasedStartLine - 1;
            if (startIndex < 0 || startIndex + expectedLines.Count > lines.Length)
            {
                return false;
            }

            for (int i = 0; i < expectedLines.Count; i++)
            {
                if (!LinesMatch(lines[startIndex + i], expectedLines[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool LinesMatch(string actual, string expected)
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

        public async Task<string> ApplyPatchAsync(string? path, string patchText)
        {
            if (string.IsNullOrWhiteSpace(patchText))
            {
                return "apply_patch failed: patch content is empty.";
            }

            patchText = AgentToolHelpers.SanitizeApplyPatchText(patchText);

            if (!TryParseFilePatchSections(path, patchText, out List<FilePatchSection> sections, out string? parseError))
            {
                return parseError ?? "apply_patch failed: patch content could not be parsed.";
            }

            sections = sections
                .GroupBy(section => section.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FilePatchSection
                {
                    Path = group.First().Path,
                    PatchText = string.Join("\n", group.Select(section => section.PatchText))
                })
                .ToList();

            if (sections.Count == 1)
            {
                return await ApplySingleFilePatchAsync(sections[0].Path, sections[0].PatchText);
            }

            var results = new List<string>();
            foreach (FilePatchSection section in sections)
            {
                string result = await ApplySingleFilePatchAsync(section.Path, section.PatchText);
                results.Add(result);

                if (result.StartsWith("apply_patch cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return "apply_patch multi-file results:" + Environment.NewLine + string.Join(
                Environment.NewLine,
                results.Select(result => $"- {result.Replace(Environment.NewLine, Environment.NewLine + "  ")}"));
        }

        private async Task<string> ApplySingleFilePatchAsync(string path, string patchText)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "apply_patch failed: path is empty and no file section was provided.";
            }

            string fullPath;
            try
            {
                fullPath = _workspace.ResolveInsideWorkspace(path);
            }
            catch (InvalidOperationException ex)
            {
                return $"apply_patch failed: invalid path '{path}': {ex.Message}";
            }

            if (!File.Exists(fullPath))
            {
                return $"apply_patch failed: file not found: {path}";
            }

            if (string.IsNullOrWhiteSpace(patchText))
            {
                return "apply_patch failed: patch content is empty.";
            }

            TextFileSnapshot file = await ReadTextFileAsync(fullPath);
            string rawText = file.Content;
            string lineEnding = DetectLineEnding(rawText);
            string content = NormalizeNewlines(rawText);
            List<string> lines = content.Split('\n').ToList();

            string[] patchLines = NormalizeNewlines(patchText).Split('\n');
            var hunkHeaderRegex = new Regex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);
            var unlocatedHunkHeaderRegex = new Regex(@"^\s*@@(?:\s+(?![-+]\d).*)?\s*$", RegexOptions.Compiled);
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
                        NewCount = newCount,
                        HasExplicitLocation = true
                    };
                    hunks.Add(currentHunk);
                }
                else if (unlocatedHunkHeaderRegex.IsMatch(line))
                {
                    currentHunk = new PatchHunk();
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

            var sortedHunks = hunks
                .OrderByDescending(h => h.HasExplicitLocation)
                .ThenByDescending(h => h.OldStart)
                .ToList();
            var skippedHunks = new List<string>();
            int changedHunkCount = 0;

            foreach (var hunk in sortedHunks)
            {
                bool ambiguousUnlocatedMatch = false;
                int matchIndex = hunk.HasExplicitLocation
                    ? FindHunkMatch(lines, hunk)
                    : FindUniqueHunkMatch(lines, hunk, out ambiguousUnlocatedMatch);
                if (matchIndex < 0)
                {
                    if (!hunk.HasExplicitLocation)
                    {
                        if (ambiguousUnlocatedMatch)
                        {
                            skippedHunks.Add(
                                $"hunk without a line range is ambiguous in file {path}; its context block must occur exactly once.");
                            continue;
                        }

                        skippedHunks.Add(
                            $"hunk without a line range requires a unique context or deleted block in file {path}.");
                        continue;
                    }

                    int targetEndLine = hunk.OldStart + Math.Max(hunk.OldCount, 1) - 1;
                    skippedHunks.Add(AppendEditFailureContext(
                        $"could not match hunk starting at line {hunk.OldStart} in file {path}.",
                        path,
                        lines,
                        hunk.OldStart,
                        targetEndLine));
                    continue;
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

                var existingLines = lines
                    .Skip(matchIndex)
                    .Take(fileLinesConsumed)
                    .ToList();
                bool hunkChanged = !existingLines.SequenceEqual(replacementLines);
                lines.RemoveRange(matchIndex, fileLinesConsumed);
                lines.InsertRange(matchIndex, replacementLines);
                if (hunkChanged)
                {
                    changedHunkCount++;
                }
            }

            string updated = string.Join("\n", lines);
            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                if (skippedHunks.Count > 0)
                {
                    return BuildApplyPatchSkippedResult(path, changedHunkCount, skippedHunks);
                }

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

            await WriteTextFileAsync(fullPath, RestoreLineEndings(updated, lineEnding), file.Encoding);
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);

            string result = $"modified: {_workspace.RelativePath(fullPath)}";
            if (skippedHunks.Count > 0)
            {
                result += "\n" + BuildApplyPatchSkippedResult(path, changedHunkCount, skippedHunks);
            }

            return result;
        }

        private static bool TryParseFilePatchSections(
            string? fallbackPath,
            string patchText,
            out List<FilePatchSection> sections,
            out string? error)
        {
            var parsedSections = new List<FilePatchSection>();
            sections = parsedSections;
            error = null;

            string normalizedPatch = NormalizeNewlines(patchText);
            string[] patchLines = normalizedPatch.Split('\n');
            bool containsFileSections = patchLines.Any(line =>
                line.StartsWith("*** Update File:", StringComparison.Ordinal));

            if (!containsFileSections)
            {
                if (string.IsNullOrWhiteSpace(fallbackPath))
                {
                    error = "apply_patch failed: path is empty and the patch contains no '*** Update File:' sections.";
                    return false;
                }

                parsedSections.Add(new FilePatchSection
                {
                    Path = fallbackPath.Trim(),
                    PatchText = patchText
                });
                return true;
            }

            string? currentPath = null;
            var currentLines = new List<string>();

            void FlushCurrentSection()
            {
                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return;
                }

                parsedSections.Add(new FilePatchSection
                {
                    Path = currentPath,
                    PatchText = string.Join("\n", currentLines)
                });
                currentPath = null;
                currentLines = new List<string>();
            }

            foreach (string line in patchLines)
            {
                if (string.Equals(line, "*** Begin Patch", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
                {
                    FlushCurrentSection();
                    continue;
                }

                if (line.StartsWith("*** Update File:", StringComparison.Ordinal))
                {
                    FlushCurrentSection();

                    string sectionPath = line.Substring("*** Update File:".Length).Trim();
                    if (string.IsNullOrWhiteSpace(sectionPath))
                    {
                        error = "apply_patch failed: an '*** Update File:' section is missing its path.";
                        return false;
                    }

                    sectionPath = sectionPath.Replace('\\', '/');
                    currentPath = sectionPath;
                    continue;
                }

                if (line.StartsWith("*** Add File:", StringComparison.Ordinal) ||
                    line.StartsWith("*** Delete File:", StringComparison.Ordinal) ||
                    line.StartsWith("*** Move to:", StringComparison.Ordinal))
                {
                    error = "apply_patch failed: multi-file patches currently support existing files with '*** Update File:' sections only.";
                    return false;
                }

                if (string.Equals(line, "*** End of File", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("*** ", StringComparison.Ordinal))
                {
                    error = $"apply_patch failed: unsupported multi-file patch directive: {line}";
                    return false;
                }

                if (currentPath != null)
                {
                    currentLines.Add(line);
                }
            }

            FlushCurrentSection();
            if (parsedSections.Count == 0)
            {
                error = "apply_patch failed: no '*** Update File:' sections were found.";
                return false;
            }

            return true;
        }

        private static string BuildApplyPatchSkippedResult(
            string path,
            int changedHunkCount,
            IReadOnlyList<string> skippedHunks)
        {
            string header = changedHunkCount > 0
                ? $"apply_patch partial: applied {changedHunkCount} hunk(s); skipped {skippedHunks.Count} hunk(s) in {path}."
                : $"apply_patch failed: no hunk was applied; skipped {skippedHunks.Count} failed hunk(s) in {path}.";

            var builder = new StringBuilder(header);
            foreach (string skippedHunk in skippedHunks)
            {
                builder.AppendLine();
                builder.AppendLine($"[Skipped patch hunk] {skippedHunk}");
            }

            return builder.ToString();
        }

        public async Task<string> OverwriteFileAsync(string path, string content)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            bool isNewFile = !File.Exists(fullPath);
            Encoding encoding = Utf8NoBom;
            string rawText = string.Empty;
            if (!isNewFile)
            {
                TextFileSnapshot file = await ReadTextFileAsync(fullPath);
                rawText = file.Content;
                encoding = file.Encoding;
            }

            string lineEnding = DetectLineEnding(rawText);

            string oldContent = NormalizeNewlines(rawText);
            string newContent = NormalizeNewlines(content);
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

            await WriteTextFileAsync(
                fullPath,
                RestoreLineEndings(newContent, lineEnding),
                encoding);
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"overwritten: {_workspace.RelativePath(fullPath)}";
        }

        public async Task<string> AppendToFileAsync(string path, string content)
        {
            string fullPath = _workspace.ResolveInsideWorkspace(path);
            bool isNewFile = !File.Exists(fullPath);
            TextFileSnapshot file = isNewFile
                ? new TextFileSnapshot()
                : await ReadTextFileAsync(fullPath);
            string rawText = file.Content;
            string lineEnding = DetectLineEnding(rawText);

            string oldContent = NormalizeNewlines(rawText);
            string newContent = oldContent;
            if (!string.IsNullOrEmpty(oldContent) && !oldContent.EndsWith("\n"))
            {
                newContent += "\n";
            }
            newContent += NormalizeNewlines(content);

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

            await WriteTextFileAsync(fullPath, RestoreLineEndings(newContent, lineEnding), file.Encoding);
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

                TextFileSnapshot sourceFile = await ReadTextFileAsync(fullPath);
                string rawText = sourceFile.Content;
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
            bool isNewFile = !File.Exists(targetFullPath);
            TextFileSnapshot targetFile = isNewFile
                ? new TextFileSnapshot()
                : await ReadTextFileAsync(targetFullPath);
            string oldContent = NormalizeNewlines(targetFile.Content);
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

            await WriteTextFileAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding), targetFile.Encoding);
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

            TextFileSnapshot file = await ReadTextFileAsync(fullPath);
            string rawText = file.Content;
            string lineEnding = DetectLineEnding(rawText);
            string fileContent = NormalizeNewlines(rawText);

            string normalizedContent = NormalizeNewlines(content);
            string normalizedBefore = NormalizeNewlines(before ?? string.Empty);
            string normalizedAfter = NormalizeNewlines(after ?? string.Empty);

            if (string.IsNullOrEmpty(normalizedBefore) || string.IsNullOrEmpty(normalizedAfter))
            {
                return "insert_to_file failed: both before and after context must be provided.";
            }

            if (TrimBoundaryBlankLines(normalizedBefore).Length < 2 || TrimBoundaryBlankLines(normalizedAfter).Length < 2)
            {
                return "insert_to_file failed: insert_after and insert_before must each contain at least 2 context lines.";
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

            await WriteTextFileAsync(fullPath, RestoreLineEndings(updated, lineEnding), file.Encoding);
            await _notifyFileEditCommittedAsync(preview);
            await _notifyFileModifiedAsync(fullPath);
            return $"inserted: {_workspace.RelativePath(fullPath)}";
        }

        private static int FindInsertionPoint(string fileContent, string before, string after)
        {
            string[] lines = fileContent.Split('\n');
            string[] beforeLines = TrimBoundaryBlankLines(before);
            string[] afterLines = TrimBoundaryBlankLines(after);

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

                    int insertionLine = i;
                    if (afterLines.Length > 0)
                    {
                        int afterStart = i;
                        while (afterStart < lines.Length && string.IsNullOrWhiteSpace(lines[afterStart]))
                        {
                            afterStart++;
                        }

                        if (afterStart + afterLines.Length > lines.Length) continue;

                        bool afterMatch = true;
                        for (int j = 0; j < afterLines.Length; j++)
                        {
                            string fileLine = lines[afterStart + j];
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

                        if (!afterMatch) continue;

                        insertionLine = afterStart;
                    }

                    if (candidates.Count > 0 && candidates[^1] == insertionLine)
                    {
                        continue;
                    }

                    candidates.Add(insertionLine);
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

        private static string[] TrimBoundaryBlankLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            string[] rawLines = text.Split('\n');
            int start = 0;
            int end = rawLines.Length - 1;
            while (start <= end && string.IsNullOrWhiteSpace(rawLines[start]))
            {
                start++;
            }

            while (end >= start && string.IsNullOrWhiteSpace(rawLines[end]))
            {
                end--;
            }

            if (start > end)
            {
                return Array.Empty<string>();
            }

            string[] trimmed = new string[end - start + 1];
            Array.Copy(rawLines, start, trimmed, 0, trimmed.Length);
            return trimmed;
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

            TextFileSnapshot sourceFile = await ReadTextFileAsync(fullPath);
            string rawText = sourceFile.Content;
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
                    bool isNewFile = !File.Exists(targetFullPath);
                    TextFileSnapshot targetFile = isNewFile
                        ? new TextFileSnapshot { Encoding = sourceFile.Encoding }
                        : await ReadTextFileAsync(targetFullPath);
                    string oldContent = NormalizeNewlines(targetFile.Content);
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

                        await WriteTextFileAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding), targetFile.Encoding);
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
                    bool isNewFile = !File.Exists(targetFullPath);
                    TextFileSnapshot targetFile = isNewFile
                        ? new TextFileSnapshot { Encoding = sourceFile.Encoding }
                        : await ReadTextFileAsync(targetFullPath);
                    string oldContent = NormalizeNewlines(targetFile.Content);
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

                        await WriteTextFileAsync(targetFullPath, RestoreLineEndings(newContent, lineEnding), targetFile.Encoding);
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

        private static string AppendEditFailureContext(
            string failureMessage,
            string path,
            IReadOnlyList<string> lines,
            int targetStartLine,
            int targetEndLine)
        {
            if (lines.Count == 0)
            {
                return failureMessage;
            }

            int requestedStartLine = Math.Max(1, targetStartLine);
            int requestedEndLine = Math.Max(requestedStartLine, targetEndLine);
            int targetStartInFile = Math.Min(requestedStartLine, lines.Count);
            int targetEndInFile = Math.Min(Math.Max(targetStartInFile, requestedEndLine), lines.Count);
            int shownStartLine = Math.Max(1, targetStartInFile - 5);
            int shownEndLine = Math.Min(lines.Count, targetEndInFile + 5);
            int lineNumberWidth = shownEndLine.ToString().Length;

            var builder = new StringBuilder();
            builder.AppendLine(failureMessage);
            builder.AppendLine();
            builder.AppendLine(AgentToolHelpers.EditFailureContextStartMarker);
            builder.AppendLine($"File: {path}");
            builder.AppendLine($"Requested edit lines: {targetStartLine}-{targetEndLine}");
            builder.AppendLine($"Shown lines: {shownStartLine}-{shownEndLine}");
            for (int lineNumber = shownStartLine; lineNumber <= shownEndLine; lineNumber++)
            {
                bool isTargetLine =
                    lineNumber >= requestedStartLine &&
                    lineNumber <= requestedEndLine;
                builder.Append(isTargetLine ? ">> " : "   ");
                builder.Append(lineNumber.ToString().PadLeft(lineNumberWidth));
                builder.Append(" | ");
                builder.AppendLine(lines[lineNumber - 1]);
            }
            builder.Append(AgentToolHelpers.EditFailureContextEndMarker);
            return builder.ToString();
        }

        private static async Task<TextFileSnapshot> ReadTextFileAsync(string fullPath)
        {
            byte[] bytes = await File.ReadAllBytesAsync(fullPath);
            Encoding encoding = TextEncodingService.GetTextEncoding(bytes, "Auto");
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(
                stream,
                encoding,
                detectEncodingFromByteOrderMarks: true);

            return new TextFileSnapshot
            {
                Content = await reader.ReadToEndAsync(),
                Encoding = encoding
            };
        }

        private static async Task WriteTextFileAsync(string fullPath, string content, Encoding encoding)
        {
            Encoding strictEncoding = (Encoding)encoding.Clone();
            strictEncoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            strictEncoding.DecoderFallback = DecoderFallback.ExceptionFallback;

            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException($"Cannot determine the target directory for '{fullPath}'.");
            }

            string tempPath = Path.Combine(
                directory,
                $"._{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllTextAsync(tempPath, content, strictEncoding);
                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }

                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(
                    stream,
                    strictEncoding,
                    detectEncodingFromByteOrderMarks: true);
                string savedContent = await reader.ReadToEndAsync();
                if (!string.Equals(savedContent, content, StringComparison.Ordinal))
                {
                    throw new IOException($"Text verification failed after writing '{fullPath}'.");
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
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

        private int FindHunkMatch(List<string> lines, PatchHunk hunk)
        {
            int expectedIdx = hunk.OldStart - 1;
            if (expectedIdx >= 0 && expectedIdx < lines.Count && IsHunkMatch(lines, expectedIdx, hunk))
            {
                return expectedIdx;
            }

            int positionCorrectionLimit = 5;
            for (int offset = 1; offset <= positionCorrectionLimit; offset++)
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

            return -1;
        }

        private static int FindUniqueHunkMatch(
            List<string> lines,
            PatchHunk hunk,
            out bool ambiguous)
        {
            ambiguous = false;
            List<string> oldLines = hunk.Lines
                .Where(line => line.StartsWith(' ') || line.StartsWith('-'))
                .Select(line => line.Substring(1))
                .ToList();
            if (oldLines.Count == 0)
            {
                return -1;
            }

            int matchIndex = -1;
            for (int candidateIndex = 0; candidateIndex <= lines.Count - oldLines.Count; candidateIndex++)
            {
                bool matches = true;
                for (int offset = 0; offset < oldLines.Count; offset++)
                {
                    if (!string.Equals(
                            lines[candidateIndex + offset],
                            oldLines[offset],
                            StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                if (matchIndex >= 0)
                {
                    ambiguous = true;
                    return -1;
                }

                matchIndex = candidateIndex;
            }

            return matchIndex;
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
            public bool HasExplicitLocation { get; set; }
            public List<string> Lines { get; } = new List<string>();
        }
    }
}
