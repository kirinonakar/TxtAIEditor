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

            if (settings.Theme == "PastelDark")
            {
                ApplyPastelDarkTheme();
                rootElement.RequestedTheme = ElementTheme.Light;
                rootElement.RequestedTheme = ElementTheme.Dark;
            }
            else
            {
                ClearCustomThemeOverrides();
                if (settings.Theme == "Light")
                {
                    rootElement.RequestedTheme = ElementTheme.Dark;
                    rootElement.RequestedTheme = ElementTheme.Light;
                }
                else
                {
                    rootElement.RequestedTheme = ElementTheme.Light;
                    rootElement.RequestedTheme = ElementTheme.Dark;
                }
            }

            ApplyTitleBarTheme(settings, appWindow);
            ApplyMarkdownToolbarTheme(settings, applyMarkdownToolbarBackground);
            ApplyShellFont(settings, rootElement);
            ApplyRootBackground(settings, rootElement);
        }

        private static void ApplyPastelDarkTheme()
        {
            var resources = Application.Current.Resources;
            resources["ActiveTheme"] = "PastelDark";

            // Pastel Dark Palette
            var baseColor = Windows.UI.Color.FromArgb(255, 36, 39, 58); // #24273a
            var mantleColor = Windows.UI.Color.FromArgb(255, 30, 32, 48); // #1e2030
            var crustColor = Windows.UI.Color.FromArgb(255, 24, 25, 38); // #181926
            var surface0Color = Windows.UI.Color.FromArgb(255, 54, 57, 79); // #36394f
            var surface1Color = Windows.UI.Color.FromArgb(255, 73, 77, 100); // #494d64
            var surface2Color = Windows.UI.Color.FromArgb(255, 91, 96, 120); // #5b6078
            var textColor = Windows.UI.Color.FromArgb(255, 202, 211, 245); // #cad3f5
            var subtext0Color = Windows.UI.Color.FromArgb(255, 165, 173, 203); // #a5adcb
            var subtext1Color = Windows.UI.Color.FromArgb(255, 184, 192, 224); // #b8c0e0
            var mauveColor = Windows.UI.Color.FromArgb(255, 198, 160, 246); // #c6a0f6
            var redColor = Windows.UI.Color.FromArgb(255, 237, 135, 150); // #ed8796
            var greenColor = Windows.UI.Color.FromArgb(255, 166, 218, 149); // #a6da95

            void SetBrush(string key, Windows.UI.Color color)
            {
                resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }

            // Custom application brushes
            SetBrush("TitleBarBackgroundBrush", mantleColor);
            SetBrush("TitleBarForegroundBrush", textColor);
            SetBrush("ToolbarBackgroundBrush", baseColor);
            SetBrush("ToolbarBorderBrush", surface0Color);
            SetBrush("SidebarBackgroundBrush", mantleColor);
            SetBrush("ActivityBarBackgroundBrush", crustColor);
            SetBrush("SidebarBorderBrush", surface0Color);
            SetBrush("StatusBarBackgroundBrush", mantleColor);
            SetBrush("StatusBarForegroundBrush", textColor);
            SetBrush("SplitterBackgroundBrush", surface1Color);
            SetBrush("SplitterHoverBackgroundBrush", mauveColor);
            SetBrush("EditorSurfaceBackgroundBrush", baseColor);
            SetBrush("TabBarBackgroundBrush", mantleColor);

            // AI Assistant & Agent panel brushes (Pastel Dark)
            SetBrush("LlmOutputBackground", crustColor);
            SetBrush("LlmOutputForeground", textColor);
            SetBrush("ButtonBackground", surface0Color);
            SetBrush("ButtonBackgroundPointerOver", surface1Color);
            SetBrush("ButtonBackgroundPressed", surface2Color);
            SetBrush("ButtonBorderBrush", surface0Color);
            SetBrush("ButtonBorderBrushPointerOver", surface1Color);
            SetBrush("ButtonBorderBrushPressed", surface2Color);

            SetBrush("AgentButtonBackground", surface0Color);
            SetBrush("AgentButtonBorderBrush", surface0Color);
            SetBrush("AgentOutputBackground", crustColor);
            SetBrush("AgentOutputForeground", textColor);
            SetBrush("AgentCodeBackground", surface0Color);
            SetBrush("AgentCodeForeground", mauveColor);

            // TabView & TabViewItem overrides
            SetBrush("TabViewTabStripBackground", mantleColor);
            SetBrush("TabViewItemHeaderBackground", mantleColor);
            SetBrush("TabViewItemHeaderForeground", subtext0Color);
            SetBrush("TabViewItemHeaderBackgroundPointerOver", surface0Color);
            SetBrush("TabViewItemHeaderForegroundPointerOver", textColor);
            SetBrush("TabViewItemHeaderBackgroundPressed", surface1Color);
            SetBrush("TabViewItemHeaderForegroundPressed", textColor);
            SetBrush("TabViewItemHeaderBackgroundSelected", baseColor);
            SetBrush("TabViewItemHeaderForegroundSelected", mauveColor);
            SetBrush("TabViewItemHeaderBackgroundSelectedPointerOver", surface0Color);
            SetBrush("TabViewItemHeaderBackgroundSelectedPressed", surface1Color);
            SetBrush("TabViewItemHeaderBorderBrush", Microsoft.UI.Colors.Transparent);
            SetBrush("TabViewBorderBrush", surface0Color);

            // Popups, Menus & Dialogs overrides
            SetBrush("ContentDialogBackground", mantleColor);
            SetBrush("ContentDialogBorderBrush", surface0Color);
            SetBrush("ContentDialogForeground", textColor);

            SetBrush("FlyoutPresenterBackground", mantleColor);
            SetBrush("FlyoutPresenterBorderBrush", surface0Color);
            SetBrush("MenuFlyoutPresenterBackground", mantleColor);
            SetBrush("MenuFlyoutPresenterBorderBrush", surface0Color);
            SetBrush("MenuFlyoutItemForeground", textColor);
            SetBrush("MenuFlyoutItemForegroundPointerOver", mauveColor);
            SetBrush("MenuFlyoutItemForegroundPressed", mauveColor);
            SetBrush("MenuFlyoutItemBackgroundPointerOver", surface0Color);
            SetBrush("MenuFlyoutItemBackgroundPressed", surface1Color);
            SetBrush("MenuFlyoutItemKeyboardAcceleratorTextForeground", subtext0Color);

            // Lists & NavigationView
            SetBrush("ListViewItemBackgroundPointerOver", surface0Color);
            SetBrush("ListViewItemBackgroundSelected", surface1Color);
            SetBrush("ListViewItemBackgroundSelectedPointerOver", surface2Color);
            SetBrush("ListViewItemForegroundPointerOver", textColor);
            SetBrush("ListViewItemForegroundSelected", mauveColor);

            SetBrush("NavigationViewContentBackground", baseColor);
            SetBrush("NavigationViewDefaultPaneBackground", mantleColor);
            SetBrush("NavigationViewTopPaneBackground", mantleColor);

            // Interactive Controls inside Dialogs/Settings
            SetBrush("TextBoxBackground", baseColor);
            SetBrush("TextBoxBackgroundPointerOver", surface0Color);
            SetBrush("TextBoxBackgroundFocused", baseColor);
            SetBrush("TextBoxBorderBrush", surface1Color);
            SetBrush("TextBoxBorderBrushFocused", mauveColor);
            SetBrush("TextBoxForeground", textColor);
            SetBrush("TextBoxForegroundFocused", textColor);

            SetBrush("ComboBoxBackground", baseColor);
            SetBrush("ComboBoxBackgroundPointerOver", surface0Color);
            SetBrush("ComboBoxBackgroundPressed", surface1Color);
            SetBrush("ComboBoxBorderBrush", surface1Color);
            SetBrush("ComboBoxBorderBrushFocused", mauveColor);
            SetBrush("ComboBoxForeground", textColor);
            SetBrush("ComboBoxForegroundPointerOver", textColor);
            SetBrush("ComboBoxDropDownBackground", mantleColor);
            SetBrush("ComboBoxDropDownBorderBrush", surface0Color);
            SetBrush("ComboBoxDropDownItemBackground", Windows.UI.Color.FromArgb(0, 0, 0, 0));
            SetBrush("ComboBoxDropDownItemBackgroundPointerOver", surface0Color);
            SetBrush("ComboBoxDropDownItemBackgroundSelected", surface1Color);
            SetBrush("ComboBoxDropDownItemBackgroundSelectedPointerOver", surface2Color);
            SetBrush("ComboBoxDropDownItemForeground", textColor);
            SetBrush("ComboBoxDropDownItemForegroundPointerOver", textColor);
            SetBrush("ComboBoxDropDownItemForegroundSelected", mauveColor);

            SetBrush("ButtonBackground", surface0Color);
            SetBrush("ButtonBackgroundPointerOver", surface1Color);
            SetBrush("ButtonBackgroundPressed", surface2Color);
            SetBrush("ButtonForeground", textColor);
            SetBrush("ButtonForegroundPointerOver", textColor);
            SetBrush("ButtonForegroundPressed", textColor);
            SetBrush("ButtonBorderBrush", surface1Color);
            SetBrush("ButtonBorderBrushPointerOver", surface2Color);

            SetBrush("CheckBoxForeground", textColor);
            SetBrush("CheckBoxForegroundPointerOver", textColor);
            SetBrush("CheckBoxForegroundPressed", textColor);

            // Slider: filled (left) uses accent (mauve); unfilled track (right) stays neutral
            SetBrush("SliderTrackFill", surface1Color);
            SetBrush("SliderTrackFillPointerOver", surface2Color);
            SetBrush("SliderTrackFillPressed", surface2Color);
            SetBrush("SliderThumbBackground", mauveColor);
            SetBrush("SliderThumbBackgroundPointerOver", mauveColor);
            SetBrush("SliderThumbBackgroundPressed", mauveColor);

            // System Control chrome backgrounds
            SetBrush("SystemControlBackgroundChromeMediumLowBrush", mantleColor);
            SetBrush("SystemControlBackgroundChromeMediumBrush", baseColor);
            SetBrush("SystemControlBackgroundAltHighBrush", baseColor);
            SetBrush("SystemControlBackgroundBaseLowBrush", surface0Color);
            SetBrush("SystemControlBackgroundBaseMediumBrush", subtext0Color);
            SetBrush("SystemControlBackgroundBaseMediumLowBrush", surface1Color);
            SetBrush("SystemControlBackgroundListLowBrush", surface0Color);
            SetBrush("SystemControlBackgroundListMediumBrush", surface1Color);
            SetBrush("SystemControlForegroundBaseHighBrush", textColor);
            SetBrush("SystemControlForegroundBaseMediumBrush", subtext0Color);

            // WinUI Accent Color Overrides
            SetBrush("SystemAccentColor", mauveColor);
            SetBrush("SystemControlHighlightAccentBrush", mauveColor);
            SetBrush("SystemControlForegroundAccentBrush", mauveColor);
            SetBrush("SystemControlHighlightListAccentLowBrush", surface0Color);
            SetBrush("SystemControlHighlightListAccentMediumBrush", surface1Color);
        }

        private static void ClearCustomThemeOverrides()
        {
            var resources = Application.Current.Resources;
            var keysToRemove = new[]
            {
                "ActiveTheme",
                "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
                "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
                "LlmOutputBackground", "LlmOutputForeground",
                "AgentButtonBackground", "AgentButtonBorderBrush",
                "AgentOutputBackground", "AgentOutputForeground",
                "AgentCodeBackground", "AgentCodeForeground",
                "TitleBarBackgroundBrush", "TitleBarForegroundBrush", "ToolbarBackgroundBrush", "ToolbarBorderBrush",
                "SidebarBackgroundBrush", "ActivityBarBackgroundBrush", "SidebarBorderBrush", "StatusBarBackgroundBrush",
                "StatusBarForegroundBrush", "SplitterBackgroundBrush", "SplitterHoverBackgroundBrush", "EditorSurfaceBackgroundBrush",
                "TabBarBackgroundBrush", "TabViewTabStripBackground", "TabViewItemHeaderBackground", "TabViewItemHeaderForeground",
                "TabViewItemHeaderBackgroundPointerOver", "TabViewItemHeaderForegroundPointerOver", "TabViewItemHeaderBackgroundPressed",
                "TabViewItemHeaderForegroundPressed", "TabViewItemHeaderBackgroundSelected", "TabViewItemHeaderForegroundSelected",
                "TabViewItemHeaderBackgroundSelectedPointerOver", "TabViewItemHeaderBackgroundSelectedPressed",
                "TabViewItemHeaderBorderBrush", "TabViewBorderBrush",
                "ContentDialogBackground", "ContentDialogBorderBrush", "ContentDialogForeground",
                "FlyoutPresenterBackground", "FlyoutPresenterBorderBrush", "MenuFlyoutPresenterBackground", "MenuFlyoutPresenterBorderBrush",
                "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed",
                "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "MenuFlyoutItemKeyboardAcceleratorTextForeground",
                "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver",
                "ListViewItemForegroundPointerOver", "ListViewItemForegroundSelected",
                "NavigationViewContentBackground", "NavigationViewDefaultPaneBackground", "NavigationViewTopPaneBackground",
                "TextBoxBackground", "TextBoxBackgroundPointerOver", "TextBoxBackgroundFocused", "TextBoxBorderBrush", "TextBoxBorderBrushFocused", "TextBoxForeground", "TextBoxForegroundFocused",
                "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBorderBrush", "ComboBoxBorderBrushFocused", "ComboBoxForeground", "ComboBoxForegroundPointerOver",
                "ComboBoxDropDownBackground", "ComboBoxDropDownBorderBrush", "ComboBoxDropDownItemBackground", "ComboBoxDropDownItemBackgroundPointerOver",
                "ComboBoxDropDownItemBackgroundSelected", "ComboBoxDropDownItemBackgroundSelectedPointerOver", "ComboBoxDropDownItemForeground", "ComboBoxDropDownItemForegroundPointerOver",
                "ComboBoxDropDownItemForegroundSelected",
                "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonBorderBrush", "ButtonBorderBrushPointerOver",
                "CheckBoxForeground", "CheckBoxForegroundPointerOver", "CheckBoxForegroundPressed",
                "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed",
                "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                "SystemControlBackgroundChromeMediumLowBrush", "SystemControlBackgroundChromeMediumBrush", "SystemControlBackgroundAltHighBrush",
                "SystemControlBackgroundBaseLowBrush", "SystemControlBackgroundBaseMediumBrush", "SystemControlBackgroundBaseMediumLowBrush",
                "SystemControlBackgroundListLowBrush", "SystemControlBackgroundListMediumBrush", "SystemControlForegroundBaseHighBrush",
                "SystemControlForegroundBaseMediumBrush",
                "SystemAccentColor", "SystemControlHighlightAccentBrush", "SystemControlForegroundAccentBrush",
                "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush"
            };

            foreach (var key in keysToRemove)
            {
                resources.Remove(key);
            }
        }

        private static void ApplyTitleBarTheme(EditorSettings settings, AppWindow appWindow)
        {
            try
            {
                var titleBar = appWindow.TitleBar;
                bool light = settings.Theme == "Light";
                bool pastelDark = settings.Theme == "PastelDark";
 
                Windows.UI.Color background = TryParseHexColor(settings.CustomBackgroundColor, out var customBg)
                    ? customBg
                    : (pastelDark
                        ? Windows.UI.Color.FromArgb(255, 30, 32, 48)
                        : (light ? Windows.UI.Color.FromArgb(255, 243, 244, 246) : Windows.UI.Color.FromArgb(255, 30, 30, 30)));
                Windows.UI.Color foreground = TryParseHexColor(settings.CustomForegroundColor, out var customFg)
                    ? customFg
                    : (pastelDark
                        ? Windows.UI.Color.FromArgb(255, 202, 211, 245)
                        : (light ? Windows.UI.Color.FromArgb(255, 31, 41, 55) : Windows.UI.Color.FromArgb(255, 212, 212, 212)));
                Windows.UI.Color inactiveBackground = pastelDark
                    ? Windows.UI.Color.FromArgb(255, 54, 57, 79)
                    : (light ? Windows.UI.Color.FromArgb(255, 229, 231, 235) : Windows.UI.Color.FromArgb(255, 45, 49, 57));
                Windows.UI.Color hoverBackground = pastelDark
                    ? Windows.UI.Color.FromArgb(255, 54, 57, 79)
                    : (light ? Windows.UI.Color.FromArgb(255, 229, 231, 235) : Windows.UI.Color.FromArgb(255, 45, 49, 57));

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
                    : (settings.Theme == "PastelDark"
                        ? Windows.UI.Color.FromArgb(255, 30, 32, 48)
                        : (settings.Theme == "Light"
                            ? Windows.UI.Color.FromArgb(255, 243, 244, 246)
                            : Windows.UI.Color.FromArgb(255, 43, 47, 54)));
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
