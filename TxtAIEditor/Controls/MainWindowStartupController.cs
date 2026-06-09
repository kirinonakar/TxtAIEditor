using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class MainWindowStartupController
    {
        private readonly Window _window;
        private readonly ISettingsService _settingsService;
        private readonly MainWindowViewModel _viewModel;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TerminalPane _terminalPane;
        private readonly TopCommandBarPane _topToolbar;
        private readonly ToggleButton _leftPanelToggle;
        private readonly ToggleButton _rightPanelToggle;
        private readonly MarkdownToolbarControl _markdownToolbar;
        private readonly ComboBox _previewModeCombo;
        private readonly DispatcherTimer _gitAutoRefreshTimer;
        private readonly LivePreviewController _livePreviewController;
        private readonly SnippetsController _snippetsController;
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly Func<string> _getCurrentRepoPath;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Action _openNewTab;
        private readonly Action<bool> _applyLeftSidebarVisibility;
        private readonly Action<bool> _applyPreviewVisibility;
        private readonly Action<EditorSettings> _applyUiPersonalization;
        private readonly Action _localizeUi;
        private readonly Action<EditorSettings> _applyToolbarSettings;
        private readonly Func<Task> _refreshGitStatusUiAsync;
        private readonly Action _updateAutoSaveStatus;
        private readonly Action<string, string> _showErrorMessage;
        private bool _initializePreviewAfterStartup;

        public MainWindowStartupController(
            Window window,
            ISettingsService settingsService,
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            TerminalPane terminalPane,
            TopCommandBarPane topToolbar,
            ToggleButton leftPanelToggle,
            ToggleButton rightPanelToggle,
            MarkdownToolbarControl markdownToolbar,
            ComboBox previewModeCombo,
            DispatcherTimer gitAutoRefreshTimer,
            LivePreviewController livePreviewController,
            SnippetsController snippetsController,
            FavoritesRecentController favoritesRecentController,
            Func<string> getCurrentRepoPath,
            Func<string, Task> navigateExplorerToFolderAsync,
            Func<string, Task> loadFileIntoTabAsync,
            Action openNewTab,
            Action<bool> applyLeftSidebarVisibility,
            Action<bool> applyPreviewVisibility,
            Action<EditorSettings> applyUiPersonalization,
            Action localizeUi,
            Action<EditorSettings> applyToolbarSettings,
            Func<Task> refreshGitStatusUiAsync,
            Action updateAutoSaveStatus,
            Action<string, string> showErrorMessage)
        {
            _window = window;
            _settingsService = settingsService;
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _terminalPane = terminalPane;
            _topToolbar = topToolbar;
            _leftPanelToggle = leftPanelToggle;
            _rightPanelToggle = rightPanelToggle;
            _markdownToolbar = markdownToolbar;
            _previewModeCombo = previewModeCombo;
            _gitAutoRefreshTimer = gitAutoRefreshTimer;
            _livePreviewController = livePreviewController;
            _snippetsController = snippetsController;
            _favoritesRecentController = favoritesRecentController;
            _getCurrentRepoPath = getCurrentRepoPath;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _openNewTab = openNewTab;
            _applyLeftSidebarVisibility = applyLeftSidebarVisibility;
            _applyPreviewVisibility = applyPreviewVisibility;
            _applyUiPersonalization = applyUiPersonalization;
            _localizeUi = localizeUi;
            _applyToolbarSettings = applyToolbarSettings;
            _refreshGitStatusUiAsync = refreshGitStatusUiAsync;
            _updateAutoSaveStatus = updateAutoSaveStatus;
            _showErrorMessage = showErrorMessage;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var startupPaths = ParseStartupPaths(Environment.GetCommandLineArgs());

                await LoadSettingsAsync();
                await LoadUserContentIndexesAsync();
                ApplyInitialShellState();

                await OpenStartupTargetsAsync(startupPaths);

                if (!string.IsNullOrEmpty(_getCurrentRepoPath()))
                {
                    _ = _refreshGitStatusUiAsync();
                    _gitAutoRefreshTimer.Start();
                }

                _updateAutoSaveStatus();
                if (_initializePreviewAfterStartup)
                {
                    QueuePreviewInitialization();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup initialization failed: {ex.Message}");
                if (_viewModel.Tabs.Count == 0)
                {
                    _openNewTab();
                }

                _showErrorMessage("시작 파일 열기 실패", ex.Message);
            }
        }

        private async Task LoadSettingsAsync()
        {
            await _settingsService.LoadSettingsAsync();
            WindowPlacementService.ApplySavedWindowPlacement(_window.AppWindow, _settingsService.CurrentSettings);
            _editorWorkspace.LastTerminalHeight = Math.Clamp(_settingsService.CurrentSettings.TerminalPanelHeight, 120, 600);
            _terminalPane.ApplySettings(_settingsService.CurrentSettings);
        }

        private async Task LoadUserContentIndexesAsync()
        {
            await _snippetsController.LoadAsync();
            _favoritesRecentController.RefreshFavorites();
            _favoritesRecentController.LoadRecentFiles();
        }

        private void ApplyInitialShellState()
        {
            var settings = _settingsService.CurrentSettings;
            _topToolbar.WordWrapIsChecked = settings.WordWrap;
            _leftPanelToggle.IsChecked = settings.LeftSidebarVisible;
            _applyLeftSidebarVisibility(settings.LeftSidebarVisible);

            bool rightPanelVisible = settings.RightSidebarVisible && settings.DefaultMarkdownEnabled;
            _initializePreviewAfterStartup = rightPanelVisible;
            _rightPanelToggle.IsChecked = rightPanelVisible;
            _applyPreviewVisibility(rightPanelVisible);

            _topToolbar.MarkdownToolbarIsChecked = settings.DefaultMarkdownToolbarEnabled;
            _markdownToolbar.Visibility = settings.DefaultMarkdownToolbarEnabled ? Visibility.Visible : Visibility.Collapsed;
            _previewModeCombo.SelectedIndex = 0;

            _applyUiPersonalization(settings);
            _localizeUi();
            _applyToolbarSettings(settings);
        }

        private async Task OpenStartupTargetsAsync(StartupPaths startupPaths)
        {
            if (startupPaths.Folders.Count > 0)
            {
                NavigateStartupFolderWithoutBlocking(startupPaths.Folders[0]);
            }

            if (startupPaths.Files.Count > 0)
            {
                if (startupPaths.Folders.Count == 0)
                {
                    NavigateToFirstFileFolder(startupPaths.Files[0]);
                }

                foreach (string filePath in startupPaths.Files)
                {
                    await _loadFileIntoTabAsync(filePath);
                }
            }
            else if (startupPaths.Folders.Count == 0)
            {
                _openNewTab();
                NavigateStartupFolderWithoutBlocking(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            }
        }

        private void NavigateToFirstFileFolder(string filePath)
        {
            string? folderPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folderPath))
            {
                return;
            }

            try
            {
                if (Directory.Exists(folderPath))
                {
                    NavigateStartupFolderWithoutBlocking(folderPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to navigate startup folder '{folderPath}': {ex.Message}");
            }
        }

        private void NavigateStartupFolderWithoutBlocking(string folderPath)
        {
            async Task NavigateAsync()
            {
                try
                {
                    await _navigateExplorerToFolderAsync(folderPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Startup folder navigation failed for '{folderPath}': {ex.Message}");
                }
            }

            _ = NavigateAsync();
        }

        private void QueuePreviewInitialization()
        {
            async void InitializePreview()
            {
                try
                {
                    await Task.Delay(150);
                    await _livePreviewController.InitializeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Deferred preview initialization failed: {ex.Message}");
                }
            }

            if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, InitializePreview))
            {
                InitializePreview();
            }
        }

        private static StartupPaths ParseStartupPaths(string[]? args)
        {
            var files = new List<string>();
            var folders = new List<string>();

            if (args == null || args.Length <= 1)
            {
                return new StartupPaths(files, folders);
            }

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("-") || arg.StartsWith("/"))
                {
                    continue;
                }

                try
                {
                    string path = arg.Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (File.Exists(path))
                    {
                        files.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        folders.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to pre-check command-line path '{arg}': {ex.Message}");
                }
            }

            return new StartupPaths(files, folders);
        }

        private sealed record StartupPaths(IReadOnlyList<string> Files, IReadOnlyList<string> Folders);
    }
}
