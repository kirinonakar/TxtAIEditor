using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const int HexBytesPerRow = 16;
        private const string HexHeaderOffsetLabel = "Offset(h)";

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
            string? filePath = GetStatsFilePath(tab);
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                bytes = new FileInfo(filePath).Length;
            }

            if (tab.IsImageViewer && ImageFileInfoReader.TryRead(filePath, out var imageInfo))
            {
                string imageFormat = _getString(
                    "StatusImageFileStatsFormat",
                    "크기: {0:N0} bytes / {1:N0} x {2:N0} px");
                _statusBar.FileStatsText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    imageFormat,
                    bytes,
                    imageInfo.Width,
                    imageInfo.Height);
                return;
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

            if (tab.IsHexViewer)
            {
                _statusBar.TotalLinesText.Text = "HEX";
                ToolTipService.SetToolTip(
                    _statusBar.LineNumberButtonControl,
                    _getString("StatusGoToOffsetTooltip", "클릭하여 오프셋 이동"));
                SetHexCursorPosition(tab, line: 2, column: 1);
                return;
            }

            ApplyTextPositionMode();

            if (tab.IsImageViewer)
            {
                _statusBar.TotalLinesText.Text = _getString("StatusImageViewer", "이미지");
                return;
            }

            if (tab.IsMediaViewer)
            {
                _statusBar.TotalLinesText.Text = _getString("StatusMediaViewer", "미디어");
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

        public void ShowTextOperationProgress(
            string operation,
            TextOperationProgress progress,
            Action? cancelAction = null)
        {
            string format = operation switch
            {
                "replaceAll" => _getString("EditorReplaceAllProgressFormat", "바꾸는 중: {0} / {1}"),
                "save" => _getString("EditorSaveProgressFormat", "저장 중: {0} / {1}"),
                "undo" => _getString("EditorUndoProgressFormat", "실행 취소 중: {0} / {1}"),
                "redo" => _getString("EditorRedoProgressFormat", "다시 실행 중: {0} / {1}"),
                _ => _getString("EditorFindAllProgressFormat", "검색 중: {0} / {1}")
            };
            string statusText = string.Format(
                CultureInfo.CurrentCulture,
                format,
                progress.ProcessedLines.ToString("N0", CultureInfo.CurrentCulture),
                progress.TotalLines.ToString("N0", CultureInfo.CurrentCulture));
            double percent = progress.TotalLines <= 0
                ? 0
                : Math.Clamp(progress.ProcessedLines * 100d / progress.TotalLines, 0, 100);
            _statusBar.ShowProgress(statusText, percent, cancelAction);
        }

        public void HideTextOperationProgress()
        {
            _statusBar.HideProgress();
        }

        public void SyncEncodingCombo(OpenedTab tab)
        {
            if (tab.IsReadOnlyViewer && !tab.IsReadOnlyTextFile)
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
            ApplyTextPositionMode();
            _statusBar.LineText.Text = line.ToString();
            _statusBar.ColumnText.Text = column.ToString();
        }

        public void SetHexCursorPosition(OpenedTab tab, int line, int column)
        {
            var (offset, useWideOffset) = CalculateHexOffset(tab, line, column);
            _statusBar.LineLabelText.Text = _getString("StatusHexOffsetLabel", "Offset");
            _statusBar.LineText.Text = "0x" + offset.ToString(useWideOffset ? "X16" : "X8");
            _statusBar.LineColumnSeparatorText.Visibility = Visibility.Collapsed;
            _statusBar.ColumnLabelText.Visibility = Visibility.Collapsed;
            _statusBar.ColumnText.Visibility = Visibility.Collapsed;
        }

        public void UpdateHexSelectionStats(OpenedTab tab, long? offset, long? length)
        {
            if (!offset.HasValue || !length.HasValue || length.Value <= 0)
            {
                _statusBar.StatusSelectionStatsText.Visibility = Visibility.Collapsed;
                _statusBar.StatusSelectionStatsText.Text = string.Empty;
                return;
            }

            long endOffset = offset.Value + length.Value - 1;
            string format = _getString("StatusHexSelectionFormat", "선택됨: Offset {0} - {1} / 길이 {2:N0} bytes");
            _statusBar.StatusSelectionStatsText.Text = string.Format(
                CultureInfo.CurrentCulture,
                format,
                FormatHexOffset(tab, offset.Value),
                FormatHexOffset(tab, endOffset),
                length.Value);
            _statusBar.StatusSelectionStatsText.Visibility = Visibility.Visible;
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
                _statusBar.LanguageText.Text = ImageFileInfoReader.TryRead(tab.FilePath, out var imageInfo)
                    ? imageInfo.Format
                    : ImageFileInfoReader.GetFormatFromExtension(tab.FilePath) ?? "IMAGE";
                return;
            }

            if (tab.IsMediaViewer)
            {
                _statusBar.LanguageText.Text = string.Equals(tab.Language, "audio", StringComparison.OrdinalIgnoreCase)
                    ? "AUDIO"
                    : "VIDEO";
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

            if (tab.IsHexViewer)
            {
                _statusBar.LanguageText.Text = "HEX";
                return;
            }

            string detected = tab.Language;
            if (!tab.IsLanguageManuallySelected &&
                (detected == "plaintext" || string.IsNullOrEmpty(detected)))
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

            if (tab.IsReadOnlyTextFile)
            {
                await _reloadTabWithEncodingAsync(tab, selectedEncoding);
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

            if (activeTab.IsHexViewer)
            {
                await ShowGoToHexOffsetDialogAsync(activeTab);
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
                ShowStatusFlyout(flyout, button);
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
            if (!string.Equals(currentLanguage, "plaintext", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentLanguage, "markdown", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var flyout = new MenuFlyout();
            string targetLanguage = string.Equals(currentLanguage, "markdown", StringComparison.OrdinalIgnoreCase)
                ? "plaintext"
                : "markdown";
            string targetLanguageText = targetLanguage == "plaintext"
                ? "PLAINTEXT"
                : _getString("Markdown", "Markdown");
            var languageItem = new MenuFlyoutItem { Text = targetLanguageText };

            languageItem.Click += async (_, _) => await ChangeLanguageAsync(tab, targetLanguage);

            flyout.Items.Add(languageItem);
            if (sender is Button button)
            {
                ShowStatusFlyout(flyout, button);
            }
        }

        private static void ShowStatusFlyout(MenuFlyout flyout, Button owner)
        {
            CursorResetHelper.AttachToFlyout(flyout, owner);
            CursorResetHelper.ResetToArrow(owner);
            flyout.ShowAt(owner, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
            CursorResetHelper.ResetToArrow(owner);
        }

        private async Task ChangeLanguageAsync(OpenedTab tab, string targetLanguage)
        {
            if (tab.IsReadOnlyViewer ||
                string.Equals(tab.Language, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            tab.Language = targetLanguage;
            tab.IsLanguageManuallySelected = true;
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

        private static string? GetStatsFilePath(OpenedTab tab)
        {
            return !string.IsNullOrWhiteSpace(tab.FilePath)
                ? tab.FilePath
                : tab.HexSourceFilePath;
        }

        private void ApplyTextPositionMode()
        {
            _statusBar.LineLabelText.Text = _getString("StatusLineLabel", "줄");
            _statusBar.ColumnLabelText.Text = _getString("StatusColumnLabel", "열");
            _statusBar.LineColumnSeparatorText.Visibility = Visibility.Visible;
            _statusBar.ColumnLabelText.Visibility = Visibility.Visible;
            _statusBar.ColumnText.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(
                _statusBar.LineNumberButtonControl,
                _getString("StatusGoToLineTooltip", "클릭하여 줄 이동"));
        }

        private static (long Offset, bool UseWideOffset) CalculateHexOffset(OpenedTab tab, int line, int column)
        {
            long fileLength = GetHexSourceLength(tab);
            bool useWideOffset = fileLength > uint.MaxValue;
            long rowOffset = Math.Max(0, line - 2L) * HexBytesPerRow;
            int byteIndex = GetHexByteIndex(useWideOffset, column);
            long offset = Math.Max(0, rowOffset + byteIndex);
            if (fileLength > 0)
            {
                offset = Math.Min(offset, fileLength - 1);
            }

            return (offset, useWideOffset);
        }

        private static int GetHexByteIndex(bool useWideOffset, int column)
        {
            int offsetWidth = Math.Max(HexHeaderOffsetLabel.Length, useWideOffset ? 16 : 8);
            int columnZeroBased = Math.Max(0, column - 1);
            int hexStart = offsetWidth + 2;
            int asciiStart = hexStart + 51;

            if (columnZeroBased >= asciiStart)
            {
                return Math.Clamp(columnZeroBased - asciiStart, 0, HexBytesPerRow - 1);
            }

            if (columnZeroBased < hexStart)
            {
                return 0;
            }

            int previousByteIndex = 0;
            for (int i = 0; i < HexBytesPerRow; i++)
            {
                int byteStart = hexStart + (i * 3) + (i >= 8 ? 1 : 0);
                if (columnZeroBased < byteStart)
                {
                    return previousByteIndex;
                }

                if (columnZeroBased <= byteStart + 2)
                {
                    return i;
                }

                previousByteIndex = i;
            }

            return previousByteIndex;
        }

        private static long GetHexSourceLength(OpenedTab tab)
        {
            try
            {
                string? filePath = tab.HexSourceFilePath;
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    return new FileInfo(filePath).Length;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return 0;
        }

        private async Task ShowGoToHexOffsetDialogAsync(OpenedTab tab)
        {
            var offsetBox = new TextBox
            {
                PlaceholderText = _getString("GoToOffsetPlaceholder", "이동할 offset 입력..."),
                Width = 220,
                Text = _statusBar.LineText.Text
            };

            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            var dialog = new ContentDialog
            {
                Title = _getString("GoToOffsetTitle", "오프셋 이동"),
                Content = offsetBox,
                PrimaryButtonText = _getString("GoToLineButton", "이동"),
                CloseButtonText = _getString("GoToLineCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            offsetBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    if (TryParseHexOffset(offsetBox.Text, out long targetOffset))
                    {
                        dialog.Hide();
                        if (terminalWasVisible)
                        {
                            _resumeTerminal();
                        }

                        await RevealHexOffsetAsync(tab, targetOffset);
                    }
                }
            };

            var result = await dialog.ShowAsync();
            if (terminalWasVisible)
            {
                _resumeTerminal();
            }

            if (result == ContentDialogResult.Primary &&
                TryParseHexOffset(offsetBox.Text, out long clickedOffset))
            {
                await RevealHexOffsetAsync(tab, clickedOffset);
            }
        }

        private async Task RevealHexOffsetAsync(OpenedTab tab, long offset)
        {
            long fileLength = GetHexSourceLength(tab);
            long safeOffset = Math.Max(0, offset);
            if (fileLength > 0)
            {
                safeOffset = Math.Min(safeOffset, fileLength - 1);
            }

            int line = (int)Math.Min(int.MaxValue, (safeOffset / HexBytesPerRow) + 2);
            int column = GetHexColumnForByteIndex(fileLength > uint.MaxValue, (int)(safeOffset % HexBytesPerRow));
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.RevealHexOffsetAsync(safeOffset);
            }
            else
            {
                await _revealLineAsync(tab.Id, line);
            }

            SetHexCursorPosition(tab, line, column);
            UpdateHexSelectionStats(tab, null, null);
        }

        private static bool TryParseHexOffset(string? text, out long offset)
        {
            offset = 0;
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return false;
            }

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value[2..];
            }

            value = value.Replace("_", string.Empty).Replace(" ", string.Empty);
            if (long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hexOffset))
            {
                offset = hexOffset;
                return true;
            }

            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
        }

        private static int GetHexColumnForByteIndex(bool useWideOffset, int byteIndex)
        {
            int offsetWidth = Math.Max(HexHeaderOffsetLabel.Length, useWideOffset ? 16 : 8);
            int safeByteIndex = Math.Clamp(byteIndex, 0, HexBytesPerRow - 1);
            return offsetWidth + 3 + (safeByteIndex * 3) + (safeByteIndex >= 8 ? 1 : 0);
        }

        private static string FormatHexOffset(OpenedTab tab, long offset)
        {
            return "0x" + Math.Max(0, offset).ToString(GetHexSourceLength(tab) > uint.MaxValue ? "X16" : "X8");
        }
    }
}
