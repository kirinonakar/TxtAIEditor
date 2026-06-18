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
        private readonly Func<Task<bool>>? _beginStreamIntoActiveEditorAsync;
        private readonly Func<string, Task<bool>>? _streamTextIntoActiveEditorAsync;
        private readonly Func<Task>? _endStreamIntoActiveEditorAsync;
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
        private readonly AgentToolExecutionController _toolExecutionController;
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
        private bool _streamToTabActive;

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
            Func<OpenedTab, string, Task<bool>>? editTabAsync = null,
            Func<Task<bool>>? beginStreamIntoActiveEditorAsync = null,
            Func<string, Task<bool>>? streamTextIntoActiveEditorAsync = null,
            Func<Task>? endStreamIntoActiveEditorAsync = null)
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
            _beginStreamIntoActiveEditorAsync = beginStreamIntoActiveEditorAsync;
            _streamTextIntoActiveEditorAsync = streamTextIntoActiveEditorAsync;
            _endStreamIntoActiveEditorAsync = endStreamIntoActiveEditorAsync;
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
            _toolExecutionController = new AgentToolExecutionController(
                _llmService,
                _fileTools,
                _fileToolController,
                _tabToolController,
                AddCurrentRunImageToolAttachment,
                AppendActivity,
                _getString);
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

            string initialTranscript = string.Empty;
            string transcript = string.Empty;
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
                initialTranscript = initialTranscriptBuilder.ToString();

                transcript = initialTranscript;
                string response = string.Empty;

                bool completed = false;
                bool reachedToolStepLimit = false;
                int emptyResponseRetryCount = 0;
                const int maxEmptyResponseRetries = 1;
                int toolCallFormatRetryCount = 0;
                const int maxToolCallFormatRetries = 2;
                int repeatedDuplicateToolSkipCount = 0;
                string? lastDuplicateToolInvocationKey = null;
                const int maxRepeatedDuplicateToolSkips = 3;
                bool planningMode = _agentPane.PlanningMode;
                int maxToolSteps = _settingsService.CurrentSettings.LlmMaxToolCalls > 0 ? _settingsService.CurrentSettings.LlmMaxToolCalls : 50;
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
                    bool suppressStreamingText = planningMode;

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
                                            printedLength = streamedText.Length;
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
                                    printedLength = streamedText.Length;
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
                                    if (!suppressStreamingText)
                                    {
                                        visibleTextFlushed = true;
                                        await AppendOutputTextAndStreamToTabAsync(textToPrint);
                                    }
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
                                    if (!suppressStreamingText)
                                    {
                                        visibleTextFlushed = true;
                                        await AppendOutputTextAndStreamToTabAsync(textToPrint);
                                    }
                                    printedLength = safeLength;
                                }
                            }
                            await Task.CompletedTask;
                        },
                        cancellationToken,
                        GetImageAttachmentsForCurrentRun(),
                        planningMode);

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

                    bool responseHasToolSyntax = AgentToolCallParser.ContainsToolCallSyntax(response);

                    int endLength = response.Length;
                    if (!_settingsService.CurrentSettings.LlmAgentVerbose)
                    {
                        int toolCallIndex = response.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                        if (toolCallIndex >= 0)
                        {
                            endLength = toolCallIndex;
                        }
                    }

                    if (!responseHasToolSyntax && suppressStreamingText && !string.IsNullOrEmpty(response))
                    {
                        visibleTextFlushed = true;
                        await AppendOutputTextAndStreamToTabAsync(response);
                    }
                    else if (!responseHasToolSyntax && printedLength < endLength)
                    {
                        string remainingText = response.Substring(printedLength, endLength - printedLength);
                        visibleTextFlushed = true;
                        await AppendOutputTextAndStreamToTabAsync(remainingText);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!AgentToolCallParser.TryParse(response, out string toolName, out JsonElement arguments))
                    {
                        if (responseHasToolSyntax)
                        {
                            toolCallFormatRetryCount++;
                            string retryNote = BuildToolCallFormatRetryNote("The tool_call JSON could not be parsed.");
                            transcript += "\n\n" + retryNote;
                            _currentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);

                            string retryMessage = _getString(
                                "AgentToolCallFormatRetry",
                                "도구 호출 형식이 섞여 다시 요청합니다.");
                            await RunOnUIThreadAsync(() =>
                            {
                                AppendActivity(retryMessage);
                                _agentPane.AppendOutputLine(retryMessage);
                                UpdateContextStatsImmediate(force: true);
                            });

                            if (toolCallFormatRetryCount > maxToolCallFormatRetries)
                            {
                                string limitMessage = _getString(
                                    "AgentToolCallFormatRetryLimit",
                                    "도구 호출 형식 오류가 반복되어 Agent 실행을 중단했습니다. 작업을 다시 실행해 주세요.");
                                await RunOnUIThreadAsync(() =>
                                {
                                    AppendActivity(limitMessage);
                                    _agentPane.AppendOutputLine(limitMessage);
                                });

                                AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                                string formatRunTranscript = transcript.Substring(initialTranscript.Length);
                                if (!string.IsNullOrWhiteSpace(formatRunTranscript))
                                {
                                    AppendSessionHistoryLine(formatRunTranscript.Trim());
                                }
                                AppendSessionHistoryLine($"[Agent Response]: {limitMessage}");
                                AppendSessionHistoryLine();
                                _ = SaveCurrentSessionToHistoryAsync(userInstruction);

                                completed = true;
                                break;
                            }

                            continue;
                        }

                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await AppendOutputTextAndStreamToTabAsync(response);
                        }

                        toolCallFormatRetryCount = 0;
                        await RunOnUIThreadAsync(async () =>
                        {
                            AppendActivity(_getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                            if (_agentPane.StreamToTab)
                            {
                                await StreamTextToTabAsync("\n");
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
                    bool stopAfterDuplicateLoopGuard = false;
                    string toolResult;
                    string toolResultForTranscript;
                    toolCallFormatRetryCount = 0;

                    if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName) &&
                        successfulToolResults.TryGetValue(toolInvocationKey, out string? cachedToolResult))
                    {
                        skippedDuplicateTool = true;
                        toolResult = cachedToolResult ?? string.Empty;
                        if (string.Equals(lastDuplicateToolInvocationKey, toolInvocationKey, StringComparison.Ordinal))
                        {
                            repeatedDuplicateToolSkipCount++;
                        }
                        else
                        {
                            lastDuplicateToolInvocationKey = toolInvocationKey;
                            repeatedDuplicateToolSkipCount = 1;
                        }

                        var duplicateResultBuilder = new StringBuilder();
                        duplicateResultBuilder.AppendLine("[Tool execution skipped: identical successful call already ran earlier in this agent run. Cached result follows; use it instead of calling the tool again.]");
                        duplicateResultBuilder.AppendLine(toolResult);
                        if (repeatedDuplicateToolSkipCount >= 2)
                        {
                            duplicateResultBuilder.AppendLine();
                            duplicateResultBuilder.AppendLine("[Loop guard] You repeated the same skipped tool call. Choose exactly one different next action, or write the final answer. Do not call this tool again unless a later mutating tool changes the relevant files or workspace state.");
                        }

                        stopAfterDuplicateLoopGuard = repeatedDuplicateToolSkipCount >= maxRepeatedDuplicateToolSkips;
                        toolResultForTranscript = duplicateResultBuilder.ToString().TrimEnd();
                    }
                    else
                    {
                        lastDuplicateToolInvocationKey = null;
                        repeatedDuplicateToolSkipCount = 0;
                        toolResult = await _toolExecutionController.ExecuteAsync(toolName, arguments, cancellationToken);
                        toolResultForTranscript = toolResult;
                        if (IsSuccessfulToolResult(toolResult))
                        {
                            if (IsMutatingTool(normalizedToolName) ||
                                string.Equals(normalizedToolName, "run_powershell", StringComparison.Ordinal))
                            {
                                successfulToolResults.Clear();
                            }

                            if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName))
                            {
                                successfulToolResults[toolInvocationKey] = toolResult;
                                toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success.]";
                            }
                        }
                        else
                        {
                            successfulToolResults.Remove(toolInvocationKey);
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
                        addedPartBuilder.AppendLine("[Current workspace context snapshot]");
                        if (string.Equals(refreshedContext, lastWorkspaceContext, StringComparison.Ordinal))
                        {
                            addedPartBuilder.AppendLine("Context summary unchanged since the previous snapshot. File edits, if any, are tracked in the next [Diff log of changes made in this session].");
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
                    
                    string displayResult = _toolExecutionController.FormatDisplayResult(
                        normalizedToolName,
                        arguments,
                        toolResult,
                        skippedDuplicateTool,
                        _settingsService.CurrentSettings.LlmAgentVerbose);

                    await RunOnUIThreadAsync(() =>
                    {
                        string outputHeader = skippedDuplicateTool
                            ? _getString("AgentDuplicateToolSkipped", "도구 중복 호출 건너뜀")
                            : _getString("AgentToolRunning", "도구 실행 중");
                        _agentPane.AppendOutputLine($"{outputHeader}: {toolName}");
                        _agentPane.AppendOutputText(displayResult.TrimEnd() + Environment.NewLine);
                    });

                    if (stopAfterDuplicateLoopGuard)
                    {
                        string loopMessage = _getString(
                            "AgentLoopGuardStopped",
                            "동일한 도구 호출 반복 루프가 감지되어 Agent 실행을 중단했습니다. 출력된 결과를 확인한 뒤 다시 실행해 주세요.");
                        await RunOnUIThreadAsync(() =>
                        {
                            AppendActivity(loopMessage);
                            _agentPane.AppendOutputLine(loopMessage);
                        });

                        AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                        string loopRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(loopRunTranscript))
                        {
                            AppendSessionHistoryLine(loopRunTranscript.Trim());
                        }
                        AppendSessionHistoryLine($"[Agent Response]: {loopMessage}");
                        AppendSessionHistoryLine();
                        _ = SaveCurrentSessionToHistoryAsync(userInstruction);

                        completed = true;
                        break;
                    }



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

                AppendSessionHistoryLine($"[User Prompt]: {instruction}");
                string runTranscript = transcript.Substring(initialTranscript.Length);
                if (!string.IsNullOrWhiteSpace(runTranscript))
                {
                    AppendSessionHistoryLine(runTranscript.Trim());
                }
                AppendSessionHistoryLine("[Agent Response]: Agent execution was interrupted by the user.");
                AppendSessionHistoryLine();
                _ = SaveCurrentSessionToHistoryAsync(userInstruction);
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
                await RunOnUIThreadAsync(async () => await FinishStreamToTabAsync());
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
                    await StreamTextToTabAsync(text);
                }
            });
        }

        private async Task StreamTextToTabAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!_streamToTabActive)
            {
                bool started = _beginStreamIntoActiveEditorAsync == null
                    ? true
                    : await _beginStreamIntoActiveEditorAsync();
                if (!started)
                {
                    return;
                }
                _streamToTabActive = true;
            }

            if (_streamTextIntoActiveEditorAsync != null)
            {
                await _streamTextIntoActiveEditorAsync(text);
                return;
            }

            await _insertIntoActiveEditorAsync(text);
        }

        private async Task FinishStreamToTabAsync()
        {
            if (!_streamToTabActive)
            {
                return;
            }

            _streamToTabActive = false;
            if (_endStreamIntoActiveEditorAsync != null)
            {
                await _endStreamIntoActiveEditorAsync();
            }
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

        private static string BuildToolCallFormatRetryNote(string detail)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Agent tool call format error]");
            builder.AppendLine("The previous assistant response was not executed.");
            builder.AppendLine("A tool turn must be exactly one <tool_call>...</tool_call> tag and no other text.");
            builder.AppendLine("Progress updates, plans, markdown, and explanations must be in a separate plain-text response with no tool_call tag.");
            builder.AppendLine("Re-emit only the tool_call now, or write the final answer with no tool_call tag.");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AppendLine($"Parser detail: {detail}");
            }

            return builder.ToString().TrimEnd();
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

        private void AddCurrentRunImageToolAttachment(LlmMessageAttachment attachment)
        {
            _currentRunImageToolAttachments.RemoveAll(existing =>
                string.Equals(existing.DisplayName, attachment.DisplayName, StringComparison.OrdinalIgnoreCase));
            _currentRunImageToolAttachments.Add(attachment);
        }

        private void AppendActivity(string message)
        {
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.AppendActivity(message);
            });
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
