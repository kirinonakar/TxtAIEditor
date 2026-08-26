using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentHistoryItem
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Title { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
        public string ModelSessionHistoryText { get; set; } = string.Empty;
        public string LastAnswerText { get; set; } = string.Empty;
        public double SessionHistoryTokenCount { get; set; }
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public string WorkspaceRoot { get; set; } = string.Empty;
    }

    internal sealed class AgentHistoryController
    {
        private const int MaxHistoryItems = 20;

        private readonly AgentPane _agentPane;
        private readonly string _historyFilePath;
        private readonly List<AgentHistoryItem> _history = new();

        public AgentHistoryController(AgentPane agentPane)
        {
            _agentPane = agentPane;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _historyFilePath = Path.Combine(settingsDir, "agent-history.json");
        }

        public async Task LoadAsync(string currentSessionId)
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    string json = await File.ReadAllTextAsync(_historyFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentHistoryItem>>(json);
                    if (loaded != null)
                    {
                        _history.Clear();
                        _history.AddRange(loaded.Where(h => !string.IsNullOrWhiteSpace(h.Id)));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load agent history: {ex.Message}");
            }

            UpdateUI(currentSessionId);
        }

        public AgentHistoryItem? GetSession(string historyId)
        {
            return _history.FirstOrDefault(h => h.Id == historyId);
        }

        public async Task SaveSessionAsync(AgentHistoryItem session, string currentSessionId)
        {
            session.SessionHistoryText = AgentRunTranscriptService.ConvertToolCallTagsToLogTags(session.SessionHistoryText);
            session.SessionHistoryText = AgentRunTranscriptService.ConvertUserRequestMarkersForHistory(session.SessionHistoryText);
            var existing = _history.FirstOrDefault(h => h.Id == session.Id);
            if (existing != null)
            {
                existing.Timestamp = session.Timestamp;
                existing.Title = session.Title;
                existing.SessionHistoryText = session.SessionHistoryText;
                existing.ModelSessionHistoryText = session.ModelSessionHistoryText;
                existing.LastAnswerText = session.LastAnswerText;
                existing.SessionHistoryTokenCount = session.SessionHistoryTokenCount;
                existing.SessionEdits = session.SessionEdits.ToList();
                existing.WorkspaceRoot = session.WorkspaceRoot;

                _history.Remove(existing);
                _history.Insert(0, existing);
            }
            else
            {
                _history.Insert(0, session);
            }

            while (_history.Count > MaxHistoryItems)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            await SaveAsync(currentSessionId);
        }

        public async Task DeleteAsync(string historyId, string currentSessionId)
        {
            if (string.IsNullOrEmpty(historyId))
            {
                return;
            }

            var item = _history.FirstOrDefault(h => h.Id == historyId);
            if (item != null)
            {
                _history.Remove(item);
                await SaveAsync(currentSessionId);
                return;
            }

            UpdateUI(currentSessionId);
        }

        public async Task ClearAsync(string currentSessionId)
        {
            _history.Clear();
            await SaveAsync(currentSessionId);
        }

        public void UpdateUI(string currentSessionId)
        {
            var viewModels = _history
                .OrderByDescending(h => h.Timestamp)
                .Select(h =>
                {
                    string prefix = string.Empty;
                    if (!string.IsNullOrWhiteSpace(h.WorkspaceRoot))
                    {
                        try
                        {
                            string trimmed = h.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string lastDir = Path.GetFileName(trimmed);
                            if (!string.IsNullOrEmpty(lastDir))
                            {
                                prefix = $"[{lastDir}] ";
                            }
                        }
                        catch { }
                    }
                    return new AgentHistoryItemViewModel
                    {
                        Id = h.Id,
                        Title = $"{prefix}{h.Title}",
                        TimeText = h.Timestamp.ToString("MM-dd HH:mm")
                    };
                })
                .ToList();

            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.UpdateHistoryItems(viewModels, currentSessionId);
            });
        }

        private async Task SaveAsync(string currentSessionId)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_historyFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_historyFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save agent history: {ex.Message}");
            }

            UpdateUI(currentSessionId);
        }

        public static string ExtractLastAgentResponse(string historyText)
        {
            if (string.IsNullOrEmpty(historyText))
            {
                return string.Empty;
            }

            var lines = historyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int responseLineIndex = -1;
            bool newResponseFormat = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase))
                {
                    responseLineIndex = i;
                    newResponseFormat = false;
                }
                else if (lines[i].StartsWith("[assistant: final answer]", StringComparison.OrdinalIgnoreCase))
                {
                    responseLineIndex = i;
                    newResponseFormat = true;
                }
            }

            if (responseLineIndex < 0)
            {
                return string.Empty;
            }

            var result = new StringBuilder();
            string firstLine = lines[responseLineIndex];
            if (!newResponseFormat)
            {
                result.AppendLine(firstLine.Substring("[Agent Response]:".Length).TrimStart());
            }
            for (int i = responseLineIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[assistant: tool call]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                result.AppendLine(line);
            }

            return result.ToString().Trim();
        }
    }

    internal static class AgentHistoryFormatter
    {
        public static string Format(
            string historyText,
            bool verbose,
            Func<string, string, string>? getString = null)
        {
            if (string.IsNullOrEmpty(historyText))
            {
                return string.Empty;
            }

            if (verbose)
            {
                return historyText;
            }

            var lines = historyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            lines = NormalizeStructuredUserTurns(lines, getString);
            var result = new StringBuilder();
            bool inToolCall = false;
            bool toolCallSyntaxReached = false;
            bool inToolResult = false;
            bool inRetryDetail = false;
            bool inLegacyRetryPayload = false;
            bool inUserPromptInstructionMetadata = false;
            bool suppressInstructionMetadataSection = false;
            bool afterUserRequest = false;
            bool inPlanningModeTaskDetails = false;
            bool inRetainedThinking = false;
            bool inGlobalAgentRules = false;
            bool inCompressedContext = false;
            string? pendingToolResultToolName = null;
            var pendingToolResultBody = new StringBuilder();
            bool agentRunHeaderWritten = false;

            void StartUserTurn()
            {
                agentRunHeaderWritten = false;
            }

            void EnsureAgentRunHeader()
            {
                if (agentRunHeaderWritten)
                {
                    return;
                }

                result.AppendLine();
                result.AppendLine("[Agent run]:");
                agentRunHeaderWritten = true;
            }

            bool IsPendingToolResultFailed()
            {
                // An empty body keeps the previous completed-line behavior.
                return pendingToolResultBody.Length > 0 &&
                    !AgentToolHelpers.IsSuccessfulToolResult(pendingToolResultBody.ToString());
            }

            void FlushPendingToolResult(bool failed)
            {
                string toolName = pendingToolResultToolName ?? string.Empty;
                string partialApplyPatchSummary = toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase)
                    ? AgentToolHelpers.ExtractPartialApplyPatchSummary(pendingToolResultBody.ToString())
                    : string.Empty;
                pendingToolResultToolName = null;
                pendingToolResultBody.Clear();
                if (string.IsNullOrEmpty(toolName))
                {
                    return;
                }

                string format = failed
                    ? getString?.Invoke("AgentHistoryToolFailedFormat", "[도구 실행 실패]: {0}") ?? "[도구 실행 실패]: {0}"
                    : getString?.Invoke("AgentHistoryToolCompletedFormat", "[도구 실행]: {0}") ?? "[도구 실행]: {0}";
                result.AppendLine(string.Format(format, toolName));
                if (!string.IsNullOrWhiteSpace(partialApplyPatchSummary))
                {
                    result.AppendLine(partialApplyPatchSummary);
                }
            }

            foreach (var line in lines)
            {
                if (inCompressedContext)
                {
                    result.AppendLine(line);
                    if (line.Trim().Equals(
                            AgentRunTranscriptService.CompressedContextEndMarker,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        inCompressedContext = false;
                    }

                    continue;
                }

                if (line.Trim().Equals(
                        AgentRunTranscriptService.CompressedContextStartMarker,
                        StringComparison.OrdinalIgnoreCase))
                {
                    EnsureAgentRunHeader();
                    result.AppendLine(line);
                    inCompressedContext = true;
                    continue;
                }

                if (inGlobalAgentRules)
                {
                    if (line.Trim().Equals("[End global agent rules]", StringComparison.OrdinalIgnoreCase))
                    {
                        inGlobalAgentRules = false;
                    }

                    continue;
                }

                if (line.Trim().Equals("[Global agent rules]", StringComparison.OrdinalIgnoreCase))
                {
                    inGlobalAgentRules = true;
                    continue;
                }

                if (pendingToolResultToolName != null && IsHistorySectionBoundaryLine(line))
                {
                    // The tool result section ended here. Decide success from the full
                    // accumulated body so that ripgrep/rga "no matches" (exit code 1 with
                    // the [ripgrep_no_matches] marker) is not reported as a failure.
                    FlushPendingToolResult(IsPendingToolResultFailed());
                }

                if (inRetainedThinking)
                {
                    bool reachedThinkingBoundary =
                        line.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Retry detail:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Retry instruction]", StringComparison.OrdinalIgnoreCase);
                    if (!reachedThinkingBoundary)
                    {
                        continue;
                    }

                    inRetainedThinking = false;
                }

                if (line.StartsWith("[assistant: thinking]", StringComparison.OrdinalIgnoreCase))
                {
                    inRetainedThinking = true;
                    continue;
                }

                if (inRetryDetail)
                {
                    if (line.StartsWith("[End retry detail]", StringComparison.OrdinalIgnoreCase))
                    {
                        inRetryDetail = false;
                    }

                    continue;
                }

                if (line.StartsWith("[Retry detail:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inRetryDetail = true;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;
                    continue;
                }

                if (inLegacyRetryPayload)
                {
                    if (!line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    inLegacyRetryPayload = false;
                }

                if (inPlanningModeTaskDetails &&
                    !line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.StartsWith("[Original user request]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                    {
                        inPlanningModeTaskDetails = false;
                        suppressInstructionMetadataSection = false;
                        afterUserRequest = true;
                        result.AppendLine(line.StartsWith("[User request]", StringComparison.OrdinalIgnoreCase)
                            ? "[User Prompt]:"
                            : line);
                        continue;
                    }

                    bool reachedTranscriptBoundary =
                        line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Previous Tool Call]:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Previous Response]:", StringComparison.OrdinalIgnoreCase);
                    if (!reachedTranscriptBoundary)
                    {
                        continue;
                    }

                    inPlanningModeTaskDetails = false;
                }

                if (line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata = true;
                    suppressInstructionMetadataSection = true;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;
                    StartUserTurn();
                    result.AppendLine("[User Prompt]:");
                }
                else if (line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata =
                        line.Contains("[Workspace agent rules]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Agent persona/instruction presets]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase);
                    suppressInstructionMetadataSection =
                        line.Contains("[Workspace agent rules]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase);
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = line.Contains("[Planning-mode task]", StringComparison.OrdinalIgnoreCase);
                    StartUserTurn();
                    result.AppendLine(inUserPromptInstructionMetadata ? "[User Prompt]:" : line);
                }
                else if (line.StartsWith("[Previous Tool Call]:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Previous Response]:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inLegacyRetryPayload = true;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;
                    continue;
                }
                else if (line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[assistant: tool call]", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureAgentRunHeader();
                    inToolCall = true;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;
                    continue;
                }
                else if (line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                    (line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) &&
                        line.EndsWith(" result]", StringComparison.OrdinalIgnoreCase)))
                {
                    EnsureAgentRunHeader();
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = true;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;

                    string toolName = line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase)
                        ? line.Replace("[Tool result:", "").Replace("]", "").Trim()
                        : line.Substring("[tool:".Length, line.Length - "[tool:".Length - " result]".Length).Trim();
                    pendingToolResultToolName = toolName;
                    continue;
                }
                else if (line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[assistant: final answer]", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    toolCallSyntaxReached = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    inPlanningModeTaskDetails = false;
                    result.AppendLine(line.StartsWith("[assistant: final answer]", StringComparison.OrdinalIgnoreCase)
                        ? "[Agent Response]:"
                        : line);
                }
                else if (line.StartsWith("[Planning-mode task]", StringComparison.OrdinalIgnoreCase))
                {
                    inPlanningModeTaskDetails = true;
                    result.AppendLine(line);
                }
                else if (inUserPromptInstructionMetadata)
                {
                    if (line.StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = false;
                        afterUserRequest = true;
                        result.AppendLine("[User Prompt]:");
                    }
                    else if (line.StartsWith("[Skill application rule]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = true;
                    }
                    else if (line.StartsWith("[Current Skill]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Current Preset]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = false;
                    }
                    else if (line.StartsWith("[Workspace agent rules]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = true;
                    }
                    else if (line.StartsWith("[Agent persona/instruction presets]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = false;
                    }
                    else if (afterUserRequest)
                    {
                        result.AppendLine(line);
                    }
                    else if (!suppressInstructionMetadataSection && line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        result.AppendLine(line);
                    }
                }
                else
                {
                    if (inToolResult)
                    {
                        if (pendingToolResultToolName != null && !string.IsNullOrWhiteSpace(line))
                        {
                            pendingToolResultBody.AppendLine(line);
                        }

                        if (line.Contains(
                                McpToolRateLimitException.ExaFreeMcpRateLimitMarker,
                                StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("Approximately ", StringComparison.OrdinalIgnoreCase))
                        {
                            result.AppendLine(line);
                        }
                    }
                    else if (inToolCall && !toolCallSyntaxReached)
                    {
                        int toolCallIndex = AgentToolCallParser.FindToolCallIndex(line);
                        if (toolCallIndex < 0)
                        {
                            // Also check for <log_tool_call tags (converted from <tool_call for display)
                            toolCallIndex = line.IndexOf("<log_tool_call", StringComparison.OrdinalIgnoreCase);
                        }
                        if (toolCallIndex >= 0)
                        {
                            string visiblePrefix = line.Substring(0, toolCallIndex).TrimEnd();
                            if (!string.IsNullOrWhiteSpace(visiblePrefix))
                            {
                                result.AppendLine(visiblePrefix);
                            }

                            toolCallSyntaxReached = true;
                        }
                        else if (line.StartsWith("[Parsed tool call:", StringComparison.OrdinalIgnoreCase) ||
                            (line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) &&
                                line.EndsWith(" arguments]", StringComparison.OrdinalIgnoreCase)) ||
                            AgentToolCallParser.ContainsToolCallSyntax(line) ||
                            line.Contains("<log_tool_call", StringComparison.OrdinalIgnoreCase))
                        {
                            toolCallSyntaxReached = true;
                        }
                        else
                        {
                            result.AppendLine(line);
                        }
                    }
                    else if (!inToolCall && !inToolResult)
                    {
                        result.AppendLine(line);
                    }
                }
            }

            if (pendingToolResultToolName != null)
            {
                FlushPendingToolResult(IsPendingToolResultFailed());
            }

            return result.ToString().TrimEnd();
        }

        private static string[] NormalizeStructuredUserTurns(
            string[] lines,
            Func<string, string, string>? getString)
        {
            var normalized = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals(
                        AgentRunTranscriptService.CompressedContextStartMarker,
                        StringComparison.OrdinalIgnoreCase))
                {
                    normalized.Add(lines[i]);
                    while (++i < lines.Length)
                    {
                        normalized.Add(lines[i]);
                        if (lines[i].Trim().Equals(
                                AgentRunTranscriptService.CompressedContextEndMarker,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    continue;
                }

                bool isUserRoleLine = lines[i].StartsWith("[user]", StringComparison.OrdinalIgnoreCase);
                bool isUserPromptLine = lines[i].StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase);
                if (!isUserRoleLine && !isUserPromptLine)
                {
                    normalized.Add(lines[i]);
                    continue;
                }

                string inlinePrompt = isUserPromptLine
                    ? lines[i].Substring("[User Prompt]:".Length).Trim()
                    : string.Empty;
                int end = i + 1;
                while (end < lines.Length &&
                    (!IsStructuredUserTurnBoundary(lines[end]) ||
                     (isUserRoleLine && lines[end].Equals("[User Prompt]:", StringComparison.OrdinalIgnoreCase))))
                {
                    end++;
                }

                if (isUserPromptLine && end == i + 1)
                {
                    if (string.IsNullOrWhiteSpace(inlinePrompt) &&
                        end < lines.Length &&
                        lines[end].StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    normalized.Add(lines[i]);
                    continue;
                }

                normalized.Add(BuildUserPromptDisplayLine(lines, i + 1, end, getString, inlinePrompt));
                i = end - 1;
            }

            return normalized.ToArray();
        }

        private static string BuildUserPromptDisplayLine(
            string[] lines,
            int start,
            int end,
            Func<string, string, string>? getString,
            string inlinePrompt = "")
        {
            var presetNames = new List<string>();
            var skillNames = new List<string>();
            var promptLines = new List<string>();
            var fallbackPromptLines = new List<string>();
            string mcpLabel = string.Empty;
            string section = string.Empty;
            bool inUserRequest = false;
            bool hasStructuredMetadata = false;
            bool foundUserRequest = false;

            if (!string.IsNullOrWhiteSpace(inlinePrompt))
            {
                fallbackPromptLines.Add(inlinePrompt);
            }

            for (int i = start; i < end; i++)
            {
                string line = lines[i];
                fallbackPromptLines.Add(line);
                if (line.StartsWith("[Current Skill]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "skill";
                    inUserRequest = false;
                    hasStructuredMetadata = true;
                }
                else if (line.StartsWith("[Current Preset]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "preset";
                    inUserRequest = false;
                    hasStructuredMetadata = true;
                }
                else if (line.StartsWith("[Current MCP]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "mcp";
                    inUserRequest = false;
                    hasStructuredMetadata = true;
                }
                else if (line.StartsWith("[User request]", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("[User Prompt]:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Original user request]", StringComparison.OrdinalIgnoreCase))
                {
                    section = string.Empty;
                    inUserRequest = true;
                    foundUserRequest = true;
                    promptLines.Clear();
                }
                else if (inUserRequest)
                {
                    promptLines.Add(line);
                }
                else if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    string name = line.Substring(3).Trim();
                    if (name.Length > 0 && section == "preset")
                    {
                        presetNames.Add(name);
                    }
                    else if (name.Length > 0 && section == "skill")
                    {
                        skillNames.Add(name);
                    }
                }
                else if (section == "mcp" && string.IsNullOrWhiteSpace(mcpLabel) && !string.IsNullOrWhiteSpace(line))
                {
                    mcpLabel = line.Trim();
                }
            }

            if (!foundUserRequest && !hasStructuredMetadata)
            {
                promptLines.AddRange(fallbackPromptLines);
            }

            var labels = new List<string>();
            if (presetNames.Count > 0)
            {
                labels.Add(string.Join(", ", presetNames));
            }
            if (!string.IsNullOrWhiteSpace(mcpLabel))
            {
                string format = getString?.Invoke("AgentMcpDisplayLabelFormat", "MCP: {0}") ?? "MCP: {0}";
                labels.Add(string.Format(format, mcpLabel));
            }
            if (skillNames.Count > 0)
            {
                string format = getString?.Invoke("AgentSkillDisplayLabelFormat", "Skill: {0}") ?? "Skill: {0}";
                labels.Add(string.Format(format, string.Join(", ", skillNames)));
            }

            string labelPrefix = labels.Count == 0 ? string.Empty : $"[{string.Join(" · ", labels)}]";
            string promptText = string.Join(Environment.NewLine, promptLines).Trim();
            string prompt = string.IsNullOrWhiteSpace(promptText)
                ? string.Empty
                : AgentRunTextFormatter.TruncateUserPrompt(promptText);
            string display = string.IsNullOrWhiteSpace(labelPrefix)
                ? prompt
                : string.IsNullOrWhiteSpace(prompt) ? labelPrefix : $"{labelPrefix} {prompt}";
            return string.IsNullOrWhiteSpace(display)
                ? "[User Prompt]:"
                : $"[User Prompt]: {display}";
        }

        private static bool IsStructuredUserTurnBoundary(string line)
        {
            return line.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[LLM API:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Previous Tool Call]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Previous Response]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Retry detail:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHistorySectionBoundaryLine(string line)
        {
            return line.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[LLM API:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Retry detail:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[End retry detail]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Previous Tool Call]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Previous Response]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[assistant: tool call]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                (line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) &&
                    (line.EndsWith(" arguments]", StringComparison.OrdinalIgnoreCase) ||
                     line.EndsWith(" result]", StringComparison.OrdinalIgnoreCase))) ||
                line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[assistant: final answer]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[Planning-mode task]", StringComparison.OrdinalIgnoreCase);
        }
    }
}
