using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsFontCatalog
    {
        private static IReadOnlyList<string>? _installedFontFamiliesCache;

        public static IReadOnlyList<string> GetInstalledFontFamilies()
        {
            if (_installedFontFamiliesCache != null)
            {
                return _installedFontFamiliesCache;
            }

            var fonts = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase)
            {
                "Consolas",
                "Courier New",
                "Segoe UI",
                "Malgun Gothic"
            };

            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));
            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));

            _installedFontFamiliesCache = fonts.ToList();
            return _installedFontFamiliesCache;
        }

        private static void AddFontsFromRegistry(ISet<string> fonts, Microsoft.Win32.RegistryKey? key)
        {
            if (key == null)
            {
                return;
            }

            using (key)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    string family = NormalizeFontRegistryName(valueName);
                    if (!string.IsNullOrWhiteSpace(family))
                    {
                        fonts.Add(family);
                    }
                }
            }
        }

        private static string NormalizeFontRegistryName(string valueName)
        {
            string family = Regex.Replace(valueName, @"\s*\([^)]+\)\s*$", string.Empty).Trim();
            family = Regex.Replace(family, @"\s+(Regular|Normal|Bold|Italic|Oblique|Light|Medium|SemiBold|Semibold|ExtraLight|ExtraBold|Black|Thin|Condensed|Narrow)$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return family;
        }
    }
}
