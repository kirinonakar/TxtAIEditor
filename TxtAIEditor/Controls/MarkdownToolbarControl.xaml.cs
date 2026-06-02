using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    public sealed class MarkdownCommandRequestedEventArgs : EventArgs
    {
        public MarkdownCommandRequestedEventArgs(string command, string? color = null)
        {
            Command = command;
            Color = color;
        }

        public string Command { get; }
        public string? Color { get; }
    }

    public sealed partial class MarkdownToolbarControl : UserControl
    {
        private string _lastTextColorHex = "#E53935";

        public MarkdownToolbarControl()
        {
            InitializeComponent();
            SetTextColor(Windows.UI.Color.FromArgb(255, 229, 57, 53));
        }

        public event EventHandler<MarkdownCommandRequestedEventArgs>? CommandRequested;

        public void SetToolbarBackground(Windows.UI.Color color)
        {
            ToolbarRoot.Background = new SolidColorBrush(color);
        }

        public void SetTextColorToolTip(string text)
        {
            ToolTipService.SetToolTip(TextColorButton, text);
        }

        public void LocalizeTooltips(Func<string, string, string> getText)
        {
            ToolTipService.SetToolTip(MarkdownHeadingButton, getText("Heading", "제목"));
            ToolTipService.SetToolTip(MarkdownBoldButton, getText("Bold", "굵게"));
            ToolTipService.SetToolTip(MarkdownItalicButton, getText("Italic", "기울임"));
            ToolTipService.SetToolTip(MarkdownUnderlineButton, getText("Underline", "밑줄"));
            ToolTipService.SetToolTip(MarkdownHighlightButton, getText("Highlight", "형광펜"));
            ToolTipService.SetToolTip(MarkdownUlButton, getText("UnorderedList", "글머리 목록"));
            ToolTipService.SetToolTip(MarkdownCutLineButton, getText("CutLine", "현재 줄 자르기"));
            ToolTipService.SetToolTip(MarkdownQuoteButton, getText("Quote", "인용문"));
            ToolTipService.SetToolTip(MarkdownArrowButton, getText("Arrow", "화살표"));
            ToolTipService.SetToolTip(MarkdownInlineCodeButton, getText("InlineCode", "인라인 코드"));
            ToolTipService.SetToolTip(MarkdownTaskButton, getText("Tasklist", "체크리스트"));
            ToolTipService.SetToolTip(MarkdownTableButton, getText("Table", "표"));
            ToolTipService.SetToolTip(MarkdownConvertToTableButton, getText("ConvertToTable", "테이블로 바꾸기"));
            ToolTipService.SetToolTip(MarkdownFontIncreaseButton, getText("FontIncrease", "글자 크게"));
            ToolTipService.SetToolTip(MarkdownFontDecreaseButton, getText("FontDecrease", "글자 작게"));
            ToolTipService.SetToolTip(TextColorButton, getText("TextColor", "글자색 (우클릭: 색상 선택)"));
            ToolTipService.SetToolTip(MarkdownCharMapButton, getText("CharMap", "문자표"));
            ToolTipService.SetToolTip(MarkdownEmojiButton, getText("Emoji", "이모지"));
            ToolTipService.SetToolTip(MarkdownCurrentDateButton, getText("CurrentDate", "현재 날짜"));
            ToolTipService.SetToolTip(MarkdownLinkButton, getText("Link", "링크"));

            TextColorPickerTitle.Text = getText("TextColorPickerTitle", "글자색 선택");
            TextColorApplyButton.Content = getText("TextColorApplyButton", "적용");
        }

        private void OnCommandButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string command })
            {
                CommandRequested?.Invoke(this, new MarkdownCommandRequestedEventArgs(command));
            }
        }

        private void OnTextColorButtonClick(object sender, RoutedEventArgs e)
        {
            CommandRequested?.Invoke(this, new MarkdownCommandRequestedEventArgs("textColor", _lastTextColorHex));
        }

        private void OnTextColorButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ColorPickerFlyout.ShowAt(TextColorButton);
        }

        private void OnApplyTextColorClick(object sender, RoutedEventArgs e)
        {
            ColorPickerFlyout.Hide();
            var color = TextColorPicker.Color;
            _lastTextColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SetTextColor(color);
            CommandRequested?.Invoke(this, new MarkdownCommandRequestedEventArgs("textColor", _lastTextColorHex));
        }

        private void SetTextColor(Windows.UI.Color color)
        {
            var brush = new SolidColorBrush(color);
            TextColorPicker.Color = color;
            TextColorButton.Foreground = brush;
            TextColorButton.Resources["ButtonForegroundPointerOver"] = brush;
            TextColorButton.Resources["ButtonForegroundPressed"] = brush;
            if (TextColorIcon != null)
            {
                TextColorIcon.Foreground = brush;
            }
        }
    }
}
