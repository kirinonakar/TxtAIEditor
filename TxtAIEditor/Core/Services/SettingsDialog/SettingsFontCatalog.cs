using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsFontCatalog
    {
        private static readonly object CacheLock = new();
        private static Task<IReadOnlyList<string>>? _installedFontFamiliesTask;

        public static Task<IReadOnlyList<string>> GetInstalledFontFamiliesAsync()
        {
            lock (CacheLock)
            {
                if (_installedFontFamiliesTask == null ||
                    _installedFontFamiliesTask.IsCanceled ||
                    _installedFontFamiliesTask.IsFaulted)
                {
                    _installedFontFamiliesTask = Task.Run(LoadInstalledFontFamilies);
                }

                return _installedFontFamiliesTask;
            }
        }

        private static IReadOnlyList<string> LoadInstalledFontFamilies()
        {
            var fonts = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase)
            {
                "Consolas",
                "Courier New",
                "Segoe UI",
                "Malgun Gothic"
            };

            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));
            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));

            return fonts.ToList();
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
                    string? fontFileName = key.GetValue(valueName) as string;
                    foreach (string family in GetFontFamiliesFromRegistryEntry(valueName, fontFileName))
                    {
                        fonts.Add(family);
                    }
                }
            }
        }

        private static IEnumerable<string> GetFontFamiliesFromRegistryEntry(string valueName, string? fontFileName)
        {
            if (fontFileName?.Trim().EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) == true)
            {
                IReadOnlyList<string> collectionFamilies = GetFontFamiliesFromCollectionFile(fontFileName);
                if (collectionFamilies.Count > 0)
                {
                    return collectionFamilies;
                }
            }

            string registeredNames = Regex.Replace(valueName, @"\s*\([^)]+\)\s*;?\s*$", string.Empty).Trim();
            string[] names = fontFileName?.Trim().EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) == true
                ? Regex.Split(registeredNames, @"\s+&\s+")
                : new[] { registeredNames };

            return names
                .Select(NormalizeFontFamilyName)
                .Where(family => !string.IsNullOrWhiteSpace(family));
        }

        private static IReadOnlyList<string> GetFontFamiliesFromCollectionFile(string fontFileName)
        {
            try
            {
                string fontPath = Environment.ExpandEnvironmentVariables(fontFileName.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(fontPath))
                {
                    fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fontPath);
                }

                if (!File.Exists(fontPath))
                {
                    return Array.Empty<string>();
                }

                using var collection = new PrivateFontCollection();
                collection.AddFontFile(fontPath);
                return collection.Families
                    .Select(family => NormalizeFontFamilyName(family.Name))
                    .Where(family => !string.IsNullOrWhiteSpace(family))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string NormalizeFontFamilyName(string name)
        {
            return Regex.Replace(
                name,
                @"(?:\s+(?:Regular|Normal|Bold|Italic|Oblique|Light|Medium|SemiLight|DemiLight|SemiBold|DemiBold|ExtraLight|ExtraBold|UltraLight|UltraBold|Black|Heavy|Thin|Condensed|Narrow))+$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
        }
    }
}
