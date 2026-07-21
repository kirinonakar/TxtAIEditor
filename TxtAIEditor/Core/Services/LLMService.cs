using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Core.Services
{
    public sealed class LLMService : ILLMService
    {
        private readonly LlmTextOperationService _textOperationService;
        private readonly LlmAgentService _agentService;
        private readonly LlmCredentialStore _credentialStore;
        private readonly LlmTokenUsageTracker _tokenUsageTracker;
        private readonly ExaSearchService _exaSearchService;

        public LLMService(
            ISettingsService settingsService,
            ICredentialService credentialService,
            ILocalizationService localizationService)
        {
            _credentialStore = new LlmCredentialStore(credentialService);
            _tokenUsageTracker = new LlmTokenUsageTracker();

            var requestExecutor = new LlmRequestExecutor(
                settingsService,
                _credentialStore,
                localizationService,
                _tokenUsageTracker);

            _textOperationService = new LlmTextOperationService(settingsService, requestExecutor);
            _agentService = new LlmAgentService(settingsService, requestExecutor);
            _exaSearchService = new ExaSearchService(
                settingsService,
                _credentialStore,
                new McpToolClient());
        }

        public LlmTokenUsage? LastTokenUsage => _tokenUsageTracker.LastTokenUsage;

        public LlmTokenUsageStats TokenUsageStats => _tokenUsageTracker.TokenUsageStats;

        public Task<string> ExplainCodeAsync(
            string code,
            string language,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.ExplainCodeAsync(code, language, onChunk, cancellationToken);
        }

        public Task<string> SummarizeTextAsync(
            string text,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.SummarizeTextAsync(text, onChunk, cancellationToken);
        }

        public Task<string> CompressAgentContextAsync(
            EditorSettings settings,
            string context,
            int targetTokens,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.CompressAgentContextAsync(
                settings,
                context,
                targetTokens,
                cancellationToken);
        }

        public Task<string> TranslateTextAsync(
            string text,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.TranslateTextAsync(text, onChunk, cancellationToken);
        }

        public Task<string> ImproveTextAsync(
            string text,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.ImproveTextAsync(text, onChunk, cancellationToken);
        }

        public Task<string> CustomPromptAsync(
            string prompt,
            string fileContext,
            string selectedText,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            return _textOperationService.CustomPromptAsync(
                prompt,
                fileContext,
                selectedText,
                onChunk,
                cancellationToken);
        }

        public Task<string> RunAgentAsync(
            string instruction,
            string workspaceContext,
            string selectedText,
            string mode,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<LlmMessageAttachment>? attachments = null,
            bool isPlanningMode = false,
            Func<string, Task>? onReasoning = null,
            IReadOnlyList<LlmTool>? tools = null,
            bool hasEnabledSkills = false,
            bool hasEnabledMcp = false,
            Func<LlmTokenUsage, Task>? onUsage = null,
            bool allowVisionFallback = false,
            Func<string, Task<bool>>? onVisionFallbackResult = null,
            string fixedContext = "")
        {
            return _agentService.RunAgentAsync(
                instruction,
                workspaceContext,
                selectedText,
                mode,
                onChunk,
                cancellationToken,
                attachments,
                isPlanningMode,
                onReasoning,
                tools,
                hasEnabledSkills,
                hasEnabledMcp,
                onUsage,
                allowVisionFallback,
                onVisionFallbackResult,
                fixedContext);
        }

        public Task<string> RunAgentAsync(
            EditorSettings settings,
            string instruction,
            string workspaceContext,
            string selectedText,
            string mode,
            Func<string, Task>? onChunk = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<LlmMessageAttachment>? attachments = null,
            bool isPlanningMode = false,
            Func<string, Task>? onReasoning = null,
            IReadOnlyList<LlmTool>? tools = null,
            bool hasEnabledSkills = false,
            bool hasEnabledMcp = false,
            Func<LlmTokenUsage, Task>? onUsage = null,
            bool allowVisionFallback = false,
            Func<string, Task<bool>>? onVisionFallbackResult = null,
            string fixedContext = "")
        {
            return _agentService.RunAgentAsync(
                settings,
                instruction,
                workspaceContext,
                selectedText,
                mode,
                onChunk,
                cancellationToken,
                attachments,
                isPlanningMode,
                onReasoning,
                tools,
                hasEnabledSkills,
                hasEnabledMcp,
                onUsage,
                allowVisionFallback,
                onVisionFallbackResult,
                fixedContext);
        }

        public void ResetTokenUsageStats()
        {
            _tokenUsageTracker.ResetTokenUsageStats();
        }

        public Task SaveApiKeyAsync(string provider, string apiKey)
        {
            return _credentialStore.SaveApiKeyAsync(provider, apiKey);
        }

        public Task<string> GetApiKeyAsync(string provider)
        {
            return _credentialStore.GetApiKeyAsync(provider);
        }

        public Task<string> SearchExaAsync(
            string query,
            int numResults,
            CancellationToken cancellationToken = default)
        {
            return _exaSearchService.SearchExaAsync(query, numResults, cancellationToken);
        }

        public Task<string> FetchExaAsync(
            string[] urls,
            CancellationToken cancellationToken = default)
        {
            return _exaSearchService.FetchExaAsync(urls, cancellationToken);
        }
    }
}
