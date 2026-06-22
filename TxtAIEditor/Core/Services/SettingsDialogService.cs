using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class SettingsDialogService : ISettingsDialogService
    {
        private readonly ILLMService _llmService;

        public SettingsDialogService(ILLMService llmService)
        {
            _llmService = llmService;
        }

        public async Task<SettingsDialogResult> ShowAsync(
            EditorSettings settings,
            XamlRoot xamlRoot,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow = null,
            string? initialTab = null)
        {
            var dialogView = await SettingsDialogView.CreateAsync(
                settings,
                _llmService,
                getString,
                initializePickerWindow,
                initialTab);

            SettingsDialogStyler.ApplyCompactStyleToLogicalTree(dialogView);
            dialogView.RestoreCustomFontSizes();

            bool isDarkTheme = xamlRoot.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
            bool settingsImported = false;
            var dialog = new ContentDialog
            {
                Title = getString("SettingsTitle", "TxtAIEditor 설정"),
                Content = dialogView,
                PrimaryButtonText = getString("SettingsSave", "적용 및 저장"),
                CloseButtonText = getString("SettingsCancel", "취소"),
                XamlRoot = xamlRoot,
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };
            dialogView.SettingsImported += (_, _) =>
            {
                settingsImported = true;
                dialog.Hide();
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return new SettingsDialogResult { Saved = false, SettingsImported = settingsImported };
            }

            dialogView.ApplyToSettings(settings);
            await dialogView.SaveSecretsAsync(settings);

            return new SettingsDialogResult
            {
                Saved = true,
                SettingsImported = settingsImported,
                ApiKeyStatusMessage = dialogView.CreateApiKeyStatusMessage(settings)
            };
        }
    }
}
