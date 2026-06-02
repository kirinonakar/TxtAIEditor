using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Core.Services
{
    public enum UnsavedChangesDialogResult
    {
        Cancel,
        Discard,
        Save
    }

    public sealed class UnsavedChangesDialogService
    {
        public bool IsShowing { get; private set; }

        public async Task<UnsavedChangesDialogResult> ShowAsync(
            string title,
            string message,
            string discardButtonText,
            string saveButtonText,
            string cancelButtonText,
            XamlRoot xamlRoot,
            ElementTheme theme)
        {
            if (IsShowing)
            {
                return UnsavedChangesDialogResult.Cancel;
            }

            IsShowing = true;
            try
            {
                var result = UnsavedChangesDialogResult.Cancel;
                var dialog = new ContentDialog
                {
                    Title = title,
                    RequestedTheme = theme,
                    XamlRoot = xamlRoot
                };

                dialog.Content = CreateDialogContent(
                    message,
                    discardButtonText,
                    saveButtonText,
                    cancelButtonText,
                    theme,
                    () =>
                    {
                        result = UnsavedChangesDialogResult.Discard;
                        dialog.Hide();
                    },
                    () =>
                    {
                        result = UnsavedChangesDialogResult.Save;
                        dialog.Hide();
                    },
                    () =>
                    {
                        result = UnsavedChangesDialogResult.Cancel;
                        dialog.Hide();
                    },
                    out var defaultButton);

                dialog.Opened += (_, __) =>
                {
                    defaultButton.DispatcherQueue.TryEnqueue(() =>
                    {
                        defaultButton.Focus(FocusState.Programmatic);
                    });
                };

                await dialog.ShowAsync();
                return result;
            }
            finally
            {
                IsShowing = false;
            }
        }

        private static FrameworkElement CreateDialogContent(
            string message,
            string discardButtonText,
            string saveButtonText,
            string cancelButtonText,
            ElementTheme theme,
            Action discardAction,
            Action saveAction,
            Action cancelAction,
            out Button defaultButton)
        {
            var root = new StackPanel
            {
                Spacing = 22,
                MinWidth = 360,
                MaxWidth = 520
            };

            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            });

            var rightButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var discardButton = CreateSolidDialogButton(discardButtonText + " (N)", DialogButtonVisual.Destructive, theme);
            discardButton.Click += (_, __) => discardAction();
            discardButton.TabIndex = 1;

            var cancelButton = new Button
            {
                Content = cancelButtonText,
                MinWidth = 90,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(4),
                RequestedTheme = theme,
                TabIndex = 2
            };
            cancelButton.Click += (_, __) => cancelAction();

            var saveButton = CreateSolidDialogButton(saveButtonText + " (Y)", DialogButtonVisual.Accent, theme);
            saveButton.Click += (_, __) => saveAction();
            saveButton.TabIndex = 0;
            defaultButton = saveButton;

            rightButtons.Children.Add(saveButton);
            rightButtons.Children.Add(discardButton);
            rightButtons.Children.Add(cancelButton);

            root.Children.Add(rightButtons);
            root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    cancelAction();
                }
                else if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Y)
                {
                    e.Handled = true;
                    saveAction();
                }
                else if (e.Key == Windows.System.VirtualKey.N)
                {
                    e.Handled = true;
                    discardAction();
                }
            };

            return root;
        }

        private enum DialogButtonVisual
        {
            Destructive,
            Accent
        }

        private static Button CreateSolidDialogButton(string text, DialogButtonVisual visual, ElementTheme theme)
        {
            bool dark = theme == ElementTheme.Dark;
            Windows.UI.Color normalColor;
            Windows.UI.Color hoverColor;
            Windows.UI.Color pressedColor;

            if (visual == DialogButtonVisual.Destructive)
            {
                normalColor = dark
                    ? Windows.UI.Color.FromArgb(255, 179, 38, 30)
                    : Windows.UI.Color.FromArgb(255, 196, 43, 28);
                hoverColor = Windows.UI.Color.FromArgb(255, 209, 52, 56);
                pressedColor = dark
                    ? Windows.UI.Color.FromArgb(255, 143, 29, 24)
                    : Windows.UI.Color.FromArgb(255, 168, 0, 0);
            }
            else
            {
                normalColor = dark
                    ? Windows.UI.Color.FromArgb(255, 96, 178, 255)
                    : Windows.UI.Color.FromArgb(255, 0, 95, 184);
                hoverColor = dark
                    ? Windows.UI.Color.FromArgb(255, 117, 188, 255)
                    : Windows.UI.Color.FromArgb(255, 0, 103, 192);
                pressedColor = dark
                    ? Windows.UI.Color.FromArgb(255, 64, 152, 232)
                    : Windows.UI.Color.FromArgb(255, 0, 74, 152);
            }

            var button = new Button
            {
                Content = text,
                MinWidth = 90,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(4),
                RequestedTheme = theme
            };

            SetSolidButtonResources(button, normalColor, hoverColor, pressedColor);
            ApplySolidButtonColors(button, normalColor);
            button.PointerEntered += (_, __) => ApplySolidButtonColors(button, hoverColor);
            button.PointerExited += (_, __) => ApplySolidButtonColors(button, normalColor);
            button.PointerPressed += (_, __) => ApplySolidButtonColors(button, pressedColor);
            button.PointerReleased += (_, __) => ApplySolidButtonColors(button, hoverColor);
            return button;
        }

        private static void SetSolidButtonResources(
            Button button,
            Windows.UI.Color normalColor,
            Windows.UI.Color hoverColor,
            Windows.UI.Color pressedColor)
        {
            button.Resources["ButtonBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(normalColor);
            button.Resources["ButtonForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            button.Resources["ButtonBorderBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(normalColor);
            button.Resources["ButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(hoverColor);
            button.Resources["ButtonForegroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            button.Resources["ButtonBorderBrushPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(hoverColor);
            button.Resources["ButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(pressedColor);
            button.Resources["ButtonForegroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            button.Resources["ButtonBorderBrushPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(pressedColor);
        }

        private static void ApplySolidButtonColors(Button button, Windows.UI.Color color)
        {
            void Apply()
            {
                var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
                button.Background = brush;
                button.BorderBrush = brush;
                button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            }

            Apply();
            button.DispatcherQueue.TryEnqueue(Apply);
        }
    }
}
