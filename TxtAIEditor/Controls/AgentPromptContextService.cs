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
        private const int FallbackSessionHistoryPromptChars = 80_000;
        private const double PromptContextSafetyRatio = 0.95;

        private readonly AgentPane _agentPane;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentPresetController _presetController;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentWorkspaceContextBuilder _workspaceContextBuilder;
        private readonly AgentAttachmentController _attachmentController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentModelContextLimitProvider _modelContextLimits;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<AgentSelectionSnapshot> _selectionSnapshotProvider;
        private readonly Func<EditorSettings> _settingsProvider;
        private readonly Func<string> _sessionHistoryProvider;
        private readonly Action _contextStatsChanged;
        private readonly Func<string, string, string> _getString;

        public AgentPromptContextService(
            AgentPane agentPane,
            AgentFileToolService fileTools,
            AgentPresetController presetController,
            AgentSkillController skillController,
            AgentMcpController mcpController,
            AgentWorkspaceContextBuilder workspaceContextBuilder,
            AgentAttachmentController attachmentController,
            AgentDisplayLocalizer displayText,
            AgentModelContextLimitProvider modelContextLimits,
            Func<OpenedTab?> activeTabProvider,
            Func<AgentSelectionSnapshot> selectionSnapshotProvider,
            Func<EditorSettings> settingsProvider,
            Func<string> sessionHistoryProvider,
            Action contextStatsChanged,
            Func<string, string, string> getString)
        {
            _agentPane = agentPane;
            _fileTools = fileTools;
            _presetController = presetController;
            _skillController = skillController;
            _mcpController = mcpController;
            _workspaceContextBuilder = workspaceContextBuilder;
            _attachmentController = attachmentController;
            _displayText = displayText;
            _modelContextLimits = modelContextLimits;
            _activeTabProvider = activeTabProvider;
            _selectionSnapshotProvider = selectionSnapshotProvider;
            _settingsProvider = settingsProvider;
            _sessionHistoryProvider = sessionHistoryProvider;
            _contextStatsChanged = contextStatsChanged;
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
            if (!_agentPane.PlanningMode)
            {
                return string.Empty;
            }

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
                _settingsProvider(),
                () => _agentPane.DispatcherQueue.TryEnqueue(() => _contextStatsChanged()));
        }

        private bool IsLmStudioProvider()
        {
            string provider = _settingsProvider().LlmProvider ?? string.Empty;
            return provider.Contains("lm studio", StringComparison.OrdinalIgnoreCase) ||
                provider.Contains("lmstudio", StringComparison.OrdinalIgnoreCase);
        }
    }
}
