using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class LivePreviewController
    {
        private const int InitialPreviewLineWarmupCount = 120;
        private const int PreviewLayoutRenderRetryLimit = 12;
        private const int PreviewLayoutRenderRetryMilliseconds = 50;
        private static readonly TimeSpan PreviewScrollEchoSuppressionDuration = TimeSpan.FromMilliseconds(900);

        private readonly RightSidebarPane _previewPane;
        private readonly ISettingsService _settingsService;
        private readonly IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string, EditorDocumentSession?> _sessionProvider;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<string> _currentRepoProvider;
        private readonly Func<bool> _isScrollSyncEnabled;
        private readonly Func<CoreWebView2WebMessageReceivedEventArgs, string> _normalizeWebMessageJson;
        private readonly Action<string> _shortcutHandler;
        private readonly Action<int, double> _previewScrollRequested;
        private readonly Action<string, string> _showErrorMessage;
        private readonly Func<string, string, string> _getString;
        private readonly DispatcherTimer _previewDebounceTimer;
        private readonly DispatcherTimer _renderAfterLayoutTimer;
        private readonly Dictionary<string, string> _mappedEditorDocumentDirectories = new Dictionary<string, string>();
        private readonly Dictionary<CoreWebView2, string> _mappedDocumentDirectoriesByWebView = new Dictionary<CoreWebView2, string>();
        private readonly Dictionary<string, int> _tabPreviewModes = new Dictionary<string, int>();

        private OpenedTab? _activeTabForPreview;
        private OpenedTab? _scheduledTabForPreview;
        private string _mappedPreviewDocumentDirectory = string.Empty;
        private Task? _initializeTask;
        private bool _initializeAndRenderQueued;
        private bool _renderAfterLayoutQueued;
        private bool _pendingForceSelectPreviewTab;
        private int _renderAfterLayoutAttempts;
        private bool _updatingPreviewModeSelection;
        private bool _isPreviewReady;
        private DateTimeOffset _suppressEditorScrollToPreviewUntil = DateTimeOffset.MinValue;

        public LivePreviewController(
            RightSidebarPane previewPane,
            ISettingsService settingsService,
            IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Func<OpenedTab?> activeTabProvider,
            Func<string, EditorDocumentSession?> sessionProvider,
            Func<string> currentFolderProvider,
            Func<string> currentRepoProvider,
            Func<bool> isScrollSyncEnabled,
            Func<CoreWebView2WebMessageReceivedEventArgs, string> normalizeWebMessageJson,
            Action<string> shortcutHandler,
            Action<int, double> previewScrollRequested,
            Action<string, string> showErrorMessage,
            Func<string, string, string> getString)
        {
            _previewPane = previewPane;
            _settingsService = settingsService;
            _tabBridges = tabBridges;
            _activeTabProvider = activeTabProvider;
            _sessionProvider = sessionProvider;
            _currentFolderProvider = currentFolderProvider;
            _currentRepoProvider = currentRepoProvider;
            _isScrollSyncEnabled = isScrollSyncEnabled;
            _normalizeWebMessageJson = normalizeWebMessageJson;
            _shortcutHandler = shortcutHandler;
            _previewScrollRequested = previewScrollRequested;
            _showErrorMessage = showErrorMessage;
            _getString = getString;

            _previewPane.PreviewModeSelectionChanged += OnPreviewModeSelectionChanged;
            _previewPane.OpenPreviewInBrowserClick += OnOpenPreviewInBrowserClick;
            _previewPane.OpenWithDefaultProgramClick += OnOpenWithDefaultProgramClick;
            _previewPane.OpenExternalViewerClick += OnOpenExternalViewerClick;

            _previewDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _previewDebounceTimer.Tick += OnPreviewDebounceTimerTick;
            _renderAfterLayoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PreviewLayoutRenderRetryMilliseconds)
            };
            _renderAfterLayoutTimer.Tick += OnRenderAfterLayoutTimerTick;
            _previewPane.RightTabs.SelectionChanged += OnRightTabsSelectionChanged;
            _previewPane.SizeChanged += OnPreviewPaneSizeChanged;
            _previewPane.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnPreviewPaneVisibilityChanged);
        }

        private WebView2 PreviewWebView => _previewPane.PreviewWebViewControl;

        private WebView2? PreviewWebViewIfCreated => _previewPane.PreviewWebViewControlIfCreated;

        private ComboBox PreviewModeCombo => _previewPane.PreviewMode;

        private bool IsLivePreviewVisible =>
            _previewPane.Visibility == Visibility.Visible &&
            ReferenceEquals(_previewPane.RightTabs.SelectedItem, _previewPane.LivePreviewTabItem);

        private OpenedTab? PreviewTargetTab => _activeTabForPreview ?? _activeTabProvider();

        public async Task InitializeAsync()
        {
            if (_initializeTask != null)
            {
                await _initializeTask;
                return;
            }

            _initializeTask = InitializeCoreAsync();
            await _initializeTask;
        }

        private async Task InitializeCoreAsync()
        {
            try
            {
                _isPreviewReady = false;
                var previewWebView = PreviewWebView;
                previewWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await previewWebView.EnsureCoreWebView2Async(env);

                var coreWebView = previewWebView.CoreWebView2;
                if (coreWebView == null)
                {
                    throw new InvalidOperationException("CoreWebView2 failed to initialize.");
                }

                WebViewAppearanceService.ApplyPreferredColorScheme(coreWebView, _settingsService.CurrentSettings.Theme);
                coreWebView.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.ResourceHostName,
                    PreviewWebResourceService.WebResourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                RegisterDocumentResourceAccess(coreWebView);

                coreWebView.Settings.IsWebMessageEnabled = true;
                coreWebView.Settings.IsScriptEnabled = true;
                coreWebView.Settings.AreDefaultContextMenusEnabled = false;
                coreWebView.Settings.AreDevToolsEnabled = false;
                previewWebView.WebMessageReceived += OnPreviewWebMessageReceived;
                previewWebView.NavigationCompleted += (_, _) => RenderActiveTab();

                previewWebView.Source = new Uri(
                    $"http://{PreviewWebResourceService.ResourceHostName}/preview.html?v={PreviewWebResourceService.GetWebResourceVersion("preview.html")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to init preview webview: {ex.Message}");
                _initializeTask = null;
            }
        }

        public void RegisterDocumentResourceAccess(CoreWebView2 coreWebView)
        {
            coreWebView.AddWebResourceRequestedFilter(
                $"http://{PreviewWebResourceService.DocumentHostName}/*",
                CoreWebView2WebResourceContext.All);
            coreWebView.WebResourceRequested += OnDocumentResourceRequested;
        }

        public void Schedule(OpenedTab tab)
        {
            var targetTab = PreviewTargetTab;
            if (targetTab != null && !string.Equals(targetTab.Id, tab.Id, StringComparison.Ordinal))
            {
                return;
            }

            _activeTabForPreview = tab;
            _scheduledTabForPreview = tab;
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Start();
        }

        public void RenderActiveTab()
        {
            var tab = PreviewTargetTab;
            if (tab != null)
            {
                Render(tab);
            }
        }

        public void EnsureVisiblePreviewRendered(bool forceSelectPreviewTab = false)
        {
            if (!EnsureLivePreviewVisibleForRender(forceSelectPreviewTab))
            {
                return;
            }

            if (!IsPreviewPaneSizedForWebView)
            {
                QueueEnsureVisiblePreviewRenderedAfterLayout(forceSelectPreviewTab);
                return;
            }

            if (_initializeAndRenderQueued)
            {
                return;
            }

            _initializeAndRenderQueued = true;

            async void InitializeAndRender()
            {
                try
                {
                    await InitializeAsync();
                    if (PreviewWebViewIfCreated?.CoreWebView2 != null &&
                        EnsureLivePreviewVisibleForRender(forceSelectPreviewTab) &&
                        IsPreviewPaneSizedForWebView)
                    {
                        RenderActiveTab();
                        QueueRenderActiveTabAfterLayout();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to initialize and render preview: {ex.Message}");
                }
                finally
                {
                    _initializeAndRenderQueued = false;
                }
            }

            var dispatcher = _previewPane.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, InitializeAndRender) != true)
            {
                InitializeAndRender();
            }
        }

        private void QueueRenderActiveTabAfterLayout()
        {
            var dispatcher = _previewPane.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (IsLivePreviewVisible)
                    {
                        RenderActiveTab();
                    }
                }) != true)
            {
                RenderActiveTab();
            }
        }

        public void Render(OpenedTab tab)
        {
            _activeTabForPreview = tab;

            try
            {
                if (!EnsureLivePreviewVisibleForRender(forceSelectPreviewTab: false))
                {
                    return;
                }

                if (!IsPreviewPaneSizedForWebView)
                {
                    QueueEnsureVisiblePreviewRenderedAfterLayout(forceSelectPreviewTab: false);
                    return;
                }

                var previewWebView = PreviewWebViewIfCreated;
                var coreWebView = previewWebView?.CoreWebView2;
                if (coreWebView == null)
                {
                    EnsureVisiblePreviewRendered();
                    return;
                }

                if (!_isPreviewReady)
                {
                    return;
                }

                if (IsReadOnlyViewerTab(tab))
                {
                    _tabPreviewModes[tab.Id] = 0;
                    if (PreviewModeCombo.SelectedIndex != 0)
                    {
                        PreviewModeCombo.SelectedIndex = 0;
                    }

                    PostEmptyVirtualPreview();
                    return;
                }

                int selectedMode;
                if (string.Equals(tab.Language, "html", StringComparison.OrdinalIgnoreCase))
                {
                    selectedMode = 1; // HTML Preview is unconditionally forced for HTML files
                }
                else if (!_tabPreviewModes.TryGetValue(tab.Id, out selectedMode))
                {
                    selectedMode = 0; // Default: Markdown
                    if (string.Equals(tab.Language, "csv", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedMode = 4; // CSV Table
                    }
                    _tabPreviewModes[tab.Id] = selectedMode;
                }

                if (PreviewModeCombo.SelectedIndex != selectedMode)
                {
                    _updatingPreviewModeSelection = true;
                    try
                    {
                        PreviewModeCombo.SelectedIndex = selectedMode;
                    }
                    finally
                    {
                        _updatingPreviewModeSelection = false;
                    }
                }

                string mode = selectedMode switch
                {
                    1 => "html",
                    2 => "latex",
                    3 => "aozora",
                    4 => "csv",
                    _ => "markdown"
                };

                if (string.Equals(mode, "csv", StringComparison.Ordinal))
                {
                    string previewText = _sessionProvider(tab.Id)?.GetText() ?? tab.Content ?? string.Empty;
                    var csvMsg = new
                    {
                        action = "renderCsvPreview",
                        text = previewText,
                        scrollSyncEnabled = _isScrollSyncEnabled(),
                        theme = _settingsService.CurrentSettings.Theme,
                        customBackgroundColor = _settingsService.CurrentSettings.CustomBackgroundColor,
                        customForegroundColor = _settingsService.CurrentSettings.CustomForegroundColor,
                        previewCustomBackgroundColor = _settingsService.CurrentSettings.PreviewCustomBackgroundColor,
                        previewCustomForegroundColor = _settingsService.CurrentSettings.PreviewCustomForegroundColor,
                        uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                        previewFontFamily = _settingsService.CurrentSettings.PreviewFontFamily,
                        previewFontSize = _settingsService.CurrentSettings.PreviewFontSize,
                        csvEmptyMessage = _getString("PreviewCsvEmpty", "빈 CSV 파일입니다.")
                    };
                    coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(csvMsg));
                    return;
                }

                if (string.Equals(mode, "html", StringComparison.Ordinal))
                {
                    string previewText = _sessionProvider(tab.Id)?.GetText() ?? tab.Content ?? string.Empty;
                    var htmlMsg = new
                    {
                        action = "renderHtmlPreview",
                        text = previewText,
                        baseHref = GetPreviewBaseHref(tab),
                        localResourceVersion = tab.PreviewResourceVersion,
                        scrollSyncEnabled = _isScrollSyncEnabled()
                    };

                    coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(htmlMsg));
                    return;
                }

                var previewSession = _sessionProvider(tab.Id);
                var renderMsg = new
                {
                    action = "initVirtualPreview",
                    lineCount = previewSession?.Model.LineCount ?? 1,
                    initialStartLine = 1,
                    initialLines = previewSession?.GetLines(1, InitialPreviewLineWarmupCount) ?? Array.Empty<string>(),
                    mode = mode,
                    baseHref = GetPreviewBaseHref(tab),
                    localResourceVersion = tab.PreviewResourceVersion,
                    wordWrap = _settingsService.CurrentSettings.WordWrap,
                    tabSize = _settingsService.CurrentSettings.TabSize,
                    theme = _settingsService.CurrentSettings.Theme,
                    customBackgroundColor = _settingsService.CurrentSettings.CustomBackgroundColor,
                    customForegroundColor = _settingsService.CurrentSettings.CustomForegroundColor,
                    uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                    previewFontFamily = _settingsService.CurrentSettings.PreviewFontFamily,
                    previewFontSize = _settingsService.CurrentSettings.PreviewFontSize,
                    previewCustomBackgroundColor = _settingsService.CurrentSettings.PreviewCustomBackgroundColor,
                    previewCustomForegroundColor = _settingsService.CurrentSettings.PreviewCustomForegroundColor,
                    scrollSyncEnabled = _isScrollSyncEnabled()
                };

                coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(renderMsg));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed sending live preview rendering data: {ex.Message}");
            }
        }

        private void PostEmptyVirtualPreview()
        {
            var coreWebView = PreviewWebViewIfCreated?.CoreWebView2;
            if (coreWebView == null)
            {
                return;
            }

            var renderMsg = new
            {
                action = "initVirtualPreview",
                lineCount = 1,
                initialStartLine = 1,
                initialLines = new[] { string.Empty },
                mode = "markdown",
                baseHref = string.Empty,
                wordWrap = _settingsService.CurrentSettings.WordWrap,
                tabSize = _settingsService.CurrentSettings.TabSize,
                theme = _settingsService.CurrentSettings.Theme,
                customBackgroundColor = _settingsService.CurrentSettings.CustomBackgroundColor,
                customForegroundColor = _settingsService.CurrentSettings.CustomForegroundColor,
                uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                previewFontFamily = _settingsService.CurrentSettings.PreviewFontFamily,
                previewFontSize = _settingsService.CurrentSettings.PreviewFontSize,
                previewCustomBackgroundColor = _settingsService.CurrentSettings.PreviewCustomBackgroundColor,
                previewCustomForegroundColor = _settingsService.CurrentSettings.PreviewCustomForegroundColor,
                scrollSyncEnabled = _isScrollSyncEnabled()
            };

            coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(renderMsg));
        }

        private static bool IsReadOnlyViewerTab(OpenedTab tab)
        {
            return tab.IsReadOnlyViewer ||
                   string.Equals(tab.Language, "pdf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tab.Language, "image", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(tab.FilePath) &&
                    (Path.GetExtension(tab.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                     IsImageFileExtension(Path.GetExtension(tab.FilePath))));
        }

        private static bool IsImageFileExtension(string extension)
        {
            return SupportedFileTypes.IsImageFile("test" + extension);
        }

        public string GetPreviewBaseHref(OpenedTab tab)
        {
            try
            {
                string directory = string.Empty;
                if (!string.IsNullOrWhiteSpace(tab.FilePath))
                {
                    directory = Path.GetDirectoryName(tab.FilePath) ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(_currentFolderProvider()))
                {
                    directory = _currentFolderProvider();
                }

                if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(_currentRepoProvider()))
                {
                    directory = _currentRepoProvider();
                }

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return string.Empty;
                }

                ConfigurePreviewDocumentFolderMapping(directory);
                ConfigureEditorDocumentFolderMapping(tab, directory);
                return $"http://{PreviewWebResourceService.DocumentHostName}/";
            }
            catch
            {
                return string.Empty;
            }
        }

        public void ApplyPreferredColorScheme(string theme)
        {
            WebViewAppearanceService.ApplyPreferredColorScheme(PreviewWebViewIfCreated?.CoreWebView2, theme);
        }

        public void PostScrollSync(int firstLine, double offset)
        {
            if (!_isScrollSyncEnabled())
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _suppressEditorScrollToPreviewUntil)
            {
                return;
            }

            if ((TabViewItem)_previewPane.RightTabs.SelectedItem != _previewPane.LivePreviewTabItem)
            {
                return;
            }

            try
            {
                var coreWebView = PreviewWebViewIfCreated?.CoreWebView2;
                if (coreWebView == null)
                {
                    return;
                }

                var syncMsg = new
                {
                    action = "syncScroll",
                    firstLine = firstLine,
                    offset = offset
                };
                coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(syncMsg));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to sync scroll to preview: {ex.Message}");
            }
        }

        public void PostScrollSyncState(bool enabled)
        {
            if ((TabViewItem)_previewPane.RightTabs.SelectedItem != _previewPane.LivePreviewTabItem)
            {
                return;
            }

            try
            {
                var coreWebView = PreviewWebViewIfCreated?.CoreWebView2;
                if (coreWebView == null)
                {
                    return;
                }

                var syncMsg = new
                {
                    action = "scrollSyncChanged",
                    enabled = enabled
                };
                coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(syncMsg));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to sync scroll sync state to preview: {ex.Message}");
            }
        }

        public void ForgetEditorTab(string tabId, CoreWebView2? coreWebView)
        {
            _mappedEditorDocumentDirectories.Remove(tabId);
            _tabPreviewModes.Remove(tabId);
            if (string.Equals(_activeTabForPreview?.Id, tabId, StringComparison.Ordinal))
            {
                _activeTabForPreview = null;
            }

            if (string.Equals(_scheduledTabForPreview?.Id, tabId, StringComparison.Ordinal))
            {
                _scheduledTabForPreview = null;
            }

            if (coreWebView != null)
            {
                _mappedDocumentDirectoriesByWebView.Remove(coreWebView);
            }
        }

        public void Close()
        {
            try
            {
                _previewDebounceTimer.Stop();
                _renderAfterLayoutTimer.Stop();
                PreviewWebViewIfCreated?.Close();
            }
            catch { }
        }

        private void OnPreviewDebounceTimerTick(object? sender, object e)
        {
            _previewDebounceTimer.Stop();
            var tab = _scheduledTabForPreview ?? PreviewTargetTab;
            _scheduledTabForPreview = null;
            if (tab != null)
            {
                Render(tab);
            }
        }

        private void OnRightTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLivePreviewVisible)
            {
                EnsureVisiblePreviewRendered();
            }
        }

        private void OnPreviewPaneVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (_previewPane.Visibility == Visibility.Visible && PreviewTargetTab != null)
            {
                QueueEnsureVisiblePreviewRenderedAfterLayout(forceSelectPreviewTab: false);
            }
        }

        private void OnPreviewPaneSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_previewPane.Visibility == Visibility.Visible &&
                IsPreviewPaneSizedForWebView &&
                PreviewTargetTab != null)
            {
                EnsureVisiblePreviewRendered();
            }
        }

        private bool IsPreviewPaneSizedForWebView =>
            _previewPane.ActualWidth > 1 &&
            _previewPane.ActualHeight > 1;

        private void QueueEnsureVisiblePreviewRenderedAfterLayout(bool forceSelectPreviewTab)
        {
            _pendingForceSelectPreviewTab = _pendingForceSelectPreviewTab || forceSelectPreviewTab;

            if (_renderAfterLayoutQueued)
            {
                return;
            }

            _renderAfterLayoutQueued = true;
            _renderAfterLayoutAttempts = 0;
            QueueRenderAfterLayoutAttempt();
        }

        private void QueueRenderAfterLayoutAttempt()
        {
            var dispatcher = _previewPane.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RunRenderAfterLayoutAttempt) != true)
            {
                RunRenderAfterLayoutAttempt();
            }
        }

        private void RunRenderAfterLayoutAttempt()
        {
            if (_previewPane.Visibility != Visibility.Visible || PreviewTargetTab == null)
            {
                ClearRenderAfterLayoutQueue();
                return;
            }

            if (!IsPreviewPaneSizedForWebView)
            {
                _renderAfterLayoutAttempts++;
                if (_renderAfterLayoutAttempts >= PreviewLayoutRenderRetryLimit)
                {
                    ClearRenderAfterLayoutQueue();
                    return;
                }

                _renderAfterLayoutTimer.Stop();
                _renderAfterLayoutTimer.Start();
                return;
            }

            bool forceSelectPreviewTab = _pendingForceSelectPreviewTab;
            ClearRenderAfterLayoutQueue();

            if (forceSelectPreviewTab)
            {
                EnsureVisiblePreviewRendered(forceSelectPreviewTab: true);
            }
            else if (IsLivePreviewVisible)
            {
                EnsureVisiblePreviewRendered(forceSelectPreviewTab: false);
            }
        }

        private void OnRenderAfterLayoutTimerTick(object? sender, object e)
        {
            _renderAfterLayoutTimer.Stop();
            QueueRenderAfterLayoutAttempt();
        }

        private void ClearRenderAfterLayoutQueue()
        {
            _renderAfterLayoutTimer.Stop();
            _renderAfterLayoutQueued = false;
            _pendingForceSelectPreviewTab = false;
            _renderAfterLayoutAttempts = 0;
        }

        private bool EnsureLivePreviewVisibleForRender(bool forceSelectPreviewTab)
        {
            if (_previewPane.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (forceSelectPreviewTab || _previewPane.RightTabs.SelectedItem == null)
            {
                _previewPane.RightTabs.SelectedItem = _previewPane.LivePreviewTabItem;
            }

            return ReferenceEquals(_previewPane.RightTabs.SelectedItem, _previewPane.LivePreviewTabItem);
        }

        private void OnPreviewModeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPreviewModeSelection)
            {
                return;
            }

            var tab = PreviewTargetTab;
            if (tab != null)
            {
                _tabPreviewModes[tab.Id] = PreviewModeCombo.SelectedIndex;
            }

            if (PreviewWebViewIfCreated?.CoreWebView2 == null)
            {
                EnsureVisiblePreviewRendered();
                return;
            }

            RenderActiveTab();
        }

        private async void OnOpenPreviewInBrowserClick(object sender, RoutedEventArgs e)
        {
            await OpenInBrowserAsync();
        }

        private async void OnOpenExternalViewerClick(object sender, RoutedEventArgs e)
        {
            await OpenExternalViewerAsync();
        }

        private async void OnOpenWithDefaultProgramClick(object sender, RoutedEventArgs e)
        {
            await OpenWithDefaultProgramAsync();
        }

        private async Task OpenInBrowserAsync()
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                _showErrorMessage(
                    _getString("OpenInBrowserDialogTitle", "브라우저 열기"),
                    _getString("OpenInBrowserNoActiveTab", "브라우저로 열 활성 탭이 없습니다."));
                return;
            }

            try
            {
                string targetPath = tab.FilePath ?? string.Empty;
                bool isSavedHtml = !string.IsNullOrWhiteSpace(targetPath) &&
                    File.Exists(targetPath) &&
                    (targetPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                     targetPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));

                if (!isSavedHtml)
                {
                    string previewDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "Preview");
                    Directory.CreateDirectory(previewDir);
                    targetPath = Path.Combine(previewDir, $"preview-{tab.Id}.html");
                    string previewText = _sessionProvider(tab.Id)?.GetText() ?? tab.Content ?? string.Empty;
                    await File.WriteAllTextAsync(targetPath, previewText, Encoding.UTF8);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("OpenInBrowserFailedTitle", "브라우저 열기 실패"), ex.Message);
            }
        }

        private async Task OpenWithDefaultProgramAsync()
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                _showErrorMessage(
                    _getString("OpenWithDefaultProgramFailedTitle", "기본 프로그램으로 열기 실패"),
                    _getString("OpenWithDefaultProgramNoActiveTab", "기본 프로그램으로 열 활성 탭이 없습니다."));
                return;
            }

            try
            {
                string targetPath = await GetDefaultProgramTargetPathAsync(tab);
                OpenWithDefaultProgram(targetPath);
            }
            catch (Exception ex)
            {
                _showErrorMessage(
                    _getString("OpenWithDefaultProgramFailedTitle", "기본 프로그램으로 열기 실패"),
                    ex.Message);
            }
        }

        public Task OpenFileWithDefaultProgramAsync(string filePath)
        {
            try
            {
                OpenWithDefaultProgram(filePath);
            }
            catch (Exception ex)
            {
                _showErrorMessage(
                    _getString("OpenWithDefaultProgramFailedTitle", "기본 프로그램으로 열기 실패"),
                    ex.Message);
            }

            return Task.CompletedTask;
        }

        private async Task<string> GetDefaultProgramTargetPathAsync(OpenedTab tab)
        {
            string targetPath = tab.FilePath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
            {
                return targetPath;
            }

            string previewDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "OpenWithDefault");
            Directory.CreateDirectory(previewDir);
            string extension = Path.GetExtension(tab.FilePath) ?? ".md";
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = tab.Language?.ToLowerInvariant() switch
                {
                    "html" => ".html",
                    "csv" => ".csv",
                    "latex" => ".tex",
                    _ => ".md"
                };
            }

            targetPath = Path.Combine(previewDir, $"open-{tab.Id}{extension}");
            string content = _sessionProvider(tab.Id)?.GetText() ?? tab.Content ?? string.Empty;
            await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            return targetPath;
        }

        private static void OpenWithDefaultProgram(string targetPath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true
            });
        }

        private async Task OpenExternalViewerAsync()
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                _showErrorMessage(
                    _getString("ExternalViewerOpenTitle", "외부 뷰어 열기"),
                    _getString("ExternalViewerNoActiveTab", "외부 뷰어로 열 활성 탭이 없습니다."));
                return;
            }

            if (!TryGetExternalViewerPath(out var settings, out string viewerPath))
            {
                return;
            }

            try
            {
                string targetPath = await GetExternalViewerTargetPathAsync(tab);
                OpenFileInExternalViewer(targetPath, viewerPath, settings.ExternalViewerArguments);
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("ExternalViewerOpenFailedTitle", "외부 뷰어 열기 실패"), ex.Message);
            }
        }

        public Task OpenFileInExternalViewerAsync(string filePath)
        {
            if (!TryGetExternalViewerPath(out var settings, out string viewerPath))
            {
                return Task.CompletedTask;
            }

            try
            {
                OpenFileInExternalViewer(filePath, viewerPath, settings.ExternalViewerArguments);
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("ExternalViewerOpenFailedTitle", "외부 뷰어 열기 실패"), ex.Message);
            }

            return Task.CompletedTask;
        }

        private bool TryGetExternalViewerPath(out EditorSettings settings, out string viewerPath)
        {
            settings = _settingsService.CurrentSettings;
            viewerPath = settings.ExternalViewerPath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(viewerPath))
            {
                return true;
            }

            _showErrorMessage(
                _getString("ExternalViewerOpenTitle", "외부 뷰어 열기"),
                _getString("ExternalViewerPathMissing", "설정 > 편집에서 외부 뷰어 경로 또는 실행 별칭을 먼저 지정해 주세요."));
            return false;
        }

        private void OpenFileInExternalViewer(string targetPath, string viewerPath, string? argumentTemplate)
        {
            string arguments = BuildExternalViewerArguments(argumentTemplate, targetPath);
            string workingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;

            StartExternalViewer(viewerPath, arguments, workingDirectory);
        }

        private void StartExternalViewer(string viewerPath, string arguments, string workingDirectory)
        {
            try
            {
                Process.Start(CreateExternalViewerStartInfo(viewerPath, arguments, workingDirectory, useShellExecute: true));
            }
            catch (Exception shellException)
            {
                try
                {
                    Process.Start(CreateExternalViewerStartInfo(viewerPath, arguments, workingDirectory, useShellExecute: false));
                }
                catch (Exception directException)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            _getString("ExternalViewerStartFailedFormat", "외부 뷰어를 실행할 수 없습니다: {0}{1}Shell 실행 실패: {2}{1}직접 실행 실패: {3}"),
                            viewerPath,
                            Environment.NewLine,
                            shellException.Message,
                            directException.Message));
                }
            }
        }

        private static ProcessStartInfo CreateExternalViewerStartInfo(string viewerPath, string arguments, string workingDirectory, bool useShellExecute)
        {
            return new ProcessStartInfo
            {
                FileName = viewerPath,
                Arguments = arguments,
                UseShellExecute = useShellExecute,
                WorkingDirectory = workingDirectory
            };
        }

        private async Task<string> GetExternalViewerTargetPathAsync(OpenedTab tab)
        {
            if (!string.IsNullOrWhiteSpace(tab.FilePath) && File.Exists(tab.FilePath))
            {
                return tab.FilePath;
            }

            string previewDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "ExternalViewer");
            Directory.CreateDirectory(previewDir);

            string extension = ResolveTemporaryExtension(tab);
            string targetPath = Path.Combine(previewDir, $"preview-{tab.Id}{extension}");
            string previewText = _sessionProvider(tab.Id)?.GetText() ?? tab.Content ?? string.Empty;
            await File.WriteAllTextAsync(targetPath, previewText, Encoding.UTF8);
            return targetPath;
        }

        private static string ResolveTemporaryExtension(OpenedTab tab)
        {
            if (!string.IsNullOrWhiteSpace(tab.FilePath))
            {
                string fileExtension = Path.GetExtension(tab.FilePath);
                if (!string.IsNullOrWhiteSpace(fileExtension))
                {
                    return fileExtension;
                }
            }

            return tab.Language?.ToLowerInvariant() switch
            {
                "html" => ".html",
                "csv" => ".csv",
                "latex" => ".tex",
                _ => ".md"
            };
        }

        private static string BuildExternalViewerArguments(string? argumentTemplate, string targetPath)
        {
            string quotedPath = QuoteArgument(targetPath);
            string template = argumentTemplate?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(template))
            {
                return quotedPath;
            }

            if (template.Contains("{file}", StringComparison.OrdinalIgnoreCase))
            {
                return RegexReplaceFilePlaceholder(template, quotedPath);
            }

            return $"{template} {quotedPath}";
        }

        private static string RegexReplaceFilePlaceholder(string template, string quotedPath)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                template,
                "\\{file\\}",
                quotedPath.Replace("$", "$$"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void OnPreviewWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = _normalizeWebMessageJson(args);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                {
                    return;
                }

                string type = typeProp.GetString() ?? string.Empty;
                if (string.Equals(type, "previewReady", StringComparison.Ordinal))
                {
                    _isPreviewReady = true;
                    sender.DispatcherQueue.TryEnqueue(() => RenderActiveTab());
                    return;
                }

                if (string.Equals(type, "shortcut", StringComparison.Ordinal))
                {
                    if (root.TryGetProperty("name", out var nameProp))
                    {
                        string name = nameProp.GetString() ?? string.Empty;
                        sender.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                    }
                    return;
                }

                if (string.Equals(type, "previewScroll", StringComparison.Ordinal))
                {
                    if (!_isScrollSyncEnabled())
                    {
                        return;
                    }

                    int firstLine = root.TryGetProperty("firstLine", out var firstLineProp) ? firstLineProp.GetInt32() : 1;
                    double offset = root.TryGetProperty("offset", out var offsetProp) ? offsetProp.GetDouble() : 0;
                    _suppressEditorScrollToPreviewUntil = DateTimeOffset.UtcNow + PreviewScrollEchoSuppressionDuration;
                    sender.DispatcherQueue.TryEnqueue(() => _previewScrollRequested(firstLine, offset));
                    return;
                }

                if (!string.Equals(type, "previewRequestLines", StringComparison.Ordinal))
                {
                    return;
                }

                int requestId = root.TryGetProperty("requestId", out var requestIdProp) ? requestIdProp.GetInt32() : 0;
                int startLine = root.TryGetProperty("startLine", out var startLineProp) ? startLineProp.GetInt32() : 1;
                int count = root.TryGetProperty("count", out var countProp) ? countProp.GetInt32() : 80;
                var activeTab = PreviewTargetTab;
                IReadOnlyList<string> lines = Array.Empty<string>();
                if (activeTab != null)
                {
                    lines = _sessionProvider(activeTab.Id)?.GetLines(startLine, count) ?? Array.Empty<string>();
                }

                var reply = new
                {
                    action = "previewLines",
                    requestId = requestId,
                    startLine = startLine,
                    lines = lines
                };
                sender.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(reply));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed handling preview message: {ex.Message}");
            }
        }

        private void ConfigurePreviewDocumentFolderMapping(string directory)
        {
            try
            {
                var coreWebView = PreviewWebViewIfCreated?.CoreWebView2;
                if (coreWebView == null || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                string normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(_mappedPreviewDocumentDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                coreWebView.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.DocumentHostName,
                    normalizedDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
                _mappedPreviewDocumentDirectory = normalizedDirectory;
                _mappedDocumentDirectoriesByWebView[coreWebView] = normalizedDirectory;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to map preview document folder: {ex.Message}");
            }
        }

        private void ConfigureEditorDocumentFolderMapping(OpenedTab tab, string directory)
        {
            try
            {
                if (!_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) ||
                    bridgeGroup.WebView.CoreWebView2 == null ||
                    string.IsNullOrWhiteSpace(directory) ||
                    !Directory.Exists(directory))
                {
                    return;
                }

                string normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (_mappedEditorDocumentDirectories.TryGetValue(tab.Id, out var mappedDirectory) &&
                    string.Equals(mappedDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bridgeGroup.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.DocumentHostName,
                    normalizedDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
                _mappedEditorDocumentDirectories[tab.Id] = normalizedDirectory;
                _mappedDocumentDirectoriesByWebView[bridgeGroup.WebView.CoreWebView2] = normalizedDirectory;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to map editor document folder: {ex.Message}");
            }
        }

        private void OnDocumentResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string mappedDirectory = _mappedDocumentDirectoriesByWebView.TryGetValue(sender, out var webViewDirectory)
                    ? webViewDirectory
                    : _mappedPreviewDocumentDirectory;

                if (string.IsNullOrWhiteSpace(mappedDirectory) ||
                    !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requestUri) ||
                    !string.Equals(requestUri.Host, PreviewWebResourceService.DocumentHostName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string relativePath = Uri.UnescapeDataString(requestUri.AbsolutePath.TrimStart('/'))
                    .Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        PreviewWebResourceService.CreateEmptyResourceStream(),
                        404,
                        "Not Found",
                        "Content-Type: text/plain");
                    return;
                }

                string root = Path.GetFullPath(mappedDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string targetPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(targetPath))
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        PreviewWebResourceService.CreateEmptyResourceStream(),
                        404,
                        "Not Found",
                        "Content-Type: text/plain");
                    return;
                }

                var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                string headers = $"Content-Type: {PreviewWebResourceService.GetContentType(targetPath)}\r\nCache-Control: no-store, no-cache, must-revalidate\r\nPragma: no-cache\r\nExpires: 0";
                args.Response = sender.Environment.CreateWebResourceResponse(stream.AsRandomAccessStream(), 200, "OK", headers);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed serving preview document resource: {ex.Message}");
                try
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        PreviewWebResourceService.CreateEmptyResourceStream(),
                        500,
                        "Internal Server Error",
                        "Content-Type: text/plain");
                }
                catch { }
            }
        }
    }
}
