using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class LlmAgentService
    {
        private readonly ISettingsService _settingsService;
        private readonly LlmRequestExecutor _requestExecutor;

        public LlmAgentService(
            ISettingsService settingsService,
            LlmRequestExecutor requestExecutor)
        {
            _settingsService = settingsService;
            _requestExecutor = requestExecutor;
        }

        public async Task<string> RunAgentAsync(string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false, Func<LlmTokenUsage, Task>? onUsage = null, bool allowVisionFallback = false, Func<string, Task>? onVisionFallbackResult = null)
        {
            string langCode = LlmLanguageResolver.Resolve(_settingsService.CurrentSettings);
            string targetLanguage = _settingsService.CurrentSettings?.LlmTargetLanguage ?? "Default";
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                targetLanguage = langCode switch
                {
                    "ko-KR" => "Korean",
                    "ja-JP" => "Japanese",
                    "zh-Hant" => "Chinese Traditional",
                    "zh-Hans" => "Chinese Simplified",
                    _ => "English"
                };
            }
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(langCode, isPlanningMode, targetLanguage, hasEnabledSkills, hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(instruction, workspaceContext, selectedText, string.Empty, langCode);
            return await _requestExecutor.ExecuteAsync(systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage, allowVisionFallback, onVisionFallbackResult);
        }

        public async Task<string> RunAgentAsync(EditorSettings settings, string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false, Func<LlmTokenUsage, Task>? onUsage = null, bool allowVisionFallback = false, Func<string, Task>? onVisionFallbackResult = null)
        {
            string langCode = LlmLanguageResolver.Resolve(settings);
            string targetLanguage = settings.LlmTargetLanguage ?? "Default";
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                targetLanguage = langCode switch
                {
                    "ko-KR" => "Korean",
                    "ja-JP" => "Japanese",
                    "zh-Hant" => "Chinese Traditional",
                    "zh-Hans" => "Chinese Simplified",
                    _ => "English"
                };
            }
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(langCode, isPlanningMode, targetLanguage, hasEnabledSkills, hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(instruction, workspaceContext, selectedText, string.Empty, langCode);
            return await _requestExecutor.ExecuteAsync(settings, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage, allowVisionFallback, onVisionFallbackResult);
        }

    }
}
