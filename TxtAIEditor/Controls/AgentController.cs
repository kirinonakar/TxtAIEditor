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
        private readonly Func<string, Task>? _fileModifiedAsync;

        private string _lastSelectionText = string.Empty;
        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;

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
            _fileModifiedAsync = fileModifiedAsync;
            _fileTools.ConfirmFileEditAsync = ConfirmFileEditAsync;
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
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync("run");
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.PlanRequested += async (_, _) => await RunAgentAsync("plan");
            _agentPane.EditRequested += async (_, _) => await RunAgentAsync("edit");
            _agentPane.InsertOutputRequested += async (_, _) => await InsertOutputAsync();
        }

        private async Task RunAgentAsync(string mode)
        {
            if (_isRunning)
            {
                return;
            }

            string instruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(instruction))
            {
                instruction = mode switch
                {
                    "plan" => _getString("AgentDefaultPlanInstruction", "현재 맥락을 분석하고 실행 계획을 세워줘."),
                    "edit" => _getString("AgentDefaultEditInstruction", "현재 맥락을 바탕으로 적용 가능한 수정안을 만들어줘."),
                    _ => string.Empty
                };
            }

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
            _agentPane.BeginOutputBlock(BuildRunHeader(mode, instruction));
            AppendActivity(_getString("AgentActivityCollectingContext", "맥락 수집 중"));

            try
            {
                string workspaceContext = BuildWorkspaceContext();
                string selectedText = _lastSelectionText;
                string transcript = workspaceContext;
                string response = string.Empty;

                for (int step = 0; step < 8; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _agentPane.BeginThinkingActivity(_getString("AgentActivityThinking", "생각중"));

                    var responseBuilder = new StringBuilder();
                    bool toolCallPlaceholderShown = false;
                    bool visibleTextFlushed = false;
                    response = await _llmService.RunAgentAsync(
                        instruction,
                        transcript,
                        selectedText,
                        mode,
                        async chunk =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            responseBuilder.Append(chunk);
                            string streamedText = responseBuilder.ToString();
                            if (IsToolCallLike(streamedText))
                            {
                                if (!toolCallPlaceholderShown)
                                {
                                    toolCallPlaceholderShown = true;
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                        _agentPane.AppendOutputLine(_getString("AgentOutputPreparingTool", "도구 호출 준비 중...")));
                                }

                                return;
                            }

                            if (!visibleTextFlushed && MightBeToolCallPrefix(streamedText))
                            {
                                if (!toolCallPlaceholderShown)
                                {
                                    toolCallPlaceholderShown = true;
                                    _agentPane.DispatcherQueue.TryEnqueue(() =>
                                        _agentPane.AppendOutputLine(_getString("AgentOutputPreparingTool", "도구 호출 준비 중...")));
                                }

                                return;
                            }

                            string textToAppend = visibleTextFlushed ? chunk : streamedText;
                            visibleTextFlushed = true;
                            _agentPane.DispatcherQueue.TryEnqueue(() => _agentPane.AppendOutputText(textToAppend));
                            await Task.CompletedTask;
                        },
                        cancellationToken);
                    _agentPane.StopThinkingActivity();
                    cancellationToken.ThrowIfCancellationRequested();
                    response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

                    if (!TryParseToolCall(response, out string toolName, out JsonElement arguments))
                    {
                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            _agentPane.AppendOutputLine(_getString("AgentToolCallParseFailed", "도구 호출을 해석하지 못해 원문을 표시합니다."));
                            _agentPane.AppendOutputText(response);
                        }

                        AppendActivity(_getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        break;
                    }

                    string toolResult = await ExecuteToolAsync(toolName, arguments, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    transcript = $"{transcript}\n\n[Agent tool call]\n{response}\n\n[Tool result: {toolName}]\n{toolResult}";
                    _agentPane.AppendOutputLine($"{_getString("AgentToolRunning", "도구 실행 중")}: {toolName}");
                    _agentPane.AppendOutputText(toolResult.TrimEnd() + Environment.NewLine);
                }
            }
            catch (OperationCanceledException)
            {
                AppendActivity(_getString("AgentActivityStopped", "중단됨"));
                _agentPane.AppendOutputLine(_getString("AgentOutputStopped", "Agent 실행이 중단되었습니다."));
            }
            catch (Exception ex)
            {
                _agentPane.AppendOutputLine(string.Format(
                    _getString("AgentExceptionFormat", "Agent 실행 도중 예외가 발생했습니다: {0}"),
                    ex.Message));
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
        }

        private string BuildRunHeader(string mode, string instruction)
        {
            string modeText = mode switch
            {
                "plan" => _getString("AgentModePlan", "계획"),
                "edit" => _getString("AgentModeEdit", "수정안"),
                _ => _getString("AgentModeRun", "실행")
            };

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            return $"===== {timestamp}  Agent {modeText}: {TruncateForActivity(instruction)} =====";
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
                        "insert_text" => await InsertTextToolAsync(
                            GetFirstStringArgument(arguments, "content", "text", "newText", "new_text")),
                        _ => $"Unknown tool: {toolName}"
                    };
                }
                cancellationToken.ThrowIfCancellationRequested();
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

        private void AppendActivity(string message)
        {
            _agentPane.AppendActivity(message);
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
                "overwrite_file" => string.Format(
                    _getString("AgentActivityOverwriteFileFormat", "파일 덮어쓰는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "insert_text" => _getString("AgentActivityInsertText", "현재 편집기에 입력 중"),
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

            bool inserted = await _insertIntoActiveEditorAsync(content);
            return inserted
                ? $"inserted into active editor: {content.Length:N0} chars"
                : "insert_text failed: active editor did not accept the text.";
        }

        private async Task<bool> ConfirmFileEditAsync(AgentFileEditPreview preview)
        {
            AppendActivity(string.Format(
                _getString("AgentActivityDiffReviewFormat", "diff 확인 대기 중: {0}"),
                preview.RelativePath));

            var diffBox = new TextBox
            {
                Text = BuildDiffPreview(preview),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                IsReadOnly = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12,
                MinWidth = 720,
                MaxWidth = 920,
                MaxHeight = 520,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetVerticalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);

            var dialog = new ContentDialog
            {
                Title = string.Format(
                    _getString("AgentDiffDialogTitleFormat", "Agent 파일 수정 확인: {0}"),
                    preview.RelativePath),
                Content = diffBox,
                PrimaryButtonText = _getString("AgentDiffApplyButton", "적용"),
                CloseButtonText = _getString("AgentDiffCancelButton", "취소"),
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await dialog.ShowAsync();
            bool approved = result == ContentDialogResult.Primary;
            AppendActivity(approved
                ? string.Format(_getString("AgentActivityDiffAppliedFormat", "diff 적용 승인: {0}"), preview.RelativePath)
                : string.Format(_getString("AgentActivityDiffCancelledFormat", "diff 적용 취소: {0}"), preview.RelativePath));
            return approved;
        }

        private static string BuildDiffPreview(AgentFileEditPreview preview)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"--- {preview.RelativePath}");
            builder.AppendLine($"+++ {preview.RelativePath}");

            string[] oldLines = preview.OldContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string[] newLines = preview.NewContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            if (preview.IsNewFile)
            {
                foreach (string line in newLines)
                {
                    builder.Append('+');
                    builder.AppendLine(line);
                }

                return TruncateDiff(builder.ToString());
            }

            int max = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < max; i++)
            {
                string? oldLine = i < oldLines.Length ? oldLines[i] : null;
                string? newLine = i < newLines.Length ? newLines[i] : null;

                if (oldLine == newLine)
                {
                    builder.Append(' ');
                    builder.AppendLine(oldLine ?? string.Empty);
                    continue;
                }

                if (oldLine != null)
                {
                    builder.Append('-');
                    builder.AppendLine(oldLine);
                }

                if (newLine != null)
                {
                    builder.Append('+');
                    builder.AppendLine(newLine);
                }
            }

            return TruncateDiff(builder.ToString());
        }

        private static string TruncateDiff(string diff)
        {
            const int maxChars = 60_000;
            return diff.Length <= maxChars
                ? diff
                : diff.Substring(0, maxChars) + Environment.NewLine + "[diff truncated]";
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
        }
    }
}
