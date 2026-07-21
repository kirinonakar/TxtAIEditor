using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TxtAIEditor.Core.Services.LLM
{
    internal static class LlmTokenBudget
    {
        private const int MinimumMaxOutputTokens = 1;
        private const int MinimumSafetyReserveTokens = 1024;
        private const double SafetyReserveRatio = 0.02;
        private const int MessageOverheadTokens = 16;
        private const int DefaultImageTokens = 1024;

        public static int GetSafeMaxOutputTokens(
            int contextLimit,
            int outputLimit,
            string systemPrompt,
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments = null,
            IReadOnlyList<LlmTool>? tools = null)
        {
            if (outputLimit <= 0)
            {
                return 0;
            }

            if (contextLimit <= 0)
            {
                return outputLimit;
            }

            int inputTokens = EstimateRequestTokens(systemPrompt, userContent, attachments, tools);
            int reserveTokens = Math.Max(
                MinimumSafetyReserveTokens,
                (int)Math.Ceiling(contextLimit * SafetyReserveRatio));

            long availableTokens = (long)contextLimit - inputTokens - reserveTokens;
            if (availableTokens <= 0)
            {
                availableTokens = (long)contextLimit - inputTokens;
            }

            if (availableTokens <= 0)
            {
                return MinimumMaxOutputTokens;
            }

            return (int)Math.Min(outputLimit, Math.Max(MinimumMaxOutputTokens, availableTokens));
        }

        internal static int EstimateRequestTokens(
            string systemPrompt,
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools)
        {
            double tokens = MessageOverheadTokens * 2;
            tokens += EstimateTextTokens(systemPrompt);
            tokens += EstimateTextTokens(userContent);

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    if (!attachment.IsImage || string.IsNullOrWhiteSpace(attachment.Base64Data))
                    {
                        continue;
                    }

                    tokens += attachment.EstimatedTokens > 0
                        ? attachment.EstimatedTokens
                        : DefaultImageTokens;
                }
            }

            if (tools != null)
            {
                foreach (var tool in tools)
                {
                    tokens += 12;
                    tokens += EstimateTextTokens(tool.Name);
                    tokens += EstimateTextTokens(tool.Description);
                    if (tool.Parameters != null)
                    {
                        try
                        {
                            tokens += EstimateTextTokens(JsonSerializer.Serialize(tool.Parameters));
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return (int)Math.Ceiling(tokens);
        }

        private static double EstimateTextTokens(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double tokens = 0;
            foreach (char c in text)
            {
                tokens += c <= 127 ? 0.25 : 0.7;
            }

            return tokens;
        }
    }
}
