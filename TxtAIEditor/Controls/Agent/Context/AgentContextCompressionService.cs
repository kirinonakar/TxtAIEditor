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
        private const double DefaultOutputReserveRatio = 0.25;
        private const int MaximumOutputReserveTokens = 65536;
        private const int MinimumInputBudgetTokens = 4096;

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
            string fixedPromptContext,
            string modelTranscript,
            string requestTranscript,
            string workspaceContext,
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

            int outputLimit = ResolveOutputLimit(settings, contextLimit);
            int inputBudget = Math.Max(MinimumInputBudgetTokens, contextLimit - outputLimit);

            int requestTokens = EstimateModelRequestTokens(
                settings,
                fixedPromptContext,
                requestTranscript,
                workspaceContext,
                selectedText,
                planningMode,
                hasEnabledSkills,
                hasEnabledMcp,
                tools,
                attachments);

            // The provider counts the requested completion budget against the context
            // window, and the character based estimate runs below the real tokenizer, so
            // compare a padded estimate against the padded input budget.
            int budgetedRequestTokens = LlmTokenBudget.ApplyEstimationSafetyMargin(requestTokens);
            if (budgetedRequestTokens <= Math.Floor(inputBudget * CompressionThresholdRatio))
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

        /// <summary>
        /// Estimates the tokens of the request that is actually sent to the model: the system
        /// prompt plus the user content built from the given transcript, workspace context,
        /// selection, attachments, and native tool catalog. The AgentPanel token display uses
        /// this raw measure; budget checks pad it with the estimation safety margin before
        /// comparing it against hard limits.
        /// </summary>
        public static int EstimateModelRequestTokens(
            EditorSettings settings,
            string fixedPromptContext,
            string requestTranscript,
            string workspaceContext,
            string selectedText,
            bool planningMode,
            bool hasEnabledSkills,
            bool hasEnabledMcp,
            IReadOnlyList<LlmTool>? tools,
            IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            string languageCode = LlmLanguageResolver.Resolve(settings);
            string targetLanguage = ResolveTargetLanguage(settings, languageCode);
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(
                languageCode,
                planningMode,
                targetLanguage,
                hasEnabledSkills,
                hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(
                fixedPromptContext,
                requestTranscript,
                workspaceContext,
                selectedText,
                string.Empty,
                languageCode);

            return LlmTokenBudget.EstimateRequestTokens(
                systemPrompt,
                userContent,
                attachments,
                AgentPromptContextService.SupportsNativeToolCatalog(settings) ? tools : null);
        }

        private static int ResolveOutputLimit(EditorSettings settings, int contextLimit)
        {
            int contextRatioLimit = (int)Math.Floor(contextLimit * DefaultOutputReserveRatio);
            var limits = ModelsDevCatalog.GetBestCachedLimits(
                settings.LlmProvider ?? string.Empty,
                settings.LlmModel ?? string.Empty);

            // The provider counts the requested completion budget (max_tokens) against the
            // context window, so the input budget must reserve the output headroom that the
            // request path can actually request. A fixed small reserve under-counts models
            // whose output limit is large (for example 384000 on a 1M context model) and let
            // input grow until the provider rejected the request with a context length error.
            if (limits.output > 0 && limits.output < contextLimit)
            {
                return Math.Max(1, Math.Min(limits.output, contextRatioLimit));
            }

            // Output limit unknown: keep a modest reserve that also covers provider fallbacks.
            return Math.Max(1, Math.Min(contextRatioLimit, MaximumOutputReserveTokens));
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
