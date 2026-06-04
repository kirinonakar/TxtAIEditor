using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int MaxActiveFileContextChars = 120_000;
        private const int MaxAttachmentTextChars = 120_000;
        private const int MaxImageDimension = 1024;

        private readonly ILLMService _llmService;
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Func<OpenedTab, int, string> _getTabText;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Func<AgentFileEditPreview, Task> _openDiffViewAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly List<AgentFileEditPreview> _sessionEdits = new();
        private readonly List<AgentAttachmentState> _attachments = new();

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

        private sealed class AgentAttachmentState
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Path { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Detail { get; set; } = string.Empty;
            public string? TextContent { get; set; }
            public LlmMessageAttachment? ImageContent { get; set; }
            public int EstimatedTokens { get; set; }
            public bool IsImage => ImageContent != null;
        }

        public AgentController(
            ILLMService llmService,
            ISettingsService settingsService,
            AgentPane agentPane,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools,
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
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _initializePickerWindow = initializePickerWindow;
            _isGitRepoProvider = isGitRepoProvider;
            _openDiffViewAsync = openDiffViewAsync;
            _fileModifiedAsync = fileModifiedAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _fileTools.ConfirmFileEditAsync = ConfirmFileEditAsync;
            _fileTools.ConfirmPowerShellAsync = ConfirmPowerShellAsync;
            if (_fileModifiedAsync != null)
            {
                _fileTools.FileModifiedAsync = _fileModifiedAsync;
            }

            WireEvents();
            UpdateContextStats();
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

        private void WireEvents()
        {
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync();
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.NewSessionRequested += (_, _) => ClearSession();
            _agentPane.InsertOutputRequested += async (_, _) => await InsertOutputAsync();
            _agentPane.AddAttachmentRequested += async (_, _) => await AddAttachmentsAsync();
            _agentPane.RemoveAttachmentRequested += (_, attachment) => RemoveAttachment(attachment.Id);
            
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
            if (!_isGitRepoProvider(root))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentGitRequired", "Agent는 Git 저장소로 지정된 폴더 내에서만 실행할 수 있습니다."));
                return;
            }
 
            string instruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(instruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }
 
            _isRunning = true;
            var cancellationSource = new CancellationTokenSource();
            _runCancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            _agentPane.SetBusy(true);
            _agentPane.ClearActivity(_getString("AgentActivityStarting", "시작 중"));
            _agentPane.BeginOutputBlock(BuildRunHeader(instruction));
            AppendActivity(_getString("AgentActivityCollectingContext", "맥락 수집 중"));

            try
            {
                string workspaceContext = BuildWorkspaceContext();
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

                for (int step = 0; step < 8; step++)
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
                                            string label = FormatPreparingToolLabel(tokenCount);
                                            _agentPane.DispatcherQueue.TryEnqueue(() =>
                                            {
                                                _agentPane.BeginThinkingActivity(label);
                                                UpdateGeneratedTokenCount(tokenCount);
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
                                    string label = FormatPreparingToolLabel(tokenCount);
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _agentPane.UpdateThinkingActivity(label);
                                        UpdateGeneratedTokenCount(tokenCount);
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
                                    string label = FormatPreparingToolLabel(tokenCount);
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _agentPane.UpdateThinkingActivity(label);
                                        UpdateGeneratedTokenCount(tokenCount);
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
                                        string label = FormatPreparingToolLabel(tokenCount);
                                        _agentPane.DispatcherQueue.TryEnqueue(() =>
                                        {
                                            _agentPane.BeginThinkingActivity(label);
                                            UpdateGeneratedTokenCount(tokenCount);
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

                        break;
                    }

                    string toolResult = await ExecuteToolAsync(toolName, arguments, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    transcript = $"{transcript}\n\n[Agent tool call]\n{response}\n\n[Tool result: {toolName}]\n{toolResult}";
                    
                    string displayResult = toolResult;
                    if (!_settingsService.CurrentSettings.LlmAgentVerbose && !toolResult.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase))
                    {
                        string normalizedName = NormalizeToolName(toolName);
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
                        _agentPane.AppendOutputLine($"{_getString("AgentToolRunning", "도구 실행 중")}: {toolName}");
                        _agentPane.AppendOutputText(displayResult.TrimEnd() + Environment.NewLine);
                    });

                    // Selection-edit verification: after a file-edit tool succeeds on the
                    // selection's file, read the affected lines back to verify the change.
                    // If the content differs from the original selection, stop the loop to
                    // prevent the agent from modifying unrelated parts of the file.
                    string verifyToolName = NormalizeToolName(toolName);
                    bool isFileEditTool = verifyToolName is "replace_in_file" or "replace_range"
                        or "apply_patch" or "overwrite_file";
                    if (isFileEditTool
                        && !toolResult.Contains("failed") && !toolResult.Contains("cancelled")
                        && _lastSelectionStartLine > 0 && _lastSelectionEndLine > 0
                        && !string.IsNullOrEmpty(_lastSelectionSourcePath))
                    {
                        try
                        {
                            string editedPath = GetPathArgument(arguments);
                            string resolvedEdited = Path.IsPathRooted(editedPath)
                                ? editedPath
                                : Path.Combine(_fileTools.WorkspaceRoot, editedPath);
                            string resolvedSelection = Path.IsPathRooted(_lastSelectionSourcePath)
                                ? _lastSelectionSourcePath
                                : Path.Combine(_fileTools.WorkspaceRoot, _lastSelectionSourcePath);

                            if (string.Equals(
                                Path.GetFullPath(resolvedEdited),
                                Path.GetFullPath(resolvedSelection),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                int verifyLineCount = _lastSelectionEndLine - _lastSelectionStartLine + 1;
                                string verifyContent = await _fileTools.ReadFileAsync(
                                    editedPath, _lastSelectionStartLine, verifyLineCount);

                                if (!string.IsNullOrEmpty(verifyContent)
                                    && !string.Equals(
                                        verifyContent.Trim(),
                                        _lastSelectionText.Trim(),
                                        StringComparison.Ordinal))
                                {
                                    string verifyMsg = _getString(
                                        "AgentSelectionEditVerified",
                                        "선택 영역 수정이 확인되었습니다. 작업을 완료합니다.");

                                    transcript += $"\n\n[Selection edit verification: lines {_lastSelectionStartLine}-{_lastSelectionEndLine} changed successfully. Task complete.]";

                                    await RunOnUIThreadAsync(() =>
                                    {
                                        AppendActivity(verifyMsg);
                                        _agentPane.AppendOutputLine(verifyMsg);
                                    });

                                    _sessionHistory.AppendLine($"[User Prompt]: {instruction}");
                                    string verifyRunTranscript = transcript.Substring(initialTranscript.Length);
                                    if (!string.IsNullOrWhiteSpace(verifyRunTranscript))
                                    {
                                        _sessionHistory.AppendLine(verifyRunTranscript.Trim());
                                    }
                                    _sessionHistory.AppendLine();

                                    break;
                                }
                            }
                        }
                        catch { /* verification is best-effort; continue the loop on failure */ }
                    }
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
            _attachments.Clear();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.ResetOutput(_getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요."));
                _agentPane.ClearActivity(_getString("AgentActivityIdle", "대기 중"));
                _agentPane.UpdateModifiedFiles(new List<AgentFileEditPreview>());
                _agentPane.UpdateAttachments(new List<AgentAttachmentItem>());
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

        private string BuildWorkspaceContext()
        {
            var context = new List<string>();
            context.Add("[Workspace root]");
            context.Add(_fileTools.WorkspaceRoot);
            context.Add("");

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
            if (activeTab != null && _agentPane.IncludeActiveFile)
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

            if (_attachments.Count > 0)
            {
                context.Add("");
                context.Add("[Agent attachments]");
                foreach (var attachment in _attachments)
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

        private IReadOnlyList<LlmMessageAttachment> GetImageAttachmentsForCurrentRun()
        {
            return _attachments
                .Select(a => a.ImageContent)
                .Where(a => a != null)
                .Cast<LlmMessageAttachment>()
                .ToList();
        }

        private async Task AddAttachmentsAsync()
        {
            if (_isRunning)
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);

            foreach (string extension in GetAttachmentPickerExtensions())
            {
                picker.FileTypeFilter.Add(extension);
            }

            try
            {
                _beforeDialog?.Invoke();
                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var attachment = await CreateAttachmentAsync(file);
                        if (attachment != null)
                        {
                            _attachments.RemoveAll(a => string.Equals(a.Path, attachment.Path, StringComparison.OrdinalIgnoreCase));
                            _attachments.Add(attachment);
                        }
                    }
                    catch (Exception ex)
                    {
                        _showError(
                            _getString("AgentAttachmentErrorTitle", "첨부 추가 오류"),
                            string.Format(_getString("AgentAttachmentErrorFormat", "첨부를 추가하는 중 오류가 발생했습니다: {0}"), ex.Message));
                    }
                }

                RefreshAttachments();
                UpdateContextStats();
            }
            finally
            {
                _afterDialog?.Invoke();
            }
        }

        private static IReadOnlyList<string> GetAttachmentPickerExtensions()
        {
            return new[] { "*" };
        }

        private async Task<AgentAttachmentState?> CreateAttachmentAsync(StorageFile file)
        {
            string displayName = file.Name;
            string path = file.Path ?? displayName;
            string mimeType = GetMimeType(file);

            if (IsImageFile(file, mimeType))
            {
                var image = await CreateImageAttachmentAsync(file, displayName, mimeType);
                return new AgentAttachmentState
                {
                    Path = path,
                    DisplayName = displayName,
                    Detail = $"{image.MimeType}, {image.Width}x{image.Height}",
                    ImageContent = image,
                    EstimatedTokens = image.EstimatedTokens
                };
            }

            string text = await ReadAttachmentTextAsync(file);
            int estimatedTokens = (int)Math.Round(EstimateTokenCount(text));
            bool truncated = text.Length >= MaxAttachmentTextChars;
            return new AgentAttachmentState
            {
                Path = path,
                DisplayName = displayName,
                Detail = truncated
                    ? string.Format(_getString("AgentAttachmentFileTruncatedDetail", "파일, {0:N0}자까지 포함"), text.Length)
                    : string.Format(_getString("AgentAttachmentFileDetail", "파일, {0:N0}자"), text.Length),
                TextContent = text,
                EstimatedTokens = estimatedTokens
            };
        }

        private async Task<LlmMessageAttachment> CreateImageAttachmentAsync(StorageFile file, string displayName, string mimeType)
        {
            using IRandomAccessStream input = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            uint originalWidth = decoder.PixelWidth;
            uint originalHeight = decoder.PixelHeight;
            uint outputWidth = originalWidth;
            uint outputHeight = originalHeight;
            string outputMimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
            byte[] bytes;

            if (Math.Max(originalWidth, originalHeight) > MaxImageDimension)
            {
                double scale = MaxImageDimension / (double)Math.Max(originalWidth, originalHeight);
                outputWidth = Math.Max(1, (uint)Math.Round(originalWidth * scale));
                outputHeight = Math.Max(1, (uint)Math.Round(originalHeight * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = outputWidth,
                    ScaledHeight = outputHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                using var output = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    outputWidth,
                    outputHeight,
                    96,
                    96,
                    pixelData.DetachPixelData());
                await encoder.FlushAsync();
                output.Seek(0);
                bytes = await ReadRandomAccessStreamAsync(output);
                outputMimeType = "image/jpeg";
            }
            else
            {
                input.Seek(0);
                bytes = await ReadRandomAccessStreamAsync(input);
            }

            int estimatedTokens = EstimateImageTokens((int)outputWidth, (int)outputHeight);
            return new LlmMessageAttachment
            {
                DisplayName = displayName,
                MimeType = outputMimeType,
                Base64Data = Convert.ToBase64String(bytes),
                Width = (int)outputWidth,
                Height = (int)outputHeight,
                EstimatedTokens = estimatedTokens
            };
        }

        private static async Task<byte[]> ReadRandomAccessStreamAsync(IRandomAccessStream stream)
        {
            using var managedStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await managedStream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static async Task<string> ReadAttachmentTextAsync(StorageFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                char[] buffer = new char[MaxAttachmentTextChars + 1];
                int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                string text = new string(buffer, 0, Math.Min(read, MaxAttachmentTextChars));
                return StripBinaryControlCharacters(text);
            }

            string fallback = await FileIO.ReadTextAsync(file);
            if (fallback.Length > MaxAttachmentTextChars)
            {
                fallback = fallback.Substring(0, MaxAttachmentTextChars);
            }
            return StripBinaryControlCharacters(fallback);
        }

        private void RemoveAttachment(string id)
        {
            if (_isRunning)
            {
                return;
            }

            _attachments.RemoveAll(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            RefreshAttachments();
            UpdateContextStats();
        }

        private void RefreshAttachments()
        {
            var items = _attachments
                .Select(a => new AgentAttachmentItem
                {
                    Id = a.Id,
                    DisplayName = a.DisplayName,
                    Detail = a.Detail,
                    TokenText = FormatAttachmentTokens(a.EstimatedTokens),
                    IconGlyph = a.IsImage ? "\uEB9F" : "\uE8A5"
                })
                .ToList();
            _agentPane.UpdateAttachments(items);
        }

        private string FormatAttachmentTokens(int tokenCount)
        {
            string lang = GetActiveLanguageCode();
            if (lang == "ko-KR")
            {
                return $"{tokenCount:N0} 토큰";
            }
            if (lang == "ja-JP")
            {
                return $"{tokenCount:N0} トークン";
            }
            return $"{tokenCount:N0} tokens";
        }

        private static int EstimateImageTokens(int width, int height)
        {
            int tilesWide = Math.Max(1, (int)Math.Ceiling(width / 512.0));
            int tilesHigh = Math.Max(1, (int)Math.Ceiling(height / 512.0));
            return 85 + (tilesWide * tilesHigh * 170);
        }

        private static string StripBinaryControlCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t' || ch >= ' ')
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString();
        }

        private static bool IsImageFile(StorageFile file, string mimeType)
        {
            if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string extension = Path.GetExtension(file.Name);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMimeType(StorageFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.ContentType) &&
                !file.ContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                return file.ContentType;
            }

            string extension = Path.GetExtension(file.Name).ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "text/plain"
            };
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
                        string editedPath = GetPathArgument(arguments);
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
                else if (normalizedToolName == "insert_text")
                {
                    isEditingActiveFile = true;
                }

                string result;
                if (normalizedToolName == "replace_in_file")
                {
                    string path = GetPathArgument(arguments);
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
                        "create_file" => await _fileTools.CreateFileAsync(
                            GetPathArgument(arguments),
                            GetStringArgument(arguments, "content")),
                        "overwrite_file" => await _fileTools.OverwriteFileAsync(
                            GetPathArgument(arguments),
                            GetFirstStringArgument(arguments, "content", "newText", "new_text", "text")),
                        "replace_range" => await _fileTools.ReplaceRangeAsync(
                            GetPathArgument(arguments),
                            GetIntArgument(arguments, "startLine", 1),
                            GetIntArgument(arguments, "endLine", 1),
                            GetFirstStringArgument(arguments, "newText", "new_text", "content", "text"),
                            GetFirstStringArgument(arguments, "expectedSnippet", "expected_snippet", "guard", "expected")),
                        "apply_patch" => await _fileTools.ApplyPatchAsync(
                            GetPathArgument(arguments),
                            GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content")),
                        "insert_text" => await InsertTextToolAsync(
                            GetFirstStringArgument(arguments, "content", "text", "newText", "new_text")),
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
                    GetStringArgument(arguments, "path")),
                "replace_range" => string.Format(
                    _getString("AgentActivityReplaceRangeFormat", "파일 범위 수정 중: {0} ({1}줄부터 {2}줄)"),
                    GetStringArgument(arguments, "path"),
                    GetIntArgument(arguments, "startLine", 1),
                    GetIntArgument(arguments, "endLine", 1)),
                "apply_patch" => string.Format(
                    _getString("AgentActivityApplyPatchFormat", "파일 패치 적용 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "overwrite_file" => string.Format(
                    _getString("AgentActivityOverwriteFileFormat", "파일 덮어쓰는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "insert_text" => _getString("AgentActivityInsertText", "현재 편집기에 입력 중"),
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

        private static string TruncateForActivity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            return value.Length > 120 ? value.Substring(0, 120) + "..." : value;
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
            var stringMatches = Regex.Matches(
                argumentsText,
                "\"(?<key>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"\\s*:\\s*\"(?<value>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"",
                RegexOptions.Singleline);

            foreach (Match match in stringMatches)
            {
                string key = DecodeLenientJsonString(match.Groups["key"].Value);
                string value = DecodeLenientJsonString(match.Groups["value"].Value);
                argumentValues[key] = value;
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

            return payload.StartsWith("{", StringComparison.Ordinal);
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

        private string GetPathArgument(JsonElement arguments)
        {
            string path = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return _activeTabProvider()?.FilePath ?? string.Empty;
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
                string summaryText = string.Format(_getString("AgentPowerShellConfirmSummaryFormat", "아래 명령을 실행하시겠습니까?\n\n{0}"), command);

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
            string output = _agentPane.Output.SelectedText;
            if (string.IsNullOrEmpty(output))
            {
                output = _agentPane.Output.Text;
            }

            if (string.IsNullOrWhiteSpace(output) ||
                output.StartsWith("대기 중", StringComparison.Ordinal) ||
                output.StartsWith("Waiting...", StringComparison.Ordinal) ||
                output.StartsWith("待機中...", StringComparison.Ordinal))
            {
                _showError(
                    _getString("AgentInsertTitle", "Agent 응답 입력"),
                    _getString("AgentNoOutputToInsert", "입력할 Agent 응답이 없습니다."));
                return;
            }

            await _insertIntoActiveEditorAsync(output);
        }

        private void UpdateContextStats()
        {
            if (_isRunning)
            {
                return;
            }

            var activeTab = _activeTabProvider();
            string tabPart = activeTab == null
                ? _getString("AgentNoActiveTab", "활성 탭 없음")
                : Path.GetFileName(string.IsNullOrWhiteSpace(activeTab.FilePath) ? activeTab.Title : activeTab.FilePath);

            string activeSelectionText = GetActiveSelectionText();
            string selectionPart = string.IsNullOrEmpty(activeSelectionText)
                ? _getString("AgentNoSelection", "선택 없음")
                : string.Format(_getString("AgentSelectionStats", "선택 {0:N0}자"), activeSelectionText.Length);

            if (_attachments.Count > 0)
            {
                selectionPart = $"{selectionPart} · {FormatAttachmentCount(_attachments.Count)}";
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

        private string GetActiveLanguageCode()
        {
            string mode = _getString("AgentModeRun", "Run");
            if (mode == "실행") return "ko-KR";
            if (mode == "実行") return "ja-JP";
            return "en-US";
        }

        private double EstimateContextTokens()
        {
            string langCode = GetActiveLanguageCode();
            string systemPrompt = TxtAIEditor.Core.Services.LLM.AgentPromptBuilder.BuildSystemPrompt(langCode);

            string instruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            string workspaceContext = BuildWorkspaceContext();
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
                   _attachments.Where(a => a.IsImage).Sum(a => a.EstimatedTokens);
        }

        private string FormatAttachmentCount(int count)
        {
            string lang = GetActiveLanguageCode();
            if (lang == "ko-KR")
            {
                return $"첨부 {count:N0}개";
            }
            if (lang == "ja-JP")
            {
                return $"添付 {count:N0}件";
            }
            return $"{count:N0} attachment{(count == 1 ? string.Empty : "s")}";
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

        private string FormatPreparingToolLabel(int tokenCount)
        {
            string baseLabel = _getString("AgentOutputPreparingTool", "도구 호출 준비 중");
            string lang = GetActiveLanguageCode();
            if (lang == "ko-KR")
            {
                return $"{baseLabel} ({tokenCount} 토큰)";
            }
            else if (lang == "ja-JP")
            {
                return $"{baseLabel} ({tokenCount} トークン)";
            }
            else
            {
                return $"{baseLabel} ({tokenCount} tokens)";
            }
        }

        private void UpdateGeneratedTokenCount(int tokenCount)
        {
            string lang = GetActiveLanguageCode();
            if (lang == "ko-KR")
            {
                _agentPane.TokenCount.Text = $"{tokenCount} 토큰";
            }
            else if (lang == "ja-JP")
            {
                _agentPane.TokenCount.Text = $"{tokenCount} トークン";
            }
            else
            {
                _agentPane.TokenCount.Text = $"{tokenCount} tokens";
            }
        }
    }
}
