using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TxtAIEditor.Core.Services
{
    public sealed record CompareFileSelection(
        bool IsValid,
        string PathA,
        string PathB,
        string? ContentA,
        string? ContentB);

    public sealed class CompareSelectionDialogService
    {
        public async Task<CompareFileSelection?> ShowAsync(Window owner, XamlRoot xamlRoot, IReadOnlyList<OpenedTab> tabs, ElementTheme theme, Func<string, string, string> getString)
        {
            var panel = new StackPanel { Spacing = 12, Width = 400, RequestedTheme = theme };

            var tabChoices = new List<string> { getString("CompareSelectFromTab", "탭에서 선택...") };
            foreach (var tab in tabs)
            {
                tabChoices.Add($"[{getString("CompareTabPrefix", "탭")}] {tab.Title}");
            }

            var originalCombo = CreateSourceCombo(tabChoices);
            var originalPathBox = new TextBox { PlaceholderText = getString("CompareOriginalPathPlaceholder", "원본 파일 경로..."), IsReadOnly = true, Height = 32, TextWrapping = TextWrapping.NoWrap };
            var originalBrowseButton = new Button { Content = getString("CompareBrowse", "찾아보기..."), Height = 32 };
            var originalRow = CreatePathRow(originalPathBox, originalBrowseButton);

            var modifiedCombo = CreateSourceCombo(tabChoices);
            var modifiedPathBox = new TextBox { PlaceholderText = getString("CompareModifiedPathPlaceholder", "비교 대상 파일 경로..."), IsReadOnly = true, Height = 32, TextWrapping = TextWrapping.NoWrap };
            var modifiedBrowseButton = new Button { Content = getString("CompareBrowse", "찾아보기..."), Height = 32 };
            var modifiedRow = CreatePathRow(modifiedPathBox, modifiedBrowseButton);

            panel.Children.Add(new TextBlock { Text = getString("CompareOriginalFileLabel", "원본 파일 (Original File)"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(originalCombo);
            panel.Children.Add(originalRow);
            panel.Children.Add(new MenuFlyoutSeparator());
            panel.Children.Add(new TextBlock { Text = getString("CompareModifiedFileLabel", "비교 대상 파일 (Modified File)"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(modifiedCombo);
            panel.Children.Add(modifiedRow);

            originalCombo.SelectionChanged += (_, _) => SyncPathBoxFromCombo(originalCombo, originalBrowseButton, originalPathBox);
            modifiedCombo.SelectionChanged += (_, _) => SyncPathBoxFromCombo(modifiedCombo, modifiedBrowseButton, modifiedPathBox);

            originalBrowseButton.Click += async (_, _) => originalPathBox.Text = await PickFileAsync(owner) ?? originalPathBox.Text;
            modifiedBrowseButton.Click += async (_, _) => modifiedPathBox.Text = await PickFileAsync(owner) ?? modifiedPathBox.Text;

            var dialog = new ContentDialog
            {
                Title = getString("CompareDialogTitle", "파일 비교 (File Compare)"),
                Content = panel,
                PrimaryButtonText = getString("CompareDialogCompareButton", "비교하기"),
                CloseButtonText = getString("CompareDialogCancelButton", "취소"),
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            OpenedTab? tabA = originalCombo.SelectedIndex > 0 ? tabs[originalCombo.SelectedIndex - 1] : null;
            OpenedTab? tabB = modifiedCombo.SelectedIndex > 0 ? tabs[modifiedCombo.SelectedIndex - 1] : null;

            string pathA = tabA == null ? originalPathBox.Text.Trim() : (string.IsNullOrEmpty(tabA.FilePath) ? tabA.Title : tabA.FilePath);
            string pathB = tabB == null ? modifiedPathBox.Text.Trim() : (string.IsNullOrEmpty(tabB.FilePath) ? tabB.Title : tabB.FilePath);

            bool validA = tabA != null || (!string.IsNullOrEmpty(pathA) && File.Exists(pathA));
            bool validB = tabB != null || (!string.IsNullOrEmpty(pathB) && File.Exists(pathB));

            return new CompareFileSelection(validA && validB, pathA, pathB, tabA?.Content, tabB?.Content);
        }

        private static ComboBox CreateSourceCombo(IEnumerable<string> choices)
        {
            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 32,
                Margin = new Thickness(0, 0, 0, 4),
                SelectedIndex = 0
            };

            foreach (var choice in choices)
            {
                combo.Items.Add(choice);
            }

            return combo;
        }

        private static Grid CreatePathRow(TextBox pathBox, Button browseButton)
        {
            var row = new Grid { Height = 32 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browseButton, 1);
            browseButton.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(pathBox);
            row.Children.Add(browseButton);
            return row;
        }

        private static void SyncPathBoxFromCombo(ComboBox combo, Button browseButton, TextBox pathBox)
        {
            bool isBrowse = combo.SelectedIndex == 0;
            browseButton.IsEnabled = isBrowse;
            pathBox.Text = isBrowse ? string.Empty : combo.SelectedItem.ToString();
        }

        private static async Task<string?> PickFileAsync(Window owner)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
    }
}
