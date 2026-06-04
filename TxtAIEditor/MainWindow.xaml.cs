using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Controls;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;


namespace TxtAIEditor
{
    public sealed partial class MainWindow : Window
    {
        private readonly IFileService _fileService;
        private readonly ISettingsService _settingsService;
        private readonly ICredentialService _credentialService;
        private readonly ILLMService _llmService;
        private readonly IGitService _gitService;
        private readonly ISnippetService _snippetService;
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly IRecentFilesService _recentFilesService;
        private readonly IFileSearchService _fileSearchService;
        private readonly IStickyNoteService _stickyNoteService;
        private readonly ISettingsDialogService _settingsDialogService;
        private readonly IUiPersonalizationService _uiPersonalizationService;
        private readonly ILocalizationService _localizationService;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly TerminalShortcutService _terminalShortcutService;
        private readonly FunctionKeyShortcutService _functionKeyShortcutService;
        private readonly ExplorerDirectoryService _explorerDirectoryService;
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly CompareSelectionDialogService _compareSelectionDialogService;
        private readonly SearchReplaceController _searchReplaceController;
        private readonly GitPanelController _gitPanelController;
        private readonly GitStatusRefreshController _gitStatusRefreshController;
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly ExplorerFileActionsController _explorerFileActionsController;
        private readonly TabContextMenuController _tabContextMenuController;
        private readonly TabEncryptionController _tabEncryptionController;
        private readonly EditorTabViewItemFactory _editorTabViewItemFactory;
        private readonly EditorTabDocumentFactory _editorTabDocumentFactory;
        private readonly FileOpenDropController _fileOpenDropController;
        private readonly RootKeyboardShortcutController _rootKeyboardShortcutController;
        private readonly SnippetsController _snippetsController;
        private readonly LlmAssistantController _llmAssistantController;
        private readonly AgentController _agentController;
        private readonly TocController _tocController;
        private readonly ShellPaneController _shellPaneController;
        private readonly MarkdownToolbarController _markdownToolbarController;
        private readonly StickyNoteModeController _stickyNoteModeController;
        private readonly StatusBarController _statusBarController;
        private readonly TabReloadController _tabReloadController;
        private readonly CompareTabController _compareTabController;
        private readonly LivePreviewController _livePreviewController;
        private readonly TabSelectionController _tabSelectionController;
        private readonly EditorSplitLayoutController _editorSplitLayoutController;
        private readonly SplitImeSyncController _splitImeSyncController;
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
        private readonly UnsavedChangesDialogService _unsavedChangesDialogService;
        private readonly WindowDialogController _dialogController;
        private readonly WindowCloseController _windowCloseController;
        private readonly MainWindowViewModel _viewModel = new MainWindowViewModel();
        private string _currentFolderPath = string.Empty;
        private string _currentRepoPath = string.Empty;

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
            _autoSaveController.UpdateStatus();
        }

        private bool _scrollSyncEnabled = true;
        private bool _csvTableModeEnabled = false;
        
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
        private TextBlock StatusLanguage => StatusBarPane.LanguageText;
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

        public MainWindow()
        {
            this.InitializeComponent();
            WindowPlacementService.SetWindowIcon(AppWindow);

            // Start pre-warming the shared WebView2 environment in the background
            _ = TxtAIEditor.Editor.MonacoBridge.GetSharedEnvironmentAsync();

            _fileService = new FileService();
            _settingsService = new SettingsService();
            _credentialService = new CredentialService();
            _llmService = new LLMService(_settingsService, _credentialService);
            _gitService = new GitService();
            _snippetService = new SnippetService();
            _languageDetectionService = new LanguageDetectionService();
            _recentFilesService = new RecentFilesService();
            _fileSearchService = new FileSearchService(_fileService);
            _stickyNoteService = new StickyNoteService();
            _settingsDialogService = new SettingsDialogService(_llmService);
            _uiPersonalizationService = new UiPersonalizationService();
            _localizationService = new ResourceLocalizationService(_settingsService);
            _editorTabViewItemFactory = new EditorTabViewItemFactory(_localizationService);
            _editorTabDocumentFactory = new EditorTabDocumentFactory(_languageDetectionService, GetLocalizedString);
            _explorerDirectoryService = new ExplorerDirectoryService();
            _secureNoteEncryptionService = new SecureNoteEncryptionService();
            var fileSaveDialogService = new FileSaveDialogService();
            _compareSelectionDialogService = new CompareSelectionDialogService();
            _unsavedChangesDialogService = new UnsavedChangesDialogService();
            _shellPanelLayoutService = new ShellPanelLayoutService(
                MainWorkGrid,
                ExplorerColumn,
                PreviewColumn,
                LeftSplitter,
                RightSplitter,
                LeftSidebarTabView,
                PreviewGrid);
            _terminalShortcutService = new TerminalShortcutService(WindowNative.GetWindowHandle(this));
            _terminalShortcutService.ToggleRequested += (_, _) => ToggleTerminal();
            _dialogController = new WindowDialogController(
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows());
            _tabEncryptionController = new TabEncryptionController(
                GetLocalizedString,
                _dialogController.WaitForDialogXamlRootAsync,
                GetCurrentElementTheme,
                UpdateWindowTitle,
                _dialogController.ShowErrorMessage);
            _stickyNoteModeController = new StickyNoteModeController(
                this,
                AppTitleBar,
                StickyNoteBar,
                TopToolbar,
                MarkdownToolbar,
                StatusBarPane,
                _shellPanelLayoutService,
                LeftPanelToggle,
                _stickyNoteService,
                ApplyLeftSidebarVisibility,
                ApplyPreviewVisibility);
            _statusBarController = new StatusBarController(
                StatusBarPane,
                GetActiveTab,
                tab => GetActiveTab() == tab,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
                GetLocalizedString,
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows(),
                ReloadTabWithEncodingAsync,
                MarkTabDirtyFromStatusBar,
                PerformLineNavigationAsync);
            _compareTabController = new CompareTabController(
                _fileService,
                _settingsService,
                _viewModel,
                EditorWorkspace,
                EditorTabView,
                _tabBridges,
                GetLocalizedString,
                NormalizeWebMessageJson,
                HandleWebViewShortcut);
            _livePreviewController = new LivePreviewController(
                PreviewGrid,
                _settingsService,
                _tabBridges,
                GetActiveTab,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
                () => _currentFolderPath,
                () => _currentRepoPath,
                () => _scrollSyncEnabled,
                NormalizeWebMessageJson,
                HandleWebViewShortcut,
                SyncPreviewScrollToEditors,
                _dialogController.ShowErrorMessage);
            _tabReloadController = new TabReloadController(
                _secureNoteEncryptionService,
                _settingsService,
                _tabBridges,
                _editorSessions,
                _statusBarController,
                InitialEditorLineWarmupCount,
                _tabEncryptionController.PromptPasswordAsync,
                GetLocalizedString,
                UpdateLivePreview,
                UpdateLanguageUI,
                SchedulePreview,
                UpdateWindowTitle,
                _dialogController.ShowErrorMessage);
            _splitImeSyncController = new SplitImeSyncController(
                _tabBridges,
                _editorSessions,
                GetTabsForSameFile,
                SchedulePreview,
                SetDirtyStateForFileGroup);
            _functionKeyShortcutService = new FunctionKeyShortcutService(WindowNative.GetWindowHandle(this));
            _functionKeyShortcutService.TopMostRequested += (_, _) => _stickyNoteModeController.ToggleTopMostFromShortcut();
            _functionKeyShortcutService.ThemeRequested += (_, _) => OnToggleThemeClick(this, new RoutedEventArgs());
            _functionKeyShortcutService.StickyNoteRequested += (_, _) => _stickyNoteModeController.ToggleMode();
            _gitAutoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _searchReplaceController = new SearchReplaceController(
                _fileSearchService,
                _viewModel,
                SearchQueryInput,
                ReplaceQueryInput,
                SearchMatchCaseToggle,
                SearchWholeWordToggle,
                SearchRegexToggle,
                SearchResultsList,
                GetSearchRoot,
                GetLargeFileThresholdBytes,
                () => this.Content.XamlRoot,
                _dialogController.ShowErrorMessage,
                LoadFileIntoTabAndHighlightAsync,
                RefreshGitStatusUIAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _searchReplaceController.FileModified += OnSearchReplaceFileModifiedAsync;
            _gitPanelController = new GitPanelController(
                _gitService,
                _fileService,
                _viewModel,
                LeftSidebarTabView,
                StatusGitBranch,
                () => _currentRepoPath,
                () => this.Content.XamlRoot,
                GetLocalizedString,
                IsGitNotDetectedText,
                _dialogController.ShowErrorMessage,
                () => _gitAutoRefreshTimer.Start(),
                _compareTabController.OpenCompareTabAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _gitPanelController.FileRestored += OnGitFileRestored;
            _gitStatusRefreshController = new GitStatusRefreshController(
                DispatcherQueue,
                _gitAutoRefreshTimer,
                () => _currentRepoPath,
                _gitPanelController.RefreshAsync);
            _fileTabLoadController = new FileTabLoadController(
                _gitService,
                _secureNoteEncryptionService,
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                path => CurrentRepoPath = path,
                GetLocalizedString,
                _tabEncryptionController.PromptPasswordAsync,
                request => OpenNewTab(
                    request.FilePath,
                    request.Content,
                    request.IsReadOnly,
                    request.EncodingName,
                    request.EncodingWasAutoDetected,
                    request.TextModel,
                    request.IsEncrypted,
                    request.EncryptionPassword),
                QueueGitStatusRefresh,
                _dialogController.ShowErrorMessage);
            _explorerNavigationController = new ExplorerNavigationController(
                LeftSidebarTabView,
                _viewModel,
                _explorerDirectoryService,
                _gitService,
                InitializePickerWindow,
                path => _currentFolderPath = path,
                path => CurrentRepoPath = path,
                RefreshGitStatusUIAsync,
                EnsureLeftPanelVisible,
                ShowLeftSidebarPage,
                LoadFileIntoTabAsync,
                _localizationService);
            _favoritesRecentController = new FavoritesRecentController(
                _settingsService,
                _recentFilesService,
                _viewModel,
                LeftSidebarTabView,
                callback => DispatcherQueue.TryEnqueue(() => callback()),
                NavigateExplorerToFolderAsync,
                LoadFileIntoTabAsync,
                _dialogController.ShowErrorMessage);
            _tabSaveController = new TabSaveController(
                this,
                _fileService,
                fileSaveDialogService,
                _secureNoteEncryptionService,
                _languageDetectionService,
                _statusBarController,
                IsTabOpen,
                tabId => _editorSessions.TryGetValue(tabId, out var session) ? session : null,
                tabId => _tabBridges.TryGetValue(tabId, out var bridgeGroup) ? bridgeGroup : null,
                FlushPendingSplitImeSyncAsync,
                CleanDirtyStateOnOtherTabs,
                UpdateLanguageUI,
                RefreshGitStatusUIAsync,
                UpdateWindowTitle,
                _favoritesRecentController.AddRecentFile,
                () => _currentFolderPath,
                LoadDirectoryRoot,
                GetLocalizedString,
                _dialogController.ShowErrorMessage);
            _autoSaveController = new AutoSaveController(
                _viewModel,
                () => _settingsService.CurrentSettings,
                () => _currentRepoPath,
                SaveTabAsync);
            _tabCloseController = new TabCloseController(
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                _editorSessions,
                _livePreviewController,
                _unsavedChangesDialogService,
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                GetLocalizedString,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows(),
                ClearPendingSplitImeSync,
                _tabEncryptionController.ForgetPassword,
                SaveTabAsync,
                () => OpenNewTab(),
                UpdateWindowTitle);
            _tabMoveController = new TabMoveController(
                _viewModel,
                GetCurrentActiveTabView);
            _windowCloseController = new WindowCloseController(
                _viewModel,
                _unsavedChangesDialogService,
                SaveUiLayoutSettingsAsync,
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                GetLocalizedString,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows(),
                SaveTabAsync,
                this.Close);
            _explorerFileActionsController = new ExplorerFileActionsController(
                LeftSidebarTabView,
                _viewModel,
                EditorTabView,
                EditorTabView2,
                () => _currentFolderPath,
                LoadDirectoryRoot,
                LoadFileIntoTabAsync,
                CloseTabAndCleanup,
                () => this.Content.XamlRoot,
                GetCurrentElementTheme,
                GetLocalizedString,
                _dialogController.ShowErrorMessage,
                () => EditorWorkspace.IsTerminalVisible,
                () => TerminalPane.SuspendNativeWindows(),
                () => TerminalPane.ResumeNativeWindows());
            _tabContextMenuController = new TabContextMenuController(
                _favoritesRecentController,
                GetLocalizedString,
                ShowLeftSidebarPage,
                NavigateExplorerToFolderAsync,
                OnTabReloadAsync,
                OnToggleTabLivePreview,
                _tabEncryptionController.EncryptAsync,
                _tabEncryptionController.ChangePasswordAsync,
                _tabEncryptionController.RemoveEncryptionAsync,
                OnCloseRightTabs,
                OnCloseLeftTabs,
                OnCloseOtherTabs);
            _fileOpenDropController = new FileOpenDropController(
                DragOverlay,
                InitializePickerWindow,
                LoadFileIntoTabAsync,
                NavigateExplorerToFolderAsync,
                _dialogController.ShowErrorMessage);
            _rootKeyboardShortcutController = new RootKeyboardShortcutController(
                () => OpenNewTab(),
                ToggleLeftPanelAsync,
                ToggleRightPanelAsync,
                FocusSearchPanel,
                () => OnCloseActiveTabShortcutInvoked(null!, null!),
                () => OnSaveFileClick(this, new RoutedEventArgs()),
                () => OnSaveAsFileClick(this, new RoutedEventArgs()),
                () => OnOpenFileClick(this, new RoutedEventArgs()),
                () => OnFindClick(this, new RoutedEventArgs()),
                () => OnPrintClick(this, new RoutedEventArgs()),
                _stickyNoteModeController.ToggleTopMostFromShortcut,
                () => OnToggleThemeClick(this, new RoutedEventArgs()),
                _stickyNoteModeController.ToggleMode,
                _terminalShortcutService);
            _terminalPanelController = new TerminalPanelController(
                this,
                EditorWorkspace,
                TopToolbar,
                TerminalPane,
                () => FileListView.SelectedItem as ExplorerItem,
                () => _currentFolderPath,
                () => _currentRepoPath,
                LoadFileIntoTabAsync,
                NavigateExplorerToFolderAsync);
            _snippetsController = new SnippetsController(
                _snippetService,
                _viewModel,
                LeftSidebarTabView,
                () => this.Content.XamlRoot,
                InsertTextIntoActiveEditorAsync,
                SyncSnippetsToOpenEditorsAsync,
                _dialogController.ShowErrorMessage,
                GetLocalizedString,
                InitializePickerWindow,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _llmAssistantController = new LlmAssistantController(
                _llmService,
                _settingsService,
                _languageDetectionService,
                PreviewGrid,
                () => this.Content.XamlRoot,
                GetActiveTab,
                GetTabTextForLlmContext,
                InsertTextIntoActiveEditorAsync,
                _dialogController.ShowErrorMessage,
                GetLocalizedString,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _agentController = new AgentController(
                _llmService,
                _settingsService,
                PreviewGrid.AgentPane,
                GetActiveTab,
                () => _viewModel.Tabs.ToList(),
                GetTabTextForLlmContext,
                InsertTextIntoActiveEditorAsync,
                _dialogController.ShowErrorMessage,
                GetLocalizedString,
                new AgentFileToolService(GetAgentWorkspaceRoot),
                InitializePickerWindow,
                path => _gitService.FindRepositoryRoot(path) != null,
                OpenAgentDiffViewAsync,
                OnAgentFileModifiedAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _tocController = new TocController(
                _viewModel,
                LeftSidebarTabView,
                GetActiveTab,
                tab => _editorSessions.TryGetValue(tab.Id, out var s) ? s : null,
                () => PreviewModeCombo.SelectedIndex == 3,
                async targetLine =>
                {
                    var activeTab = GetActiveTab();
                    if (activeTab != null && _tabBridges.TryGetValue(activeTab.Id, out var bridgeGroup))
                    {
                        if (bridgeGroup.Bridge != null)
                            await bridgeGroup.Bridge.RevealLineAsync(targetLine, 0, 0, "");
                        else if (bridgeGroup.WebView?.CoreWebView2 != null)
                        {
                            var msg = new { action = "revealLine", lineNumber = targetLine, indexOfMatch = 0, matchLength = 0, query = "" };
                            bridgeGroup.WebView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg));
                        }
                    }
                });
            _shellPaneController = new ShellPaneController(
                LeftSidebarTabView,
                StatusBarPane,
                _shellPanelLayoutService,
                SearchQueryInput,
                SaveSidebarVisibilitySettingsAsync,
                () => _favoritesRecentController.RefreshFavorites(true),
                () => _tocController.RefreshToc(GetActiveTab()),
                RefreshActivePreview);
            _markdownToolbarController = new MarkdownToolbarController(
                TopToolbar,
                MarkdownToolbar,
                EditorTabView,
                _tabBridges,
                LoadFileIntoTabAsync,
                InsertTextIntoActiveEditorAsync,
                _dialogController.ShowErrorMessage);
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
            _editorSplitLayoutController = new EditorSplitLayoutController(
                TopToolbar,
                EditorWorkspace,
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                _editorSessions,
                GetActiveTab,
                () => OpenNewTab(),
                (filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted, encryptionPassword) =>
                    OpenNewTab(filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted: isEncrypted, encryptionPassword: encryptionPassword),
                IsAnySameFileTabDirty,
                SetDirtyStateForFileGroup,
                tab => SyncEditsToOtherTabsAsync(tab),
                CloseTabAndCleanup,
                OnEditorTabViewTabCloseRequested,
                _tabSelectionController.QueueChanged,
                _tabSelectionController.ClearQueue,
                UpdateWindowTitle);
            _startupController = new MainWindowStartupController(
                this,
                _settingsService,
                _viewModel,
                EditorWorkspace,
                TerminalPane,
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
                ApplyUiPersonalization,
                LocalizeUi,
                ApplyToolbarSettings,
                RefreshGitStatusUIAsync,
                UpdateAutoSaveStatus,
                _dialogController.ShowErrorMessage);
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
            SearchResultsList.ItemsSource = _viewModel.SearchResults;
            _statusBarController.InitializeEncodings(TextEncodingService.SupportedEncodingNames, "UTF-8");
            WireTopToolbarEvents();
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
        }

        private void WireLeftSidebarEvents()
        {
            LeftSidebarTabView.SearchQueryInputKeyDown += OnSearchQueryInputKeyDown;
            LeftSidebarTabView.SearchAllFilesClick += OnSearchAllFilesClick;
            LeftSidebarTabView.ReplaceAllClick += OnReplaceAllClick;
            LeftSidebarTabView.ReplaceOneClick += OnReplaceOneClick;
            LeftSidebarTabView.SearchResultDoubleTapped += OnSearchResultDoubleTapped;
        }

        private void WireTopToolbarEvents()
        {
            TopToolbar.OpenFileClick += OnOpenFileClick;
            TopToolbar.SaveFileClick += OnSaveFileClick;
            TopToolbar.SaveAsFileClick += OnSaveAsFileClick;
            TopToolbar.CompareFilesClick += OnCompareFilesClick;
            TopToolbar.OpenTerminalClick += OnOpenTerminalClick;
            TopToolbar.PrintClick += OnPrintClick;
            TopToolbar.TopMostToggleClick += (_, _) => _stickyNoteModeController.ApplyTopMostFromToolbar();
            TopToolbar.StickyNoteClick += (_, _) => _stickyNoteModeController.ToggleMode();
            TopToolbar.WordWrapToggleClick += OnWordWrapToggleClick;
            TopToolbar.FindClick += OnFindClick;
            TopToolbar.ToggleCsvTableClick += OnToggleCsvTableClick;
            TopToolbar.ToggleThemeClick += OnToggleThemeClick;
            TopToolbar.SettingsClick += OnSettingsClick;
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
            await _startupController.InitializeAsync();
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
            var documentParts = _editorTabDocumentFactory.Create(
                filePath,
                content,
                isReadOnly,
                encodingName,
                encodingWasAutoDetected,
                textModel,
                isEncrypted,
                encryptionPassword);
            var tab = documentParts.Tab;
            var session = documentParts.Session;
            isReadOnly = documentParts.IsReadOnly;
            _editorSessions[tab.Id] = session;

            _viewModel.Tabs.Add(tab);
            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
            {
                _favoritesRecentController.AddRecentFile(tab.FilePath);
            }

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            ApplyEditorSurfaceBackground(settings);

            var targetTabView = GetCurrentActiveTabView();
            var tabParts = _editorTabViewItemFactory.Create(
                tab,
                editorBgColor,
                settings.UiFontFamily,
                GetLocalizedString("EncryptedTabTooltip", "암호화됨"),
                _tabEncryptionController.ShowMenu,
                (tabItem, args) => ShowTabContextMenu(tab, tabItem, targetTabView, tabItem, args));
            _tabBridges[tab.Id] = (tabParts.WebView, tabParts.Bridge);

            WireEditorBridge(tabParts.Bridge, tabParts.WebView, tabParts.LoadCover, tab, tabParts.TabItem, session, isReadOnly);

            // 탭 헤더와 선택 상태를 먼저 UI에 올린다.
            // WebView2 초기화가 간헐적으로 UI 턴을 잡으면 파일 내용보다 탭 제목이 늦게 보이는 현상이 생길 수 있다.
            EditorWorkspace.DisableTabItemTransitions();
            targetTabView.TabItems.Add(tabParts.TabItem);
            targetTabView.SelectedItem = tabParts.TabItem;
            EditorWorkspace.SetEditorSurfaceBackground(editorBgColor);
            EditorWorkspace.DisableTabItemTransitions();
            this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    EditorWorkspace.SetEditorSurfaceBackground(WebViewAppearanceService.ResolveEditorBackgroundColor(_settingsService.CurrentSettings));
                    EditorWorkspace.DisableTabItemTransitions();
                });

            _statusBarController.UpdateFileStats(tab);
            _statusBarController.UpdateTotalLines(tab);
            _statusBarController.UpdateSelectionStats(null);
            _statusBarController.SyncEncodingCombo(tab);
            _statusBarController.SyncLineEndingText(tab);
            UpdateWindowTitle();

            // Editor WebView2 초기화는 다음 UI 턴의 낮은 우선순위로 미룬다.
            // 이렇게 하면 탭 제목 렌더링이 항상 WebView2 시작 비용보다 먼저 처리된다.
            bool initQueued = this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (_tabBridges.ContainsKey(tab.Id))
                    {
                        InitializeEditorWebView(tabParts.WebView, tabParts.Bridge);
                    }
                });

            if (!initQueued)
            {
                InitializeEditorWebView(tabParts.WebView, tabParts.Bridge);
            }

            return tab;
        }

        private void WireEditorBridge(
            MonacoBridge bridge,
            WebView2 editorWebView,
            Border editorLoadCover,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            bool isReadOnly)
        {
            Func<EditorDocumentSession> getSession = () => _editorSessions.TryGetValue(tab.Id, out var s) ? s : session;

            editorWebView.GotFocus += (sender, args) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    var ownerTabView = GetTabViewForTabItem(tabItem);
                    if (ownerTabView != null)
                    {
                        if (EditorWorkspace.ActiveTabView != ownerTabView || !ReferenceEquals(ownerTabView.SelectedItem, tabItem))
                        {
                            EditorWorkspace.ActiveTabView = ownerTabView;
                            ownerTabView.SelectedItem = tabItem;
                            _tabSelectionController.QueueChanged(ownerTabView, tabItem);
                        }
                    }
                });
            };

            bridge.ShortcutPressed += (shortcutName) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    var currentSession = getSession();
                    switch (shortcutName)
                    {
                        case "f9":
                            _stickyNoteModeController.ToggleTopMostFromShortcut();
                            break;
                        case "f10":
                            OnToggleThemeClick(this, new RoutedEventArgs());
                            break;
                        case "f12":
                            _stickyNoteModeController.ToggleMode();
                            break;
                        case "toggleLeftPanel":
                            _ = ToggleLeftPanelAsync();
                            break;
                        case "toggleRightPanel":
                            _ = ToggleRightPanelAsync();
                            break;
                        case "newTab":
                            OpenNewTab();
                            break;
                        case "save":
                            OnSaveFileClick(this, new RoutedEventArgs());
                            break;
                        case "open":
                            OnOpenFileClick(this, new RoutedEventArgs());
                            break;
                        case "terminal":
                            _terminalShortcutService.RequestToggle();
                            break;
                        case "closeTab":
                            OnCloseActiveTabShortcutInvoked(null!, null!);
                            break;
                        case "print":
                            OnPrintClick(this, new RoutedEventArgs());
                            break;
                        case "searchAll":
                            EnsureLeftPanelVisible();
                            ShowLeftSidebarPage(3);
                            this.DispatcherQueue.TryEnqueue(() =>
                            {
                                SearchQueryInput.Focus(FocusState.Programmatic);
                                SearchQueryInput.Focus(FocusState.Keyboard);
                            });
                            break;
                        case "undo":
                            {
                                var text = currentSession.Undo();
                                if (text != null)
                                {
                                    MarkTabDirty(tab, tabItem);
                                    PropagateDirtyStateToOtherTabs(tab);
                                    SchedulePreview(tab);
                                    _ = bridge.SetTextAsync(text);
                                    _ = SyncEditsToOtherTabsAsync(tab);
                                }
                            }
                            break;
                        case "redo":
                            {
                                var text = currentSession.Redo();
                                if (text != null)
                                {
                                    MarkTabDirty(tab, tabItem);
                                    PropagateDirtyStateToOtherTabs(tab);
                                    SchedulePreview(tab);
                                    _ = bridge.SetTextAsync(text);
                                    _ = SyncEditsToOtherTabsAsync(tab);
                                }
                            }
                            break;
                    }
                });
            };

            bridge.EditorReady += async () =>
            {
                var currentSession = getSession();
                await bridge.InitializeModelAsync(
                    currentSession.Model.LineCount,
                    tab.Language,
                    _settingsService.CurrentSettings,
                    isReadOnly,
                    currentSession.GetLines(1, InitialEditorLineWarmupCount));
                await bridge.UpdateSnippetsAsync(_snippetService.GetSnippets());
                await bridge.UpdateScrollSyncStateAsync(_scrollSyncEnabled);
                await bridge.SetInlineLivePreviewAsync(tab.InlineLivePreviewEnabled, GetPreviewBaseHref(tab));
                await bridge.SetCsvTableModeAsync(_csvTableModeEnabled);
            };

            bridge.EditorRendered += () =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    editorWebView.Opacity = 1;
                    editorLoadCover.Visibility = Visibility.Collapsed;
                    if (GetActiveTab() == tab)
                    {
                        editorWebView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                        _ = bridge.FocusAsync();
                    }
                });
            };

            bridge.LinesRequested += async (requestId, startLine, count) =>
            {
                try
                {
                    var currentSession = getSession();
                    var lines = currentSession.GetLines(startLine, count);
                    await bridge.SendLinesAsync(requestId, startLine, lines);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send editor lines: {ex.Message}");
                }
            };

            bridge.LineChanged += async (lineNumber, text, isComposing) =>
            {
                var currentSession = getSession();
                currentSession.ReplaceLine(lineNumber, text);

                if (!isComposing)
                {
                    MarkTabDirty(tab, tabItem);
                    PropagateDirtyStateToOtherTabs(tab);
                }

                SchedulePreview(tab);

                // Split 상태의 같은 파일 IME 조합 중에는 반대편 pane에 live patch를 보내지 않는다.
                // 특히 컬럼 입력은 첫 줄과 나머지 줄 이벤트 간격이 일정하지 않아 짧은 타이머로는
                // 컬럼 여부를 안전하게 판정할 수 없다. 따라서 조합 중에는 현재 pane의 session만 갱신하고,
                // composition 완료(isComposing=false) 후 보류된 줄을 한 번에 동기화한다.
                // split이 아니거나 같은 파일 반대편 탭이 없는 경우, 그리고 IME 조합 중이 아닌 일반 입력은 기존 경로를 유지한다.
                if (isComposing && QueuePendingSplitImeLineSyncIfNeeded(tab, lineNumber, text))
                {
                    return;
                }

                if (!isComposing && SchedulePendingSplitImeCompletionSyncIfNeeded(tab, lineNumber, text))
                {
                    return;
                }

                await SyncLineChangeToOtherTabsAsync(tab, lineNumber, text, isComposing);
            };

            bridge.LineInsertRequested += async (lineNumber, text) =>
            {
                var currentSession = getSession();
                int lineCount = currentSession.InsertLine(lineNumber, text);
                MarkTabDirty(tab, tabItem);
                PropagateDirtyStateToOtherTabs(tab);
                await bridge.UpdateLineCountAsync(lineCount);
                SchedulePreview(tab);
                await SyncEditsToOtherTabsAsync(tab);
                _statusBarController.UpdateTotalLines(tab);
            };

            bridge.LineSplitRequested += async (lineNumber, before, after) =>
            {
                var currentSession = getSession();
                int lineCount = currentSession.SplitLine(lineNumber, before, after);
                MarkTabDirty(tab, tabItem);
                PropagateDirtyStateToOtherTabs(tab);
                await bridge.UpdateLineCountAsync(lineCount);
                SchedulePreview(tab);
                await SyncEditsToOtherTabsAsync(tab);
                _statusBarController.UpdateTotalLines(tab);
            };

            bridge.MergeLineWithPreviousRequested += async (lineNumber) =>
            {
                var currentSession = getSession();
                int lineCount = currentSession.MergeLineWithPrevious(lineNumber);
                MarkTabDirty(tab, tabItem);
                PropagateDirtyStateToOtherTabs(tab);
                await bridge.UpdateLineCountAsync(lineCount);
                SchedulePreview(tab);
                await SyncEditsToOtherTabsAsync(tab);
                _statusBarController.UpdateTotalLines(tab);
            };

            bridge.DeleteLineRequested += async (lineNumber) =>
            {
                var currentSession = getSession();
                int lineCount = currentSession.DeleteLine(lineNumber);
                MarkTabDirty(tab, tabItem);
                PropagateDirtyStateToOtherTabs(tab);
                await bridge.UpdateLineCountAsync(lineCount);
                SchedulePreview(tab);
                await SyncEditsToOtherTabsAsync(tab);
                _statusBarController.UpdateTotalLines(tab);
            };

            bridge.FindRequested += async (query, startLine, startColumn, reverse, matchCase, isRegex) =>
            {
                var currentSession = getSession();
                var result = currentSession.Find(query, startLine, startColumn, reverse, matchCase, isRegex);
                await bridge.SendFindResultAsync(result, query);
            };

            bridge.FindAllRequested += async (query, matchCase, isRegex) =>
            {
                var currentSession = getSession();
                var results = currentSession.FindAll(query, matchCase, isRegex);
                await bridge.SendFindAllResultsAsync(results, query);
            };

            bridge.ReplaceAllRequested += async (query, replace, matchCase, isRegex) =>
            {
                var currentSession = getSession();
                currentSession.ReplaceAll(query, replace, matchCase, isRegex);
                string updatedText = currentSession.GetText();
                await bridge.SetTextAsync(updatedText, shouldFocus: false);
                await SyncEditsToOtherTabsAsync(tab);
                await bridge.SendFindAllResultsAsync(currentSession.FindAll(query, matchCase, isRegex), query);

                MarkTabDirty(tab, tabItem);
                PropagateDirtyStateToOtherTabs(tab);
                SchedulePreview(tab);
                _statusBarController.UpdateTotalLines(tab);
            };

            bridge.ContentChanged += async (isComposing) =>
            {
                if (!isComposing)
                {
                    MarkTabDirty(tab, tabItem);
                    PropagateDirtyStateToOtherTabs(tab);
                    UpdateLanguageUI(tab);
                    _tocController?.RefreshToc(tab);
                    _statusBarController.UpdateTotalLines(tab);
                    ScheduleDeferredPendingSplitImeSyncIfNeeded(tab);
                }

                SchedulePreview(tab);
            };

            bridge.CursorChanged += (line, col) =>
            {
                var ownerTabView = GetTabViewForTab(tab);
                if (ownerTabView != null && EditorWorkspace.ActiveTabView != ownerTabView)
                {
                    EditorWorkspace.ActiveTabView = ownerTabView;
                    if (ownerTabView.SelectedItem is TabViewItem activeTabItem)
                    {
                        _tabSelectionController.QueueChanged(ownerTabView, activeTabItem);
                    }
                }

                if (GetActiveTab() == tab)
                {
                    _statusBarController.SetCursorPosition(line, col);
                    _ = bridge.RequestSelectionAsync();
                }
            };

            bridge.SelectionReceived += (selectedText, selStartLine, selEndLine) =>
            {
                var ownerTabView = GetTabViewForTab(tab);
                if (ownerTabView != null && EditorWorkspace.ActiveTabView != ownerTabView)
                {
                    EditorWorkspace.ActiveTabView = ownerTabView;
                    if (ownerTabView.SelectedItem is TabViewItem activeTabItem)
                    {
                        _tabSelectionController.QueueChanged(ownerTabView, activeTabItem);
                    }
                }

                if (GetActiveTab() == tab)
                {
                    _llmAssistantController.SetSelectionText(selectedText);
                    _agentController.SetSelectionText(selectedText, tab, selStartLine, selEndLine);
                    if (string.IsNullOrEmpty(selectedText))
                    {
                        SelectionStatsText.Text = GetLocalizedString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
                    }
                    else
                    {
                        string fmt = GetLocalizedString("SelectionStats", "선택 영역: {0} 글자 수 (약 {1} 토큰)");
                        SelectionStatsText.Text = string.Format(fmt, selectedText.Length.ToString("N0"), (selectedText.Length / 4).ToString("N0"));
                    }
                    _statusBarController.UpdateSelectionStats(selectedText);
                }
            };

            bridge.ScrollChanged += (firstLine, offset) =>
            {
                if (!_scrollSyncEnabled)
                {
                    return;
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (GetActiveTab() == tab)
                    {
                        _livePreviewController.PostScrollSync(firstLine, offset);

                        // If split mode is active, sync the other pane's selected tab too
                        if (EditorWorkspace.CurrentSplitMode != TxtAIEditor.Controls.EditorSplitMode.None)
                        {
                            TabView? otherTabView = null;
                            if (IsTabInTabView(EditorTabView, tab.Id))
                            {
                                otherTabView = EditorTabView2;
                            }
                            else if (IsTabInTabView(EditorTabView2, tab.Id))
                            {
                                otherTabView = EditorTabView;
                            }

                            if (otherTabView != null && otherTabView.SelectedItem is TabViewItem otherItem && otherItem.Tag is string otherTabId)
                            {
                                if (_tabBridges.TryGetValue(otherTabId, out var otherBridgeGroup) && otherBridgeGroup.Bridge != null)
                                {
                                    _ = otherBridgeGroup.Bridge.SyncScrollFromPreviewAsync(firstLine, offset);
                                }
                            }
                        }
                    }
                });
            };

            bridge.ScrollSyncChanged += (enabled) =>
            {
                this.DispatcherQueue.TryEnqueue(async () =>
                {
                    _scrollSyncEnabled = enabled;

                    // Synchronize all open editor tabs to this state
                    foreach (var grp in _tabBridges.Values)
                    {
                        if (grp.Bridge != null)
                        {
                            await grp.Bridge.UpdateScrollSyncStateAsync(enabled);
                        }
                    }

                    _livePreviewController.PostScrollSyncState(enabled);
                });
            };
        }

        private Dictionary<int, string> ComputeDirtyLines(OpenedTab tab, EditorDocumentSession session)
        {
            var markers = new Dictionary<int, string>();
            if (tab.OriginalContent == null) return markers;

            var orig = tab.OriginalLines;
            var current = session.Model.GetLines(1, session.Model.LineCount);

            // Find common prefix
            int prefixMatchCount = 0;
            int maxPrefix = Math.Min(orig.Length, current.Count);
            while (prefixMatchCount < maxPrefix && orig[prefixMatchCount] == current[prefixMatchCount])
            {
                prefixMatchCount++;
            }

            // Find common suffix
            int suffixMatchCount = 0;
            int maxSuffix = Math.Min(orig.Length - prefixMatchCount, current.Count - prefixMatchCount);
            while (suffixMatchCount < maxSuffix && 
                   orig[orig.Length - 1 - suffixMatchCount] == current[current.Count - 1 - suffixMatchCount])
            {
                suffixMatchCount++;
            }

            int unmatchedOrigCount = orig.Length - prefixMatchCount - suffixMatchCount;
            int unmatchedCurrentCount = current.Count - prefixMatchCount - suffixMatchCount;

            int oi = prefixMatchCount;
            int ci = prefixMatchCount;
            int limitOrig = orig.Length - suffixMatchCount;
            int limitCurr = current.Count - suffixMatchCount;

            int scanLimit = Math.Max(100, Math.Abs(unmatchedOrigCount - unmatchedCurrentCount) + 10);

            while (oi < limitOrig && ci < limitCurr)
            {
                if (orig[oi] == current[ci])
                {
                    oi++; ci++;
                }
                else
                {
                    int aheadOrig = -1;
                    for (int s = oi + 1; s < Math.Min(oi + scanLimit, limitOrig); s++)
                    {
                        if (orig[s] == current[ci]) { aheadOrig = s; break; }
                    }
                    int aheadCurr = -1;
                    for (int s = ci + 1; s < Math.Min(ci + scanLimit, limitCurr); s++)
                    {
                        if (current[s] == orig[oi]) { aheadCurr = s; break; }
                    }

                    if (aheadOrig >= 0 && (aheadCurr < 0 || (aheadOrig - oi) < (aheadCurr - ci)))
                    {
                        markers[ci + 1] = "del";
                        oi = aheadOrig;
                    }
                    else if (aheadCurr >= 0)
                    {
                        for (int a = ci; a < aheadCurr; a++)
                        {
                            markers[a + 1] = "add";
                        }
                        ci = aheadCurr;
                    }
                    else
                    {
                        markers[ci + 1] = "mod";
                        oi++; ci++;
                    }
                }
            }

            if (oi < limitOrig && limitCurr >= 1)
            {
                markers[limitCurr] = "del";
            }

            while (ci < limitCurr)
            {
                markers[ci + 1] = "add";
                ci++;
            }

            return markers;
        }

        private void CheckAndUpdateDirtyState(OpenedTab tab)
        {
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                var dirtyLines = ComputeDirtyLines(tab, session);
                bool isDirty = (dirtyLines.Count > 0) ||
                               (tab.OriginalLineEnding != null && session.Model.LineEnding != tab.OriginalLineEnding) ||
                               (tab.OriginalEncodingName != null && tab.EncodingName != tab.OriginalEncodingName);
                SetDirtyStateForFileGroup(tab, isDirty);

                foreach (var t in GetTabsForSameFile(tab))
                {
                    if (_tabBridges.TryGetValue(t.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        _ = bridgeGroup.Bridge.UpdateDirtyLinesAsync(dirtyLines);
                    }
                }
            }
            else
            {
                SetDirtyStateForFileGroup(tab, true);
            }
        }

        private void MarkTabDirty(OpenedTab tab, TabViewItem? tabItem = null)
        {
            CheckAndUpdateDirtyState(tab);
        }

        private List<OpenedTab> GetTabsForSameFile(OpenedTab sourceTab)
        {
            string? pathKey = NormalizeTabPath(sourceTab.FilePath);
            if (pathKey == null)
            {
                return new List<OpenedTab> { sourceTab };
            }

            var tabs = _viewModel.Tabs
                .Where(tab =>
                {
                    string? otherPathKey = NormalizeTabPath(tab.FilePath);
                    return otherPathKey != null &&
                           string.Equals(otherPathKey, pathKey, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (!tabs.Any(tab => tab.Id == sourceTab.Id))
            {
                tabs.Add(sourceTab);
            }

            return tabs;
        }

        private static string? NormalizeTabPath(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch
            {
                return filePath;
            }
        }

        private bool IsAnySameFileTabDirty(OpenedTab sourceTab)
        {
            return GetTabsForSameFile(sourceTab).Any(tab => tab.IsDirty);
        }

        private void SetDirtyStateForFileGroup(OpenedTab sourceTab, bool isDirty)
        {
            bool changed = false;
            string? savedContent = null;
            if (!isDirty)
            {
                if (_editorSessions.TryGetValue(sourceTab.Id, out var session))
                {
                    savedContent = session.GetText();
                }
                else
                {
                    savedContent = sourceTab.Content;
                }
            }

            foreach (var tab in GetTabsForSameFile(sourceTab))
            {
                if (savedContent != null)
                {
                    tab.OriginalContent = savedContent;
                    bool hasSession = _editorSessions.TryGetValue(tab.Id, out var session);
                    tab.OriginalLineEnding = hasSession ? session!.Model.LineEnding : LineArrayTextModel.FromText(savedContent).LineEnding;
                    tab.OriginalEncodingName = tab.EncodingName;

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        if (hasSession)
                        {
                            var lines = session!.GetLines(1, session.Model.LineCount);
                            _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(lines);
                        }
                        else
                        {
                            var lines = savedContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                            _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(lines);
                        }
                    }
                }

                if (tab.IsDirty != isDirty)
                {
                    tab.IsDirty = isDirty;
                    changed = true;
                }
            }

            if (changed)
            {
                UpdateWindowTitle();
            }
        }

        private void PropagateDirtyStateToOtherTabs(OpenedTab sourceTab)
        {
            CheckAndUpdateDirtyState(sourceTab);
        }

        private void CleanDirtyStateOnOtherTabs(OpenedTab sourceTab)
        {
            SetDirtyStateForFileGroup(sourceTab, false);
        }

        private void SchedulePreview(OpenedTab tab)
        {
            _livePreviewController.Schedule(tab);
        }

        private async void InitializeEditorWebView(WebView2 wv, MonacoBridge bridge)
        {
            try
            {
                WebViewAppearanceService.ApplyEditorHostBackground(
                    wv,
                    WebViewAppearanceService.ResolveEditorBackgroundColor(_settingsService.CurrentSettings));
                await bridge.InitializeAsync();

                var coreWebView = wv.CoreWebView2;
                if (coreWebView == null)
                {
                    throw new InvalidOperationException("CoreWebView2 failed to initialize.");
                }

                WebViewAppearanceService.ApplyPreferredColorScheme(coreWebView, _settingsService.CurrentSettings.Theme);
                
                coreWebView.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.ResourceHostName,
                    PreviewWebResourceService.WebResourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow
                );
                _livePreviewController.RegisterDocumentResourceAccess(coreWebView);

                var settings = _settingsService.CurrentSettings;
                var url = $"http://{PreviewWebResourceService.ResourceHostName}/editor.html?v={PreviewWebResourceService.GetEditorResourceVersion()}" +
                    $"&theme={Uri.EscapeDataString(settings.Theme)}" +
                    $"&fontSize={settings.FontSize}" +
                    $"&fontFamily={Uri.EscapeDataString(settings.FontFamily)}" +
                    $"&wordWrap={(settings.WordWrap ? "pre-wrap" : "pre")}";
                if (!string.IsNullOrEmpty(settings.CustomBackgroundColor))
                    url += $"&customBg={Uri.EscapeDataString(settings.CustomBackgroundColor)}";
                if (!string.IsNullOrEmpty(settings.CustomForegroundColor))
                    url += $"&customFg={Uri.EscapeDataString(settings.CustomForegroundColor)}";

                bridge.LoadEditor(url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed initialization of editor: {ex.Message}");
            }
        }

        #endregion

        #region Live Preview Debouncing & Sync

        private void UpdateLivePreview(OpenedTab tab) => _livePreviewController.Render(tab);

        private string GetPreviewBaseHref(OpenedTab tab) => _livePreviewController.GetPreviewBaseHref(tab);

        #endregion

        #region XAML Interactive Handlers

        private async void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            await _fileOpenDropController.OpenFileAsync();
        }

        internal async Task LoadFileIntoTabAsync(string filePath)
        {
            await _fileTabLoadController.LoadAsync(filePath);
        }

        private async void OnSaveFileClick(object sender, RoutedEventArgs e)
        {
            var activeTabView = GetCurrentActiveTabView();
            if (activeTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId)
            {
                var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    await SaveTabAsync(tab);
                }
            }
        }

        private async void OnSaveAsFileClick(object sender, RoutedEventArgs e)
        {
            var activeTabView = GetCurrentActiveTabView();
            if (activeTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId)
            {
                var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    await SaveAsTabAsync(tab);
                }
            }
        }

        private async void OnWordWrapToggleClick(object sender, RoutedEventArgs e)
        {
            var settings = _settingsService.CurrentSettings;
            settings.WordWrap = TopToolbar.WordWrapIsChecked;
            await _settingsService.SaveSettingsAsync(settings);

            await ApplySettingsToOpenEditorsAsync(settings);
        }

        private async void OnFindClick(object sender, RoutedEventArgs e)
        {
            if (EditorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup))
            {
                if (bridgeGroup.Bridge != null)
                {
                    bridgeGroup.WebView.Focus(FocusState.Programmatic);
                    await bridgeGroup.Bridge.TriggerFindAsync();
                    return;
                }
            }

            EnsureLeftPanelVisible();
            ShowLeftSidebarPage(3);
            SearchQueryInput.Focus(FocusState.Programmatic);
            SearchQueryInput.Focus(FocusState.Keyboard);
        }

        private async void OnToggleCsvTableClick(object sender, RoutedEventArgs e)
        {
            _csvTableModeEnabled = TopToolbar.CsvTableIsChecked;

            foreach (var grp in _tabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.SetCsvTableModeAsync(_csvTableModeEnabled);
                }
            }
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

        private async Task ToggleLeftPanelAsync()
        {
            await _shellPaneController.ToggleLeftPanelAsync();
        }

        private async Task ToggleRightPanelAsync()
        {
            await _shellPaneController.ToggleRightPanelAsync();
        }

        private async void OnToggleThemeClick(object sender, RoutedEventArgs e)
        {
            var settings = _settingsService.CurrentSettings;
            settings.Theme = settings.Theme == "Light" ? "Dark" : "Light";
            await _settingsService.SaveSettingsAsync(settings);
            ApplyUiPersonalization(settings);
            RefreshAllSplitters();

            _livePreviewController.ApplyPreferredColorScheme(settings.Theme);
            foreach (var grp in _tabBridges.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(grp.WebView?.CoreWebView2, settings.Theme);
            }

            foreach (var grp in _tabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateOptionsAsync(settings);
                }
                else if (grp.WebView?.CoreWebView2 != null)
                {
                    var updateMsg = new
                    {
                            action = "updateOptions",
                            theme = settings.Theme,
                            wordWrap = settings.WordWrap,
                            bracketPairColorization = settings.BracketPairColorization,
                            fontSize = settings.FontSize,
                            fontFamily = settings.FontFamily,
                            tabSize = settings.TabSize,
                            customBackgroundColor = settings.CustomBackgroundColor,
                            customForegroundColor = settings.CustomForegroundColor,
                            autocompleteOnEnter = settings.AutocompleteOnEnter,
                            autocompleteOnTab = settings.AutocompleteOnTab,
                            readOnly = true
                        };
                    grp.WebView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(updateMsg));
                }
            }

            if (EditorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId)
            {
                var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null) UpdateLivePreview(tab);
            }
        }

        private void RefreshAllSplitters()
        {
            LeftSplitter.RefreshTheme();
            RightSplitter.RefreshTheme();
            EditorWorkspace.RefreshSplitters();
        }

        private string GetLocalizedString(string key, string fallback)
        {
            return _localizationService.GetString(key, fallback);
        }

        private void ApplyResourceLanguage()
        {
            _localizationService.ApplyResourceLanguage();
        }

        private void LocalizeUi()
        {
            try
            {
                ApplyResourceLanguage();
                string GetString(string key, string fallback) => GetLocalizedString(key, fallback);

                TopToolbar.Localize(GetString);
                EditorWorkspace.Localize(GetString);
                LeftSidebarTabView.Localize(GetString, string.IsNullOrEmpty(_currentFolderPath), IsGitNotDetectedText);
                StatusBarPane.Localize(GetString, IsGitNotDetectedText);
                TerminalPane.Localize(GetString);
                PreviewGrid.Localize(GetString);
                PreviewGrid.UpdateTranslateLanguage(_settingsService.CurrentSettings?.LlmTargetLanguage ?? "Korean");
                MarkdownToolbar.LocalizeTooltips(GetString);
                StickyNoteBar.Localize(GetString);

                var activeTab = GetActiveTab();
                if (activeTab != null)
                {
                    _statusBarController.UpdateTotalLines(activeTab);
                }

                _agentController?.UpdateModelDisplay();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to localize UI: {ex.Message}");
            }
        }

        private static bool IsGitNotDetectedText(string text)
        {
            return text.Equals("Git: 감지 안됨", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Git: Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Git: 検出されていません", StringComparison.OrdinalIgnoreCase);
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            // Suspend native terminal windows so settings dialog is not hidden behind them
            bool terminalWasVisible = EditorWorkspace.IsTerminalVisible;
            if (terminalWasVisible)
                TerminalPane.SuspendNativeWindows();

            var settings = _settingsService.CurrentSettings;
            string oldLanguage = settings.Language;

            string GetSettingsString(string key, string fallback) => GetLocalizedString(key, fallback);

            var result = await _settingsDialogService.ShowAsync(settings, this.Content.XamlRoot, GetSettingsString);
            if (terminalWasVisible)
                TerminalPane.ResumeNativeWindows();
            if (!result.Saved)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.ApiKeyStatusMessage))
            {
                _llmAssistantController.SetOutput(result.ApiKeyStatusMessage);
            }

            await _settingsService.SaveSettingsAsync(settings);
            _agentController.UpdateModelDisplay();
            ApplyResourceLanguage();
            ApplyPreviewVisibility(settings.DefaultMarkdownEnabled);
            TopToolbar.MarkdownToolbarIsChecked = settings.DefaultMarkdownToolbarEnabled;
            MarkdownToolbar.Visibility = settings.DefaultMarkdownToolbarEnabled ? Visibility.Visible : Visibility.Collapsed;

            // Enable auto-save if setting is on and git is available
            UpdateAutoSaveStatus();
            TopToolbar.WordWrapIsChecked = settings.WordWrap;
            ApplyUiPersonalization(settings);
            TerminalPane.ApplySettings(settings);
            LocalizeUi();
            ApplyToolbarSettings(settings);

            if (oldLanguage != settings.Language && await ConfirmRestartForLanguageChangeAsync(GetSettingsString))
            {
                _lifecycleController.CleanupBeforeRestart();
                Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                return;
            }

            await ApplySettingsToOpenEditorsAsync(settings);
            RefreshActivePreview();
        }

        private async Task<bool> ConfirmRestartForLanguageChangeAsync(Func<string, string, string> getString)
        {
            bool terminalWasVisible = EditorWorkspace.IsTerminalVisible;
            if (terminalWasVisible)
                TerminalPane.SuspendNativeWindows();
            var restartDialog = new ContentDialog
            {
                Title = getString("LanguageChangedTitle", "Language Change"),
                Content = getString("LanguageChangedMessage", "You must restart the application to apply the language settings. Would you like to restart now?"),
                PrimaryButtonText = getString("Restart", "Restart"),
                CloseButtonText = getString("No", "Later"),
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = GetCurrentElementTheme()
            };

            var result = await restartDialog.ShowAsync() == ContentDialogResult.Primary;
            if (terminalWasVisible)
                TerminalPane.ResumeNativeWindows();
            return result;
        }

        private async Task ApplySettingsToOpenEditorsAsync(EditorSettings settings)
        {
            foreach (var grp in _tabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateOptionsAsync(settings);
                }
                else if (grp.WebView?.CoreWebView2 != null)
                {
                    var updateMsg = new
                    {
                        action = "updateOptions",
                        theme = settings.Theme,
                        wordWrap = settings.WordWrap,
                        bracketPairColorization = settings.BracketPairColorization,
                        fontSize = settings.FontSize,
                        fontFamily = settings.FontFamily,
                        tabSize = settings.TabSize,
                        customBackgroundColor = settings.CustomBackgroundColor,
                        customForegroundColor = settings.CustomForegroundColor,
                        autocompleteOnEnter = settings.AutocompleteOnEnter,
                        autocompleteOnTab = settings.AutocompleteOnTab,
                        readOnly = true
                    };
                    string updateJson = System.Text.Json.JsonSerializer.Serialize(updateMsg);
                    grp.WebView.CoreWebView2.PostWebMessageAsJson(updateJson);
                }
            }
        }

        private async Task SyncSnippetsToOpenEditorsAsync()
        {
            var snippets = _snippetService.GetSnippets();
            foreach (var grp in _tabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateSnippetsAsync(snippets);
                }
            }
        }

        private void RefreshActivePreview()
        {
            _livePreviewController.RenderActiveTab();
        }

        #endregion

        #region Explorer Side Panel & Folder Picker

        private void LoadDirectoryRoot(string folderPath)
        {
            _explorerNavigationController.LoadDirectoryRoot(folderPath);
        }

        private async Task NavigateExplorerToFolderAsync(string folderPath)
        {
            await _explorerNavigationController.NavigateToFolderAsync(folderPath);
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
                await LoadFileIntoTabAsync(cleanedPath);
            }
            else if (Directory.Exists(cleanedPath))
            {
                await NavigateExplorerToFolderAsync(cleanedPath);
            }
        }

        private void OnOpenTerminalClick(object sender, RoutedEventArgs e)
        {
            ToggleTerminal();
        }

        #endregion

        #region Terminal Panel Layout

        private void ToggleTerminal()
        {
            _terminalPanelController.Toggle();
        }

        private async Task OnAgentFileModifiedAsync(string filePath)
        {
            await OnSearchReplaceFileModifiedAsync(filePath);

            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                LoadDirectoryRoot(_currentFolderPath);
            }

            QueueGitStatusRefresh();

            // Auto-update open compare tabs for this file
            var edit = _agentController.SessionEdits.FirstOrDefault(e => string.Equals(e.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
            if (edit != null)
            {
                string title = $"{GetLocalizedString("AgentDiffTitle", "Agent 변경 비교")}: {Path.GetFileName(edit.RelativePath)}";
                await _compareTabController.UpdateCompareTabIfOpenAsync(
                    title,
                    edit.FullPath,
                    edit.FullPath,
                    edit.OldContent,
                    edit.NewContent,
                    labelA: GetLocalizedString("DiffOriginalLabel", "원본"),
                    labelB: GetLocalizedString("DiffModifiedLabel", "수정본"));
            }
        }

        private async Task OpenAgentDiffViewAsync(AgentFileEditPreview preview)
        {
            await _compareTabController.OpenCompareTabAsync(
                preview.FullPath,
                preview.FullPath,
                preview.OldContent,
                preview.NewContent,
                customTitle: $"{GetLocalizedString("AgentDiffTitle", "Agent 변경 비교")}: {Path.GetFileName(preview.RelativePath)}",
                labelA: GetLocalizedString("DiffOriginalLabel", "원본"),
                labelB: GetLocalizedString("DiffModifiedLabel", "수정본"));
        }

        private string GetAgentWorkspaceRoot()
        {
            if (FileListView.SelectedItem is ExplorerItem selectedItem)
            {
                if (selectedItem.IsFolder && Directory.Exists(selectedItem.Path))
                {
                    return selectedItem.Path;
                }

                string? selectedFileDirectory = Path.GetDirectoryName(selectedItem.Path);
                if (!string.IsNullOrWhiteSpace(selectedFileDirectory) && Directory.Exists(selectedFileDirectory))
                {
                    return selectedFileDirectory;
                }
            }

            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            {
                return _currentFolderPath;
            }

            if (!string.IsNullOrWhiteSpace(_currentRepoPath) && Directory.Exists(_currentRepoPath))
            {
                return _currentRepoPath;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }



        #endregion

        #region Split Editor Layout

        private TabView GetCurrentActiveTabView()
        {
            return EditorWorkspace.GetCurrentActiveTabView();
        }

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

        #endregion

        #region Helpers & UI Triggers

        private void MarkTabDirtyFromStatusBar(OpenedTab tab)
        {
            var tabItem = EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                       ?? EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
            if (tabItem != null)
            {
                MarkTabDirty(tab, tabItem);
            }
            else
            {
                tab.IsDirty = true;
            }
        }

        private async Task ReloadTabWithEncodingAsync(OpenedTab tab, string encodingName)
        {
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

        private void SyncPreviewScrollToEditors(int firstLine, double offset)
        {
            var activeTab = GetActiveTab();
            if (activeTab == null)
            {
                return;
            }

            if (_tabBridges.TryGetValue(activeTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                _ = bridgeGroup.Bridge.SyncScrollFromPreviewAsync(firstLine, offset);
            }

            if (EditorWorkspace.CurrentSplitMode == TxtAIEditor.Controls.EditorSplitMode.None)
            {
                return;
            }

            TabView? otherTabView = null;
            if (IsTabInTabView(EditorTabView, activeTab.Id))
            {
                otherTabView = EditorTabView2;
            }
            else if (IsTabInTabView(EditorTabView2, activeTab.Id))
            {
                otherTabView = EditorTabView;
            }

            if (otherTabView?.SelectedItem is TabViewItem otherItem &&
                otherItem.Tag is string otherTabId &&
                _tabBridges.TryGetValue(otherTabId, out var otherBridgeGroup) &&
                otherBridgeGroup.Bridge != null)
            {
                _ = otherBridgeGroup.Bridge.SyncScrollFromPreviewAsync(firstLine, offset);
            }
        }

        private void HandleWebViewShortcut(string name)
        {
            if (string.Equals(name, "find", StringComparison.Ordinal))
            {
                OnFindClick(null!, null!);
            }
            else if (string.Equals(name, "f9", StringComparison.Ordinal))
            {
                _stickyNoteModeController.ToggleTopMostFromShortcut();
            }
            else if (string.Equals(name, "f10", StringComparison.Ordinal))
            {
                OnToggleThemeClick(this, new RoutedEventArgs());
            }
            else if (string.Equals(name, "f12", StringComparison.Ordinal))
            {
                _stickyNoteModeController.ToggleMode();
            }
            else if (string.Equals(name, "print", StringComparison.Ordinal))
            {
                OnPrintClick(this, new RoutedEventArgs());
            }
        }

        private OpenedTab? GetActiveTab()
        {
            var activeTabView = GetCurrentActiveTabView();
            if (activeTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId)
            {
                return _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            }

            return null;
        }

        private bool IsTabInTabView(TabView tabView, string tabId)
        {
            foreach (var item in tabView.TabItems)
            {
                if (item is TabViewItem tvi && string.Equals(tvi.Tag as string, tabId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsTabOpen(OpenedTab tab)
        {
            return IsTabInTabView(EditorTabView, tab.Id) || IsTabInTabView(EditorTabView2, tab.Id);
        }

        private TabView? GetTabViewForTab(OpenedTab tab)
        {
            if (IsTabInTabView(EditorTabView, tab.Id)) return EditorTabView;
            if (IsTabInTabView(EditorTabView2, tab.Id)) return EditorTabView2;
            return null;
        }

        private TabView? GetTabViewForTabItem(TabViewItem tabItem)
        {
            if (EditorTabView.TabItems.Contains(tabItem))
            {
                return EditorTabView;
            }

            if (EditorTabView2.TabItems.Contains(tabItem))
            {
                return EditorTabView2;
            }

            return null;
        }

        private void ShowTabContextMenu(
            OpenedTab tab,
            TabViewItem tabItem,
            TabView fallbackTabView,
            FrameworkElement target,
            RightTappedRoutedEventArgs args)
        {
            args.Handled = true;

            var ownerTabView = GetTabViewForTabItem(tabItem) ?? fallbackTabView;
            var flyout = _tabContextMenuController.CreateContextFlyout(tab, tabItem, ownerTabView);
            CursorResetHelper.AttachToFlyout(flyout, target);
            CursorResetHelper.ResetToArrow(target);
            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = args.GetPosition(target)
            });
            CursorResetHelper.ResetToArrow(target);
        }

        private async void OnToggleTabLivePreview(OpenedTab tab, TabViewItem tabItem, bool enabled)
        {
            tab.InlineLivePreviewEnabled = enabled;
            await ApplyInlineLivePreviewAsync(tab);
        }

        private async Task ApplyInlineLivePreviewAsync(OpenedTab tab)
        {
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.SetInlineLivePreviewAsync(
                    tab.InlineLivePreviewEnabled,
                    GetPreviewBaseHref(tab));
            }
        }

        private async Task<bool> InsertTextIntoActiveEditorAsync(string text)
        {
            var activeTabView = GetCurrentActiveTabView();
            if (activeTabView.SelectedItem is not TabViewItem activeTabItem ||
                activeTabItem.Tag is not string tabId ||
                !_tabBridges.TryGetValue(tabId, out var bridgeGroup) ||
                bridgeGroup.Bridge == null)
            {
                return false;
            }

            await bridgeGroup.Bridge.InsertTextAsync(text);

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                MarkTabDirty(tab, activeTabItem);
                PropagateDirtyStateToOtherTabs(tab);
            }

            return true;
        }

        private string GetTabTextForLlmContext(OpenedTab tab, int maxChars)
        {
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                return session.GetText(maxChars);
            }

            return tab.Content ?? string.Empty;
        }

        private void UpdateWindowTitle()
        {
            var activeTab = GetActiveTab();
            string pathOrTitle = activeTab != null 
                ? (!string.IsNullOrEmpty(activeTab.FilePath) ? activeTab.FilePath : activeTab.Title)
                : "";

            string newTitle = string.IsNullOrEmpty(pathOrTitle) 
                ? "TxtAIEditor" 
                : $"TxtAIEditor - {pathOrTitle}";

            this.Title = newTitle;

            if (AppTitleTextBlock != null)
            {
                AppTitleTextBlock.Text = newTitle;
            }
        }

        #endregion

        #region UI Personalization Helper
        private void ApplyUiPersonalization(EditorSettings settings)
        {
            _uiPersonalizationService.Apply(
                settings,
                AppWindow,
                Content as FrameworkElement,
                ApplyMarkdownToolbarBackground);
            ApplyEditorSurfaceBackground(settings);
        }

        private void ApplyMarkdownToolbarBackground(Windows.UI.Color color)
        {
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            MarkdownToolbarHost.Background = brush;
            MarkdownToolbar.SetToolbarBackground(color);
        }

        private void ApplyToolbarSettings(EditorSettings settings)
        {
            TopToolbar.ApplySettings(settings, GetLocalizedString);
        }
        #endregion

        #region Advanced Git Handlers

        private async Task RefreshGitStatusUIAsync()
        {
            await _gitStatusRefreshController.RefreshAsync();
        }

        private void QueueGitStatusRefresh()
        {
            _gitStatusRefreshController.QueueRefresh();
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

        private async Task OnSearchReplaceFileModifiedAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            var matchedTabs = _viewModel.Tabs.Where(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchedTabs.Count == 0) return;

            try
            {
                var readResult = await LineArrayTextModel.LoadFromFileAsync(filePath, "Auto");
                
                foreach (var tab in matchedTabs)
                {
                    if (_editorSessions.TryGetValue(tab.Id, out var session))
                    {
                        session.UpdateContentFromSync(readResult.Model.GetText());
                    }

                    tab.Content = readResult.Model.GetText();
                    tab.IsDirty = false;

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        await bridgeGroup.Bridge.SetTextAsync(tab.Content, shouldFocus: false);
                    }

                    var tabItem = EditorTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id)
                               ?? EditorTabView2.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tab.Id);
                    if (tabItem != null)
                    {
                        CleanDirtyStateOnOtherTabs(tab);
                    }

                    if (tab == GetActiveTab())
                    {
                        UpdateLivePreview(tab);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to hot-reload replaced file '{filePath}': {ex.Message}");
            }
        }

        private async void OnSearchResultDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            await _searchReplaceController.OpenSearchResultAsync(e.OriginalSource);
        }

        private string GetSearchRoot()
        {
            return !string.IsNullOrEmpty(_currentFolderPath) ? _currentFolderPath : _currentRepoPath;
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
            _tabCloseController.CloseActive(GetCurrentActiveTabView());
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
            if (tab == null) return;
            string detected = tab.Language;
            if (detected == "plaintext" || string.IsNullOrEmpty(detected))
            {
                string content = tab.Content;
                if (_editorSessions.TryGetValue(tab.Id, out var session))
                {
                    content = session.GetText(2000);
                }
                detected = _languageDetectionService.DetectLanguageFromContent(content, "plaintext");
            }

            if (StatusLanguage != null)
            {
                StatusLanguage.Text = detected.ToUpper();
            }

            if (tab.Language != detected)
            {
                tab.Language = detected;
                if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    _ = bridgeGroup.Bridge.SetLanguageAsync(detected);
                }
            }
        }

        private async Task LoadFileIntoTabAndHighlightAsync(SearchResultItem item, string query)
        {
            await LoadFileIntoTabAsync(item.Path);
            await Task.Delay(250);

            string? targetTabId = null;
            foreach (var tab in _viewModel.Tabs)
            {
                if (string.Equals(tab.FilePath, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    targetTabId = tab.Id;
                    break;
                }
            }

            if (targetTabId != null && _tabBridges.TryGetValue(targetTabId, out var bridgeGroup) && bridgeGroup.WebView?.CoreWebView2 != null)
            {
                var revealMsg = new { action = "revealLine", lineNumber = item.LineNumber, indexOfMatch = item.IndexOfMatch, matchLength = item.MatchLength, query };
                string json = System.Text.Json.JsonSerializer.Serialize(revealMsg);
                try { bridgeGroup.WebView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
            }
        }

        private async void OnSearchQueryInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await _searchReplaceController.HandleSearchQueryEnterAsync();
            }
        }

        private void ApplyPreviewVisibility(bool show)
        {
            _shellPaneController.ApplyPreviewVisibility(show);
        }

        private async void OnCompareFilesClick(object sender, RoutedEventArgs e)
        {
            bool terminalWasVisible = EditorWorkspace.IsTerminalVisible;
            if (terminalWasVisible)
                TerminalPane.SuspendNativeWindows();
            var selection = await _compareSelectionDialogService.ShowAsync(this, this.Content.XamlRoot, _viewModel.Tabs, GetCurrentElementTheme(), GetLocalizedString);
            if (terminalWasVisible)
                TerminalPane.ResumeNativeWindows();
            if (selection == null)
            {
                return;
            }

            if (selection.IsValid)
            {
                await _compareTabController.OpenCompareTabAsync(selection.PathA, selection.PathB, selection.ContentA, selection.ContentB);
            }
            else
            {
                _dialogController.ShowErrorMessage("비교 오류", "올바른 두 파일 혹은 탭을 선택해 주세요.");
            }
        }

        private async Task OnTabReloadAsync(OpenedTab tab, TabViewItem tabItem)
        {
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

        private async void OnPrintClick(object sender, RoutedEventArgs e)
        {
            if (EditorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId &&
                _editorSessions.TryGetValue(tabId, out var session) &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup) &&
                bridgeGroup.WebView.CoreWebView2 != null)
            {
                string fullText = session.GetText();
                string jsonText = System.Text.Json.JsonSerializer.Serialize(fullText);
                await bridgeGroup.WebView.CoreWebView2.ExecuteScriptAsync(
                    $"printDocument({jsonText})");
            }
        }

        private async Task PerformLineNavigationAsync(string tabId, int targetLine)
        {
            if (_tabBridges.TryGetValue(tabId, out var bridgeGroup))
            {
                if (bridgeGroup.Bridge != null)
                    await bridgeGroup.Bridge.RevealLineAsync(targetLine, 0, 0, "");
                else if (bridgeGroup.WebView?.CoreWebView2 != null)
                {
                    var msg = new { action = "revealLine", lineNumber = targetLine, indexOfMatch = 0, matchLength = 0, query = "" };
                    bridgeGroup.WebView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg));
                }
            }
        }

        private void ApplyEditorSurfaceBackground(EditorSettings settings)
        {
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            EditorWorkspace.SetEditorSurfaceBackground(editorBgColor);

            foreach (var grp in _tabBridges.Values)
            {
                WebViewAppearanceService.ApplyEditorHostBackground(grp.WebView, editorBgColor);
            }
        }

        #endregion
    }

}
