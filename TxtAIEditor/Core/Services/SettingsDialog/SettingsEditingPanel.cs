using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsEditingPanel : UserControl
    {
        private readonly CheckBox _wordWrapCheck;
        private readonly CheckBox _bracketColorCheck;
        private readonly CheckBox _autocompleteEnterCheck;
        private readonly CheckBox _autocompleteTabCheck;
        private readonly CheckBox _autoSaveCheck;
        private readonly CheckBox _autoSaveAllowNonGitCheck;
        private readonly CheckBox _defaultMarkdownCheck;
        private readonly CheckBox _defaultMarkdownToolbarCheck;
        private readonly TextBox _tabSizeBox;
        private readonly TextBox _homeFolderBox;
        private readonly TextBox _externalViewerPathBox;
        private readonly TextBox _externalViewerArgumentsBox;

        public SettingsEditingPanel(
            EditorSettings settings,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            _wordWrapCheck = new CheckBox { Content = getString("SettingsWordWrap", "기본 Word Wrap 켜기"), IsChecked = settings.WordWrap };
            _bracketColorCheck = new CheckBox { Content = getString("SettingsBracketPair", "괄호 쌍 색상화 활성화"), IsChecked = settings.BracketPairColorization };
            _autocompleteEnterCheck = new CheckBox { Content = getString("SettingsAutocompleteEnter", "Enter로 자동완성"), IsChecked = settings.AutocompleteOnEnter };
            _autocompleteTabCheck = new CheckBox { Content = getString("SettingsAutocompleteTab", "Tab으로 자동완성"), IsChecked = settings.AutocompleteOnTab };
            _autoSaveCheck = new CheckBox { Content = getString("SettingsAutoSave", "Autosave 사용"), IsChecked = settings.AutoSave };
            _autoSaveAllowNonGitCheck = new CheckBox { Content = getString("SettingsAutoSaveAllowNonGitFolders", "Git 폴더가 아니어도 Autosave 허용"), IsChecked = settings.AutoSaveAllowNonGitFolders };
            _defaultMarkdownCheck = new CheckBox { Content = getString("SettingsLivePreview", "실시간 미리보기 기본 활성화"), IsChecked = settings.DefaultMarkdownEnabled };
            _defaultMarkdownToolbarCheck = new CheckBox { Content = getString("SettingsMarkdownToolbar", "기본 마크다운 툴바 활성화"), IsChecked = settings.DefaultMarkdownToolbarEnabled };
            _tabSizeBox = new TextBox { PlaceholderText = "예: 4", Text = settings.TabSize.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
            _homeFolderBox = new TextBox { PlaceholderText = getString("SettingsHomeFolderPlaceholder", "C:\\Users\\..."), Text = settings.HomeFolderPath, Width = 420, IsSpellCheckEnabled = false };
            _externalViewerPathBox = new TextBox { PlaceholderText = getString("SettingsExternalViewerPathPlaceholder", "uviewer 또는 C:\\Program Files\\Viewer\\viewer.exe"), Text = settings.ExternalViewerPath, Width = 420, IsSpellCheckEnabled = false };
            _externalViewerArgumentsBox = new TextBox
            {
                PlaceholderText = getString("SettingsExternalViewerArgumentsPlaceholder", "예: --open {file}"),
                Text = settings.ExternalViewerArguments,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsSpellCheckEnabled = false
            };

            var section = SettingsDialogUi.CreateSection();
            section.Children.Add(_wordWrapCheck);
            section.Children.Add(_bracketColorCheck);
            section.Children.Add(_autocompleteEnterCheck);
            section.Children.Add(_autocompleteTabCheck);
            section.Children.Add(_autoSaveCheck);
            section.Children.Add(_autoSaveAllowNonGitCheck);
            section.Children.Add(_defaultMarkdownCheck);
            section.Children.Add(_defaultMarkdownToolbarCheck);
            SettingsDialogUi.AddLabel(section, getString("SettingsTabSize", "Tab size"));
            section.Children.Add(_tabSizeBox);

            AddHomeFolderPicker(section, getString, initializePickerWindow);
            AddExternalViewerPicker(section, getString, initializePickerWindow);
            Content = section;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.WordWrap = _wordWrapCheck.IsChecked == true;
            settings.BracketPairColorization = _bracketColorCheck.IsChecked == true;
            settings.AutocompleteOnEnter = _autocompleteEnterCheck.IsChecked == true;
            settings.AutocompleteOnTab = _autocompleteTabCheck.IsChecked == true;
            settings.AutoSave = _autoSaveCheck.IsChecked == true;
            settings.AutoSaveAllowNonGitFolders = _autoSaveAllowNonGitCheck.IsChecked == true;
            if (int.TryParse(_tabSizeBox.Text.Trim(), out int tabSize))
            {
                settings.TabSize = Math.Clamp(tabSize, 1, 16);
            }

            settings.HomeFolderPath = _homeFolderBox.Text.Trim();
            settings.ExternalViewerPath = _externalViewerPathBox.Text.Trim();
            settings.ExternalViewerArguments = _externalViewerArgumentsBox.Text.Trim();
            settings.DefaultMarkdownEnabled = _defaultMarkdownCheck.IsChecked == true;
            settings.RightSidebarVisible = settings.DefaultMarkdownEnabled;
            settings.DefaultMarkdownToolbarEnabled = _defaultMarkdownToolbarCheck.IsChecked == true;
        }

        private void AddHomeFolderPicker(
            StackPanel section,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            SettingsDialogUi.AddLabel(section, getString("SettingsHomeFolder", "홈 폴더"));
            var homeFolderBrowseButton = new Button { Content = "...", Width = 32, Height = 32, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var homeFolderPanel = new StackPanel { Orientation = Orientation.Horizontal };
            homeFolderPanel.Children.Add(_homeFolderBox);
            homeFolderPanel.Children.Add(homeFolderBrowseButton);
            section.Children.Add(homeFolderPanel);

            homeFolderBrowseButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null)
                {
                    return;
                }

                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                initializePickerWindow(picker);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    _homeFolderBox.Text = folder.Path;
                }
            };
        }

        private void AddExternalViewerPicker(
            StackPanel section,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            SettingsDialogUi.AddLabel(section, getString("SettingsExternalViewerPath", "외부 뷰어 경로"));
            var externalViewerBrowseButton = new Button { Content = "...", Width = 32, Height = 32, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var externalViewerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            externalViewerPanel.Children.Add(_externalViewerPathBox);
            externalViewerPanel.Children.Add(externalViewerBrowseButton);
            section.Children.Add(externalViewerPanel);

            SettingsDialogUi.AddLabel(section, getString("SettingsExternalViewerArguments", "외부 뷰어 파라미터"));
            section.Children.Add(_externalViewerArgumentsBox);
            section.Children.Add(new TextBlock
            {
                Text = getString("SettingsExternalViewerArgumentsInfo", "{file} 위치에 현재 파일 경로를 넣습니다. {file}이 없으면 마지막 인자로 파일 경로를 자동 추가합니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });

            externalViewerBrowseButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null)
                {
                    return;
                }

                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                initializePickerWindow(picker);
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".bat");
                picker.FileTypeFilter.Add(".cmd");
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _externalViewerPathBox.Text = file.Path;
                }
            };
        }
    }
}
