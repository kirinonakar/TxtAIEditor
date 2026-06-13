using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class EditorTabOpenController
    {
        private readonly ISettingsService _settingsService;
        private readonly ISnippetService _snippetService;
        private readonly MainWindowViewModel _viewModel;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly EditorTabDocumentFactory _editorTabDocumentFactory;
        private readonly EditorTabViewItemFactory _editorTabViewItemFactory;
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly StatusBarController _statusBarController;
        private readonly TabEncryptionController _tabEncryptionController;
        private readonly PdfViewerController _pdfViewerController;
        private readonly EditorWebViewInitializationController _editorWebViewInitializationController;
        private readonly EditorBridgeShortcutController _editorBridgeShortcutController;
        private readonly EditorBridgeDocumentController _editorBridgeDocumentController;
        private readonly EditorBridgeInteractionController _editorBridgeInteractionController;
        private readonly EditorLinkNavigationController _editorLinkNavigationController;
        private readonly TabSelectionController _tabSelectionController;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<TabView> _getCurrentActiveTabView;
        private readonly Func<OpenedTab?> _getActiveTab;
        private readonly Func<TabViewItem, TabView?> _getTabViewForTabItem;
        private readonly Func<string> _getCurrentFolderPath;
        private readonly Func<bool> _isLivePreviewEnabled;
        private readonly Func<bool> _isScrollSyncEnabled;
        private readonly Func<bool> _isCsvTableModeEnabled;
        private readonly Func<OpenedTab, string> _getPreviewBaseHref;
        private readonly Func<string, string, string> _getLocalizedString;
        private readonly Action<EditorSettings> _applyEditorSurfaceBackground;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly Action _updateWindowTitle;
        private readonly Action<OpenedTab, TabViewItem, TabView, FrameworkElement, RightTappedRoutedEventArgs> _showTabContextMenu;
        private readonly int _initialEditorLineWarmupCount;

        public EditorTabOpenController(
            ISettingsService settingsService,
            ISnippetService snippetService,
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            EditorTabDocumentFactory editorTabDocumentFactory,
            EditorTabViewItemFactory editorTabViewItemFactory,
            FavoritesRecentController favoritesRecentController,
            StatusBarController statusBarController,
            TabEncryptionController tabEncryptionController,
            PdfViewerController pdfViewerController,
            EditorWebViewInitializationController editorWebViewInitializationController,
            EditorBridgeShortcutController editorBridgeShortcutController,
            EditorBridgeDocumentController editorBridgeDocumentController,
            EditorBridgeInteractionController editorBridgeInteractionController,
            EditorLinkNavigationController editorLinkNavigationController,
            TabSelectionController tabSelectionController,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            DispatcherQueue dispatcherQueue,
            Func<TabView> getCurrentActiveTabView,
            Func<OpenedTab?> getActiveTab,
            Func<TabViewItem, TabView?> getTabViewForTabItem,
            Func<string> getCurrentFolderPath,
            Func<bool> isLivePreviewEnabled,
            Func<bool> isScrollSyncEnabled,
            Func<bool> isCsvTableModeEnabled,
            Func<OpenedTab, string> getPreviewBaseHref,
            Func<string, string, string> getLocalizedString,
            Action<EditorSettings> applyEditorSurfaceBackground,
            Action<OpenedTab> updateLanguageUi,
            Action updateWindowTitle,
            Action<OpenedTab, TabViewItem, TabView, FrameworkElement, RightTappedRoutedEventArgs> showTabContextMenu,
            int initialEditorLineWarmupCount)
        {
            _settingsService = settingsService;
            _snippetService = snippetService;
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _editorTabDocumentFactory = editorTabDocumentFactory;
            _editorTabViewItemFactory = editorTabViewItemFactory;
            _favoritesRecentController = favoritesRecentController;
            _statusBarController = statusBarController;
            _tabEncryptionController = tabEncryptionController;
            _pdfViewerController = pdfViewerController;
            _editorWebViewInitializationController = editorWebViewInitializationController;
            _editorBridgeShortcutController = editorBridgeShortcutController;
            _editorBridgeDocumentController = editorBridgeDocumentController;
            _editorBridgeInteractionController = editorBridgeInteractionController;
            _editorLinkNavigationController = editorLinkNavigationController;
            _tabSelectionController = tabSelectionController;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _dispatcherQueue = dispatcherQueue;
            _getCurrentActiveTabView = getCurrentActiveTabView;
            _getActiveTab = getActiveTab;
            _getTabViewForTabItem = getTabViewForTabItem;
            _getCurrentFolderPath = getCurrentFolderPath;
            _isLivePreviewEnabled = isLivePreviewEnabled;
            _isScrollSyncEnabled = isScrollSyncEnabled;
            _isCsvTableModeEnabled = isCsvTableModeEnabled;
            _getPreviewBaseHref = getPreviewBaseHref;
            _getLocalizedString = getLocalizedString;
            _applyEditorSurfaceBackground = applyEditorSurfaceBackground;
            _updateLanguageUi = updateLanguageUi;
            _updateWindowTitle = updateWindowTitle;
            _showTabContextMenu = showTabContextMenu;
            _initialEditorLineWarmupCount = initialEditorLineWarmupCount;
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
            tab.InlineLivePreviewEnabled = _isLivePreviewEnabled();
            var session = documentParts.Session;
            _editorSessions[tab.Id] = session;

            AddOpenTab(tab);

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _applyEditorSurfaceBackground(settings);

            var targetTabView = _getCurrentActiveTabView();
            var tabParts = _editorTabViewItemFactory.Create(
                tab,
                editorBgColor,
                settings.UiFontFamily,
                _getLocalizedString("EncryptedTabTooltip", "암호화됨"),
                _tabEncryptionController.ShowMenu,
                (tabItem, args) => _showTabContextMenu(tab, tabItem, targetTabView, tabItem, args),
                _getCurrentFolderPath());
            _tabBridges[tab.Id] = (tabParts.WebView, tabParts.Bridge);

            WireEditorBridge(
                tabParts.Bridge,
                tabParts.WebView,
                tabParts.LoadCover,
                tab,
                tabParts.TabItem,
                session,
                documentParts.IsReadOnly);

            AddTabItemToWorkspace(targetTabView, tabParts.TabItem, editorBgColor, queueSurfaceRefresh: true);
            UpdateTabStatus(tab, updateLanguageUi: false);
            QueueEditorInitialization(tab, tabParts.WebView, tabParts.Bridge);

            return tab;
        }

        public OpenedTab OpenPdfTab(string filePath)
        {
            var tab = new OpenedTab
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                Content = string.Empty,
                Language = "pdf",
                EncodingName = string.Empty,
                EncodingWasAutoDetected = false,
                IsPdfViewer = true
            };

            AddOpenTab(tab);

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _applyEditorSurfaceBackground(settings);

            var targetTabView = _getCurrentActiveTabView();
            var tabParts = _editorTabViewItemFactory.CreatePdfViewer(
                tab,
                editorBgColor,
                settings.UiFontFamily,
                _getLocalizedString("EncryptedTabTooltip", "암호화됨"),
                _tabEncryptionController.ShowMenu,
                (item, args) => _showTabContextMenu(tab, item, targetTabView, item, args),
                _getCurrentFolderPath());

            _pdfViewerController.Register(tab, tabParts.WebView);

            AddTabItemToWorkspace(targetTabView, tabParts.TabItem, editorBgColor, queueSurfaceRefresh: false);
            UpdateTabStatus(tab, updateLanguageUi: true);

            return tab;
        }

        public OpenedTab OpenImageTab(string filePath)
        {
            var tab = new OpenedTab
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                Content = string.Empty,
                Language = "image",
                EncodingName = string.Empty,
                EncodingWasAutoDetected = false,
                IsImageViewer = true
            };

            AddOpenTab(tab);

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _applyEditorSurfaceBackground(settings);

            var targetTabView = _getCurrentActiveTabView();
            var tabItem = _editorTabViewItemFactory.CreateImageViewer(
                tab,
                editorBgColor,
                settings.UiFontFamily,
                _getLocalizedString("EncryptedTabTooltip", "암호화됨"),
                _tabEncryptionController.ShowMenu,
                (item, args) => _showTabContextMenu(tab, item, targetTabView, item, args),
                _getCurrentFolderPath());

            AddTabItemToWorkspace(targetTabView, tabItem, editorBgColor, queueSurfaceRefresh: false);
            UpdateTabStatus(tab, updateLanguageUi: true);

            return tab;
        }

        private void AddOpenTab(OpenedTab tab)
        {
            _viewModel.Tabs.Add(tab);
            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
            {
                _favoritesRecentController.AddRecentFile(tab.FilePath);
            }
        }

        private void AddTabItemToWorkspace(
            TabView targetTabView,
            TabViewItem tabItem,
            Windows.UI.Color editorBgColor,
            bool queueSurfaceRefresh)
        {
            _editorWorkspace.DisableTabItemTransitions();
            targetTabView.TabItems.Add(tabItem);
            targetTabView.SelectedItem = tabItem;
            _editorWorkspace.SetEditorSurfaceBackground(editorBgColor);

            if (!queueSurfaceRefresh)
            {
                return;
            }

            _editorWorkspace.DisableTabItemTransitions();
            _dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    _editorWorkspace.SetEditorSurfaceBackground(
                        WebViewAppearanceService.ResolveEditorBackgroundColor(_settingsService.CurrentSettings));
                    _editorWorkspace.DisableTabItemTransitions();
                });
        }

        private void UpdateTabStatus(OpenedTab tab, bool updateLanguageUi)
        {
            _statusBarController.UpdateFileStats(tab);
            _statusBarController.UpdateTotalLines(tab);
            _statusBarController.UpdateSelectionStats(null);
            _statusBarController.SyncEncodingCombo(tab);
            _statusBarController.SyncLineEndingText(tab);

            if (updateLanguageUi)
            {
                _updateLanguageUi(tab);
            }

            _updateWindowTitle();
        }

        private void QueueEditorInitialization(OpenedTab tab, WebView2 webView, MonacoBridge bridge)
        {
            bool initQueued = _dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Normal,
                () =>
                {
                    if (_tabBridges.ContainsKey(tab.Id))
                    {
                        InitializeEditorWebView(webView, bridge);
                    }
                });

            if (!initQueued)
            {
                InitializeEditorWebView(webView, bridge);
            }
        }

        private async void InitializeEditorWebView(WebView2 webView, MonacoBridge bridge)
        {
            await _editorWebViewInitializationController.InitializeAsync(webView, bridge);
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
                _dispatcherQueue.TryEnqueue(() =>
                {
                    var ownerTabView = _getTabViewForTabItem(tabItem);
                    if (ownerTabView != null)
                    {
                        if (_editorWorkspace.ActiveTabView != ownerTabView || !ReferenceEquals(ownerTabView.SelectedItem, tabItem))
                        {
                            _editorWorkspace.ActiveTabView = ownerTabView;
                            ownerTabView.SelectedItem = tabItem;
                            _tabSelectionController.QueueChanged(ownerTabView, tabItem);
                        }
                    }
                });
            };

            bridge.ShortcutPressed += shortcutName =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    _editorBridgeShortcutController.Handle(shortcutName, bridge, tab, tabItem, getSession());
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
                    currentSession.GetLines(1, _initialEditorLineWarmupCount));
            };

            bridge.EditorRendered += () =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    editorWebView.Opacity = 1;
                    editorLoadCover.Visibility = Visibility.Collapsed;
                    if (_getActiveTab() == tab)
                    {
                        editorWebView.Focus(FocusState.Programmatic);
                        _ = bridge.FocusAsync();
                    }
                });

                _ = ApplyDeferredEditorStateAsync(bridge, tab);
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
                    Debug.WriteLine($"Failed to send editor lines: {ex.Message}");
                }
            };

            bridge.LineChanged += async (lineNumber, text, isComposing) =>
            {
                await _editorBridgeDocumentController.HandleLineChangedAsync(
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber,
                    text,
                    isComposing);
            };

            bridge.LineInsertRequested += async (lineNumber, text) =>
            {
                await _editorBridgeDocumentController.HandleLineInsertRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber,
                    text);
            };

            bridge.LineSplitRequested += async (lineNumber, before, after) =>
            {
                await _editorBridgeDocumentController.HandleLineSplitRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber,
                    before,
                    after);
            };

            bridge.MergeLineWithPreviousRequested += async lineNumber =>
            {
                await _editorBridgeDocumentController.HandleMergeLineWithPreviousRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber);
            };

            bridge.DeleteLineRequested += async lineNumber =>
            {
                await _editorBridgeDocumentController.HandleDeleteLineRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber);
            };

            bridge.EditTransactionStarted += () =>
            {
                getSession().BeginUndoGroup();
            };

            bridge.EditTransactionEnded += () =>
            {
                getSession().EndUndoGroup();
            };

            bridge.FindRequested += async (query, startLine, startColumn, reverse, matchCase, isRegex) =>
            {
                await _editorBridgeDocumentController.HandleFindRequestedAsync(
                    bridge,
                    getSession(),
                    query,
                    startLine,
                    startColumn,
                    reverse,
                    matchCase,
                    isRegex);
            };

            bridge.FindAllRequested += async (query, matchCase, isRegex) =>
            {
                await _editorBridgeDocumentController.HandleFindAllRequestedAsync(
                    bridge,
                    getSession(),
                    query,
                    matchCase,
                    isRegex);
            };

            bridge.ReplaceAllRequested += async (query, replace, matchCase, isRegex) =>
            {
                await _editorBridgeDocumentController.HandleReplaceAllRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    query,
                    replace,
                    matchCase,
                    isRegex);
            };

            bridge.ContentChanged += isComposing =>
            {
                _editorBridgeDocumentController.HandleContentChanged(tab, tabItem, isComposing);
            };

            bridge.CursorChanged += (line, col) =>
            {
                _editorBridgeInteractionController.HandleCursorChanged(bridge, tab, line, col);
            };

            bridge.SelectionReceived += (selectedText, selStartLine, selEndLine) =>
            {
                _editorBridgeInteractionController.HandleSelectionReceived(
                    tab,
                    selectedText,
                    selStartLine,
                    selEndLine);
            };

            bridge.ScrollChanged += (firstLine, offset) =>
            {
                _editorBridgeInteractionController.HandleScrollChanged(tab, firstLine, offset);
            };

            bridge.ScrollSyncChanged += enabled =>
            {
                _editorBridgeInteractionController.HandleScrollSyncChanged(enabled);
            };

            bridge.CtrlClicked += (text, isUrl, isPath) =>
            {
                _dispatcherQueue.TryEnqueue(async () =>
                {
                    await _editorLinkNavigationController.HandleCtrlClickAsync(text, isUrl, isPath);
                });
            };
        }

        private async Task ApplyDeferredEditorStateAsync(MonacoBridge bridge, OpenedTab tab)
        {
            try
            {
                await bridge.UpdateScrollSyncStateAsync(_isScrollSyncEnabled());
                await bridge.SetInlineLivePreviewAsync(_isLivePreviewEnabled(), _getPreviewBaseHref(tab));
                await bridge.SetCsvTableModeAsync(_isCsvTableModeEnabled());

                var snippets = _snippetService.GetSnippets();
                if (snippets.Count > 0)
                {
                    await bridge.UpdateSnippetsAsync(snippets);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Deferred editor state sync failed: {ex.Message}");
            }
        }
    }
}
