using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TxtAIEditor.Controls
{
    public sealed partial class AgentPane : UserControl
    {
        public AgentPane()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler? RunRequested;
        public event RoutedEventHandler? StopRequested;
        public event RoutedEventHandler? PlanRequested;
        public event RoutedEventHandler? EditRequested;
        public event RoutedEventHandler? InsertOutputRequested;

        public TextBox Output => AgentOutputText;
        public TextBox Prompt => AgentPromptInput;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public bool IncludeActiveFile => AgentIncludeActiveFileCheckBox.IsChecked == true;

        private bool _isBusy;
        private string _runButtonText = "실행";
        private string _stopButtonText = "중단";
        private const string OutputLineBreak = "\r\n";

        public void Localize(Func<string, string, string> getString)
        {
            string outputText = AgentOutputText.Text.TrimStart();
            if (outputText.StartsWith("대기 중...", StringComparison.Ordinal) ||
                outputText.StartsWith("Waiting...", StringComparison.Ordinal) ||
                outputText.StartsWith("待機中...", StringComparison.Ordinal))
            {
                AgentOutputText.Text = getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요.");
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentIncludeActiveFileCheckBox.Content = getString("AgentIncludeActiveFile", "현재 탭 포함");
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            _runButtonText = getString("AgentRunButton", "실행");
            _stopButtonText = getString("AgentStopButton", "중단");
            AgentRunButton.Content = _isBusy ? _stopButtonText : _runButtonText;
            AgentPlanButton.Content = getString("AgentPlanButton", "계획");
            AgentEditButton.Content = getString("AgentEditButton", "수정안");
            AgentInsertOutputButton.Content = getString("LlmInsertOutputButtonText", "입력");
            AgentActivityHeaderText.Text = getString("AgentActivityHeader", "진행 상황");
            if (AgentActivityText.Text == "대기 중" || AgentActivityText.Text == "Idle")
            {
                AgentActivityText.Text = getString("AgentActivityIdle", "대기 중");
            }
            ToolTipService.SetToolTip(AgentInsertOutputButton, getString("AgentInsertOutputTooltip", "Agent 응답을 현재 커서에 입력"));
        }

        public void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            AgentRunButton.IsEnabled = true;
            AgentRunButton.Content = isBusy ? _stopButtonText : _runButtonText;
            AgentPlanButton.IsEnabled = !isBusy;
            AgentEditButton.IsEnabled = !isBusy;
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

            ClearOutputPlaceholder();
            AgentOutputText.Text += text;
            ScrollOutputToEnd();
        }

        public void AppendOutputLine(string line)
        {
            ClearOutputPlaceholder();
            if (!string.IsNullOrEmpty(AgentOutputText.Text) &&
                !EndsWithLineBreak(AgentOutputText.Text))
            {
                AgentOutputText.Text += OutputLineBreak;
            }

            AgentOutputText.Text += line + OutputLineBreak;
            ScrollOutputToEnd();
        }

        public void BeginOutputBlock(string title)
        {
            ClearOutputPlaceholder();
            if (!string.IsNullOrWhiteSpace(AgentOutputText.Text))
            {
                if (!EndsWithBlankLine(AgentOutputText.Text))
                {
                    AgentOutputText.Text += EndsWithLineBreak(AgentOutputText.Text)
                        ? OutputLineBreak
                        : OutputLineBreak + OutputLineBreak;
                }
            }

            AgentOutputText.Text += title + OutputLineBreak;
            ScrollOutputToEnd();
        }

        private void ClearOutputPlaceholder()
        {
            string text = AgentOutputText.Text.TrimStart();
            if (text.StartsWith("대기 중...", StringComparison.Ordinal) ||
                text.StartsWith("Waiting...", StringComparison.Ordinal) ||
                text.StartsWith("待機中...", StringComparison.Ordinal))
            {
                AgentOutputText.Text = string.Empty;
            }
        }

        private void ScrollOutputToEnd()
        {
            AgentOutputText.SelectionStart = AgentOutputText.Text.Length;
            AgentOutputText.SelectionLength = 0;
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

        private void OnPlanClick(object sender, RoutedEventArgs e)
        {
            PlanRequested?.Invoke(sender, e);
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            EditRequested?.Invoke(sender, e);
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
    }
}
