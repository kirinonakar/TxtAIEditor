using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Windows.ApplicationModel.Resources;
using TxtAIEditor.Core.Interfaces;
using Windows.Globalization;

namespace TxtAIEditor.Core.Services
{
    public sealed class ResourceLocalizationService : ILocalizationService
    {
        private readonly ISettingsService _settingsService;
        private readonly Dictionary<string, Dictionary<string, string>> _reswStringCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private ResourceManager _resourceManager = new ResourceManager();
        private ResourceContext? _resourceContext;

        public ResourceLocalizationService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public string GetString(string key, string fallback)
        {
            string reswValue = GetLocalizedStringFromResw(key);
            if (!string.IsNullOrEmpty(reswValue))
            {
                return reswValue;
            }

            try
            {
                EnsureResourceContext();
                string value = _resourceContext == null
                    ? new ResourceLoader().GetString(key)
                    : _resourceManager.MainResourceMap.GetSubtree("Resources").GetValue(key, _resourceContext).ValueAsString;
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Missing localized string '{key}': {ex.Message}");
                return fallback;
            }
        }

        public void ApplyResourceLanguage()
        {
            try
            {
                string configuredLanguage = _settingsService.CurrentSettings.Language ?? "Default";
                string activeLanguage = GetActiveLanguage();
                ApplicationLanguages.PrimaryLanguageOverride = configuredLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : activeLanguage;

                var culture = new System.Globalization.CultureInfo(activeLanguage);
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = culture;

                _resourceManager = new ResourceManager();
                _resourceContext = _resourceManager.CreateResourceContext();
                _resourceContext.QualifierValues["Language"] = activeLanguage;
                _reswStringCache.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply resource language: {ex.Message}");
            }
        }

        private string GetLocalizedStringFromResw(string key)
        {
            try
            {
                string language = GetActiveLanguage();
                var strings = GetReswStrings(language);
                if (strings.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
                {
                    return value;
                }

                if (!language.Equals("en-US", StringComparison.OrdinalIgnoreCase) &&
                    GetReswStrings("en-US").TryGetValue(key, out value) &&
                    !string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read localized resw string '{key}': {ex.Message}");
            }

            return string.Empty;
        }

        private Dictionary<string, string> GetReswStrings(string language)
        {
            if (_reswStringCache.TryGetValue(language, out var strings))
            {
                return strings;
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Strings", language, "Resources.resw");
            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw");
            }

            strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                var doc = XDocument.Load(path);
                foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
                {
                    string? name = data.Attribute("name")?.Value;
                    string? value = data.Element("value")?.Value;
                    if (!string.IsNullOrWhiteSpace(name) && value != null)
                    {
                        strings[name] = value;
                    }
                }
            }

            _reswStringCache[language] = strings;
            return strings;
        }

        private string GetActiveLanguage()
        {
            var lang = _settingsService.CurrentSettings.Language;
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
            }

            return "en-US";
        }

        private void EnsureResourceContext()
        {
            if (_resourceContext == null)
            {
                ApplyResourceLanguage();
            }
        }
    }
}
