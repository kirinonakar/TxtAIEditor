using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsEditingPanel : UserControl
    {
        private readonly CheckBox _wordWrapCheck;
        private readonly CheckBox _syntaxHighlightingCheck;
        private readonly CheckBox _showDirtyLinesCheck;
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

        public event EventHandler? SettingsImported;

        public SettingsEditingPanel(
            EditorSettings settings,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            _wordWrapCheck = new CheckBox { Content = getString("SettingsWordWrap", "기본 Word Wrap 켜기"), IsChecked = settings.WordWrap };
            _syntaxHighlightingCheck = new CheckBox { Content = getString("SettingsSyntaxHighlighting", "구문 강조 활성화"), IsChecked = settings.SyntaxHighlighting };
            _showDirtyLinesCheck = new CheckBox { Content = getString("SettingsShowDirtyLines", "Dirty line 표시"), IsChecked = settings.ShowDirtyLines };
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
            section.Children.Add(_syntaxHighlightingCheck);
            section.Children.Add(_showDirtyLinesCheck);
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
            AddSettingsBackupActions(section, getString, initializePickerWindow);
            Content = section;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.WordWrap = _wordWrapCheck.IsChecked == true;
            settings.SyntaxHighlighting = _syntaxHighlightingCheck.IsChecked == true;
            settings.ShowDirtyLines = _showDirtyLinesCheck.IsChecked == true;
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

        private void AddSettingsBackupActions(
            StackPanel section,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            SettingsDialogUi.AddLabel(section, getString("SettingsBackupTitle", "전체 설정"));

            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };

            var exportButton = new Button
            {
                Content = getString("SettingsExportAllButton", "전체 설정 내보내기"),
                MinWidth = 130
            };
            var importButton = new Button
            {
                Content = getString("SettingsImportAllButton", "전체 설정 불러오기"),
                MinWidth = 130
            };
            actionsPanel.Children.Add(exportButton);
            actionsPanel.Children.Add(importButton);
            section.Children.Add(actionsPanel);

            var statusText = new TextBlock
            {
                Text = getString("SettingsBackupDescription", ".TxtAIEditor 폴더 전체를 txtaieditor-setting.zip으로 내보내거나 zip에서 불러옵니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            section.Children.Add(statusText);

            exportButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null)
                {
                    statusText.Text = getString("SettingsBackupPickerUnavailable", "파일 선택기를 열 수 없습니다.");
                    return;
                }

                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = SettingsBackupService.ArchiveFileName
                };
                picker.FileTypeChoices.Add(
                    getString("SettingsBackupZipFileType", "ZIP archive"),
                    new List<string> { ".zip" });
                initializePickerWindow(picker);

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                try
                {
                    exportButton.IsEnabled = false;
                    importButton.IsEnabled = false;
                    statusText.Text = getString("SettingsBackupExporting", "전체 설정을 내보내는 중...");
                    await SettingsBackupService.ExportAsync(file.Path);
                    statusText.Text = string.Format(
                        getString("SettingsBackupExportedFormat", "전체 설정을 내보냈습니다: {0}"),
                        file.Path);
                }
                catch (Exception ex)
                {
                    statusText.Text = string.Format(
                        getString("SettingsBackupExportFailedFormat", "전체 설정 내보내기에 실패했습니다: {0}"),
                        ex.Message);
                }
                finally
                {
                    exportButton.IsEnabled = true;
                    importButton.IsEnabled = true;
                }
            };

            importButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null)
                {
                    statusText.Text = getString("SettingsBackupPickerUnavailable", "파일 선택기를 열 수 없습니다.");
                    return;
                }

                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add(".zip");
                initializePickerWindow(picker);

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                try
                {
                    exportButton.IsEnabled = false;
                    importButton.IsEnabled = false;
                    statusText.Text = getString("SettingsBackupImporting", "전체 설정을 불러오는 중...");
                    await SettingsBackupService.ImportAsync(file.Path);
                    statusText.Text = getString("SettingsBackupImported", "전체 설정을 불러왔습니다.");
                    SettingsImported?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    statusText.Text = string.Format(
                        getString("SettingsBackupImportFailedFormat", "전체 설정 불러오기에 실패했습니다: {0}"),
                        ex.Message);
                }
                finally
                {
                    exportButton.IsEnabled = true;
                    importButton.IsEnabled = true;
                }
            };

        }
    }
}
