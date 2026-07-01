using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class StatusBarController
    {
        private readonly StatusBarPane _statusBar;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<OpenedTab, bool> _isActiveTab;
        private readonly Func<string, EditorDocumentSession?> _sessionProvider;
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Func<string, string, string> _getString;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<ElementTheme> _themeProvider;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Func<OpenedTab, string, Task> _reloadTabWithEncodingAsync;
        private readonly Action<OpenedTab> _markTabDirty;
        private readonly Func<string, int, Task> _revealLineAsync;
        private readonly Action<OpenedTab> _updateLivePreview;

        private bool _isSyncingEncodingCombo;

        public StatusBarController(
            StatusBarPane statusBar,
            Func<OpenedTab?> activeTabProvider,
            Func<OpenedTab, bool> isActiveTab,
            Func<string, EditorDocumentSession?> sessionProvider,
            ILanguageDetectionService languageDetectionService,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<string, string, string> getString,
            Func<XamlRoot> xamlRootProvider,
            Func<ElementTheme> themeProvider,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Func<OpenedTab, string, Task> reloadTabWithEncodingAsync,
            Action<OpenedTab> markTabDirty,
            Func<string, int, Task> revealLineAsync,
            Action<OpenedTab> updateLivePreview)
        {
            _statusBar = statusBar;
            _activeTabProvider = activeTabProvider;
            _isActiveTab = isActiveTab;
            _sessionProvider = sessionProvider;
            _languageDetectionService = languageDetectionService;
            _tabBridges = tabBridges;
            _getString = getString;
            _xamlRootProvider = xamlRootProvider;
            _themeProvider = themeProvider;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _reloadTabWithEncodingAsync = reloadTabWithEncodingAsync;
            _markTabDirty = markTabDirty;
            _revealLineAsync = revealLineAsync;
            _updateLivePreview = updateLivePreview;

            _statusBar.EncodingSelectionChanged += OnEncodingSelectionChanged;
            _statusBar.LineNumberClick += OnLineNumberClick;
            _statusBar.LineEndingClick += OnLineEndingClick;
            _statusBar.LanguageClick += OnLanguageClick;
        }

        public void InitializeEncodings(IEnumerable<string> encodingNames, string selectedEncoding)
        {
            foreach (string encodingName in encodingNames)
            {
                _statusBar.EncodingCombo.Items.Add(encodingName);
            }

            _statusBar.EncodingCombo.SelectedItem = selectedEncoding;
        }

        public void UpdateFileStats(OpenedTab tab)
        {
            long bytes = 0;
            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
            {
                bytes = new FileInfo(tab.FilePath).Length;
            }

            string format = _getString("StatusFileSizeFormat", "크기: {0:N0} bytes");
            _statusBar.FileStatsText.Text = string.Format(format, bytes);
        }

        public void UpdateTotalLines(OpenedTab tab)
        {
            if (tab == null || !_isActiveTab(tab))
            {
                return;
            }

            if (tab.IsImageViewer)
            {
                _statusBar.TotalLinesText.Text = _getString("StatusImageViewer", "이미지");
                return;
            }

            if (tab.IsPdfViewer)
            {
                _statusBar.TotalLinesText.Text = "PDF";
                return;
            }

            int totalLines = _sessionProvider(tab.Id)?.Model.LineCount ?? 1;
            string format = _getString("StatusTotalLinesFormat", "전체 줄수: {0}");
            _statusBar.TotalLinesText.Text = string.Format(format, totalLines);
        }

        public void UpdateSelectionStats(string? selectedText)
        {
            if (string.IsNullOrEmpty(selectedText))
            {
                _statusBar.StatusSelectionStatsText.Visibility = Visibility.Collapsed;
                _statusBar.StatusSelectionStatsText.Text = string.Empty;
                return;
            }

            int charCount = selectedText.Length;
            int tokenCount = EstimateTokenCount(selectedText);
            int wordCount = selectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            int lineCount = selectedText.Replace("\r\n", "\n").Split('\n').Length;

            string format = _getString("StatusSelectionStatsFormat", "선택됨: {0}자 / {1}토큰 / {2}단어 / {3}줄");
            _statusBar.StatusSelectionStatsText.Text = string.Format(
                format,
                charCount.ToString("N0"),
                tokenCount.ToString("N0"),
                wordCount.ToString("N0"),
                lineCount.ToString("N0"));
            _statusBar.StatusSelectionStatsText.Visibility = Visibility.Visible;
        }

        public void SyncEncodingCombo(OpenedTab tab)
        {
            if (tab.IsReadOnlyViewer)
            {
                return;
            }

            try
            {
                _isSyncingEncodingCombo = true;
                string encodingName = string.IsNullOrWhiteSpace(tab.EncodingName) ? "UTF-8" : tab.EncodingName;
                _statusBar.EncodingCombo.SelectedItem = _statusBar.EncodingCombo.Items.Contains(encodingName)
                    ? encodingName
                    : "UTF-8";
            }
            finally
            {
                _isSyncingEncodingCombo = false;
            }
        }

        public void SetCursorPosition(int line, int column)
        {
            _statusBar.LineText.Text = line.ToString();
            _statusBar.ColumnText.Text = column.ToString();
        }

        public void SyncLineEndingText(OpenedTab tab)
        {
            if (tab.IsReadOnlyViewer)
            {
                _statusBar.LineEndingText.Text = "";
                return;
            }

            string lineEnding = _sessionProvider(tab.Id)?.Model.LineEnding == "\r\n" ? "CRLF" : "LF";
            _statusBar.LineEndingText.Text = lineEnding;
        }

        public void UpdateLanguage(OpenedTab tab)
        {
            if (tab == null)
            {
                return;
            }

            if (tab.IsImageViewer)
            {
                _statusBar.LanguageText.Text = "IMAGE";
                return;
            }

            if (tab.IsPdfViewer)
            {
                _statusBar.LanguageText.Text = "PDF";
                return;
            }

            if (tab.IsDocxViewer)
            {
                string extension = string.IsNullOrWhiteSpace(tab.FilePath)
                    ? string.Empty
                    : Path.GetExtension(tab.FilePath);
                _statusBar.LanguageText.Text = extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase)
                    ? "HWPX"
                    : "DOCX";
                return;
            }

            string detected = tab.Language;
            if (detected == "plaintext" || string.IsNullOrEmpty(detected))
            {
                string content = tab.Content;
                if (_sessionProvider(tab.Id) is { } session)
                {
                    content = session.GetText(2000);
                }
                detected = _languageDetectionService.DetectLanguageFromContent(content, "plaintext");
            }

            _statusBar.LanguageText.Text = detected.ToUpper();

            if (tab.Language != detected)
            {
                tab.Language = detected;
                if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    _ = bridgeGroup.Bridge.SetLanguageAsync(detected);
                }
            }
        }

        private async void OnEncodingSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingEncodingCombo)
            {
                return;
            }

            if (_statusBar.EncodingCombo.SelectedItem is not string selectedEncoding)
            {
                return;
            }

            var tab = _activeTabProvider();
            if (tab == null)
            {
                return;
            }

            if (tab.IsReadOnlyViewer)
            {
                SyncEncodingCombo(tab);
                return;
            }

            if (string.IsNullOrWhiteSpace(tab.FilePath) || !File.Exists(tab.FilePath))
            {
                tab.EncodingName = selectedEncoding == "Auto" ? "UTF-8" : selectedEncoding;
                tab.EncodingWasAutoDetected = false;
                return;
            }

            if (selectedEncoding == "Auto")
            {
                await _reloadTabWithEncodingAsync(tab, selectedEncoding);
                return;
            }

            string dirtyWarning = tab.IsDirty
                ? _getString("EncodingChangeDirtyWarning", "\n\n(주의: '다시 읽기'를 선택하면 저장하지 않은 변경 사항이 유실됩니다!)")
                : string.Empty;

            string contentFormat = _getString(
                "EncodingChangeContentFormat",
                "현재 열려 있는 파일의 인코딩을 '{0}'(으)로 변경하시겠습니까?\n\n- 변환: 현재 편집 중인 텍스트를 유지하고 파일 인코딩 형식을 변환하여 저장합니다.\n- 다시 읽기: 저장된 파일을 해당 인코딩으로 다시 로드합니다.{1}");

            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            var dialog = new ContentDialog
            {
                Title = _getString("EncodingChangeTitle", "인코딩 변경"),
                Content = string.Format(contentFormat, selectedEncoding, dirtyWarning),
                PrimaryButtonText = _getString("EncodingChangeConvert", "변환"),
                SecondaryButtonText = _getString("EncodingChangeReopen", "다시 읽기"),
                CloseButtonText = _getString("EncodingChangeCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            var result = await dialog.ShowAsync();
            if (terminalWasVisible)
            {
                _resumeTerminal();
            }

            if (result == ContentDialogResult.Primary)
            {
                tab.EncodingName = selectedEncoding;
                _markTabDirty(tab);
                SyncEncodingCombo(tab);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await _reloadTabWithEncodingAsync(tab, selectedEncoding);
            }
            else
            {
                SyncEncodingCombo(tab);
            }
        }

        private async void OnLineNumberClick(object sender, RoutedEventArgs e)
        {
            var activeTab = _activeTabProvider();
            if (activeTab == null)
            {
                return;
            }

            var lineBox = new TextBox
            {
                PlaceholderText = _getString("GoToLinePlaceholder", "이동할 줄 번호 입력..."),
                Width = 200
            };
            int currentLine = int.TryParse(_statusBar.LineText.Text, out int line) ? line : 1;
            lineBox.Text = currentLine.ToString();

            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            var dialog = new ContentDialog
            {
                Title = _getString("GoToLineTitle", "줄 이동 (Go to Line)"),
                Content = lineBox,
                PrimaryButtonText = _getString("GoToLineButton", "이동"),
                CloseButtonText = _getString("GoToLineCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            lineBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    if (int.TryParse(lineBox.Text, out int targetLine) && targetLine > 0)
                    {
                        dialog.Hide();
                        if (terminalWasVisible)
                        {
                            _resumeTerminal();
                        }

                        await _revealLineAsync(activeTab.Id, targetLine);
                    }
                }
            };

            var result = await dialog.ShowAsync();
            if (terminalWasVisible)
            {
                _resumeTerminal();
            }

            if (result == ContentDialogResult.Primary &&
                int.TryParse(lineBox.Text, out int clickedLine) &&
                clickedLine > 0)
            {
                await _revealLineAsync(activeTab.Id, clickedLine);
            }
        }

        private void OnLineEndingClick(object sender, RoutedEventArgs e)
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                return;
            }

            if (tab.IsReadOnlyViewer)
            {
                return;
            }

            string currentLineEnding = _sessionProvider(tab.Id)?.Model.LineEnding == "\r\n" ? "CRLF" : "LF";

            var flyout = new MenuFlyout();
            var lfItem = new MenuFlyoutItem { Text = "LF" };
            var crlfItem = new MenuFlyoutItem { Text = "CRLF" };

            lfItem.Click += async (_, _) =>
            {
                if (currentLineEnding == "LF")
                {
                    return;
                }

                await ChangeLineEndingWithPopupAsync(tab, "LF");
            };

            crlfItem.Click += async (_, _) =>
            {
                if (currentLineEnding == "CRLF")
                {
                    return;
                }

                await ChangeLineEndingWithPopupAsync(tab, "CRLF");
            };

            flyout.Items.Add(lfItem);
            flyout.Items.Add(crlfItem);
            if (sender is Button button)
            {
                flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
            }
        }

        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            var tab = _activeTabProvider();
            if (tab == null || tab.IsReadOnlyViewer)
            {
                return;
            }

            string currentLanguage = string.IsNullOrWhiteSpace(tab.Language) ? "plaintext" : tab.Language;
            if (!string.Equals(currentLanguage, "plaintext", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var flyout = new MenuFlyout();
            var markdownItem = new MenuFlyoutItem { Text = _getString("Markdown", "Markdown") };

            markdownItem.Click += async (_, _) => await ChangeLanguageAsync(tab, "markdown");

            flyout.Items.Add(markdownItem);
            if (sender is Button button)
            {
                flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
            }
        }

        private async Task ChangeLanguageAsync(OpenedTab tab, string targetLanguage)
        {
            if (tab.IsReadOnlyViewer ||
                string.Equals(tab.Language, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            tab.Language = targetLanguage;
            _statusBar.LanguageText.Text = targetLanguage.ToUpperInvariant();

            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.SetLanguageAsync(targetLanguage);
            }

            _updateLivePreview(tab);
        }

        private async Task ChangeLineEndingWithPopupAsync(OpenedTab tab, string targetEnding)
        {
            string contentFormat = _getString(
                "LineEndingChangeContentFormat",
                "현재 열려 있는 파일의 줄 끝 방식을 '{0}'(으)로 변환하시겠습니까?");

            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            var dialog = new ContentDialog
            {
                Title = _getString("LineEndingChangeTitle", "줄 끝 방식 변경"),
                Content = string.Format(contentFormat, targetEnding),
                PrimaryButtonText = _getString("LineEndingChangeConvert", "변환"),
                CloseButtonText = _getString("LineEndingChangeCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            var result = await dialog.ShowAsync();
            if (terminalWasVisible)
            {
                _resumeTerminal();
            }

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var session = _sessionProvider(tab.Id);
            if (session != null)
            {
                session.Model.LineEnding = targetEnding == "CRLF" ? "\r\n" : "\n";
            }

            _markTabDirty(tab);
            _statusBar.LineEndingText.Text = targetEnding;
        }

        public static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            int cjkCount = 0;
            int spaceCount = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 0xAC00 && c <= 0xD7A3) || // Hangul Syllables
                    (c >= 0x1100 && c <= 0x11FF) || // Hangul Jamo
                    (c >= 0x3130 && c <= 0x318F) || // Hangul Compatibility Jamo
                    (c >= 0x4E00 && c <= 0x9FFF) || // CJK Unified Ideographs
                    (c >= 0x3040 && c <= 0x309F) || // Hiragana
                    (c >= 0x30A0 && c <= 0x30FF))   // Katakana
                {
                    cjkCount++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    spaceCount++;
                }
            }

            double estLr = cjkCount * 0.418 + spaceCount * 3.95 - words * 2.67;
            int minBound = Math.Max(words, (int)Math.Round(cjkCount * 0.5));
            return Math.Max(minBound, (int)Math.Round(estLr));
        }
    }
}
