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

        public string Text => _pane.RawOutputText;
        public string SelectedText => _pane.SelectedOutputText;
    }

    public sealed partial class AgentPane : UserControl
    {
        private int _outputLength;
        private string _rawOutputText = string.Empty;
        private readonly List<string> _renderedLines = new List<string>();

        public string RawOutputText => _rawOutputText;
        public string SelectedOutputText => AgentOutputText.SelectedText;

        public AgentPane()
        {
            InitializeComponent();
            
            string placeholder = "대기 중... Agent에게 작업을 지시해 보세요.";
            try
            {
                var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                string localized = loader.GetString("AgentOutputPlaceholder");
                if (!string.IsNullOrEmpty(localized))
                {
                    placeholder = localized;
                }
            }
            catch
            {
            }

            _rawOutputText = placeholder;
            UpdateRichText(_rawOutputText);
            _outputLength = _rawOutputText.Length;
        }

        public event RoutedEventHandler? RunRequested;
        public event RoutedEventHandler? StopRequested;
        public event RoutedEventHandler? NewSessionRequested;
        public event RoutedEventHandler? InsertOutputRequested;
        public event RoutedEventHandler? DiffApproved;
        public event RoutedEventHandler? DiffCancelled;
        public event EventHandler<AgentFileEditPreview>? FileRevertRequested;
        public event EventHandler<AgentFileEditPreview>? FileDiffRequested;

        public AgentOutputWrapper Output => new AgentOutputWrapper(this);
        public TextBox Prompt => AgentPromptInput;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public TextBlock TokenCount => AgentTokenCountText;
        public CheckBox IncludeActiveFileCheckBox => AgentIncludeActiveFileCheckBox;
        public bool IncludeActiveFile => AgentIncludeActiveFileCheckBox.IsChecked == true;

        private bool _isBusy;
        private string _runButtonText = "실행";
        private string _stopButtonText = "중단";
        private const string OutputLineBreak = "\r\n";
        private DispatcherTimer? _thinkingTimer;
        private bool _thinkingLineActive;
        private int _thinkingLineStart;
        private int _thinkingDotCount;
        private string _thinkingLinePrefix = string.Empty;
        private string _thinkingLineTimestamp = string.Empty;

        private void AppendText(string text)
        {
            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }
            _rawOutputText = _rawOutputText.Insert(_outputLength, text);
            _outputLength += text.Length;
            UpdateRichText(_rawOutputText);
        }

        public void Localize(Func<string, string, string> getString)
        {
            string outputText = _rawOutputText.TrimStart();
            bool isPlaceholder = false;
            try
            {
                var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                string localized = loader.GetString("AgentOutputPlaceholder")?.TrimStart() ?? string.Empty;
                if (!string.IsNullOrEmpty(localized) && outputText.StartsWith(localized, System.StringComparison.Ordinal))
                {
                    isPlaceholder = true;
                }
            }
            catch
            {
            }

            if (isPlaceholder ||
                outputText.StartsWith("대기 중...", StringComparison.Ordinal) ||
                outputText.StartsWith("Waiting...", StringComparison.Ordinal) ||
                outputText.StartsWith("待機中...", StringComparison.Ordinal) ||
                outputText.StartsWith("待機中...", StringComparison.Ordinal))
            {
                _rawOutputText = getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요.");
                UpdateRichText(_rawOutputText);
                _outputLength = _rawOutputText.Length;
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentIncludeActiveFileCheckBox.Content = getString("AgentIncludeActiveFile", "현재 탭 포함");
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            _runButtonText = getString("AgentRunButton", "실행");
            _stopButtonText = getString("AgentStopButton", "중단");
            AgentRunButton.Content = _isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.Content = getString("AgentNewSessionButton", "새 세션");
            AgentInsertOutputButton.Content = getString("LlmInsertOutputButtonText", "입력");
            AgentActivityHeaderText.Text = getString("AgentActivityHeader", "진행 상황");
            if (AgentActivityText.Text == "대기 중" || AgentActivityText.Text == "Idle")
            {
                AgentActivityText.Text = getString("AgentActivityIdle", "대기 중");
            }
            ToolTipService.SetToolTip(AgentInsertOutputButton, getString("AgentInsertOutputTooltip", "Agent 응답을 현재 커서에 입력"));

            AgentDiffApproveButton.Content = getString("AgentDiffApplyButton", "승인");
            AgentDiffCancelButton.Content = getString("AgentDiffCancelButton", "취소");
            AgentDiffConfirmHeader.Text = getString("AgentDiffConfirmHeaderDefault", "파일 변경 확인");
            AgentDiffConfirmDescription.Text = getString("AgentDiffConfirmDescriptionDefault", "파일을 수정하시겠습니까?");
            AgentModifiedFilesHeader.Text = getString("AgentModifiedFilesHeader", "변경됨 (더블클릭 시 비교)");
            AgentModifiedFilesDescription.Text = getString("AgentModifiedFilesDescription", "수정된 파일 목록입니다. 되돌리려면 우측 아이콘을 클릭하세요.");
            ToolTipService.SetToolTip(AgentModifiedFilesCloseButton, getString("AgentModifiedFilesCloseTooltip", "목록 닫기"));
        }

        public void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            AgentRunButton.IsEnabled = true;
            AgentRunButton.Content = isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.IsEnabled = !isBusy;
            AgentPromptInput.IsEnabled = !isBusy;
            AgentIncludeActiveFileCheckBox.IsEnabled = !isBusy;
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
                AgentActivityText.Text == "대기 중" ||
                AgentActivityText.Text == "Idle")
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
            AppendText(text);
            ScrollOutputToEnd();
        }

        public void AppendOutputLine(string line)
        {
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
            ScrollOutputToEnd();
        }

        public void BeginThinkingActivity(string label)
        {
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
            ScrollOutputToEnd();

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
            if (trimmed.StartsWith("대기 중...", StringComparison.Ordinal) ||
                trimmed.StartsWith("Waiting...", StringComparison.Ordinal) ||
                trimmed.StartsWith("待機中...", StringComparison.Ordinal))
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
            _rawOutputText = text ?? string.Empty;
            UpdateRichText(_rawOutputText);
            _outputLength = _rawOutputText.Length;
        }

        private void ScrollOutputToEnd()
        {
            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }
            AgentOutputScrollViewer.ChangeView(null, AgentOutputScrollViewer.ScrollableHeight, null);
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
            UpdateRichText(_rawOutputText);
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

        private void OnInsertOutputClick(object sender, RoutedEventArgs e)
        {
            InsertOutputRequested?.Invoke(sender, e);
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
                string textToCopy = AgentOutputText.SelectedText;
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
                    Brush? bgBrush = null;
                    Brush? fgBrush = null;

                    if (Application.Current.Resources.ContainsKey("AgentCodeBackground"))
                    {
                        bgBrush = Application.Current.Resources["AgentCodeBackground"] as Brush;
                    }
                    else
                    {
                        bgBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray);
                    }

                    if (Application.Current.Resources.ContainsKey("AgentCodeForeground"))
                    {
                        fgBrush = Application.Current.Resources["AgentCodeForeground"] as Brush;
                    }

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

                    if (fgBrush != null)
                    {
                        textBlock.Foreground = fgBrush;
                    }

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

        public void ShowDiffConfirm(string header, string description)
        {
            AgentDiffConfirmHeader.Text = header;
            AgentDiffConfirmDescription.Text = description;
            AgentDiffConfirmPanel.Visibility = Visibility.Visible;
        }

        public void HideDiffConfirm()
        {
            AgentDiffConfirmPanel.Visibility = Visibility.Collapsed;
        }

        public void UpdateModifiedFiles(IReadOnlyList<AgentFileEditPreview> edits)
        {
            AgentModifiedFilesList.ItemsSource = edits;
            AgentModifiedFilesPanel.Visibility = edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        }

        public void UpdateModelName(string text)
        {
            AgentModelNameText.Text = text;
        }
    }
}
