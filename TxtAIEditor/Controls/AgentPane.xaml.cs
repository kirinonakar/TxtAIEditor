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
        public event RoutedEventHandler? PlanRequested;
        public event RoutedEventHandler? EditRequested;
        public event RoutedEventHandler? InsertOutputRequested;

        public TextBox Output => AgentOutputText;
        public TextBox Prompt => AgentPromptInput;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public bool IncludeActiveFile => AgentIncludeActiveFileCheckBox.IsChecked == true;

        public void Localize(Func<string, string, string> getString)
        {
            if (AgentOutputText.Text.Contains("대기 중...") ||
                AgentOutputText.Text.Contains("Waiting...") ||
                AgentOutputText.Text.Contains("待機中..."))
            {
                AgentOutputText.Text = getString("AgentOutputPlaceholder", "대기 중... Agent에게 작업을 지시해 보세요.");
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentIncludeActiveFileCheckBox.Content = getString("AgentIncludeActiveFile", "현재 탭 포함");
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            AgentRunButton.Content = getString("AgentRunButton", "실행");
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
            AgentRunButton.IsEnabled = !isBusy;
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

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
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

            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            if (!ctrl)
            {
                return;
            }

            e.Handled = true;
            RunRequested?.Invoke(sender, e);
        }
    }
}
