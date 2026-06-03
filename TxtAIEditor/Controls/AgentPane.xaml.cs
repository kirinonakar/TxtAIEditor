using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TxtAIEditor.Controls
{
    public sealed partial class AgentPane : UserControl
    {
        private int _outputLength;

        public AgentPane()
        {
            InitializeComponent();
            _outputLength = AgentOutputText.Text?.Length ?? 0;
        }

        public event RoutedEventHandler? RunRequested;
        public event RoutedEventHandler? StopRequested;
        public event RoutedEventHandler? NewSessionRequested;
        public event RoutedEventHandler? InsertOutputRequested;
        public event RoutedEventHandler? DiffApproved;
        public event RoutedEventHandler? DiffCancelled;

        public TextBox Output => AgentOutputText;
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

        private void AppendText(string text)
        {
            int currentLength = AgentOutputText.Text.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }
            AgentOutputText.IsReadOnly = false;
            AgentOutputText.Select(_outputLength, 0);
            AgentOutputText.SelectedText = text;
            AgentOutputText.IsReadOnly = true;
            _outputLength += text.Length;
        }

        public void Localize(Func<string, string, string> getString)
        {
            string outputText = AgentOutputText.Text.TrimStart();
            if (outputText.StartsWith("대기 중...", StringComparison.Ordinal) ||
                outputText.StartsWith("Waiting...", StringComparison.Ordinal) ||
                outputText.StartsWith("待機中...", StringComparison.Ordinal))
            {
                AgentOutputText.Text = getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요.");
                _outputLength = AgentOutputText.Text.Length;
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

            string currentText = AgentOutputText.Text;
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

            string currentText = AgentOutputText.Text;
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

            string currentText = AgentOutputText.Text;
            int lineBreakLength = 0;
            if (!string.IsNullOrEmpty(currentText) &&
                !EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
                lineBreakLength = OutputLineBreak.Length;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _thinkingLinePrefix = $"{timestamp}  {label}";
            _thinkingLineStart = currentText.Length + lineBreakLength;
            _thinkingDotCount = 0;
            _thinkingLineActive = true;
            AppendText(_thinkingLinePrefix);
            ScrollOutputToEnd();

            _thinkingTimer ??= CreateThinkingTimer();
            _thinkingTimer.Start();
        }

        public void StopThinkingActivity()
        {
            CompleteThinkingLine();
        }

        private void ClearOutputPlaceholder()
        {
            string text = AgentOutputText.Text;
            if (text.Length > 100)
            {
                return;
            }

            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("대기 중...", StringComparison.Ordinal) ||
                trimmed.StartsWith("Waiting...", StringComparison.Ordinal) ||
                trimmed.StartsWith("待機中...", StringComparison.Ordinal))
            {
                AgentOutputText.Text = string.Empty;
                _outputLength = 0;
            }
            else
            {
                _outputLength = text.Length;
            }
        }

        public void ResetOutput(string text)
        {
            AgentOutputText.Text = text;
            _outputLength = text?.Length ?? 0;
        }

        private void ScrollOutputToEnd()
        {
            int currentLength = AgentOutputText.Text.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }
            AgentOutputText.SelectionStart = _outputLength;
            AgentOutputText.SelectionLength = 0;
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
            string currentText = AgentOutputText.Text;
            if (!EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
            }

            _thinkingLineActive = false;
            ScrollOutputToEnd();
        }

        private void ReplaceThinkingLine(string text)
        {
            int currentLength = AgentOutputText.Text.Length;
            if (_thinkingLineStart < 0 || _thinkingLineStart > currentLength)
            {
                return;
            }

            AgentOutputText.Text = AgentOutputText.Text.Substring(0, _thinkingLineStart) + text;
            _outputLength = _thinkingLineStart + text.Length;
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
    }
}
