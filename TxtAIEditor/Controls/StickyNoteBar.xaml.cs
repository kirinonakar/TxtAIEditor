using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    public sealed partial class StickyNoteBar : UserControl
    {
        public StickyNoteBar()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler? ExitClick;
        public event RoutedEventHandler? TopMostClick;

        public bool TopMostIsChecked
        {
            get => TopMostButton.IsChecked == true;
            set => TopMostButton.IsChecked = value;
        }

        public void Localize(Func<string, string, string> getString)
        {
            string topMostTooltip = getString("TopMost", "항상 위") + " (F9)";
            ToolTipService.SetToolTip(TopMostButton, topMostTooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TopMostButton, topMostTooltip);

            string exitTooltip = getString("ExitStickyNoteTooltip", "스티커 노트 모드 종료 (F12)");
            ToolTipService.SetToolTip(ExitButton, exitTooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExitButton, exitTooltip);
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            ExitClick?.Invoke(sender, e);
        }

        private void OnTopMostClick(object sender, RoutedEventArgs e)
        {
            TopMostClick?.Invoke(sender, e);
        }
    }
}
