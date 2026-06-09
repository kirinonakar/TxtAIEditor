using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace TxtAIEditor.Controls
{
    public sealed class AgentOutputWrapper
    {
        private readonly AgentPane _pane;
        public AgentOutputWrapper(AgentPane pane)
        {
            _pane = pane;
        }

        public string Text => _pane.GetRawOutputText();
        public string SelectedText => _pane.SelectedOutputText;
    }

    public sealed class AgentAttachmentItem
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string TokenText { get; set; } = string.Empty;
        public string RemoveTooltip { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "\uE8A5";
    }

    public sealed class AgentHistoryItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string TimeText { get; set; } = string.Empty;
    }

    public sealed partial class AgentPane : UserControl
    {
        private int _outputLength;
        private string _rawOutputText = string.Empty;
        private readonly List<string> _renderedLines = new List<string>();
        private readonly StringBuilder _pendingOutputText = new StringBuilder();
        private bool _outputScrollQueued;
        private bool _userScrolledUp;
        private double _lastVerticalOffset;
        private DispatcherTimer? _outputFlushTimer;
        private AgentDisplayLocalizer _displayText = AgentDisplayLocalizer.CreateWithResourceLoader();
        private string _explicitSelectedOutputText = string.Empty;
        private Windows.Foundation.Point? _outputPointerDownPoint;
        private bool _outputPointerSelectionGesture;
        private bool _hasExplicitOutputSelection;
        private List<string> _agentPresetNames = new List<string>();
        private HashSet<string> _selectedAgentPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Func<string, string, string>? _getString;

        public string RawOutputText => GetRawOutputText();
        public string SelectedOutputText
        {
            get
            {
                if (_hasExplicitOutputSelection)
                {
                    CaptureExplicitOutputSelection();
                }

                return _explicitSelectedOutputText;
            }
        }

        public AgentPane()
        {
            InitializeComponent();
            AgentOutputText.SizeChanged += (_, _) => QueueOutputScrollToEnd();
            AgentOutputText.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnOutputPointerPressed),
                true);
            AgentOutputText.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(OnOutputPointerMoved),
                true);
            AgentOutputText.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnOutputPointerReleased),
                true);

            ResetOutput(_displayText.OutputPlaceholder);
            Localize(_displayText.GetString);
        }

        public event RoutedEventHandler? RunRequested;
        public event RoutedEventHandler? StopRequested;
        public event RoutedEventHandler? NewSessionRequested;
        public event RoutedEventHandler? InsertOutputRequested;
        public event RoutedEventHandler? InsertNewTabOutputRequested;
        public event RoutedEventHandler? AddAttachmentRequested;
        public event EventHandler<AgentAttachmentItem>? RemoveAttachmentRequested;
        public event RoutedEventHandler? AgentPresetAddRequested;
        public event EventHandler<string>? AgentPresetToggled;
        public event EventHandler<string>? AgentPresetEdited;
        public event EventHandler<string>? AgentPresetDeleted;
        public event EventHandler<string>? AgentPresetRemoved;
        public event RoutedEventHandler? AgentPresetExportRequested;
        public event RoutedEventHandler? AgentPresetImportRequested;
        public event RoutedEventHandler? DiffApproved;
        public event RoutedEventHandler? DiffCancelled;
        public event EventHandler<AgentFileEditPreview>? FileRevertRequested;
        public event EventHandler<AgentFileEditPreview>? FileDiffRequested;

        public event EventHandler<string>? HistorySelected;
        public event EventHandler<string>? HistoryDeleted;
        public event RoutedEventHandler? HistoryToolbarDeleteClicked;

        private List<AgentHistoryItemViewModel> _historyItems = new List<AgentHistoryItemViewModel>();
        private string? _selectedHistoryId;

        public AgentOutputWrapper Output => new AgentOutputWrapper(this);
        public TextBox Prompt => AgentPromptInput;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public TextBlock TokenCount => AgentTokenCountText;
        public CheckBox IncludeActiveFileCheckBox => AgentIncludeActiveFileCheckBox;
        public bool IncludeActiveFile => AgentIncludeActiveFileCheckBox.IsChecked == true;

        private bool _isBusy;
        private string _runButtonText = string.Empty;
        private string _stopButtonText = string.Empty;
        private const string OutputLineBreak = "\r\n";
        private DispatcherTimer? _thinkingTimer;
        private bool _thinkingLineActive;
        private int _thinkingLineStart;
        private int _thinkingDotCount;
        private string _thinkingLinePrefix = string.Empty;
        private string _thinkingLineTimestamp = string.Empty;
        private const int MaxOutputFlushChars = 20_000;

        private void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }

            bool appendingAtEnd = _outputLength == currentLength;
            if (appendingAtEnd)
            {
                _rawOutputText += text;
                _outputLength += text.Length;
                AppendRenderedText(text);
            }
            else
            {
                _rawOutputText = _rawOutputText.Insert(_outputLength, text);
                _outputLength += text.Length;
                UpdateRichText(_rawOutputText);
            }
        }

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
            _displayText = new AgentDisplayLocalizer(getString);
            string outputText = _rawOutputText.TrimStart();
            if (_displayText.IsOutputPlaceholder(outputText))
            {
                ResetOutput(_displayText.OutputPlaceholder);
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentIncludeActiveFileCheckBox.Content = getString("AgentIncludeActiveFile", "현재 탭 포함");
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            ToolTipService.SetToolTip(AgentAddAttachmentButton, getString("AgentAddAttachmentTooltip", "이미지 또는 파일 추가"));
            ToolTipService.SetToolTip(AgentPresetButton, getString("AgentPresetButtonTooltip", "페르소나/지침 프리셋"));
            AgentAddPresetText.Text = getString("AgentPresetAddText", "프리셋 추가");
            AgentExportPresetText.Text = getString("PresetExportText", "내보내기");
            AgentImportPresetText.Text = getString("PresetImportText", "가져오기");
            _runButtonText = getString("AgentRunButton", "실행");
            _stopButtonText = getString("AgentStopButton", "중단");
            AgentRunButton.Content = _isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.Content = getString("AgentNewSessionButton", "새 세션");
            ToolTipService.SetToolTip(AgentHistoryButton, getString("AgentHistoryTooltip", "세션 히스토리"));
            ToolTipService.SetToolTip(AgentDeleteHistoryButton, getString("AgentDeleteHistoryTooltip", "히스토리 삭제"));
            AgentInsertOutputButton.Content = getString("LlmInsertOutputButtonText", "입력");
            AgentActivityHeaderText.Text = getString("AgentActivityHeader", "진행 상황");
            if (_displayText.IsActivityIdle(AgentActivityText.Text))
            {
                AgentActivityText.Text = _displayText.ActivityIdle;
            }
            ToolTipService.SetToolTip(AgentInsertOutputButton, getString("AgentInsertOutputTooltip", "Agent 응답을 현재 커서에 입력"));

            AgentDiffApproveButton.Content = getString("AgentDiffApplyButton", "승인");
            AgentDiffCancelButton.Content = getString("AgentDiffCancelButton", "취소");
            AgentDiffConfirmHeader.Text = getString("AgentDiffConfirmHeaderDefault", "파일 변경 확인");
            AgentDiffConfirmDescription.Text = getString("AgentDiffConfirmDescriptionDefault", "파일을 수정하시겠습니까?");
            AgentModifiedFilesHeader.Text = getString("AgentModifiedFilesHeader", "변경됨 (더블클릭 시 비교)");
            AgentModifiedFilesDescription.Text = getString("AgentModifiedFilesDescription", "수정된 파일 목록입니다. 되돌리려면 우측 아이콘을 클릭하세요.");
            ToolTipService.SetToolTip(AgentModifiedFilesCloseButton, getString("AgentModifiedFilesCloseTooltip", "목록 닫기"));
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void SetBusy(bool isBusy)
        {
            if (!isBusy)
            {
                FlushAllPendingOutputText();
            }

            _isBusy = isBusy;
            AgentRunButton.IsEnabled = true;
            AgentRunButton.Content = isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.IsEnabled = !isBusy;
            AgentHistoryButton.IsEnabled = !isBusy;
            AgentDeleteHistoryButton.IsEnabled = !isBusy;
            AgentPromptInput.IsEnabled = !isBusy;
            AgentIncludeActiveFileCheckBox.IsEnabled = !isBusy;
            AgentAddAttachmentButton.IsEnabled = !isBusy;
            AgentAttachmentsList.IsEnabled = !isBusy;
            AgentPresetButton.IsEnabled = !isBusy;
            AgentSelectedPresetScrollViewer.IsHitTestVisible = !isBusy;
            AgentSelectedPresetScrollViewer.Opacity = isBusy ? 0.65 : 1.0;

            if (!isBusy)
            {
                ScrollOutputToEnd(true);
            }
        }

        public void ClearActivity(string idleText)
        {
            AgentActivityText.Text = idleText;
        }

        public void AppendActivity(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            AppendOutputLine($"{timestamp}  {message}");
            if (string.IsNullOrWhiteSpace(AgentActivityText.Text) ||
                _displayText.IsActivityIdle(AgentActivityText.Text))
            {
                AgentActivityText.Text = $"{timestamp}  {message}";
            }
            else
            {
                AgentActivityText.Text += Environment.NewLine + $"{timestamp}  {message}";
            }

            AgentActivityText.SelectionStart = AgentActivityText.Text.Length;
            AgentActivityText.SelectionLength = 0;
        }

        public void AppendOutputText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            CompleteThinkingLine();
            ClearOutputPlaceholder();
            QueueOutputText(text);
        }

        public void AppendOutputLine(string line)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            if (!string.IsNullOrEmpty(currentText) &&
                !EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
            }

            AppendText(line + OutputLineBreak);
            ScrollOutputToEnd();
        }

        public void BeginOutputBlock(string title)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            if (!string.IsNullOrWhiteSpace(currentText))
            {
                if (!EndsWithBlankLine(currentText))
                {
                    AppendText(EndsWithLineBreak(currentText)
                        ? OutputLineBreak
                        : OutputLineBreak + OutputLineBreak);
                }
            }

            AppendText(title + OutputLineBreak);
            ScrollOutputToEnd(true);
        }

        public void BeginThinkingActivity(string label)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            int lineBreakLength = 0;
            if (!string.IsNullOrEmpty(currentText) &&
                !EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
                lineBreakLength = OutputLineBreak.Length;
            }

            _thinkingLineTimestamp = DateTime.Now.ToString("HH:mm:ss");
            _thinkingLinePrefix = $"{_thinkingLineTimestamp}  {label}";
            _thinkingLineStart = currentText.Length + lineBreakLength;
            _thinkingDotCount = 0;
            _thinkingLineActive = true;
            AppendText(_thinkingLinePrefix);
            ScrollOutputToEnd(true);

            _thinkingTimer ??= CreateThinkingTimer();
            _thinkingTimer.Start();
        }

        public void UpdateThinkingActivity(string label)
        {
            if (!_thinkingLineActive)
            {
                return;
            }
            _thinkingLinePrefix = $"{_thinkingLineTimestamp}  {label}";
            ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
        }

        public void StopThinkingActivity()
        {
            CompleteThinkingLine();
        }

        private void ClearOutputPlaceholder()
        {
            string text = _rawOutputText;
            if (text.Length > 100)
            {
                return;
            }

            string trimmed = text.TrimStart();
            if (_displayText.IsOutputPlaceholder(trimmed))
            {
                _rawOutputText = string.Empty;
                UpdateRichText(_rawOutputText);
                _outputLength = 0;
            }
            else
            {
                _outputLength = text.Length;
            }
        }

        public void ResetOutput(string text)
        {
            ClearPendingOutputText();
            ClearExplicitOutputSelection();
            _rawOutputText = text ?? string.Empty;
            UpdateRichText(_rawOutputText);
            _outputLength = _rawOutputText.Length;
        }

        public string GetRawOutputText()
        {
            FlushAllPendingOutputText();
            return _rawOutputText;
        }

        private void QueueOutputText(string text)
        {
            _pendingOutputText.Append(text);
            _outputFlushTimer ??= CreateOutputFlushTimer();

            if (!_outputFlushTimer.IsEnabled)
            {
                _outputFlushTimer.Start();
            }
        }

        private DispatcherTimer CreateOutputFlushTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (_, _) => FlushPendingOutputText();
            return timer;
        }

        private void FlushPendingOutputText()
        {
            if (_pendingOutputText.Length == 0)
            {
                _outputFlushTimer?.Stop();
                return;
            }

            int take = Math.Min(_pendingOutputText.Length, MaxOutputFlushChars);
            string text = _pendingOutputText.ToString(0, take);
            _pendingOutputText.Remove(0, take);

            AppendText(text);
            ScrollOutputToEnd();

            if (_pendingOutputText.Length == 0)
            {
                _outputFlushTimer?.Stop();
            }
        }

        private void FlushAllPendingOutputText()
        {
            while (_pendingOutputText.Length > 0)
            {
                FlushPendingOutputText();
            }
        }

        private void ClearPendingOutputText()
        {
            _pendingOutputText.Clear();
            _outputFlushTimer?.Stop();
        }

        private void ScrollOutputToEnd(bool force = false)
        {
            if (force)
            {
                _userScrolledUp = false;
            }

            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }

            if (!_userScrolledUp)
            {
                ChangeOutputViewToEnd();
                QueueOutputScrollToEnd();
            }
        }

        private void QueueOutputScrollToEnd()
        {
            if (_outputScrollQueued)
            {
                return;
            }

            _outputScrollQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _outputScrollQueued = false;
                ChangeOutputViewToEnd();
                QueueDeferredOutputScrollToEnd();
            });
        }

        private void QueueDeferredOutputScrollToEnd()
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ChangeOutputViewToEnd);
        }

        private void ChangeOutputViewToEnd()
        {
            AgentOutputScrollViewer.ChangeView(null, double.MaxValue, null, true);
        }

        private void OnAgentOutputScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            double offset = AgentOutputScrollViewer.VerticalOffset;
            double maxOffset = AgentOutputScrollViewer.ScrollableHeight;

            if (offset < _lastVerticalOffset - 1.0)
            {
                _userScrolledUp = true;
            }
            else if (offset >= maxOffset - 5.0)
            {
                _userScrolledUp = false;
            }

            _lastVerticalOffset = offset;
        }

        private DispatcherTimer CreateThinkingTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            timer.Tick += (_, _) =>
            {
                if (!_thinkingLineActive)
                {
                    timer.Stop();
                    return;
                }

                _thinkingDotCount = (_thinkingDotCount + 1) % 4;
                ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
            };
            return timer;
        }

        private void CompleteThinkingLine()
        {
            if (!_thinkingLineActive)
            {
                return;
            }

            _thinkingTimer?.Stop();
            ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
            string currentText = _rawOutputText;
            if (!EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
            }

            _thinkingLineActive = false;
            ScrollOutputToEnd();
        }

        private void ReplaceThinkingLine(string text)
        {
            int currentLength = _rawOutputText.Length;
            if (_thinkingLineStart < 0 || _thinkingLineStart > currentLength)
            {
                return;
            }

            _rawOutputText = _rawOutputText.Substring(0, _thinkingLineStart) + text;
            _outputLength = _thinkingLineStart + text.Length;
            if (_renderedLines.Count > 0)
            {
                SetRenderedLine(_renderedLines.Count - 1, text);
            }
            else
            {
                UpdateRichText(_rawOutputText);
            }
            ScrollOutputToEnd();
        }

        private static bool EndsWithLineBreak(string text)
        {
            return text.EndsWith("\r\n", StringComparison.Ordinal) ||
                   text.EndsWith("\n", StringComparison.Ordinal) ||
                   text.EndsWith("\r", StringComparison.Ordinal);
        }

        private static bool EndsWithBlankLine(string text)
        {
            return text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
                   text.EndsWith("\n\n", StringComparison.Ordinal);
        }

        private bool IsControlDown()
        {
            return IsKeyDown(VirtualKey.Control) ||
                   IsKeyDown(VirtualKey.LeftControl) ||
                   IsKeyDown(VirtualKey.RightControl);
        }

        private static bool IsKeyDown(VirtualKey key)
        {
            return (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
                    Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private void RequestRunFromPrompt()
        {
            if (_isBusy)
            {
                return;
            }

            RunRequested?.Invoke(AgentPromptInput, new RoutedEventArgs());
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                StopRequested?.Invoke(sender, e);
                return;
            }

            RunRequested?.Invoke(sender, e);
        }

        private void OnNewSessionClick(object sender, RoutedEventArgs e)
        {
            NewSessionRequested?.Invoke(sender, e);
        }

        private void OnClearPromptClick(object sender, RoutedEventArgs e)
        {
            AgentPromptInput.Text = string.Empty;
            AgentPromptInput.Focus(FocusState.Programmatic);
        }

        private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
        {
            AgentClearPromptButton.Visibility = string.IsNullOrEmpty(AgentPromptInput.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void OnAgentHistoryFlyoutOpened(object sender, object e)
        {
            RebuildHistoryMenu();
        }

        private void OnDeleteHistoryClick(object sender, RoutedEventArgs e)
        {
            HistoryToolbarDeleteClicked?.Invoke(this, e);
        }

        private void OnInsertOutputClick(object sender, RoutedEventArgs e)
        {
            InsertOutputRequested?.Invoke(sender, e);
        }

        private void OnInsertNewTabOutputClick(object sender, RoutedEventArgs e)
        {
            InsertNewTabOutputRequested?.Invoke(sender, e);
        }

        private void OnOutputPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _outputPointerDownPoint = e.GetCurrentPoint(AgentOutputText).Position;
            _outputPointerSelectionGesture = false;
        }

        private void OnOutputPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_outputPointerDownPoint == null)
            {
                return;
            }

            var currentPoint = e.GetCurrentPoint(AgentOutputText);
            if (!currentPoint.Properties.IsLeftButtonPressed)
            {
                return;
            }

            var startPoint = _outputPointerDownPoint.Value;
            double dx = currentPoint.Position.X - startPoint.X;
            double dy = currentPoint.Position.Y - startPoint.Y;
            if ((dx * dx) + (dy * dy) > 16)
            {
                _outputPointerSelectionGesture = true;
            }
        }

        private void OnOutputPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_outputPointerSelectionGesture)
            {
                _hasExplicitOutputSelection = true;
                QueueCaptureExplicitOutputSelection();
            }
            else
            {
                ClearExplicitOutputSelection();
            }

            _outputPointerDownPoint = null;
            _outputPointerSelectionGesture = false;
        }

        private void OnOutputDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            _hasExplicitOutputSelection = true;
            QueueCaptureExplicitOutputSelection();
        }

        private void QueueCaptureExplicitOutputSelection()
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                CaptureExplicitOutputSelection);
        }

        private void CaptureExplicitOutputSelection()
        {
            if (!_hasExplicitOutputSelection)
            {
                _explicitSelectedOutputText = string.Empty;
                return;
            }

            string selectedText = AgentOutputText.SelectedText;
            _explicitSelectedOutputText = IsTextFromOutput(selectedText)
                ? selectedText
                : string.Empty;
        }

        private void ClearExplicitOutputSelection()
        {
            _explicitSelectedOutputText = string.Empty;
            _hasExplicitOutputSelection = false;
            _outputPointerDownPoint = null;
            _outputPointerSelectionGesture = false;
        }

        private bool IsTextFromOutput(string selectedText)
        {
            if (string.IsNullOrEmpty(selectedText) || string.IsNullOrEmpty(_rawOutputText))
            {
                return false;
            }

            if (_rawOutputText.Contains(selectedText, StringComparison.Ordinal))
            {
                return true;
            }

            string normalizedSelected = NormalizeLineEndings(selectedText);
            string normalizedOutput = NormalizeLineEndings(_rawOutputText);
            return normalizedSelected.Length > 0 &&
                normalizedOutput.Contains(normalizedSelected, StringComparison.Ordinal);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private void OnAddAttachmentClick(object sender, RoutedEventArgs e)
        {
            AddAttachmentRequested?.Invoke(sender, e);
        }

        private void OnAgentAddPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetAddRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentExportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetExportRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentImportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetImportRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentPresetFlyoutOpened(object sender, object e)
        {
            RebuildAgentPresetMenu();
        }

        private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AgentAttachmentItem item)
            {
                RemoveAttachmentRequested?.Invoke(this, item);
            }
        }

        private void OnPromptInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            if (!IsControlDown())
            {
                return;
            }

            e.Handled = true;
            RequestRunFromPrompt();
        }

        private void OnPromptRunKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            RequestRunFromPrompt();
        }

        private void OnDiffApproveClick(object sender, RoutedEventArgs e)
        {
            DiffApproved?.Invoke(sender, e);
        }

        private void OnDiffCancelClick(object sender, RoutedEventArgs e)
        {
            DiffCancelled?.Invoke(sender, e);
        }

        private void OnOutputTextKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.C && IsControlDown())
            {
                _hasExplicitOutputSelection = true;
                CaptureExplicitOutputSelection();
                string textToCopy = _explicitSelectedOutputText;
                if (string.IsNullOrEmpty(textToCopy))
                {
                    textToCopy = _rawOutputText;
                }

                if (!string.IsNullOrEmpty(textToCopy))
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(textToCopy);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                    e.Handled = true;
                }
            }
        }

        private void UpdateRichText(string rawText)
        {
            string normalized = (rawText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            // Adjust the number of blocks in AgentOutputText and cache to match lines.Length
            while (AgentOutputText.Blocks.Count > lines.Length)
            {
                AgentOutputText.Blocks.RemoveAt(AgentOutputText.Blocks.Count - 1);
            }
            while (_renderedLines.Count > lines.Length)
            {
                _renderedLines.RemoveAt(_renderedLines.Count - 1);
            }

            while (AgentOutputText.Blocks.Count < lines.Length)
            {
                AgentOutputText.Blocks.Add(new Paragraph());
            }
            while (_renderedLines.Count < lines.Length)
            {
                _renderedLines.Add(null!);
            }

            // Update only the changed lines
            for (int k = 0; k < lines.Length; k++)
            {
                string line = lines[k];
                if (_renderedLines[k] != line)
                {
                    if (AgentOutputText.Blocks[k] is Paragraph paragraph)
                    {
                        paragraph.Inlines.Clear();
                        ParseLineToInlines(line, paragraph.Inlines);
                    }
                    _renderedLines[k] = line;
                }
            }
        }

        private void AppendRenderedText(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] parts = normalized.Split('\n');

            EnsureRenderedLineExists();

            int lastIndex = _renderedLines.Count - 1;
            SetRenderedLine(lastIndex, _renderedLines[lastIndex] + parts[0]);

            for (int i = 1; i < parts.Length; i++)
            {
                AddRenderedLine(parts[i]);
            }
        }

        private void EnsureRenderedLineExists()
        {
            if (_renderedLines.Count > 0 && AgentOutputText.Blocks.Count > 0)
            {
                return;
            }

            _renderedLines.Clear();
            AgentOutputText.Blocks.Clear();
            AddRenderedLine(string.Empty);
        }

        private void AddRenderedLine(string line)
        {
            var paragraph = new Paragraph();
            AgentOutputText.Blocks.Add(paragraph);
            _renderedLines.Add(string.Empty);
            SetRenderedLine(_renderedLines.Count - 1, line);
        }

        private void SetRenderedLine(int index, string line)
        {
            if (index < 0)
            {
                return;
            }

            while (AgentOutputText.Blocks.Count <= index)
            {
                AgentOutputText.Blocks.Add(new Paragraph());
            }
            while (_renderedLines.Count <= index)
            {
                _renderedLines.Add(string.Empty);
            }

            if (_renderedLines[index] == line)
            {
                return;
            }

            if (AgentOutputText.Blocks[index] is Paragraph paragraph)
            {
                paragraph.Inlines.Clear();
                ParseLineToInlines(line, paragraph.Inlines);
            }
            _renderedLines[index] = line;
        }

        private void ParseLineToInlines(string line, InlineCollection inlines)
        {
            if (string.IsNullOrEmpty(line))
            {
                inlines.Add(new Run { Text = string.Empty });
                return;
            }

            int i = 0;
            bool isBold = false;
            bool isCode = false;
            var currentSegment = new StringBuilder();

            void FlushCurrentSegment()
            {
                if (currentSegment.Length == 0)
                {
                    return;
                }

                string text = currentSegment.ToString();
                currentSegment.Clear();

                if (isCode)
                {
                    var container = new InlineUIContainer();
                    Brush bgBrush = GetBrushResource("AgentCodeBackground", Microsoft.UI.Colors.LightGray);
                    Brush fgBrush = GetBrushResource("AgentCodeForeground", Microsoft.UI.Colors.Black);

                    var border = new Border
                    {
                        Background = bgBrush,
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(2, 2.5, 2, -2.5)
                    };

                    var textBlock = new TextBlock
                    {
                        Text = text,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    textBlock.Foreground = fgBrush;

                    border.Child = textBlock;
                    container.Child = border;
                    inlines.Add(container);
                }
                else
                {
                    var run = new Run { Text = text };
                    if (isBold)
                    {
                        run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    }
                    inlines.Add(run);
                }
            }

            while (i < line.Length)
            {
                if (isCode)
                {
                    if (line[i] == '`')
                    {
                        FlushCurrentSegment();
                        isCode = false;
                        i++;
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
                else
                {
                    if (line[i] == '`')
                    {
                        if (line.IndexOf('`', i + 1) >= 0)
                        {
                            FlushCurrentSegment();
                            isCode = true;
                            i++;
                        }
                        else
                        {
                            currentSegment.Append(line[i]);
                            i++;
                        }
                    }
                    else if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                    {
                        if (isBold)
                        {
                            FlushCurrentSegment();
                            isBold = false;
                            i += 2;
                        }
                        else if (line.IndexOf("**", i + 2, StringComparison.Ordinal) >= 0)
                        {
                            FlushCurrentSegment();
                            isBold = true;
                            i += 2;
                        }
                        else
                        {
                            currentSegment.Append("**");
                            i += 2;
                        }
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
            }

            FlushCurrentSegment();
        }

        private Brush GetBrushResource(string key, Windows.UI.Color fallbackColor)
        {
            if (Resources.TryGetValue(key, out object resource) && resource is Brush localBrush)
            {
                return localBrush;
            }

            if (Application.Current.Resources.TryGetValue(key, out resource) && resource is Brush appBrush)
            {
                return appBrush;
            }

            return new SolidColorBrush(fallbackColor);
        }

        public void UpdateAgentPresetsMenu(
            IReadOnlyList<string> presetNames,
            IReadOnlyCollection<string> selectedPresetNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentPresetNames = new List<string>(presetNames);
            _selectedAgentPresetNames = new HashSet<string>(selectedPresetNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
        }

        private void RebuildAgentPresetMenu()
        {
            if (AgentPresetListPanel == null)
            {
                return;
            }

            AgentPresetListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_agentPresetNames.Count == 0)
            {
                AgentPresetListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentPresetEmptyText", "프리셋 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (string presetName in _agentPresetNames)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = _selectedAgentPresetNames.Contains(presetName);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {presetName}" : presetName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = presetName;
                selectBtn.Click += (_, _) => AgentPresetToggled?.Invoke(this, currentName);
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(editBtn, getString("AgentPresetEditText", "수정"));
                editBtn.Click += (_, _) =>
                {
                    AgentPresetEdited?.Invoke(this, currentName);
                    AgentPresetFlyout.Hide();
                };
                Grid.SetColumn(editBtn, 1);
                rowGrid.Children.Add(editBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentPresetDeleteText", "삭제"));
                deleteBtn.Click += (_, _) => AgentPresetDeleted?.Invoke(this, currentName);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                AgentPresetListPanel.Children.Add(rowGrid);
            }
        }

        private void RebuildSelectedAgentPresetChips()
        {
            if (AgentSelectedPresetPanel == null)
            {
                return;
            }

            AgentSelectedPresetPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            foreach (string presetName in _agentPresetNames)
            {
                if (!_selectedAgentPresetNames.Contains(presetName))
                {
                    continue;
                }

                var chip = new Border
                {
                    Background = (Brush)Application.Current.Resources["SystemControlBackgroundAltMediumLowBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 2, 2)
                };

                var chipContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center
                };
                chipContent.Children.Add(new TextBlock
                {
                    Text = presetName,
                    FontSize = 11,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var removeBtn = new Button
                {
                    Content = "x",
                    Tag = presetName,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(removeBtn, getString("AgentPresetRemoveTooltip", "선택 해제"));
                removeBtn.Click += (_, _) => AgentPresetRemoved?.Invoke(this, presetName);
                chipContent.Children.Add(removeBtn);

                chip.Child = chipContent;
                AgentSelectedPresetPanel.Children.Add(chip);
            }

            AgentSelectedPresetScrollViewer.Visibility =
                AgentSelectedPresetPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowDiffConfirm(string header, string description)
        {
            AgentDiffConfirmHeader.Text = header;
            AgentDiffConfirmDescription.Text = description;
            AgentDiffConfirmPanel.Visibility = Visibility.Visible;
            UpdateReviewPanelsHostVisibility();
        }

        public void HideDiffConfirm()
        {
            AgentDiffConfirmPanel.Visibility = Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        public void UpdateModifiedFiles(IReadOnlyList<AgentFileEditPreview> edits)
        {
            AgentModifiedFilesList.ItemsSource = edits;
            AgentModifiedFilesPanel.Visibility = edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        public void UpdateAttachments(IReadOnlyList<AgentAttachmentItem> attachments)
        {
            AgentAttachmentsList.ItemsSource = attachments;
            AgentAttachmentsList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnModifiedFilesListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (AgentModifiedFilesList.SelectedItem is AgentFileEditPreview preview)
            {
                FileDiffRequested?.Invoke(this, preview);
            }
        }

        private void OnRevertFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AgentFileEditPreview preview)
            {
                FileRevertRequested?.Invoke(this, preview);
            }
        }

        private void OnModifiedFilesCloseClick(object sender, RoutedEventArgs e)
        {
            AgentModifiedFilesPanel.Visibility = Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        private void UpdateReviewPanelsHostVisibility()
        {
            bool hasVisiblePanel =
                AgentDiffConfirmPanel.Visibility == Visibility.Visible ||
                AgentModifiedFilesPanel.Visibility == Visibility.Visible;
            AgentReviewPanelsHost.Visibility = hasVisiblePanel ? Visibility.Visible : Visibility.Collapsed;
        }

        public void UpdateModelName(string text)
        {
            AgentModelNameText.Text = text;
        }

        public void UpdateHistoryItems(List<AgentHistoryItemViewModel> items, string? selectedId)
        {
            _historyItems = items;
            _selectedHistoryId = selectedId;
            RebuildHistoryMenu();
        }

        private void RebuildHistoryMenu()
        {
            if (AgentHistoryListPanel == null)
            {
                return;
            }

            AgentHistoryListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_historyItems.Count == 0)
            {
                AgentHistoryListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentHistoryEmptyText", "히스토리 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var item in _historyItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = string.Equals(_selectedHistoryId, item.Id, StringComparison.Ordinal);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {item.Title} ({item.TimeText})" : $"{item.Title} ({item.TimeText})",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                if (isSelected)
                {
                    selectBtn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                string currentId = item.Id;
                selectBtn.Click += (_, _) =>
                {
                    HistorySelected?.Invoke(this, currentId);
                    AgentHistoryFlyout.Hide();
                };
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentHistoryDeleteText", "삭제"));
                deleteBtn.Click += (_, _) =>
                {
                    HistoryDeleted?.Invoke(this, currentId);
                };
                Grid.SetColumn(deleteBtn, 1);
                rowGrid.Children.Add(deleteBtn);

                AgentHistoryListPanel.Children.Add(rowGrid);
            }
        }
    }
}
