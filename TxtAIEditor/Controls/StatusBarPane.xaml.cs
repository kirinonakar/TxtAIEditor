using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class StatusBarPane : UserControl
    {
        public StatusBarPane()
        {
            InitializeComponent();
            AttachArrowCursorReset(LineEndingButton);
            AttachArrowCursorReset(LanguageButton);
        }

        public event RoutedEventHandler? LeftPanelToggleClick;
        public event RoutedEventHandler? RightPanelToggleClick;
        public event RoutedEventHandler? ExpandPreviewClick;
        public event SelectionChangedEventHandler? EncodingSelectionChanged;
        public event RoutedEventHandler? LineNumberClick;
        public event RoutedEventHandler? LineEndingClick;
        public event RoutedEventHandler? LanguageClick;

        public ToggleButton LeftPanelToggleButton => LeftPanelToggle;
        public ToggleButton RightPanelToggleButton => RightPanelToggle;
        public Button ExpandPreviewBtn => ExpandPreviewButton;
        public TextBlock LineText => StatusLine;
        public TextBlock LineLabelText => StatusLineLabel;
        public TextBlock LineColumnSeparatorText => StatusLineColumnSeparator;
        public TextBlock ColumnText => StatusCol;
        public TextBlock ColumnLabelText => StatusColumnLabel;
        public TextBlock TotalLinesText => StatusTotalLines;
        public TextBlock StatusSelectionStatsText => StatusSelectionStats;
        public TextBlock FileStatsText => StatusFileStats;
        public TextBlock GitBranchText => StatusGitBranch;
        public TextBlock LanguageText => StatusLanguage;
        public Button LanguageButtonControl => LanguageButton;
        public ComboBox EncodingCombo => StatusEncodingCombo;
        public TextBlock LineEndingText => StatusLineEnding;
        public Button LineNumberButtonControl => LineNumberButton;
        public Button LineEndingButtonControl => LineEndingButton;

        public void Localize(Func<string, string, string> getString)
        {
            StatusLineLabel.Text = getString("StatusLineLabel", "줄");
            StatusColumnLabel.Text = getString("StatusColumnLabel", "열");
            if (GitBranchStatus.IsNotDetectedTag(StatusGitBranch.Tag))
            {
                StatusGitBranch.Text = getString("GitNotDetected", "Git: 감지 안됨");
            }

            ToolTipService.SetToolTip(LeftPanelToggle, getString("StatusLeftPanelTooltip", "좌측 패널"));
            ToolTipService.SetToolTip(RightPanelToggle, getString("StatusRightPanelTooltip", "우측 패널"));
            ToolTipService.SetToolTip(ExpandPreviewButton, getString("ExpandPreviewTooltip", "프리뷰 늘리기") + " (Ctrl+3)");
            ToolTipService.SetToolTip(LineNumberButton, getString("StatusGoToLineTooltip", "클릭하여 줄 이동"));
            ToolTipService.SetToolTip(LineEndingButton, getString("StatusLineEndingTooltip", "클릭하여 줄 끝 방식 변경"));
            ToolTipService.SetToolTip(LanguageButton, getString("StatusLanguageTooltip", "파일 유형 변경"));
            ToolTipService.SetToolTip(StatusEncodingCombo, getString("StatusEncodingTooltip", "파일 인코딩 선택"));
        }

        private void HandleLeftPanelToggleClick(object sender, RoutedEventArgs e)
        {
            LeftPanelToggleClick?.Invoke(sender, e);
        }

        private void HandleRightPanelToggleClick(object sender, RoutedEventArgs e)
        {
            RightPanelToggleClick?.Invoke(sender, e);
        }

        private void HandleExpandPreviewClick(object sender, RoutedEventArgs e)
        {
            ExpandPreviewClick?.Invoke(sender, e);
        }

        private void HandleEncodingSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EncodingSelectionChanged?.Invoke(sender, e);
        }

        private void HandleLineNumberClick(object sender, RoutedEventArgs e)
        {
            LineNumberClick?.Invoke(sender, e);
        }

        private void HandleLineEndingClick(object sender, RoutedEventArgs e)
        {
            LineEndingClick?.Invoke(sender, e);
        }

        private void HandleLanguageClick(object sender, RoutedEventArgs e)
        {
            LanguageClick?.Invoke(sender, e);
        }

        private void AttachArrowCursorReset(FrameworkElement element)
        {
            element.PointerEntered += ResetPointerCursor;
            element.PointerMoved += ResetPointerCursor;
            element.PointerPressed += ResetPointerCursor;
            element.PointerReleased += ResetPointerCursor;
        }

        private void ResetPointerCursor(object sender, PointerRoutedEventArgs e)
        {
            CursorResetHelper.ResetToArrow(sender as FrameworkElement ?? this);
        }
    }
}
