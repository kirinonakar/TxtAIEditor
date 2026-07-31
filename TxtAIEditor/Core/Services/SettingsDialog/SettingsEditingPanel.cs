using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;
using AppFileOpenPicker = Microsoft.Windows.Storage.Pickers.FileOpenPicker;
using AppFileSavePicker = Microsoft.Windows.Storage.Pickers.FileSavePicker;
using AppPickerLocationId = Microsoft.Windows.Storage.Pickers.PickerLocationId;

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
        private readonly CheckBox _stripJupyterOutputsOnCommitCheck;
        private readonly CheckBox _defaultMarkdownCheck;
        private readonly CheckBox _defaultMarkdownToolbarCheck;
        private readonly CheckBox _startInTreeModeCheck;
        private readonly TextBox _tabSizeBox;
        private readonly TextBox _homeFolderBox;
        private readonly TextBox _externalViewerPathBox;
        private readonly TextBox _externalViewerArgumentsBox;

        public event EventHandler? SettingsImported;

        public SettingsEditingPanel(
            EditorSettings settings,
            Func<string, string, string> getString,
            Microsoft.UI.WindowId pickerWindowId,
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
            _stripJupyterOutputsOnCommitCheck = new CheckBox
            {
                Content = getString("SettingsStripJupyterOutputsOnCommit", "Git 커밋 시 Jupyter Notebook 출력 자동 제거"),
                IsChecked = settings.StripJupyterOutputsOnCommit
            };
            _defaultMarkdownCheck = new CheckBox { Content = getString("SettingsLivePreview", "실시간 미리보기 기본 활성화"), IsChecked = settings.DefaultMarkdownEnabled };
            _defaultMarkdownToolbarCheck = new CheckBox { Content = getString("SettingsMarkdownToolbar", "기본 마크다운 툴바 활성화"), IsChecked = settings.DefaultMarkdownToolbarEnabled };
            _startInTreeModeCheck = new CheckBox { Content = getString("SettingsStartInTreeMode", "트리 모드로 시작"), IsChecked = settings.StartInTreeMode };
            _tabSizeBox = new TextBox { PlaceholderText = "예: 4", Text = settings.TabSize.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
            _homeFolderBox = new TextBox { PlaceholderText = getString("SettingsHomeFolderPlaceholder", "C:\\Users\\..."), Text = settings.HomeFolderPath, HorizontalAlignment = HorizontalAlignment.Stretch, IsSpellCheckEnabled = false };
            _externalViewerPathBox = new TextBox { PlaceholderText = getString("SettingsExternalViewerPathPlaceholder", "uviewer 또는 C:\\Program Files\\Viewer\\viewer.exe"), Text = settings.ExternalViewerPath, HorizontalAlignment = HorizontalAlignment.Stretch, IsSpellCheckEnabled = false };
            _externalViewerArgumentsBox = new TextBox
            {
                PlaceholderText = getString("SettingsExternalViewerArgumentsPlaceholder", "예: --open {file}"),
                Text = settings.ExternalViewerArguments,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsSpellCheckEnabled = false
            };

            var section = new StackPanel { Spacing = 10, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
            section.Children.Add(CreateDisplayCard(getString));
            section.Children.Add(CreateAutocompleteSaveCard(getString));
            section.Children.Add(CreateMarkdownWorkspaceCard(getString));
            section.Children.Add(CreatePathsCard(getString, initializePickerWindow));
            section.Children.Add(CreateBackupRestoreCard(getString, pickerWindowId));
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
            settings.StripJupyterOutputsOnCommit = _stripJupyterOutputsOnCommitCheck.IsChecked == true;
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
            settings.StartInTreeMode = _startInTreeModeCheck.IsChecked == true;
        }

        private Border CreateCard(string title, UIElement content, string fontIconGlyph)
        {
            var container = new StackPanel { Spacing = 8 };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 2)
            };

            if (!string.IsNullOrEmpty(fontIconGlyph))
            {
                headerPanel.Children.Add(new FontIcon
                {
                    Glyph = fontIconGlyph,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            headerPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            });

            container.Children.Add(headerPanel);
            container.Children.Add(content);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            card.Loaded += (s, e) =>
            {
                if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out object? bg) && bg is Brush bgBrush)
                {
                    card.Background = bgBrush;
                }
                else
                {
                    card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128));
                }

                if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? border) && border is Brush borderBrush)
                {
                    card.BorderBrush = borderBrush;
                }
                else
                {
                    card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 128, 128, 128));
                }
            };

            card.Child = container;
            return card;
        }

        private Border CreateSubGroup(string title, UIElement content)
        {
            var container = new StackPanel { Spacing = 6 };
            container.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            });
            container.Children.Add(content);

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            border.Loaded += (s, e) =>
            {
                if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? bg) && bg is Brush bgBrush)
                {
                    border.Background = bgBrush;
                }
                else
                {
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 128, 128, 128));
                }

                if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? bBorder) && bBorder is Brush borderBrush)
                {
                    border.BorderBrush = borderBrush;
                }
                else
                {
                    border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128));
                }
            };

            border.Child = container;
            return border;
        }

        private Border CreateDisplayCard(Func<string, string, string> getString)
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(_wordWrapCheck);
            content.Children.Add(_syntaxHighlightingCheck);
            content.Children.Add(_showDirtyLinesCheck);
            content.Children.Add(_bracketColorCheck);

            var tabSizeGrid = new Grid();
            tabSizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabSizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80, GridUnitType.Pixel) });

            var tabLabel = new TextBlock
            {
                Text = getString("SettingsTabSize", "Tab size"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tabLabel, 0);
            Grid.SetColumn(_tabSizeBox, 1);
            tabSizeGrid.Children.Add(tabLabel);
            tabSizeGrid.Children.Add(_tabSizeBox);
            content.Children.Add(tabSizeGrid);

            return CreateCard(
                getString("SettingsEditingGroupDisplay", "에디터 화면 & 옵션"),
                content,
                "\uE8AC");
        }

        private Border CreateAutocompleteSaveCard(Func<string, string, string> getString)
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(_autocompleteEnterCheck);
            content.Children.Add(_autocompleteTabCheck);

            var autoSavePanel = new StackPanel { Spacing = 4 };
            autoSavePanel.Children.Add(_autoSaveCheck);
            autoSavePanel.Children.Add(_autoSaveAllowNonGitCheck);

            var autoSaveSubGroup = CreateSubGroup(
                getString("SettingsEditingGroupAutoSave", "자동 저장 (Autosave)"),
                autoSavePanel);
            content.Children.Add(autoSaveSubGroup);

            return CreateCard(
                getString("SettingsEditingGroupAutocomplete", "자동완성 & 저장"),
                content,
                "\uE74E");
        }

        private Border CreateMarkdownWorkspaceCard(Func<string, string, string> getString)
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(_defaultMarkdownCheck);
            content.Children.Add(_defaultMarkdownToolbarCheck);
            content.Children.Add(_startInTreeModeCheck);
            content.Children.Add(_stripJupyterOutputsOnCommitCheck);

            return CreateCard(
                getString("SettingsEditingGroupMarkdown", "문서 & 미리보기"),
                content,
                "\uE8D2");
        }

        private Border CreatePathsCard(
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow)
        {
            var content = new StackPanel { Spacing = 6 };

            // Home Folder
            SettingsDialogUi.AddLabel(content, getString("SettingsHomeFolder", "홈 폴더"));
            var homeFolderBrowseButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8B7", FontSize = 12 },
                Width = 32,
                Height = 26,
                MinWidth = 32,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                Tag = "IconOnlyButton",
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(homeFolderBrowseButton, getString("SettingsBrowseFolder", "폴더 찾기"));

            var homeFolderGrid = new Grid();
            homeFolderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            homeFolderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_homeFolderBox, 0);
            Grid.SetColumn(homeFolderBrowseButton, 1);
            homeFolderGrid.Children.Add(_homeFolderBox);
            homeFolderGrid.Children.Add(homeFolderBrowseButton);
            content.Children.Add(homeFolderGrid);

            homeFolderBrowseButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null) return;
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                initializePickerWindow(picker);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) _homeFolderBox.Text = folder.Path;
            };

            // External Viewer Path
            SettingsDialogUi.AddLabel(content, getString("SettingsExternalViewerPath", "외부 뷰어 경로"));
            var externalViewerBrowseButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8B7", FontSize = 12 },
                Width = 32,
                Height = 26,
                MinWidth = 32,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                Tag = "IconOnlyButton",
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(externalViewerBrowseButton, getString("SettingsBrowseFile", "파일 찾기"));

            var externalViewerGrid = new Grid();
            externalViewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            externalViewerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_externalViewerPathBox, 0);
            Grid.SetColumn(externalViewerBrowseButton, 1);
            externalViewerGrid.Children.Add(_externalViewerPathBox);
            externalViewerGrid.Children.Add(externalViewerBrowseButton);
            content.Children.Add(externalViewerGrid);

            externalViewerBrowseButton.Click += async (_, _) =>
            {
                if (initializePickerWindow == null) return;
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                initializePickerWindow(picker);
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".bat");
                picker.FileTypeFilter.Add(".cmd");
                var file = await picker.PickSingleFileAsync();
                if (file != null) _externalViewerPathBox.Text = file.Path;
            };

            // External Viewer Arguments
            SettingsDialogUi.AddLabel(content, getString("SettingsExternalViewerArguments", "외부 뷰어 파라미터"));
            content.Children.Add(_externalViewerArgumentsBox);
            content.Children.Add(new TextBlock
            {
                Text = getString("SettingsExternalViewerArgumentsInfo", "{file} 위치에 현재 파일 경로를 넣습니다. {file}이 없으면 마지막 인자로 파일 경로를 자동 추가합니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            });

            return CreateCard(
                getString("SettingsEditingGroupPaths", "경로 & 외부 뷰어"),
                content,
                "\uE8B7");
        }

        private Border CreateBackupRestoreCard(
            Func<string, string, string> getString,
            Microsoft.UI.WindowId pickerWindowId)
        {
            var content = new StackPanel { Spacing = 8 };

            var buttonGrid = new Grid();
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Pixel) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var exportButton = new Button
            {
                Content = getString("SettingsExportAllButton", "전체 설정 내보내기"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var importButton = new Button
            {
                Content = getString("SettingsImportAllButton", "전체 설정 불러오기"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(exportButton, 0);
            Grid.SetColumn(importButton, 2);
            buttonGrid.Children.Add(exportButton);
            buttonGrid.Children.Add(importButton);
            content.Children.Add(buttonGrid);

            var statusText = new TextBlock
            {
                Text = getString("SettingsBackupDescription", ".TxtAIEditor 폴더 전체를 txtaieditor-setting.zip으로 내보내거나 zip에서 불러옵니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
            content.Children.Add(statusText);

            bool backupOperationActive = false;

            exportButton.Click += async (_, _) =>
            {
                if (backupOperationActive) return;
                backupOperationActive = true;
                exportButton.IsEnabled = false;
                importButton.IsEnabled = false;
                try
                {
                    var picker = new AppFileSavePicker(pickerWindowId)
                    {
                        SuggestedStartLocation = AppPickerLocationId.DocumentsLibrary,
                        SuggestedFileName = SettingsBackupService.ArchiveFileName,
                        DefaultFileExtension = ".zip"
                    };
                    picker.FileTypeChoices.Add(
                        getString("SettingsBackupZipFileType", "ZIP archive"),
                        new List<string> { ".zip" });

                    var file = await picker.PickSaveFileAsync();
                    if (file == null) return;

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
                    backupOperationActive = false;
                    exportButton.IsEnabled = true;
                    importButton.IsEnabled = true;
                }
            };

            importButton.Click += async (_, _) =>
            {
                if (backupOperationActive) return;
                backupOperationActive = true;
                exportButton.IsEnabled = false;
                importButton.IsEnabled = false;
                try
                {
                    var picker = new AppFileOpenPicker(pickerWindowId)
                    {
                        SuggestedStartLocation = AppPickerLocationId.DocumentsLibrary
                    };
                    picker.FileTypeFilter.Add(".zip");

                    var file = await picker.PickSingleFileAsync();
                    if (file == null) return;

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
                    backupOperationActive = false;
                    exportButton.IsEnabled = true;
                    importButton.IsEnabled = true;
                }
            };

            return CreateCard(
                getString("SettingsEditingGroupBackup", "전체 설정 백업 & 복원"),
                content,
                "\uE8F7");
        }
    }
}
