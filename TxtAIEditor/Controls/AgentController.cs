using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int FallbackSessionHistoryPromptChars = 80_000;
        private const double PromptContextSafetyRatio = 0.95;

        private readonly ILLMService _llmService;
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Func<string, Task<AgentOpenFileResult>>? _openFileInEditorAsync;
        private readonly Func<AgentFileEditPreview, Task> _openDiffViewAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly Func<string, string, bool, Task>? _revertTabOrFileAsync;
        private readonly Action<string>? _closeTabById;
        private readonly Func<string, Task>? _navigateToFolderAsync;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentAttachmentController _attachmentController;
        private readonly AgentPresetController _presetController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentWorkspaceContextBuilder _workspaceContextBuilder;
        private readonly AgentOutputInsertController _outputInsertController;
        private readonly AgentConfirmationController _confirmationController;
        private readonly AgentTabToolController _tabToolController;
        private readonly AgentSelectionContextController _selectionContextController;
        private readonly AgentFileToolController _fileToolController;
        private readonly AgentContextStatsController _contextStatsController;
        private readonly AgentModelContextLimitProvider _modelContextLimits = new();
        private string _currentSessionId = Guid.NewGuid().ToString();

        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;
        private readonly StringBuilder _sessionHistory = new();
        private double _sessionHistoryTokenCount;
        private readonly DispatcherTimer _statsDebounceTimer;
        private double _currentRunTranscriptTokens;
        private readonly List<LlmMessageAttachment> _currentRunImageToolAttachments = new();

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
            Func<string, Task<AgentOpenFileResult>>? openFileInEditorAsync = null,
            Action? beforeDialog = null,
            Action? afterDialog = null,
            Func<string, string, bool, Task>? revertTabOrFileAsync = null,
            Action<string>? closeTabById = null,
            Func<string, Task>? navigateToFolderAsync = null,
            Func<OpenedTab, string?, Task<bool>>? saveTabAsync = null,
            Func<OpenedTab, string, Task<bool>>? editTabAsync = null)
        {
            _llmService = llmService;
            _settingsService = settingsService;
            _agentPane = agentPane;
            _activeTabProvider = activeTabProvider;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _initializePickerWindow = initializePickerWindow;
            _isGitRepoProvider = isGitRepoProvider;
            _openDiffViewAsync = openDiffViewAsync;
            _fileModifiedAsync = fileModifiedAsync;
            _openFileInEditorAsync = openFileInEditorAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _revertTabOrFileAsync = revertTabOrFileAsync;
            _closeTabById = closeTabById;
            _navigateToFolderAsync = navigateToFolderAsync;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _displayText = new AgentDisplayLocalizer(_getString);
            _attachmentController = new AgentAttachmentController(
                _agentPane,
                _initializePickerWindow,
                _showError,
                _getString,
                _displayText,
                () => _isRunning,
                UpdateContextStats,
                AgentTokenEstimator.Estimate,
                pdfTextExtractionService,
                _beforeDialog,
                _afterDialog);
            _selectionContextController = new AgentSelectionContextController(
                _activeTabProvider,
                () => _fileTools.WorkspaceRoot);
            _fileToolController = new AgentFileToolController(
                _fileTools,
                _selectionContextController,
                GetActiveTabForContext,
                () => _isRunning,
                _getString,
                _openFileInEditorAsync);
            _presetController = new AgentPresetController(
                _agentPane,
                _initializePickerWindow,
                _showError,
                _getString,
                () => UpdateContextStatsImmediate(),
                _beforeDialog,
                _afterDialog);
            _historyController = new AgentHistoryController(_agentPane);
            _sessionEditController = new AgentSessionEditController(
                _agentPane,
                async action => await RunOnUIThreadAsync<bool>(async () =>
                {
                    await action();
                    return true;
                }),
                _fileModifiedAsync,
                _revertTabOrFileAsync,
                _closeTabById,
                AppendActivity,
                _showError,
                _getString);
            _workspaceContextBuilder = new AgentWorkspaceContextBuilder(
                () => _fileTools.WorkspaceRoot,
                openTabsProvider,
                _attachmentController);
            _contextStatsController = new AgentContextStatsController(
                _settingsService,
                _agentPane,
                _displayText,
                _attachmentController,
                () => _isRunning,
                GetActiveTabForContext,
                GetActiveSelectionText,
                BuildActiveSelectionContext,
                BuildAgentInstruction,
                BuildWorkspaceContext,
                BuildSessionHistoryForPrompt,
                () => _currentRunTranscriptTokens,
                RefreshOutputDisplay,
                _getString,
                _modelContextLimits);
            _outputInsertController = new AgentOutputInsertController(
                _agentPane,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                _showError,
                _getString,
                _displayText.IsOutputPlaceholder,
                () => RunOnUIThreadAsync(() => { }));
            _tabToolController = new AgentTabToolController(
                GetActiveTabForContext,
                _activeTabProvider,
                openTabsProvider,
                getTabText,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                saveTabAsync,
                editTabAsync,
                _fileTools,
                _sessionEditController,
                action => RunOnUIThreadAsync(action),
                action => RunOnUIThreadAsync(action),
                action => RunOnUIThreadAsync(action));
            _confirmationController = new AgentConfirmationController(
                _settingsService,
                _agentPane,
                _fileTools,
                _isGitRepoProvider,
                _sessionEditController,
                async action => await RunOnUIThreadAsync(action),
                AppendActivity,
                _getString);
            _fileTools.ConfirmFileEditAsync = _confirmationController.ConfirmFileEditAsync;
            _fileTools.ConfirmPowerShellAsync = _confirmationController.ConfirmPowerShellAsync;
            _fileTools.ActivityReporter = AppendActivity;
            if (_fileModifiedAsync != null)
            {
                _fileTools.FileModifiedAsync = _fileModifiedAsync;
            }

            _statsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _statsDebounceTimer.Tick += (s, e) =>
            {
                _statsDebounceTimer.Stop();
                UpdateContextStatsImmediate();
            };

            WireEvents();
            UpdateContextStatsImmediate();
            QueueDeferredStartupDataLoad();
        }

        public IReadOnlyList<AgentFileEditPreview> SessionEdits => _sessionEditController.SessionEdits;

        private void QueueDeferredStartupDataLoad()
        {
            var dispatcher = _agentPane.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = LoadStartupDataAsync()) == true)
            {
                return;
            }

            _ = LoadStartupDataAsync();
        }

        private async Task LoadStartupDataAsync()
        {
            await _presetController.LoadAsync();
            await _historyController.LoadAsync(_currentSessionId);
        }

        public void SetActiveTab(OpenedTab? activeTab)
        {
            _selectionContextController.SetActiveTab(activeTab);
            UpdateContextStats();
        }

        public void SetSelectionText(string selectedText, OpenedTab? sourceTab = null, int startLine = 0, int endLine = 0)
        {
            _selectionContextController.SetSelectionText(selectedText, sourceTab, startLine, endLine);
            UpdateContextStats();
        }

        public void ClearSelection()
        {
            _selectionContextController.ClearSelection();
            UpdateContextStats();
        }

        private OpenedTab? GetActiveTabForContext()
        {
            return _selectionContextController.GetActiveTabForContext(_isRunning);
        }

        private Task<OpenedTab?> CaptureActiveTabForRunAsync()
        {
            return RunOnUIThreadAsync(() => _selectionContextController.CaptureActiveTabForRun(_isRunning));
        }

        private string GetActiveSelectionText()
        {
            return _selectionContextController.GetActiveSelectionText(_isRunning);
        }

        private string BuildActiveSelectionContext()
        {
            return _selectionContextController.BuildActiveSelectionContext(_isRunning);
        }

        private AgentSelectionSnapshot CaptureActiveSelectionSnapshot()
        {
            return _selectionContextController.CaptureActiveSelectionSnapshot(_isRunning);
        }

        private void WireEvents()
        {
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync();
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.NewSessionRequested += (_, _) => ClearSession();
            _agentPane.HistorySelected += (_, historyId) => LoadHistorySession(historyId);
            _agentPane.HistoryDeleted += async (_, historyId) => await DeleteHistorySessionAsync(historyId);
            _agentPane.HistoryToolbarDeleteClicked += async (_, _) => await ClearAllHistoryAsync();
            _agentPane.InsertOutputRequested += async (_, _) => await _outputInsertController.InsertOutputAsync();
            _agentPane.InsertNewTabOutputRequested += async (_, _) => await _outputInsertController.InsertNewTabOutputAsync();
            _agentPane.AddAttachmentRequested += async (_, _) => await _attachmentController.AddAttachmentsAsync();
            _agentPane.RemoveAttachmentRequested += (_, attachment) => _attachmentController.RemoveAttachment(attachment.Id);
            _agentPane.AgentPresetAddRequested += async (_, _) => await _presetController.AddPresetAsync();
            _agentPane.AgentPresetToggled += (_, presetName) => _presetController.TogglePreset(presetName);
            _agentPane.AgentPresetEdited += async (_, presetName) => await _presetController.EditPresetAsync(presetName);
            _agentPane.AgentPresetDeleted += async (_, presetName) => await _presetController.DeletePresetAsync(presetName);
            _agentPane.AgentPresetRemoved += (_, presetName) => _presetController.RemoveSelectedPreset(presetName);
            _agentPane.AgentPresetExportRequested += async (_, _) => await _presetController.ExportPresetsAsync();
            _agentPane.AgentPresetImportRequested += async (_, _) => await _presetController.ImportPresetsAsync();
            
            _agentPane.Prompt.TextChanged += (_, _) => UpdateContextStats();
            _agentPane.PlanningModeCheckBox.Checked += (_, _) => UpdateContextStats();
            _agentPane.PlanningModeCheckBox.Unchecked += (_, _) => UpdateContextStats();

            _agentPane.DiffApproved += (_, _) => _confirmationController.ApprovePending();
            _agentPane.DiffCancelled += (_, _) => _confirmationController.CancelPending();
            _agentPane.FileRevertRequested += async (_, preview) => await _sessionEditController.RevertAsync(preview);
            _agentPane.FileDiffRequested += async (_, preview) => await _openDiffViewAsync(preview);
        }
 
        private async Task RunAgentAsync()
        {
            if (_isRunning)
            {
                return;
            }
  
            var settings = _settingsService.CurrentSettings;

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
            _fileToolController.StartRun();
            _currentRunImageToolAttachments.Clear();
            _selectionContextController.ClearRunSnapshots();
            _currentRunTranscriptTokens = 0;
            var cancellationSource = new CancellationTokenSource();
            _runCancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            _agentPane.SetBusy(true);
            _agentPane.ClearActivity(_getString("AgentActivityStarting", "시작 중"));
            _agentPane.BeginOutputBlock(BuildRunHeader(BuildInstructionDisplay(userInstruction)));
            AppendActivity(_getString("AgentActivityCollectingContext", "맥락 수집 중"));

            await RunOnUIThreadAsync(() => { });

            try
            {
                await CaptureActiveTabForRunAsync();
                AgentSelectionSnapshot currentRunSelectionSnapshot = _selectionContextController.CaptureSelectionForRun(_isRunning);
                _fileToolController.SetRestrictEditsToSelection(
                    currentRunSelectionSnapshot.HasLineRange &&
                    !UserRequestAllowsEditsOutsideSelection(instruction));
                string workspaceContext = BuildWorkspaceContext(instruction);
                string lastWorkspaceContext = workspaceContext;
                string runSelectionContext = BuildActiveSelectionContext();
                var initialTranscriptBuilder = new StringBuilder();
                string sessionHistoryForPrompt = BuildSessionHistoryForPrompt(
                    instruction,
                    workspaceContext,
                    runSelectionContext);
                if (!string.IsNullOrWhiteSpace(sessionHistoryForPrompt))
                {
                    initialTranscriptBuilder.AppendLine("[Session History]");
                    initialTranscriptBuilder.AppendLine(sessionHistoryForPrompt);
                    initialTranscriptBuilder.AppendLine("=================================");
                    initialTranscriptBuilder.AppendLine();
                }
                initialTranscriptBuilder.AppendLine(workspaceContext);
                string initialTranscript = initialTranscriptBuilder.ToString();

                string transcript = initialTranscript;
                string response = string.Empty;

                bool completed = false;
                bool reachedToolStepLimit = false;
                int emptyResponseRetryCount = 0;
                const int maxEmptyResponseRetries = 1;
                int maxToolSteps = _settingsService.CurrentSettings.LlmMaxToolCalls > 0 ? _settingsService.CurrentSettings.LlmMaxToolCalls : 50;
                string? lastSuccessfulToolInvocationKey = null;
                string? lastSuccessfulToolResult = null;

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

                    string currentTranscript = transcript;
                    string sessionDiffLog = _sessionEditController.BuildDiffLog();
                    if (string.IsNullOrEmpty(sessionDiffLog))
                    {
                        sessionDiffLog = "(No changes have been made in this session yet.)";
                    }
                    currentTranscript = $"{transcript}\n\n[Diff log of changes made in this session]\n{sessionDiffLog}";

                    response = await _llmService.RunAgentAsync(
                        instruction,
                        currentTranscript,
                        runSelectionContext,
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
                                            int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
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
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
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
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(toolCallText));
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
                                    await AppendOutputTextAndStreamToTabAsync(textToPrint);
                                    printedLength = toolCallIndex;
                                }

                                if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                                {
                                    if (!toolCallPlaceholderShown)
                                    {
                                        toolCallPlaceholderShown = true;
                                        int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText.Substring(toolCallIndex)));
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
                                    await AppendOutputTextAndStreamToTabAsync(textToPrint);
                                    printedLength = safeLength;
                                }
                            }
                            await Task.CompletedTask;
                        },
                        cancellationToken,
                        GetImageAttachmentsForCurrentRun(),
                        _agentPane.PlanningMode);

                    cancellationToken.ThrowIfCancellationRequested();
                    response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            _agentPane.StopThinkingActivity();
                        });

                        if (emptyResponseRetryCount < maxEmptyResponseRetries)
                        {
                            emptyResponseRetryCount++;
                            string retryNote =
                                "\n\n[Agent empty response]\n" +
                                "The model returned no visible content. Continue by writing exactly one tool_call or a final answer.";

                            await RunOnUIThreadAsync(() =>
                            {
                                transcript += retryNote;
                                _currentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);
                                UpdateContextStatsImmediate(force: true);
                                AppendActivity(_getString(
                                    "AgentActivityEmptyResponseRetry",
                                    "빈 응답을 수신해 다시 시도합니다."));
                            });

                            continue;
                        }

                        string emptyResponseMessage = _getString(
                            "LlmErrorEmptyResponse",
                            "AI로부터 빈 응답을 수신했습니다.");

                        await RunOnUIThreadAsync(() =>
                        {
                            AppendActivity(emptyResponseMessage);
                            _agentPane.AppendOutputLine(emptyResponseMessage);
                        });

                        AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                        string emptyRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(emptyRunTranscript))
                        {
                            AppendSessionHistoryLine(emptyRunTranscript.Trim());
                        }
                        AppendSessionHistoryLine($"[Agent Response]: {emptyResponseMessage}");
                        AppendSessionHistoryLine();
                        _ = SaveCurrentSessionToHistoryAsync(userInstruction);

                        completed = true;
                        break;
                    }

                    await RunOnUIThreadAsync(() =>
                    {
                        _agentPane.StopThinkingActivity();
                    });

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
                        await AppendOutputTextAndStreamToTabAsync(remainingText);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!AgentToolCallParser.TryParse(response, out string toolName, out JsonElement arguments))
                    {
                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await RunOnUIThreadAsync(() =>
                                _agentPane.AppendOutputLine(_getString("AgentToolCallParseFailed", "도구 호출을 해석하지 못해 원문을 표시합니다.")));
                            await AppendOutputTextAndStreamToTabAsync(response);
                        }

                        await RunOnUIThreadAsync(async () =>
                        {
                            AppendActivity(_getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                            if (_agentPane.StreamToTab)
                            {
                                await _insertIntoActiveEditorAsync("\n");
                            }
                        });

                        AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                        string runTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(runTranscript))
                        {
                            AppendSessionHistoryLine(runTranscript.Trim());
                        }
                        AppendSessionHistoryLine($"[Agent Response]: {response.Trim()}");
                        AppendSessionHistoryLine();
                        _ = SaveCurrentSessionToHistoryAsync(userInstruction);

                        completed = true;
                        break;
                    }

                    string normalizedToolName = NormalizeToolName(toolName);
                    string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, arguments);
                    bool skippedDuplicateTool = false;
                    string toolResult;
                    string toolResultForTranscript;

                    if (string.Equals(lastSuccessfulToolInvocationKey, toolInvocationKey, StringComparison.Ordinal) &&
                        lastSuccessfulToolResult != null &&
                        ShouldSkipDuplicateSuccessfulTool(normalizedToolName))
                    {
                        skippedDuplicateTool = true;
                        toolResult = lastSuccessfulToolResult;
                        toolResultForTranscript = "[Tool execution skipped: identical successful call was the previous agent tool call. Reused the immediately previous result.]";
                    }
                    else
                    {
                        toolResult = await ExecuteToolAsync(toolName, arguments, cancellationToken);
                        toolResultForTranscript = toolResult;
                        if (IsSuccessfulToolResult(toolResult))
                        {
                            lastSuccessfulToolInvocationKey = toolInvocationKey;
                            lastSuccessfulToolResult = toolResult;
                            if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName))
                            {
                                toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success.]";
                            }
                        }
                        else
                        {
                            lastSuccessfulToolInvocationKey = null;
                            lastSuccessfulToolResult = null;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    await RunOnUIThreadAsync(() =>
                    {
                        string refreshedContext = BuildWorkspaceContext(instruction);
                        var addedPartBuilder = new StringBuilder();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine("[Agent tool call]");
                        addedPartBuilder.AppendLine(response);
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine($"[Tool result: {toolName}]");
                        addedPartBuilder.AppendLine(toolResultForTranscript);
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine("[Current workspace state]");
                        if (string.Equals(refreshedContext, lastWorkspaceContext, StringComparison.Ordinal))
                        {
                            addedPartBuilder.AppendLine("Unchanged since the previous workspace context.");
                        }
                        else
                        {
                            addedPartBuilder.AppendLine(refreshedContext);
                            lastWorkspaceContext = refreshedContext;
                        }

                        string addedPart = addedPartBuilder.ToString();
                        transcript += addedPart;
                        _currentRunTranscriptTokens += AgentTokenEstimator.Estimate(addedPart);
                        UpdateContextStatsImmediate(force: true);
                    });
                    
                    string displayResult = toolResult;
                    if (skippedDuplicateTool)
                    {
                        displayResult = _getString(
                            "AgentDuplicateToolReused",
                            "동일한 도구 호출이 이미 성공해 재실행하지 않았습니다.");
                    }
                    else if (!_settingsService.CurrentSettings.LlmAgentVerbose &&
                             !toolResult.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase))
                    {
                        string normalizedName = normalizedToolName;
                        if (normalizedName == "read_file")
                        {
                            string path = GetStringArgument(arguments, "path");
                            displayResult = string.Format(_getString("AgentVerboseReadFileOnly", "파일을 읽었습니다: {0}"), path);
                        }
                        else if (normalizedName == "extract_document")
                        {
                            string path = GetStringArgument(arguments, "path");
                            displayResult = string.Format(_getString("AgentVerboseExtractDocumentOnly", "문서 텍스트 추출을 완료했습니다: {0}"), path);
                        }
                        else if (normalizedName == "append_to_file")
                        {
                            string path = GetEditPathArgument(arguments);
                            displayResult = string.Format(_getString("AgentVerboseAppendFileOnly", "파일에 내용을 덧붙였습니다: {0}"), path);
                        }
                        else if (normalizedName == "search_replace")
                        {
                            string path = GetEditPathArgument(arguments);
                            displayResult = string.Format(_getString("AgentVerboseSearchReplaceOnly", "검색/치환을 완료했습니다: {0}"), path);
                        }
                        else if (normalizedName == "merge_files")
                        {
                            string target = GetFirstStringArgument(arguments, "targetPath", "target_path", "path", "target");
                            displayResult = string.Format(_getString("AgentVerboseMergeFilesOnly", "파일들을 합쳤습니다: {0}"), target);
                        }
                        else if (normalizedName == "split_file")
                        {
                            string source = GetEditPathArgument(arguments);
                            displayResult = string.Format(_getString("AgentVerboseSplitFileOnly", "파일을 분리했습니다: {0}"), source);
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
                        else if (normalizedName == "run_rga")
                        {
                            string args = GetStringArgument(arguments, "arguments");
                            displayResult = string.Format(_getString("AgentVerboseRunRgaOnly", "Ripgrep All 검색을 완료했습니다: {0}"), args);
                        }
                        else if (normalizedName == "web_search_exa")
                        {
                            string query = GetStringArgument(arguments, "query");
                            displayResult = string.Format(_getString("AgentVerboseWebSearchOnly", "웹 검색을 완료했습니다: {0}"), query);
                        }
                        else if (normalizedName == "web_fetch" || normalizedName == "web_fetch_exa")
                        {
                            string[] urls = GetUrlsArgument(arguments);
                            displayResult = string.Format(_getString("AgentVerboseWebFetchOnly", "웹페이지를 읽었습니다: {0}"), string.Join(", ", urls));
                        }
                        else if (normalizedName == "open_file")
                        {
                            string path = GetStringArgument(arguments, "path");
                            string resourceKey = toolResult.StartsWith("open_file activated_existing:", StringComparison.OrdinalIgnoreCase)
                                ? "AgentVerboseOpenFileExistingOnly"
                                : "AgentVerboseOpenFileOnly";
                            string fallback = toolResult.StartsWith("open_file activated_existing:", StringComparison.OrdinalIgnoreCase)
                                ? "이미 열려 있던 파일을 활성화했습니다: {0}"
                                : "파일을 열었습니다: {0}";
                            displayResult = string.Format(_getString(resourceKey, fallback), path);
                        }
                        else if (normalizedName == "save_tab")
                        {
                            string path = GetFirstStringArgument(arguments, "path", "filePath", "file_path");
                            if (string.IsNullOrEmpty(path))
                            {
                                path = GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id");
                            }
                            displayResult = string.Format(_getString("AgentVerboseSaveTabOnly", "탭을 저장했습니다: {0}"), path);
                        }
                        else if (normalizedName == "edit_tab")
                        {
                            string path = GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id");
                            displayResult = string.Format(_getString("AgentVerboseEditTabOnly", "탭 내용을 수정했습니다: {0}"), path);
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



                    string verifyToolName = NormalizeToolName(toolName);
                    bool isFileEditTool = verifyToolName is "replace_in_file" or "search_replace" or "replace_range"
                        or "apply_patch" or "overwrite_file" or "append_to_file";
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

                        AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                        string unchangedRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(unchangedRunTranscript))
                        {
                            AppendSessionHistoryLine(unchangedRunTranscript.Trim());
                        }
                        AppendSessionHistoryLine();
                        _ = SaveCurrentSessionToHistoryAsync(userInstruction);

                        completed = true;
                        break;
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

                    AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                    string runTranscript = transcript.Substring(initialTranscript.Length);
                    if (!string.IsNullOrWhiteSpace(runTranscript))
                    {
                        AppendSessionHistoryLine(runTranscript.Trim());
                    }
                    AppendSessionHistoryLine("[Agent Response]: Tool step limit reached before a final answer.");
                    AppendSessionHistoryLine();
                    _ = SaveCurrentSessionToHistoryAsync(userInstruction);
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
                _selectionContextController.ClearRunSnapshots();
                _currentRunImageToolAttachments.Clear();
                _fileToolController.FinishRun();
                if (ReferenceEquals(_runCancellation, cancellationSource))
                {
                    _runCancellation = null;
                }

                cancellationSource.Dispose();
                _agentPane.SetBusy(false);
                UpdateContextStatsImmediate();
            }
        }

        private void ClearSession()
        {
            _currentSessionId = Guid.NewGuid().ToString();
            _sessionHistory.Clear();
            _sessionHistoryTokenCount = 0;
            _currentRunTranscriptTokens = 0;
            _sessionEditController.Clear();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.Prompt.Text = string.Empty;
                _agentPane.ResetOutput(_displayText.OutputPlaceholder);
                _agentPane.ClearActivity(_displayText.ActivityIdle);
                _attachmentController.Clear();
                UpdateContextStatsImmediate();
                _historyController.UpdateUI(_currentSessionId);
            });
        }

        private Task AppendOutputTextAndStreamToTabAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Task.CompletedTask;
            }

            return RunOnUIThreadAsync(async () =>
            {
                _agentPane.AppendOutputText(text);
                if (_agentPane.StreamToTab)
                {
                    await _insertIntoActiveEditorAsync(text);
                }
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

        private Task RunOnUIThreadAsync(Func<Task> func)
        {
            var tcs = new TaskCompletionSource();
            _agentPane.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await func();
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
            _confirmationController.CancelPending();
        }

        private string BuildRunHeader(string instruction)
        {
            string modeText = _getString("AgentModeRun", "실행");
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            return $"{timestamp}  Agent {modeText}: {TruncateForActivity(instruction)}";
        }

        private string BuildInstructionDisplay(string userInstruction)
        {
            return _presetController.BuildInstructionDisplay(userInstruction);
        }

        private string BuildAgentInstruction(string userInstruction)
        {
            return _presetController.BuildAgentInstruction(userInstruction);
        }

        private string BuildSessionHistoryForPrompt(
            string instruction,
            string workspaceContext,
            string selectedText)
        {
            if (_sessionHistory.Length == 0)
            {
                return string.Empty;
            }

            string history = _sessionHistory.ToString();
            int contextLimit = GetModelContextLimitForPromptBudget();
            if (contextLimit <= 0)
            {
                if (IsLmStudioProvider())
                {
                    return string.Empty;
                }

                return BuildSessionHistoryTail(history, FallbackSessionHistoryPromptChars);
            }

            double maxPromptTokens = Math.Floor(contextLimit * PromptContextSafetyRatio);
            double basePromptTokens = EstimateAgentPromptTokens(
                instruction,
                workspaceContext,
                selectedText);
            double sessionHistoryWrapperTokens = AgentTokenEstimator.Estimate(
                "[Session History]" + Environment.NewLine +
                Environment.NewLine +
                "=================================" + Environment.NewLine + Environment.NewLine);
            double availableHistoryTokens = maxPromptTokens - basePromptTokens - sessionHistoryWrapperTokens;

            if (availableHistoryTokens <= 0)
            {
                return string.Empty;
            }

            int hardCharCap = Math.Min(history.Length, FallbackSessionHistoryPromptChars);
            string cappedHistory = BuildSessionHistoryTail(history, hardCharCap);
            if (AgentTokenEstimator.Estimate(cappedHistory) <= availableHistoryTokens)
            {
                return cappedHistory;
            }

            return TrimSessionHistoryToTokenBudget(history, hardCharCap, availableHistoryTokens);
        }

        private string BuildSessionHistoryTail(string history, int maxChars)
        {
            if (string.IsNullOrEmpty(history) || maxChars <= 0)
            {
                return string.Empty;
            }

            if (history.Length <= maxChars)
            {
                return history;
            }

            string tail = history.Substring(history.Length - maxChars);
            int firstLineBreak = tail.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < tail.Length)
            {
                tail = tail.Substring(firstLineBreak + 1);
            }

            return "[... earlier session history omitted to keep the prompt compact ...]" +
                Environment.NewLine +
                tail;
        }

        private string TrimSessionHistoryToTokenBudget(
            string history,
            int maxChars,
            double tokenBudget)
        {
            string best = string.Empty;
            int low = 1;
            int high = Math.Min(history.Length, maxChars);

            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                string candidate = BuildSessionHistoryTail(history, mid);
                double candidateTokens = AgentTokenEstimator.Estimate(candidate);

                if (candidateTokens <= tokenBudget)
                {
                    best = candidate;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return best;
        }

        private double EstimateAgentPromptTokens(
            string instruction,
            string workspaceContext,
            string selectedText)
        {
            string languageCode = _displayText.LanguageCode;
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(languageCode);
            string userContent = AgentPromptBuilder.BuildUserContent(
                instruction,
                workspaceContext,
                selectedText,
                string.Empty,
                languageCode);

            return AgentTokenEstimator.Estimate(systemPrompt) +
                AgentTokenEstimator.Estimate(userContent) +
                _attachmentController.EstimatedImageTokens;
        }

        private int GetModelContextLimitForPromptBudget()
        {
            return _modelContextLimits.GetContextLimit(
                _settingsService.CurrentSettings,
                () => _agentPane.DispatcherQueue.TryEnqueue(() => UpdateContextStatsImmediate(force: true)));
        }

        private bool IsLmStudioProvider()
        {
            string provider = _settingsService.CurrentSettings?.LlmProvider ?? string.Empty;
            return provider.Contains("lm studio", StringComparison.OrdinalIgnoreCase) ||
                provider.Contains("lmstudio", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildWorkspaceContext(string instruction)
        {
            return _workspaceContextBuilder.Build(
                instruction,
                GetActiveTabForContext(),
                true,
                CaptureActiveSelectionSnapshot().HasLineRange);
        }

        private IReadOnlyList<LlmMessageAttachment> GetImageAttachmentsForCurrentRun()
        {
            var attachments = new List<LlmMessageAttachment>();
            attachments.AddRange(_attachmentController.GetImageAttachments());
            attachments.AddRange(_currentRunImageToolAttachments);
            return attachments;
        }

        private async Task<string> ExecuteToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedToolName = NormalizeToolName(toolName);
                string? selectionScopeError = _fileToolController.ValidateSelectionEditScope(normalizedToolName, arguments);
                if (!string.IsNullOrEmpty(selectionScopeError))
                {
                    AppendActivity(string.Format(
                        _getString("AgentActivityToolBlockedFormat", "도구 차단: {0}"),
                        normalizedToolName));
                    return selectionScopeError;
                }

                AppendActivity(GetToolStartMessage(normalizedToolName, arguments));

                string result;
                if (normalizedToolName == "replace_in_file")
                {
                    result = await _fileToolController.ReplaceInFileAsync(arguments);
                }
                else if (normalizedToolName == "search_replace")
                {
                    result = await _fileToolController.SearchReplaceAsync(arguments);
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
                        "run_rga" => await _fileTools.RunRgaAsync(
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
                        "read_image" => await ReadImageToolAsync(arguments),
                        "extract_document" => await _fileTools.ExtractDocumentAsync(
                            GetExtractDocumentInputPathArgument(arguments),
                            GetExtractDocumentOutputPathArgument(arguments),
                            GetIntArgument(arguments, "maxChars", 5000000)),
                        "create_file" => await _fileToolController.CreateFileAsync(arguments),
                        "overwrite_file" => await _fileToolController.OverwriteFileAsync(arguments),
                        "append_to_file" => await _fileToolController.AppendToFileAsync(arguments),
                        "merge_files" => await _fileToolController.MergeFilesAsync(arguments),
                        "split_file" => await _fileToolController.SplitFileAsync(arguments),
                        "replace_range" => await _fileToolController.ReplaceRangeAsync(arguments),
                        "apply_patch" => await _fileToolController.ApplyPatchAsync(arguments),
                        "insert_text" => await _tabToolController.InsertTextAsync(
                            GetFirstStringArgument(arguments, "content", "text", "newText", "new_text")),
                        "create_tab" => await _tabToolController.CreateTabAsync(arguments),
                        "edit_tab" => await _tabToolController.EditTabAsync(arguments),
                        "save_tab" => await _tabToolController.SaveTabAsync(arguments),
                        "open_file" => await _fileToolController.OpenFileAsync(arguments),
                        "web_search_exa" => await _llmService.SearchExaAsync(
                            GetStringArgument(arguments, "query"),
                            GetIntArgument(arguments, "numResults", 5),
                            cancellationToken),
                        "web_fetch" => await _llmService.FetchExaAsync(
                            GetUrlsArgument(arguments),
                            cancellationToken),
                        "web_fetch_exa" => await _llmService.FetchExaAsync(
                            GetUrlsArgument(arguments),
                            cancellationToken),
                        _ => $"Unknown tool: {toolName}"
                    };
                }
                cancellationToken.ThrowIfCancellationRequested();
                _fileToolController.TrackSuccessfulFileToolPath(normalizedToolName, arguments, result);
                AppendActivity(string.Format(
                    _getString("AgentActivityToolDoneFormat", "도구 완료: {0}"),
                    normalizedToolName));

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

        private async Task<string> ReadImageToolAsync(JsonElement arguments)
        {
            AgentReadImageResult imageResult = await _fileTools.ReadImageAsync(
                GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path"));

            if (imageResult.Attachment != null)
            {
                _currentRunImageToolAttachments.RemoveAll(existing =>
                    string.Equals(existing.DisplayName, imageResult.Attachment.DisplayName, StringComparison.OrdinalIgnoreCase));
                _currentRunImageToolAttachments.Add(imageResult.Attachment);
            }

            return imageResult.TranscriptText;
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
                "run_rga" => string.Format(
                    _getString("AgentActivityRunRgaFormat", "rga 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "arguments"))),
                "run_powershell" => string.Format(
                    _getString("AgentActivityRunPowerShellFormat", "PowerShell 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "command"))),
                "read_file" => string.Format(
                    _getString("AgentActivityReadFileFormat", "파일 읽는 중: {0} ({1}줄부터 {2}줄)"),
                    GetStringArgument(arguments, "path"),
                    GetIntArgument(arguments, "startLine", 1),
                    GetIntArgument(arguments, "lineCount", 160)),
                "read_image" => string.Format(
                    _getString("AgentActivityReadImageFormat", "이미지 읽는 중: {0}"),
                    GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path")),
                "extract_document" => string.Format(
                    _getString("AgentActivityExtractDocumentFormat", "문서 텍스트 추출 중: {0}"),
                    GetExtractDocumentInputPathArgument(arguments)),
                "create_file" => string.Format(
                    _getString("AgentActivityCreateFileFormat", "파일 만드는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "replace_in_file" => string.Format(
                    _getString("AgentActivityReplaceFileFormat", "파일 수정 중: {0}"),
                    GetEditPathArgument(arguments)),
                "search_replace" => string.Format(
                    _getString("AgentActivitySearchReplaceFormat", "검색/치환 중: {0}"),
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
                "append_to_file" => string.Format(
                    _getString("AgentActivityAppendFileFormat", "파일 덧붙이는 중: {0}"),
                    GetEditPathArgument(arguments)),
                "merge_files" => string.Format(
                    _getString("AgentActivityMergeFilesFormat", "파일 합치는 중: {0}"),
                    GetFirstStringArgument(arguments, "targetPath", "target_path", "path", "target")),
                "split_file" => string.Format(
                    _getString("AgentActivitySplitFileFormat", "파일 분리하는 중: {0}"),
                    GetEditPathArgument(arguments)),
                "insert_text" => _getString("AgentActivityInsertText", "현재 편집기에 입력 중"),
                "create_tab" => string.Format(
                    _getString("AgentActivityCreateTabFormat", "새 탭에 입력 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "name", "fileName", "file_name"))),
                "save_tab" => string.Format(
                    _getString("AgentActivitySaveTabFormat", "탭 저장 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id", "path", "filePath", "file_path"))),
                "edit_tab" => string.Format(
                    _getString("AgentActivityEditTabFormat", "탭 수정 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id"))),
                "open_file" => string.Format(
                    _getString("AgentActivityOpenFileFormat", "파일 여는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "web_search_exa" => string.Format(
                    _getString("AgentActivityWebSearchExaFormat", "Exa 웹 검색 중: {0}"),
                    GetStringArgument(arguments, "query")),
                "web_fetch" => string.Format(
                    _getString("AgentActivityWebFetchFormat", "웹 페이지 읽는 중: {0}"),
                    string.Join(", ", GetUrlsArgument(arguments))),
                "web_fetch_exa" => string.Format(
                    _getString("AgentActivityWebFetchExaFormat", "Exa 웹 페이지 읽는 중: {0}"),
                    string.Join(", ", GetUrlsArgument(arguments))),
                _ => string.Format(
                    _getString("AgentActivityUnknownToolFormat", "도구 실행 중: {0}"),
                    toolName)
            };
        }

        private string GetExtractDocumentInputPathArgument(JsonElement arguments)
        {
            string explicitPath = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path", "source", "input");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            string legacyArguments = GetStringArgument(arguments, "arguments");
            if (string.IsNullOrWhiteSpace(legacyArguments))
            {
                return string.Empty;
            }

            foreach (string token in SplitCommandLineArguments(legacyArguments))
            {
                if (IsSupportedDocumentPathToken(token))
                {
                    return token;
                }
            }

            return string.Empty;
        }

        private string GetExtractDocumentOutputPathArgument(JsonElement arguments)
        {
            string explicitOutput = GetFirstStringArgument(arguments, "outputPath", "output_path", "targetPath", "target_path", "target", "output");
            if (!string.IsNullOrWhiteSpace(explicitOutput))
            {
                return explicitOutput;
            }

            string legacyArguments = GetStringArgument(arguments, "arguments");
            if (string.IsNullOrWhiteSpace(legacyArguments))
            {
                return string.Empty;
            }

            var tokens = SplitCommandLineArguments(legacyArguments);
            int sourceIndex = tokens.FindIndex(IsSupportedDocumentPathToken);
            if (sourceIndex < 0)
            {
                return string.Empty;
            }

            for (int i = sourceIndex + 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrWhiteSpace(token) ||
                    token.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return token;
            }

            return string.Empty;
        }

        private static List<string> SplitCommandLineArguments(string arguments)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return tokens;
            }

            var builder = new StringBuilder();
            char quote = '\0';

            foreach (char ch in arguments)
            {
                if ((ch == '"' || ch == '\'') && (quote == '\0' || quote == ch))
                {
                    quote = quote == '\0' ? ch : '\0';
                    continue;
                }

                if (char.IsWhiteSpace(ch) && quote == '\0')
                {
                    if (builder.Length > 0)
                    {
                        tokens.Add(builder.ToString());
                        builder.Clear();
                    }
                    continue;
                }

                builder.Append(ch);
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
            }

            return tokens;
        }

        private static bool IsSupportedDocumentPathToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("-", StringComparison.Ordinal))
            {
                return false;
            }

            string extension = Path.GetExtension(token);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase);
        }

        private string GetPathArgument(JsonElement arguments)
        {
            return _fileToolController.GetPathArgument(arguments);
        }

        private string GetEditPathArgument(JsonElement arguments)
        {
            return _fileToolController.GetEditPathArgument(arguments);
        }

        private int GetReplaceRangeStartLineArgument(JsonElement arguments, string path)
        {
            return _fileToolController.GetReplaceRangeStartLineArgument(arguments, path);
        }

        private int GetReplaceRangeEndLineArgument(JsonElement arguments, string path)
        {
            return _fileToolController.GetReplaceRangeEndLineArgument(arguments, path);
        }

        public void UpdateContextStats()
        {
            if (_isRunning)
            {
                return;
            }

            _statsDebounceTimer.Stop();
            _statsDebounceTimer.Start();
        }

        private void UpdateContextStatsImmediate(bool force = false)
        {
            _contextStatsController.Update(force);
        }

        public void UpdateModelDisplay(bool forceClearCache = false)
        {
            _contextStatsController.UpdateModelDisplay(forceClearCache);
        }

        private void AppendSessionHistory(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _sessionHistory.Append(text);
            _sessionHistoryTokenCount += AgentTokenEstimator.Estimate(text);
        }

        private void AppendSessionHistoryLine(string line = "")
        {
            _sessionHistory.AppendLine(line);
            _sessionHistoryTokenCount += AgentTokenEstimator.Estimate(line + Environment.NewLine);
        }

        private async Task SaveCurrentSessionToHistoryAsync(string userInstruction)
        {
            if (_sessionHistory.Length == 0)
            {
                return;
            }

            var item = new AgentHistoryItem
            {
                Id = _currentSessionId,
                Timestamp = DateTime.Now,
                Title = GetSessionTitle(userInstruction),
                SessionHistoryText = _sessionHistory.ToString(),
                SessionHistoryTokenCount = _sessionHistoryTokenCount,
                SessionEdits = _sessionEditController.SessionEdits.ToList(),
                WorkspaceRoot = _fileTools.WorkspaceRoot
            };

            await _historyController.SaveSessionAsync(item, _currentSessionId);
        }

        private void LoadHistorySession(string historyId)
        {
            if (_isRunning) return;

            var item = _historyController.GetSession(historyId);
            if (item == null) return;

            _currentSessionId = item.Id;
            _sessionHistory.Clear();
            _sessionHistory.Append(item.SessionHistoryText);
            _sessionHistoryTokenCount = item.SessionHistoryTokenCount;
            _currentRunTranscriptTokens = 0;

            _sessionEditController.Replace(item.SessionEdits);

            // Restore workspace folder if it was saved and still exists
            string savedWorkspaceRoot = item.WorkspaceRoot ?? string.Empty;
            if (_navigateToFolderAsync != null &&
                !string.IsNullOrWhiteSpace(savedWorkspaceRoot) &&
                Directory.Exists(savedWorkspaceRoot))
            {
                _ = _navigateToFolderAsync(savedWorkspaceRoot);
            }

            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                string formatted = AgentHistoryFormatter.Format(item.SessionHistoryText, _settingsService.CurrentSettings.LlmAgentVerbose);
                _agentPane.ResetOutput(formatted);
                _agentPane.ClearActivity(_getString("AgentHistoryLoadedActivity", "세션 히스토리 로드됨"));
                _attachmentController.Clear();
                UpdateContextStatsImmediate();
                _historyController.UpdateUI(_currentSessionId);
            });
        }

        private async Task DeleteHistorySessionAsync(string historyId)
        {
            if (string.IsNullOrEmpty(historyId)) return;

            await _historyController.DeleteAsync(historyId, _currentSessionId);

            if (string.Equals(_currentSessionId, historyId, StringComparison.Ordinal))
            {
                ClearSession();
            }
            else
            {
                _historyController.UpdateUI(_currentSessionId);
            }
        }

        private async Task ClearAllHistoryAsync()
        {
            await _historyController.ClearAsync(_currentSessionId);
            ClearSession();
        }

        private void RefreshOutputDisplay()
        {
            if (_isRunning) return;

            string text = _sessionHistory.ToString();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string formatted = AgentHistoryFormatter.Format(text, _settingsService.CurrentSettings.LlmAgentVerbose);
            _agentPane.ResetOutput(formatted);
        }

        private string GetSessionTitle(string instruction)
        {
            string firstLine;
            if (string.IsNullOrWhiteSpace(instruction))
            {
                string selectedPresetLabel = _presetController.GetSelectedPresetLabel();
                if (!string.IsNullOrEmpty(selectedPresetLabel))
                {
                    firstLine = selectedPresetLabel;
                }
                else
                {
                    return "Untitled Session";
                }
            }
            else
            {
                firstLine = instruction.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? instruction;
                firstLine = firstLine.Trim();
            }

            if (firstLine.Length > 20)
            {
                return firstLine.Substring(0, 17) + "...";
            }
            return firstLine;
        }

    }
}
