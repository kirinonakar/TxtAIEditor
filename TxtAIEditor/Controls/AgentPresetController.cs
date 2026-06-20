using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPreset
    {
        public string Name { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    internal sealed class AgentPresetController
    {
        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly string _presetsFilePath;
        private readonly List<AgentPreset> _presets = new();
        private readonly HashSet<string> _selectedPresetNames = new(StringComparer.OrdinalIgnoreCase);

        public AgentPresetController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action contextChanged,
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _contextChanged = contextChanged;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _presetsFilePath = Path.Combine(settingsDir, "agent-presets.json");
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_presetsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_presetsFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentPreset>>(json);
                    if (loaded != null)
                    {
                        _presets.Clear();
                        _presets.AddRange(loaded.Where(p => !string.IsNullOrWhiteSpace(p.Name)));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load agent presets: {ex.Message}");
            }

            UpdateUI();
        }

        public string BuildInstructionDisplay(string userInstruction)
        {
            string presetLabel = GetSelectedPresetLabel();
            if (string.IsNullOrEmpty(presetLabel))
            {
                return userInstruction;
            }

            if (string.IsNullOrWhiteSpace(userInstruction))
            {
                return $"[{presetLabel}]";
            }

            return $"[{presetLabel}] {userInstruction}";
        }

        public string BuildAgentInstruction(string userInstruction)
        {
            string presetSection = BuildSelectedPresetSection();
            if (string.IsNullOrWhiteSpace(presetSection))
            {
                return userInstruction;
            }

            var builder = new StringBuilder();
            builder.AppendLine(presetSection);

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                builder.AppendLine();
                builder.AppendLine("[User request]");
                builder.Append(userInstruction);
            }

            return builder.ToString().Trim();
        }

        public string BuildSelectedPresetSection()
        {
            var selectedPresets = GetSelectedPresets();
            if (selectedPresets.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[Agent persona/instruction presets]");
            foreach (var preset in selectedPresets)
            {
                builder.AppendLine($"## {preset.Name}");
                builder.AppendLine(preset.Content);
                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        public string GetSelectedPresetLabel()
        {
            return string.Join(", ", GetSelectedPresets().Select(p => p.Name));
        }

        public async Task AddPresetAsync()
        {
            var nameBox = CreateNameBox();
            var contentBox = CreateContentBox();
            var stack = CreateDialogContent(nameBox, contentBox);

            var dialog = new ContentDialog
            {
                Title = _getString("AgentPresetAddText", "프리셋 추가"),
                Content = stack,
                PrimaryButtonText = _getString("AgentPresetSaveAddButton", "추가"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string presetName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(presetName))
            {
                _showError(_getString("AgentPresetNameEmptyTitle", "프리셋 추가 오류"), _getString("AgentPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            var existing = FindPreset(presetName);
            if (existing != null && !await ConfirmOverwriteAsync())
            {
                return;
            }

            if (existing != null)
            {
                _presets.Remove(existing);
            }

            _presets.Add(new AgentPreset
            {
                Name = presetName,
                Content = NormalizePresetContent(contentBox.Text)
            });
            await SaveAsync();
        }

        public async Task ExportPresetsAsync()
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "agent-presets.json"
            };
            _initializePickerWindow(picker);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetExportErrorTitle", "프리셋 내보내기 오류"),
                    string.Format(_getString("PresetExportErrorMessage", "프리셋을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task ImportPresetsAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(file.Path);
                var imported = JsonSerializer.Deserialize<List<AgentPreset>>(json);
                if (imported == null)
                {
                    throw new InvalidDataException(_getString("PresetImportInvalidFile", "가져올 수 있는 프리셋 JSON이 아닙니다."));
                }

                foreach (var item in imported.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    string name = item.Name.Trim();
                    string content = NormalizePresetContent(item.Content);
                    var existing = FindPreset(name);
                    if (existing != null)
                    {
                        bool wasSelected = _selectedPresetNames.Remove(existing.Name);
                        existing.Name = name;
                        existing.Content = content;
                        if (wasSelected)
                        {
                            _selectedPresetNames.Add(existing.Name);
                        }
                    }
                    else
                    {
                        _presets.Add(new AgentPreset
                        {
                            Name = name,
                            Content = content
                        });
                    }
                }

                await SaveAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetImportErrorTitle", "프리셋 가져오기 오류"),
                    string.Format(_getString("PresetImportErrorMessage", "프리셋을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task EditPresetAsync(string presetName)
        {
            var preset = FindPreset(presetName);
            if (preset == null)
            {
                return;
            }

            var nameBox = CreateNameBox(preset.Name);
            var contentBox = CreateContentBox(preset.Content);
            var stack = CreateDialogContent(nameBox, contentBox);

            var dialog = new ContentDialog
            {
                Title = _getString("AgentPresetEditTitle", "프리셋 수정"),
                Content = stack,
                PrimaryButtonText = _getString("AgentPresetEditSaveButton", "저장"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                _showError(_getString("AgentPresetNameEmptyTitle", "프리셋 수정 오류"), _getString("AgentPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            if (!newName.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            {
                var existing = FindPreset(newName);
                if (existing != null)
                {
                    if (!await ConfirmOverwriteAsync())
                    {
                        return;
                    }

                    _presets.Remove(existing);
                    _selectedPresetNames.Remove(existing.Name);
                }
            }

            bool wasSelected = _selectedPresetNames.Remove(preset.Name);
            preset.Name = newName;
            preset.Content = NormalizePresetContent(contentBox.Text);
            if (wasSelected)
            {
                _selectedPresetNames.Add(preset.Name);
            }

            await SaveAsync();
        }

        public async Task DeletePresetAsync(string presetName)
        {
            var preset = FindPreset(presetName);
            if (preset == null)
            {
                return;
            }

            _presets.Remove(preset);
            _selectedPresetNames.Remove(preset.Name);
            await SaveAsync();
        }

        public void TogglePreset(string presetName)
        {
            if (FindPreset(presetName) == null)
            {
                return;
            }

            if (!_selectedPresetNames.Add(presetName))
            {
                _selectedPresetNames.Remove(presetName);
            }

            UpdateUI();
        }

        public void RemoveSelectedPreset(string presetName)
        {
            _selectedPresetNames.Remove(presetName);
            UpdateUI();
        }

        private List<AgentPreset> GetSelectedPresets()
        {
            return _presets
                .Where(p => _selectedPresetNames.Contains(p.Name))
                .ToList();
        }

        private AgentPreset? FindPreset(string name)
        {
            return _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task SaveAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_presetsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_presetsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save agent presets: {ex.Message}");
            }

            UpdateUI();
        }

        private void UpdateUI()
        {
            var presetNames = _presets.Select(p => p.Name).ToList();
            var selectedNames = _selectedPresetNames.ToList();

            void ApplyUI()
            {
                _agentPane.UpdateAgentPresetsMenu(presetNames, selectedNames, _getString);
                QueueContextChanged();
            }

            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.HasThreadAccess == true)
            {
                ApplyUI();
                return;
            }

            if (dispatcher?.TryEnqueue(ApplyUI) == true)
            {
                return;
            }

            ApplyUI();
        }

        private void QueueContextChanged()
        {
            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _contextChanged()) == true)
            {
                return;
            }

            _contextChanged();
        }

        private TextBox CreateNameBox(string text = "")
        {
            return new TextBox
            {
                PlaceholderText = _getString("AgentPresetSavePlaceholder", "프리셋 이름 입력..."),
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private TextBox CreateContentBox(string text = "")
        {
            var contentBox = new TextBox
            {
                PlaceholderText = _getString("AgentPresetContentPlaceholder", "페르소나/지침 내용..."),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new FontFamily("Consolas, Segoe UI, Malgun Gothic")
            };
            contentBox.Text = NormalizeTextBoxLineEndings(text);
            ScrollViewer.SetVerticalScrollMode(contentBox, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);
            contentBox.Paste += async (_, e) =>
            {
                e.Handled = true;
                await PasteClipboardTextAsync(contentBox);
            };
            return contentBox;
        }

        private StackPanel CreateDialogContent(TextBox nameBox, TextBox contentBox)
        {
            var stack = new StackPanel { Spacing = 10, Width = 400 };
            stack.Children.Add(new TextBlock { Text = _getString("AgentPresetSaveLabel", "프리셋 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentPresetContentLabel", "페르소나/지침") });
            stack.Children.Add(contentBox);
            return stack;
        }

        private async Task<bool> ConfirmOverwriteAsync()
        {
            var confirmDialog = new ContentDialog
            {
                Title = _getString("AgentPresetDuplicateTitle", "프리셋 중복"),
                Content = _getString("AgentPresetDuplicateMessage", "이미 동일한 이름의 프리셋이 존재합니다. 덮어쓰시겠습니까?"),
                PrimaryButtonText = _getString("Yes", "예"),
                CloseButtonText = _getString("No", "아니오"),
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var confirmResult = await ShowDialogAsync(confirmDialog);
            return confirmResult == ContentDialogResult.Primary;
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            _beforeDialog?.Invoke();
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _afterDialog?.Invoke();
            }
        }

        private static string NormalizePresetContent(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }

        private static async Task PasteClipboardTextAsync(TextBox textBox)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string clipboardText = await content.GetTextAsync();
                InsertTextAtSelection(textBox, NormalizeTextBoxLineEndings(clipboardText));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to paste agent preset content: {ex.Message}");
            }
        }

        private static void InsertTextAtSelection(TextBox textBox, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string current = textBox.Text ?? string.Empty;
            int selectionStart = Math.Max(0, Math.Min(textBox.SelectionStart, current.Length));
            int selectionLength = Math.Max(0, Math.Min(textBox.SelectionLength, current.Length - selectionStart));

            textBox.Text = current.Substring(0, selectionStart) +
                text +
                current.Substring(selectionStart + selectionLength);
            textBox.SelectionStart = selectionStart + text.Length;
            textBox.SelectionLength = 0;
        }

        private static string NormalizeTextBoxLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }
    }
}
