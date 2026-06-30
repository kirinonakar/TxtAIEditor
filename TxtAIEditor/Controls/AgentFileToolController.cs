using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentFileToolController
    {
        private readonly AgentFileToolService _fileTools;
        private readonly AgentSelectionContextController _selectionContextController;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<bool> _isRunningProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, Task<AgentOpenFileResult>>? _openFileInEditorAsync;

        private readonly AsyncLocal<RunFileToolState?> _currentRunState = new();

        private sealed class RunFileToolState
        {
            public string? LastFilePath { get; set; }
            public AgentSelectionSnapshot? SelectionSnapshot { get; set; }
            public string? ActiveTabPath { get; set; }
            public HashSet<string> WrittenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public AgentFileToolController(
            AgentFileToolService fileTools,
            AgentSelectionContextController selectionContextController,
            Func<OpenedTab?> activeTabProvider,
            Func<bool> isRunningProvider,
            Func<string, string, string> getString,
            Func<string, Task<AgentOpenFileResult>>? openFileInEditorAsync)
        {
            _fileTools = fileTools;
            _selectionContextController = selectionContextController;
            _activeTabProvider = activeTabProvider;
            _isRunningProvider = isRunningProvider;
            _getString = getString;
            _openFileInEditorAsync = openFileInEditorAsync;
        }

        public void StartRun()
        {
            _currentRunState.Value = new RunFileToolState();
        }

        public void SetRunContext(AgentSelectionSnapshot selectionSnapshot, OpenedTab? activeTab)
        {
            RunFileToolState state = _currentRunState.Value ??= new RunFileToolState();
            state.SelectionSnapshot = selectionSnapshot;
            state.ActiveTabPath = activeTab?.FilePath;
        }

        public void FinishRun()
        {
            _currentRunState.Value = null;
        }

        public void TrackSuccessfulFileToolPath(string normalizedToolName, JsonElement arguments, string result)
        {
            if (result.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (normalizedToolName is not ("read_file" or "read_image" or "extract_document" or "replace_in_file" or "search_replace" or "replace_range" or "apply_patch" or "overwrite_file" or "append_to_file" or "merge_files" or "split_file" or "insert_to_file"))
            {
                return;
            }

            string path;
            if (normalizedToolName == "merge_files")
            {
                path = GetFirstStringArgument(arguments, "targetPath", "target_path", "path", "target");
            }
            else if (normalizedToolName == "extract_document")
            {
                path = GetExtractDocumentOutputPath(arguments, result);
            }
            else
            {
                path = normalizedToolName is "read_file" or "read_image"
                    ? GetPathArgument(arguments)
                    : GetEditPathArgument(arguments);
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                RunFileToolState state = _currentRunState.Value ??= new RunFileToolState();
                state.LastFilePath = path;

                string canonicalPath = GetCanonicalPath(path);
                if (normalizedToolName == "read_file")
                {
                    state.WrittenFiles.Remove(canonicalPath);
                }
                else if (AgentToolHelpers.IsMutatingTool(normalizedToolName))
                {
                    state.WrittenFiles.Add(canonicalPath);
                }
            }
        }

        private string GetExtractDocumentOutputPath(JsonElement arguments, string result)
        {
            string explicitOutput = GetFirstStringArgument(arguments, "outputPath", "output_path", "targetPath", "target_path", "target", "output");
            if (!string.IsNullOrWhiteSpace(explicitOutput))
            {
                return explicitOutput.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? explicitOutput
                    : explicitOutput + ".txt";
            }

            string savedPrefix = _getString("AgentExtractDocumentSavedPrefix", "extract_document saved:");
            string unchangedPrefix = _getString("AgentExtractDocumentUnchangedPrefix", "extract_document unchanged:");
            using var reader = new StringReader(result ?? string.Empty);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith(savedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(savedPrefix.Length).Trim();
                }

                if (line.StartsWith(unchangedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = line.Substring(unchangedPrefix.Length).Trim();
                    int spaceIndex = remainder.IndexOf(' ');
                    return spaceIndex > 0 ? remainder.Substring(0, spaceIndex) : remainder;
                }
            }

            return GetPathArgument(arguments);
        }

        public string GetPathArgument(JsonElement arguments)
        {
            string path = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path");
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        }

        public string GetEditPathArgument(JsonElement arguments)
        {
            string path = GetPathArgument(arguments);
            return string.IsNullOrWhiteSpace(path) ? InferEditPathFromContext() : path;
        }

        public int GetReplaceRangeStartLineArgument(JsonElement arguments, string path)
        {
            if (TryGetIntArgument(arguments, "startLine", out int explicitStartLine) && explicitStartLine > 0)
            {
                return explicitStartLine;
            }

            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot();
            return ShouldUseActiveSelectionRangeForPath(path)
                ? selection.StartLine
                : 1;
        }

        public int GetReplaceRangeEndLineArgument(JsonElement arguments, string path)
        {
            if (TryGetIntArgument(arguments, "endLine", out int explicitEndLine) && explicitEndLine > 0)
            {
                return explicitEndLine;
            }

            if (TryGetIntArgument(arguments, "startLine", out int explicitStartLine) && explicitStartLine > 0)
            {
                return explicitStartLine;
            }

            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot();
            return ShouldUseActiveSelectionRangeForPath(path)
                ? selection.EndLine
                : 1;
        }

        public int GetSearchReplaceStartLineArgument(JsonElement arguments)
        {
            if (TryGetIntArgument(arguments, "startLine", out int startLine) && startLine > 0)
            {
                return startLine;
            }

            if (TryGetIntArgument(arguments, "start_line", out startLine) && startLine > 0)
            {
                return startLine;
            }

            return 0;
        }

        public int GetSearchReplaceEndLineArgument(JsonElement arguments)
        {
            if (TryGetIntArgument(arguments, "endLine", out int endLine) && endLine > 0)
            {
                return endLine;
            }

            if (TryGetIntArgument(arguments, "end_line", out endLine) && endLine > 0)
            {
                return endLine;
            }

            return 0;
        }

        public async Task<string> ReplaceInFileAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "replace_in_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            string oldText = GetFirstStringArgument(arguments, "oldText", "old_text", "find", "search", "target", "before");
            string newText = GetFirstStringArgument(arguments, "newText", "new_text", "replace", "replacement", "after");
            string content = GetFirstStringArgument(arguments, "content", "text");

            return string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(content)
                ? await _fileTools.OverwriteFileAsync(path, content)
                : await _fileTools.ReplaceInFileAsync(path, oldText, newText);
        }

        public async Task<string> SearchReplaceAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "search_replace failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            string searchText = GetFirstStringArgument(arguments, "search", "find", "query", "pattern", "oldText", "old_text", "target", "before");
            string replacementText = GetFirstStringArgument(arguments, "replacement", "replaceWith", "replace_with", "replace", "newText", "new_text", "after");
            bool useRegex = GetBoolArgument(arguments, "useRegex",
                GetBoolArgument(arguments, "isRegex",
                    GetBoolArgument(arguments, "regex", false)));
            bool matchCase = GetBoolArgument(arguments, "matchCase",
                GetBoolArgument(arguments, "caseSensitive", true));
            bool wholeWord = GetBoolArgument(arguments, "wholeWord",
                GetBoolArgument(arguments, "whole_word", false));

            int maxReplacements = GetIntArgument(arguments, "maxReplacements", 0);
            if (maxReplacements == 0)
            {
                maxReplacements = GetIntArgument(arguments, "max_replacements", 0);
            }
            if (maxReplacements == 0)
            {
                maxReplacements = GetIntArgument(arguments, "maxCount", 0);
            }
            if (maxReplacements == 0)
            {
                maxReplacements = GetIntArgument(arguments, "count", 0);
            }

            int startLine = GetSearchReplaceStartLineArgument(arguments);
            int endLine = GetSearchReplaceEndLineArgument(arguments);

            return await _fileTools.SearchReplaceAsync(
                path,
                searchText,
                replacementText,
                useRegex,
                matchCase,
                wholeWord,
                maxReplacements,
                startLine,
                endLine,
                null,
                null);
        }

        public async Task<string> CreateFileAsync(JsonElement arguments)
        {
            string path = GetPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "create_file failed: path is empty. Provide the exact output file path requested by the user.";
            }

            string result = await _fileTools.CreateFileAsync(path, GetStringArgument(arguments, "content"));
            if (!GetBoolArgument(arguments, "openAfterCreate",
                    GetBoolArgument(arguments, "open_after_create", false)) ||
                !IsSuccessfulToolResult(result))
            {
                return result;
            }

            string createdPath = ExtractCreatedPath(result);
            if (string.IsNullOrWhiteSpace(createdPath))
            {
                return AppendToolStatusMessage(
                    result,
                    "open_file skipped: created file path could not be determined from create_file result.");
            }

            string openResult = await OpenFileByPathAsync(createdPath);
            return AppendToolStatusMessage(result, openResult);
        }

        public async Task<string> OpenFileAsync(JsonElement arguments)
        {
            string path = GetPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "open_file failed: path is empty. Provide the file path you want to open.";
            }

            return await OpenFileByPathAsync(path);
        }

        private async Task<string> OpenFileByPathAsync(string path)
        {
            string fullPath = path;
            if (!Path.IsPathRooted(path))
            {
                string root = _fileTools.WorkspaceRoot;
                if (!string.IsNullOrEmpty(root))
                {
                    fullPath = Path.GetFullPath(Path.Combine(root, path));
                }
            }

            if (!File.Exists(fullPath))
            {
                return $"open_file failed: file not found: {path}";
            }

            if (_openFileInEditorAsync != null)
            {
                AgentOpenFileResult openResult = await _openFileInEditorAsync(fullPath);
                if (!openResult.Success)
                {
                    string message = string.IsNullOrWhiteSpace(openResult.ErrorMessage)
                        ? "unknown error"
                        : openResult.ErrorMessage;
                    return $"open_file failed: {path}: {message}";
                }

                return openResult.ActivatedExistingTab
                    ? $"open_file activated_existing: {path}"
                    : $"open_file opened: {path}";
            }

            return "open_file failed: opening files in editor is not available.";
        }

        private static string ExtractCreatedPath(string result)
        {
            const string createdPrefix = "created:";
            using var reader = new StringReader(result ?? string.Empty);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith(createdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = line.Substring(createdPrefix.Length).Trim();
                int noteIndex = path.IndexOf(" (Note:", StringComparison.OrdinalIgnoreCase);
                if (noteIndex >= 0)
                {
                    path = path.Substring(0, noteIndex).Trim();
                }

                return path;
            }

            return string.Empty;
        }

        public async Task<string> OverwriteFileAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "overwrite_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            return await _fileTools.OverwriteFileAsync(
                path,
                GetFirstStringArgument(arguments, "content", "newText", "new_text", "text"));
        }

        public async Task<string> AppendToFileAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "append_to_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            return await _fileTools.AppendToFileAsync(
                path,
                GetFirstStringArgument(arguments, "content", "newText", "new_text", "text"));
        }

        public async Task<string> MergeFilesAsync(JsonElement arguments)
        {
            string targetPath = GetFirstStringArgument(arguments, "targetPath", "target_path", "path", "target");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return "merge_files failed: targetPath is empty.";
            }

            var pathsList = new List<string>();
            if (arguments.TryGetProperty("paths", out var pathsProp))
            {
                if (pathsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pathsProp.EnumerateArray())
                    {
                        string p = item.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(p))
                        {
                            pathsList.Add(p);
                        }
                    }
                }
                else if (pathsProp.ValueKind == JsonValueKind.String)
                {
                    string singlePath = pathsProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(singlePath))
                    {
                        pathsList.Add(singlePath);
                    }
                }
            }

            if (pathsList.Count == 0 &&
                arguments.TryGetProperty("sources", out var sourcesProp) &&
                sourcesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sourcesProp.EnumerateArray())
                {
                    string p = item.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        pathsList.Add(p);
                    }
                }
            }

            if (pathsList.Count == 0 &&
                arguments.TryGetProperty("files", out var filesProp) &&
                filesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in filesProp.EnumerateArray())
                {
                    string p = item.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        pathsList.Add(p);
                    }
                }
            }

            return await _fileTools.MergeFilesAsync(pathsList.ToArray(), targetPath);
        }

        public async Task<string> SplitFileAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "split_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            int linesPerFile = GetIntArgument(arguments, "linesPerFile", 0);
            if (linesPerFile == 0)
            {
                linesPerFile = GetIntArgument(arguments, "lines_per_file", 0);
            }
            if (linesPerFile == 0)
            {
                linesPerFile = GetIntArgument(arguments, "lines", 0);
            }

            var rangesList = new List<AgentFileToolService.SplitRange>();
            if (arguments.TryGetProperty("ranges", out var rangesProp) && rangesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rangesProp.EnumerateArray())
                {
                    string targetPath = GetFirstStringArgument(item, "path", "targetPath", "target_path", "file");
                    int startLine = GetIntArgument(item, "startLine", 0);
                    if (startLine == 0) startLine = GetIntArgument(item, "start_line", 0);

                    int endLine = GetIntArgument(item, "endLine", 0);
                    if (endLine == 0) endLine = GetIntArgument(item, "end_line", 0);

                    int lineCount = GetIntArgument(item, "lineCount", 0);
                    if (lineCount == 0) lineCount = GetIntArgument(item, "line_count", 0);
                    if (lineCount == 0) lineCount = GetIntArgument(item, "count", 0);

                    rangesList.Add(new AgentFileToolService.SplitRange
                    {
                        Path = targetPath,
                        StartLine = startLine,
                        EndLine = endLine,
                        LineCount = lineCount
                    });
                }
            }

            return await _fileTools.SplitFileAsync(path, rangesList, linesPerFile);
        }

        public async Task<string> ReplaceRangeAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "replace_range failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            int startLine = GetReplaceRangeStartLineArgument(arguments, path);
            int endLine = GetReplaceRangeEndLineArgument(arguments, path);
            int lineCount = endLine - startLine + 1;

            string? expectedSnippet = null;
            List<string>? expectedStartLines = null;
            List<string>? expectedEndLines = null;

            if (lineCount >= 5)
            {
                expectedStartLines = GetStringListArgument(arguments, "expectedStartLines", "expected_start_lines");
                expectedEndLines = GetStringListArgument(arguments, "expectedEndLines", "expected_end_lines");
            }
            else
            {
                expectedSnippet = GetReplaceRangeExpectedSnippetArgument(arguments, path);
                if (string.IsNullOrWhiteSpace(expectedSnippet))
                {
                    return "replace_range failed: expectedSnippet is required for replace_range edits to ensure edit safety.";
                }
            }

            return await _fileTools.ReplaceRangeAsync(
                path,
                startLine,
                endLine,
                GetFirstStringArgument(arguments, "newText", "new_text", "content", "text"),
                expectedSnippet,
                null,
                null,
                expectedStartLines,
                expectedEndLines);
        }

        public async Task<string> InsertIntoFileAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "insert_to_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            string content = GetFirstStringArgument(arguments, "content", "text", "newText", "new_text");

            string insertAfter = GetFirstStringArgument(arguments, "insert_after", "insertAfter");
            string insertBefore = GetFirstStringArgument(arguments, "insert_before", "insertBefore");

            string before = string.Empty;
            string after = string.Empty;

            if (!string.IsNullOrEmpty(insertAfter) || !string.IsNullOrEmpty(insertBefore))
            {
                before = insertAfter;
                after = insertBefore;
            }
            else
            {
                string rawBefore = GetFirstStringArgument(arguments, "before", "beforeLines", "before_lines", "previous");
                string rawAfter = GetFirstStringArgument(arguments, "after", "afterLines", "after_lines", "next");

                if (!string.IsNullOrEmpty(rawBefore) && !string.IsNullOrEmpty(rawAfter))
                {
                    before = rawBefore;
                    after = rawAfter;
                }
                else if (!string.IsNullOrEmpty(rawAfter))
                {
                    before = rawAfter;
                }
                else if (!string.IsNullOrEmpty(rawBefore))
                {
                    after = rawBefore;
                }
            }

            return await _fileTools.InsertIntoFileAsync(path, content, before, after);
        }

        public async Task<string> ApplyPatchAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "apply_patch failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            return await _fileTools.ApplyPatchAsync(
                path,
                GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content"));
        }

        private AgentSelectionSnapshot CaptureActiveSelectionSnapshot()
        {
            AgentSelectionSnapshot? runSelection = _currentRunState.Value?.SelectionSnapshot;
            if (runSelection != null && !string.IsNullOrEmpty(runSelection.Text))
            {
                return runSelection;
            }

            return _selectionContextController.CaptureActiveSelectionSnapshot(_isRunningProvider());
        }

        private string InferEditPathFromContext()
        {
            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot();
            if (!string.IsNullOrWhiteSpace(selection.SourcePath) &&
                !string.IsNullOrEmpty(selection.Text))
            {
                return selection.SourcePath;
            }

            string? currentRunLastFilePath = _currentRunState.Value?.LastFilePath;
            if (!string.IsNullOrWhiteSpace(currentRunLastFilePath))
            {
                return currentRunLastFilePath;
            }

            string activePath = _currentRunState.Value?.ActiveTabPath ?? _activeTabProvider()?.FilePath ?? string.Empty;
            return string.IsNullOrWhiteSpace(activePath) ? string.Empty : activePath;
        }

        private bool ShouldUseActiveSelectionRangeForPath(string path)
        {
            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot();
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(selection.SourcePath) ||
                string.IsNullOrEmpty(selection.Text) ||
                selection.StartLine <= 0 ||
                selection.EndLine <= 0)
            {
                return false;
            }

            return PathsReferToSameFile(path, selection.SourcePath);
        }

        private string GetReplaceRangeExpectedSnippetArgument(JsonElement arguments, string path)
        {
            string explicitExpected = GetFirstStringArgument(arguments, "expectedSnippet", "expected_snippet", "guard", "expected");
            if (!string.IsNullOrEmpty(explicitExpected))
            {
                return explicitExpected;
            }

            if (TryGetIntArgument(arguments, "startLine", out _) ||
                TryGetIntArgument(arguments, "endLine", out _))
            {
                return string.Empty;
            }

            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot();
            return ShouldUseActiveSelectionRangeForPath(path)
                ? selection.Text
                : string.Empty;
        }

        private List<string>? GetStringListArgument(JsonElement arguments, params string[] names)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (string name in names)
            {
                if (arguments.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in prop.EnumerateArray())
                    {
                        list.Add(item.GetString() ?? string.Empty);
                    }
                    return list;
                }

                if (arguments.TryGetProperty(name, out prop) && prop.ValueKind == JsonValueKind.String)
                {
                    return SplitStringArgumentIntoLines(prop.GetString() ?? string.Empty);
                }
            }

            return null;
        }

        private static List<string> SplitStringArgumentIntoLines(string value)
        {
            string normalizedValue = value.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = new List<string>(normalizedValue.Split('\n'));

            if (lines.Count > 1 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        private bool PathsReferToSameFile(string path, string selectionPath)
        {
            try
            {
                string resolvedPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(_fileTools.WorkspaceRoot, path);
                string resolvedSelectionPath = Path.IsPathRooted(selectionPath)
                    ? selectionPath
                    : Path.Combine(_fileTools.WorkspaceRoot, selectionPath);

                return string.Equals(
                    Path.GetFullPath(resolvedPath),
                    Path.GetFullPath(resolvedSelectionPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(path, selectionPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private string GetCanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                string resolved = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(_fileTools.WorkspaceRoot, path);
                return Path.GetFullPath(resolved);
            }
            catch
            {
                return path;
            }
        }
    }
}
