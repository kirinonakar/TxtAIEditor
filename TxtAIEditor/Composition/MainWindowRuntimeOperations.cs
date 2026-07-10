using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed class MainWindowRuntimeOperations
    {
        private readonly MainWindow _window;
        private readonly MainWindowUiRefs _ui;
        private readonly MainWindowServices _services;
        private readonly MainWindowViewModel _viewModel;
        private readonly MainWindowState _state;
        private readonly Func<MainWindowControllers> _getControllers;
        private bool _startupInitializationComplete;

        public MainWindowRuntimeOperations(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            MainWindowState state,
            Func<MainWindowControllers> getControllers)
        {
            _window = window;
            _ui = ui;
            _services = services;
            _viewModel = viewModel;
            _state = state;
            _getControllers = getControllers;
        }

        public bool IsStartupInitializationComplete => _startupInitializationComplete;

        public bool ScrollSyncEnabled
        {
            get => _state.ScrollSyncEnabled;
            set => _state.ScrollSyncEnabled = value;
        }

        private MainWindowControllers Controllers => _getControllers();

        public MainWindowCompositionRootCallbacks CreateCompositionCallbacks()
        {
            return new MainWindowCompositionRootCallbacks(
                GetCurrentElementTheme,
                GetLocalizedString,
                UpdateWindowTitle,
                ReloadTabWithEncodingAsync,
                MarkTabDirtyFromStatusBar,
                PerformLineNavigationAsync,
                ToggleMaximize,
                LoadFileIntoTabAsync,
                LoadFileIntoTabAsync,
                LoadFileIntoTabForAgentAsync,
                UpdateRightPanelSelectionContext,
                UpdateLivePreview,
                UpdateLanguageUi,
                SchedulePreview,
                FocusSearchPanel,
                EnsureLeftPanelVisible,
                ShowLeftSidebarPage,
                request => OpenNewTab(request),
                () => OpenNewTab(),
                OpenGeneratedTab,
                OpenImageTab,
                OpenMediaTab,
                OpenPdfTab,
                OpenOfficeDocumentTab,
                OpenHexViewAsync,
                CloseActiveTab,
                SyncSnippetsToOpenEditorsAsync,
                InitializePickerWindow,
                GetAgentSessionEdits,
                CloseTabAndCleanup,
                CloseReadOnlyViewer,
                SaveTabAsync,
                GetPreviewBaseHref,
                RefreshActivePreview,
                LocalizeUi,
                SyncAgentSettingsAfterLoad,
                UpdateAutoSaveStatus,
                GetSelectedExplorerItem,
                SetCurrentRepoPath,
                SetCurrentFolderPath,
                () => IsStartupInitializationComplete,
                OnGitFileRestored);
        }

        public async Task PrepareForInitialActivationAsync()
        {
            try
            {
                if (!_services.SettingsService.IsLoaded)
                {
                    await _services.SettingsService.LoadSettingsAsync();
                }

                WindowPlacementService.ApplySavedWindowPlacement(_window.AppWindow, _services.SettingsService.CurrentSettings);
                SyncAgentSettingsAfterLoad();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to prepare initial window placement: {ex.Message}");
            }
        }

        public async Task InitializeStartupAsync()
        {
            try
            {
                await Controllers.Lifecycle.Startup.InitializeAsync();
            }
            finally
            {
                _startupInitializationComplete = true;
            }
        }

        public void SetCurrentFolderPath(string folderPath)
        {
            if (_state.CurrentFolderPath == folderPath)
            {
                return;
            }

            _state.CurrentFolderPath = folderPath;
            Controllers.Editor.Foundation.SearchReplace.CancelActiveSearch();
            UpdateAutoSaveStatus();
            UpdateAllTabWorkspaceIndicators();
            Controllers.Workspace.GitStatusRefresh.QueueRefresh();

            if (Controllers?.Workspace?.FavoritesRecent != null && !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                Controllers.Workspace.FavoritesRecent.AddRecentFolder(folderPath);
            }
        }

        public void SetCurrentRepoPath(string repoPath)
        {
            if (_state.CurrentRepoPath == repoPath)
            {
                return;
            }

            _state.CurrentRepoPath = repoPath;
            UpdateAutoSaveStatus();
        }

        public OpenedTab OpenNewTab(
            string? filePath = null,
            string content = "",
            bool isReadOnly = false,
            string encodingName = "UTF-8",
            bool encodingWasAutoDetected = true,
            ITextModel? textModel = null,
            bool isEncrypted = false,
            string? encryptionPassword = null)
        {
            return Controllers.Editor.Runtime.EditorTabOpen.OpenNewTab(
                filePath,
                content,
                isReadOnly,
                encodingName,
                encodingWasAutoDetected,
                textModel,
                isEncrypted,
                encryptionPassword);
        }

        public OpenedTab OpenNewTab(FileTabOpenRequest request)
        {
            return OpenNewTab(
                request.FilePath,
                request.Content,
                request.IsReadOnly,
                request.EncodingName,
                request.EncodingWasAutoDetected,
                request.TextModel,
                request.IsEncrypted,
                request.EncryptionPassword);
        }

        public OpenedTab OpenGeneratedTab(string content) => OpenNewTab(null, content);

        public OpenedTab OpenPdfTab(string filePath) => Controllers.Editor.Runtime.EditorTabOpen.OpenPdfTab(filePath);

        public OpenedTab OpenOfficeDocumentTab(string filePath) => Controllers.Editor.Runtime.EditorTabOpen.OpenOfficeDocumentTab(filePath);

        public OpenedTab OpenImageTab(string filePath) => Controllers.Editor.Runtime.EditorTabOpen.OpenImageTab(filePath);

        public OpenedTab OpenMediaTab(string filePath) => Controllers.Editor.Runtime.EditorTabOpen.OpenMediaTab(filePath);

        public Task OpenHexViewAsync(OpenedTab tab)
        {
            string? sourcePath = GetHexViewSourcePath(tab);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return Task.CompletedTask;
            }

            try
            {
                var existingHexTab = FindOpenHexTab(sourcePath);
                if (existingHexTab != null)
                {
                    ActivateLoadedTab(existingHexTab);
                    return Task.CompletedTask;
                }

                var openedTab = Controllers.Editor.Runtime.EditorTabOpen.OpenHexTab(sourcePath);
                ActivateLoadedTab(openedTab);
            }
            catch (Exception ex)
            {
                Controllers.Shell.Core.Dialog.ShowErrorMessage(
                    GetLocalizedString("HexViewOpenFailedTitle", "Hex 보기 실패"),
                    ex.Message);
            }

            return Task.CompletedTask;
        }

        public void SchedulePreview(OpenedTab tab) => Controllers.Preview.LivePreview.Schedule(tab);

        public void UpdateLivePreview(OpenedTab tab) => Controllers.Preview.LivePreview.Render(tab);

        public string GetPreviewBaseHref(OpenedTab tab) => Controllers.Preview.LivePreview.GetPreviewBaseHref(tab);

        public Task LoadFileIntoTabAsync(string filePath) => LoadFileIntoTabAsync(filePath, 0);

        public async Task LoadFileIntoTabAsync(string filePath, int lineNumber)
        {
            if (Controllers.Workspace.ExplorerNavigation.TryOpenArchive(filePath))
            {
                return;
            }

            var loadedTab = await Controllers.Workspace.FileTabLoad.LoadAsync(filePath);
            if (loadedTab != null)
            {
                ActivateLoadedTab(loadedTab);
            }

            if (lineNumber >= 1)
            {
                await Task.Delay(250);
                await Controllers.Preview.EditorLineNavigation.RevealFileLineAsync(filePath, lineNumber);
            }
        }

        public async Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath)
        {
            var loadResult = await Controllers.Workspace.FileTabLoad.LoadWithResultAsync(filePath);
            if (!loadResult.Success || loadResult.Tab == null)
            {
                return AgentOpenFileResult.Failed(
                    filePath,
                    string.IsNullOrWhiteSpace(loadResult.ErrorMessage)
                        ? "file could not be opened in the editor."
                        : loadResult.ErrorMessage);
            }

            ActivateLoadedTab(loadResult.Tab);

            return loadResult.ActivatedExistingTab
                ? AgentOpenFileResult.ActivatedExisting(loadResult.FullPath)
                : AgentOpenFileResult.Opened(loadResult.FullPath);
        }

        public void UpdateRightPanelSelectionContext(string selectedText, OpenedTab tab, int startLine, int endLine)
        {
            Controllers.Agents.LlmAssistant.SetSelectionText(selectedText);
            Controllers.Agents.Agent.SetSelectionText(selectedText, tab, startLine, endLine);
            if (string.IsNullOrEmpty(selectedText))
            {
                _ui.PreviewGrid.SelectionStats.Text = GetLocalizedString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
            }
            else
            {
                string fmt = GetLocalizedString("SelectionStats", "선택 영역: {0} 글자 수 (약 {1} 토큰)");
                _ui.PreviewGrid.SelectionStats.Text = string.Format(fmt, selectedText.Length.ToString("N0"), StatusBarController.EstimateTokenCount(selectedText).ToString("N0"));
            }

            Controllers.Shell.Core.StatusBar.UpdateSelectionStats(selectedText);
        }

        public void ShowLeftSidebarPage(int index) => Controllers.Editor.Runtime.ShellPane.ShowLeftSidebarPage(index);

        public void EnsureLeftPanelVisible() => Controllers.Editor.Runtime.ShellPane.EnsureLeftPanelVisible();

        public void FocusSearchPanel() => Controllers.Editor.Runtime.ShellPane.FocusSearchPanel();

        public void ToggleMaximize() => MainWindowLayoutOperations.ToggleMaximize(_window.AppWindow);

        public string GetLocalizedString(string key, string fallback)
        {
            return _services.LocalizationService.GetString(key, fallback);
        }

        public void LocalizeUi() => Controllers.Lifecycle.Settings.LocalizeUi();

        public async Task SyncSnippetsToOpenEditorsAsync()
        {
            var snippets = _services.SnippetService.GetSnippets();
            var autocompleteWords = _services.SnippetService.GetAutocompleteWords();
            foreach (var grp in _state.TabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateSnippetsAsync(snippets, autocompleteWords);
                }
            }
        }

        public void RefreshActivePreview() => Controllers.Preview.LivePreview.EnsureVisiblePreviewRendered();

        public Task OpenShellPathAsync(string path)
        {
            return MainWindowWorkspaceOperations.OpenShellPathAsync(
                path,
                Controllers.Shell.Core.ShellPanelLayout,
                Controllers.Workspace.ExplorerNavigation,
                LoadFileIntoTabAsync);
        }

        public IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return Controllers.Agents.Agent.SessionEdits;
        }

        public void CloseTabAndCleanup(OpenedTab tab, TabViewItem tabItem)
        {
            Controllers.Documents.TabClose.CloseAndCleanup(tab, tabItem);
        }

        public void CloseReadOnlyViewer(string tabId)
        {
            Controllers.Preview.PdfViewer.Close(tabId);
            Controllers.Preview.OfficeDocumentViewer.Close(tabId);
        }

        public void MarkTabDirtyFromStatusBar(OpenedTab tab)
        {
            var tabItem = _ui.EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                       ?? _ui.EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
            if (tabItem != null)
            {
                Controllers.Editor.Foundation.TabDirtyState.MarkTabDirty(tab, tabItem);
            }
            else
            {
                tab.IsDirty = true;
            }
        }

        public async Task ReloadTabWithEncodingAsync(OpenedTab tab, string encodingName)
        {
            if (tab.IsReadOnlyViewer && !tab.IsReadOnlyTextFile)
            {
                Controllers.Shell.Core.StatusBar.UpdateFileStats(tab);
                Controllers.Shell.Core.StatusBar.UpdateTotalLines(tab);
                Controllers.Shell.Core.StatusBar.SyncLineEndingText(tab);
                UpdateLanguageUi(tab);
                UpdateWindowTitle();
                return;
            }

            await Controllers.Editor.Foundation.TabReload.ReloadWithEncodingAsync(tab, encodingName);
        }

        public void OnGitFileRestored(object? sender, string filePath)
        {
            _window.DispatcherQueue.TryEnqueue(async () =>
            {
                var tabsToProcess = _viewModel.Tabs.Where(t =>
                    !string.IsNullOrEmpty(t.FilePath) &&
                    (
                        (!string.IsNullOrEmpty(filePath) && t.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)) ||
                        (string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(_state.CurrentRepoPath) && t.FilePath.StartsWith(_state.CurrentRepoPath, StringComparison.OrdinalIgnoreCase))
                    )
                ).ToList();

                foreach (var tab in tabsToProcess)
                {
                    if (!File.Exists(tab.FilePath))
                    {
                        var tabItem = _ui.EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                                   ?? _ui.EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
                        if (tabItem != null)
                        {
                            CloseTabAndCleanup(tab, tabItem);
                        }
                    }
                    else
                    {
                        await ReloadTabWithEncodingAsync(tab, tab.EncodingName);
                    }
                }

                if (!string.IsNullOrEmpty(_state.CurrentFolderPath) && Directory.Exists(_state.CurrentFolderPath))
                {
                    Controllers.Workspace.ExplorerNavigation.LoadDirectoryRoot(_state.CurrentFolderPath);
                }
            });
        }

        public void InitializePickerWindow(object picker)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(_window);
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        public void UpdateWindowTitle() => Controllers.Shell.Core.WindowTitle.Update();

        public Task<bool> SaveTabAsync(OpenedTab tab) => Controllers.Documents.TabSave.SaveAsync(tab);

        public void CloseActiveTab()
        {
            Controllers.Documents.TabClose.CloseActive(Controllers.Shell.Core.TabNavigation.GetCurrentActiveTabView());
        }

        public async Task HandleAppWindowClosingAsync(Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            await Controllers.Documents.WindowClose.HandleClosingAsync(args);
        }

        public ElementTheme GetCurrentElementTheme()
        {
            if (string.Equals(_services.SettingsService.CurrentSettings.Theme, "Light", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Light;
            }

            if (string.Equals(_services.SettingsService.CurrentSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Dark;
            }

            return _window.Content is FrameworkElement element
                ? element.ActualTheme
                : ElementTheme.Default;
        }

        public void UpdateLanguageUi(OpenedTab tab) => Controllers.Shell.Core.StatusBar.UpdateLanguage(tab);

        public async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            await Controllers.Preview.EditorLineNavigation.RevealTabLineAsync(tabId, targetLine);
        }

        public void SyncAgentSettingsAfterLoad()
        {
            Controllers.Agents.Agent.UpdateModelDisplay(true);
            Controllers.Agents.Agent.UpdateContextStats();
        }

        public void UpdateAutoSaveStatus()
        {
            Controllers.Documents.AutoSave.UpdateStatus();
        }

        public ExplorerItem? GetSelectedExplorerItem()
        {
            return _ui.LeftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private void ActivateLoadedTab(OpenedTab tab)
        {
            var tabView = Controllers.Shell.Core.TabNavigation.GetTabView(tab);
            var tabItem = tabView != null ? TabNavigationController.FindItem(tabView, tab.Id) : null;

            if (tabView != null && tabItem != null)
            {
                _ui.EditorWorkspace.ActiveTabView = tabView;
                if (!ReferenceEquals(tabView.SelectedItem, tabItem))
                {
                    tabView.SelectedItem = tabItem;
                }

                Controllers.Editor.Runtime.TabSelection.QueueChanged(tabView, tabItem);
            }

            Controllers.Preview.LivePreview.Render(tab);
        }

        private static string? GetHexViewSourcePath(OpenedTab tab)
        {
            return !string.IsNullOrWhiteSpace(tab.FilePath)
                ? tab.FilePath
                : tab.HexSourceFilePath;
        }

        private OpenedTab? FindOpenHexTab(string sourcePath)
        {
            string normalizedSourcePath = NormalizePathForComparison(sourcePath);
            return _viewModel.Tabs.FirstOrDefault(tab =>
                tab.IsHexViewer &&
                !string.IsNullOrWhiteSpace(tab.HexSourceFilePath) &&
                string.Equals(
                    NormalizePathForComparison(tab.HexSourceFilePath),
                    normalizedSourcePath,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePathForComparison(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private void UpdateAllTabWorkspaceIndicators()
        {
            var folderPath = _state.CurrentFolderPath;
            UpdateTabViewWorkspaceIndicators(_ui.EditorTabView, folderPath);
            UpdateTabViewWorkspaceIndicators(_ui.EditorTabView2, folderPath);
        }

        private static void UpdateTabViewWorkspaceIndicators(TabView tabView, string folderPath)
        {
            foreach (var item in tabView.TabItems)
            {
                if (item is TabViewItem tabItem && tabItem.Header is TabHeaderControl header)
                {
                    header.SetWorkspaceFolderPath(folderPath);
                }
            }
        }
    }
}
