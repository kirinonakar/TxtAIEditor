using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly PdfTextExtractionService _pdfTextExtractionService;
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly CompareSelectionDialogService _compareSelectionDialogService;
        private readonly SearchReplaceController _searchReplaceController;
        private readonly SearchReplaceTabSyncController _searchReplaceTabSyncController;
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
        private readonly TabSelectionController _tabSelectionController;
        private readonly EditorSplitLayoutController _editorSplitLayoutController;
        private readonly EditorBridgeShortcutController _editorBridgeShortcutController;
        private readonly EditorBridgeDocumentController _editorBridgeDocumentController;
        private readonly EditorBridgeInteractionController _editorBridgeInteractionController;
        private readonly EditorLinkNavigationController _editorLinkNavigationController;
        private readonly EditorWebViewInitializationController _editorWebViewInitializationController;
        private readonly EditorLineNavigationController _editorLineNavigationController;
        private readonly EditorTabOpenController _editorTabOpenController;
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
        private readonly UnsavedChangesDialogService _unsavedChangesDialogService;
        private readonly WindowDialogController _dialogController;
        private readonly WindowCloseController _windowCloseController;
        private readonly WindowTitleController _windowTitleController;
        private readonly MainWindowSettingsController _settingsController;
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
        private bool _csvTableModeEnabled = false;
        private bool _livePreviewEnabled = false;
        
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

        public MainWindow()
        {
            this.InitializeComponent();
            WindowPlacementService.SetWindowIcon(AppWindow);

            // Start pre-warming the shared WebView2 environment in the background
            _ = TxtAIEditor.Editor.MonacoBridge.GetSharedEnvironmentAsync();

            _fileService = new FileService();
            _settingsService = new SettingsService();
            _credentialService = new CredentialService();
            _localizationService = new ResourceLocalizationService(_settingsService);
            _llmService = new LLMService(_settingsService, _credentialService, _localizationService);
            _gitService = new GitService();
            _snippetService = new SnippetService();
            _languageDetectionService = new LanguageDetectionService();
            _recentFilesService = new RecentFilesService();
            _fileSearchService = new FileSearchService(_fileService);
            _stickyNoteService = new StickyNoteService();
            _settingsDialogService = new SettingsDialogService(_llmService);
            _uiPersonalizationService = new UiPersonalizationService();
            _pdfTextExtractionService = new PdfTextExtractionService();
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
            _windowTitleController = new WindowTitleController(
                this,
                AppTitleTextBlock,
                GetActiveTab);
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
                _languageDetectionService,
                _tabBridges,
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
                _dialogController.ShowErrorMessage,
                GetLocalizedString);
            _editorWebViewInitializationController = new EditorWebViewInitializationController(
                _settingsService,
                _livePreviewController);
            _editorLineNavigationController = new EditorLineNavigationController(
                _viewModel,
                _tabBridges);
            _pdfViewerController = new PdfViewerController(
                _settingsService,
                GetActiveTab,
                UpdateRightPanelSelectionContext);
            _editorLinkNavigationController = new EditorLinkNavigationController(
                GetActiveTab,
                NavigateExplorerToFolderAndRevealAsync);
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
            _tabDirtyStateController = new TabDirtyStateController(
                _viewModel,
                _tabBridges,
                _editorSessions,
                UpdateWindowTitle);
            _editorBridgeShortcutController = new EditorBridgeShortcutController(
                ToggleLivePreview,
                _stickyNoteModeController.ToggleTopMostFromShortcut,
                () => OnToggleThemeClick(this, new RoutedEventArgs()),
                ToggleMaximize,
                _stickyNoteModeController.ToggleMode,
                ToggleLeftPanelAsync,
                ToggleRightPanelAsync,
                TogglePreviewWidth,
                () => OpenNewTab(),
                () => OnSaveFileClick(this, new RoutedEventArgs()),
                () => OnOpenFileClick(this, new RoutedEventArgs()),
                _terminalShortcutService.RequestToggle,
                () => OnCloseActiveTabShortcutInvoked(null!, null!),
                () => OnPrintClick(this, new RoutedEventArgs()),
                FocusSearchPanel,
                _tabDirtyStateController,
                SchedulePreview,
                tab => SyncEditsToOtherTabsAsync(tab));
            _searchReplaceTabSyncController = new SearchReplaceTabSyncController(
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                _editorSessions,
                _tabDirtyStateController,
                GetActiveTab,
                LoadFileIntoTabAsync,
                UpdateLivePreview,
                _editorLineNavigationController);
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
                _searchReplaceTabSyncController.LoadAndHighlightAsync,
                RefreshGitStatusUIAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); });
            _searchReplaceController.FileModified += _searchReplaceTabSyncController.HandleFileModifiedAsync;
            _splitImeSyncController = new SplitImeSyncController(
                _tabBridges,
                _editorSessions,
                _tabDirtyStateController.GetTabsForSameFile,
                SchedulePreview,
                _tabDirtyStateController.SetDirtyStateForFileGroup);
            _functionKeyShortcutService = new FunctionKeyShortcutService(WindowNative.GetWindowHandle(this));
            _functionKeyShortcutService.TopMostRequested += (_, _) => _stickyNoteModeController.ToggleTopMostFromShortcut();
            _functionKeyShortcutService.ThemeRequested += (_, _) => OnToggleThemeClick(this, new RoutedEventArgs());
            _functionKeyShortcutService.StickyNoteRequested += (_, _) => _stickyNoteModeController.ToggleMode();
            _gitAutoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _gitPanelController = new GitPanelController(
                _gitService,
                _fileService,
                _viewModel,
                LeftSidebarTabView,
                StatusGitBranch,
                GetCurrentRepoPathForGitRefresh,
                () => this.Content.XamlRoot,
                GetLocalizedString,
                IsGitNotDetectedText,
                _dialogController.ShowErrorMessage,
                () => _gitAutoRefreshTimer.Start(),
                _compareTabController.OpenCompareTabAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); },
                refreshExplorerGitStatus: async () =>
                {
                    if (_explorerNavigationController != null)
                    {
                        await _explorerNavigationController.UpdateGitStatusesAsync();
                    }
                });
            _gitPanelController.FileRestored += OnGitFileRestored;
            _gitStatusRefreshController = new GitStatusRefreshController(
                DispatcherQueue,
                _gitAutoRefreshTimer,
                GetCurrentRepoPathForGitRefresh,
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
                OpenImageTab,
                OpenPdfTab,
                QueueGitStatusRefresh,
                _dialogController.ShowErrorMessage);
            _explorerNavigationController = new ExplorerNavigationController(
                LeftSidebarTabView,
                _viewModel,
                _explorerDirectoryService,
                _gitService,
                InitializePickerWindow,
                path => CurrentFolderPath = path,
                path => CurrentRepoPath = path,
                RefreshGitStatusUIAsync,
                EnsureLeftPanelVisible,
                ShowLeftSidebarPage,
                LoadFileIntoTabAsync,
                _localizationService,
                () => _settingsService.CurrentSettings.HomeFolderPath);
            _favoritesRecentController = new FavoritesRecentController(
                _settingsService,
                _recentFilesService,
                _viewModel,
                LeftSidebarTabView,
                callback => DispatcherQueue.TryEnqueue(() => callback()),
                NavigateExplorerToFolderAndRevealAsync,
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
                _tabDirtyStateController.CleanDirtyStateOnOtherTabs,
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
                GetSearchRoot,
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
                CloseReadOnlyViewer,
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
                GetActiveTab,
                LoadDirectoryRoot,
                LoadFileIntoTabAsync,
                InsertTextIntoActiveEditorAsync,
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
                NavigateExplorerToFolderAndRevealAsync,
                OnTabReloadAsync,
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
                (folderPath, revealInLeftPanel) => NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel),
                () => _shellPanelLayoutService.IsLeftSidebarVisible,
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
                IsActiveTabPdfViewer,
                _stickyNoteModeController.ToggleTopMostFromShortcut,
                () => OnToggleThemeClick(this, new RoutedEventArgs()),
                _stickyNoteModeController.ToggleMode,
                _terminalShortcutService,
                ToggleLivePreview,
                TogglePreviewWidth,
                ToggleMaximize);
            _terminalPanelController = new TerminalPanelController(
                this,
                EditorWorkspace,
                TopToolbar,
                () => FileListView.SelectedItem as ExplorerItem,
                () => _currentFolderPath,
                () => _currentRepoPath,
                async (filePath, line) => await LoadFileIntoTabAsync(filePath, line),
                NavigateExplorerToFolderAndRevealAsync);
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
                (title, content) =>
                {
                    string uniqueTitle = string.IsNullOrWhiteSpace(title) ? GetLocalizedString("UntitledNewTab", "제목 없음") : title;
                    var tab = OpenNewTab(null, content);
                    tab.Title = uniqueTitle;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        tab.Language = _languageDetectionService.GetMonacoLanguageName(title);
                    }
                    else
                    {
                        tab.Language = "plaintext";
                    }

                    tab.OriginalContent = string.Empty;
                    _tabDirtyStateController.MarkTabDirty(tab);
                    UpdateWindowTitle();
                    return tab;
                },
                _dialogController.ShowErrorMessage,
                GetLocalizedString,
                InitializePickerWindow,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); },
                onFileSaved: () =>
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        _explorerNavigationController.RefreshCurrentFolder();
                    });
                });
            _agentFileWorkflowController = new AgentFileWorkflowController(
                _viewModel,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                _editorSessions,
                _tabCloseController,
                _searchReplaceTabSyncController,
                _compareTabController,
                GetAgentSessionEdits,
                () => FileListView.SelectedItem as ExplorerItem,
                () => _currentFolderPath,
                () => _currentRepoPath,
                LoadDirectoryRoot,
                QueueGitStatusRefresh,
                GetLocalizedString);
            _agentController = new AgentController(
                _llmService,
                _settingsService,
                PreviewGrid.AgentPane,
                GetActiveTab,
                () => _viewModel.Tabs.ToList(),
                GetTabTextForLlmContext,
                InsertTextIntoActiveEditorAsync,
                (title, content) =>
                {
                    string uniqueTitle = string.IsNullOrWhiteSpace(title) ? GetLocalizedString("UntitledNewTab", "제목 없음") : title;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        string extension = "";
                        string baseName = title;
                        int lastDot = title.LastIndexOf('.');
                        if (lastDot >= 0)
                        {
                            baseName = title.Substring(0, lastDot);
                            extension = title.Substring(lastDot);
                        }

                        int counter = 1;
                        while (_viewModel.Tabs.Any(t =>
                            string.IsNullOrEmpty(t.FilePath) &&
                            string.Equals(t.Title, uniqueTitle, StringComparison.OrdinalIgnoreCase)))
                        {
                            counter++;
                            uniqueTitle = $"{baseName} ({counter}){extension}";
                        }
                    }

                    var tab = OpenNewTab(null, content);
                    tab.Title = uniqueTitle;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        tab.Language = _languageDetectionService.GetMonacoLanguageName(title);
                    }
                    else
                    {
                        tab.Language = "plaintext";
                    }

                    tab.OriginalContent = string.Empty;
                    _tabDirtyStateController.MarkTabDirty(tab);
                    UpdateWindowTitle();
                    return tab;
                },
                _dialogController.ShowErrorMessage,
                GetLocalizedString,
                new AgentFileToolService(_agentFileWorkflowController.GetWorkspaceRoot, GetLocalizedString),
                _pdfTextExtractionService,
                InitializePickerWindow,
                path => _gitService.FindRepositoryRoot(path) != null,
                _agentFileWorkflowController.OpenDiffViewAsync,
                _agentFileWorkflowController.HandleFileModifiedAsync,
                openFileInEditorAsync: LoadFileIntoTabForAgentAsync,
                beforeDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (EditorWorkspace.IsTerminalVisible) TerminalPane.ResumeNativeWindows(); },
                revertTabOrFileAsync: _agentFileWorkflowController.RevertTabOrFileAsync,
                closeTabById: _agentFileWorkflowController.CloseTabById,
                navigateToFolderAsync: NavigateExplorerToFolderAndRevealAsync,
                saveTabAsync: async (tab, targetPath) =>
                {
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        tab.FilePath = targetPath;
                        tab.Title = Path.GetFileName(targetPath);
                        tab.Language = _languageDetectionService.GetMonacoLanguageName(targetPath);
                    }
                    return await SaveTabAsync(tab);
                },
                editTabAsync: async (tab, newContent) =>
                {
                    tab.Content = newContent;
                    if (_editorSessions.TryGetValue(tab.Id, out var session))
                    {
                        session.UpdateContentFromSync(newContent);
                    }
                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        await bridgeGroup.Bridge.SetTextAsync(newContent, shouldFocus: false);
                    }
                    _tabDirtyStateController.MarkTabDirty(tab);
                    UpdateWindowTitle();
                    return true;
                });
            _tocController = new TocController(
                _viewModel,
                LeftSidebarTabView,
                GetActiveTab,
                tab => _editorSessions.TryGetValue(tab.Id, out var s) ? s : null,
                () => PreviewModeCombo.SelectedIndex == 3,
                async targetLine =>
                {
                    var activeTab = GetActiveTab();
                    if (activeTab != null)
                    {
                        await _editorLineNavigationController.RevealTabLineAsync(activeTab.Id, targetLine);
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
            _editorBridgeInteractionController = new EditorBridgeInteractionController(
                EditorWorkspace,
                EditorTabView,
                EditorTabView2,
                _tabBridges,
                DispatcherQueue,
                GetActiveTab,
                _statusBarController,
                _tabSelectionController,
                _livePreviewController,
                UpdateRightPanelSelectionContext,
                () => _scrollSyncEnabled,
                enabled => _scrollSyncEnabled = enabled);
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
                _editorWebViewInitializationController,
                _editorBridgeShortcutController,
                _editorBridgeDocumentController,
                _editorBridgeInteractionController,
                _editorLinkNavigationController,
                _tabSelectionController,
                _tabBridges,
                _editorSessions,
                DispatcherQueue,
                GetCurrentActiveTabView,
                GetActiveTab,
                GetTabViewForTabItem,
                () => _currentFolderPath,
                () => _livePreviewEnabled,
                () => _scrollSyncEnabled,
                () => _csvTableModeEnabled,
                GetPreviewBaseHref,
                GetLocalizedString,
                ApplyEditorSurfaceBackground,
                UpdateLanguageUI,
                UpdateWindowTitle,
                ShowTabContextMenu,
                InitialEditorLineWarmupCount);
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
                StickyNoteBar,
                LeftSplitter,
                RightSplitter,
                _tabBridges,
                _pdfViewerController,
                _statusBarController,
                _livePreviewController,
                _llmAssistantController,
                _agentController,
                GetActiveTab,
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
                ApplyUiPersonalization,
                LocalizeUi,
                ApplyToolbarSettings,
                RefreshGitStatusUIAsync,
                UpdateAutoSaveStatus,
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

            PreviewGrid.ModelNameClick += OnModelNameClick;
            PreviewGrid.AgentPane.ModelNameClick += OnModelNameClick;
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to prepare initial window placement: {ex.Message}");
            }
        }

        private void WireLeftSidebarEvents()
        {
            LeftSidebarTabView.SearchQueryInputKeyDown += OnSearchQueryInputKeyDown;
            LeftSidebarTabView.SearchAllFilesClick += OnSearchAllFilesClick;
            LeftSidebarTabView.ReplaceAllClick += OnReplaceAllClick;
            LeftSidebarTabView.ReplaceOneClick += OnReplaceOneClick;
            LeftSidebarTabView.SearchResultItemClick += OnSearchResultItemClick;
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
            TopToolbar.ToggleLivePreviewClick += OnToggleLivePreviewClick;
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

        private async void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            await _fileOpenDropController.OpenFileAsync();
        }

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
            var tabView = GetTabViewForTab(tab);
            var tabItem = tabView != null ? FindTabItem(tabView, tab.Id) : null;

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

            await _settingsController.ApplySettingsToOpenEditorsAsync(settings);
        }

        private async void OnFindClick(object sender, RoutedEventArgs e)
        {
            if (await _pdfViewerController.FocusFindInActiveViewerAsync())
            {
                return;
            }

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

        private bool IsActiveTabPdfViewer()
        {
            return _pdfViewerController.IsActiveViewer();
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

        private void ToggleLivePreview()
        {
            TopToolbar.LivePreviewIsChecked = !TopToolbar.LivePreviewIsChecked;
            OnToggleLivePreviewClick(this, new RoutedEventArgs());
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

        private async void OnToggleThemeClick(object sender, RoutedEventArgs e)
        {
            await _settingsController.ToggleThemeAsync();
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

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            await _settingsController.ShowSettingsAsync();
        }

        private async void OnModelNameClick(object sender, RoutedEventArgs e)
        {
            await _settingsController.ShowSettingsAsync("LLM");
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

        private IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits()
        {
            return _agentController.SessionEdits;
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

        private void CloseReadOnlyViewer(string tabId)
        {
            _pdfViewerController.Close(tabId);
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
            else if (string.Equals(name, "f4", StringComparison.Ordinal))
            {
                ToggleLivePreview();
            }
            else if (string.Equals(name, "f9", StringComparison.Ordinal))
            {
                _stickyNoteModeController.ToggleTopMostFromShortcut();
            }
            else if (string.Equals(name, "f10", StringComparison.Ordinal))
            {
                OnToggleThemeClick(this, new RoutedEventArgs());
            }
            else if (string.Equals(name, "f11", StringComparison.Ordinal))
            {
                ToggleMaximize();
            }
            else if (string.Equals(name, "f12", StringComparison.Ordinal))
            {
                _stickyNoteModeController.ToggleMode();
            }
            else if (string.Equals(name, "print", StringComparison.Ordinal))
            {
                OnPrintClick(this, new RoutedEventArgs());
            }
            else if (string.Equals(name, "expandRightPanel", StringComparison.Ordinal))
            {
                TogglePreviewWidth();
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
            return FindTabItem(tabView, tabId) != null;
        }

        private static TabViewItem? FindTabItem(TabView tabView, string tabId)
        {
            foreach (var item in tabView.TabItems)
            {
                if (item is TabViewItem tvi && string.Equals(tvi.Tag as string, tabId, StringComparison.Ordinal))
                {
                    return tvi;
                }
            }

            return null;
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

        private async void OnToggleLivePreviewClick(object sender, RoutedEventArgs e)
        {
            _livePreviewEnabled = TopToolbar.LivePreviewIsChecked;

            foreach (var tab in _viewModel.Tabs)
            {
                tab.InlineLivePreviewEnabled = _livePreviewEnabled;
                await ApplyInlineLivePreviewAsync(tab);
            }
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

            try
            {
                bridgeGroup.WebView.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to focus editor: {ex.Message}");
            }

            await bridgeGroup.Bridge.InsertTextAsync(text);

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                _tabDirtyStateController.MarkTabDirty(tab, activeTabItem);
                _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
            }

            return true;
        }

        private string GetTabTextForLlmContext(OpenedTab tab, int maxChars)
        {
            if (tab.IsPdfViewer && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                string cached = tab.Content ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return cached.Length > maxChars ? cached.Substring(0, maxChars) : cached;
                }

                string extracted = _pdfTextExtractionService
                    .ExtractTextAsync(tab.FilePath, maxChars)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                tab.Content = extracted;
                return extracted;
            }

            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                return session.GetText(maxChars);
            }

            return tab.Content ?? string.Empty;
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
                string? activeFilePath = GetActiveTab()?.FilePath;
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

        private void ApplyPreviewVisibility(bool show)
        {
            _shellPaneController.ApplyPreviewVisibility(show);
            if (show && _startupInitializationComplete)
            {
                _ = _livePreviewController.InitializeAsync();
            }
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
            if (_pdfViewerController.Reload(tab))
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
            await _editorLineNavigationController.RevealTabLineAsync(tabId, targetLine);
        }

        #endregion
    }

}
