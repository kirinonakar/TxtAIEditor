using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public static class WebViewAppearanceService
    {
        public static void ApplyPreferredColorScheme(CoreWebView2? coreWebView, string theme)
        {
            try
            {
                if (coreWebView?.Profile == null)
                {
                    return;
                }

                bool isDark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(theme, "CatppuccinMacchiato", StringComparison.OrdinalIgnoreCase);
                coreWebView.Profile.PreferredColorScheme = isDark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }
        }

        public static Windows.UI.Color ResolveEditorBackgroundColor(EditorSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.CustomBackgroundColor) &&
                TryParseHexColor(settings.CustomBackgroundColor, out var parsedBackground))
            {
                return parsedBackground;
            }

            if (string.Equals(settings.Theme, "CatppuccinMacchiato", StringComparison.OrdinalIgnoreCase))
            {
                return Windows.UI.Color.FromArgb(255, 36, 39, 58);
            }

            bool isLight = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            return isLight
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 30, 30, 30);
        }

        public static void ApplyEditorHostBackground(WebView2? webView, Windows.UI.Color color)
        {
            if (webView == null)
            {
                return;
            }

            webView.DefaultBackgroundColor = color;
            if (webView.Parent is Grid hostGrid)
            {
                hostGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

                foreach (var child in hostGrid.Children)
                {
                    if (child is Border border &&
                        string.Equals(border.Tag as string, "EditorLoadCover", StringComparison.Ordinal))
                    {
                        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
                    }
                }
            }
        }

        private static bool TryParseHexColor(string? value, out Windows.UI.Color color)
        {
            color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            string hex = (value ?? string.Empty).Trim().TrimStart('#');
            if (hex.Length != 6)
            {
                return false;
            }

            try
            {
                byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                color = Windows.UI.Color.FromArgb(255, r, g, b);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
