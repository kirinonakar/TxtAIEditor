using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsShortcutsPanel : UserControl
    {
        public SettingsShortcutsPanel(Func<string, string, string> getString)
        {
            var section = SettingsDialogUi.CreateSection();
            section.Spacing = 4;
            section.Padding = new Thickness(12, 12, 12, 12);
            section.Children.Add(CreateHeaderRow(getString));

            bool alternate = false;
            foreach (var item in CreateShortcutList(getString))
            {
                section.Children.Add(CreateShortcutRow(item.Key, item.Desc, alternate));
                alternate = !alternate;
            }

            Content = section;
        }

        private static Grid CreateHeaderRow(Func<string, string, string> getString)
        {
            var headerKey = new TextBlock { Text = getString("SettingsShortcutsHeaderKey", "단축키"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11 };
            var headerDesc = new TextBlock { Text = getString("SettingsShortcutsHeaderDesc", "설명"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11 };

            var headerRow = new Grid
            {
                Padding = new Thickness(6, 6, 6, 6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                CornerRadius = new CornerRadius(4)
            };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(headerKey, 0);
            Grid.SetColumn(headerDesc, 1);
            headerRow.Children.Add(headerKey);
            headerRow.Children.Add(headerDesc);
            return headerRow;
        }

        private static StackPanel CreateShortcutRow(string key, string description, bool alternate)
        {
            var rowGrid = new Grid { Padding = new Thickness(6, 6, 6, 6) };
            if (alternate)
            {
                rowGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 128, 128, 128));
            }

            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var shortcutKeyBorder = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            shortcutKeyBorder.Child = new TextBlock
            {
                Text = key,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas")
            };

            var shortcutDescText = new TextBlock
            {
                Text = description,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(shortcutKeyBorder, 0);
            Grid.SetColumn(shortcutDescText, 1);
            rowGrid.Children.Add(shortcutKeyBorder);
            rowGrid.Children.Add(shortcutDescText);

            var rowContainer = new StackPanel();
            rowContainer.Children.Add(rowGrid);
            rowContainer.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            return rowContainer;
        }

        private static IReadOnlyList<(string Key, string Desc)> CreateShortcutList(Func<string, string, string> getString)
        {
            return new List<(string Key, string Desc)>
            {
                ("Ctrl + N", getString("ShortcutDescNewTab", "새 탭")),
                ("Ctrl + S", getString("ShortcutDescSave", "저장")),
                ("Ctrl + Shift + S", getString("ShortcutDescSaveAs", "다른 이름으로 저장")),
                ("Ctrl + O", getString("ShortcutDescOpen", "파일 열기")),
                ("Ctrl + F", getString("ShortcutDescFind", "찾기")),
                ("Ctrl + W", getString("ShortcutDescClose", "탭 닫기")),
                ("Ctrl + P", getString("ShortcutDescPrint", "인쇄")),
                ("Ctrl + 1", getString("ShortcutDescLeftPanel", "좌측 패널 토글")),
                ("Ctrl + 2", getString("ShortcutDescRightPanel", "우측 패널 토글")),
                ("Ctrl + 3", getString("ShortcutDescExpandRightPanel", "우측 패널 확장")),
                ("Ctrl + `", getString("ShortcutDescTerminal", "터미널 토글")),
                ("Ctrl + Z", getString("ShortcutDescUndo", "실행 취소")),
                ("Ctrl + Y", getString("ShortcutDescRedo", "다시 실행")),
                ("Ctrl + C", getString("ShortcutDescCopy", "복사")),
                ("Ctrl + V", getString("ShortcutDescPaste", "붙여넣기")),
                ("Ctrl + X", getString("ShortcutDescCut", "잘라내기")),
                ("Ctrl + Enter", getString("ShortcutDescAiPrompt", "AI 프롬프트 전송")),
                ("F4", getString("ShortcutDescLivePreview", "라이브 프리뷰 토글")),
                ("F9", getString("ShortcutDescTopMost", "항상위 토글")),
                ("F10", getString("ShortcutDescTheme", "테마 토글")),
                ("F11", getString("ShortcutDescMaximize", "창 최대화 토글")),
                ("F12", getString("ShortcutDescStickyNote", "스티커 노트"))
            };
        }
    }
}
