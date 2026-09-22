using System;
using System.Collections.Generic;
using System.Globalization;

namespace TxtAIEditor.Core.Services.LLM
{
    /// <summary>
    /// Ambient request tuning derived from the global editor settings. The settings dialog applies
    /// application-wide values, so SettingsService publishes the selected API format and max context
    /// length here, and the provider/catalog helpers read them while building a request.
    /// </summary>
    internal static class LlmRequestTuning
    {
        public const string ApiFormatAuto = "auto";
        public const string ApiFormatChatCompletions = "chat";
        public const string ApiFormatResponses = "responses";
        public const string ApiFormatMessages = "messages";
        public const string MaxContextLengthAuto = "auto";

        private const long KiloTokens = 1024L;
        private const long MegaTokens = 1024L * 1024L;

        /// <summary>Selected API format: auto, chat, responses, or messages.</summary>
        public static string ApiFormat { get; private set; } = ApiFormatAuto;

        /// <summary>User supplied context length in tokens; 0 means auto (use the catalog value).</summary>
        public static int MaxContextTokens { get; private set; }

        /// <summary>When true, the temperature parameter is omitted from request payloads.</summary>
        public static bool DisableTemperature { get; private set; }

        public static bool ForcesResponses =>
            ApiFormat.Equals(ApiFormatResponses, StringComparison.Ordinal);

        public static bool ForcesChatCompletions =>
            ApiFormat.Equals(ApiFormatChatCompletions, StringComparison.Ordinal);

        public static bool ForcesMessages =>
            ApiFormat.Equals(ApiFormatMessages, StringComparison.Ordinal);

        public static void Apply(string? apiFormat, string? maxContextLength, bool disableTemperature)
        {
            ApiFormat = NormalizeApiFormat(apiFormat);
            MaxContextTokens = ParseMaxContextTokens(maxContextLength);
            DisableTemperature = disableTemperature;
        }

        /// <summary>
        /// Writes the temperature parameter into a request payload unless the user disabled it.
        /// </summary>
        public static void SetTemperature(IDictionary<string, object> payload, double temperature)
        {
            if (!DisableTemperature)
            {
                payload["temperature"] = temperature;
            }
        }

        public static string NormalizeApiFormat(string? value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "chat" or "chat completion" or "chat completions" or "chatcompletions" => ApiFormatChatCompletions,
                "responses" or "response" => ApiFormatResponses,
                "messages" or "message" or "anthropic" or "anthropic messages" => ApiFormatMessages,
                _ => ApiFormatAuto
            };
        }

        public static string NormalizeMaxContextLength(string? value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 ||
                trimmed.Equals(MaxContextLengthAuto, StringComparison.OrdinalIgnoreCase))
            {
                return MaxContextLengthAuto;
            }

            return ParseMaxContextTokens(trimmed) > 0 ? trimmed.ToLowerInvariant() : MaxContextLengthAuto;
        }

        /// <summary>
        /// Parses a context length such as "128k", "150k", "1m", or a raw token count.
        /// Returns 0 for auto, empty, or unparsable input.
        /// </summary>
        public static int ParseMaxContextTokens(string? value)
        {
            string trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length == 0 ||
                trimmed.Equals(MaxContextLengthAuto, StringComparison.Ordinal))
            {
                return 0;
            }

            long multiplier = 1;
            if (trimmed.EndsWith("k", StringComparison.Ordinal))
            {
                multiplier = KiloTokens;
                trimmed = trimmed.Substring(0, trimmed.Length - 1).Trim();
            }
            else if (trimmed.EndsWith("m", StringComparison.Ordinal))
            {
                multiplier = MegaTokens;
                trimmed = trimmed.Substring(0, trimmed.Length - 1).Trim();
            }

            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) ||
                amount <= 0)
            {
                return 0;
            }

            double tokens = amount * multiplier;
            if (tokens <= 0)
            {
                return 0;
            }

            return tokens >= int.MaxValue ? int.MaxValue : (int)tokens;
        }
    }
}
