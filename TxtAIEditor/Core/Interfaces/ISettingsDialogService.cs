using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public sealed class SettingsDialogResult
    {
        public bool Saved { get; set; }
        public bool SettingsImported { get; set; }
        public string ApiKeyStatusMessage { get; set; } = string.Empty;
    }

    public interface ISettingsDialogService
    {
        Task<SettingsDialogResult> ShowAsync(
            EditorSettings settings,
            XamlRoot xamlRoot,
            Microsoft.UI.WindowId pickerWindowId,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow = null,
            string? initialTab = null,
            Action<string, string>? openTextInEditor = null);
    }
}
