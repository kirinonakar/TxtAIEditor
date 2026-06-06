using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed class LlmAssistantController
    {
        private readonly ILLMService _llmService;
        private readonly ISettingsService _settingsService;
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly RightSidebarPane _rightSidebar;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<OpenedTab, int, string> _getTabText;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;

        private string _lastSelectionText = string.Empty;
        private string _fileContextText = string.Empty;
        private string _fileContextDisplay = string.Empty;

        private class CustomInstruction
        {
            public string Name { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string FileContext { get; set; } = "";
            public string FileContextDisplay { get; set; } = "";
            public string PresetContent { get; set; } = "";
        }

        private List<CustomInstruction> _instructions = new();
        private int _activeInstructionIndex = -1;
        private int _instructionNameCounter = 0;

        private class PresetItem
        {
            public string Name { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
        }

        private readonly string _presetsFilePath;
        private List<PresetItem> _presets = new();

        public LlmAssistantController(
            ILLMService llmService,
            ISettingsService settingsService,
            ILanguageDetectionService languageDetectionService,
            RightSidebarPane rightSidebar,
            Func<XamlRoot> xamlRootProvider,
            Func<OpenedTab?> activeTabProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action? beforeDialog = null,
            Action? afterDialog = null)
        {
            _llmService = llmService;
            _settingsService = settingsService;
            _languageDetectionService = languageDetectionService;
            _rightSidebar = rightSidebar;
            _xamlRootProvider = xamlRootProvider;
            _activeTabProvider = activeTabProvider;
            _getTabText = getTabText;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _showError = showError;
            _getString = getString;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            WireEvents();

            var initialTargetLang = _settingsService.CurrentSettings?.LlmTargetLanguage ?? "Korean";
            _rightSidebar.UpdateTranslateLanguage(initialTargetLang);
            UpdateModelDisplay();

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _presetsFilePath = Path.Combine(settingsDir, "presets.json");
            _ = LoadPresetsAsync();
        }

        public void SetSelectionText(string selectedText)
        {
            _lastSelectionText = selectedText ?? string.Empty;
        }

        public void ClearSelection()
        {
            _lastSelectionText = string.Empty;
        }

        public void SetOutput(string message)
        {
            _rightSidebar.LlmOutput.Text = message;
        }

        public void UpdateModelDisplay()
        {
            var settings = _settingsService.CurrentSettings;
            if (settings != null)
            {
                string provider = settings.LlmProvider ?? string.Empty;
                string model = settings.LlmModel ?? string.Empty;
                string format = _getString("AgentModelFormat", "모델: {0} ({1})");
                _rightSidebar.UpdateLlmModelName(string.Format(format, model, provider));
            }
        }

        private void WireEvents()
        {
            _rightSidebar.LlmAddFileContextClick += OnLlmAddFileContextClick;
            _rightSidebar.LlmRemoveFileContextClick += OnLlmRemoveFileContextClick;
            _rightSidebar.LlmExplainClick += OnLlmExplainClick;
            _rightSidebar.LlmSummarizeClick += OnLlmSummarizeClick;
            _rightSidebar.LlmTranslateClick += OnLlmTranslateClick;
            _rightSidebar.LlmImproveClick += OnLlmImproveClick;
            _rightSidebar.LlmCustomClick += OnLlmCustomClick;
            _rightSidebar.LlmInsertOutputClick += OnLlmInsertOutputClick;
            _rightSidebar.LlmTargetLanguageSelected += OnLlmTargetLanguageSelected;
            _rightSidebar.LlmAddInstructionClick += OnLlmAddInstructionClick;
        }

        private string GetActiveSelectionLanguage()
        {
            var activeTab = _activeTabProvider();
            if (activeTab == null)
            {
                return "plaintext";
            }

            if (!string.IsNullOrWhiteSpace(activeTab.Language))
            {
                return activeTab.Language;
            }

            if (!string.IsNullOrWhiteSpace(activeTab.FilePath))
            {
                return _languageDetectionService.GetMonacoLanguageName(activeTab.FilePath);
            }

            return "plaintext";
        }

        private async void OnLlmExplainClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string language = GetActiveSelectionLanguage();
            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionExplain", "선택 영역 설명 (Explain)"), context,
                onChunk => _llmService.ExplainCodeAsync(context, language, onChunk));
        }

        private async void OnLlmSummarizeClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionSummarize", "선택 영역 요약 (Summarize)"), context,
                onChunk => _llmService.SummarizeTextAsync(context, onChunk));
        }

        private async void OnLlmTranslateClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionTranslate", "선택 영역 번역 (Translate)"), context,
                onChunk => _llmService.TranslateTextAsync(context, onChunk));
        }

        private async void OnLlmImproveClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionImprove", "수식 및 마크다운 개선"), context,
                onChunk => _llmService.ImproveTextAsync(context, onChunk));
        }

        private async void OnLlmCustomClick(object sender, RoutedEventArgs e)
        {
            SaveActivePrompt();

            string prompt = GetActivePrompt();

            if (_activeInstructionIndex < 0 && string.IsNullOrEmpty(prompt))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmEmptyCustomPrompt", "커스텀 지시사항 입력란이 비어 있습니다."));
                return;
            }

            string fileContext = GetActiveFileContext();
            string selectedText = _lastSelectionText;
            string actionName = _getString("LlmActionCustom", "커스텀 지시사항 실행");

            await PreflightCheckAndRunAsync(actionName, $"{fileContext}\n\n{selectedText}",
                onChunk => _llmService.CustomPromptAsync(prompt, fileContext, selectedText, onChunk),
                customInstruction: prompt);
        }

        private void OnLlmAddFileContextClick(object sender, RoutedEventArgs e)
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                _showError(_getString("LlmFileContextTitle", "AI 파일 맥락"), _getString("LlmNoActiveTabForFileContext", "파일 맥락으로 추가할 활성 탭이 없습니다."));
                return;
            }

            string title = string.IsNullOrWhiteSpace(tab.FilePath) ? tab.Title : tab.FilePath;
            const int maxChars = 120_000;
            string content = _getTabText(tab, maxChars);
            if (content.Length > maxChars)
            {
                content = content.Substring(0, maxChars) + "\n\n[파일 맥락이 길어 앞부분만 포함됨]";
            }

            string fileContext = $"[파일 맥락: {title}]\n{content}";
            string display = $"{Path.GetFileName(title)} · {fileContext.Length:N0} 글자";

            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                _instructions[_activeInstructionIndex].FileContext = fileContext;
                _instructions[_activeInstructionIndex].FileContextDisplay = display;
            }
            else
            {
                _fileContextText = fileContext;
                _fileContextDisplay = display;
            }

            _rightSidebar.LlmFileContext.Text = display;
        }

        private void OnLlmRemoveFileContextClick(object sender, RoutedEventArgs e)
        {
            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                _instructions[_activeInstructionIndex].FileContext = string.Empty;
                _instructions[_activeInstructionIndex].FileContextDisplay = string.Empty;
            }
            else
            {
                _fileContextText = string.Empty;
                _fileContextDisplay = string.Empty;
            }

            _rightSidebar.LlmFileContext.Text = string.Empty;
        }

        private async void OnLlmInsertOutputClick(object sender, RoutedEventArgs e)
        {
            string output = _rightSidebar.LlmOutput.SelectedText;
            if (string.IsNullOrEmpty(output))
            {
                output = _rightSidebar.LlmOutput.Text;
            }

            if (string.IsNullOrWhiteSpace(output) || output.StartsWith("대기 중", StringComparison.Ordinal) || output.StartsWith("Waiting...", StringComparison.Ordinal) || output.StartsWith("待機中...", StringComparison.Ordinal))
            {
                _showError(_getString("LlmInsertTitle", "AI 응답 입력"), _getString("LlmNoOutputToInsert", "입력할 AI 응답이 없습니다."));
                return;
            }

            await _insertIntoActiveEditorAsync(output);
        }

        private void OnLlmAddInstructionClick(object sender, RoutedEventArgs e)
        {
            if (_instructions.Count >= 4) return;

            SaveActivePrompt();

            _instructionNameCounter++;
            string defaultPrefix = _getString("LlmInstructionDefaultName", "지시문");
            var name = $"{defaultPrefix} {_instructionNameCounter}";

            _instructions.Add(new CustomInstruction
            {
                Name = name,
                Prompt = string.Empty,
                FileContext = string.Empty,
                FileContextDisplay = string.Empty
            });
            _activeInstructionIndex = _instructions.Count - 1;

            LoadActiveInstruction();
            RebuildInstructionTabsUI();
        }

        private void OnInstructionTabClick(int index)
        {
            if (index < 0 || index >= _instructions.Count) return;

            SaveActivePrompt();
            _activeInstructionIndex = index;
            LoadActiveInstruction();
            RebuildInstructionTabsUI();
        }

        private void OnInstructionTabDelete(int index)
        {
            if (index < 0 || index >= _instructions.Count) return;

            _instructions.RemoveAt(index);

            if (_activeInstructionIndex >= _instructions.Count)
            {
                _activeInstructionIndex = _instructions.Count - 1;
            }
            else if (_activeInstructionIndex == index)
            {
                _activeInstructionIndex = Math.Min(index, _instructions.Count - 1);
            }
            else if (_activeInstructionIndex > index)
            {
                _activeInstructionIndex--;
            }

            if (_instructions.Count == 0)
            {
                _activeInstructionIndex = -1;
                _instructionNameCounter = 0;
            }

            LoadActiveInstruction();
            RebuildInstructionTabsUI();
        }

        private void SaveActivePrompt()
        {
            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                _instructions[_activeInstructionIndex].Prompt = _rightSidebar.LlmCustomPrompt.Text;
            }
        }

        private void LoadActiveInstruction()
        {
            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                var inst = _instructions[_activeInstructionIndex];
                _rightSidebar.LlmCustomPrompt.Text = inst.Prompt;
                _rightSidebar.LlmFileContext.Text = inst.FileContextDisplay;

                if (!string.IsNullOrEmpty(inst.PresetContent))
                {
                    string format = _getString("LlmPresetPlaceholderFormat", "[{0}] 프리셋 적용됨. 추가 지시사항 입력...");
                    _rightSidebar.LlmCustomPrompt.PlaceholderText = string.Format(format, inst.Name);
                }
                else
                {
                    _rightSidebar.LlmCustomPrompt.PlaceholderText = _getString("LlmCustomPromptPlaceholder", "질문이나 커스텀 지시사항 입력...");
                }
            }
            else
            {
                _rightSidebar.LlmFileContext.Text = _fileContextDisplay;
                _rightSidebar.LlmCustomPrompt.PlaceholderText = _getString("LlmCustomPromptPlaceholder", "질문이나 커스텀 지시사항 입력...");
            }
        }

        private void RebuildInstructionTabsUI()
        {
            var tabs = _instructions.Select((inst, idx) => (inst.Name, IsActive: idx == _activeInstructionIndex)).ToList();
            _rightSidebar.UpdateInstructionTabs(tabs, OnInstructionTabClick, OnInstructionTabDelete);

            var hasTabs = _instructions.Count > 0;
            _rightSidebar.InstructionTabScroller.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetActivePrompt()
        {
            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                var inst = _instructions[_activeInstructionIndex];
                if (!string.IsNullOrEmpty(inst.PresetContent))
                {
                    if (!string.IsNullOrWhiteSpace(inst.Prompt))
                    {
                        return $"{inst.PresetContent}\n\n{inst.Prompt}";
                    }
                    return inst.PresetContent;
                }
                return inst.Prompt;
            }
            return _rightSidebar.LlmCustomPrompt.Text;
        }

        private string GetActiveFileContext()
        {
            if (_activeInstructionIndex >= 0 && _activeInstructionIndex < _instructions.Count)
            {
                return _instructions[_activeInstructionIndex].FileContext;
            }
            return _fileContextText;
        }

        private string BuildLlmContext(string selectedText)
        {
            string fileContext = GetActiveFileContext();

            if (string.IsNullOrEmpty(fileContext))
            {
                return selectedText;
            }

            if (string.IsNullOrEmpty(selectedText))
            {
                return fileContext;
            }

            return $"{fileContext}\n\n[선택 영역]\n{selectedText}";
        }

        private async Task PreflightCheckAndRunAsync(string actionName, string contentText, Func<Func<string, Task>, Task<string>> streamingCall, string customInstruction = "")
        {
            if (_settingsService.CurrentSettings.LlmConfirmBeforeSending)
            {
                string previewText = contentText;
                if (!string.IsNullOrEmpty(customInstruction))
                {
                    previewText = $"[사용자 지시사항]\n{customInstruction}\n\n{contentText}";
                }
                var textPreview = previewText.Length > 200 ? previewText.Substring(0, 200) + "..." : previewText;

                string format = _getString("LlmPreflightContentFormat", "액션: {0}\n\n전송될 AI 공급자: {1} ({2})\n전송 텍스트 크기: {3:N0} 자 (약 {4:N0} 토큰 소모)\n\n[전송 내용 미리보기]\n{5}\n\n보안상의 문제나 의도하지 않은 토큰 대량 유실이 없는지 확인 후 전송해 주십시오.");
                string dialogContent = string.Format(format,
                    actionName,
                    _settingsService.CurrentSettings.LlmProvider,
                    _settingsService.CurrentSettings.LlmModel,
                    previewText.Length,
                    previewText.Length / 4,
                    textPreview);

                _beforeDialog?.Invoke();
                var dialog = new ContentDialog
                {
                    Title = _getString("LlmPreflightTitle", "AI 전송 사전 확인 (Pre-flight Check)"),
                    Content = dialogContent,
                    PrimaryButtonText = _getString("LlmPreflightApprove", "API 전송 승인"),
                    CloseButtonText = _getString("LlmPreflightCancel", "취소"),
                    XamlRoot = _xamlRootProvider()
                };

                var result = await dialog.ShowAsync();
                _afterDialog?.Invoke();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            _rightSidebar.LlmOutput.Text = "";
            _rightSidebar.RightTabs.SelectedIndex = 1;

            try
            {
                await streamingCall(async chunk =>
                {
                    _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                    {
                        _rightSidebar.LlmOutput.Text += chunk;
                        _rightSidebar.LlmOutput.SelectionStart = _rightSidebar.LlmOutput.Text.Length;
                        _rightSidebar.LlmOutput.SelectionLength = 0;
                    });
                    await Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                string exceptionFormat = _getString("LlmExceptionFormat", "AI 실행 도중 예외가 터졌습니다: {0}");
                _rightSidebar.LlmOutput.Text = string.Format(exceptionFormat, ex.Message);
            }
        }

        private async void OnLlmTargetLanguageSelected(string targetLanguage)
        {
            var settings = _settingsService.CurrentSettings;
            if (settings.LlmTargetLanguage == targetLanguage)
            {
                return;
            }

            settings.LlmTargetLanguage = targetLanguage;
            await _settingsService.SaveSettingsAsync(settings);

            _rightSidebar.UpdateTranslateLanguage(targetLanguage);
        }

        private async Task LoadPresetsAsync()
        {
            try
            {
                if (File.Exists(_presetsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_presetsFilePath);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<List<PresetItem>>(json);
                    if (loaded != null)
                    {
                        _presets = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load presets: {ex.Message}");
            }
            UpdatePresetsUI();
        }

        private async Task SavePresetsAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_presetsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = System.Text.Json.JsonSerializer.Serialize(_presets, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_presetsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save presets: {ex.Message}");
            }
            UpdatePresetsUI();
        }

        private void UpdatePresetsUI()
        {
            var dispatcher = _rightSidebar.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            dispatcher?.TryEnqueue(() =>
            {
                var presetNames = _presets.Select(p => p.Name).ToList();
                _rightSidebar.UpdatePresetsMenu(presetNames, OnAddPresetClick, OnPresetSelected, OnPresetEdited, OnPresetDeleted, _getString);
            });
        }

        private async void OnAddPresetClick()
        {
            var nameBox = new TextBox
            {
                PlaceholderText = _getString("LlmPresetSavePlaceholder", "프리셋 이름 입력..."),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var contentBox = CreatePresetContentBox();

            var stack = new StackPanel { Spacing = 10, Width = 400 };
            stack.Children.Add(new TextBlock { Text = _getString("LlmPresetSaveLabel", "프리셋 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("LlmPresetContentLabel", "프리셋 내용") });
            stack.Children.Add(contentBox);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("LlmPresetAddText", "프리셋 추가"),
                Content = stack,
                PrimaryButtonText = _getString("LlmPresetSaveAddButton", "추가"),
                CloseButtonText = _getString("LlmPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _rightSidebar.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string presetName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(presetName))
            {
                _showError(_getString("LlmPresetNameEmptyTitle", "프리셋 추가 오류"), _getString("LlmPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _beforeDialog?.Invoke();
                var confirmDialog = new ContentDialog
                {
                    Title = _getString("LlmPresetDuplicateTitle", "프리셋 중복"),
                    Content = _getString("LlmPresetDuplicateMessage", "이미 동일한 이름의 프리셋이 존재합니다. 덮어쓰시겠습니까?"),
                    PrimaryButtonText = _getString("Yes", "예"),
                    CloseButtonText = _getString("No", "아니오"),
                    XamlRoot = _xamlRootProvider(),
                    RequestedTheme = _rightSidebar.ActualTheme
                };
                var confirmResult = await confirmDialog.ShowAsync();
                _afterDialog?.Invoke();

                if (confirmResult != ContentDialogResult.Primary)
                {
                    return;
                }
                _presets.Remove(existing);
            }

            var newPreset = new PresetItem
            {
                Name = presetName,
                Title = presetName,
                Content = contentBox.Text.Replace("\r\n", "\n").Replace("\r", "\n"),
                FilePath = string.Empty
            };
            _presets.Add(newPreset);
            await SavePresetsAsync();
        }

        private void OnPresetSelected(string presetName)
        {
            var preset = _presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            int requiredNewTabs = 0;
            bool basePromptNotEmpty = !string.IsNullOrWhiteSpace(_rightSidebar.LlmCustomPrompt.Text);

            if (_activeInstructionIndex < 0)
            {
                requiredNewTabs = basePromptNotEmpty ? 2 : 1;
            }
            else
            {
                requiredNewTabs = 1;
            }

            if (_instructions.Count + requiredNewTabs > 4)
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmMaxInstructionsReached", "지시문 탭은 최대 4개까지만 생성할 수 있습니다. 기존 탭을 삭제한 후 다시 시도해 주세요."));
                return;
            }

            if (_activeInstructionIndex < 0)
            {
                if (basePromptNotEmpty)
                {
                    _instructionNameCounter++;
                    string defaultPrefix = _getString("LlmInstructionDefaultName", "지시문");
                    var name = $"{defaultPrefix} {_instructionNameCounter}";

                    _instructions.Add(new CustomInstruction
                    {
                        Name = name,
                        Prompt = _rightSidebar.LlmCustomPrompt.Text,
                        FileContext = _fileContextText,
                        FileContextDisplay = _fileContextDisplay
                    });
                }
            }
            else
            {
                SaveActivePrompt();
            }

            var presetInstruction = new CustomInstruction
            {
                Name = preset.Name,
                Prompt = string.Empty,
                FileContext = string.Empty,
                FileContextDisplay = string.Empty,
                PresetContent = preset.Content
            };
            _instructions.Add(presetInstruction);
            _activeInstructionIndex = _instructions.Count - 1;

            LoadActiveInstruction();
            RebuildInstructionTabsUI();
        }

        private async void OnPresetDeleted(string presetName)
        {
            var preset = _presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            _presets.Remove(preset);
            await SavePresetsAsync();
        }

        private async void OnPresetEdited(string presetName)
        {
            var preset = _presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            var nameBox = new TextBox
            {
                PlaceholderText = _getString("LlmPresetSavePlaceholder", "프리셋 이름 입력..."),
                Text = preset.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var contentBox = CreatePresetContentBox(preset.Content);

            var stack = new StackPanel { Spacing = 10, Width = 400 };
            stack.Children.Add(new TextBlock { Text = _getString("LlmPresetSaveLabel", "프리셋 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("LlmPresetContentLabel", "프리셋 내용") });
            stack.Children.Add(contentBox);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("LlmPresetEditTitle", "프리셋 수정"),
                Content = stack,
                PrimaryButtonText = _getString("LlmPresetEditSaveButton", "저장"),
                CloseButtonText = _getString("LlmPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _rightSidebar.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                _showError(_getString("LlmPresetNameEmptyTitle", "프리셋 수정 오류"), _getString("LlmPresetNameEmptyMessage", "프리셋 이름을 입력해주세요."));
                return;
            }

            if (!newName.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            {
                var existing = _presets.FirstOrDefault(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    _beforeDialog?.Invoke();
                    var confirmDialog = new ContentDialog
                    {
                        Title = _getString("LlmPresetDuplicateTitle", "프리셋 중복"),
                        Content = _getString("LlmPresetDuplicateMessage", "이미 동일한 이름의 프리셋이 존재합니다. 덮어쓰시겠습니까?"),
                        PrimaryButtonText = _getString("Yes", "예"),
                        CloseButtonText = _getString("No", "아니오"),
                        XamlRoot = _xamlRootProvider(),
                        RequestedTheme = _rightSidebar.ActualTheme
                    };
                    var confirmResult = await confirmDialog.ShowAsync();
                    _afterDialog?.Invoke();

                    if (confirmResult != ContentDialogResult.Primary)
                    {
                        return;
                    }
                    _presets.Remove(existing);
                }
            }

            preset.Name = newName;
            preset.Content = contentBox.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            await SavePresetsAsync();
        }

        private TextBox CreatePresetContentBox(string text = "")
        {
            var contentBox = new TextBox
            {
                PlaceholderText = _getString("LlmPresetContentPlaceholder", "프리셋 내용..."),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Segoe UI, Malgun Gothic")
            };
            contentBox.Text = NormalizeTextBoxLineEndings(text);
            ScrollViewer.SetVerticalScrollMode(contentBox, ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);
            contentBox.KeyDown += async (_, e) =>
            {
                if (!IsPasteShortcut(e))
                {
                    return;
                }

                e.Handled = true;
                await PasteClipboardTextAsync(contentBox);
            };
            return contentBox;
        }

        private static bool IsPasteShortcut(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            bool ctrlDown = IsKeyDown(Windows.System.VirtualKey.Control) ||
                IsKeyDown(Windows.System.VirtualKey.LeftControl) ||
                IsKeyDown(Windows.System.VirtualKey.RightControl);
            bool shiftDown = IsKeyDown(Windows.System.VirtualKey.Shift) ||
                IsKeyDown(Windows.System.VirtualKey.LeftShift) ||
                IsKeyDown(Windows.System.VirtualKey.RightShift);

            return (ctrlDown && e.Key == Windows.System.VirtualKey.V) ||
                (shiftDown && e.Key == Windows.System.VirtualKey.Insert);
        }

        private static bool IsKeyDown(Windows.System.VirtualKey key)
        {
            return (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
                    Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private static async Task PasteClipboardTextAsync(TextBox textBox)
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    return;
                }

                string clipboardText = await content.GetTextAsync();
                InsertTextAtSelection(textBox, NormalizeTextBoxLineEndings(clipboardText));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to paste LLM preset content: {ex.Message}");
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
