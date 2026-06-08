using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;

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
        private readonly Action<object> _initializePickerWindow;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;

        private string _lastSelectionText = string.Empty;
        private string _fileContextText = string.Empty;
        private string _fileContextDisplay = string.Empty;
        private System.Threading.CancellationTokenSource? _assistantCts = null;
        private bool _isAssistantRunning = false;

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
            Action<object> initializePickerWindow,
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
            _initializePickerWindow = initializePickerWindow;
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
            if (_isAssistantRunning) return;
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string language = GetActiveSelectionLanguage();
            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionExplain", "선택 영역 설명 (Explain)"), context,
                (onChunk, ct) => _llmService.ExplainCodeAsync(context, language, onChunk, ct));
        }

        private async void OnLlmSummarizeClick(object sender, RoutedEventArgs e)
        {
            if (_isAssistantRunning) return;
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string fileContext = GetActiveFileContext();
            if (!string.IsNullOrEmpty(fileContext))
            {
                await ProcessChunkedSummarizationAsync(fileContext);
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionSummarize", "선택 영역 요약 (Summarize)"), context,
                (onChunk, ct) => _llmService.SummarizeTextAsync(context, onChunk, ct));
        }

        private async void OnLlmTranslateClick(object sender, RoutedEventArgs e)
        {
            if (_isAssistantRunning) return;
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string fileContext = GetActiveFileContext();
            if (!string.IsNullOrEmpty(fileContext))
            {
                await ProcessChunkedTranslationAsync(fileContext);
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionTranslate", "선택 영역 번역 (Translate)"), context,
                (onChunk, ct) => _llmService.TranslateTextAsync(context, onChunk, ct));
        }

        private async void OnLlmImproveClick(object sender, RoutedEventArgs e)
        {
            if (_isAssistantRunning) return;
            if (string.IsNullOrEmpty(_lastSelectionText) && string.IsNullOrEmpty(GetActiveFileContext()))
            {
                _showError(_getString("LlmErrorTitle", "AI 오류"), _getString("LlmNoSelectionCustom", "선택 영역이나 파일 맥락이 없습니다. 텍스트를 선택하거나 파일 맥락을 추가하십시오."));
                return;
            }

            string context = BuildLlmContext(_lastSelectionText);
            await PreflightCheckAndRunAsync(_getString("LlmActionImprove", "수식 및 마크다운 개선"), context,
                (onChunk, ct) => _llmService.ImproveTextAsync(context, onChunk, ct));
        }

        private async void OnLlmCustomClick(object sender, RoutedEventArgs e)
        {
            if (_isAssistantRunning)
            {
                _assistantCts?.Cancel();
                return;
            }

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
                (onChunk, ct) => _llmService.CustomPromptAsync(prompt, fileContext, selectedText, onChunk, ct),
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

            if (IsPdfTab(tab))
            {
                _showError(
                    _getString("LlmFileContextTitle", "AI 파일 맥락"),
                    _getString("LlmPdfFileContextExcluded", "PDF 탭은 파일 맥락에 포함되지 않습니다."));
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
            int estimatedTokens = StatusBarController.EstimateTokenCount(fileContext);
            string format = _getString("LlmFileContextDisplayFormat", "{0} · 약 {1:N0} 토큰");
            string display = string.Format(format, Path.GetFileName(title), estimatedTokens);

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

        private static bool IsPdfTab(OpenedTab tab)
        {
            return tab.IsPdfViewer ||
                   string.Equals(tab.Language, "pdf", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(tab.FilePath) &&
                    string.Equals(Path.GetExtension(tab.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase));
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

        private static string ExtractContentFromFileContext(string fileContext)
        {
            int idx = fileContext.IndexOf('\n');
            if (idx >= 0 && idx < fileContext.Length - 1)
                return fileContext.Substring(idx + 1);
            return fileContext;
        }

        private string GetOutputFilePath(string suffix)
        {
            var tab = _activeTabProvider();
            if (tab != null && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                string dir = Path.GetDirectoryName(tab.FilePath) ?? ".";
                string nameWithoutExt = Path.GetFileNameWithoutExtension(tab.FilePath);
                if (nameWithoutExt.EndsWith(suffix, StringComparison.Ordinal))
                {
                    nameWithoutExt = nameWithoutExt.Substring(0, nameWithoutExt.Length - suffix.Length);
                }
                return Path.Combine(dir, $"{nameWithoutExt}{suffix}.txt");
            }

            string fileContext = GetActiveFileContext();
            string displayName = "untitled";
            if (!string.IsNullOrEmpty(fileContext))
            {
                int endOfHeader = fileContext.IndexOf(']');
                if (endOfHeader > 0)
                {
                    int colonIdx = fileContext.IndexOf(':');
                    if (colonIdx > 0 && colonIdx < endOfHeader)
                    {
                        displayName = fileContext.Substring(colonIdx + 1, endOfHeader - colonIdx - 1).Trim();
                        displayName = Path.GetFileNameWithoutExtension(displayName);
                    }
                }
            }
            return $"{displayName}{suffix}.txt";
        }

        private async Task ProcessChunkedSummarizationAsync(string fileContext)
        {
            _assistantCts = new CancellationTokenSource();
            _isAssistantRunning = true;
            _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomCancelButtonText", "중단");

            try
            {
                string content = ExtractContentFromFileContext(fileContext);
                string[] lines = content.Split('\n');
                const int chunkSize = 500;
                int totalChunks = (int)Math.Ceiling((double)lines.Length / chunkSize);

                if (totalChunks == 0) return;

                _rightSidebar.LlmOutput.Text = "";
                _rightSidebar.RightTabs.SelectedIndex = 1;

                string actionName = _getString("LlmActionSummarize", "요약");
                var results = new List<string>();
                bool hasError = false;

                string progressFormat = _getString("LlmChunkProgressFormat", "{0} 진행 중: {1}/{2} 청크 진행중...");

                for (int i = 0; i < totalChunks; i++)
                {
                    _assistantCts.Token.ThrowIfCancellationRequested();

                    if (hasError) break;

                    int current = i + 1;
                    string progressText = string.Format(progressFormat, actionName, current, totalChunks);
                    _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                    {
                        _rightSidebar.LlmOutput.Text = progressText;
                    });

                    int start = i * chunkSize;
                    int count = Math.Min(chunkSize, lines.Length - start);
                    string chunkText = string.Join("\n", lines, start, count);

                    try
                    {
                        string summary = await _llmService.SummarizeTextAsync(chunkText, cancellationToken: _assistantCts.Token);
                        results.Add(summary);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException || _assistantCts.IsCancellationRequested)
                        {
                            throw;
                        }
                        hasError = true;
                        string errorFormat = _getString("LlmChunkErrorFormat", "청크 {0} 처리 중 오류: {1}");
                        _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                        {
                            _rightSidebar.LlmOutput.Text = string.Format(errorFormat, current, ex.Message);
                        });
                        return;
                    }
                }

                if (hasError || results.Count == 0) return;

                string combined = string.Join("\n\n---\n\n", results);
                string outputPath = GetOutputFilePath("_summary");
                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(outputPath, combined);

                string completeMsg = string.Format(
                    _getString("LlmSummarizeComplete", "{0} 완료. {1} 로 저장하였습니다."),
                    actionName, outputPath);
                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = completeMsg;
                });
            }
            catch (OperationCanceledException)
            {
                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = _getString("LlmOperationCanceled", "작업이 중단되었습니다.");
                });
            }
            finally
            {
                _isAssistantRunning = false;
                _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomRunButtonText", "전송");
                _assistantCts?.Dispose();
                _assistantCts = null;
            }
        }

        // 번역 진행 마커 형식: 파일 마지막 줄에 기록되어 resume 시 정확한 chunk 인덱스 파악에 사용
        private const string TranslationProgressMarkerPrefix = "<!-- CHUNK:";
        private const string TranslationProgressMarkerSuffix = " -->";

        private static string BuildProgressMarker(int chunkIndex, int totalChunks)
            => $"\n{TranslationProgressMarkerPrefix}{chunkIndex + 1}/{totalChunks}{TranslationProgressMarkerSuffix}";

        // 파일에서 마커를 읽어 완료된 마지막 chunk 인덱스(0-based)를 반환. 마커 없으면 -1.
        private static int TryReadProgressMarker(string fileContent, int totalChunks)
        {
            int lastNewline = fileContent.LastIndexOf('\n');
            if (lastNewline < 0) return -1;
            string lastLine = fileContent.Substring(lastNewline + 1).Trim();
            if (!lastLine.StartsWith(TranslationProgressMarkerPrefix, StringComparison.Ordinal)) return -1;
            int slashIdx = lastLine.IndexOf('/', TranslationProgressMarkerPrefix.Length);
            if (slashIdx < 0) return -1;
            string chunkNumStr = lastLine.Substring(TranslationProgressMarkerPrefix.Length, slashIdx - TranslationProgressMarkerPrefix.Length);
            if (!int.TryParse(chunkNumStr, out int chunkNum)) return -1;
            if (chunkNum < 1 || chunkNum > totalChunks) return -1;
            return chunkNum - 1; // 0-based 인덱스로 변환
        }

        // 파일에서 마커 줄을 제거한 내용을 반환
        private static string StripProgressMarker(string fileContent)
        {
            int lastNewline = fileContent.LastIndexOf('\n');
            if (lastNewline < 0) return fileContent;
            string lastLine = fileContent.Substring(lastNewline + 1).Trim();
            if (lastLine.StartsWith(TranslationProgressMarkerPrefix, StringComparison.Ordinal))
                return fileContent.Substring(0, lastNewline);
            return fileContent;
        }

        // 파일에서 모든 진행 마커를 제거한 내용을 반환
        private static string StripAllProgressMarkers(string fileContent)
        {
            if (string.IsNullOrEmpty(fileContent)) return fileContent;
            var lines = fileContent.Split('\n');
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith(TranslationProgressMarkerPrefix, StringComparison.Ordinal))
                {
                    if (result.Length > 0) result.Append('\n');
                    result.Append(lines[i]);
                }
            }
            return result.ToString();
        }

        private async Task ProcessChunkedTranslationAsync(string fileContext)
        {
            _assistantCts = new CancellationTokenSource();
            _isAssistantRunning = true;
            _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomCancelButtonText", "중단");

            try
            {
                string content = ExtractContentFromFileContext(fileContext);
                string[] lines = content.Split('\n');
                const int chunkSize = 200;
                int totalChunks = (int)Math.Ceiling((double)lines.Length / chunkSize);

                if (totalChunks == 0) return;

                _rightSidebar.RightTabs.SelectedIndex = 1;

                string actionName = _getString("LlmActionTranslate", "번역");
                string progressFormat = _getString("LlmChunkProgressFormat", "{0} 진행 중: {1}/{2} 청크 진행중...");

                string outputPath = GetOutputFilePath("_translation");
                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 이전 번역 파일의 마커에서 정확한 완료 chunk 인덱스를 읽어 resume 처리
                int startChunkIndex = 0;
                bool isResuming = false;
                if (File.Exists(outputPath))
                {
                    string existingContent = await File.ReadAllTextAsync(outputPath);
                    if (!string.IsNullOrEmpty(existingContent))
                    {
                        int markerChunkIndex = TryReadProgressMarker(existingContent, totalChunks);
                        if (markerChunkIndex >= 0 && markerChunkIndex + 1 < totalChunks)
                        {
                            // 모든 이전 마커 제거 후 깨끗한 내용 + resume 마커 기록
                            string cleaned = StripAllProgressMarkers(existingContent);
                            string resumeMarker = BuildProgressMarker(markerChunkIndex, totalChunks);
                            await File.WriteAllTextAsync(outputPath, cleaned + resumeMarker);
                            startChunkIndex = markerChunkIndex + 1;
                            isResuming = true;
                        }
                    }
                }

                // 이어쓰기가 아닌 경우(처음 시작) 파일 초기화
                if (!isResuming)
                {
                    await File.WriteAllTextAsync(outputPath, string.Empty);
                }

                string resumeMsg = isResuming
                    ? string.Format(_getString("LlmTranslateResuming", "{0} 이어서 번역 중 (청크 {1}/{2}부터)..."), actionName, startChunkIndex + 1, totalChunks)
                    : string.Format(progressFormat, actionName, 1, totalChunks);

                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = resumeMsg;
                });

                bool hasError = false;

                for (int i = startChunkIndex; i < totalChunks; i++)
                {
                    _assistantCts.Token.ThrowIfCancellationRequested();

                    if (hasError) break;

                    int current = i + 1;
                    string progressText = string.Format(progressFormat, actionName, current, totalChunks);
                    _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                    {
                        _rightSidebar.LlmOutput.Text = progressText;
                    });

                    int start = i * chunkSize;
                    int count = Math.Min(chunkSize, lines.Length - start);
                    string chunkText = string.Join("\n", lines, start, count);

                    try
                    {
                        string translated = await _llmService.TranslateTextAsync(chunkText, cancellationToken: _assistantCts.Token);

                        // chunk 완료 후 즉시 번역문 저장, 그 뒤에 진행 마커 기록
                        bool isFirstWrite = (i == 0 && !isResuming);
                        string translationToAppend = isFirstWrite ? translated : "\n" + translated;
                        string marker = BuildProgressMarker(i, totalChunks);
                        await File.AppendAllTextAsync(outputPath, translationToAppend + marker);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException || _assistantCts.IsCancellationRequested)
                        {
                            string cancelledWithSaveMsg = string.Format(
                                _getString("LlmTranslateCancelledWithSave", "작업이 중단되었습니다. 현재까지 번역된 내용이 저장되었습니다: {0}"),
                                outputPath);
                            _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                            {
                                _rightSidebar.LlmOutput.Text = cancelledWithSaveMsg;
                            });
                            return;
                        }
                        hasError = true;
                        string errorFormat = _getString("LlmChunkErrorFormat", "청크 {0} 처리 중 오류: {1}");
                        _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                        {
                            _rightSidebar.LlmOutput.Text = string.Format(errorFormat, current, ex.Message);
                        });
                        return;
                    }
                }

                if (hasError) return;

                // 모든 chunk 완료 — 모든 마커 제거 후 최종 파일 저장
                string finalContent = await File.ReadAllTextAsync(outputPath);
                await File.WriteAllTextAsync(outputPath, StripAllProgressMarkers(finalContent));

                string completeMsg = string.Format(
                    _getString("LlmTranslateComplete", "{0} 완료. {1} 로 저장하였습니다."),
                    actionName, outputPath);
                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = completeMsg;
                });
            }
            catch (OperationCanceledException)
            {
                string outputPath = GetOutputFilePath("_translation");
                string cancelMsg = File.Exists(outputPath)
                    ? string.Format(
                        _getString("LlmTranslateCancelledWithSave", "작업이 중단되었습니다. 현재까지 번역된 내용이 저장되었습니다: {0}"),
                        outputPath)
                    : _getString("LlmOperationCanceled", "작업이 중단되었습니다.");
                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = cancelMsg;
                });
            }
            finally
            {
                _isAssistantRunning = false;
                _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomRunButtonText", "전송");
                _assistantCts?.Dispose();
                _assistantCts = null;
            }
        }

        private async Task PreflightCheckAndRunAsync(
            string actionName,
            string contentText,
            Func<Func<string, Task>, CancellationToken, Task<string>> streamingCall,
            string customInstruction = "")
        {
            if (_isAssistantRunning) return;

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
                    StatusBarController.EstimateTokenCount(previewText),
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

            _assistantCts = new CancellationTokenSource();
            _isAssistantRunning = true;
            _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomCancelButtonText", "중단");

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
                }, _assistantCts.Token);
            }
            catch (OperationCanceledException)
            {
                _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _rightSidebar.LlmOutput.Text = _getString("LlmOperationCanceled", "작업이 중단되었습니다.");
                });
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException || _assistantCts.IsCancellationRequested)
                {
                    _rightSidebar.DispatcherQueue.TryEnqueue(() =>
                    {
                        _rightSidebar.LlmOutput.Text = _getString("LlmOperationCanceled", "작업이 중단되었습니다.");
                    });
                }
                else
                {
                    string exceptionFormat = _getString("LlmExceptionFormat", "AI 실행 도중 예외가 터졌습니다: {0}");
                    _rightSidebar.LlmOutput.Text = string.Format(exceptionFormat, ex.Message);
                }
            }
            finally
            {
                _isAssistantRunning = false;
                _rightSidebar.LlmCustomRunBtn.Content = _getString("LlmCustomRunButtonText", "전송");
                _assistantCts?.Dispose();
                _assistantCts = null;
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
                _rightSidebar.UpdatePresetsMenu(
                    presetNames,
                    OnAddPresetClick,
                    OnPresetSelected,
                    OnPresetEdited,
                    OnPresetDeleted,
                    OnExportPresetsClick,
                    OnImportPresetsClick,
                    _getString);
            });
        }

        private async void OnExportPresetsClick()
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "ai-assistant-presets.json"
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
                string json = System.Text.Json.JsonSerializer.Serialize(
                    _presets,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetExportErrorTitle", "프리셋 내보내기 오류"),
                    string.Format(_getString("PresetExportErrorMessage", "프리셋을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private async void OnImportPresetsClick()
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
                var imported = System.Text.Json.JsonSerializer.Deserialize<List<PresetItem>>(json);
                if (imported == null)
                {
                    throw new InvalidDataException(_getString("PresetImportInvalidFile", "가져올 수 있는 프리셋 JSON이 아닙니다."));
                }

                foreach (var item in imported.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    string name = item.Name.Trim();
                    string content = NormalizePresetContent(item.Content);
                    var existing = _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Name = name;
                        existing.Title = string.IsNullOrWhiteSpace(item.Title) ? name : item.Title.Trim();
                        existing.Content = content;
                        existing.FilePath = item.FilePath ?? string.Empty;
                    }
                    else
                    {
                        _presets.Add(new PresetItem
                        {
                            Name = name,
                            Title = string.IsNullOrWhiteSpace(item.Title) ? name : item.Title.Trim(),
                            Content = content,
                            FilePath = item.FilePath ?? string.Empty
                        });
                    }
                }

                await SavePresetsAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("PresetImportErrorTitle", "프리셋 가져오기 오류"),
                    string.Format(_getString("PresetImportErrorMessage", "프리셋을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
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

        private static string NormalizePresetContent(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
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
            contentBox.Paste += async (_, e) =>
            {
                e.Handled = true;
                await PasteClipboardTextAsync(contentBox);
            };
            return contentBox;
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
