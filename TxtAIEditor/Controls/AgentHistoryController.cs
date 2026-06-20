using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentHistoryItem
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Title { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
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
            var existing = _history.FirstOrDefault(h => h.Id == session.Id);
            if (existing != null)
            {
                existing.Timestamp = session.Timestamp;
                existing.SessionHistoryText = session.SessionHistoryText;
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
                .Select(h => new AgentHistoryItemViewModel
                {
                    Id = h.Id,
                    Title = h.Title,
                    TimeText = h.Timestamp.ToString("MM-dd HH:mm")
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
    }

    internal static class AgentHistoryFormatter
    {
        public static string Format(string historyText, bool verbose)
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
            var result = new StringBuilder();
            bool inToolCall = false;
            bool inToolResult = false;
            bool inUserPromptInstructionMetadata = false;
            bool suppressInstructionMetadataSection = false;
            bool afterUserRequest = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata =
                        line.Contains("[Agent persona/instruction presets]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase);
                    suppressInstructionMetadataSection = line.Contains("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase);
                    afterUserRequest = false;
                    result.AppendLine(inUserPromptInstructionMetadata ? "[User Prompt]:" : line);
                }
                else if (line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = true;
                    inToolResult = false;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    continue;
                }
                else if (line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    inToolResult = true;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;

                    string toolName = line.Replace("[Tool result:", "").Replace("]", "").Trim();
                    result.AppendLine($"[도구 실행 완료: {toolName}]");
                    continue;
                }
                else if (line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase))
                {
                    inToolCall = false;
                    inToolResult = false;
                    inUserPromptInstructionMetadata = false;
                    suppressInstructionMetadataSection = false;
                    afterUserRequest = false;
                    result.AppendLine(line);
                }
                else if (inUserPromptInstructionMetadata)
                {
                    if (line.StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressInstructionMetadataSection = false;
                        afterUserRequest = true;
                        result.AppendLine(line);
                    }
                    else if (line.StartsWith("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase))
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
                    if (!inToolCall && !inToolResult)
                    {
                        result.AppendLine(line);
                    }
                }
            }

            return result.ToString().TrimEnd();
        }
    }
}
