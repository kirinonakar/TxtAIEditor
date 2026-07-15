using System;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.LLM
{
    internal static class LlmLanguageResolver
    {
        public static string Resolve(EditorSettings? settings)
        {
            var lang = settings?.Language;
            if (string.IsNullOrEmpty(lang) || lang.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
                }
                catch
                {
                    lang = "en-US";
                }
            }

            if (lang != null)
            {
                if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko-KR";
                if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
                if (IsTraditionalChinese(lang)) return "zh-Hant";
                if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            }
            return "en-US";
        }

        private static bool IsTraditionalChinese(string language)
        {
            return language.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase);
        }
    }
}
