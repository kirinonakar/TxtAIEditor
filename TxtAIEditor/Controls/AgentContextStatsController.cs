using System;
using System.IO;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
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
            _settingsProvider = settingsProvider ?? (() => _settingsService.CurrentSettings);
        }

        public void Update(bool force = false)
        {
            if (_isRunningProvider() && !force)
            {
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

            _agentPane.ContextStats.Text = string.Format(
                _getString("AgentContextStatsFormat", "맥락: {0} · {1}"),
                tabPart,
                selectionPart);

            double estimatedTokens = EstimateContextTokens();
            int maxTokens = GetModelContextLimit();

            if (maxTokens > 0)
            {
                string currentStr = AgentTokenEstimator.Format(estimatedTokens);
                string maxStr = AgentTokenEstimator.Format(maxTokens);
                _agentPane.TokenCount.Text = string.Format(
                    _getString("AgentTokenCountWithLimitFormat", "{0} / {1} tokens"),
                    currentStr,
                    maxStr);
            }
            else
            {
                double kTokens = estimatedTokens / 1000.0;
                _agentPane.TokenCount.Text = string.Format(
                    _getString("AgentTokenCountFormat", "{0:F1}k tokens"),
                    kTokens);
            }

            UpdateModelDisplay();
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
                string format = _getString("AgentModelFormat", "모델: {0} ({1})");
                string result = string.Format(format, model, provider);
                if (settings.LlmAgentVerbose)
                {
                    result += " -v";
                }
                _agentPane.UpdateModelName(result);
            _refreshOutputDisplay();
        }

        private double EstimateContextTokens()
        {
            string langCode = _displayText.LanguageCode;
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(langCode, _agentPane.PlanningMode);

            string instruction = _buildAgentInstruction(_agentPane.Prompt.Text?.Trim() ?? string.Empty);
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

            return tokens + _currentRunTranscriptTokensProvider() + _attachmentController.EstimatedImageTokens;
        }

        private int GetModelContextLimit()
        {
            return _modelContextLimits.GetContextLimit(
                _settingsService.CurrentSettings,
                () => _agentPane.DispatcherQueue.TryEnqueue(() => Update(true)));
        }
    }
}
