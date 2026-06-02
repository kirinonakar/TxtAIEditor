using System;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class UiPersonalizationService : IUiPersonalizationService
    {
        public void Apply(
            EditorSettings settings,
            AppWindow appWindow,
            FrameworkElement? rootElement,
            Action<Windows.UI.Color> applyMarkdownToolbarBackground)
        {
            if (rootElement == null)
            {
                return;
            }

            rootElement.RequestedTheme = settings.Theme == "Light"
                ? ElementTheme.Light
                : ElementTheme.Dark;

            ApplyTitleBarTheme(settings, appWindow);
            ApplyMarkdownToolbarTheme(settings, applyMarkdownToolbarBackground);
            ApplyShellFont(settings, rootElement);
            ApplyRootBackground(settings, rootElement);
        }

        private static void ApplyTitleBarTheme(EditorSettings settings, AppWindow appWindow)
        {
            try
            {
                var titleBar = appWindow.TitleBar;
                bool light = settings.Theme == "Light";

                Windows.UI.Color background = TryParseHexColor(settings.CustomBackgroundColor, out var customBg)
                    ? customBg
                    : (light ? Windows.UI.Color.FromArgb(255, 243, 244, 246) : Windows.UI.Color.FromArgb(255, 30, 30, 30));
                Windows.UI.Color foreground = TryParseHexColor(settings.CustomForegroundColor, out var customFg)
                    ? customFg
                    : (light ? Windows.UI.Color.FromArgb(255, 31, 41, 55) : Windows.UI.Color.FromArgb(255, 212, 212, 212));
                Windows.UI.Color inactiveBackground = light
                    ? Windows.UI.Color.FromArgb(255, 229, 231, 235)
                    : Windows.UI.Color.FromArgb(255, 45, 49, 57);
                Windows.UI.Color hoverBackground = light
                    ? Windows.UI.Color.FromArgb(255, 229, 231, 235)
                    : Windows.UI.Color.FromArgb(255, 45, 49, 57);

                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveBackgroundColor = inactiveBackground;
                titleBar.InactiveForegroundColor = foreground;
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.ButtonInactiveForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = hoverBackground;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = hoverBackground;
                titleBar.ButtonPressedForegroundColor = foreground;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply titlebar theme: {ex.Message}");
            }
        }

        private static void ApplyMarkdownToolbarTheme(
            EditorSettings settings,
            Action<Windows.UI.Color> applyMarkdownToolbarBackground)
        {
            try
            {
                Windows.UI.Color background = TryParseHexColor(settings.MarkdownToolbarBackgroundColor, out var customToolbarBg)
                    ? customToolbarBg
                    : (settings.Theme == "Light"
                        ? Windows.UI.Color.FromArgb(255, 243, 244, 246)
                        : Windows.UI.Color.FromArgb(255, 43, 47, 54));
                applyMarkdownToolbarBackground(background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply markdown toolbar theme: {ex.Message}");
            }
        }

        private static void ApplyShellFont(EditorSettings settings, FrameworkElement rootElement)
        {
            try
            {
                var fontFamily = new Microsoft.UI.Xaml.Media.FontFamily(settings.UiFontFamily);
                
                // Override theme resource font families globally in the application
                Application.Current.Resources["ContentControlThemeFontFamily"] = fontFamily;
                Application.Current.Resources["SystemControlFontFamily"] = fontFamily;
                
                // Override theme resource font families at the root element level
                rootElement.Resources["ContentControlThemeFontFamily"] = fontFamily;
                rootElement.Resources["SystemControlFontFamily"] = fontFamily;
                
                ApplyFontFamilyRecursively(rootElement, fontFamily);
            }
            catch
            {
            }
        }

        private static void ApplyRootBackground(EditorSettings settings, FrameworkElement rootElement)
        {
            if (!string.IsNullOrEmpty(settings.CustomBackgroundColor))
            {
                try
                {
                    if (TryParseHexColor(settings.CustomBackgroundColor, out var color) && rootElement is Grid rootGrid)
                    {
                        rootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
                    }
                }
                catch
                {
                }
            }
            else if (rootElement is Grid rootGrid)
            {
                rootGrid.Background = null;
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

        private static void ApplyFontFamilyRecursively(DependencyObject parent, Microsoft.UI.Xaml.Media.FontFamily fontFamily)
        {
            if (parent == null)
            {
                return;
            }

            if (parent is IconElement)
            {
                return;
            }

            if (parent is FrameworkElement fe)
            {
                // Force font family resource overrides on all FrameworkElements
                fe.Resources["ContentControlThemeFontFamily"] = fontFamily;
                fe.Resources["SystemControlFontFamily"] = fontFamily;
            }

            if (parent is Control ctrl)
            {
                if (ctrl.FontFamily.Source.Contains("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (ctrl is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button &&
                    button.Content is string content &&
                    content.Any(ch => ch >= '\uE000' && ch <= '\uF8FF'))
                {
                    return;
                }

                ctrl.FontFamily = fontFamily;
            }
            else if (parent is TextBlock tb)
            {
                tb.FontFamily = fontFamily;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                ApplyFontFamilyRecursively(child, fontFamily);
            }
        }
    }
}
