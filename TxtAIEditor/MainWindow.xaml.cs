using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly ISettingsDialogService _settingsDialogService;
        private readonly IUiPersonalizationService _uiPersonalizationService;
        private readonly ILocalizationService _localizationService;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly TerminalShortcutService _terminalShortcutService;
        private readonly FunctionKeyShortcutService _functionKeyShortcutService;
        private readonly CompareSelectionDialogService _compareSelectionDialogService;
        private readonly SearchReplaceController _searchReplaceController;
        private readonly SearchReplaceTabSyncController _searchReplaceTabSyncController;
        private readonly GitPanelController _gitPanelController;
        private readonly GitStatusRefreshController _gitStatusRefreshController;
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly ExplorerFileActionsController _explorerFileActionsController;
        private readonly TabContextMenuController _tabContextMenuController;
        private readonly TabNavigationController _tabNavigationController;
        private readonly TabEncryptionController _tabEncryptionController;
        private readonly EditorTabViewItemFactory _editorTabViewItemFactory;
        private readonly EditorTabDocumentFactory _editorTabDocumentFactory;
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
        private bool _startupInitializationComplete;
        private string _currentFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private string _currentRepoPath = string.Empty;

        private string CurrentFolderPath
        {
            get => _currentFolderPath;
            set
            {
                if (_currentFolderPath != value)
                {
                    _currentFolderPath = value;
                    _searchReplaceController?.CancelActiveSearch();
                    UpdateAutoSaveStatus();
                    UpdateAllTabWorkspaceIndicators();
                }
            }
        }

        private void UpdateAllTabWorkspaceIndicators()
        {
            var folderPath = _currentFolderPath;
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
            get => _currentRepoPath;
            set
            {
                if (_currentRepoPath != value)
                {
                    _currentRepoPath = value;
                    UpdateAutoSaveStatus();
                }
            }
        }

        private void UpdateAutoSaveStatus()
        {
            _autoSaveController?.UpdateStatus();
        }

        private bool _scrollSyncEnabled = true;
        public bool ScrollSyncEnabled
        {
            get => _scrollSyncEnabled;
            set => _scrollSyncEnabled = value;
        }
        
        // Dynamic tabs collection
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges = 
            new Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)>();
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions =
            new Dictionary<string, EditorDocumentSession>();

        private const int InitialEditorLineWarmupCount = 120;

        private readonly DispatcherTimer _gitAutoRefreshTimer;

        private ToggleButton LeftPanelToggle => StatusBarPane.LeftPanelToggleButton;
        private ToggleButton RightPanelToggle => StatusBarPane.RightPanelToggleButton;
        private TextBlock StatusGitBranch => StatusBarPane.GitBranchText;
        private ListView FileListView => LeftSidebarTabView.FileList;
        private ListView SearchResultsList => LeftSidebarTabView.SearchResults;
        private TextBox SearchQueryInput => LeftSidebarTabView.SearchQuery;
        private TextBox ReplaceQueryInput => LeftSidebarTabView.ReplaceQuery;
        private ToggleButton SearchMatchCaseToggle => LeftSidebarTabView.SearchMatchCase;
        private ToggleButton SearchWholeWordToggle => LeftSidebarTabView.SearchWholeWord;
        private ToggleButton SearchRegexToggle => LeftSidebarTabView.SearchRegex;
        private ComboBox PreviewModeCombo => PreviewGrid.PreviewMode;
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
            _languageDetectionService = services.LanguageDetectionService;
            _settingsDialogService = services.SettingsDialogService;
            _uiPersonalizationService = services.UiPersonalizationService;
            _editorTabViewItemFactory = new EditorTabViewItemFactory(_localizationService);
            _editorTabDocumentFactory = new EditorTabDocumentFactory(_languageDetectionService, GetLocalizedString);
            _compareSelectionDialogService = services.CompareSelectionDialogService;
            var shellControllers = MainWindowShellComposition.Compose(
                this,
                ui,
                services,
                _viewModel,
                _tabBridges,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
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
                _tabBridges,
                _tabNavigationController,
                _stickyNoteModeController,
                _dialogController,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowPreviewCompositionCallbacks(
                    () => _toolbarCommandController?.Find(),
                    () => _toolbarCommandController?.ToggleLivePreview(),
                    () => _toolbarCommandController?.ToggleTheme(),
                    ToggleMaximize,
                    () => _toolbarCommandController?.Print(),
                    TogglePreviewWidth,
                    LoadFileIntoTabAsync,
                    NormalizeWebMessageJson,
                    () => _currentFolderPath,
                    () => _currentRepoPath,
                    () => _scrollSyncEnabled,
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
                _tabBridges,
                _editorSessions,
                _tabNavigationController,
                _tabEncryptionController,
                _stickyNoteModeController,
                _statusBarController,
                _dialogController,
                _terminalShortcutService,
                _editorLineNavigationController,
                InitialEditorLineWarmupCount,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
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
                _tabBridges,
                _tabEncryptionController,
                _compareTabController,
                _dialogController,
                new MainWindowWorkspaceCompositionCallbacks(
                    _stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => _toolbarCommandController?.ToggleTheme(),
                    _stickyNoteModeController.ToggleMode,
                    GetCurrentRepoPathForGitRefresh,
                    () => _currentFolderPath,
                    GetLocalizedString,
                    IsGitNotDetectedText,
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
                _tabBridges,
                _editorSessions,
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
                    () => _currentFolderPath,
                    LoadDirectoryRoot,
                    GetSearchRoot,
                    () => _currentRepoPath,
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
                    () => _currentFolderPath,
                    () => _currentRepoPath,
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
                    OnTabReloadAsync,
                    OnCloseRightTabs,
                    OnCloseLeftTabs,
                    OnCloseOtherTabs));
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
                _tabBridges,
                _editorSessions,
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
                    () => _currentFolderPath,
                    () => _currentRepoPath,
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
            _tocController = new TocController(
                _viewModel,
                LeftSidebarTabView,
                _tabNavigationController.GetActiveTab,
                tab => _editorSessions.TryGetValue(tab.Id, out var s) ? s : null,
                () => PreviewModeCombo.SelectedIndex == 3,
                async targetLine =>
                {
                    var activeTab = _tabNavigationController.GetActiveTab();
                    if (activeTab != null)
                    {
                        await _editorLineNavigationController.RevealTabLineAsync(activeTab.Id, targetLine);
                    }
                },
                async targetPage =>
                {
                    var activeTab = _tabNavigationController.GetActiveTab();
                    if (activeTab != null)
                    {
                        await _pdfViewerController.NavigateToPageAsync(activeTab, targetPage);
                    }
                });
            _editorBridgeDocumentController = new EditorBridgeDocumentController(
                _tabDirtyStateController,
                _statusBarController,
                _tocController,
                SchedulePreview,
                UpdateLanguageUI,
                QueuePendingSplitImeLineSyncIfNeeded,
                SchedulePendingSplitImeCompletionSyncIfNeeded,
                tab => ScheduleDeferredPendingSplitImeSyncIfNeeded(tab),
                SyncLineChangeToOtherTabsAsync,
                tab => SyncEditsToOtherTabsAsync(tab));
            _shellPaneController = new ShellPaneController(
                LeftSidebarTabView,
                StatusBarPane,
                _shellPanelLayoutService,
                SearchQueryInput,
                SaveSidebarVisibilitySettingsAsync,
                () => _favoritesRecentController.RefreshFavorites(true),
                () => _tocController.RefreshToc(_tabNavigationController.GetActiveTab()),
                RefreshActivePreview);
            _markdownToolbarController = new MarkdownToolbarController(
                TopToolbar,
                MarkdownToolbar,
                EditorTabView,
                _tabBridges,
                LoadFileIntoTabAsync,
                _activeEditorInsertionController.InsertTextAsync,
                _dialogController.ShowErrorMessage,
                GetLocalizedString);
            _tabSelectionController = new TabSelectionController(
                EditorWorkspace,
                _viewModel,
                EditorTabView,
                _tabBridges,
                DispatcherQueue,
                _llmAssistantController,
                _agentController,
                SelectionStatsText,
                _statusBarController,
                GetLocalizedString,
                UpdateLivePreview,
                UpdateLanguageUI,
                _tocController,
                UpdateWindowTitle);
            _editorBridgeInteractionController = new EditorBridgeInteractionController(
                EditorWorkspace,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                DispatcherQueue,
                _tabNavigationController.GetActiveTab,
                _statusBarController,
                _tabSelectionController,
                _livePreviewController,
                UpdateRightPanelSelectionContext,
                () => _scrollSyncEnabled,
                async enabled =>
                {
                    _scrollSyncEnabled = enabled;
                    var settings = _settingsService.CurrentSettings;
                    if (settings.ScrollSyncEnabled != enabled)
                    {
                        settings.ScrollSyncEnabled = enabled;
                        await _settingsService.SaveSettingsAsync(settings);
                    }
                });
            _editorTabOpenController = new EditorTabOpenController(
                _settingsService,
                _snippetService,
                _viewModel,
                EditorWorkspace,
                _editorTabDocumentFactory,
                _editorTabViewItemFactory,
                _favoritesRecentController,
                _statusBarController,
                _tabEncryptionController,
                _pdfViewerController,
                _officeDocumentViewerController,
                _editorWebViewInitializationController,
                _editorBridgeShortcutController,
                _editorBridgeDocumentController,
                _editorBridgeInteractionController,
                _editorLinkNavigationController,
                _tabSelectionController,
                _tabBridges,
                _editorSessions,
                DispatcherQueue,
                _tabNavigationController.GetCurrentActiveTabView,
                _tabNavigationController.GetActiveTab,
                _tabNavigationController.GetTabViewForItem,
                () => _currentFolderPath,
                () => _toolbarCommandController?.LivePreviewEnabled == true,
                () => _scrollSyncEnabled,
                () => _toolbarCommandController?.CsvTableModeEnabled == true,
                GetPreviewBaseHref,
                GetLocalizedString,
                ApplyEditorSurfaceBackground,
                UpdateLanguageUI,
                UpdateWindowTitle,
                _tabContextMenuController.ShowContextMenu,
                InitialEditorLineWarmupCount);
            _editorSplitLayoutController = new EditorSplitLayoutController(
                TopToolbar,
                EditorWorkspace,
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                _editorSessions,
                _tabNavigationController.GetActiveTab,
                () => OpenNewTab(),
                (filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted, encryptionPassword) =>
                    OpenNewTab(filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted: isEncrypted, encryptionPassword: encryptionPassword),
                _tabDirtyStateController.IsAnySameFileTabDirty,
                _tabDirtyStateController.SetDirtyStateForFileGroup,
                tab => SyncEditsToOtherTabsAsync(tab),
                CloseTabAndCleanup,
                OnEditorTabViewTabCloseRequested,
                _tabSelectionController.QueueChanged,
                _tabSelectionController.ClearQueue,
                UpdateWindowTitle);
            _lifecycleController = new MainWindowLifecycleController(
                this,
                AppTitleBar,
                _terminalShortcutService,
                _functionKeyShortcutService,
                _autoSaveController,
                _gitAutoRefreshTimer,
                _splitImeSyncController,
                EditorWorkspace,
                _tabBridges,
                _livePreviewController);
            _settingsController = new MainWindowSettingsController(
                AppWindow,
                () => Content as FrameworkElement,
                () => Content.XamlRoot,
                GetCurrentElementTheme,
                _settingsService,
                _settingsDialogService,
                _uiPersonalizationService,
                _localizationService,
                TopToolbar,
                MarkdownToolbar,
                MarkdownToolbarHost,
                EditorWorkspace,
                LeftSidebarTabView,
                StatusBarPane,
                PreviewGrid,
                EditorWorkspace.StickyNoteBarControl,
                LeftSplitter,
                RightSplitter,
                _tabBridges,
                _pdfViewerController,
                _officeDocumentViewerController,
                _statusBarController,
                _livePreviewController,
                _llmAssistantController,
                _agentController,
                _tabNavigationController.GetActiveTab,
                () => _currentFolderPath,
                GetLocalizedString,
                IsGitNotDetectedText,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows(),
                ApplyPreviewVisibility,
                UpdateAutoSaveStatus,
                _lifecycleController.CleanupBeforeRestart,
                EditorWorkspace.RefreshSplitters,
                InitializePickerWindow);
            _startupController = new MainWindowStartupController(
                this,
                _settingsService,
                _viewModel,
                EditorWorkspace,
                TopToolbar,
                LeftPanelToggle,
                RightPanelToggle,
                MarkdownToolbar,
                PreviewModeCombo,
                _gitAutoRefreshTimer,
                _livePreviewController,
                _snippetsController,
                _favoritesRecentController,
                () => _currentRepoPath,
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
                _dialogController.ShowErrorMessage);
            _shellInteractionController = new MainWindowShellInteractionController(
                RootGrid,
                DragOverlay,
                LeftSplitter,
                RightSplitter,
                _fileOpenDropController,
                _shellPanelLayoutService,
                _rootKeyboardShortcutController);

            if (Content is FrameworkElement rootElement)
            {
                rootElement.DataContext = _viewModel;
            }

            // Bind Left Sidebar Tab items
            FileListView.ItemsSource = _viewModel.ExplorerItems;
            var groupedSource = new Microsoft.UI.Xaml.Data.CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = _viewModel.SearchResultsGrouped
            };
            SearchResultsList.ItemsSource = groupedSource.View;
            _statusBarController.InitializeEncodings(TextEncodingService.SupportedEncodingNames, "UTF-8");
            _toolbarCommandController = new MainWindowToolbarCommandController(
                this,
                TopToolbar,
                EditorTabView,
                SearchQueryInput,
                _viewModel,
                _settingsService,
                _fileOpenDropController,
                _tabNavigationController,
                _tabSaveController,
                _terminalPanelController,
                _settingsController,
                _stickyNoteModeController,
                _pdfViewerController,
                _officeDocumentViewerController,
                _shellPaneController,
                _compareSelectionDialogService,
                _compareTabController,
                _dialogController,
                _tabBridges,
                _editorSessions,
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                GetLocalizedString,
                GetPreviewBaseHref,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows());
            WireLeftSidebarEvents();
            WireEditorWorkspaceEvents();

            // Load local configurations and boot initial states
            // Setup custom title bar
            _lifecycleController.InitializeTitleBar();

            this.Activated += OnWindowActivated;
            this.Activated += _lifecycleController.HandleActivationChanged;
            this.Closed += _lifecycleController.HandleWindowClosed;
            this.AppWindow.Closing += OnAppWindowClosing;
            _lifecycleController.StartShortcuts();

            PreviewGrid.ModelNameClick += (_, _) => _toolbarCommandController?.ShowModelSettings();
            PreviewGrid.AgentPane.ModelNameClick += (_, _) => _toolbarCommandController?.ShowModelSettings();
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

        private void WireLeftSidebarEvents()
        {
            LeftSidebarTabView.SearchQueryInputKeyDown += OnSearchQueryInputKeyDown;
            LeftSidebarTabView.SearchQuery.TextChanged += OnSearchQueryTextChanged;
            LeftSidebarTabView.SearchAllFilesClick += OnSearchAllFilesClick;
            LeftSidebarTabView.ReplaceAllClick += OnReplaceAllClick;
            LeftSidebarTabView.ReplaceOneClick += OnReplaceOneClick;
            LeftSidebarTabView.SearchResultItemClick += OnSearchResultItemClick;
        }

        private void SyncAgentSettingsAfterLoad()
        {
            _agentController.UpdateModelDisplay(true);
            _agentController.UpdateContextStats();
        }


        private void WireEditorWorkspaceEvents()
        {
            EditorWorkspace.PrimaryAddTabButtonClick += OnEditorTabViewAddTabClick;
            EditorWorkspace.PrimaryTabCloseRequested += OnEditorTabViewTabCloseRequested;
            EditorWorkspace.MoveTabLeftClick += OnMoveTabLeftClick;
            EditorWorkspace.MoveTabRightClick += OnMoveTabRightClick;
            EditorWorkspace.TerminalPanelHeightChanged += async (_, _) => await SaveUiLayoutSettingsAsync();
        }

        private async Task SaveUiLayoutSettingsAsync()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                WindowPlacementService.CaptureRestoredWindowPlacement(AppWindow, settings);
                settings.TerminalPanelHeight = EditorWorkspace.PersistedTerminalPanelHeight;
                settings.LeftSidebarVisible = _shellPanelLayoutService.IsLeftSidebarVisible;
                settings.RightSidebarVisible = _shellPanelLayoutService.IsRightSidebarVisible;
                settings.LeftSidebarWidth = _shellPanelLayoutService.LeftSidebarWidth;
                settings.RightSidebarWidth = _shellPanelLayoutService.RightSidebarWidth;

                await _settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UI layout settings: {ex.Message}");
            }
        }

        private async Task SaveSidebarVisibilitySettingsAsync()
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                settings.LeftSidebarVisible = _shellPanelLayoutService.IsLeftSidebarVisible;
                settings.RightSidebarVisible = _shellPanelLayoutService.IsRightSidebarVisible;
                settings.LeftSidebarWidth = _shellPanelLayoutService.LeftSidebarWidth;
                settings.RightSidebarWidth = _shellPanelLayoutService.RightSidebarWidth;
                await _settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save sidebar visibility settings: {ex.Message}");
            }
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

        private void ApplyLeftSidebarVisibility(bool show)
        {
            _shellPaneController.ApplyLeftSidebarVisibility(show);
        }

        private void ApplySavedPanelWidths(EditorSettings settings)
        {
            _shellPanelLayoutService.ApplySavedPanelWidths(settings.LeftSidebarWidth, settings.RightSidebarWidth);
        }

        private async Task ToggleLeftPanelAsync()
        {
            await _shellPaneController.ToggleLeftPanelAsync();
        }

        private async Task ToggleRightPanelAsync()
        {
            await _shellPaneController.ToggleRightPanelAsync();
        }


        private void TogglePreviewWidth()
        {
            _shellPanelLayoutService.TogglePreviewWidth();
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

        private static bool IsGitNotDetectedText(string text)
        {
            return text.Equals("Git: 감지 안됨", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Git: Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Git: 検出されていません", StringComparison.OrdinalIgnoreCase);
        }


        private async Task SyncSnippetsToOpenEditorsAsync()
        {
            var snippets = _snippetService.GetSnippets();
            var autocompleteWords = _snippetService.GetAutocompleteWords();
            foreach (var grp in _tabBridges.Values)
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

        private void LoadDirectoryRoot(string folderPath)
        {
            _explorerNavigationController.LoadDirectoryRoot(folderPath);
        }

        private async Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true)
        {
            await _explorerNavigationController.NavigateToFolderAsync(folderPath, revealInLeftPanel);
        }

        private Task NavigateExplorerToFolderAndRevealAsync(string folderPath)
        {
            return NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel: true);
        }

        internal async Task OpenShellPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string cleanedPath = path.Trim().Trim('"', '\'');
            if (File.Exists(cleanedPath))
            {
                string? folderPath = Path.GetDirectoryName(cleanedPath);
                if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                {
                    await NavigateExplorerToFolderAsync(
                        folderPath,
                        revealInLeftPanel: _shellPanelLayoutService.IsLeftSidebarVisible);
                }
                await LoadFileIntoTabAsync(cleanedPath);
            }
            else if (Directory.Exists(cleanedPath))
            {
                await NavigateExplorerToFolderAsync(
                    cleanedPath,
                    revealInLeftPanel: _shellPanelLayoutService.IsLeftSidebarVisible);
            }
        }


        #endregion

        #region Terminal Panel Layout


        private IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return _agentController.SessionEdits;
        }

        #endregion

        #region Split Editor Layout

        private void OnMoveTabLeftClick(object sender, RoutedEventArgs e)
        {
            _tabMoveController.MoveLeft();
        }

        private void OnMoveTabRightClick(object sender, RoutedEventArgs e)
        {
            _tabMoveController.MoveRight();
        }

        #endregion

        #region TabView Structural Interops

        private void OnEditorTabViewAddTabClick(TabView sender, object args)
        {
            OpenNewTab();
        }

        private void OnEditorTabViewTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            _tabCloseController.CloseRequested(args);
        }

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
                        (string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(_currentRepoPath) && t.FilePath.StartsWith(_currentRepoPath, StringComparison.OrdinalIgnoreCase))
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
                if (!string.IsNullOrEmpty(_currentFolderPath) && Directory.Exists(_currentFolderPath))
                {
                    LoadDirectoryRoot(_currentFolderPath);
                }
            });
        }

        private bool QueuePendingSplitImeLineSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text)
        {
            return _splitImeSyncController.QueuePendingLineSyncIfNeeded(sourceTab, lineNumber, text);
        }

        private bool SchedulePendingSplitImeCompletionSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text)
        {
            return _splitImeSyncController.ScheduleCompletionSyncIfNeeded(sourceTab, lineNumber, text);
        }

        private bool ScheduleDeferredPendingSplitImeSyncIfNeeded(OpenedTab sourceTab)
        {
            return _splitImeSyncController.ScheduleDeferredSyncIfNeeded(sourceTab);
        }

        private async Task FlushPendingSplitImeSyncAsync(OpenedTab sourceTab)
        {
            await _splitImeSyncController.FlushAsync(sourceTab);
        }

        private void ClearPendingSplitImeSync(string tabId)
        {
            _splitImeSyncController.Clear(tabId);
        }

        private async Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing)
        {
            await _splitImeSyncController.SyncLineChangeToOtherTabsAsync(sourceTab, lineNumber, text, isComposing);
        }

        private async Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true)
        {
            await _splitImeSyncController.SyncEditsToOtherTabsAsync(sourceTab, updateUi);
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

        #region UI Personalization Helper
        private void ApplyUiPersonalization(EditorSettings settings)
        {
            _settingsController.ApplyUiPersonalization(settings);
        }

        private void ApplyToolbarSettings(EditorSettings settings)
        {
            _settingsController.ApplyToolbarSettings(settings);
        }

        private void ApplyEditorSurfaceBackground(EditorSettings settings)
        {
            _settingsController.ApplyEditorSurfaceBackground(settings);
        }
        #endregion

        #region Advanced Git Handlers

        private async Task RefreshGitStatusUIAsync()
        {
            UpdateCurrentRepoPathFromWorkspace();
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                _gitAutoRefreshTimer.Start();
            }

            await _gitStatusRefreshController.RefreshAsync();
            await _explorerNavigationController.UpdateGitStatusesAsync();
        }

        private void QueueGitStatusRefresh()
        {
            _gitStatusRefreshController.QueueRefresh();
        }

        private string GetCurrentRepoPathForGitRefresh()
        {
            UpdateCurrentRepoPathFromWorkspace();
            return _currentRepoPath;
        }

        private void UpdateCurrentRepoPathFromWorkspace()
        {
            string searchPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                searchPath = _currentFolderPath;
            }
            else
            {
                string? activeFilePath = _tabNavigationController.GetActiveTab()?.FilePath;
                if (!string.IsNullOrWhiteSpace(activeFilePath))
                {
                    searchPath = File.Exists(activeFilePath)
                        ? Path.GetDirectoryName(activeFilePath) ?? string.Empty
                        : activeFilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(searchPath))
            {
                return;
            }

            CurrentRepoPath = _gitService.FindRepositoryRoot(searchPath) ?? string.Empty;
        }

        #endregion

        #region Advanced Search & Replace Handlers

        private async void OnSearchAllFilesClick(object sender, RoutedEventArgs e)
        {
            await _searchReplaceController.SearchAllFilesAsync();
        }

        private async void OnReplaceAllClick(object sender, RoutedEventArgs e)
        {
            await _searchReplaceController.ReplaceAllAsync();
        }

        private async void OnReplaceOneClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchResultItem item)
            {
                await _searchReplaceController.ReplaceOneAsync(item);
            }
        }

        private async void OnSearchResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResultItem item)
            {
                await _searchReplaceController.OpenSearchResultAsync(item);
            }
        }

        private string GetSearchRoot()
        {
            return _currentFolderPath ?? string.Empty;
        }

        private long GetLargeFileThresholdBytes()
        {
            return _settingsService.CurrentSettings.LargeFileThresholdMB * 1024L * 1024L;
        }

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

        private async void OnSearchQueryInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await _searchReplaceController.HandleSearchQueryEnterAsync();
            }
        }

        private void OnSearchQueryTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && string.IsNullOrWhiteSpace(textBox.Text))
            {
                _searchReplaceController.CancelActiveSearch();
            }
        }

        private void ApplyPreviewVisibility(bool show)
        {
            _shellPaneController.ApplyPreviewVisibility(show);
            if (show && _startupInitializationComplete)
            {
                _ = _livePreviewController.InitializeAsync();
            }
        }


        private async Task OnTabReloadAsync(OpenedTab tab, TabViewItem tabItem)
        {
            if (tab.IsImageViewer)
            {
                await EditorTabViewItemFactory.ReloadImageAsync(tabItem, tab.FilePath);
                _statusBarController.UpdateFileStats(tab);
                _statusBarController.UpdateTotalLines(tab);
                UpdateLanguageUI(tab);
                UpdateWindowTitle();
                return;
            }

            if (_pdfViewerController.Reload(tab))
            {
                _statusBarController.UpdateFileStats(tab);
                _statusBarController.UpdateTotalLines(tab);
                UpdateLanguageUI(tab);
                UpdateWindowTitle();
                return;
            }

            if (_officeDocumentViewerController.Reload(tab))
            {
                _statusBarController.UpdateFileStats(tab);
                _statusBarController.UpdateTotalLines(tab);
                UpdateLanguageUI(tab);
                UpdateWindowTitle();
                return;
            }

            await _tabReloadController.ReloadFromDiskAsync(tab);
        }

        private void OnCloseOtherTabs(OpenedTab tab, TabViewItem tabItem, TabView tabView)
        {
            _tabCloseController.CloseOtherTabs(tabItem, tabView);
        }

        private void OnCloseRightTabs(OpenedTab tab, TabViewItem tabItem, TabView tabView)
        {
            _tabCloseController.CloseRightTabs(tabItem, tabView);
        }

        private void OnCloseLeftTabs(OpenedTab tab, TabViewItem tabItem, TabView tabView)
        {
            _tabCloseController.CloseLeftTabs(tabItem, tabView);
        }


        private async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            await _editorLineNavigationController.RevealTabLineAsync(tabId, targetLine);
        }

        #endregion
    }

}
