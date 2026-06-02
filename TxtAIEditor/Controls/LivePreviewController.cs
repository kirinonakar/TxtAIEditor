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

        private readonly RightSidebarPane _previewPane;
        private readonly ISettingsService _settingsService;
        private readonly IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string, EditorDocumentSession?> _sessionProvider;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<string> _currentRepoProvider;
        private readonly Func<bool> _isScrollSyncEnabled;
        private readonly Func<CoreWebView2WebMessageReceivedEventArgs, string> _normalizeWebMessageJson;
        private readonly Action<string> _shortcutHandler;
        private readonly Action<int, double> _previewScrollRequested;
        private readonly Action<string, string> _showErrorMessage;
        private readonly DispatcherTimer _previewDebounceTimer;
        private readonly Dictionary<string, string> _mappedEditorDocumentDirectories = new Dictionary<string, string>();
        private readonly Dictionary<CoreWebView2, string> _mappedDocumentDirectoriesByWebView = new Dictionary<CoreWebView2, string>();
        private readonly Dictionary<string, int> _tabPreviewModes = new Dictionary<string, int>();

        private OpenedTab? _activeTabForPreview;
        private string _mappedPreviewDocumentDirectory = string.Empty;

        public LivePreviewController(
            RightSidebarPane previewPane,
            ISettingsService settingsService,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<OpenedTab?> activeTabProvider,
            Func<string, EditorDocumentSession?> sessionProvider,
            Func<string> currentFolderProvider,
            Func<string> currentRepoProvider,
            Func<bool> isScrollSyncEnabled,
            Func<CoreWebView2WebMessageReceivedEventArgs, string> normalizeWebMessageJson,
            Action<string> shortcutHandler,
            Action<int, double> previewScrollRequested,
            Action<string, string> showErrorMessage)
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

            _previewPane.PreviewModeSelectionChanged += OnPreviewModeSelectionChanged;
            _previewPane.OpenPreviewInBrowserClick += OnOpenPreviewInBrowserClick;

            _previewDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _previewDebounceTimer.Tick += OnPreviewDebounceTimerTick;
        }

        private WebView2 PreviewWebView => _previewPane.PreviewWebViewControl;

        private ComboBox PreviewModeCombo => _previewPane.PreviewMode;

        public async Task InitializeAsync()
        {
            try
            {
                PreviewWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                var env = await MonacoBridge.GetSharedEnvironmentAsync();
                await PreviewWebView.EnsureCoreWebView2Async(env);

                var coreWebView = PreviewWebView.CoreWebView2;
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
                PreviewWebView.WebMessageReceived += OnPreviewWebMessageReceived;
                PreviewWebView.NavigationCompleted += (_, _) => RenderActiveTab();

                PreviewWebView.Source = new Uri(
                    $"http://{PreviewWebResourceService.ResourceHostName}/preview.html?v={PreviewWebResourceService.GetWebResourceVersion("preview.html")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to init preview webview: {ex.Message}");
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
            _activeTabForPreview = tab;
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Start();
        }

        public void RenderActiveTab()
        {
            var tab = _activeTabProvider();
            if (tab != null)
            {
                Render(tab);
            }
        }

        public void Render(OpenedTab tab)
        {
            try
            {
                if (PreviewWebView.CoreWebView2 == null)
                {
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
                    PreviewModeCombo.SelectedIndex = selectedMode;
                    return;
                }

                string mode = PreviewModeCombo.SelectedIndex switch
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
                        previewFontSize = _settingsService.CurrentSettings.PreviewFontSize
                    };
                    PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(csvMsg));
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
                        scrollSyncEnabled = _isScrollSyncEnabled()
                    };

                    PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(htmlMsg));
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

                PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(renderMsg));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed sending live preview rendering data: {ex.Message}");
            }
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
            WebViewAppearanceService.ApplyPreferredColorScheme(PreviewWebView.CoreWebView2, theme);
        }

        public void PostScrollSync(int firstLine, double offset)
        {
            if (!_isScrollSyncEnabled())
            {
                return;
            }

            try
            {
                if (PreviewWebView.CoreWebView2 == null)
                {
                    return;
                }

                var syncMsg = new
                {
                    action = "syncScroll",
                    firstLine = firstLine,
                    offset = offset
                };
                PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(syncMsg));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to sync scroll to preview: {ex.Message}");
            }
        }

        public void PostScrollSyncState(bool enabled)
        {
            try
            {
                if (PreviewWebView.CoreWebView2 == null)
                {
                    return;
                }

                var syncMsg = new
                {
                    action = "scrollSyncChanged",
                    enabled = enabled
                };
                PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(syncMsg));
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
                PreviewWebView.Close();
            }
            catch { }
        }

        private void OnPreviewDebounceTimerTick(object? sender, object e)
        {
            _previewDebounceTimer.Stop();
            if (_activeTabForPreview != null)
            {
                Render(_activeTabForPreview);
            }
        }

        private void OnPreviewModeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PreviewWebView.CoreWebView2 != null)
            {
                var tab = _activeTabProvider();
                if (tab != null)
                {
                    _tabPreviewModes[tab.Id] = PreviewModeCombo.SelectedIndex;
                }
                RenderActiveTab();
            }
        }

        private async void OnOpenPreviewInBrowserClick(object sender, RoutedEventArgs e)
        {
            await OpenInBrowserAsync();
        }

        private async Task OpenInBrowserAsync()
        {
            var tab = _activeTabProvider();
            if (tab == null)
            {
                _showErrorMessage("브라우저 열기", "브라우저로 열 활성 탭이 없습니다.");
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
                _showErrorMessage("브라우저 열기 실패", ex.Message);
            }
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
                var activeTab = _activeTabProvider();
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
                PreviewWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(reply));
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
                if (PreviewWebView.CoreWebView2 == null || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                string normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(_mappedPreviewDocumentDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.DocumentHostName,
                    normalizedDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
                _mappedPreviewDocumentDirectory = normalizedDirectory;
                _mappedDocumentDirectoriesByWebView[PreviewWebView.CoreWebView2] = normalizedDirectory;
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
                string headers = $"Content-Type: {PreviewWebResourceService.GetContentType(targetPath)}\r\nCache-Control: no-store";
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
