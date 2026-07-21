using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPromptContextService
    {
        private readonly AgentPane _agentPane;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentPresetController _presetController;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentWorkspaceContextBuilder _workspaceContextBuilder;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<AgentSelectionSnapshot> _selectionSnapshotProvider;
        private readonly Func<EditorSettings> _settingsProvider;
        private readonly Func<string> _sessionHistoryProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Dictionary<(bool PlanningMode, bool HasEnabledSkills), double> _baseToolTokenCache = new();

        public AgentPromptContextService(
            AgentPane agentPane,
            AgentFileToolService fileTools,
            AgentPresetController presetController,
            AgentSkillController skillController,
            AgentMcpController mcpController,
            AgentWorkspaceContextBuilder workspaceContextBuilder,
            Func<OpenedTab?> activeTabProvider,
            Func<AgentSelectionSnapshot> selectionSnapshotProvider,
            Func<EditorSettings> settingsProvider,
            Func<string> sessionHistoryProvider,
            Func<string, string, string> getString)
        {
            _agentPane = agentPane;
            _fileTools = fileTools;
            _presetController = presetController;
            _skillController = skillController;
            _mcpController = mcpController;
            _workspaceContextBuilder = workspaceContextBuilder;
            _activeTabProvider = activeTabProvider;
            _selectionSnapshotProvider = selectionSnapshotProvider;
            _settingsProvider = settingsProvider;
            _sessionHistoryProvider = sessionHistoryProvider;
            _getString = getString;
        }

        public string BuildInstructionDisplay(string userInstruction)
        {
            var labels = new List<string>();
            string presetLabel = _presetController.GetSelectedPresetLabel();
            if (!string.IsNullOrEmpty(presetLabel))
            {
                labels.Add(presetLabel);
            }

            string mcpLabel = _mcpController.GetSelectedMcpLabel();
            if (!string.IsNullOrEmpty(mcpLabel))
            {
                labels.Add(string.Format(_getString("AgentMcpDisplayLabelFormat", "MCP: {0}"), mcpLabel));
            }

            string skillLabel = _skillController.GetSelectedSkillLabel();
            if (!string.IsNullOrEmpty(skillLabel))
            {
                labels.Add(string.Format(_getString("AgentSkillDisplayLabelFormat", "Skill: {0}"), skillLabel));
            }

            if (labels.Count == 0)
            {
                return userInstruction;
            }

            string prefix = $"[{string.Join(" · ", labels)}]";
            if (string.IsNullOrWhiteSpace(userInstruction))
            {
                return prefix;
            }

            return $"{prefix} {userInstruction}";
        }

        public string BuildAgentInstruction(string userInstruction)
        {
            string presetSection = _presetController.BuildSelectedPresetSection();
            string mcpSection = _mcpController.BuildSelectedMcpSection();
            string skillSection = _skillController.BuildSelectedSkillSection();
            string agentsMdSection = BuildWorkspaceAgentsMdSection();
            if (string.IsNullOrWhiteSpace(presetSection) &&
                string.IsNullOrWhiteSpace(mcpSection) &&
                string.IsNullOrWhiteSpace(skillSection) &&
                string.IsNullOrWhiteSpace(agentsMdSection))
            {
                return userInstruction;
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agentsMdSection))
            {
                builder.AppendLine(agentsMdSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(presetSection))
            {
                builder.AppendLine(presetSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(mcpSection))
            {
                builder.AppendLine(mcpSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(skillSection))
            {
                builder.AppendLine(skillSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                builder.AppendLine("[User request]");
                builder.Append(userInstruction);
            }

            return builder.ToString().Trim();
        }

        public string BuildSessionHistoryForPrompt(
            string instruction,
            string workspaceContext,
            string selectedText)
        {
            string history = _sessionHistoryProvider();
            if (string.IsNullOrEmpty(history))
            {
                return string.Empty;
            }

            history = AgentRunTranscriptService.RemoveRetryDebugDetails(history);
            if (string.IsNullOrEmpty(history))
            {
                return string.Empty;
            }

            return history;
        }

        public string BuildWorkspaceContext(string instruction)
        {
            return _workspaceContextBuilder.Build(
                instruction,
                _activeTabProvider(),
                true,
                _selectionSnapshotProvider().HasLineRange);
        }

        public string BuildWorkspaceContext(
            string instruction,
            OpenedTab? activeTab,
            AgentSelectionSnapshot selectionSnapshot,
            IEnumerable<AgentAttachmentState> attachments,
            string? workspaceRootOverride = null)
        {
            return _workspaceContextBuilder.Build(
                instruction,
                activeTab,
                true,
                selectionSnapshot.HasLineRange,
                attachments,
                workspaceRootOverride);
        }

        public IReadOnlyList<LlmMessageAttachment> GetImageAttachmentsForRun(AgentRunContext context)
        {
            var attachments = new List<LlmMessageAttachment>();
            attachments.AddRange(context.Attachments
                .Select(attachment => attachment.ImageContent)
                .Where(attachment => attachment != null)
                .Cast<LlmMessageAttachment>());
            attachments.AddRange(context.ImageToolAttachments);
            return attachments;
        }

        private string BuildWorkspaceAgentsMdSection()
        {
            string workspaceRoot = _fileTools.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return string.Empty;
            }

            string agentsMdPath = Path.Combine(workspaceRoot, "AGENTS.md");
            if (!File.Exists(agentsMdPath))
            {
                return string.Empty;
            }

            try
            {
                string content = File.ReadAllText(agentsMdPath);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                builder.AppendLine("[Workspace agent rules]");
                builder.AppendLine($"Source: {agentsMdPath}");
                builder.AppendLine(content.Trim());
                return builder.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public double EstimateToolCatalogTokens()
        {
            if (!SupportsNativeToolCatalog(_settingsProvider()))
            {
                return 0;
            }

            bool planningMode = _agentPane.PlanningMode;
            bool hasEnabledSkills = _skillController.HasSelectedSkills();
            var baseCacheKey = (planningMode, hasEnabledSkills);
            if (!_baseToolTokenCache.TryGetValue(baseCacheKey, out double tokens))
            {
                var baseTools = new AgentLlmToolCatalog().Build(
                    planningMode,
                    Array.Empty<AgentMcpToolAlias>(),
                    hasEnabledSkills);
                tokens = AgentTokenEstimator.EstimateToolsTokens(baseTools);
                _baseToolTokenCache[baseCacheKey] = tokens;
            }

            var mcpAliases = _mcpController.GetActiveToolAliases();
            foreach (AgentMcpToolAlias alias in mcpAliases)
            {
                tokens += 12;
                tokens += AgentTokenEstimator.Estimate(alias.Alias);
                tokens += AgentTokenEstimator.Estimate(string.IsNullOrEmpty(alias.Description)
                    ? $"MCP tool '{alias.ToolName}' from server '{alias.ServerName}'."
                    : alias.Description);
                tokens += EstimateCompactJsonTokens(alias.InputSchemaJson);
            }

            return tokens;
        }

        private static double EstimateCompactJsonTokens(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return AgentTokenEstimator.Estimate("{\"type\":\"object\",\"properties\":{}}");
            }

            double tokens = 0;
            bool insideString = false;
            bool escaped = false;
            foreach (char character in json)
            {
                if (!insideString && character is ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                tokens += character <= 127 ? 0.25 : 0.7;
                if (!insideString)
                {
                    if (character == '"')
                    {
                        insideString = true;
                    }
                    continue;
                }

                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    insideString = false;
                }
            }

            return tokens;
        }

        internal static bool SupportsNativeToolCatalog(EditorSettings? settings)
        {
            string provider = NormalizeProviderName(settings?.LlmProvider);
            string model = settings?.LlmModel ?? string.Empty;

            // These providers currently ignore the native tools argument; their text tool protocol is already counted in the system prompt.
            if (provider is "gemini" or "lmstudio" or "ollama")
            {
                return false;
            }

            if (provider is "opencodego" or "go" or "opencodezen" or "zen")
            {
                return !model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static string NormalizeProviderName(string? provider)
        {
            return (provider ?? string.Empty)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        }

    }
}
