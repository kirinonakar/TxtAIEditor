using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPromptContextService
    {
        private const string ChatChartGuidance = """
            [Chat charts]
            Render requested charts in standalone Markdown fences.
            ```chart uses YAML with type, labels, series [{title, data}], and optional beginAtZero.
            For function sliders use ```chart YAML with type: function, expression (e.g. "a*sin(b*x)"), and parameters (e.g. {a: 1, b: {value: 2, min: 0, max: 5, step: 0.1}}). Use explicit *, ^, sin/cos/tan, exp/ln, sqrt/abs, pi/e. Optional xMin/xMax and yMin/yMax.
            ```advanced-chart uses strict Chart.js 4 JSON with type, data.labels, data.datasets [{label, data}], and optional options.
            For interactivity use built-in hover tooltips and clickable legends. For shared tooltips set options.interaction to {"mode":"index","intersect":false}. Use data and built-in options only.
            """;

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

        public string BuildConversationTurn(string userInstruction)
        {
            string presetSection = _presetController.BuildSelectedPresetSection();
            string skillSection = _skillController.BuildSelectedSkillSection();
            string mcpLabel = _mcpController.GetSelectedMcpLabel();
            if (string.IsNullOrWhiteSpace(presetSection) &&
                string.IsNullOrWhiteSpace(skillSection) &&
                string.IsNullOrWhiteSpace(userInstruction))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[user]");
            if (!string.IsNullOrWhiteSpace(skillSection))
            {
                builder.AppendLine("[Skill application rule]");
                builder.AppendLine("The skill metadata below applies to this user request only. If a listed skill is relevant, call skill_use before applying it and follow the returned SKILL.md.");
                builder.AppendLine();
                builder.AppendLine("[Current Skill]");
                builder.AppendLine(skillSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(mcpLabel))
            {
                builder.AppendLine("[Current MCP]");
                builder.AppendLine(mcpLabel);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(presetSection))
            {
                builder.AppendLine("[Current Preset]");
                builder.AppendLine(presetSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                builder.AppendLine("[User Prompt]:");
                builder.Append(userInstruction);
            }

            return builder.ToString().Trim();
        }

        private string BuildFixedPromptContext(
            string? workspaceRoot, bool includeMcp, EditorSettings settings, bool planningMode)
        {
            // A vision fallback reuses this prompt with its own provider.
            bool includePluginManagerTextTools = !SupportsNativeToolCatalog(settings) ||
                (!string.IsNullOrWhiteSpace(settings.LlmVisionFallbackModel) &&
                    UsesTextToolCalls(settings.LlmVisionFallbackProvider, settings.LlmVisionFallbackModel));
            string mcpSection = includeMcp
                ? _mcpController.BuildSelectedMcpSection(includePluginManagerTextTools, planningMode)
                : string.Empty;
            string globalAgentsMdSection = BuildGlobalAgentsMdSection();
            string agentsMdSection = BuildWorkspaceAgentsMdSection(workspaceRoot);
            if (string.IsNullOrWhiteSpace(mcpSection) &&
                string.IsNullOrWhiteSpace(globalAgentsMdSection) &&
                string.IsNullOrWhiteSpace(agentsMdSection))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(mcpSection))
            {
                builder.AppendLine("[MCP instructions and tool schemas]");
                builder.AppendLine(mcpSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(globalAgentsMdSection))
            {
                builder.AppendLine(globalAgentsMdSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(agentsMdSection))
            {
                builder.AppendLine(agentsMdSection);
            }

            return builder.ToString().Trim();
        }

        public string BuildGlobalAgentsMdLogSection()
        {
            string section = BuildGlobalAgentsMdSection();
            if (string.IsNullOrWhiteSpace(section))
            {
                return string.Empty;
            }

            return section + Environment.NewLine + "[End global agent rules]";
        }

        public string BuildFixedPromptContextForRun(
            string? workspaceRoot = null, bool includeMcp = true, EditorSettings? settings = null, bool? planningMode = null,
            bool includeChatCharts = false)
        {
            string skills = _skillController.BuildSelectedSkillSection();
            return BuildFixedPromptContext(workspaceRoot, includeMcp, settings ?? _settingsProvider(), planningMode ?? _agentPane.PlanningMode) +
                Environment.NewLine + Environment.NewLine +
                "[Current plugin selection for this model turn]" + Environment.NewLine +
                "This turn's MCP tools and enabled skills override past selections. Use skill_use before applying a relevant skill." + Environment.NewLine +
                (string.IsNullOrWhiteSpace(skills) ? "No agent skills are currently enabled." : skills) +
                (includeChatCharts ? Environment.NewLine + Environment.NewLine + ChatChartGuidance : string.Empty);
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

            return PrepareSessionHistoryForPrompt(history);
        }

        public string PrepareSessionHistoryForPrompt(string history)
        {
            history = AgentRunTranscriptService.RemoveRetryDebugDetails(history);
            history = AgentRunTranscriptService.RemoveApiTypeMarkers(history);
            if (!_settingsProvider().LlmRetainThinking)
            {
                history = AgentThinkingTranscriptFormatter.RemoveRetainedThinkingSections(history);
            }
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

        public Task<string> BuildWorkspaceContextAsync(
            string instruction,
            OpenedTab? activeTab,
            AgentSelectionSnapshot selectionSnapshot,
            IEnumerable<AgentAttachmentState> attachments,
            string workspaceRoot,
            CancellationToken cancellationToken)
        {
            return _workspaceContextBuilder.BuildAsync(
                instruction,
                activeTab,
                true,
                selectionSnapshot.HasLineRange,
                attachments,
                workspaceRoot,
                cancellationToken);
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

        private string BuildGlobalAgentsMdSection()
        {
            return BuildAgentsMdSection(
                Path.Combine(AgentSkillDirectories.UserSettingsDirectory, "AGENTS.md"),
                "[Global agent rules]");
        }

        private string BuildWorkspaceAgentsMdSection(string? workspaceRoot)
        {
            workspaceRoot ??= _fileTools.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return string.Empty;
            }

            try
            {
                AgentWorkspaceFileResolver.VerifyPhysicalPath(workspaceRoot);
                var workspace = new AgentWorkspaceFileResolver(() => workspaceRoot);
                string path = workspace.ResolveInsideWorkspace("AGENTS.md");
                workspace.VerifyResolvedPath(path);
                return BuildAgentsMdSection(path, "[Workspace agent rules]");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string BuildAgentsMdSection(string agentsMdPath, string header)
        {
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
                builder.AppendLine(header);
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
                if (planningMode && AgentMcpPluginManagerTool.IsInstallAlias(alias.Alias))
                {
                    continue;
                }

                tokens += 12;
                tokens += AgentTokenEstimator.Estimate(alias.Alias);
                tokens += AgentTokenEstimator.Estimate(string.IsNullOrEmpty(alias.Description)
                    ? $"MCP tool '{alias.ToolName}' from server '{alias.ServerName}'."
                    : alias.Description);
                tokens += EstimateCompactJsonTokens(alias.InputSchemaJson);
            }

            if (_agentPane.UseSubagents)
            {
                tokens += AgentTokenEstimator.EstimateToolsTokens(AgentSubagentCoordinator.Tools);
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

        internal static bool SupportsNativeToolCatalog(EditorSettings? settings) =>
            !UsesTextToolCalls(settings?.LlmProvider, settings?.LlmModel);

        private static bool UsesTextToolCalls(string? provider, string? model) =>
            GeminiProvider.IsGeminiProvider(provider) && !GeminiProvider.SupportsFunctionCalling(model);

    }
}
