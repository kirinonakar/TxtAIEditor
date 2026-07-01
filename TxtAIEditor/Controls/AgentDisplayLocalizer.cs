using System;
using System.Collections.Generic;
using System.Linq;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentDisplayLocalizer
    {
        private static readonly string[] LegacyOutputPlaceholderPrefixes =
        {
            "대기 중...",
            "Waiting...",
            "待機中...",
            "等待中..."
        };

        private static readonly string[] LegacyActivityIdleValues =
        {
            "대기 중",
            "Idle",
            "待機中",
            "空闲",
            "闲置",
            "閒置"
        };

        private readonly Func<string, string, string> _getString;

        public AgentDisplayLocalizer(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public static AgentDisplayLocalizer CreateWithResourceLoader()
        {
            Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? loader = null;
            try
            {
                loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            }
            catch
            {
            }

            return new AgentDisplayLocalizer((key, fallback) =>
            {
                if (loader == null)
                {
                    return fallback;
                }

                try
                {
                    string value = loader.GetString(key);
                    return string.IsNullOrEmpty(value) ? fallback : value;
                }
                catch
                {
                    return fallback;
                }
            });
        }

        public string GetString(string key, string fallback) => _getString(key, fallback);

        public string LanguageCode
        {
            get
            {
                string code = GetString("AgentLanguageCode", "en-US").Trim();
                return string.IsNullOrWhiteSpace(code) ? "en-US" : code;
            }
        }

        public string OutputPlaceholder =>
            GetString("AgentOutputPlaceholder", "Waiting... Enter a task for the Agent.");

        public string ActivityIdle =>
            GetString("AgentActivityIdle", "Idle");

        public string FormatInlineTokenCount(int tokenCount)
        {
            return string.Format(
                GetString("AgentInlineTokenCountFormat", "{0:N0} tokens"),
                tokenCount);
        }

        public string FormatAttachmentCount(int count)
        {
            string key = count == 1
                ? "AgentAttachmentCountSingularFormat"
                : "AgentAttachmentCountFormat";
            string fallback = count == 1
                ? "{0:N0} attachment"
                : "{0:N0} attachments";

            return string.Format(GetString(key, fallback), count);
        }

        public string FormatPreparingToolLabel(int tokenCount)
        {
            return string.Format(
                GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                GetString("AgentOutputPreparingTool", "Preparing tool call"),
                FormatInlineTokenCount(tokenCount));
        }

        public bool IsOutputPlaceholder(string text)
        {
            string trimmed = (text ?? string.Empty).TrimStart();
            return GetDelimitedValues("AgentOutputPlaceholderKnownPrefixes", OutputPlaceholder)
                .Concat(LegacyOutputPlaceholderPrefixes)
                .Distinct(StringComparer.Ordinal)
                .Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal));
        }

        public bool IsActivityIdle(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return GetDelimitedValues("AgentActivityIdleKnownValues", ActivityIdle)
                .Concat(LegacyActivityIdleValues)
                .Distinct(StringComparer.Ordinal)
                .Any(value => string.Equals(trimmed, value, StringComparison.Ordinal));
        }

        private IEnumerable<string> GetDelimitedValues(string key, string fallback)
        {
            string raw = GetString(key, fallback);
            return raw
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0);
        }
    }
}
