using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsDialogUi
    {
        public static StackPanel CreateSection()
        {
            return new StackPanel { Spacing = 6, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
        }

        public static void AddLabel(StackPanel target, string text)
        {
            target.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        }

        public static string GetActiveThemeName()
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue("ActiveTheme", out object? themeObj) &&
                themeObj is string themeStr)
            {
                return themeStr;
            }
            return string.Empty;
        }

        public static Border CreateCard(string title, UIElement content, string fontIconGlyph)
        {
            var container = new StackPanel { Spacing = 8 };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 2)
            };

            FontIcon? icon = null;
            if (!string.IsNullOrEmpty(fontIconGlyph))
            {
                icon = new FontIcon
                {
                    Glyph = fontIconGlyph,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(icon);
            }

            var titleText = new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(titleText);

            container.Children.Add(headerPanel);
            container.Children.Add(content);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Action updateThemeBrushes = () =>
            {
                string activeTheme = GetActiveThemeName();
                bool isPastel = string.Equals(activeTheme, "PastelDark", StringComparison.OrdinalIgnoreCase);
                bool isLight = !isPastel && card.ActualTheme == ElementTheme.Light;

                if (isPastel)
                {
                    // Pastel Dark (Catppuccin Macchiato)
                    card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 48, 71)); // #2d3047
                    card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 73, 77, 100)); // #494d64
                    titleText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 202, 211, 245)); // #cad3f5
                    if (icon != null) icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 198, 160, 246)); // #c6a0f6 (mauve)
                }
                else if (isLight)
                {
                    // Light Theme
                    card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 249, 250)); // #f8f9fa
                    card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 228, 228, 231)); // #e4e4e7
                    titleText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 27)); // #18181b
                    if (icon != null) icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 99, 235)); // #2563eb
                }
                else
                {
                    // Dark Theme (vs-dark)
                    card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 39, 39, 42)); // #27272a
                    card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 63, 63, 70)); // #3f3f46
                    titleText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 245)); // #f4f4f5
                    if (icon != null) icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 165, 250)); // #60a5fa
                }
            };

            card.Loaded += (_, _) => updateThemeBrushes();
            card.ActualThemeChanged += (_, _) => updateThemeBrushes();

            card.Child = container;
            return card;
        }

        public static Border CreateSubGroup(string title, UIElement content)
        {
            var container = new StackPanel { Spacing = 6 };

            var subHeader = new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 11
            };

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Action updateSubGroupThemeBrushes = () =>
            {
                string activeTheme = GetActiveThemeName();
                bool isPastel = string.Equals(activeTheme, "PastelDark", StringComparison.OrdinalIgnoreCase);
                bool isLight = !isPastel && border.ActualTheme == ElementTheme.Light;

                if (isPastel)
                {
                    // Pastel Dark SubGroup
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 35, 54)); // #212336
                    border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 57, 79)); // #36394f
                    subHeader.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 165, 173, 203)); // #a5adcb
                }
                else if (isLight)
                {
                    // Light SubGroup
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 249)); // #f1f5f9
                    border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240)); // #e2e8f0
                    subHeader.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139)); // #64748b
                }
                else
                {
                    // Dark SubGroup
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 31, 31, 35)); // #1f1f23
                    border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 55)); // #333337
                    subHeader.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 161, 161, 170)); // #a1a1aa
                }
            };

            container.Children.Add(subHeader);
            container.Children.Add(content);

            border.Loaded += (_, _) => updateSubGroupThemeBrushes();
            border.ActualThemeChanged += (_, _) => updateSubGroupThemeBrushes();

            border.Child = container;
            return border;
        }

        public static TextBlock CreateMutedTextBlock(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };

            Action updateMutedColor = () =>
            {
                string activeTheme = GetActiveThemeName();
                bool isPastel = string.Equals(activeTheme, "PastelDark", StringComparison.OrdinalIgnoreCase);
                bool isLight = !isPastel && tb.ActualTheme == ElementTheme.Light;

                if (isPastel)
                {
                    tb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 165, 173, 203)); // #a5adcb
                }
                else if (isLight)
                {
                    tb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139)); // #64748b
                }
                else
                {
                    tb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 161, 161, 170)); // #a1a1aa
                }
            };

            tb.Loaded += (_, _) => updateMutedColor();
            tb.ActualThemeChanged += (_, _) => updateMutedColor();
            return tb;
        }

        public static ComboBox CreateFontComboBox(string currentFontFamily, IReadOnlyList<string> fontFamilies)
        {
            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "폰트 선택"
            };

            string current = string.IsNullOrWhiteSpace(currentFontFamily)
                ? "Consolas"
                : currentFontFamily.Trim();

            if (!fontFamilies.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                comboBox.Items.Add(current);
            }

            foreach (string family in fontFamilies)
            {
                comboBox.Items.Add(family);
            }

            comboBox.SelectedItem = comboBox.Items
                .OfType<string>()
                .FirstOrDefault(item => item.Equals(current, StringComparison.OrdinalIgnoreCase))
                ?? comboBox.Items.OfType<string>().FirstOrDefault();

            return comboBox;
        }

        public static string GetSelectedComboText(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as string)?.Trim() ?? fallback.Trim();
        }

        public static DropDownButton CreateColorDropdown(string title, Windows.UI.Color initialColor, out ColorPicker colorPicker)
        {
            var swatch = new Border
            {
                Width = 120,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 128, 128, 128)),
                Background = new SolidColorBrush(initialColor)
            };

            var picker = new ColorPicker
            {
                Color = initialColor,
                IsAlphaEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsMoreButtonVisible = false
            };
            colorPicker = picker;

            var flyoutContent = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 6,
                Padding = new Thickness(6)
            };
            flyoutContent.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            flyoutContent.Children.Add(picker);

            SettingsDialogStyler.ApplyCompactStyleToLogicalTree(flyoutContent);
            picker.Loaded += (_, __) => SettingsDialogStyler.ApplyCompactStyleToVisualTree(picker);
            picker.ColorChanged += (_, __) => swatch.Background = new SolidColorBrush(picker.Color);

            var flyoutStyle = new Style(typeof(FlyoutPresenter));
            flyoutStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
            flyoutStyle.Setters.Add(new Setter(Control.MinWidthProperty, 360.0));
            flyoutStyle.Setters.Add(new Setter(Control.MaxWidthProperty, 400.0));

            return new DropDownButton
            {
                Content = swatch,
                Flyout = new Flyout
                {
                    Content = flyoutContent,
                    FlyoutPresenterStyle = flyoutStyle
                },
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        public static Windows.UI.Color ResolvePickerColor(string? colorValue, string fallbackHex)
        {
            if (TryParseHexColor(colorValue, out var color) || TryParseHexColor(fallbackHex, out color))
            {
                return color;
            }

            return Windows.UI.Color.FromArgb(255, 0, 0, 0);
        }

        public static string ColorToHex(Windows.UI.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
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
