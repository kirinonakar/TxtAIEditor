using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed class TabEncryptionController
    {
        private readonly Func<string, string, string> _getString;
        private readonly Func<Task<XamlRoot?>> _getDialogXamlRootAsync;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly Action _updateWindowTitle;
        private readonly Action<string, string> _showErrorMessage;

        public TabEncryptionController(
            Func<string, string, string> getString,
            Func<Task<XamlRoot?>> getDialogXamlRootAsync,
            Func<ElementTheme> getCurrentElementTheme,
            Action updateWindowTitle,
            Action<string, string> showErrorMessage)
        {
            _getString = getString;
            _getDialogXamlRootAsync = getDialogXamlRootAsync;
            _getCurrentElementTheme = getCurrentElementTheme;
            _updateWindowTitle = updateWindowTitle;
            _showErrorMessage = showErrorMessage;
        }

        public void ShowMenu(OpenedTab tab, FrameworkElement target, RightTappedRoutedEventArgs args)
        {
            args.Handled = true;

            var menu = new MenuFlyout();

            if (tab.IsEncrypted)
            {
                var changePasswordItem = new MenuFlyoutItem { Text = _getString("TabMenuChangeEncryptionPassword", "암호 변경") };
                changePasswordItem.Click += async (_, __) => await ChangePasswordAsync(tab);
                menu.Items.Add(changePasswordItem);

                var removeEncryptionItem = new MenuFlyoutItem { Text = _getString("TabMenuRemoveEncryption", "암호 해제") };
                removeEncryptionItem.Click += async (_, __) => await RemoveEncryptionAsync(tab);
                menu.Items.Add(removeEncryptionItem);
            }
            else
            {
                var encryptItem = new MenuFlyoutItem { Text = _getString("TabMenuEncrypt", "암호화") };
                encryptItem.Click += async (_, __) => await EncryptAsync(tab);
                menu.Items.Add(encryptItem);
            }

            CursorResetHelper.AttachToFlyout(menu, target);
            CursorResetHelper.ResetToArrow(target);

            menu.ShowAt(target, new FlyoutShowOptions
            {
                Position = args.GetPosition(target)
            });
            CursorResetHelper.ResetToArrow(target);
        }

        public async Task EncryptAsync(OpenedTab tab)
        {
            if (tab.IsEncrypted)
            {
                await ChangePasswordAsync(tab);
                return;
            }

            string? password = await PromptConfirmedPasswordAsync(
                _getString("EncryptionSetPasswordTitle", "암호화"),
                _getString("EncryptionPasswordLabel", "암호"),
                _getString("EncryptionConfirmPasswordLabel", "암호 확인"));
            if (password == null)
            {
                return;
            }

            tab.EncryptionPassword = password;
            tab.IsEncrypted = true;
            tab.IsDirty = true;
            _updateWindowTitle();
        }

        public async Task ChangePasswordAsync(OpenedTab tab)
        {
            string? password = await PromptConfirmedPasswordAsync(
                _getString("EncryptionChangePasswordTitle", "암호 변경"),
                _getString("EncryptionPasswordLabel", "새 암호"),
                _getString("EncryptionConfirmPasswordLabel", "새 암호 확인"));
            if (password == null)
            {
                return;
            }

            tab.EncryptionPassword = password;
            tab.IsEncrypted = true;
            tab.IsDirty = true;
            _updateWindowTitle();
        }

        public async Task RemoveEncryptionAsync(OpenedTab tab)
        {
            if (!tab.IsEncrypted)
            {
                return;
            }

            string? password = await PromptConfirmedPasswordAsync(
                _getString("EncryptionRemoveTitle", "암호 해제"),
                _getString("EncryptionCurrentPasswordLabel", "현재 암호"),
                _getString("EncryptionConfirmPasswordLabel", "현재 암호 확인"));
            if (password == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(tab.EncryptionPassword) &&
                !string.Equals(tab.EncryptionPassword, password, StringComparison.Ordinal))
            {
                _showErrorMessage(
                    _getString("EncryptionRemoveTitle", "암호 해제"),
                    _getString("EncryptionCurrentPasswordMismatch", "현재 암호가 올바르지 않습니다."));
                return;
            }

            tab.EncryptionPassword = null;
            tab.IsEncrypted = false;
            tab.IsDirty = true;
            _updateWindowTitle();
        }

        public void ForgetPassword(OpenedTab tab)
        {
            tab.EncryptionPassword = null;
        }

        public async Task<string?> PromptPasswordAsync(string title, string primaryButtonText)
        {
            XamlRoot? xamlRoot = await _getDialogXamlRootAsync();
            if (xamlRoot == null)
            {
                throw new InvalidOperationException(_getString("EncryptionDialogNotReady", "암호 입력 창을 준비할 수 없습니다. 잠시 후 다시 시도해 주세요."));
            }

            var panel = new StackPanel
            {
                Spacing = 8,
                Width = 360,
                RequestedTheme = _getCurrentElementTheme()
            };
            var passwordBox = new PasswordBox
            {
                PasswordChar = "\u25CF",
                PlaceholderText = _getString("EncryptionPasswordLabel", "암호"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var errorText = CreateErrorTextBlock();
            panel.Children.Add(passwordBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = _getString("EncryptionCancelButton", "취소"),
                XamlRoot = xamlRoot,
                RequestedTheme = _getCurrentElementTheme()
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(passwordBox.Password))
                {
                    args.Cancel = true;
                    errorText.Text = _getString("EncryptionPasswordEmpty", "암호를 입력해 주세요.");
                    errorText.Visibility = Visibility.Visible;
                }
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? passwordBox.Password
                : null;
        }

        private async Task<string?> PromptConfirmedPasswordAsync(string title, string passwordLabel, string confirmLabel)
        {
            XamlRoot? xamlRoot = await _getDialogXamlRootAsync();
            if (xamlRoot == null)
            {
                throw new InvalidOperationException(_getString("EncryptionDialogNotReady", "암호 입력 창을 준비할 수 없습니다. 잠시 후 다시 시도해 주세요."));
            }

            var panel = new StackPanel
            {
                Spacing = 8,
                Width = 360,
                RequestedTheme = _getCurrentElementTheme()
            };
            var passwordBox = new PasswordBox
            {
                PasswordChar = "\u25CF",
                PlaceholderText = passwordLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var confirmBox = new PasswordBox
            {
                PasswordChar = "\u25CF",
                PlaceholderText = confirmLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var errorText = CreateErrorTextBlock();

            panel.Children.Add(new TextBlock { Text = passwordLabel });
            panel.Children.Add(passwordBox);
            panel.Children.Add(new TextBlock { Text = confirmLabel });
            panel.Children.Add(confirmBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = _getString("EncryptionApplyButton", "적용"),
                CloseButtonText = _getString("EncryptionCancelButton", "취소"),
                XamlRoot = xamlRoot,
                RequestedTheme = _getCurrentElementTheme()
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(passwordBox.Password))
                {
                    args.Cancel = true;
                    errorText.Text = _getString("EncryptionPasswordEmpty", "암호를 입력해 주세요.");
                    errorText.Visibility = Visibility.Visible;
                    return;
                }

                if (!string.Equals(passwordBox.Password, confirmBox.Password, StringComparison.Ordinal))
                {
                    args.Cancel = true;
                    errorText.Text = _getString("EncryptionPasswordMismatch", "입력한 암호가 일치하지 않습니다.");
                    errorText.Visibility = Visibility.Visible;
                }
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? passwordBox.Password
                : null;
        }

        private static TextBlock CreateErrorTextBlock()
        {
            return new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 60, 60)),
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }
}
