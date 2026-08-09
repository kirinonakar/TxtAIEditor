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
    internal sealed class MainWindowRuntimeOperations :
        IMainWindowShellFacade,
        IMainWindowEditorFacade,
        IMainWindowDocumentFacade,
        IMainWindowPreviewFacade,
        IMainWindowAgentFacade,
        IMainWindowWorkspaceFacade,
        IMainWindowLifecycleFacade
    {
        private readonly MainWindow _window;
        private readonly MainWindowUiRefs _ui;
        private readonly MainWindowCommonServices _commonServices;
        private readonly MainWindowEditorServices _editorServices;
        private readonly MainWindowViewModel _viewModel;
        private readonly MainWindowState _state;
        private readonly Func<MainWindowControllers> _getControllers;
        private bool _startupInitializationComplete;

        public MainWindowRuntimeOperations(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowEditorServices editorServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            Func<MainWindowControllers> getControllers)
        {
            _window = window;
            _ui = ui;
            _commonServices = commonServices;
            _editorServices = editorServices;
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

        public MainWindowHostFacades CreateHostFacades()
        {
            return new MainWindowHostFacades(
                this,
                this,
                this,
                this,
                this,
                this,
                this);
        }

        public async Task PrepareForInitialActivationAsync()
        {
            try
            {
                if (!_commonServices.SettingsService.IsLoaded)
                {
                    await _commonServices.SettingsService.LoadSettingsAsync();
                }

                WindowPlacementService.ApplySavedWindowPlacement(_window.AppWindow, _commonServices.SettingsService.CurrentSettings);
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
            Controllers.Editor.CancelActiveSearch();
            UpdateAutoSaveStatus();
            UpdateAllTabWorkspaceIndicators();
            Controllers.Agents.Composition.Agent.NotifyWorkspaceChanged();
            Controllers.Workspace.QueueGitStatusRefresh();

            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                Controllers.Workspace.AddRecentFolder(folderPath);
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
            return Controllers.Editor.OpenNewTab(
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

        public OpenedTab OpenEmptyTab() => OpenNewTab();

        public OpenedTab OpenPdfTab(string filePath) => Controllers.Editor.OpenPdfTab(filePath);

        public OpenedTab OpenOfficeDocumentTab(string filePath) => Controllers.Editor.OpenOfficeDocumentTab(filePath);

        public OpenedTab OpenHexTab(string filePath) => Controllers.Editor.OpenHexTab(filePath);

        public OpenedTab OpenNotebookTab(string filePath) => Controllers.Editor.OpenNotebookTab(filePath);

        public async Task OpenNotebookSourceTabAsync(string filePath) => await Controllers.Editor.OpenNotebookSourceTabAsync(filePath);

        public async Task OpenNotebookViewerTabAsync(string filePath) => await Controllers.Editor.OpenNotebookViewerTabAsync(filePath);

        public OpenedTab OpenImageTab(string filePath) => Controllers.Editor.OpenImageTab(filePath);

        public OpenedTab OpenMediaTab(string filePath) => Controllers.Editor.OpenMediaTab(filePath);

        public async Task SetHexViewModeAsync(OpenedTab tab, bool enabled)
        {
            try
            {
                await Controllers.Editor.SetHexViewModeAsync(tab, enabled);
            }
            catch (Exception ex)
            {
                Controllers.Shell.ShowErrorMessage(
                    GetLocalizedString("HexViewOpenFailedTitle", "Hex 보기 실패"),
                    ex.Message);
            }
        }

        public void SchedulePreview(OpenedTab tab) => Controllers.Preview.Schedule(tab);

        public void UpdateLivePreview(OpenedTab tab) => Controllers.Preview.Render(tab);

        public string GetPreviewBaseHref(OpenedTab tab) => Controllers.Preview.GetPreviewBaseHref(tab);

        public Task LoadFileIntoTabAsync(string filePath) => LoadFileIntoTabAsync(filePath, 0);

        public async Task LoadFileIntoTabAsync(string filePath, int lineNumber)
        {
            if (Controllers.Workspace.TryOpenArchive(filePath))
            {
                return;
            }

            var loadedTab = await Controllers.Workspace.LoadFileAsync(filePath);
            if (loadedTab != null)
            {
                ActivateLoadedTab(loadedTab);
            }

            if (lineNumber >= 1)
            {
                await Task.Delay(250);
                await Controllers.Preview.RevealFileLineAsync(filePath, lineNumber);
            }
        }

        public async Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath)
        {
            var loadResult = await Controllers.Workspace.LoadFileWithResultAsync(filePath);
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
            Controllers.Agents.SetSelectionContext(selectedText, tab, startLine, endLine);
            if (string.IsNullOrEmpty(selectedText))
            {
                _ui.PreviewGrid.SelectionStats.Text = GetLocalizedString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
            }
            else
            {
                string fmt = GetLocalizedString("SelectionStats", "선택 영역: {0} 글자 수 (약 {1} 토큰)");
                _ui.PreviewGrid.SelectionStats.Text = string.Format(fmt, selectedText.Length.ToString("N0"), StatusBarController.EstimateTokenCount(selectedText).ToString("N0"));
            }

            Controllers.Shell.UpdateSelectionStats(selectedText);
        }

        public void ShowLeftSidebarPage(int index) => Controllers.Editor.ShowLeftSidebarPage(index);

        public void EnsureLeftPanelVisible() => Controllers.Editor.EnsureLeftPanelVisible();

        public void FocusSearchPanel() => Controllers.Editor.FocusSearchPanel();

        public void ToggleMaximize() => MainWindowLayoutOperations.ToggleMaximize(_window.AppWindow);

        public string GetLocalizedString(string key, string fallback)
        {
            return _commonServices.LocalizationService.GetString(key, fallback);
        }

        public void LocalizeUi() => Controllers.Lifecycle.Settings.LocalizeUi();

        public async Task SyncSnippetsToOpenEditorsAsync()
        {
            var snippets = _editorServices.SnippetService.GetSnippets();
            var autocompleteWords = _editorServices.SnippetService.GetAutocompleteWords();
            foreach (var grp in _state.TabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateSnippetsAsync(snippets, autocompleteWords);
                }
            }
        }

        public void RefreshActivePreview() => Controllers.Preview.RefreshActivePreview();

        public Task OpenShellPathAsync(string path)
        {
            return Controllers.Workspace.OpenShellPathAsync(
                path,
                Controllers.Shell,
                LoadFileIntoTabAsync);
        }

        public IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return Controllers.Agents.GetSessionEdits();
        }

        public void CloseTabAndCleanup(OpenedTab tab, TabViewItem tabItem)
        {
            Controllers.Editor.ForgetHexViewState(tab.Id);
            Controllers.Documents.CloseAndCleanup(tab, tabItem);
        }

        public void CloseReadOnlyViewer(string tabId)
        {
            Controllers.Preview.CloseReadOnlyViewer(tabId);
        }

        public void MarkTabDirtyFromStatusBar(OpenedTab tab)
        {
            var tabItem = _ui.EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                       ?? _ui.EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
            if (tabItem != null)
            {
                Controllers.Editor.MarkTabDirty(tab, tabItem);
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
                Controllers.Shell.UpdateReadOnlyTabStatus(tab);
                UpdateLanguageUi(tab);
                UpdateWindowTitle();
                return;
            }

            await Controllers.Editor.ReloadWithEncodingAsync(tab, encodingName);
        }

        public void HandleGitFileRestored(object? sender, string filePath)
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
                    Controllers.Workspace.RefreshCurrentFolder(_state.CurrentFolderPath);
                }
            });
        }

        public void InitializePickerWindow(object picker)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(_window);
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        public void UpdateWindowTitle() => Controllers.Shell.UpdateWindowTitle();

        public Task<bool> SaveTabAsync(OpenedTab tab) => Controllers.Documents.SaveAsync(tab);

        public void CloseActiveTab()
        {
            Controllers.Documents.CloseActive(Controllers.Shell.GetCurrentActiveTabView());
        }

        public async Task HandleAppWindowClosingAsync(
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args,
            bool saveUiLayoutSettings = true)
        {
            await Controllers.Documents.HandleWindowClosingAsync(args, saveUiLayoutSettings);
        }

        public Task SaveUiLayoutSettingsAsync() => Controllers.Shell.SaveUiLayoutSettingsAsync();

        public EditorSettings CurrentSettings => _commonServices.SettingsService.CurrentSettings;

        public ElementTheme GetCurrentElementTheme()
        {
            if (string.Equals(_commonServices.SettingsService.CurrentSettings.Theme, "Light", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Light;
            }

            if (string.Equals(_commonServices.SettingsService.CurrentSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Dark;
            }

            return _window.Content is FrameworkElement element
                ? element.ActualTheme
                : ElementTheme.Default;
        }

        public void UpdateLanguageUi(OpenedTab tab) => Controllers.Shell.UpdateLanguage(tab);

        public async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            await Controllers.Preview.RevealTabLineAsync(tabId, targetLine);
        }

        public void SyncAgentSettingsAfterLoad()
        {
            Controllers.Agents.SyncSettingsAfterLoad();
        }

        public void UpdateAutoSaveStatus()
        {
            Controllers.Documents.UpdateAutoSaveStatus();
        }

        public ExplorerItem? GetSelectedExplorerItem()
        {
            return _ui.LeftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private void ActivateLoadedTab(OpenedTab tab)
        {
            var tabView = Controllers.Shell.GetTabView(tab);
            var tabItem = tabView != null ? TabNavigationController.FindItem(tabView, tab.Id) : null;

            if (tabView != null && tabItem != null)
            {
                _ui.EditorWorkspace.ActiveTabView = tabView;
                if (!ReferenceEquals(tabView.SelectedItem, tabItem))
                {
                    tabView.SelectedItem = tabItem;
                }

                Controllers.Editor.QueueTabSelectionChanged(tabView, tabItem);
            }

            Controllers.Preview.Render(tab);
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
