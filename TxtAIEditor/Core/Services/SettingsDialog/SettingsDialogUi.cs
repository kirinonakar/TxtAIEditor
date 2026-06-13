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
