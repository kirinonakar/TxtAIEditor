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

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int MaxActiveFileContextChars = 120_000;

        private readonly ILLMService _llmService;
        private readonly AgentPane _agentPane;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Func<OpenedTab, int, string> _getTabText;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;

        private string _lastSelectionText = string.Empty;
        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;
        private readonly StringBuilder _sessionHistory = new();
        private TaskCompletionSource<bool>? _diffApprovalTcs;

        public AgentController(
            ILLMService llmService,
            AgentPane agentPane,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools,
            Func<string, bool> isGitRepoProvider,
            Func<string, Task>? fileModifiedAsync = null)
        {
            _llmService = llmService;
            _agentPane = agentPane;
            _activeTabProvider = activeTabProvider;
            _openTabsProvider = openTabsProvider;
            _getTabText = getTabText;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _isGitRepoProvider = isGitRepoProvider;
            _fileModifiedAsync = fileModifiedAsync;
            _fileTools.ConfirmFileEditAsync = ConfirmFileEditAsync;
            _fileTools.ConfirmPowerShellAsync = ConfirmPowerShellAsync;
            if (_fileModifiedAsync != null)
            {
                _fileTools.FileModifiedAsync = _fileModifiedAsync;
            }

            WireEvents();
            UpdateContextStats();
        }

        public void SetSelectionText(string selectedText)
        {
            _lastSelectionText = selectedText ?? string.Empty;
            UpdateContextStats();
        }

        public void ClearSelection()
        {
            _lastSelectionText = string.Empty;
            UpdateContextStats();
        }

        private void WireEvents()
        {
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync();
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.NewSessionRequested += (_, _) => ClearSession();
            _agentPane.InsertOutputRequested += async (_, _) => await InsertOutputAsync();
            
            _agentPane.Prompt.TextChanged += (_, _) => UpdateContextStats();
            _agentPane.IncludeActiveFileCheckBox.Checked += (_, _) => UpdateContextStats();
            _agentPane.IncludeActiveFileCheckBox.Unchecked += (_, _) => UpdateContextStats();

            _agentPane.DiffApproved += (_, _) => _diffApprovalTcs?.TrySetResult(true);
            _agentPane.DiffCancelled += (_, _) => _diffApprovalTcs?.TrySetResult(false);
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
                string selectedText = _lastSelectionText;

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
                        _lastSelectionText,
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
                                        _agentPane.DispatcherQueue.TryEnqueue(() =>
                                            _agentPane.BeginThinkingActivity(_getString("AgentOutputPreparingTool", "도구 호출 준비 중")));
                                    }
                                }
                            }

                            if (isJsonToolCall == true)
                            {
                                return;
                            }

                            if (hasToolCall)
                            {
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

                                if (!toolCallPlaceholderShown)
                                {
                                    toolCallPlaceholderShown = true;
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                        _agentPane.BeginThinkingActivity(_getString("AgentOutputPreparingTool", "도구 호출 준비 중")));
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
                        cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

                    await RunOnUIThreadAsync(() =>
                    {
                        _agentPane.StopThinkingActivity();
                        if (printedLength < response.Length)
                        {
                            int toolCallIndex = response.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                            int endLength = toolCallIndex >= 0 ? toolCallIndex : response.Length;
                            if (printedLength < endLength)
                            {
                                string remainingText = response.Substring(printedLength, endLength - printedLength);
                                visibleTextFlushed = true;
                                _agentPane.AppendOutputText(remainingText);
                            }
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
                    
                    await RunOnUIThreadAsync(() =>
                    {
                        _agentPane.AppendOutputLine($"{_getString("AgentToolRunning", "도구 실행 중")}: {toolName}");
                        _agentPane.AppendOutputText(toolResult.TrimEnd() + Environment.NewLine);
                    });
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
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.ResetOutput(_getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요."));
                _agentPane.ClearActivity(_getString("AgentActivityIdle", "대기 중"));
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
            if (activeTab == null || !_agentPane.IncludeActiveFile)
            {
                return string.Join(Environment.NewLine, context);
            }

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

            return string.Join(Environment.NewLine, context);
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

            if (!TryExtractToolCallJson(response, out string json))
            {
                return TryExtractToolCallPayload(response, out string payload) &&
                       TryParseToolCallLenient(payload, out toolName, out arguments);
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement.Clone();
                if (!root.TryGetProperty("name", out var nameProp))
                {
                    return false;
                }

                toolName = nameProp.GetString() ?? string.Empty;
                arguments = root.TryGetProperty("arguments", out var argsProp)
                    ? argsProp.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();

                return !string.IsNullOrWhiteSpace(toolName);
            }
            catch
            {
                return TryParseToolCallLenient(json, out toolName, out arguments);
            }
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

                return approved;
            });
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

            string selectionPart = string.IsNullOrEmpty(_lastSelectionText)
                ? _getString("AgentNoSelection", "선택 없음")
                : string.Format(_getString("AgentSelectionStats", "선택 {0:N0}자"), _lastSelectionText.Length);

            _agentPane.ContextStats.Text = string.Format(
                _getString("AgentContextStatsFormat", "맥락: {0} · {1}"),
                tabPart,
                selectionPart);

            double estimatedTokens = EstimateContextTokens();
            double kTokens = estimatedTokens / 1000.0;
            _agentPane.TokenCount.Text = string.Format(
                _getString("AgentTokenCountFormat", "{0:F1}k tokens"),
                kTokens);
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
            string selectedText = _lastSelectionText;

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

            return EstimateTokenCount(systemPrompt) + EstimateTokenCount(userContent);
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
