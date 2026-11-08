using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    // Error paths in models/static services have no window or localization dependency.
    // Use the UI culture applied by ResourceLocalizationService and the same resw files.
    internal static class LocalizedServiceMessages
    {
        internal static string Get(string key, string englishFallback)
        {
            string culture = CultureInfo.CurrentUICulture.Name;
            string language = culture.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko-KR" :
                culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja-JP" :
                culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ?
                    (culture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || culture is "zh-TW" or "zh-HK" or "zh-MO" ? "zh-Hant" : "zh-Hans") : "en-US";
            return Read(language, key) ?? (language == "en-US" ? null : Read("en-US", key)) ?? englishFallback;
        }

        private static string? Read(string language, string key)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw");
                return XDocument.Load(path).Root?.Elements("data")
                    .FirstOrDefault(element => (string?)element.Attribute("name") == key)?.Element("value")?.Value;
            }
            catch { return null; }
        }
    }
}
