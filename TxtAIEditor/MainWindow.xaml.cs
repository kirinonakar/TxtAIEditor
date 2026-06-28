using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
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
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly TerminalShortcutService _terminalShortcutService;
        private readonly FunctionKeyShortcutService _functionKeyShortcutService;
        private readonly SearchReplaceController _searchReplaceController;
        private readonly SearchReplaceTabSyncController _searchReplaceTabSyncController;
        private readonly GitPanelController _gitPanelController;
        private readonly GitStatusRefreshController _gitStatusRefreshController;
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly ExplorerFileActionsController _explorerFileActionsController;
        private readonly TabContextMenuController _tabContextMenuController;
        private readonly TabNavigationController _tabNavigationController;
        private readonly TabEncryptionController _tabEncryptionController;
        private readonly FileOpenDropController _fileOpenDropController;
        private readonly RootKeyboardShortcutController _rootKeyboardShortcutController;
        private readonly SnippetsController _snippetsController;
        private readonly LlmAssistantController _llmAssistantController;
        private readonly AgentController _agentController;
        private readonly AgentFileWorkflowController _agentFileWorkflowController;
        private readonly TocController _tocController;
        private readonly ShellPaneController _shellPaneController;
        private readonly MarkdownToolbarController _markdownToolbarController;
        private readonly StickyNoteModeController _stickyNoteModeController;
        private readonly StatusBarController _statusBarController;
        private readonly TabReloadController _tabReloadController;
        private readonly CompareTabController _compareTabController;
        private readonly LivePreviewController _livePreviewController;
        private readonly PdfViewerController _pdfViewerController;
        private readonly OfficeDocumentViewerController _officeDocumentViewerController;
        private readonly TabSelectionController _tabSelectionController;
        private readonly EditorSplitLayoutController _editorSplitLayoutController;
        private readonly EditorBridgeShortcutController _editorBridgeShortcutController;
        private readonly EditorBridgeDocumentController _editorBridgeDocumentController;
        private readonly EditorBridgeInteractionController _editorBridgeInteractionController;
        private readonly EditorLinkNavigationController _editorLinkNavigationController;
        private readonly EditorWebViewInitializationController _editorWebViewInitializationController;
        private readonly EditorLineNavigationController _editorLineNavigationController;
        private readonly EditorTabOpenController _editorTabOpenController;
        private readonly ActiveEditorInsertionController _activeEditorInsertionController;
        private readonly PreviewScrollSyncController _previewScrollSyncController;
        private readonly TabTextContextProvider _tabTextContextProvider;
        private readonly WebViewShortcutController _webViewShortcutController;
        private readonly SplitImeSyncController _splitImeSyncController;
        private readonly TabDirtyStateController _tabDirtyStateController;
        private readonly TabSaveController _tabSaveController;
        private readonly TabCloseController _tabCloseController;
        private readonly TabMoveController _tabMoveController;
        private readonly AutoSaveController _autoSaveController;
        private readonly MainWindowStartupController _startupController;
        private readonly MainWindowLifecycleController _lifecycleController;
        private readonly MainWindowShellInteractionController _shellInteractionController;
        private readonly FileTabLoadController _fileTabLoadController;
        private readonly TerminalPanelController _terminalPanelController;
        private readonly ExplorerNavigationController _explorerNavigationController;
        private readonly WindowDialogController _dialogController;
        private readonly WindowCloseController _windowCloseController;
        private readonly WindowTitleController _windowTitleController;
        private readonly MainWindowSettingsController _settingsController;
        private MainWindowToolbarCommandController? _toolbarCommandController;
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

        private readonly DispatcherTimer _gitAutoRefreshTimer;

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

            Task SaveUiLayoutSettingsAsync() =>
                MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(AppWindow, _settingsService, EditorWorkspace, _shellPanelLayoutService!);

            Task SaveSidebarVisibilitySettingsAsync() =>
                MainWindowLayoutOperations.SaveSidebarVisibilitySettingsAsync(_settingsService, _shellPanelLayoutService);

            void ApplyLeftSidebarVisibility(bool show) =>
                _shellPaneController!.ApplyLeftSidebarVisibility(show);

            void ApplyPreviewVisibility(bool show) =>
                MainWindowLayoutOperations.ApplyPreviewVisibility(show, _shellPaneController!, _startupInitializationComplete, _livePreviewController!);

            void ApplySavedPanelWidths(EditorSettings settings) =>
                _shellPanelLayoutService.ApplySavedPanelWidths(settings.LeftSidebarWidth, settings.RightSidebarWidth);

            Task ToggleLeftPanelAsync() =>
                _shellPaneController!.ToggleLeftPanelAsync();

            Task ToggleRightPanelAsync() =>
                _shellPaneController!.ToggleRightPanelAsync();

            void TogglePreviewWidth() =>
                _shellPanelLayoutService.TogglePreviewWidth();

            void LoadDirectoryRoot(string folderPath) =>
                _explorerNavigationController!.LoadDirectoryRoot(folderPath);

            Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true) =>
                _explorerNavigationController!.NavigateToFolderAsync(folderPath, revealInLeftPanel);

            Task NavigateExplorerToFolderAndRevealAsync(string folderPath) =>
                _explorerNavigationController!.NavigateToFolderAsync(folderPath, revealInLeftPanel: true);

            Task RefreshGitStatusUIAsync() =>
                MainWindowWorkspaceOperations.RefreshGitStatusUiAsync(
                    _state,
                    _gitService,
                    _gitAutoRefreshTimer!,
                    _tabNavigationController!,
                    _gitStatusRefreshController!,
                    _explorerNavigationController!,
                    path => CurrentRepoPath = path);

            void QueueGitStatusRefresh() =>
                _gitStatusRefreshController!.QueueRefresh();

            string GetCurrentRepoPathForGitRefresh() =>
                MainWindowWorkspaceOperations.GetCurrentRepoPathForGitRefresh(
                    _state,
                    _gitService,
                    _tabNavigationController!,
                    path => CurrentRepoPath = path);

            string GetSearchRoot() =>
                MainWindowWorkspaceOperations.GetSearchRoot(_state);

            long GetLargeFileThresholdBytes() =>
                MainWindowWorkspaceOperations.GetLargeFileThresholdBytes(_settingsService);

            bool QueuePendingSplitImeLineSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text) =>
                _splitImeSyncController!.QueuePendingLineSyncIfNeeded(sourceTab, lineNumber, text);

            bool SchedulePendingSplitImeCompletionSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text) =>
                _splitImeSyncController!.ScheduleCompletionSyncIfNeeded(sourceTab, lineNumber, text);

            bool ScheduleDeferredPendingSplitImeSyncIfNeeded(OpenedTab sourceTab) =>
                _splitImeSyncController!.ScheduleDeferredSyncIfNeeded(sourceTab);

            Task FlushPendingSplitImeSyncAsync(OpenedTab sourceTab) =>
                _splitImeSyncController!.FlushAsync(sourceTab);

            void ClearPendingSplitImeSync(string tabId) =>
                _splitImeSyncController!.Clear(tabId);

            Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing) =>
                _splitImeSyncController!.SyncLineChangeToOtherTabsAsync(sourceTab, lineNumber, text, isComposing);

            Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true) =>
                _splitImeSyncController!.SyncEditsToOtherTabsAsync(sourceTab, updateUi);

            void ApplyUiPersonalization(EditorSettings settings) =>
                _settingsController!.ApplyUiPersonalization(settings);

            void ApplyToolbarSettings(EditorSettings settings) =>
                _settingsController!.ApplyToolbarSettings(settings);

            void ApplyEditorSurfaceBackground(EditorSettings settings) =>
                _settingsController!.ApplyEditorSurfaceBackground(settings);

            var shellControllers = MainWindowShellComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                tabId => _state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowShellCompositionCallbacks(
                    SaveUiLayoutSettingsAsync,
                    () => _toolbarCommandController?.ToggleTerminal(),
                    GetCurrentElementTheme,
                    GetLocalizedString,
                    UpdateWindowTitle,
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    ReloadTabWithEncodingAsync,
                    MarkTabDirtyFromStatusBar,
                    PerformLineNavigationAsync));
            _shellPanelLayoutService = shellControllers.ShellPanelLayout;
            _tabNavigationController = shellControllers.TabNavigation;
            _terminalShortcutService = shellControllers.TerminalShortcut;
            _dialogController = shellControllers.Dialog;
            _windowTitleController = shellControllers.WindowTitle;
            _tabEncryptionController = shellControllers.TabEncryption;
            _stickyNoteModeController = shellControllers.StickyNoteMode;
            _statusBarController = shellControllers.StatusBar;
            var previewControllers = MainWindowPreviewComposition.Compose(
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _tabNavigationController,
                _stickyNoteModeController,
                _dialogController,
                tabId => _state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowPreviewCompositionCallbacks(
                    () => _toolbarCommandController?.Find(),
                    () => _toolbarCommandController?.ToggleLivePreview(),
                    () => _toolbarCommandController?.ToggleTheme(),
                    ToggleMaximize,
                    () => _toolbarCommandController?.Print(),
                    TogglePreviewWidth,
                    LoadFileIntoTabAsync,
                    NormalizeWebMessageJson,
                    () => _state.CurrentFolderPath,
                    () => _state.CurrentRepoPath,
                    () => _state.ScrollSyncEnabled,
                    UpdateRightPanelSelectionContext,
                    NavigateExplorerToFolderAndRevealAsync,
                    GetLocalizedString));
            _webViewShortcutController = previewControllers.WebViewShortcut;
            _previewScrollSyncController = previewControllers.PreviewScrollSync;
            _compareTabController = previewControllers.CompareTab;
            _livePreviewController = previewControllers.LivePreview;
            _editorWebViewInitializationController = previewControllers.EditorWebViewInitialization;
            _editorLineNavigationController = previewControllers.EditorLineNavigation;
            _pdfViewerController = previewControllers.PdfViewer;
            _officeDocumentViewerController = previewControllers.OfficeDocumentViewer;
            _editorLinkNavigationController = previewControllers.EditorLinkNavigation;
            var editorFoundationControllers = MainWindowEditorFoundationComposition.Compose(
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _state.EditorSessions,
                _tabNavigationController,
                _tabEncryptionController,
                _stickyNoteModeController,
                _statusBarController,
                _dialogController,
                _terminalShortcutService,
                _editorLineNavigationController,
                InitialEditorLineWarmupCount,
                tabId => _state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowEditorFoundationCallbacks(
                    () => _toolbarCommandController?.ToggleLivePreview(),
                    () => _toolbarCommandController?.ToggleTheme(),
                    ToggleMaximize,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    TogglePreviewWidth,
                    () => OpenNewTab(),
                    () => _toolbarCommandController?.SaveActive(),
                    () => _toolbarCommandController?.OpenFile(),
                    () => OnCloseActiveTabShortcutInvoked(null!, null!),
                    () => _toolbarCommandController?.Print(),
                    FocusSearchPanel,
                    UpdateLivePreview,
                    UpdateLanguageUI,
                    SchedulePreview,
                    UpdateWindowTitle,
                    tab => SyncEditsToOtherTabsAsync(tab),
                    LoadFileIntoTabAsync,
                    GetSearchRoot,
                    GetLargeFileThresholdBytes,
                    RefreshGitStatusUIAsync,
                    GetLocalizedString));
            _tabReloadController = editorFoundationControllers.TabReload;
            _tabDirtyStateController = editorFoundationControllers.TabDirtyState;
            _activeEditorInsertionController = editorFoundationControllers.ActiveEditorInsertion;
            _tabTextContextProvider = editorFoundationControllers.TabTextContext;
            _editorBridgeShortcutController = editorFoundationControllers.EditorBridgeShortcut;
            _searchReplaceTabSyncController = editorFoundationControllers.SearchReplaceTabSync;
            _searchReplaceController = editorFoundationControllers.SearchReplace;
            _splitImeSyncController = editorFoundationControllers.SplitImeSync;
            var workspaceControllers = MainWindowWorkspaceComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _tabEncryptionController,
                _compareTabController,
                _dialogController,
                new MainWindowWorkspaceCompositionCallbacks(
                    _stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => _toolbarCommandController?.ToggleTheme(),
                    _stickyNoteModeController.ToggleMode,
                    GetCurrentRepoPathForGitRefresh,
                    () => _state.CurrentFolderPath,
                    GetLocalizedString,
                    () => _explorerNavigationController,
                    path => CurrentRepoPath = path,
                    path => CurrentFolderPath = path,
                    RefreshGitStatusUIAsync,
                    EnsureLeftPanelVisible,
                    ShowLeftSidebarPage,
                    LoadFileIntoTabAsync,
                    InitializePickerWindow,
                    NavigateExplorerToFolderAndRevealAsync,
                    request => OpenNewTab(
                        request.FilePath,
                        request.Content,
                        request.IsReadOnly,
                        request.EncodingName,
                        request.EncodingWasAutoDetected,
                        request.TextModel,
                        request.IsEncrypted,
                        request.EncryptionPassword),
                    OpenImageTab,
                    OpenPdfTab,
                    OpenOfficeDocumentTab,
                    QueueGitStatusRefresh));
            _functionKeyShortcutService = workspaceControllers.FunctionKeyShortcut;
            _gitAutoRefreshTimer = workspaceControllers.GitAutoRefreshTimer;
            _gitPanelController = workspaceControllers.GitPanel;
            _gitPanelController.FileRestored += OnGitFileRestored;
            _gitStatusRefreshController = workspaceControllers.GitStatusRefresh;
            _fileTabLoadController = workspaceControllers.FileTabLoad;
            _explorerNavigationController = workspaceControllers.ExplorerNavigation;
            _favoritesRecentController = workspaceControllers.FavoritesRecent;
            var documentCommandControllers = MainWindowDocumentCommandComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _state.EditorSessions,
                _statusBarController,
                _tabNavigationController,
                _livePreviewController,
                _tabDirtyStateController,
                _tabEncryptionController,
                _favoritesRecentController,
                _dialogController,
                new MainWindowDocumentCommandCallbacks(
                    FlushPendingSplitImeSyncAsync,
                    UpdateLanguageUI,
                    RefreshGitStatusUIAsync,
                    UpdateWindowTitle,
                    () => _state.CurrentFolderPath,
                    LoadDirectoryRoot,
                    GetSearchRoot,
                    () => _state.CurrentRepoPath,
                    ClearPendingSplitImeSync,
                    () => OpenNewTab(),
                    CloseReadOnlyViewer,
                    SaveUiLayoutSettingsAsync,
                    GetCurrentElementTheme,
                    GetLocalizedString));
            _tabSaveController = documentCommandControllers.TabSave;
            _autoSaveController = documentCommandControllers.AutoSave;
            _tabCloseController = documentCommandControllers.TabClose;
            _tabMoveController = documentCommandControllers.TabMove;
            _windowCloseController = documentCommandControllers.WindowClose;
            var interactionControllers = MainWindowInteractionComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _shellPanelLayoutService,
                _terminalShortcutService,
                _tabNavigationController,
                _tabEncryptionController,
                _activeEditorInsertionController,
                _favoritesRecentController,
                _dialogController,
                _pdfViewerController,
                _officeDocumentViewerController,
                new MainWindowInteractionCallbacks(
                    () => _state.CurrentFolderPath,
                    () => _state.CurrentRepoPath,
                    () => OpenNewTab(),
                    LoadDirectoryRoot,
                    LoadFileIntoTabAsync,
                    async (filePath, line) => await LoadFileIntoTabAsync(filePath, line),
                    NavigateExplorerToFolderAsync,
                    NavigateExplorerToFolderAndRevealAsync,
                    () => FileListView.SelectedItem as ExplorerItem,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    FocusSearchPanel,
                    () => OnCloseActiveTabShortcutInvoked(null!, null!),
                    () => _toolbarCommandController?.SaveActive(),
                    () => _toolbarCommandController?.SaveActiveAs(),
                    () => _toolbarCommandController?.OpenFile(),
                    () => _toolbarCommandController?.Find(),
                    () => _toolbarCommandController?.Print(),
                    () => _pdfViewerController.IsActiveViewer() || _officeDocumentViewerController.IsActiveViewer(),
                    _stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => _toolbarCommandController?.ToggleTheme(),
                    _stickyNoteModeController.ToggleMode,
                    () => _toolbarCommandController?.ToggleLivePreview(),
                    TogglePreviewWidth,
                    ToggleMaximize,
                    ShowLeftSidebarPage,
                    CloseTabAndCleanup,
                    SyncSnippetsToOpenEditorsAsync,
                    InitializePickerWindow,
                    GetLocalizedString,
                    GetCurrentElementTheme,
                    (tab, tabItem) => MainWindowTabOperations.ReloadAsync(
                        tab,
                        tabItem,
                        _statusBarController,
                        _pdfViewerController,
                        _officeDocumentViewerController,
                        _tabReloadController,
                        UpdateLanguageUI,
                        UpdateWindowTitle),
                    (_, tabItem, tabView) => _tabCloseController.CloseRightTabs(tabItem, tabView),
                    (_, tabItem, tabView) => _tabCloseController.CloseLeftTabs(tabItem, tabView),
                    (_, tabItem, tabView) => _tabCloseController.CloseOtherTabs(tabItem, tabView)));
            _explorerFileActionsController = interactionControllers.ExplorerFileActions;
            _tabContextMenuController = interactionControllers.TabContextMenu;
            _fileOpenDropController = interactionControllers.FileOpenDrop;
            _rootKeyboardShortcutController = interactionControllers.RootKeyboardShortcut;
            _terminalPanelController = interactionControllers.TerminalPanel;
            _snippetsController = interactionControllers.Snippets;
            var agentControllers = MainWindowAgentComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _state.EditorSessions,
                _tabNavigationController,
                _tabDirtyStateController,
                _tabCloseController,
                _searchReplaceTabSyncController,
                _compareTabController,
                _activeEditorInsertionController,
                _tabTextContextProvider,
                _dialogController,
                new MainWindowAgentCompositionCallbacks(
                    () => FileListView.SelectedItem as ExplorerItem,
                    () => _state.CurrentFolderPath,
                    () => _state.CurrentRepoPath,
                    LoadDirectoryRoot,
                    QueueGitStatusRefresh,
                    GetAgentSessionEdits,
                    LoadFileIntoTabForAgentAsync,
                    NavigateExplorerToFolderAndRevealAsync,
                    content => OpenNewTab(null, content),
                    SaveTabAsync,
                    InitializePickerWindow,
                    () => _explorerNavigationController.RefreshCurrentFolder(),
                    GetLocalizedString,
                    UpdateWindowTitle));
            _llmAssistantController = agentControllers.LlmAssistant;
            _agentFileWorkflowController = agentControllers.AgentFileWorkflow;
            _agentController = agentControllers.Agent;
            var editorRuntimeControllers = MainWindowEditorRuntimeComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _state.EditorSessions,
                _statusBarController,
                _tabNavigationController,
                _tabDirtyStateController,
                _tabEncryptionController,
                _livePreviewController,
                _pdfViewerController,
                _officeDocumentViewerController,
                _editorWebViewInitializationController,
                _editorLineNavigationController,
                _editorBridgeShortcutController,
                _editorLinkNavigationController,
                _activeEditorInsertionController,
                _tabContextMenuController,
                _favoritesRecentController,
                _llmAssistantController,
                _agentController,
                _dialogController,
                _shellPanelLayoutService,
                InitialEditorLineWarmupCount,
                new MainWindowEditorRuntimeCallbacks(
                    SchedulePreview,
                    UpdateLanguageUI,
                    QueuePendingSplitImeLineSyncIfNeeded,
                    SchedulePendingSplitImeCompletionSyncIfNeeded,
                    ScheduleDeferredPendingSplitImeSyncIfNeeded,
                    SyncLineChangeToOtherTabsAsync,
                    tab => SyncEditsToOtherTabsAsync(tab),
                    SaveSidebarVisibilitySettingsAsync,
                    RefreshActivePreview,
                    LoadFileIntoTabAsync,
                    UpdateRightPanelSelectionContext,
                    () => _state.ScrollSyncEnabled,
                    async enabled =>
                    {
                        _state.ScrollSyncEnabled = enabled;
                        var settings = _settingsService.CurrentSettings;
                        if (settings.ScrollSyncEnabled != enabled)
                        {
                            settings.ScrollSyncEnabled = enabled;
                            await _settingsService.SaveSettingsAsync(settings);
                        }
                    },
                    () => _state.CurrentFolderPath,
                    () => _toolbarCommandController?.LivePreviewEnabled == true,
                    () => _toolbarCommandController?.CsvTableModeEnabled == true,
                    GetPreviewBaseHref,
                    GetLocalizedString,
                    ApplyEditorSurfaceBackground,
                    UpdateWindowTitle,
                    () => OpenNewTab(),
                    (filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted, encryptionPassword) =>
                        OpenNewTab(filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted: isEncrypted, encryptionPassword: encryptionPassword),
                    CloseTabAndCleanup,
                    (_, args) => _tabCloseController.CloseRequested(args)));
            _tocController = editorRuntimeControllers.Toc;
            _editorBridgeDocumentController = editorRuntimeControllers.EditorBridgeDocument;
            _shellPaneController = editorRuntimeControllers.ShellPane;
            _markdownToolbarController = editorRuntimeControllers.MarkdownToolbar;
            _tabSelectionController = editorRuntimeControllers.TabSelection;
            _editorBridgeInteractionController = editorRuntimeControllers.EditorBridgeInteraction;
            _editorTabOpenController = editorRuntimeControllers.EditorTabOpen;
            _editorSplitLayoutController = editorRuntimeControllers.EditorSplitLayout;
            var startupControllers = MainWindowStartupComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state.TabBridges,
                _state.EditorSessions,
                _terminalShortcutService,
                _functionKeyShortcutService,
                _autoSaveController,
                _gitAutoRefreshTimer,
                _splitImeSyncController,
                _livePreviewController,
                _pdfViewerController,
                _officeDocumentViewerController,
                _statusBarController,
                _llmAssistantController,
                _agentController,
                _tabNavigationController,
                _snippetsController,
                _favoritesRecentController,
                _fileOpenDropController,
                _shellPanelLayoutService,
                _rootKeyboardShortcutController,
                _tabSaveController,
                _terminalPanelController,
                _stickyNoteModeController,
                _shellPaneController,
                _compareTabController,
                _dialogController,
                new MainWindowStartupCallbacks(
                    () => _state.CurrentRepoPath,
                    () => _state.CurrentFolderPath,
                    NavigateExplorerToFolderAsync,
                    LoadFileIntoTabAsync,
                    () => OpenNewTab(),
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    ApplySavedPanelWidths,
                    ApplyUiPersonalization,
                    LocalizeUi,
                    ApplyToolbarSettings,
                    SyncAgentSettingsAfterLoad,
                    RefreshGitStatusUIAsync,
                    UpdateAutoSaveStatus,
                    GetLocalizedString,
                    GetCurrentElementTheme,
                    InitializePickerWindow,
                    GetPreviewBaseHref));
            _lifecycleController = startupControllers.Lifecycle;
            _settingsController = startupControllers.Settings;
            _startupController = startupControllers.Startup;
            _shellInteractionController = startupControllers.ShellInteraction;
            _toolbarCommandController = startupControllers.ToolbarCommand;
            MainWindowEventBinder.Bind(
                ui,
                _searchReplaceController,
                _tabMoveController,
                _tabCloseController,
                _toolbarCommandController,
                () => OpenNewTab(),
                SaveUiLayoutSettingsAsync);

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

        private static string NormalizeWebMessageJson(CoreWebView2WebMessageReceivedEventArgs args)
        {
            string json = args.WebMessageAsJson;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return doc.RootElement.GetString() ?? "{}";
                }
            }
            catch
            {
                string? asString = args.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(asString))
                {
                    return asString;
                }
            }

            return json;
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
