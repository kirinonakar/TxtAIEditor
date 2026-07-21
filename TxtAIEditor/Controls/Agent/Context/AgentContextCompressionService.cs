using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentContextCompressionService
    {
        private const double CompressionThresholdRatio = 0.90;
        private const double PrefixCompressionRatio = 0.50;
        private const double SummaryTargetRatio = 0.20;
        private const int MinimumSummaryTargetTokens = 128;
        private const int MaximumSummaryTargetTokens = 4096;

        private readonly ILLMService _llmService;
        private readonly AgentModelContextLimitProvider _modelContextLimits;

        public AgentContextCompressionService(
            ILLMService llmService,
            AgentModelContextLimitProvider modelContextLimits)
        {
            _llmService = llmService;
            _modelContextLimits = modelContextLimits;
        }

        public async Task<AgentContextCompressionResult> CompressIfNeededAsync(
            EditorSettings settings,
            string instruction,
            string modelTranscript,
            string requestTranscript,
            string selectedText,
            bool planningMode,
            bool hasEnabledSkills,
            bool hasEnabledMcp,
            IReadOnlyList<LlmTool> tools,
            IReadOnlyList<LlmMessageAttachment> attachments,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelTranscript))
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            int contextLimit = _modelContextLimits.GetContextLimit(settings, () => { });
            if (contextLimit <= 0)
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            string languageCode = LlmLanguageResolver.Resolve(settings);
            string targetLanguage = ResolveTargetLanguage(settings, languageCode);
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(
                languageCode,
                planningMode,
                targetLanguage,
                hasEnabledSkills,
                hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(
                instruction,
                requestTranscript,
                selectedText,
                string.Empty,
                languageCode);
            int requestTokens = LlmTokenBudget.EstimateRequestTokens(
                systemPrompt,
                userContent,
                attachments,
                AgentPromptContextService.SupportsNativeToolCatalog(settings) ? tools : null);

            if (requestTokens <= Math.Floor(contextLimit * CompressionThresholdRatio))
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            int prefixLength = FindPrefixLengthByTokenRatio(modelTranscript, PrefixCompressionRatio);
            if (prefixLength <= 0 || prefixLength >= modelTranscript.Length)
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            string prefix = modelTranscript.Substring(0, prefixLength);
            string tail = modelTranscript.Substring(prefixLength);
            double prefixTokens = AgentTokenEstimator.Estimate(prefix);
            int targetTokens = Math.Clamp(
                (int)Math.Floor(prefixTokens * SummaryTargetRatio),
                MinimumSummaryTargetTokens,
                MaximumSummaryTargetTokens);

            string summary = await _llmService.CompressAgentContextAsync(
                settings,
                prefix,
                targetTokens,
                cancellationToken);
            summary = TrimToTokenBudget(summary?.Trim() ?? string.Empty, targetTokens);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            string compressedPrefix =
                "[Compressed earlier context]\n" +
                summary +
                "\n[End compressed earlier context]\n\n";
            if (AgentTokenEstimator.Estimate(compressedPrefix) >= prefixTokens)
            {
                return AgentContextCompressionResult.Unchanged(modelTranscript);
            }

            return new AgentContextCompressionResult(compressedPrefix + tail, true);
        }

        private static int FindPrefixLengthByTokenRatio(string text, double ratio)
        {
            double targetTokens = AgentTokenEstimator.Estimate(text) * ratio;
            double tokens = 0;
            int best = 0;
            while (best < text.Length - 1)
            {
                double nextTokens = tokens + EstimateCharacterTokens(text[best]);
                if (nextTokens > targetTokens)
                {
                    break;
                }

                tokens = nextTokens;
                best++;
            }

            if (best <= 0)
            {
                return 0;
            }

            int precedingLineBreak = text.LastIndexOf('\n', best - 1, best);
            int split = precedingLineBreak >= (int)Math.Floor(best * 0.90)
                ? precedingLineBreak + 1
                : best;
            return split > 0 && char.IsHighSurrogate(text[split - 1])
                ? split - 1
                : split;
        }

        private static string TrimToTokenBudget(string text, int tokenBudget)
        {
            if (string.IsNullOrEmpty(text) || AgentTokenEstimator.Estimate(text) <= tokenBudget)
            {
                return text;
            }

            double tokens = 0;
            int best = 0;
            while (best < text.Length)
            {
                double nextTokens = tokens + EstimateCharacterTokens(text[best]);
                if (nextTokens > tokenBudget)
                {
                    break;
                }

                tokens = nextTokens;
                best++;
            }

            if (best > 0 && best < text.Length && char.IsHighSurrogate(text[best - 1]))
            {
                best--;
            }

            return best > 0 ? text.Substring(0, best).TrimEnd() : string.Empty;
        }

        private static double EstimateCharacterTokens(char character) =>
            character <= 127 ? 0.25 : 0.7;

        private static string ResolveTargetLanguage(EditorSettings settings, string languageCode)
        {
            string targetLanguage = settings.LlmTargetLanguage ?? "Default";
            if (!string.IsNullOrEmpty(targetLanguage) &&
                !targetLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return targetLanguage;
            }

            return languageCode switch
            {
                "ko-KR" => "Korean",
                "ja-JP" => "Japanese",
                "zh-Hant" => "Chinese Traditional",
                "zh-Hans" => "Chinese Simplified",
                _ => "English"
            };
        }
    }

    internal sealed class AgentContextCompressionResult
    {
        public AgentContextCompressionResult(string transcript, bool compressed)
        {
            Transcript = transcript;
            Compressed = compressed;
        }

        public string Transcript { get; }
        public bool Compressed { get; }

        public static AgentContextCompressionResult Unchanged(string transcript) =>
            new(transcript, false);
    }
}
