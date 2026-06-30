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
                OpenPdfTab,
                OpenOfficeDocumentTab,
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
                await Controllers.Startup.InitializeAsync();
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
            Controllers.SearchReplace.CancelActiveSearch();
            UpdateAutoSaveStatus();
            UpdateAllTabWorkspaceIndicators();
            Controllers.GitStatusRefresh.QueueRefresh();
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
            return Controllers.EditorTabOpen.OpenNewTab(
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

        public OpenedTab OpenPdfTab(string filePath) => Controllers.EditorTabOpen.OpenPdfTab(filePath);

        public OpenedTab OpenOfficeDocumentTab(string filePath) => Controllers.EditorTabOpen.OpenOfficeDocumentTab(filePath);

        public OpenedTab OpenImageTab(string filePath) => Controllers.EditorTabOpen.OpenImageTab(filePath);

        public void SchedulePreview(OpenedTab tab) => Controllers.LivePreview.Schedule(tab);

        public void UpdateLivePreview(OpenedTab tab) => Controllers.LivePreview.Render(tab);

        public string GetPreviewBaseHref(OpenedTab tab) => Controllers.LivePreview.GetPreviewBaseHref(tab);

        public Task LoadFileIntoTabAsync(string filePath) => LoadFileIntoTabAsync(filePath, 0);

        public async Task LoadFileIntoTabAsync(string filePath, int lineNumber)
        {
            var loadedTab = await Controllers.FileTabLoad.LoadAsync(filePath);
            if (loadedTab != null)
            {
                ActivateLoadedTab(loadedTab);
            }

            if (lineNumber >= 1)
            {
                await Task.Delay(250);
                await Controllers.EditorLineNavigation.RevealFileLineAsync(filePath, lineNumber);
            }
        }

        public async Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath)
        {
            var loadResult = await Controllers.FileTabLoad.LoadWithResultAsync(filePath);
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
            Controllers.LlmAssistant.SetSelectionText(selectedText);
            Controllers.Agent.SetSelectionText(selectedText, tab, startLine, endLine);
            if (string.IsNullOrEmpty(selectedText))
            {
                _ui.PreviewGrid.SelectionStats.Text = GetLocalizedString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
            }
            else
            {
                string fmt = GetLocalizedString("SelectionStats", "선택 영역: {0} 글자 수 (약 {1} 토큰)");
                _ui.PreviewGrid.SelectionStats.Text = string.Format(fmt, selectedText.Length.ToString("N0"), StatusBarController.EstimateTokenCount(selectedText).ToString("N0"));
            }

            Controllers.StatusBar.UpdateSelectionStats(selectedText);
        }

        public void ShowLeftSidebarPage(int index) => Controllers.ShellPane.ShowLeftSidebarPage(index);

        public void EnsureLeftPanelVisible() => Controllers.ShellPane.EnsureLeftPanelVisible();

        public void FocusSearchPanel() => Controllers.ShellPane.FocusSearchPanel();

        public void ToggleMaximize() => MainWindowLayoutOperations.ToggleMaximize(_window.AppWindow);

        public string GetLocalizedString(string key, string fallback)
        {
            return _services.LocalizationService.GetString(key, fallback);
        }

        public void LocalizeUi() => Controllers.Settings.LocalizeUi();

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

        public void RefreshActivePreview() => Controllers.LivePreview.EnsureVisiblePreviewRendered();

        public Task OpenShellPathAsync(string path)
        {
            return MainWindowWorkspaceOperations.OpenShellPathAsync(
                path,
                Controllers.ShellPanelLayout,
                Controllers.ExplorerNavigation,
                LoadFileIntoTabAsync);
        }

        public IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return Controllers.Agent.SessionEdits;
        }

        public void CloseTabAndCleanup(OpenedTab tab, TabViewItem tabItem)
        {
            Controllers.TabClose.CloseAndCleanup(tab, tabItem);
        }

        public void CloseReadOnlyViewer(string tabId)
        {
            Controllers.PdfViewer.Close(tabId);
            Controllers.OfficeDocumentViewer.Close(tabId);
        }

        public void MarkTabDirtyFromStatusBar(OpenedTab tab)
        {
            var tabItem = _ui.EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                       ?? _ui.EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
            if (tabItem != null)
            {
                Controllers.TabDirtyState.MarkTabDirty(tab, tabItem);
            }
            else
            {
                tab.IsDirty = true;
            }
        }

        public async Task ReloadTabWithEncodingAsync(OpenedTab tab, string encodingName)
        {
            if (tab.IsReadOnlyViewer)
            {
                Controllers.StatusBar.UpdateFileStats(tab);
                Controllers.StatusBar.UpdateTotalLines(tab);
                Controllers.StatusBar.SyncLineEndingText(tab);
                UpdateLanguageUi(tab);
                UpdateWindowTitle();
                return;
            }

            await Controllers.TabReload.ReloadWithEncodingAsync(tab, encodingName);
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
                    Controllers.ExplorerNavigation.LoadDirectoryRoot(_state.CurrentFolderPath);
                }
            });
        }

        public void InitializePickerWindow(object picker)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(_window);
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        public void UpdateWindowTitle() => Controllers.WindowTitle.Update();

        public Task<bool> SaveTabAsync(OpenedTab tab) => Controllers.TabSave.SaveAsync(tab);

        public void CloseActiveTab()
        {
            Controllers.TabClose.CloseActive(Controllers.TabNavigation.GetCurrentActiveTabView());
        }

        public async Task HandleAppWindowClosingAsync(Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            await Controllers.WindowClose.HandleClosingAsync(args);
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

        public void UpdateLanguageUi(OpenedTab tab) => Controllers.StatusBar.UpdateLanguage(tab);

        public async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            await Controllers.EditorLineNavigation.RevealTabLineAsync(tabId, targetLine);
        }

        public void SyncAgentSettingsAfterLoad()
        {
            Controllers.Agent.UpdateModelDisplay(true);
            Controllers.Agent.UpdateContextStats();
        }

        public void UpdateAutoSaveStatus()
        {
            Controllers.AutoSave.UpdateStatus();
        }

        public ExplorerItem? GetSelectedExplorerItem()
        {
            return _ui.LeftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private void ActivateLoadedTab(OpenedTab tab)
        {
            var tabView = Controllers.TabNavigation.GetTabView(tab);
            var tabItem = tabView != null ? TabNavigationController.FindItem(tabView, tab.Id) : null;

            if (tabView != null && tabItem != null)
            {
                _ui.EditorWorkspace.ActiveTabView = tabView;
                if (!ReferenceEquals(tabView.SelectedItem, tabItem))
                {
                    tabView.SelectedItem = tabItem;
                }

                Controllers.TabSelection.QueueChanged(tabView, tabItem);
            }

            Controllers.LivePreview.Render(tab);
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
