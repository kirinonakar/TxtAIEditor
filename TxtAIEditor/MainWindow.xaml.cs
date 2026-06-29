using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Composition;
using TxtAIEditor.Controls;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;


namespace TxtAIEditor
{
    public sealed partial class MainWindow : Window
    {
        private readonly ISettingsService _settingsService;
        private readonly IGitService _gitService;
        private readonly ISnippetService _snippetService;
        private readonly ILocalizationService _localizationService;
        private readonly MainWindowControllers? _controllers;
        private MainWindowControllers Controllers =>
            _controllers ?? throw new InvalidOperationException("MainWindow controllers have not been composed.");
        private ShellPanelLayoutService _shellPanelLayoutService => Controllers.ShellPanelLayout;
        private SearchReplaceController _searchReplaceController => Controllers.SearchReplace;
        private GitStatusRefreshController _gitStatusRefreshController => Controllers.GitStatusRefresh;
        private TabNavigationController _tabNavigationController => Controllers.TabNavigation;
        private LlmAssistantController _llmAssistantController => Controllers.LlmAssistant;
        private AgentController _agentController => Controllers.Agent;
        private ShellPaneController _shellPaneController => Controllers.ShellPane;
        private StatusBarController _statusBarController => Controllers.StatusBar;
        private TabReloadController _tabReloadController => Controllers.TabReload;
        private LivePreviewController _livePreviewController => Controllers.LivePreview;
        private PdfViewerController _pdfViewerController => Controllers.PdfViewer;
        private OfficeDocumentViewerController _officeDocumentViewerController => Controllers.OfficeDocumentViewer;
        private TabSelectionController _tabSelectionController => Controllers.TabSelection;
        private EditorLineNavigationController _editorLineNavigationController => Controllers.EditorLineNavigation;
        private EditorTabOpenController _editorTabOpenController => Controllers.EditorTabOpen;
        private TabDirtyStateController _tabDirtyStateController => Controllers.TabDirtyState;
        private TabSaveController _tabSaveController => Controllers.TabSave;
        private TabCloseController _tabCloseController => Controllers.TabClose;
        private AutoSaveController _autoSaveController => Controllers.AutoSave;
        private MainWindowStartupController _startupController => Controllers.Startup;
        private MainWindowLifecycleController _lifecycleController => Controllers.Lifecycle;
        private FileTabLoadController _fileTabLoadController => Controllers.FileTabLoad;
        private ExplorerNavigationController _explorerNavigationController => Controllers.ExplorerNavigation;
        private WindowCloseController _windowCloseController => Controllers.WindowClose;
        private WindowTitleController _windowTitleController => Controllers.WindowTitle;
        private MainWindowSettingsController _settingsController => Controllers.Settings;
        private readonly MainWindowViewModel _viewModel = new MainWindowViewModel();
        private readonly MainWindowState _state = new MainWindowState();
        private bool _startupInitializationComplete;

        private string CurrentFolderPath
        {
            get => _state.CurrentFolderPath;
            set
            {
                if (_state.CurrentFolderPath != value)
                {
                    _state.CurrentFolderPath = value;
                    _searchReplaceController?.CancelActiveSearch();
                    UpdateAutoSaveStatus();
                    UpdateAllTabWorkspaceIndicators();
                    _gitStatusRefreshController?.QueueRefresh();
                }
            }
        }

        private void UpdateAllTabWorkspaceIndicators()
        {
            var folderPath = _state.CurrentFolderPath;
            UpdateTabViewWorkspaceIndicators(EditorTabView, folderPath);
            UpdateTabViewWorkspaceIndicators(EditorTabView2, folderPath);
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

        private string CurrentRepoPath
        {
            get => _state.CurrentRepoPath;
            set
            {
                if (_state.CurrentRepoPath != value)
                {
                    _state.CurrentRepoPath = value;
                    UpdateAutoSaveStatus();
                }
            }
        }

        private void UpdateAutoSaveStatus()
        {
            _autoSaveController?.UpdateStatus();
        }

        public bool ScrollSyncEnabled
        {
            get => _state.ScrollSyncEnabled;
            set => _state.ScrollSyncEnabled = value;
        }

        private const int InitialEditorLineWarmupCount = 120;

        private ListView FileListView => LeftSidebarTabView.FileList;
        private TextBlock SelectionStatsText => PreviewGrid.SelectionStats;
        private TabView EditorTabView => EditorWorkspace.EditorTabViewControl;
        private TabView EditorTabView2 => EditorWorkspace.EditorTabView2Control;
        private TerminalPane TerminalPane => EditorWorkspace.TerminalPaneControl;

        private MainWindowUiRefs CreateUiRefs()
        {
            return new MainWindowUiRefs(
                RootGrid,
                AppTitleBar,
                TitleBarRow,
                AppTitleTextBlock,
                TopToolbar,
                MarkdownToolbarHost,
                MarkdownToolbar,
                MainWorkGrid,
                ExplorerColumn,
                PreviewColumn,
                LeftSplitter,
                RightSplitter,
                LeftSidebarTabView,
                EditorWorkspace,
                PreviewGrid,
                StatusBarPane,
                DragOverlay,
                EditorTabView,
                EditorTabView2,
                TerminalPane,
                Content as FrameworkElement ?? RootGrid);
        }

        public MainWindow()
        {
            this.InitializeComponent();
            WindowPlacementService.SetWindowIcon(AppWindow);

            // Start pre-warming the shared WebView2 environment in the background
            _ = TxtAIEditor.Editor.MonacoBridge.GetSharedEnvironmentAsync();

            var ui = CreateUiRefs();
            var services = MainWindowServices.Create(GetLocalizedString);
            _settingsService = services.SettingsService;
            _localizationService = services.LocalizationService;
            _gitService = services.GitService;
            _snippetService = services.SnippetService;

            _controllers = MainWindowCompositionRoot.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state,
                InitialEditorLineWarmupCount,
                new MainWindowCompositionRootCallbacks(
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
                    UpdateLanguageUI,
                    SchedulePreview,
                    FocusSearchPanel,
                    EnsureLeftPanelVisible,
                    ShowLeftSidebarPage,
                    request => OpenNewTab(
                        request.FilePath,
                        request.Content,
                        request.IsReadOnly,
                        request.EncodingName,
                        request.EncodingWasAutoDetected,
                        request.TextModel,
                        request.IsEncrypted,
                        request.EncryptionPassword),
                    () => OpenNewTab(),
                    content => OpenNewTab(null, content),
                    OpenImageTab,
                    OpenPdfTab,
                    OpenOfficeDocumentTab,
                    () => OnCloseActiveTabShortcutInvoked(null!, null!),
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
                    () => FileListView.SelectedItem as ExplorerItem,
                    path => CurrentRepoPath = path,
                    path => CurrentFolderPath = path,
                    () => _startupInitializationComplete,
                    OnGitFileRestored));

            // Load local configurations and boot initial states
            // Setup custom title bar
            _lifecycleController.InitializeTitleBar();

            this.Activated += OnWindowActivated;
            this.Activated += _lifecycleController.HandleActivationChanged;
            this.Closed += _lifecycleController.HandleWindowClosed;
            this.AppWindow.Closing += OnAppWindowClosing;
            _lifecycleController.StartShortcuts();

        }

        public async Task PrepareForInitialActivationAsync()
        {
            try
            {
                if (!_settingsService.IsLoaded)
                {
                    await _settingsService.LoadSettingsAsync();
                }

                WindowPlacementService.ApplySavedWindowPlacement(AppWindow, _settingsService.CurrentSettings);
                SyncAgentSettingsAfterLoad();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to prepare initial window placement: {ex.Message}");
            }
        }

        private void SyncAgentSettingsAfterLoad()
        {
            _agentController.UpdateModelDisplay(true);
            _agentController.UpdateContextStats();
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= OnWindowActivated;
            try
            {
                await _startupController.InitializeAsync();
            }
            finally
            {
                _startupInitializationComplete = true;
            }
        }

        #region Tab Operations (탭 비즈니스 로직)

        private OpenedTab OpenNewTab(
            string? filePath = null,
            string content = "",
            bool isReadOnly = false,
            string encodingName = "UTF-8",
            bool encodingWasAutoDetected = true,
            ITextModel? textModel = null,
            bool isEncrypted = false,
            string? encryptionPassword = null)
        {
            return _editorTabOpenController.OpenNewTab(
                filePath,
                content,
                isReadOnly,
                encodingName,
                encodingWasAutoDetected,
                textModel,
                isEncrypted,
                encryptionPassword);
        }

        private OpenedTab OpenPdfTab(string filePath)
        {
            return _editorTabOpenController.OpenPdfTab(filePath);
        }

        private OpenedTab OpenOfficeDocumentTab(string filePath)
        {
            return _editorTabOpenController.OpenOfficeDocumentTab(filePath);
        }

        private OpenedTab OpenImageTab(string filePath)
        {
            return _editorTabOpenController.OpenImageTab(filePath);
        }

        private void SchedulePreview(OpenedTab tab)
        {
            _livePreviewController.Schedule(tab);
        }

        #endregion

        #region Live Preview Debouncing & Sync

        private void UpdateLivePreview(OpenedTab tab) => _livePreviewController.Render(tab);

        private string GetPreviewBaseHref(OpenedTab tab) => _livePreviewController.GetPreviewBaseHref(tab);

        #endregion

        #region XAML Interactive Handlers


        internal Task LoadFileIntoTabAsync(string filePath) => LoadFileIntoTabAsync(filePath, 0);

        internal async Task LoadFileIntoTabAsync(string filePath, int lineNumber)
        {
            var loadedTab = await _fileTabLoadController.LoadAsync(filePath);
            if (loadedTab != null)
            {
                ActivateLoadedTab(loadedTab);
            }

            if (lineNumber >= 1)
            {
                await Task.Delay(250);
                await _editorLineNavigationController.RevealFileLineAsync(filePath, lineNumber);
            }
        }

        internal async Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath)
        {
            var loadResult = await _fileTabLoadController.LoadWithResultAsync(filePath);
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

        private void ActivateLoadedTab(OpenedTab tab)
        {
            var tabView = _tabNavigationController.GetTabView(tab);
            var tabItem = tabView != null ? TabNavigationController.FindItem(tabView, tab.Id) : null;

            if (tabView != null && tabItem != null)
            {
                EditorWorkspace.ActiveTabView = tabView;
                if (!ReferenceEquals(tabView.SelectedItem, tabItem))
                {
                    tabView.SelectedItem = tabItem;
                }

                _tabSelectionController.QueueChanged(tabView, tabItem);
            }

            _livePreviewController.Render(tab);
        }


        private void UpdateRightPanelSelectionContext(string selectedText, OpenedTab tab, int startLine, int endLine)
        {
            _llmAssistantController.SetSelectionText(selectedText);
            _agentController.SetSelectionText(selectedText, tab, startLine, endLine);
            if (string.IsNullOrEmpty(selectedText))
            {
                SelectionStatsText.Text = GetLocalizedString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
            }
            else
            {
                string fmt = GetLocalizedString("SelectionStats", "선택 영역: {0} 글자 수 (약 {1} 토큰)");
                SelectionStatsText.Text = string.Format(fmt, selectedText.Length.ToString("N0"), StatusBarController.EstimateTokenCount(selectedText).ToString("N0"));
            }
            _statusBarController.UpdateSelectionStats(selectedText);
        }


        private void ShowLeftSidebarPage(int index)
        {
            _shellPaneController.ShowLeftSidebarPage(index);
        }

        private void EnsureLeftPanelVisible()
        {
            _shellPaneController.EnsureLeftPanelVisible();
        }

        private void FocusSearchPanel()
        {
            _shellPaneController.FocusSearchPanel();
        }

        private void ToggleMaximize()
        {
            var presenter = this.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter != null)
            {
                if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                }
                else
                {
                    presenter.Maximize();
                }
            }
        }


        private string GetLocalizedString(string key, string fallback)
        {
            return _localizationService.GetString(key, fallback);
        }

        private void LocalizeUi()
        {
            _settingsController.LocalizeUi();
        }

        private async Task SyncSnippetsToOpenEditorsAsync()
        {
            var snippets = _snippetService.GetSnippets();
            var autocompleteWords = _snippetService.GetAutocompleteWords();
            foreach (var grp in _state.TabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateSnippetsAsync(snippets, autocompleteWords);
                }
            }
        }

        private void RefreshActivePreview()
        {
            _livePreviewController.EnsureVisiblePreviewRendered();
        }

        #endregion

        #region Explorer Side Panel & Folder Picker

        internal Task OpenShellPathAsync(string path)
        {
            return MainWindowWorkspaceOperations.OpenShellPathAsync(
                path,
                _shellPanelLayoutService,
                _explorerNavigationController,
                LoadFileIntoTabAsync);
        }


        #endregion

        #region Terminal Panel Layout


        private IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return _agentController.SessionEdits;
        }

        #endregion

        #region TabView Structural Interops

        private void WarnUnsavedAndClose(OpenedTab tab, TabViewItem tabItem)
        {
            _tabCloseController.WarnUnsavedAndClose(tab, tabItem);
        }

        private void CloseTabAndCleanup(OpenedTab tab, TabViewItem tabItem)
        {
            _tabCloseController.CloseAndCleanup(tab, tabItem);
        }

        private void CloseReadOnlyViewer(string tabId)
        {
            _pdfViewerController.Close(tabId);
            _officeDocumentViewerController.Close(tabId);
        }

        #endregion

        #region Helpers & UI Triggers

        private void MarkTabDirtyFromStatusBar(OpenedTab tab)
        {
            var tabItem = EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                       ?? EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
            if (tabItem != null)
            {
                _tabDirtyStateController.MarkTabDirty(tab, tabItem);
            }
            else
            {
                tab.IsDirty = true;
            }
        }

        private async Task ReloadTabWithEncodingAsync(OpenedTab tab, string encodingName)
        {
            if (tab.IsReadOnlyViewer)
            {
                _statusBarController.UpdateFileStats(tab);
                _statusBarController.UpdateTotalLines(tab);
                _statusBarController.SyncLineEndingText(tab);
                UpdateLanguageUI(tab);
                UpdateWindowTitle();
                return;
            }

            await _tabReloadController.ReloadWithEncodingAsync(tab, encodingName);
        }

        private void OnGitFileRestored(object? sender, string filePath)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
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
                        var tabItem = EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                                   ?? EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
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

                // Refresh the file browser list to reflect the restored files on disk
                if (!string.IsNullOrEmpty(_state.CurrentFolderPath) && Directory.Exists(_state.CurrentFolderPath))
                {
                    _explorerNavigationController.LoadDirectoryRoot(_state.CurrentFolderPath);
                }
            });
        }

        private void InitializePickerWindow(object picker)
        {
            // WinUI 3 Window association wrapper for file pickers (required in WinAppSDK)
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        private void UpdateWindowTitle()
        {
            _windowTitleController.Update();
        }

        #endregion

        private Task<bool> SaveTabAsync(OpenedTab tab) => _tabSaveController.SaveAsync(tab);

        private Task<bool> SaveAsTabAsync(OpenedTab tab) => _tabSaveController.SaveAsAsync(tab);

        private void OnCloseActiveTabShortcutInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (args != null) args.Handled = true;
            _tabCloseController.CloseActive(_tabNavigationController.GetCurrentActiveTabView());
        }

        private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            await _windowCloseController.HandleClosingAsync(args);
        }

        private ElementTheme GetCurrentElementTheme()
        {
            if (string.Equals(_settingsService.CurrentSettings.Theme, "Light", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Light;
            }

            if (string.Equals(_settingsService.CurrentSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Dark;
            }

            return this.Content is FrameworkElement element
                ? element.ActualTheme
                : ElementTheme.Default;
        }

        private void UpdateLanguageUI(OpenedTab tab)
        {
            _statusBarController.UpdateLanguage(tab);
        }

        private async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            await _editorLineNavigationController.RevealTabLineAsync(tabId, targetLine);
        }

    }

}
