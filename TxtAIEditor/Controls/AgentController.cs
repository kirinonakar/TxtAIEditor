using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int MaxActiveFileContextChars = 120_000;

        private readonly ILLMService _llmService;
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Func<OpenedTab, int, string> _getTabText;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Func<string?, string, OpenedTab> _openNewTabWithContent;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Func<AgentFileEditPreview, Task> _openDiffViewAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentAttachmentController _attachmentController;
        private readonly List<AgentFileEditPreview> _sessionEdits = new();
        private readonly string _agentPresetsFilePath;
        private readonly List<AgentPresetItem> _agentPresets = new();
        private readonly HashSet<string> _selectedAgentPresetNames = new(StringComparer.OrdinalIgnoreCase);

        private string _lastSelectionText = string.Empty;
        private string? _lastSelectionTabId;
        private string? _lastSelectionSourceTitle;
        private string? _lastSelectionSourcePath;
        private int _lastSelectionStartLine;
        private int _lastSelectionEndLine;
        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;
        private readonly StringBuilder _sessionHistory = new();
        private TaskCompletionSource<bool>? _diffApprovalTcs;
        private string? _currentRunLastFilePath;

        private sealed class SelectionSnapshot
        {
            public string Text { get; init; } = string.Empty;
            public string? SourcePath { get; init; }
            public int StartLine { get; init; }
            public int EndLine { get; init; }

            public bool HasLineRange =>
                !string.IsNullOrEmpty(Text) &&
                !string.IsNullOrEmpty(SourcePath) &&
                StartLine > 0 &&
                EndLine > 0;
        }

        private sealed class AgentPresetItem
        {
            public string Name { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }

        public AgentController(
            ILLMService llmService,
            ISettingsService settingsService,
            AgentPane agentPane,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Func<string?, string, OpenedTab> openNewTabWithContent,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools,
            PdfTextExtractionService pdfTextExtractionService,
            Action<object> initializePickerWindow,
            Func<string, bool> isGitRepoProvider,
            Func<AgentFileEditPreview, Task> openDiffViewAsync,
            Func<string, Task>? fileModifiedAsync = null,
            Action? beforeDialog = null,
            Action? afterDialog = null)
        {
            _llmService = llmService;
            _settingsService = settingsService;
            _agentPane = agentPane;
            _activeTabProvider = activeTabProvider;
            _openTabsProvider = openTabsProvider;
            _getTabText = getTabText;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _openNewTabWithContent = openNewTabWithContent;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _initializePickerWindow = initializePickerWindow;
            _isGitRepoProvider = isGitRepoProvider;
            _openDiffViewAsync = openDiffViewAsync;
            _fileModifiedAsync = fileModifiedAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _displayText = new AgentDisplayLocalizer(_getString);
            _attachmentController = new AgentAttachmentController(
                _agentPane,
                _initializePickerWindow,
                _showError,
                _getString,
                _displayText,
                () => _isRunning,
                UpdateContextStats,
                EstimateTokenCount,
                pdfTextExtractionService,
                _beforeDialog,
                _afterDialog);
            _fileTools.ConfirmFileEditAsync = ConfirmFileEditAsync;
            _fileTools.ConfirmPowerShellAsync = ConfirmPowerShellAsync;
            if (_fileModifiedAsync != null)
            {
                _fileTools.FileModifiedAsync = _fileModifiedAsync;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _agentPresetsFilePath = Path.Combine(settingsDir, "agent-presets.json");

            WireEvents();
            UpdateContextStats();
            _ = LoadAgentPresetsAsync();
        }

        public IReadOnlyList<AgentFileEditPreview> SessionEdits => _sessionEdits;

        public void SetSelectionText(string selectedText, OpenedTab? sourceTab = null, int startLine = 0, int endLine = 0)
        {
            _lastSelectionText = selectedText ?? string.Empty;
            if (string.IsNullOrEmpty(_lastSelectionText))
            {
                _lastSelectionTabId = null;
                _lastSelectionSourceTitle = null;
                _lastSelectionSourcePath = null;
                _lastSelectionStartLine = 0;
                _lastSelectionEndLine = 0;
            }
            else
            {
                _lastSelectionTabId = sourceTab?.Id;
                _lastSelectionSourceTitle = sourceTab?.Title;
                _lastSelectionSourcePath = sourceTab?.FilePath;
                _lastSelectionStartLine = startLine;
                _lastSelectionEndLine = endLine;
            }
            UpdateContextStats();
        }

        public void ClearSelection()
        {
            _lastSelectionText = string.Empty;
            _lastSelectionTabId = null;
            _lastSelectionSourceTitle = null;
            _lastSelectionSourcePath = null;
            _lastSelectionStartLine = 0;
            _lastSelectionEndLine = 0;
            UpdateContextStats();
        }

        private string GetActiveSelectionText()
        {
            var activeTab = _activeTabProvider();
            if (string.IsNullOrEmpty(_lastSelectionText))
            {
                return string.Empty;
            }

            if (activeTab == null)
            {
                return string.Empty;
            }

            if (_lastSelectionTabId == null)
            {
                return _lastSelectionText;
            }

            return string.Equals(_lastSelectionTabId, activeTab.Id, StringComparison.Ordinal)
                ? _lastSelectionText
                : string.Empty;
        }

        private string BuildActiveSelectionContext()
        {
            string selectedText = GetActiveSelectionText();
            if (string.IsNullOrEmpty(selectedText))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(_lastSelectionSourceTitle) &&
                string.IsNullOrWhiteSpace(_lastSelectionSourcePath))
            {
                return selectedText;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[Selection source]");
            if (!string.IsNullOrWhiteSpace(_lastSelectionSourceTitle))
            {
                builder.AppendLine($"Title: {_lastSelectionSourceTitle}");
            }
            if (!string.IsNullOrWhiteSpace(_lastSelectionSourcePath))
            {
                builder.AppendLine($"Path: {_lastSelectionSourcePath}");
            }
            if (_lastSelectionStartLine > 0 && _lastSelectionEndLine > 0)
            {
                builder.AppendLine($"Lines: {_lastSelectionStartLine}-{_lastSelectionEndLine}");
            }
            builder.AppendLine();
            builder.AppendLine("[Selection text]");
            builder.Append(selectedText);
            return builder.ToString();
        }

        private SelectionSnapshot CaptureActiveSelectionSnapshot()
        {
            string selectedText = GetActiveSelectionText();
            if (string.IsNullOrEmpty(selectedText))
            {
                return new SelectionSnapshot();
            }

            return new SelectionSnapshot
            {
                Text = selectedText,
                SourcePath = _lastSelectionSourcePath,
                StartLine = _lastSelectionStartLine,
                EndLine = _lastSelectionEndLine
            };
        }

        private void WireEvents()
        {
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync();
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.NewSessionRequested += (_, _) => ClearSession();
            _agentPane.InsertOutputRequested += async (_, _) => await InsertOutputAsync();
            _agentPane.AddAttachmentRequested += async (_, _) => await _attachmentController.AddAttachmentsAsync();
            _agentPane.RemoveAttachmentRequested += (_, attachment) => _attachmentController.RemoveAttachment(attachment.Id);
            _agentPane.AgentPresetAddRequested += (_, _) => OnAddAgentPresetClick();
            _agentPane.AgentPresetToggled += (_, presetName) => ToggleAgentPreset(presetName);
            _agentPane.AgentPresetEdited += (_, presetName) => OnAgentPresetEdited(presetName);
            _agentPane.AgentPresetDeleted += (_, presetName) => OnAgentPresetDeleted(presetName);
            _agentPane.AgentPresetRemoved += (_, presetName) => RemoveSelectedAgentPreset(presetName);
            _agentPane.AgentPresetExportRequested += (_, _) => OnExportAgentPresetsClick();
            _agentPane.AgentPresetImportRequested += (_, _) => OnImportAgentPresetsClick();
            
            _agentPane.Prompt.TextChanged += (_, _) => UpdateContextStats();
            _agentPane.IncludeActiveFileCheckBox.Checked += (_, _) => UpdateContextStats();
            _agentPane.IncludeActiveFileCheckBox.Unchecked += (_, _) => UpdateContextStats();

            _agentPane.DiffApproved += (_, _) => _diffApprovalTcs?.TrySetResult(true);
            _agentPane.DiffCancelled += (_, _) => _diffApprovalTcs?.TrySetResult(false);
            _agentPane.FileRevertRequested += async (_, preview) => await RevertFileChangeAsync(preview);
            _agentPane.FileDiffRequested += async (_, preview) => await _openDiffViewAsync(preview);
        }
 
        private async Task RunAgentAsync()
        {
            if (_isRunning)
            {
                return;
            }
 
            string root = _fileTools.WorkspaceRoot;
            var settings = _settingsService.CurrentSettings;
            if (!_isGitRepoProvider(root) && !settings.LlmAgentAllowNonGitFolders)
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentGitRequired", "Agent는 Git 저장소로 지정된 폴더 내에서만 실행할 수 있습니다."));
                return;
            }
 
            string userInstruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            string instruction = BuildAgentInstruction(userInstruction);
            if (string.IsNullOrWhiteSpace(instruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }
 
            _isRunning = true;
            _currentRunLastFilePath = null;
            var cancellationSource = new CancellationTokenSource();
            _runCancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            _agentPane.SetBusy(true);
            _agentPane.ClearActivity(_getString("AgentActivityStarting", "시작 중"));
            _agentPane.BeginOutputBlock(BuildRunHeader(BuildInstructionDisplay(userInstruction)));
            AppendActivity(_getString("AgentActivityCollectingContext", "맥락 수집 중"));

            try
            {
                string workspaceContext = BuildWorkspaceContext(instruction);
                var initialTranscriptBuilder = new StringBuilder();
                if (_sessionHistory.Length > 0)
                {
                    initialTranscriptBuilder.AppendLine("[Session History]");
                    initialTranscriptBuilder.AppendLine(_sessionHistory.ToString());
                    initialTranscriptBuilder.AppendLine("=================================");
                    initialTranscriptBuilder.AppendLine();
                }
                initialTranscriptBuilder.AppendLine(workspaceContext);
                string initialTranscript = initialTranscriptBuilder.ToString();

                string transcript = initialTranscript;
                string response = string.Empty;

                bool completed = false;
                bool reachedToolStepLimit = false;
                const int maxToolSteps = 15;
                var successfulToolResults = new Dictionary<string, string>(StringComparer.Ordinal);

                for (int step = 0; step < maxToolSteps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string thinkingLabel = _getString("AgentActivityThinking", "생각중");
                    await RunOnUIThreadAsync(() => _agentPane.BeginThinkingActivity(thinkingLabel));

                    var responseBuilder = new StringBuilder();
                    int printedLength = 0;
                    bool toolCallPlaceholderShown = false;
                    bool visibleTextFlushed = false;
                    bool? isJsonToolCall = null;
                    bool hasToolCall = false;

                    response = await _llmService.RunAgentAsync(
                        instruction,
                        transcript,
                        BuildActiveSelectionContext(),
                        "run",
                        async chunk =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            responseBuilder.Append(chunk);
                            string streamedText = responseBuilder.ToString();

                            if (isJsonToolCall == null)
                            {
                                string trimmed = streamedText.TrimStart();
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    isJsonToolCall = trimmed.StartsWith("{", StringComparison.Ordinal);
                                    if (isJsonToolCall.Value)
                                    {
                                        toolCallPlaceholderShown = true;
                                        if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                                        {
                                            int tokenCount = (int)Math.Round(EstimateTokenCount(streamedText));
                                            string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                            _agentPane.DispatcherQueue.TryEnqueue(() =>
                                            {
                                                _agentPane.BeginThinkingActivity(label);
                                            });
                                        }
                                        else
                                        {
                                            _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(streamedText));
                                            printedLength = streamedText.Length;
                                        }
                                    }
                                }
                            }

                            if (isJsonToolCall == true)
                            {
                                if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                                {
                                    int tokenCount = (int)Math.Round(EstimateTokenCount(streamedText));
                                    string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _agentPane.UpdateThinkingActivity(label);
                                    });
                                }
                                else
                                {
                                    _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(chunk));
                                    printedLength = streamedText.Length;
                                }
                                return;
                            }

                            if (hasToolCall)
                            {
                                if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                                {
                                    int idx = streamedText.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                                    string toolCallText = idx >= 0 ? streamedText.Substring(idx) : streamedText;
                                    int tokenCount = (int)Math.Round(EstimateTokenCount(toolCallText));
                                    string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _agentPane.UpdateThinkingActivity(label);
                                    });
                                }
                                else
                                {
                                    _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(chunk));
                                    printedLength = streamedText.Length;
                                }
                                return;
                            }

                            int checkStart = Math.Max(0, streamedText.Length - chunk.Length - 11);
                            int toolCallIndex = streamedText.IndexOf("<tool_call>", checkStart, StringComparison.OrdinalIgnoreCase);
                            if (toolCallIndex >= 0)
                            {
                                hasToolCall = true;
                                if (printedLength < toolCallIndex)
                                {
                                    string textToPrint = streamedText.Substring(printedLength, toolCallIndex - printedLength);
                                    visibleTextFlushed = true;
                                    _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(textToPrint));
                                    printedLength = toolCallIndex;
                                }

                                if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                                {
                                    if (!toolCallPlaceholderShown)
                                    {
                                        toolCallPlaceholderShown = true;
                                        int tokenCount = (int)Math.Round(EstimateTokenCount(streamedText.Substring(toolCallIndex)));
                                        string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                        _agentPane.DispatcherQueue.TryEnqueue(() =>
                                        {
                                            _agentPane.BeginThinkingActivity(label);
                                        });
                                    }
                                }
                                else
                                {
                                    string toolCallText = streamedText.Substring(toolCallIndex);
                                    _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(toolCallText));
                                    printedLength = streamedText.Length;
                                }
                            }
                            else
                            {
                                int holdBack = 0;
                                string tag = "<tool_call>";
                                for (int i = 1; i < tag.Length; i++)
                                {
                                    string sub = tag.Substring(0, i);
                                    if (streamedText.EndsWith(sub, StringComparison.OrdinalIgnoreCase))
                                    {
                                        holdBack = i;
                                        break;
                                    }
                                }

                                int safeLength = streamedText.Length - holdBack;
                                if (printedLength < safeLength)
                                {
                                    string textToPrint = streamedText.Substring(printedLength, safeLength - printedLength);
                                    visibleTextFlushed = true;
                                    _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(textToPrint));
                                    printedLength = safeLength;
                                }
                            }
                            await Task.CompletedTask;
                        },
                        cancellationToken,
                        GetImageAttachmentsForCurrentRun());

                    cancellationToken.ThrowIfCancellationRequested();
                    response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

                    await RunOnUIThreadAsync(() =>
                    {
                        _agentPane.StopThinkingActivity();
                        int endLength = response.Length;
                        if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                        {
                            int toolCallIndex = response.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                            if (toolCallIndex >= 0)
                            {
                                endLength = toolCallIndex;
                            }
                        }

                        if (printedLength < endLength)
                        {
                            string remainingText = response.Substring(printedLength, endLength - printedLength);
                            visibleTextFlushed = true;
                            _agentPane.AppendOutputText(remainingText);
                        }
                    });

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryParseToolCall(response, out string toolName, out JsonElement arguments))
                    {
                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await RunOnUIThreadAsync(() =>
                            {
                                _agentPane.AppendOutputLine(_getString("AgentToolCallParseFailed", "도구 호출을 해석하지 못해 원문을 표시합니다."));
                                _agentPane.AppendOutputText(response);
                            });
                        }

                        await RunOnUIThreadAsync(() =>
                        {
                            AppendActivity(_getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        });

                        _sessionHistory.AppendLine($"[User Prompt]: {instruction}");
                        string runTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(runTranscript))
                        {
                            _sessionHistory.AppendLine(runTranscript.Trim());
                        }
                        _sessionHistory.AppendLine($"[Agent Response]: {response.Trim()}");
                        _sessionHistory.AppendLine();

                        completed = true;
                        break;
                    }

                    string normalizedToolName = NormalizeToolName(toolName);
                    string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, arguments);
                    bool skippedDuplicateTool = false;
                    string toolResult;
                    string toolResultForTranscript;
                    SelectionSnapshot selectionBeforeTool = CaptureActiveSelectionSnapshot();

                    if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName) &&
                        successfulToolResults.TryGetValue(toolInvocationKey, out string? previousToolResult))
                    {
                        skippedDuplicateTool = true;
                        toolResult = string.Format(
                            _getString(
                                "AgentDuplicateToolSkippedFormat",
                                "동일한 {0} 도구 호출이 이미 성공해서 다시 실행하지 않았습니다. 이전 결과를 바탕으로 다음 단계로 진행하세요."),
                            normalizedToolName);
                        toolResultForTranscript = $"{toolResult}\n\n[Previous successful result]\n{previousToolResult ?? string.Empty}";
                        AppendActivity(string.Format(
                            _getString("AgentActivityDuplicateToolSkippedFormat", "중복 도구 호출 건너뜀: {0}"),
                            normalizedToolName));
                    }
                    else
                    {
                        toolResult = await ExecuteToolAsync(toolName, arguments, cancellationToken);
                        toolResultForTranscript = toolResult;
                        if (IsSuccessfulToolResult(toolResult))
                        {
                            string successContinueMessage = _getString(
                                "AgentToolSuccessContinue",
                                "작업이 성공하였습니다. 다음 단계로 진행합니다.");
                            toolResult = AppendToolStatusMessage(toolResult, successContinueMessage);
                            toolResultForTranscript = toolResult;

                            if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName))
                            {
                                successfulToolResults[toolInvocationKey] = toolResult;
                                toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success. Continue from this result; do not repeat this same tool call unless the user explicitly asks to rerun it or the previous result is incomplete.]";
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    transcript = $"{transcript}\n\n[Agent tool call]\n{response}\n\n[Tool result: {toolName}]\n{toolResultForTranscript}";
                    
                    string displayResult = toolResult;
                    if (!_settingsService.CurrentSettings.LlmAgentVerbose && !toolResult.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase))
                    {
                        string normalizedName = normalizedToolName;
                        if (normalizedName == "read_file")
                        {
                            string path = GetStringArgument(arguments, "path");
                            displayResult = string.Format(_getString("AgentVerboseReadFileOnly", "파일을 읽었습니다: {0}"), path);
                        }
                        else if (normalizedName == "list_files")
                        {
                            string glob = GetStringArgument(arguments, "glob");
                            displayResult = string.Format(_getString("AgentVerboseListFilesOnly", "폴더를 읽었습니다: {0}"), glob);
                        }
                        else if (normalizedName == "search_text")
                        {
                            string query = GetStringArgument(arguments, "query");
                            displayResult = string.Format(_getString("AgentVerboseSearchTextOnly", "텍스트 검색을 완료했습니다: {0}"), query);
                        }
                        else if (normalizedName == "run_rg")
                        {
                            string args = GetStringArgument(arguments, "arguments");
                            displayResult = string.Format(_getString("AgentVerboseRunRgOnly", "Ripgrep 검색을 완료했습니다: {0}"), args);
                        }
                        else if (normalizedName == "web_search_exa")
                        {
                            string query = GetStringArgument(arguments, "query");
                            displayResult = string.Format(_getString("AgentVerboseWebSearchOnly", "웹 검색을 완료했습니다: {0}"), query);
                        }
                        else if (normalizedName == "web_fetch_exa")
                        {
                            string[] urls = GetUrlsArgument(arguments);
                            displayResult = string.Format(_getString("AgentVerboseWebFetchOnly", "웹페이지를 읽었습니다: {0}"), string.Join(", ", urls));
                        }
                    }

                    await RunOnUIThreadAsync(() =>
                    {
                        string outputHeader = skippedDuplicateTool
                            ? _getString("AgentDuplicateToolSkipped", "도구 중복 호출 건너뜀")
                            : _getString("AgentToolRunning", "도구 실행 중");
                        _agentPane.AppendOutputLine($"{outputHeader}: {toolName}");
                        _agentPane.AppendOutputText(displayResult.TrimEnd() + Environment.NewLine);
                    });

                    // Selection-edit verification: after a file-edit tool succeeds on the
                    // selection's file, inspect the raw affected lines. Do not use read_file
                    // here because its headers/line numbers would make every comparison differ.
                    string verifyToolName = NormalizeToolName(toolName);
                    bool isFileEditTool = verifyToolName is "replace_in_file" or "replace_range"
                        or "apply_patch" or "overwrite_file";
                    if (isFileEditTool && IsUnchangedEditCompletionResult(toolResult))
                    {
                        string completeMsg = _getString(
                            "AgentFileEditAlreadyComplete",
                            "요청한 작업을 완료하였습니다.");

                        transcript += "\n\n[File edit verification: requested content already matches the current file. Task complete.]";

                        await RunOnUIThreadAsync(() =>
                        {
                            _agentPane.AppendOutputLine(completeMsg);
                        });

                        _sessionHistory.AppendLine($"[User Prompt]: {instruction}");
                        string unchangedRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(unchangedRunTranscript))
                        {
                            _sessionHistory.AppendLine(unchangedRunTranscript.Trim());
                        }
                        _sessionHistory.AppendLine();

                        completed = true;
                        break;
                    }

                    if (isFileEditTool
                        && !toolResult.Contains("failed") && !toolResult.Contains("cancelled")
                        && selectionBeforeTool.HasLineRange)
                    {
                        try
                        {
                            string editedPath = GetEditPathArgument(arguments);
                            string resolvedEdited = Path.IsPathRooted(editedPath)
                                ? editedPath
                                : Path.Combine(_fileTools.WorkspaceRoot, editedPath);
                            string resolvedSelection = Path.IsPathRooted(selectionBeforeTool.SourcePath!)
                                ? selectionBeforeTool.SourcePath!
                                : Path.Combine(_fileTools.WorkspaceRoot, selectionBeforeTool.SourcePath!);

                            if (string.Equals(
                                Path.GetFullPath(resolvedEdited),
                                Path.GetFullPath(resolvedSelection),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                string verifyContent = await ReadRawLineRangeAsync(
                                    resolvedSelection,
                                    selectionBeforeTool.StartLine,
                                    selectionBeforeTool.EndLine);

                                if (SelectionLineRangeChanged(verifyContent, selectionBeforeTool.Text))
                                {
                                    string verifyMsg = _getString(
                                        "AgentSelectionEditVerified",
                                        "방금 적용한 선택 영역 변경을 확인했습니다. 작업을 마칩니다.");

                                    transcript += $"\n\n[Selection edit verification: lines {selectionBeforeTool.StartLine}-{selectionBeforeTool.EndLine} changed successfully. Task complete.]";

                                    await RunOnUIThreadAsync(() =>
                                    {
                                        _agentPane.AppendOutputLine(verifyMsg);
                                    });

                                    _sessionHistory.AppendLine($"[User Prompt]: {instruction}");
                                    string verifyRunTranscript = transcript.Substring(initialTranscript.Length);
                                    if (!string.IsNullOrWhiteSpace(verifyRunTranscript))
                                    {
                                        _sessionHistory.AppendLine(verifyRunTranscript.Trim());
                                    }
                                    _sessionHistory.AppendLine();

                                    completed = true;
                                    break;
                                }
                            }
                        }
                        catch { /* verification is best-effort; continue the loop on failure */ }
                    }

                    if (step == maxToolSteps - 1)
                    {
                        reachedToolStepLimit = true;
                    }
                }

                if (!completed && reachedToolStepLimit)
                {
                    string limitMsg = _getString(
                        "AgentToolStepLimitReached",
                        "도구 호출 한도에 도달해 작업을 중단했습니다. 지금까지의 결과를 검토한 뒤 다시 실행해 주세요.");

                    await RunOnUIThreadAsync(() =>
                    {
                        AppendActivity(limitMsg);
                        _agentPane.AppendOutputLine(limitMsg);
                    });

                    _sessionHistory.AppendLine($"[User Prompt]: {instruction}");
                    string runTranscript = transcript.Substring(initialTranscript.Length);
                    if (!string.IsNullOrWhiteSpace(runTranscript))
                    {
                        _sessionHistory.AppendLine(runTranscript.Trim());
                    }
                    _sessionHistory.AppendLine("[Agent Response]: Tool step limit reached before a final answer.");
                    _sessionHistory.AppendLine();
                }
            }
            catch (OperationCanceledException)
            {
                await RunOnUIThreadAsync(() =>
                {
                    AppendActivity(_getString("AgentActivityStopped", "중단됨"));
                    _agentPane.AppendOutputLine(_getString("AgentOutputStopped", "Agent 실행이 중단되었습니다."));
                });
            }
            catch (Exception ex)
            {
                await RunOnUIThreadAsync(() =>
                {
                    _agentPane.AppendOutputLine(string.Format(
                        _getString("AgentExceptionFormat", "Agent 실행 도중 예외가 발생했습니다: {0}"),
                        ex.Message));
                });
            }
            finally
            {
                _isRunning = false;
                if (ReferenceEquals(_runCancellation, cancellationSource))
                {
                    _runCancellation = null;
                }

                cancellationSource.Dispose();
                _agentPane.SetBusy(false);
                UpdateContextStats();
            }
        }

        private void ClearSession()
        {
            _sessionHistory.Clear();
            _sessionEdits.Clear();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.ResetOutput(_displayText.OutputPlaceholder);
                _agentPane.ClearActivity(_displayText.ActivityIdle);
                _agentPane.UpdateModifiedFiles(new List<AgentFileEditPreview>());
                _attachmentController.Clear();
                UpdateContextStats();
            });
        }

        private Task RunOnUIThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private Task<T> RunOnUIThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    T result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private Task<T> RunOnUIThreadAsync<T>(Func<Task<T>> func)
        {
            var tcs = new TaskCompletionSource<T>();
            _agentPane.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    T result = await func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private void StopAgent()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_runCancellation?.IsCancellationRequested == true)
            {
                return;
            }

            AppendActivity(_getString("AgentActivityStopRequested", "중단 요청됨"));
            if (string.IsNullOrWhiteSpace(_agentPane.Output.Text))
            {
                _agentPane.AppendOutputLine(_getString("AgentOutputStopping", "Agent 실행을 중단하는 중..."));
            }

            _runCancellation?.Cancel();
            _diffApprovalTcs?.TrySetResult(false);
        }

        private string BuildRunHeader(string instruction)
        {
            string modeText = _getString("AgentModeRun", "실행");
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            return $"{timestamp}  Agent {modeText}: {TruncateForActivity(instruction)}";
        }

        private string BuildInstructionDisplay(string userInstruction)
        {
            var selectedPresets = GetSelectedAgentPresets();
            if (selectedPresets.Count == 0)
            {
                return userInstruction;
            }

            string presetLabel = string.Join(", ", selectedPresets.Select(p => p.Name));
            if (string.IsNullOrWhiteSpace(userInstruction))
            {
                return $"[{presetLabel}]";
            }

            return $"[{presetLabel}] {userInstruction}";
        }

        private string BuildAgentInstruction(string userInstruction)
        {
            var selectedPresets = GetSelectedAgentPresets();
            if (selectedPresets.Count == 0)
            {
                return userInstruction;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[Agent persona/instruction presets]");
            foreach (var preset in selectedPresets)
            {
                builder.AppendLine($"## {preset.Name}");
                builder.AppendLine(preset.Content);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                builder.AppendLine("[User request]");
                builder.Append(userInstruction);
            }

            return builder.ToString().Trim();
        }

        private List<AgentPresetItem> GetSelectedAgentPresets()
        {
            return _agentPresets
                .Where(p => _selectedAgentPresetNames.Contains(p.Name))
                .ToList();
        }

        private string BuildWorkspaceContext(string instruction)
        {
            var context = new List<string>();
            context.Add("[Workspace root]");
            context.Add(_fileTools.WorkspaceRoot);
            context.Add("");

            AddReferencedPathContext(context, instruction);

            var openTabs = _openTabsProvider();
            if (openTabs.Count > 0)
            {
                context.Add("[Open tabs]");
                foreach (var tab in openTabs.Take(30))
                {
                    string tabName = string.IsNullOrWhiteSpace(tab.FilePath) ? tab.Title : tab.FilePath;
                    context.Add($"- {tabName}");
                }
            }

            var activeTab = _activeTabProvider();
            if (activeTab != null && _agentPane.IncludeActiveFile && !IsPdfTab(activeTab))
            {
                string title = string.IsNullOrWhiteSpace(activeTab.FilePath) ? activeTab.Title : activeTab.FilePath;
                string content = _getTabText(activeTab, MaxActiveFileContextChars);
                bool truncated = content.Length >= MaxActiveFileContextChars;

                context.Add("");
                context.Add("[Active tab]");
                context.Add($"Title: {activeTab.Title}");
                context.Add($"Path: {title}");
                context.Add($"Language: {activeTab.Language ?? "plaintext"}");
                context.Add($"Dirty: {activeTab.IsDirty}");
                context.Add("");
                context.Add("[Active tab content]");
                context.Add(content);
                if (truncated)
                {
                    context.Add("");
                    context.Add("[Context truncated: active tab exceeded the maximum included length]");
                }
            }

            if (_attachmentController.Count > 0)
            {
                context.Add("");
                context.Add("[Agent attachments]");
                foreach (var attachment in _attachmentController.Attachments)
                {
                    context.Add($"- {attachment.DisplayName} ({attachment.Detail}, approx {attachment.EstimatedTokens:N0} tokens)");
                    if (attachment.IsImage)
                    {
                        var image = attachment.ImageContent;
                        context.Add($"  Image input included separately for vision-capable models: {image?.MimeType}, {image?.Width}x{image?.Height}");
                    }
                    else if (!string.IsNullOrEmpty(attachment.TextContent))
                    {
                        context.Add("");
                        context.Add($"[Attachment file: {attachment.DisplayName}]");
                        context.Add($"Path: {attachment.Path}");
                        context.Add(attachment.TextContent);
                        context.Add("");
                    }
                }
            }

            return string.Join(Environment.NewLine, context);
        }

        private static bool IsPdfTab(OpenedTab tab)
        {
            return tab.IsPdfViewer ||
                   string.Equals(tab.Language, "pdf", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(tab.FilePath) &&
                    string.Equals(Path.GetExtension(tab.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase));
        }

        private void AddReferencedPathContext(List<string> context, string instruction)
        {
            var mentionedPaths = ExtractMentionedPaths(instruction).Take(20).ToList();
            if (mentionedPaths.Count == 0)
            {
                return;
            }

            context.Add("[User-referenced file names]");
            context.Add("Use these exact file names and paths. Do not translate, romanize, or rename them.");
            foreach (string mentionedPath in mentionedPaths)
            {
                context.Add($"- Mentioned exactly: {mentionedPath}");
                var matches = FindWorkspacePathMatches(mentionedPath, 5).ToList();
                if (matches.Count == 0)
                {
                    context.Add("  Workspace match: not found yet; if the user asked to create/save this file, create it with exactly this name.");
                }
                else
                {
                    foreach (string match in matches)
                    {
                        context.Add($"  Workspace match: {match}");
                    }
                }
            }
            context.Add("");
        }

        private IEnumerable<string> ExtractMentionedPaths(string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string extensions = "csv|md|txt|json|xml|html|htm|css|js|ts|tsx|jsx|cs|xaml|py|rs|java|kt|cpp|c|h|hpp|sql|xlsx|xls|docx|pptx|pdf|png|jpg|jpeg|webp|gif|bmp";
            string pattern = $@"(?<path>[^\s""'<>|:*?\r\n]+?\.(?:{extensions}))(?=$|[\s""'<>|,.;:!?()\[\]{{}}]|[가-힣])";
            foreach (Match match in Regex.Matches(instruction, pattern, RegexOptions.IgnoreCase))
            {
                string path = match.Groups["path"].Value
                    .Trim()
                    .Trim('.', ',', ';', ':', '!', '?', ')', ']', '}');

                if (string.IsNullOrWhiteSpace(path) ||
                    path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private IEnumerable<string> FindWorkspacePathMatches(string mentionedPath, int maxResults)
        {
            string root = _fileTools.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            string normalizedMention = mentionedPath.Replace('\\', '/');
            string mentionedFileName = Path.GetFileName(mentionedPath);
            int count = 0;

            foreach (string filePath in EnumerateWorkspaceFilesForContext(root))
            {
                string relative = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                bool isMatch = string.Equals(relative, normalizedMention, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Path.GetFileName(filePath), mentionedFileName, StringComparison.OrdinalIgnoreCase);
                if (!isMatch)
                {
                    continue;
                }

                yield return relative;
                count++;
                if (count >= maxResults)
                {
                    yield break;
                }
            }
        }

        private static IEnumerable<string> EnumerateWorkspaceFilesForContext(string root)
        {
            var excludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vs", "bin", "obj", "node_modules", ".next", "dist", "build"
            };

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<string> files;
                IEnumerable<string> subdirs;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                    subdirs = Directory.EnumerateDirectories(dir);
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
                    if (!excludedDirectoryNames.Contains(Path.GetFileName(subdir)))
                    {
                        pending.Push(subdir);
                    }
                }
            }
        }

        private IReadOnlyList<LlmMessageAttachment> GetImageAttachmentsForCurrentRun()
        {
            return _attachmentController.GetImageAttachments();
        }

        private async Task<string> ExecuteToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedToolName = NormalizeToolName(toolName);
                AppendActivity(GetToolStartMessage(normalizedToolName, arguments));

                bool isEditingActiveFile = false;
                if (normalizedToolName == "replace_in_file" ||
                    normalizedToolName == "replace_range" ||
                    normalizedToolName == "apply_patch" ||
                    normalizedToolName == "overwrite_file")
                {
                    try
                    {
                        string editedPath = GetEditPathArgument(arguments);
                        string activePath = _activeTabProvider()?.FilePath ?? string.Empty;
                        if (!string.IsNullOrEmpty(editedPath) && !string.IsNullOrEmpty(activePath))
                        {
                            string resolvedEdited = Path.IsPathRooted(editedPath) ? editedPath : Path.Combine(_fileTools.WorkspaceRoot, editedPath);
                            string resolvedActive = Path.IsPathRooted(activePath) ? activePath : Path.Combine(_fileTools.WorkspaceRoot, activePath);
                            if (string.Equals(Path.GetFullPath(resolvedEdited), Path.GetFullPath(resolvedActive), StringComparison.OrdinalIgnoreCase))
                            {
                                isEditingActiveFile = true;
                            }
                        }
                    }
                    catch {}
                }
                else if (normalizedToolName == "insert_text" ||
                         normalizedToolName == "create_tab")
                {
                    isEditingActiveFile = true;
                }

                string result;
                if (normalizedToolName == "replace_in_file")
                {
                    string path = GetEditPathArgument(arguments);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return "replace_in_file failed: path is empty and no selected, recently read, or active file path could be inferred.";
                    }

                    string oldText = GetFirstStringArgument(arguments, "oldText", "old_text", "find", "search", "target", "before");
                    string newText = GetFirstStringArgument(arguments, "newText", "new_text", "replace", "replacement", "after");
                    string content = GetFirstStringArgument(arguments, "content", "text");

                    result = string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(content)
                        ? await _fileTools.OverwriteFileAsync(path, content)
                        : await _fileTools.ReplaceInFileAsync(path, oldText, newText);
                }
                else
                {
                    result = normalizedToolName switch
                    {
                        "list_files" => await _fileTools.ListFilesAsync(
                            GetStringArgument(arguments, "glob"),
                            GetIntArgument(arguments, "maxResults", 80)),
                        "search_text" => await _fileTools.SearchTextAsync(
                            GetStringArgument(arguments, "query"),
                            GetStringArgument(arguments, "glob"),
                            GetIntArgument(arguments, "maxResults", 80)),
                        "run_rg" => await _fileTools.RunRgAsync(
                            GetStringArgument(arguments, "arguments"),
                            GetIntArgument(arguments, "timeoutMs", 10000),
                            cancellationToken),
                        "run_powershell" => await _fileTools.RunPowerShellAsync(
                            GetStringArgument(arguments, "command"),
                            GetIntArgument(arguments, "timeoutMs", 10000),
                            cancellationToken),
                        "read_file" => await _fileTools.ReadFileAsync(
                            GetStringArgument(arguments, "path"),
                            GetIntArgument(arguments, "startLine", 1),
                            GetIntArgument(arguments, "lineCount", 160)),
                        "create_file" => await CreateFileToolAsync(arguments),
                        "overwrite_file" => await OverwriteFileToolAsync(arguments),
                        "replace_range" => await ReplaceRangeToolAsync(arguments),
                        "apply_patch" => await ApplyPatchToolAsync(arguments),
                        "insert_text" => await InsertTextToolAsync(
                            GetFirstStringArgument(arguments, "content", "text", "newText", "new_text")),
                        "create_tab" => await CreateTabToolAsync(arguments),
                        "web_search_exa" => await _llmService.SearchExaAsync(
                            GetStringArgument(arguments, "query"),
                            GetIntArgument(arguments, "numResults", 5),
                            cancellationToken),
                        "web_fetch_exa" => await _llmService.FetchExaAsync(
                            GetUrlsArgument(arguments),
                            cancellationToken),
                        _ => $"Unknown tool: {toolName}"
                    };
                }
                cancellationToken.ThrowIfCancellationRequested();
                TrackSuccessfulFileToolPath(normalizedToolName, arguments, result);
                AppendActivity(string.Format(
                    _getString("AgentActivityToolDoneFormat", "도구 완료: {0}"),
                    normalizedToolName));

                if (isEditingActiveFile && !result.Contains("failed") && !result.Contains("cancelled"))
                {
                    ClearSelection();
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                AppendActivity(_getString("AgentActivityToolCancelled", "도구 실행 중단됨"));
                throw;
            }
            catch (Exception ex)
            {
                string result = $"Tool failed: {ex.Message}";
                AppendActivity(string.Format(
                    _getString("AgentActivityToolFailedFormat", "도구 실패: {0}"),
                    toolName));
                return result;
            }
        }

        private void AppendActivity(string message)
        {
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.AppendActivity(message);
            });
        }

        private string GetToolStartMessage(string toolName, JsonElement arguments)
        {
            return toolName switch
            {
                "list_files" => string.Format(
                    _getString("AgentActivityListFilesFormat", "파일 목록 조회 중: {0}"),
                    GetStringArgument(arguments, "glob")),
                "search_text" => string.Format(
                    _getString("AgentActivitySearchTextFormat", "텍스트 검색 중: {0}"),
                    GetStringArgument(arguments, "query")),
                "run_rg" => string.Format(
                    _getString("AgentActivityRunRgFormat", "rg 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "arguments"))),
                "run_powershell" => string.Format(
                    _getString("AgentActivityRunPowerShellFormat", "PowerShell 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "command"))),
                "read_file" => string.Format(
                    _getString("AgentActivityReadFileFormat", "파일 읽는 중: {0} ({1}줄부터 {2}줄)"),
                    GetStringArgument(arguments, "path"),
                    GetIntArgument(arguments, "startLine", 1),
                    GetIntArgument(arguments, "lineCount", 160)),
                "create_file" => string.Format(
                    _getString("AgentActivityCreateFileFormat", "파일 만드는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "replace_in_file" => string.Format(
                    _getString("AgentActivityReplaceFileFormat", "파일 수정 중: {0}"),
                    GetEditPathArgument(arguments)),
                "replace_range" => string.Format(
                    _getString("AgentActivityReplaceRangeFormat", "파일 범위 수정 중: {0} ({1}줄부터 {2}줄)"),
                    GetEditPathArgument(arguments),
                    GetReplaceRangeStartLineArgument(arguments, GetEditPathArgument(arguments)),
                    GetReplaceRangeEndLineArgument(arguments, GetEditPathArgument(arguments))),
                "apply_patch" => string.Format(
                    _getString("AgentActivityApplyPatchFormat", "파일 패치 적용 중: {0}"),
                    GetEditPathArgument(arguments)),
                "overwrite_file" => string.Format(
                    _getString("AgentActivityOverwriteFileFormat", "파일 덮어쓰는 중: {0}"),
                    GetEditPathArgument(arguments)),
                "insert_text" => _getString("AgentActivityInsertText", "현재 편집기에 입력 중"),
                "create_tab" => string.Format(
                    _getString("AgentActivityCreateTabFormat", "새 탭에 입력 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "name", "fileName", "file_name"))),
                "web_search_exa" => string.Format(
                    _getString("AgentActivityWebSearchExaFormat", "Exa 웹 검색 중: {0}"),
                    GetStringArgument(arguments, "query")),
                "web_fetch_exa" => string.Format(
                    _getString("AgentActivityWebFetchExaFormat", "Exa 웹 페이지 읽는 중: {0}"),
                    string.Join(", ", GetUrlsArgument(arguments))),
                _ => string.Format(
                    _getString("AgentActivityUnknownToolFormat", "도구 실행 중: {0}"),
                    toolName)
            };
        }

        private static async Task<string> ReadRawLineRangeAsync(string fullPath, int startLine, int endLine)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return string.Empty;
            }

            int start = Math.Max(1, startLine);
            int end = Math.Max(start, endLine);
            var lines = new List<string>();
            int currentLine = 0;

            using (var reader = new StreamReader(fullPath, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    currentLine++;
                    if (currentLine >= start && currentLine <= end)
                    {
                        lines.Add(line);
                    }

                    if (currentLine > end)
                    {
                        break;
                    }
                }
            }

            return string.Join("\n", lines);
        }

        private static bool SelectionLineRangeChanged(string currentLineRange, string originalSelection)
        {
            string current = NormalizeForSelectionCompare(currentLineRange);
            string original = NormalizeForSelectionCompare(originalSelection);
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(original))
            {
                return false;
            }

            if (string.Equals(current, original, StringComparison.Ordinal))
            {
                return false;
            }

            // For partial-line selections, the raw line range can differ from the exact
            // selection even before any edit. Only treat it as changed when the original
            // selected text no longer appears in the affected line window.
            return !current.Contains(original, StringComparison.Ordinal);
        }

        private static bool IsUnchangedEditCompletionResult(string toolResult)
        {
            return !string.IsNullOrWhiteSpace(toolResult) &&
                toolResult.Contains(" unchanged:", StringComparison.OrdinalIgnoreCase) &&
                !toolResult.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                !toolResult.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeForSelectionCompare(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
        }

        private static string TruncateForActivity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            return value.Length > 120 ? value.Substring(0, 120) + "..." : value;
        }

        private static string TruncateForConfirmation(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            const int maxConfirmationChars = 40;
            return value.Length > maxConfirmationChars ? value.Substring(0, maxConfirmationChars) + "..." : value;
        }

        private static bool TryParseToolCall(string response, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            if (!TryExtractToolCallPayload(response, out string payload))
            {
                return false;
            }

            string trimmedPayload = payload.Trim();

            // Check if the payload starts with a tool name, followed by JSON arguments.
            // e.g. create_file\n{"arguments":{"path":"test/fire.html","content":"..."}}
            int openBraceIndex = trimmedPayload.IndexOf('{');
            if (openBraceIndex > 0)
            {
                string possibleName = trimmedPayload.Substring(0, openBraceIndex).Trim();
                if (Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$"))
                {
                    toolName = possibleName;
                    string jsonPart = trimmedPayload.Substring(openBraceIndex);
                    try
                    {
                        using var document = JsonDocument.Parse(jsonPart);
                        var root = document.RootElement.Clone();
                        arguments = root.TryGetProperty("arguments", out var argsProp)
                            ? argsProp.Clone()
                            : root.Clone();
                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                    catch
                    {
                        // Fall through to lenient/other parsing methods on failure
                    }
                }
            }

            if (TryExtractToolCallJson(response, out string json))
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement.Clone();
                    if (root.TryGetProperty("name", out var nameProp))
                    {
                        toolName = nameProp.GetString() ?? string.Empty;
                        arguments = root.TryGetProperty("arguments", out var argsProp)
                            ? argsProp.Clone()
                            : JsonDocument.Parse("{}").RootElement.Clone();

                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                }
                catch
                {
                    // Fall through to lenient
                }
            }

            return TryParseToolCallLenient(payload, out toolName, out arguments);
        }

        private static bool TryParseToolCallLenient(string json, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            string? name = ExtractLenientStringProperty(json, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string argumentsText = ExtractLenientObjectProperty(json, "arguments") ?? "{}";
            var argumentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in ExtractLenientStringProperties(argumentsText))
            {
                argumentValues[property.Key] = property.Value;
            }

            var numberMatches = Regex.Matches(
                argumentsText,
                "\"(?<key>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"\\s*:\\s*(?<value>-?\\d+)",
                RegexOptions.Singleline);

            foreach (Match match in numberMatches)
            {
                string key = DecodeLenientJsonString(match.Groups["key"].Value);
                if (!argumentValues.ContainsKey(key))
                {
                    argumentValues[key] = match.Groups["value"].Value;
                }
            }

            string repairedJson = JsonSerializer.Serialize(argumentValues);
            using var argumentsDocument = JsonDocument.Parse(repairedJson);
            toolName = DecodeLenientJsonString(name);
            arguments = argumentsDocument.RootElement.Clone();
            return !string.IsNullOrWhiteSpace(toolName);
        }

        private static IEnumerable<KeyValuePair<string, string>> ExtractLenientStringProperties(string objectText)
        {
            if (string.IsNullOrEmpty(objectText))
            {
                yield break;
            }

            int i = 0;
            while (i < objectText.Length)
            {
                int keyQuoteIndex = objectText.IndexOf('"', i);
                if (keyQuoteIndex < 0)
                {
                    yield break;
                }

                if (!TryReadQuotedToken(objectText, keyQuoteIndex, out string rawKey, out int keyEndIndex))
                {
                    yield break;
                }

                int colonIndex = SkipWhitespace(objectText, keyEndIndex + 1);
                if (colonIndex >= objectText.Length || objectText[colonIndex] != ':')
                {
                    i = keyEndIndex + 1;
                    continue;
                }

                int valueStartIndex = SkipWhitespace(objectText, colonIndex + 1);
                if (valueStartIndex >= objectText.Length || objectText[valueStartIndex] != '"')
                {
                    i = valueStartIndex + 1;
                    continue;
                }

                int valueContentStart = valueStartIndex + 1;
                int valueEndIndex = FindLenientStringValueEnd(objectText, valueContentStart);
                string rawValue = objectText.Substring(
                    valueContentStart,
                    Math.Max(0, valueEndIndex - valueContentStart));

                yield return new KeyValuePair<string, string>(
                    DecodeLenientJsonString(rawKey),
                    DecodeLenientJsonString(rawValue));

                i = Math.Min(objectText.Length, valueEndIndex + 1);
            }
        }

        private static bool TryReadQuotedToken(string text, int quoteIndex, out string rawValue, out int endQuoteIndex)
        {
            rawValue = string.Empty;
            endQuoteIndex = -1;
            if (quoteIndex < 0 || quoteIndex >= text.Length || text[quoteIndex] != '"')
            {
                return false;
            }

            bool escaped = false;
            for (int i = quoteIndex + 1; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    rawValue = text.Substring(quoteIndex + 1, i - quoteIndex - 1);
                    endQuoteIndex = i;
                    return true;
                }
            }

            rawValue = text.Substring(quoteIndex + 1);
            endQuoteIndex = text.Length;
            return true;
        }

        private static int FindLenientStringValueEnd(string text, int valueStartIndex)
        {
            bool escaped = false;
            for (int i = valueStartIndex; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch != '"')
                {
                    continue;
                }

                int nextIndex = SkipWhitespace(text, i + 1);
                if (nextIndex >= text.Length ||
                    text[nextIndex] == '}' ||
                    text[nextIndex] == ']')
                {
                    return i;
                }

                if (text[nextIndex] == ',')
                {
                    int afterCommaIndex = SkipWhitespace(text, nextIndex + 1);
                    if (LooksLikePropertyKeyAt(text, afterCommaIndex))
                    {
                        return i;
                    }
                }
            }

            return text.Length;
        }

        private static bool LooksLikePropertyKeyAt(string text, int quoteIndex)
        {
            if (!TryReadQuotedToken(text, quoteIndex, out _, out int keyEndIndex))
            {
                return false;
            }

            int colonIndex = SkipWhitespace(text, keyEndIndex + 1);
            return colonIndex < text.Length && text[colonIndex] == ':';
        }

        private static int SkipWhitespace(string text, int startIndex)
        {
            int i = Math.Max(0, startIndex);
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return i;
        }

        private static string? ExtractLenientStringProperty(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"(?<value>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["value"].Value : null;
        }

        private static string? ExtractLenientObjectProperty(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            int objectStart = text.IndexOf('{', match.Index + match.Length);
            if (objectStart < 0)
            {
                return null;
            }

            if (TryExtractBalancedJsonObject(text, objectStart, out string objectText))
            {
                return objectText;
            }

            string fallback = text.Substring(objectStart);
            int closingTagIndex = fallback.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
            if (closingTagIndex >= 0)
            {
                fallback = fallback.Substring(0, closingTagIndex);
            }

            return fallback;
        }

        private static string DecodeLenientJsonString(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(ch);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(next);
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 'u' when i + 4 < value.Length:
                        string hex = value.Substring(i + 1, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
                        {
                            builder.Append((char)codePoint);
                            i += 4;
                        }
                        else
                        {
                            builder.Append('\\').Append(next);
                        }
                        break;
                    default:
                        builder.Append('\\').Append(next);
                        break;
                }
            }

            return builder.ToString();
        }

        private static bool IsToolCallLike(string response)
        {
            string trimmed = (response ?? string.Empty).TrimStart();
            return trimmed.StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("{\"name\"", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MightBeToolCallPrefix(string response)
        {
            string trimmed = (response ?? string.Empty).TrimStart();
            if (string.IsNullOrEmpty(trimmed))
            {
                return true;
            }

            return "<tool_call>".StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
                   "{\"name\"".StartsWith(trimmed, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractToolCallJson(string response, out string json)
        {
            json = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            int toolCallIndex = text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                text = text.Substring(toolCallIndex + "<tool_call>".Length).TrimStart();
            }

            int jsonStart = text.IndexOf('{');
            if (jsonStart < 0)
            {
                return false;
            }

            return TryExtractBalancedJsonObject(text, jsonStart, out json);
        }

        private static bool TryExtractToolCallPayload(string response, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            int toolCallIndex = text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                int payloadStart = toolCallIndex + "<tool_call>".Length;
                int payloadEnd = text.IndexOf("</tool_call>", payloadStart, StringComparison.OrdinalIgnoreCase);
                payload = payloadEnd >= 0
                    ? text.Substring(payloadStart, payloadEnd - payloadStart).Trim()
                    : text.Substring(payloadStart).Trim();
            }
            else
            {
                int jsonStart = text.IndexOf('{');
                if (jsonStart < 0)
                {
                    return false;
                }

                payload = text.Substring(jsonStart).Trim();
            }

            if (payload.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            int openBraceIndex = payload.IndexOf('{');
            if (openBraceIndex <= 0)
            {
                return false;
            }

            string possibleName = payload.Substring(0, openBraceIndex).Trim();
            return Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$");
        }

        private static bool TryExtractBalancedJsonObject(string text, int jsonStart, out string json)
        {
            json = string.Empty;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = jsonStart; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = text.Substring(jsonStart, i - jsonStart + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static string GetStringArgument(JsonElement arguments, string name)
        {
            return arguments.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static string[] GetUrlsArgument(JsonElement arguments)
        {
            if (arguments.TryGetProperty("urls", out var urlsProp))
            {
                if (urlsProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var item in urlsProp.EnumerateArray())
                    {
                        string val = item.GetString() ?? "";
                        if (!string.IsNullOrEmpty(val))
                        {
                            list.Add(val);
                        }
                    }
                    if (list.Count > 0)
                    {
                        return list.ToArray();
                    }
                }
                else if (urlsProp.ValueKind == JsonValueKind.String)
                {
                    string singleUrl = urlsProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(singleUrl))
                    {
                        return new[] { singleUrl };
                    }
                }
            }

            // Fallback to checking single "url" key
            string fallbackUrl = GetFirstStringArgument(arguments, "url", "uri", "address");
            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                return new[] { fallbackUrl };
            }

            return System.Array.Empty<string>();
        }

        private static bool ShouldSkipDuplicateSuccessfulTool(string normalizedToolName)
        {
            return normalizedToolName is "run_powershell"
                or "run_rg"
                or "search_text"
                or "create_file"
                or "overwrite_file"
                or "replace_in_file"
                or "replace_range"
                or "apply_patch"
                or "insert_text"
                or "create_tab"
                or "web_search_exa"
                or "web_fetch_exa";
        }

        private static bool IsSuccessfulToolResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return false;
            }

            if (result.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" failed:", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" cancelled", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" timed out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var exitCodeMatch = Regex.Match(result, @"\[exit_code\]\s+(-?\d+)", RegexOptions.IgnoreCase);
            return !exitCodeMatch.Success ||
                (int.TryParse(exitCodeMatch.Groups[1].Value, out int exitCode) && exitCode == 0);
        }

        private static string AppendToolStatusMessage(string result, string message)
        {
            string trimmedResult = (result ?? string.Empty).TrimEnd();
            if (string.IsNullOrWhiteSpace(message) ||
                trimmedResult.EndsWith(message, StringComparison.Ordinal))
            {
                return trimmedResult;
            }

            return $"{trimmedResult}\n{message}";
        }

        private static string BuildToolInvocationKey(string normalizedToolName, JsonElement arguments)
        {
            if (normalizedToolName == "run_powershell")
            {
                return normalizedToolName + ":" + NormalizePowerShellForDuplicateCheck(GetStringArgument(arguments, "command"));
            }

            var builder = new StringBuilder(normalizedToolName);
            builder.Append(':');
            AppendCanonicalJson(builder, arguments);
            return builder.ToString();
        }

        private static string NormalizePowerShellForDuplicateCheck(string command)
        {
            string normalized = Regex.Replace(command ?? string.Empty, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+2>\s*&\s*1\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return normalized.ToLowerInvariant();
        }

        private static void AppendCanonicalJson(StringBuilder builder, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    bool firstProperty = true;
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }

                        firstProperty = false;
                        builder.Append(JsonSerializer.Serialize(property.Name));
                        builder.Append(':');
                        AppendCanonicalJson(builder, property.Value);
                    }
                    builder.Append('}');
                    break;

                case JsonValueKind.Array:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!firstItem)
                        {
                            builder.Append(',');
                        }

                        firstItem = false;
                        AppendCanonicalJson(builder, item);
                    }
                    builder.Append(']');
                    break;

                case JsonValueKind.String:
                    builder.Append(JsonSerializer.Serialize(element.GetString() ?? string.Empty));
                    break;

                default:
                    builder.Append(element.GetRawText());
                    break;
            }
        }

        private string GetPathArgument(JsonElement arguments)
        {
            string path = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return string.Empty;
        }

        private string GetEditPathArgument(JsonElement arguments)
        {
            string path = GetPathArgument(arguments);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return InferEditPathFromContext();
        }

        private string InferEditPathFromContext()
        {
            if (!string.IsNullOrWhiteSpace(_lastSelectionSourcePath) &&
                !string.IsNullOrEmpty(GetActiveSelectionText()))
            {
                return _lastSelectionSourcePath;
            }

            if (!string.IsNullOrWhiteSpace(_currentRunLastFilePath))
            {
                return _currentRunLastFilePath;
            }

            string activePath = _activeTabProvider()?.FilePath ?? string.Empty;
            return string.IsNullOrWhiteSpace(activePath) ? string.Empty : activePath;
        }

        private void TrackSuccessfulFileToolPath(string normalizedToolName, JsonElement arguments, string result)
        {
            if (result.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (normalizedToolName is not ("read_file" or "replace_in_file" or "replace_range" or "apply_patch" or "overwrite_file"))
            {
                return;
            }

            string path = normalizedToolName == "read_file"
                ? GetPathArgument(arguments)
                : GetEditPathArgument(arguments);

            if (!string.IsNullOrWhiteSpace(path))
            {
                _currentRunLastFilePath = path;
            }
        }

        private bool ShouldUseActiveSelectionRangeForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(_lastSelectionSourcePath) ||
                string.IsNullOrEmpty(GetActiveSelectionText()) ||
                _lastSelectionStartLine <= 0 ||
                _lastSelectionEndLine <= 0)
            {
                return false;
            }

            try
            {
                string resolvedPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(_fileTools.WorkspaceRoot, path);
                string resolvedSelectionPath = Path.IsPathRooted(_lastSelectionSourcePath)
                    ? _lastSelectionSourcePath
                    : Path.Combine(_fileTools.WorkspaceRoot, _lastSelectionSourcePath);

                return string.Equals(
                    Path.GetFullPath(resolvedPath),
                    Path.GetFullPath(resolvedSelectionPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(path, _lastSelectionSourcePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private int GetReplaceRangeStartLineArgument(JsonElement arguments, string path)
        {
            return ShouldUseActiveSelectionRangeForPath(path)
                ? _lastSelectionStartLine
                : GetIntArgument(arguments, "startLine", 1);
        }

        private int GetReplaceRangeEndLineArgument(JsonElement arguments, string path)
        {
            return ShouldUseActiveSelectionRangeForPath(path)
                ? _lastSelectionEndLine
                : GetIntArgument(arguments, "endLine", 1);
        }

        private string GetReplaceRangeExpectedSnippetArgument(JsonElement arguments, string path)
        {
            return ShouldUseActiveSelectionRangeForPath(path)
                ? GetActiveSelectionText()
                : GetFirstStringArgument(arguments, "expectedSnippet", "expected_snippet", "guard", "expected");
        }

        private static string GetFirstStringArgument(JsonElement arguments, params string[] names)
        {
            foreach (string name in names)
            {
                string value = GetStringArgument(arguments, name);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string NormalizeToolName(string toolName)
        {
            string normalized = (toolName ?? string.Empty)
                .Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .Replace(" ", "_", StringComparison.Ordinal)
                .ToLowerInvariant();

            return normalized switch
            {
                "replace_text" => "replace_in_file",
                "replace" => "replace_in_file",
                "edit_file" => "replace_in_file",
                "write_file" => "overwrite_file",
                "write_text" => "overwrite_file",
                "insert" => "insert_text",
                "insert_into_editor" => "insert_text",
                "insert_text_into_editor" => "insert_text",
                "paste_text" => "insert_text",
                "new_tab" => "create_tab",
                "open_new_tab" => "create_tab",
                "create_new_tab" => "create_tab",
                "insert_text_new_tab" => "create_tab",
                "insert_into_new_tab" => "create_tab",
                "paste_text_new_tab" => "create_tab",
                "read" => "read_file",
                "search" => "search_text",
                "powershell" => "run_powershell",
                "rg" => "run_rg",
                "search_exa" => "web_search_exa",
                "exa_search" => "web_search_exa",
                "exa" => "web_search_exa",
                "fetch_exa" => "web_fetch_exa",
                "exa_fetch" => "web_fetch_exa",
                "fetch" => "web_fetch_exa",
                "patch" => "apply_patch",
                "apply_diff" => "apply_patch",
                _ => normalized
            };
        }

        private static int GetIntArgument(JsonElement arguments, string name, int fallback)
        {
            if (!arguments.TryGetProperty(name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            {
                return number;
            }

            return int.TryParse(value.GetString(), out int parsed) ? parsed : fallback;
        }

        private async Task<string> CreateFileToolAsync(JsonElement arguments)
        {
            string path = GetPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "create_file failed: path is empty. Provide the exact output file path requested by the user.";
            }

            return await _fileTools.CreateFileAsync(path, GetStringArgument(arguments, "content"));
        }

        private async Task<string> OverwriteFileToolAsync(JsonElement arguments)
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

        private async Task<string> ReplaceRangeToolAsync(JsonElement arguments)
        {
            string path = GetEditPathArgument(arguments);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "replace_range failed: path is empty and no selected, recently read, or active file path could be inferred.";
            }

            return await _fileTools.ReplaceRangeAsync(
                path,
                GetReplaceRangeStartLineArgument(arguments, path),
                GetReplaceRangeEndLineArgument(arguments, path),
                GetFirstStringArgument(arguments, "newText", "new_text", "content", "text"),
                GetReplaceRangeExpectedSnippetArgument(arguments, path));
        }

        private async Task<string> ApplyPatchToolAsync(JsonElement arguments)
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

        private async Task<string> InsertTextToolAsync(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "insert_text failed: content is empty.";
            }

            bool inserted = await RunOnUIThreadAsync(async () => await _insertIntoActiveEditorAsync(content));
            return inserted
                ? $"inserted into active editor: {content.Length:N0} chars"
                : "insert_text failed: active editor did not accept the text.";
        }

        private async Task<string> CreateTabToolAsync(JsonElement arguments)
        {
            string content = GetFirstStringArgument(arguments, "content", "text", "newText", "new_text");
            if (string.IsNullOrEmpty(content))
            {
                return "create_tab failed: content is empty.";
            }

            string title = GetFirstStringArgument(arguments, "title", "name", "fileName", "file_name");
            OpenedTab tab = await RunOnUIThreadAsync(() => _openNewTabWithContent(
                string.IsNullOrWhiteSpace(title) ? null : title,
                content));

            string displayTitle = string.IsNullOrWhiteSpace(tab.Title) ? "Untitled" : tab.Title;
            return $"created new tab: {displayTitle} ({content.Length:N0} chars)";
        }

        private async Task<bool> ConfirmFileEditAsync(AgentFileEditPreview preview)
        {
            AppendActivity(string.Format(
                _getString("AgentActivityDiffReviewFormat", "파일 변경 승인 대기 중: {0}"),
                preview.RelativePath));

            return await RunOnUIThreadAsync(async () =>
            {
                string titleKey = preview.ActionName switch
                {
                    "create_file" => "AgentCreateDialogTitle",
                    _ => "AgentEditDialogTitle"
                };

                string defaultTitle = preview.ActionName switch
                {
                    "create_file" => "Agent 파일 생성 확인: {0}",
                    _ => "Agent 파일 수정 확인: {0}"
                };

                string summaryText = preview.IsNewFile
                    ? string.Format(_getString("AgentCreateSummaryFormat", "파일을 생성하시겠습니까? 경로: {0}"), preview.RelativePath)
                    : string.Format(_getString("AgentEditSummaryFormat", "파일을 수정하시겠습니까? 경로: {0}"), preview.RelativePath);

                string headerText = string.Format(
                    _getString(titleKey, defaultTitle),
                    Path.GetFileName(preview.RelativePath));

                _agentPane.ShowDiffConfirm(headerText, summaryText);

                _diffApprovalTcs = new TaskCompletionSource<bool>();

                bool approved = await _diffApprovalTcs.Task;

                _agentPane.HideDiffConfirm();

                AppendActivity(approved
                    ? string.Format(_getString("AgentActivityDiffAppliedFormat", "변경 적용 승인: {0}"), preview.RelativePath)
                    : string.Format(_getString("AgentActivityDiffCancelledFormat", "변경 적용 취소: {0}"), preview.RelativePath));

                if (approved)
                {
                    TrackSessionEdit(preview);
                }

                return approved;
            });
        }

        private void TrackSessionEdit(AgentFileEditPreview preview)
        {
            var existing = _sessionEdits.FirstOrDefault(e => string.Equals(e.FullPath, preview.FullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var updatedEdit = new AgentFileEditPreview
                {
                    ActionName = preview.ActionName,
                    RelativePath = preview.RelativePath,
                    FullPath = preview.FullPath,
                    OldContent = preview.OldContent, // Immediately preceding content
                    NewContent = preview.NewContent,   // Latest version
                    IsNewFile = existing.IsNewFile     // Keep original flag
                };
                _sessionEdits.Remove(existing);
                _sessionEdits.Add(updatedEdit);
            }
            else
            {
                _sessionEdits.Add(preview);
            }

            _agentPane.UpdateModifiedFiles(_sessionEdits.ToList());
        }

        private async Task RevertFileChangeAsync(AgentFileEditPreview preview)
        {
            try
            {
                if (preview.IsNewFile)
                {
                    if (File.Exists(preview.FullPath))
                    {
                        File.Delete(preview.FullPath);
                    }
                }
                else
                {
                    await File.WriteAllTextAsync(preview.FullPath, preview.OldContent);
                }

                _sessionEdits.Remove(preview);
                _agentPane.UpdateModifiedFiles(_sessionEdits.ToList());

                if (_fileModifiedAsync != null)
                {
                    await _fileModifiedAsync(preview.FullPath);
                }

                AppendActivity(string.Format(_getString("AgentActivityFileReverted", "파일 변경 취소 완료: {0}"), preview.RelativePath));
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentRevertErrorTitle", "변경 취소 오류"),
                    string.Format(_getString("AgentRevertErrorFormat", "파일을 되돌리는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private async Task<bool> ConfirmPowerShellAsync(string command)
        {
            AppendActivity(_getString("AgentActivityPowerShellConfirmationPending", "PowerShell 실행 승인 대기 중"));

            return await RunOnUIThreadAsync(async () =>
            {
                string headerText = _getString("AgentPowerShellConfirmHeader", "PowerShell 실행 확인");
                string displayCommand = TruncateForConfirmation(command);
                string summaryText = string.Format(_getString("AgentPowerShellConfirmSummaryFormat", "아래 명령을 실행하시겠습니까?\n\n{0}"), displayCommand);

                _agentPane.ShowDiffConfirm(headerText, summaryText);

                _diffApprovalTcs = new TaskCompletionSource<bool>();

                bool approved = await _diffApprovalTcs.Task;

                _agentPane.HideDiffConfirm();

                AppendActivity(approved
                    ? _getString("AgentActivityPowerShellApproved", "PowerShell 실행 승인됨")
                    : _getString("AgentActivityPowerShellCancelled", "PowerShell 실행 취소됨"));

                return approved;
            });
        }

        private string BuildDiffSummary(AgentFileEditPreview preview)
        {
            if (preview.IsNewFile)
            {
                return string.Format(_getString("AgentCreateSummaryFormat", "파일을 생성하시겠습니까? 경로: {0}"), preview.RelativePath);
            }
            return string.Format(_getString("AgentEditSummaryFormat", "파일을 수정하시겠습니까? 경로: {0}"), preview.RelativePath);
        }

        private async Task InsertOutputAsync()
        {
            string fullOutput = _agentPane.Output.Text;
            string selectedOutput = _agentPane.Output.SelectedText;
            string output = IsSelectionFromOutput(selectedOutput, fullOutput)
                ? selectedOutput
                : fullOutput;

            if (string.IsNullOrWhiteSpace(output) ||
                _displayText.IsOutputPlaceholder(output))
            {
                _showError(
                    _getString("AgentInsertTitle", "Agent 응답 입력"),
                    _getString("AgentNoOutputToInsert", "입력할 Agent 응답이 없습니다."));
                return;
            }

            await _insertIntoActiveEditorAsync(output);
        }

        private static bool IsSelectionFromOutput(string selectedText, string fullOutput)
        {
            if (string.IsNullOrEmpty(selectedText) || string.IsNullOrEmpty(fullOutput))
            {
                return false;
            }

            if (fullOutput.Contains(selectedText, StringComparison.Ordinal))
            {
                return true;
            }

            string normalizedSelected = NormalizeLineEndings(selectedText);
            string normalizedOutput = NormalizeLineEndings(fullOutput);
            return normalizedSelected.Length > 0 &&
                normalizedOutput.Contains(normalizedSelected, StringComparison.Ordinal);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private void UpdateContextStats()
        {
            if (_isRunning)
            {
                return;
            }

            var activeTab = _activeTabProvider();
            string tabPart;
            if (activeTab == null)
            {
                tabPart = _getString("AgentNoActiveTab", "활성 탭 없음");
            }
            else
            {
                tabPart = Path.GetFileName(string.IsNullOrWhiteSpace(activeTab.FilePath) ? activeTab.Title : activeTab.FilePath);
                if (_agentPane.IncludeActiveFile && IsPdfTab(activeTab))
                {
                    tabPart = string.Format(_getString("AgentPdfActiveTabExcluded", "{0} (PDF 제외)"), tabPart);
                }
            }

            string activeSelectionText = GetActiveSelectionText();
            string selectionPart = string.IsNullOrEmpty(activeSelectionText)
                ? _getString("AgentNoSelection", "선택 없음")
                : string.Format(_getString("AgentSelectionStats", "선택 {0:N0}자"), activeSelectionText.Length);

            if (_attachmentController.Count > 0)
            {
                selectionPart = $"{selectionPart} · {_displayText.FormatAttachmentCount(_attachmentController.Count)}";
            }

            _agentPane.ContextStats.Text = string.Format(
                _getString("AgentContextStatsFormat", "맥락: {0} · {1}"),
                tabPart,
                selectionPart);

            double estimatedTokens = EstimateContextTokens();
            double kTokens = estimatedTokens / 1000.0;
            _agentPane.TokenCount.Text = string.Format(
                _getString("AgentTokenCountFormat", "{0:F1}k tokens"),
                kTokens);

            UpdateModelDisplay();
        }

        public void UpdateModelDisplay()
        {
            var settings = _settingsService.CurrentSettings;
            if (settings != null)
            {
                string provider = settings.LlmProvider ?? string.Empty;
                string model = settings.LlmModel ?? string.Empty;
                string format = _getString("AgentModelFormat", "모델: {0} ({1})");
                _agentPane.UpdateModelName(string.Format(format, model, provider));
            }
        }

        private async Task LoadAgentPresetsAsync()
        {
            try
            {
                if (File.Exists(_agentPresetsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_agentPresetsFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentPresetItem>>(json);
                    if (loaded != null)
                    {
                        _agentPresets.Clear();
                        _agentPresets.AddRange(loaded.Where(p => !string.IsNullOrWhiteSpace(p.Name)));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load agent presets: {ex.Message}");
            }

            UpdateAgentPresetsUI();
        }

        private async Task SaveAgentPresetsAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_agentPresetsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_agentPresets, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_agentPresetsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save agent presets: {ex.Message}");
            }

            UpdateAgentPresetsUI();
        }

        private void UpdateAgentPresetsUI()
        {
            var presetNames = _agentPresets.Select(p => p.Name).ToList();
            var selectedNames = _selectedAgentPresetNames.ToList();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.UpdateAgentPresetsMenu(presetNames, selectedNames, _getString);
                UpdateContextStats();
            });
        }

        private async void OnAddAgentPresetClick()
        {
            var nameBox = CreateAgentPresetNameBox();
            var contentBox = CreateAgentPresetContentBox();
            var stack = CreateAgentPresetDialogContent(nameBox, contentBox);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("AgentPresetAddText", "프리셋 추가"),
                Content = stack,
                PrimaryButtonText = _getString("AgentPresetSaveAddButton", "추가"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string presetName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(presetName))
            {
                _showError(_getString("AgentPresetNameEmptyTitle", "프리셋 추가 오류"), _getString("AgentPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            var existing = _agentPresets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !await ConfirmAgentPresetOverwriteAsync())
            {
                return;
            }

            if (existing != null)
            {
                _agentPresets.Remove(existing);
            }

            _agentPresets.Add(new AgentPresetItem
            {
                Name = presetName,
                Content = NormalizePresetContent(contentBox.Text)
            });
            await SaveAgentPresetsAsync();
        }

        private async void OnExportAgentPresetsClick()
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "agent-presets.json"
            };
            _initializePickerWindow(picker);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(_agentPresets, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetExportErrorTitle", "프리셋 내보내기 오류"),
                    string.Format(_getString("PresetExportErrorMessage", "프리셋을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private async void OnImportAgentPresetsClick()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(file.Path);
                var imported = JsonSerializer.Deserialize<List<AgentPresetItem>>(json);
                if (imported == null)
                {
                    throw new InvalidDataException(_getString("PresetImportInvalidFile", "가져올 수 있는 프리셋 JSON이 아닙니다."));
                }

                foreach (var item in imported.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    string name = item.Name.Trim();
                    string content = NormalizePresetContent(item.Content);
                    var existing = _agentPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        bool wasSelected = _selectedAgentPresetNames.Remove(existing.Name);
                        existing.Name = name;
                        existing.Content = content;
                        if (wasSelected)
                        {
                            _selectedAgentPresetNames.Add(existing.Name);
                        }
                    }
                    else
                    {
                        _agentPresets.Add(new AgentPresetItem
                        {
                            Name = name,
                            Content = content
                        });
                    }
                }

                await SaveAgentPresetsAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetImportErrorTitle", "프리셋 가져오기 오류"),
                    string.Format(_getString("PresetImportErrorMessage", "프리셋을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private async void OnAgentPresetEdited(string presetName)
        {
            var preset = _agentPresets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
            {
                return;
            }

            var nameBox = CreateAgentPresetNameBox(preset.Name);
            var contentBox = CreateAgentPresetContentBox(preset.Content);
            var stack = CreateAgentPresetDialogContent(nameBox, contentBox);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("AgentPresetEditTitle", "프리셋 수정"),
                Content = stack,
                PrimaryButtonText = _getString("AgentPresetEditSaveButton", "저장"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                _showError(_getString("AgentPresetNameEmptyTitle", "프리셋 수정 오류"), _getString("AgentPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            if (!newName.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            {
                var existing = _agentPresets.FirstOrDefault(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!await ConfirmAgentPresetOverwriteAsync())
                    {
                        return;
                    }
                    _agentPresets.Remove(existing);
                    _selectedAgentPresetNames.Remove(existing.Name);
                }
            }

            bool wasSelected = _selectedAgentPresetNames.Remove(preset.Name);
            preset.Name = newName;
            preset.Content = NormalizePresetContent(contentBox.Text);
            if (wasSelected)
            {
                _selectedAgentPresetNames.Add(preset.Name);
            }

            await SaveAgentPresetsAsync();
        }

        private async void OnAgentPresetDeleted(string presetName)
        {
            var preset = _agentPresets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
            {
                return;
            }

            _agentPresets.Remove(preset);
            _selectedAgentPresetNames.Remove(preset.Name);
            await SaveAgentPresetsAsync();
        }

        private void ToggleAgentPreset(string presetName)
        {
            if (!_agentPresets.Any(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!_selectedAgentPresetNames.Add(presetName))
            {
                _selectedAgentPresetNames.Remove(presetName);
            }

            UpdateAgentPresetsUI();
        }

        private void RemoveSelectedAgentPreset(string presetName)
        {
            _selectedAgentPresetNames.Remove(presetName);
            UpdateAgentPresetsUI();
        }

        private TextBox CreateAgentPresetNameBox(string text = "")
        {
            return new TextBox
            {
                PlaceholderText = _getString("AgentPresetSavePlaceholder", "프리셋 이름 입력..."),
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private TextBox CreateAgentPresetContentBox(string text = "")
        {
            var contentBox = new TextBox
            {
                PlaceholderText = _getString("AgentPresetContentPlaceholder", "페르소나/지침 내용..."),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Segoe UI, Malgun Gothic")
            };
            contentBox.Text = NormalizeTextBoxLineEndings(text);
            ScrollViewer.SetVerticalScrollMode(contentBox, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);
            contentBox.Paste += async (_, e) =>
            {
                e.Handled = true;
                await PasteClipboardTextAsync(contentBox);
            };
            return contentBox;
        }

        private StackPanel CreateAgentPresetDialogContent(TextBox nameBox, TextBox contentBox)
        {
            var stack = new StackPanel { Spacing = 10, Width = 400 };
            stack.Children.Add(new TextBlock { Text = _getString("AgentPresetSaveLabel", "프리셋 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentPresetContentLabel", "페르소나/지침") });
            stack.Children.Add(contentBox);
            return stack;
        }

        private async Task<bool> ConfirmAgentPresetOverwriteAsync()
        {
            _beforeDialog?.Invoke();
            var confirmDialog = new ContentDialog
            {
                Title = _getString("AgentPresetDuplicateTitle", "프리셋 중복"),
                Content = _getString("AgentPresetDuplicateMessage", "이미 동일한 이름의 프리셋이 존재합니다. 덮어쓰시겠습니까?"),
                PrimaryButtonText = _getString("Yes", "예"),
                CloseButtonText = _getString("No", "아니오"),
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };
            var confirmResult = await confirmDialog.ShowAsync();
            _afterDialog?.Invoke();
            return confirmResult == ContentDialogResult.Primary;
        }

        private static string NormalizePresetContent(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }

        private static async Task PasteClipboardTextAsync(TextBox textBox)
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    return;
                }

                string clipboardText = await content.GetTextAsync();
                InsertTextAtSelection(textBox, NormalizeTextBoxLineEndings(clipboardText));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to paste agent preset content: {ex.Message}");
            }
        }

        private static void InsertTextAtSelection(TextBox textBox, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string current = textBox.Text ?? string.Empty;
            int selectionStart = Math.Max(0, Math.Min(textBox.SelectionStart, current.Length));
            int selectionLength = Math.Max(0, Math.Min(textBox.SelectionLength, current.Length - selectionStart));

            textBox.Text = current.Substring(0, selectionStart) +
                text +
                current.Substring(selectionStart + selectionLength);
            textBox.SelectionStart = selectionStart + text.Length;
            textBox.SelectionLength = 0;
        }

        private static string NormalizeTextBoxLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        private double EstimateContextTokens()
        {
            string langCode = _displayText.LanguageCode;
            string systemPrompt = TxtAIEditor.Core.Services.LLM.AgentPromptBuilder.BuildSystemPrompt(langCode);

            string instruction = BuildAgentInstruction(_agentPane.Prompt.Text?.Trim() ?? string.Empty);
            string workspaceContext = BuildWorkspaceContext(instruction);
            string selectedText = BuildActiveSelectionContext();

            var initialTranscriptBuilder = new StringBuilder();
            if (_sessionHistory.Length > 0)
            {
                initialTranscriptBuilder.AppendLine("[Session History]");
                initialTranscriptBuilder.AppendLine(_sessionHistory.ToString());
                initialTranscriptBuilder.AppendLine("=================================");
                initialTranscriptBuilder.AppendLine();
            }
            initialTranscriptBuilder.AppendLine(workspaceContext);
            string initialTranscript = initialTranscriptBuilder.ToString();

            string userContent = TxtAIEditor.Core.Services.LLM.AgentPromptBuilder.BuildUserContent(instruction, initialTranscript, selectedText, string.Empty);

            return EstimateTokenCount(systemPrompt) + EstimateTokenCount(userContent) +
                   _attachmentController.EstimatedImageTokens;
        }

        private static double EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double tokens = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c <= 127)
                {
                    tokens += 0.25;
                }
                else
                {
                    tokens += 0.7;
                }
            }
            return tokens;
        }

    }
}
