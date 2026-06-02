using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
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

        private string _lastSelectionText = string.Empty;
        private bool _isRunning;

        public AgentController(
            ILLMService llmService,
            AgentPane agentPane,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools)
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
            _agentPane.SetBusy(true);
            _agentPane.Output.Text = string.Empty;

            try
            {
                string workspaceContext = BuildWorkspaceContext();
                string selectedText = _lastSelectionText;
                string transcript = workspaceContext;
                string response = string.Empty;

                for (int step = 0; step < 8; step++)
                {
                    response = await _llmService.RunAgentAsync(instruction, transcript, selectedText, mode);
                    _agentPane.Output.Text = response;

                    if (!TryParseToolCall(response, out string toolName, out JsonElement arguments))
                    {
                        break;
                    }

                    string toolResult = await ExecuteToolAsync(toolName, arguments);
                    transcript = $"{transcript}\n\n[Agent tool call]\n{response}\n\n[Tool result: {toolName}]\n{toolResult}";
                    _agentPane.Output.Text = $"{_getString("AgentToolRunning", "도구 실행 중")}: {toolName}\n\n{toolResult}";
                }
            }
            catch (Exception ex)
            {
                _agentPane.Output.Text = string.Format(
                    _getString("AgentExceptionFormat", "Agent 실행 도중 예외가 발생했습니다: {0}"),
                    ex.Message);
            }
            finally
            {
                _isRunning = false;
                _agentPane.SetBusy(false);
                UpdateContextStats();
            }
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

        private async Task<string> ExecuteToolAsync(string toolName, JsonElement arguments)
        {
            try
            {
                return toolName switch
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
                        GetIntArgument(arguments, "timeoutMs", 10000)),
                    "run_powershell" => await _fileTools.RunPowerShellAsync(
                        GetStringArgument(arguments, "command"),
                        GetIntArgument(arguments, "timeoutMs", 10000)),
                    "read_file" => await _fileTools.ReadFileAsync(
                        GetStringArgument(arguments, "path"),
                        GetIntArgument(arguments, "startLine", 1),
                        GetIntArgument(arguments, "lineCount", 160)),
                    "create_file" => await _fileTools.CreateFileAsync(
                        GetStringArgument(arguments, "path"),
                        GetStringArgument(arguments, "content")),
                    "replace_in_file" => await _fileTools.ReplaceInFileAsync(
                        GetStringArgument(arguments, "path"),
                        GetStringArgument(arguments, "oldText"),
                        GetStringArgument(arguments, "newText")),
                    "overwrite_file" => await _fileTools.OverwriteFileAsync(
                        GetStringArgument(arguments, "path"),
                        GetStringArgument(arguments, "content")),
                    _ => $"Unknown tool: {toolName}"
                };
            }
            catch (Exception ex)
            {
                return $"Tool failed: {ex.Message}";
            }
        }

        private static bool TryParseToolCall(string response, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            var match = Regex.Match(response ?? string.Empty, @"<tool_call>\s*(?<json>\{[\s\S]*\})\s*</tool_call>", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
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
                return false;
            }
        }

        private static string GetStringArgument(JsonElement arguments, string name)
        {
            return arguments.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
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
