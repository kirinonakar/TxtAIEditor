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
        private readonly Func<string> _buildFixedPromptContext;
        private readonly Func<string, string> _buildAgentInstruction;
        private readonly Func<string, string> _buildWorkspaceContext;
        private readonly Func<string, string, string, string> _buildSessionHistoryForPrompt;
        private readonly Func<double> _currentRunTranscriptTokensProvider;
        private readonly Func<double> _actualRequestTokensProvider;
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
            Func<string> buildFixedPromptContext,
            Func<string, string> buildAgentInstruction,
            Func<string, string> buildWorkspaceContext,
            Func<string, string, string, string> buildSessionHistoryForPrompt,
            Func<double> currentRunTranscriptTokensProvider,
            Func<double> actualRequestTokensProvider,
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
            _buildFixedPromptContext = buildFixedPromptContext;
            _buildAgentInstruction = buildAgentInstruction;
            _buildWorkspaceContext = buildWorkspaceContext;
            _buildSessionHistoryForPrompt = buildSessionHistoryForPrompt;
            _currentRunTranscriptTokensProvider = currentRunTranscriptTokensProvider;
            _actualRequestTokensProvider = actualRequestTokensProvider;
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
            // while the user is typing. Forced updates are exempt because they are requested
            // by the run itself (streaming tokens, run completion) and must always refresh
            // the visible token/context count.
            if (!force && _agentPane.IsPromptInputFocused)
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
            // After context compression the run reports the tokens that are actually sent to
            // the model, so prefer that value over the estimate that still contains the
            // uncompressed transcript.
            double actualRequestTokens = _actualRequestTokensProvider();
            double displayTokens = actualRequestTokens > 0 ? actualRequestTokens : estimatedTokens;
            _estimatedTokensExcludingPrompt = Math.Max(
                0,
                displayTokens - AgentTokenEstimator.Estimate(promptText));
            _hasFullTokenEstimate = true;
            UpdateTokenCount(displayTokens);

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
                    _getString("AgentTokenCountWithLimitFormat", "{0} / {1}"),
                    currentStr,
                    maxStr);
            }
            else
            {
                double kTokens = estimatedTokens / 1000.0;
                tokenCountText = string.Format(
                    _getString("AgentTokenCountFormat", "{0:F1}k"),
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
                _modelContextLimits.ResetContextLimitCache();
            }

                string provider = settings.LlmProvider ?? string.Empty;
                string model = settings.LlmModel ?? string.Empty;
                string thinkingLevel = SettingsLlmModelCatalog.GetThinkingLevelDisplay(settings.LlmThinkingLevel, provider);
                string displayInfo = string.IsNullOrEmpty(thinkingLevel) ? provider : $"{provider}, {thinkingLevel}";
                string format = _getString("AgentModelFormat", "모델: {0} ({1})");
                string result = string.Format(format, model, displayInfo);
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
            string sessionHistoryForPrompt = _buildSessionHistoryForPrompt(instruction, workspaceContext, selectedText);
            string conversationTranscript;
            if (!string.IsNullOrWhiteSpace(sessionHistoryForPrompt))
            {
                conversationTranscript = sessionHistoryForPrompt.TrimEnd() +
                    Environment.NewLine + Environment.NewLine + instruction;
            }
            else
            {
                conversationTranscript = instruction;
            }

            string baseUserContent = AgentPromptBuilder.BuildUserContent(
                _buildFixedPromptContext(),
                conversationTranscript,
                workspaceContext,
                selectedText,
                string.Empty,
                langCode);
            double tokens = AgentTokenEstimator.Estimate(systemPrompt) + AgentTokenEstimator.Estimate(baseUserContent);

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
