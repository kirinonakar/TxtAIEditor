using System;
using System.IO;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentContextStatsController
    {
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentAttachmentController _attachmentController;
        private readonly Func<bool> _isRunningProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string> _activeSelectionTextProvider;
        private readonly Func<string> _activeSelectionContextProvider;
        private readonly Func<string, string> _buildAgentInstruction;
        private readonly Func<string, string> _buildWorkspaceContext;
        private readonly Func<string, string, string, string> _buildSessionHistoryForPrompt;
        private readonly Func<double> _currentRunTranscriptTokensProvider;
        private readonly Action _refreshOutputDisplay;
        private readonly Func<string, string, string> _getString;
        private readonly AgentModelContextLimitProvider _modelContextLimits;
        private readonly Func<EditorSettings> _settingsProvider;
        private readonly Func<double> _toolCatalogTokensProvider;
        private readonly Func<bool> _hasEnabledSkillsProvider;
        private readonly Func<bool> _hasEnabledMcpProvider;
        private double _estimatedTokensExcludingPrompt;
        private bool _hasFullTokenEstimate;

        public AgentContextStatsController(
            ISettingsService settingsService,
            AgentPane agentPane,
            AgentDisplayLocalizer displayText,
            AgentAttachmentController attachmentController,
            Func<bool> isRunningProvider,
            Func<OpenedTab?> activeTabProvider,
            Func<string> activeSelectionTextProvider,
            Func<string> activeSelectionContextProvider,
            Func<string, string> buildAgentInstruction,
            Func<string, string> buildWorkspaceContext,
            Func<string, string, string, string> buildSessionHistoryForPrompt,
            Func<double> currentRunTranscriptTokensProvider,
            Action refreshOutputDisplay,
            Func<string, string, string> getString,
            AgentModelContextLimitProvider modelContextLimits,
            Func<double> toolCatalogTokensProvider,
            Func<bool> hasEnabledSkillsProvider,
            Func<bool> hasEnabledMcpProvider,
            Func<EditorSettings>? settingsProvider = null)
        {
            _settingsService = settingsService;
            _agentPane = agentPane;
            _displayText = displayText;
            _attachmentController = attachmentController;
            _isRunningProvider = isRunningProvider;
            _activeTabProvider = activeTabProvider;
            _activeSelectionTextProvider = activeSelectionTextProvider;
            _activeSelectionContextProvider = activeSelectionContextProvider;
            _buildAgentInstruction = buildAgentInstruction;
            _buildWorkspaceContext = buildWorkspaceContext;
            _buildSessionHistoryForPrompt = buildSessionHistoryForPrompt;
            _currentRunTranscriptTokensProvider = currentRunTranscriptTokensProvider;
            _refreshOutputDisplay = refreshOutputDisplay;
            _getString = getString;
            _modelContextLimits = modelContextLimits;
            _toolCatalogTokensProvider = toolCatalogTokensProvider;
            _hasEnabledSkillsProvider = hasEnabledSkillsProvider;
            _hasEnabledMcpProvider = hasEnabledMcpProvider;
            _settingsProvider = settingsProvider ?? (() => _settingsService.CurrentSettings);
        }

        public void Update(bool force = false)
        {
            if (_isRunningProvider() && !force)
            {
                return;
            }

            // Some model-limit callbacks invoke this controller directly. Keep the focus
            // guard here as well so no full workspace/history calculation can slip through
            // while the user is typing.
            if (_agentPane.IsPromptInputFocused)
            {
                UpdatePromptTokenEstimate();
                return;
            }

            var activeTab = _activeTabProvider();
            string tabPart;
            if (activeTab == null)
            {
                tabPart = _getString("AgentNoActiveTab", "활성 탭 없음");
            }
            else
            {
                tabPart = Path.GetFileName(string.IsNullOrWhiteSpace(activeTab.FilePath) ? activeTab.Title : activeTab.FilePath);
                if (AgentWorkspaceContextBuilder.IsPdfTab(activeTab))
                {
                    tabPart = string.Format(_getString("AgentPdfActiveTabExcluded", "{0} (PDF 제외)"), tabPart);
                }
            }

            string activeSelectionText = _activeSelectionTextProvider();
            string selectionPart = string.IsNullOrEmpty(activeSelectionText)
                ? _getString("AgentNoSelection", "선택 없음")
                : string.Format(_getString("AgentSelectionStats", "선택 {0:N0}자"), activeSelectionText.Length);

            if (_attachmentController.Count > 0)
            {
                selectionPart = $"{selectionPart} · {_displayText.FormatAttachmentCount(_attachmentController.Count)}";
            }

            string contextStatsText = string.Format(
                _getString("AgentContextStatsFormat", "맥락: {0} · {1}"),
                tabPart,
                selectionPart);
            if (!string.Equals(_agentPane.ContextStats.Text, contextStatsText, StringComparison.Ordinal))
            {
                _agentPane.ContextStats.Text = contextStatsText;
            }

            string promptText = GetPromptText();
            double estimatedTokens = EstimateContextTokens(promptText);
            _estimatedTokensExcludingPrompt = Math.Max(
                0,
                estimatedTokens - AgentTokenEstimator.Estimate(promptText));
            _hasFullTokenEstimate = true;
            UpdateTokenCount(estimatedTokens);

            UpdateModelDisplay();
        }

        public void UpdatePromptTokenEstimate()
        {
            if (_isRunningProvider())
            {
                return;
            }

            if (!_hasFullTokenEstimate)
            {
                UpdateTokenCount(AgentTokenEstimator.Estimate(GetPromptText()));
                return;
            }

            double estimatedTokens = _estimatedTokensExcludingPrompt +
                AgentTokenEstimator.Estimate(GetPromptText());
            UpdateTokenCount(estimatedTokens);
        }

        private void UpdateTokenCount(double estimatedTokens)
        {
            int maxTokens = GetModelContextLimit();
            string tokenCountText;

            if (maxTokens > 0)
            {
                string currentStr = AgentTokenEstimator.Format(estimatedTokens);
                string maxStr = AgentTokenEstimator.Format(maxTokens);
                tokenCountText = string.Format(
                    _getString("AgentTokenCountWithLimitFormat", "{0} / {1} tokens"),
                    currentStr,
                    maxStr);
            }
            else
            {
                double kTokens = estimatedTokens / 1000.0;
                tokenCountText = string.Format(
                    _getString("AgentTokenCountFormat", "{0:F1}k tokens"),
                    kTokens);
            }

            if (!string.Equals(_agentPane.TokenCount.Text, tokenCountText, StringComparison.Ordinal))
            {
                _agentPane.TokenCount.Text = tokenCountText;
            }
        }

        public void UpdateModelDisplay(bool forceClearCache = false)
        {
            var settings = _settingsProvider();
            if (settings == null)
            {
                return;
            }

            if (forceClearCache)
            {
                _modelContextLimits.ResetLmStudioCache();
            }

                string provider = settings.LlmProvider ?? string.Empty;
                string model = settings.LlmModel ?? string.Empty;
                string thinkingLevel = SettingsLlmModelCatalog.GetThinkingLevelDisplay(settings.LlmThinkingLevel, provider);
                string displayInfo = string.IsNullOrEmpty(thinkingLevel) ? provider : $"{provider}, {thinkingLevel}";
                string format = _getString("AgentModelFormat", "모델: {0} ({1})");
                string result = string.Format(format, model, displayInfo);
                if (settings.LlmAgentVerbose)
                {
                    result += " -v";
                }
                _agentPane.UpdateModelName(result);
            if (forceClearCache)
            {
                _refreshOutputDisplay();
            }
        }

        private double EstimateContextTokens(string promptText)
        {
            string langCode = _displayText.LanguageCode;
            string targetLanguage = _settingsProvider()?.ResolveTargetLanguage() ?? "Korean";
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(
                langCode,
                _agentPane.PlanningMode,
                targetLanguage,
                _hasEnabledSkillsProvider(),
                _hasEnabledMcpProvider());

            string instruction = _buildAgentInstruction(promptText);
            string workspaceContext = _buildWorkspaceContext(instruction);
            string selectedText = _activeSelectionContextProvider();

            string baseUserContent = AgentPromptBuilder.BuildUserContent(
                instruction,
                string.Empty,
                selectedText,
                string.Empty);
            double tokens = AgentTokenEstimator.Estimate(systemPrompt) + AgentTokenEstimator.Estimate(baseUserContent);

            tokens += AgentTokenEstimator.Estimate(Environment.NewLine + "[Workspace context]" + Environment.NewLine);

            string sessionHistoryForPrompt = _buildSessionHistoryForPrompt(instruction, workspaceContext, selectedText);
            if (!string.IsNullOrWhiteSpace(sessionHistoryForPrompt))
            {
                tokens += AgentTokenEstimator.Estimate("[Session History]" + Environment.NewLine);
                tokens += AgentTokenEstimator.Estimate(sessionHistoryForPrompt) + AgentTokenEstimator.Estimate(Environment.NewLine);
                tokens += AgentTokenEstimator.Estimate("=================================" + Environment.NewLine + Environment.NewLine);
                tokens += AgentTokenEstimator.Estimate(workspaceContext + Environment.NewLine + Environment.NewLine);
            }
            else
            {
                tokens += AgentTokenEstimator.Estimate(workspaceContext + Environment.NewLine + Environment.NewLine);
            }

            return tokens + _currentRunTranscriptTokensProvider() + _attachmentController.EstimatedImageTokens + _toolCatalogTokensProvider();
        }

        private string GetPromptText()
        {
            return _agentPane.Prompt.Text?.Trim() ?? string.Empty;
        }

        private int GetModelContextLimit()
        {
            return _modelContextLimits.GetContextLimit(
                _settingsProvider(),
                () => _agentPane.DispatcherQueue.TryEnqueue(() => Update(true)));
        }
    }
}
