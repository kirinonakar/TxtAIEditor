using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        private readonly OfficeDocumentViewerController _officeDocumentViewerController;
        private readonly EditorWebViewInitializationController _editorWebViewInitializationController;
        private readonly EditorBridgeShortcutController _editorBridgeShortcutController;
        private readonly EditorBridgeDocumentController _editorBridgeDocumentController;
        private readonly EditorBridgeInteractionController _editorBridgeInteractionController;
        private readonly EditorLinkNavigationController _editorLinkNavigationController;
        private readonly TabSelectionController _tabSelectionController;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Dictionary<string, HexViewState> _hexViewStates = new();
        private readonly Dictionary<string, int> _viewModeTransitionVersions = new();
        private int _viewModeTransitionVersion;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<TabView> _getCurrentActiveTabView;
        private readonly Func<OpenedTab?> _getActiveTab;
        private readonly Func<TabViewItem, TabView?> _getTabViewForTabItem;
        private readonly Func<string> _getCurrentFolderPath;
        private readonly Func<bool> _isLivePreviewEnabled;
        private readonly Func<bool> _isScrollSyncEnabled;
        private readonly Func<OpenedTab, string> _getPreviewBaseHref;
        private readonly Func<string, string, string> _getLocalizedString;
        private readonly Action<EditorSettings> _applyEditorSurfaceBackground;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly Action _updateWindowTitle;
        private readonly Action<OpenedTab, TabViewItem, TabView, FrameworkElement, RightTappedRoutedEventArgs> _showTabContextMenu;
        private readonly int _initialEditorLineWarmupCount;

        private sealed record HexViewState(
            EditorDocumentSession TextSession,
            HexDumpTextModel InitialHexModel,
            string Language,
            bool IsLanguageManuallySelected,
            string EncodingName,
            bool EncodingWasAutoDetected,
            bool InlineLivePreviewEnabled,
            string OriginalContent,
            string? OriginalLineEnding,
            string? OriginalEncodingName,
            string? HexSourceFilePath);

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
            OfficeDocumentViewerController officeDocumentViewerController,
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
            _officeDocumentViewerController = officeDocumentViewerController;
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
            var session = documentParts.Session;
            OpenEditorDocumentTab(tab, session, documentParts.IsReadOnly, updateLanguageUi: false);

            return tab;
        }

        public OpenedTab OpenHexTab(string filePath)
        {
            var model = new HexDumpTextModel(filePath);
            var tab = new OpenedTab
            {
                FilePath = filePath,
                HexSourceFilePath = filePath,
                Title = Path.GetFileName(filePath),
                Content = string.Empty,
                Language = "hex",
                EncodingName = string.Empty,
                EncodingWasAutoDetected = false,
                IsHexViewer = true
            };

            var session = new EditorDocumentSession(tab, model);
            tab.OriginalContent = tab.Content;
            tab.OriginalLineEnding = model.LineEnding;
            tab.OriginalEncodingName = tab.EncodingName;

            OpenEditorDocumentTab(tab, session, isReadOnly: true, updateLanguageUi: true);
            return tab;
        }

        public async Task SetHexViewModeAsync(OpenedTab tab, bool enabled)
        {
            if (enabled == tab.IsHexViewer)
            {
                return;
            }

            if (enabled)
            {
                await EnableHexViewModeAsync(tab);
            }
            else
            {
                await DisableHexViewModeAsync(tab);
            }
        }

        private async Task EnableHexViewModeAsync(OpenedTab tab)
        {
            string? sourcePath = tab.FilePath;
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !File.Exists(sourcePath) ||
                tab.IsEncrypted ||
                tab.IsImageViewer ||
                tab.IsMediaViewer ||
                tab.IsPdfViewer ||
                tab.IsDocxViewer ||
                tab.IsOfficeDocumentViewer ||
                !_editorSessions.TryGetValue(tab.Id, out var textSession) ||
                textSession.Model is HexDumpTextModel)
            {
                return;
            }

            if (_tabBridges.TryGetValue(tab.Id, out var existingBridgeGroup) && existingBridgeGroup.Bridge != null)
            {
                await existingBridgeGroup.Bridge.FlushPendingEditForSaveAsync();
            }

            byte[] sourceBytes = EncodeText(textSession.GetText(), tab.EncodingName);
            var hexModel = new HexDumpTextModel(sourcePath, sourceBytes);
            _hexViewStates[tab.Id] = new HexViewState(
                textSession,
                hexModel,
                tab.Language,
                tab.IsLanguageManuallySelected,
                tab.EncodingName,
                tab.EncodingWasAutoDetected,
                tab.InlineLivePreviewEnabled,
                tab.OriginalContent,
                tab.OriginalLineEnding,
                tab.OriginalEncodingName,
                tab.HexSourceFilePath);

            tab.HexSourceFilePath = sourcePath;
            tab.IsHexViewer = true;
            tab.IsCsvTableModeEnabled = false;
            tab.InlineLivePreviewEnabled = false;
            tab.Language = "hex";

            var hexSession = new EditorDocumentSession(tab, hexModel);
            _editorSessions[tab.Id] = hexSession;
            await ApplyViewModeToBridgeAsync(tab, hexSession, isReadOnly: true);
            UpdateTabStatus(tab, updateLanguageUi: true);
        }

        private async Task DisableHexViewModeAsync(OpenedTab tab)
        {
            if (!_hexViewStates.Remove(tab.Id, out var state) ||
                !_editorSessions.TryGetValue(tab.Id, out var hexSession) ||
                hexSession.Model is not HexDumpTextModel hexModel)
            {
                return;
            }

            tab.IsHexViewer = false;
            tab.HexSourceFilePath = state.HexSourceFilePath;
            tab.Language = state.Language;
            tab.IsLanguageManuallySelected = state.IsLanguageManuallySelected;
            tab.EncodingName = state.EncodingName;
            tab.EncodingWasAutoDetected = state.EncodingWasAutoDetected;
            tab.InlineLivePreviewEnabled = state.InlineLivePreviewEnabled;

            EditorDocumentSession restoredSession;
            if (!ReferenceEquals(hexModel, state.InitialHexModel) || hexModel.HasEverBeenEdited)
            {
                string restoredText = DecodeText(hexModel.GetCurrentBytes(), state.EncodingName);
                restoredSession = new EditorDocumentSession(tab, LineArrayTextModel.FromText(restoredText));
            }
            else
            {
                restoredSession = state.TextSession;
                restoredSession.RefreshTabContentPreview();
            }

            _editorSessions[tab.Id] = restoredSession;
            if (tab.IsDirty)
            {
                tab.OriginalContent = state.OriginalContent;
                tab.OriginalLineEnding = state.OriginalLineEnding;
                tab.OriginalEncodingName = state.OriginalEncodingName;
            }
            else
            {
                tab.OriginalContent = restoredSession.GetText();
                tab.OriginalLineEnding = restoredSession.Model.LineEnding;
                tab.OriginalEncodingName = tab.EncodingName;
            }

            await ApplyViewModeToBridgeAsync(tab, restoredSession, tab.IsReadOnlyTextFile);
            UpdateTabStatus(tab, updateLanguageUi: true);
        }

        public void ForgetHexViewState(string tabId)
        {
            _hexViewStates.Remove(tabId);
            _viewModeTransitionVersions.Remove(tabId);
        }

        private async Task ApplyViewModeToBridgeAsync(
            OpenedTab tab,
            EditorDocumentSession session,
            bool isReadOnly)
        {
            if (!_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) || bridgeGroup.Bridge == null)
            {
                return;
            }

            int transitionVersion = BeginViewModeTransition(tab.Id);
            try
            {
                await bridgeGroup.Bridge.InitializeModelAsync(
                    session.Model.LineCount,
                    tab.Language,
                    _settingsService.CurrentSettings,
                    isReadOnly,
                    session.GetLines(1, _initialEditorLineWarmupCount));
                await bridgeGroup.Bridge.SetCsvTableModeAsync(tab.IsCsvTableModeEnabled);
                await bridgeGroup.Bridge.SetInlineLivePreviewAsync(
                    tab.InlineLivePreviewEnabled,
                    _getPreviewBaseHref(tab));
            }
            catch
            {
                CompleteViewModeTransition(tab.Id, transitionVersion);
                throw;
            }
        }

        private int BeginViewModeTransition(string tabId)
        {
            int version = ++_viewModeTransitionVersion;
            _viewModeTransitionVersions[tabId] = version;
            _ = ClearViewModeTransitionFallbackAsync(tabId, version);
            return version;
        }

        private void CompleteViewModeTransition(string tabId, int? expectedVersion = null)
        {
            if (!expectedVersion.HasValue ||
                (_viewModeTransitionVersions.TryGetValue(tabId, out int version) && version == expectedVersion.Value))
            {
                _viewModeTransitionVersions.Remove(tabId);
            }
        }

        private async Task ClearViewModeTransitionFallbackAsync(string tabId, int version)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            _dispatcherQueue.TryEnqueue(() => CompleteViewModeTransition(tabId, version));
        }

        private static byte[] EncodeText(string text, string encodingName)
        {
            Encoding encoding = TextEncodingService.GetEncodingByName(encodingName);
            byte[] preamble = encoding.GetPreamble();
            byte[] content = encoding.GetBytes(text);
            if (preamble.Length == 0)
            {
                return content;
            }

            byte[] bytes = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);
            return bytes;
        }

        private static string DecodeText(byte[] bytes, string encodingName)
        {
            Encoding encoding = TextEncodingService.GetEncodingByName(encodingName);
            byte[] preamble = encoding.GetPreamble();
            int offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)
                ? preamble.Length
                : 0;
            return encoding.GetString(bytes, offset, bytes.Length - offset);
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

            _pdfViewerController.Register(tab, tabParts.WebView, tabParts.FindControl!);

            AddTabItemToWorkspace(targetTabView, tabParts.TabItem, editorBgColor, queueSurfaceRefresh: false);
            UpdateTabStatus(tab, updateLanguageUi: true);

            return tab;
        }

        public OpenedTab OpenOfficeDocumentTab(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            var tab = new OpenedTab
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                Content = string.Empty,
                Language = extension.TrimStart('.'),
                EncodingName = string.Empty,
                EncodingWasAutoDetected = false,
                IsOfficeDocumentViewer = true
            };

            AddOpenTab(tab);

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _applyEditorSurfaceBackground(settings);

            var targetTabView = _getCurrentActiveTabView();
            var tabParts = _editorTabViewItemFactory.CreateOfficeDocumentViewer(
                tab,
                editorBgColor,
                settings.UiFontFamily,
                _getLocalizedString("EncryptedTabTooltip", "암호화됨"),
                _tabEncryptionController.ShowMenu,
                (item, args) => _showTabContextMenu(tab, item, targetTabView, item, args),
                _getCurrentFolderPath());

            _officeDocumentViewerController.Register(tab, tabParts.WebView);

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

        public OpenedTab OpenMediaTab(string filePath)
        {
            var tab = new OpenedTab
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                Content = string.Empty,
                Language = SupportedFileTypes.IsAudioFile(filePath) ? "audio" : "video",
                EncodingName = string.Empty,
                EncodingWasAutoDetected = false,
                IsMediaViewer = true
            };

            AddOpenTab(tab);

            var settings = _settingsService.CurrentSettings;
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _applyEditorSurfaceBackground(settings);

            var targetTabView = _getCurrentActiveTabView();
            var tabItem = _editorTabViewItemFactory.CreateMediaViewer(
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
            if (!string.IsNullOrEmpty(tab.FilePath) &&
                File.Exists(tab.FilePath) &&
                !ArchiveExplorerService.IsArchiveCachePath(tab.FilePath))
            {
                _favoritesRecentController.AddRecentFile(tab.FilePath);
            }
        }

        private void OpenEditorDocumentTab(
            OpenedTab tab,
            EditorDocumentSession session,
            bool isReadOnly,
            bool updateLanguageUi)
        {
            tab.InlineLivePreviewEnabled = !tab.IsHexViewer && _isLivePreviewEnabled();
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
            _ = tabParts.Bridge.SetSplitViewAsync(_editorWorkspace.CurrentSplitMode != EditorSplitMode.None);

            WireEditorBridge(
                tabParts.Bridge,
                tabParts.WebView,
                tabParts.LoadCover,
                tab,
                tabParts.TabItem,
                session,
                isReadOnly);

            AddTabItemToWorkspace(targetTabView, tabParts.TabItem, editorBgColor, queueSurfaceRefresh: true);
            UpdateTabStatus(tab, updateLanguageUi);
            QueueEditorInitialization(tab, tabParts.WebView, tabParts.Bridge);
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
                await bridge.SetSplitViewAsync(_editorWorkspace.CurrentSplitMode != EditorSplitMode.None);
                await bridge.InitializeModelAsync(
                    currentSession.Model.LineCount,
                    tab.Language,
                    _settingsService.CurrentSettings,
                    isReadOnly,
                    currentSession.GetLines(1, _initialEditorLineWarmupCount));
            };

            bridge.EditorRendered += () =>
            {
                CompleteViewModeTransition(tab.Id);
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

            bridge.HexEditRequested += async (offset, hex) =>
            {
                try
                {
                    var currentSession = getSession();
                    if (currentSession.Model is not HexDumpTextModel hexModel ||
                        string.IsNullOrEmpty(hex) ||
                        hex.Length % 2 != 0)
                    {
                        return;
                    }

                    byte[] bytes = Convert.FromHexString(hex);
                    int written = hexModel.ApplyByteEdit(offset, bytes);
                    if (written <= 0)
                    {
                        return;
                    }

                    int startLine = checked((int)(offset / 16) + 2);
                    int endLine = checked((int)((offset + written - 1) / 16) + 2);
                    await bridge.SendLinesAsync(0, startLine, currentSession.GetLines(startLine, endLine - startLine + 1));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to apply hex edit: {ex.Message}");
                }
            };

            bridge.LineEditRequested += async (lineNumber, startColumn, endColumn, text, isComposing) =>
            {
                await _editorBridgeDocumentController.HandleLineEditAsync(
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber,
                    startColumn,
                    endColumn,
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

            bridge.DeleteLineRequested += async (lineNumber, isComposing) =>
            {
                await _editorBridgeDocumentController.HandleDeleteLineRequestedAsync(
                    bridge,
                    tab,
                    tabItem,
                    getSession(),
                    lineNumber,
                    isComposing);
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

            bridge.FindAllRequested += async (query, matchCase, isRegex, currentLine) =>
            {
                await _editorBridgeDocumentController.HandleFindAllRequestedAsync(
                    bridge,
                    getSession(),
                    query,
                    matchCase,
                    isRegex,
                    currentLine);
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
                if (_viewModeTransitionVersions.ContainsKey(tab.Id))
                {
                    return;
                }

                _editorBridgeDocumentController.HandleContentChanged(
                    tab,
                    tabItem,
                    getSession(),
                    isComposing);
            };

            bridge.CursorChanged += (line, col) =>
            {
                _editorBridgeInteractionController.HandleCursorChanged(bridge, tab, line, col);
            };

            bridge.SelectionReceived += (selectedText, selStartLine, selEndLine, hexOffset, hexLength) =>
            {
                _editorBridgeInteractionController.HandleSelectionReceived(
                    tab,
                    selectedText,
                    selStartLine,
                    selEndLine,
                    hexOffset,
                    hexLength);
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

            bridge.OpenableHoverRequested += (text, isUrl, isPath) =>
            {
                var completion = new TaskCompletionSource<bool>();
                if (!_dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(await _editorLinkNavigationController.CanOpenAsync(text, isUrl, isPath));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Openable hover validation failed: {ex.Message}");
                        completion.TrySetResult(false);
                    }
                }))
                {
                    completion.TrySetResult(false);
                }

                return completion.Task;
            };
        }

        private async Task ApplyDeferredEditorStateAsync(MonacoBridge bridge, OpenedTab tab)
        {
            try
            {
                await bridge.UpdateScrollSyncStateAsync(_isScrollSyncEnabled());
                await bridge.SetInlineLivePreviewAsync(!tab.IsHexViewer && _isLivePreviewEnabled(), _getPreviewBaseHref(tab));
                await bridge.SetCsvTableModeAsync(!tab.IsHexViewer && tab.IsCsvTableModeEnabled);

                var snippets = _snippetService.GetSnippets();
                var autocompleteWords = _snippetService.GetAutocompleteWords();
                if (snippets.Count > 0 || autocompleteWords.Count > 0)
                {
                    await bridge.UpdateSnippetsAsync(snippets, autocompleteWords);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Deferred editor state sync failed: {ex.Message}");
            }
        }
    }
}
