using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;

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

    public sealed class AgentReadImageResult
    {
        public string TranscriptText { get; init; } = string.Empty;
        public LlmMessageAttachment? Attachment { get; init; }
    }

    public sealed class AgentOpenFileResult
    {
        public string FullPath { get; init; } = string.Empty;
        public bool Success { get; init; }
        public bool ActivatedExistingTab { get; init; }
        public string? ErrorMessage { get; init; }

        public static AgentOpenFileResult Opened(string fullPath)
        {
            return new AgentOpenFileResult
            {
                FullPath = fullPath,
                Success = true
            };
        }

        public static AgentOpenFileResult ActivatedExisting(string fullPath)
        {
            return new AgentOpenFileResult
            {
                FullPath = fullPath,
                Success = true,
                ActivatedExistingTab = true
            };
        }

        public static AgentOpenFileResult Failed(string fullPath, string errorMessage)
        {
            return new AgentOpenFileResult
            {
                FullPath = fullPath,
                ErrorMessage = errorMessage
            };
        }
    }

    public sealed class AgentFileToolService
    {
        private readonly AgentWorkspaceFileResolver _workspace;
        private readonly AgentWorkspaceFileToolService _workspaceFiles;
        private readonly AgentImageToolService _images;
        private readonly AgentDocumentExtractionToolService _documents;
        private readonly AgentProcessToolService _processes;
        private readonly AgentFileEditToolService _edits;
        private readonly Func<string, string, string> _getString;

        public AgentFileToolService(
            Func<string> workspaceRootProvider,
            Func<string, string, string>? getString = null)
        {
            _getString = getString ?? ((_, fallback) => fallback);
            _workspace = new AgentWorkspaceFileResolver(workspaceRootProvider);
            _workspaceFiles = new AgentWorkspaceFileToolService(_workspace);
            _images = new AgentImageToolService(_workspace);
            _documents = new AgentDocumentExtractionToolService(
                _workspace,
                _getString,
                NotifyFileModifiedAsync,
                () => ActivityReporter);
            _processes = new AgentProcessToolService(
                _workspace,
                (query, maxResults) => _workspaceFiles.SearchTextAsync(query, null, maxResults),
                ConfirmPowerShellCommandAsync);
            _edits = new AgentFileEditToolService(
                _workspace,
                ConfirmEditAsync,
                NotifyFileModifiedAsync);
        }

        public Func<AgentFileEditPreview, Task<bool>>? ConfirmFileEditAsync { get; set; }
        public Func<string, Task<bool>>? ConfirmPowerShellAsync { get; set; }
        public Func<string, Task>? FileModifiedAsync { get; set; }
        public Action<string>? ActivityReporter { get; set; }

        public string WorkspaceRoot => _workspace.WorkspaceRoot;

        public Task<string> ListFilesAsync(string? glob, int maxResults)
        {
            return _workspaceFiles.ListFilesAsync(glob, maxResults);
        }

        public Task<string> SearchTextAsync(string query, string? glob, int maxResults)
        {
            return _workspaceFiles.SearchTextAsync(query, glob, maxResults);
        }

        public Task<string> ReadFileAsync(string path, int startLine, int lineCount)
        {
            return _workspaceFiles.ReadFileAsync(path, startLine, lineCount);
        }

        public Task<AgentReadImageResult> ReadImageAsync(string path)
        {
            return _images.ReadImageAsync(path);
        }

        public Task<string> ExtractDocumentAsync(string path, string outputPath, int maxChars)
        {
            return _documents.ExtractDocumentAsync(path, outputPath, maxChars);
        }

        public Task<string> RunRgAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            return _processes.RunRgAsync(arguments, timeoutMs, cancellationToken);
        }

        public Task<string> RunRgaAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            return _processes.RunRgaAsync(arguments, timeoutMs, cancellationToken);
        }

        public Task<string> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken = default)
        {
            return _processes.RunPowerShellAsync(command, timeoutMs, cancellationToken);
        }

        public Task<string> CreateFileAsync(string path, string content)
        {
            return _edits.CreateFileAsync(path, content);
        }

        public Task<string> ReplaceInFileAsync(string path, string oldText, string newText)
        {
            return _edits.ReplaceInFileAsync(path, oldText, newText);
        }

        public Task<string> SearchReplaceAsync(
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
            return _edits.SearchReplaceAsync(
                path,
                searchText,
                replacementText,
                useRegex,
                matchCase,
                wholeWord,
                maxReplacements,
                startLine,
                endLine,
                allowedStartLine,
                allowedEndLine);
        }

        public Task<string> ReplaceRangeAsync(
            string path,
            int startLine,
            int endLine,
            string newText,
            string? expectedSnippet,
            int? allowedStartLine = null,
            int? allowedEndLine = null)
        {
            return _edits.ReplaceRangeAsync(
                path,
                startLine,
                endLine,
                newText,
                expectedSnippet,
                allowedStartLine,
                allowedEndLine);
        }

        public Task<string> ApplyPatchAsync(string path, string patchText)
        {
            return _edits.ApplyPatchAsync(path, patchText);
        }

        public Task<string> OverwriteFileAsync(string path, string content)
        {
            return _edits.OverwriteFileAsync(path, content);
        }

        public Task<string> AppendToFileAsync(string path, string content)
        {
            return _edits.AppendToFileAsync(path, content);
        }

        public Task<string> MergeFilesAsync(string[] paths, string targetPath)
        {
            return _edits.MergeFilesAsync(paths, targetPath);
        }

        public Task<string> SplitFileAsync(string path, List<SplitRange> ranges, int linesPerFile)
        {
            return _edits.SplitFileAsync(path, ranges, linesPerFile);
        }

        private async Task<bool> ConfirmEditAsync(AgentFileEditPreview preview)
        {
            if (ConfirmFileEditAsync == null)
            {
                return true;
            }

            return await ConfirmFileEditAsync(preview);
        }

        private async Task<bool> ConfirmPowerShellCommandAsync(string command)
        {
            return ConfirmPowerShellAsync != null && await ConfirmPowerShellAsync(command);
        }

        private async Task NotifyFileModifiedAsync(string fullPath)
        {
            if (FileModifiedAsync != null)
            {
                await FileModifiedAsync(fullPath);
            }
        }

        public class SplitRange
        {
            public string Path { get; set; } = string.Empty;
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public int LineCount { get; set; }
        }
    }
}
