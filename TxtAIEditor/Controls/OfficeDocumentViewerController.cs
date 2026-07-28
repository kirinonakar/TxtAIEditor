using System;
using System.Collections.Generic;
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
    public sealed class OfficeDocumentViewerController
    {
        private readonly ISettingsService _settingsService;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string> _shortcutHandler;
        private readonly OfficeDocumentViewerService _viewerService;
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();
        private readonly Dictionary<string, string> _viewerHtmlPaths = new Dictionary<string, string>();

        public OfficeDocumentViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider,
            Action<string> shortcutHandler,
            Func<string, string, string> getString)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
            _shortcutHandler = shortcutHandler;
            _viewerService = new OfficeDocumentViewerService(getString);
        }

        public void Register(OpenedTab tab, WebView2 webView)
        {
            _viewerWebViews[tab.Id] = webView;
            _ = InitializeAsync(tab, webView);
        }

        public bool IsActiveViewer()
        {
            return _activeTabProvider()?.IsOfficeDocumentViewer == true;
        }

        public async Task<bool> FocusFindInActiveViewerAsync()
        {
            if (!TryGetActiveViewer(out var webView))
            {
                return false;
            }

            webView.Focus(FocusState.Programmatic);
            await TriggerViewerFindAsync(webView);
            return true;
        }

        public bool Reload(OpenedTab tab)
        {
            if (!tab.IsOfficeDocumentViewer || !_viewerWebViews.TryGetValue(tab.Id, out var webView))
            {
                return false;
            }

            _ = NavigateAsync(tab, webView);
            return true;
        }

        public void Close(string tabId)
        {
            if (_viewerWebViews.TryGetValue(tabId, out var webView))
            {
                webView.Close();
                _viewerWebViews.Remove(tabId);
            }

            DeleteViewerHtml(tabId);
        }

        public void ApplyPreferredColorScheme(string theme)
        {
            foreach (var webView in _viewerWebViews.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(webView?.CoreWebView2, theme);
            }
        }

        private bool TryGetActiveViewer(out WebView2 webView)
        {
            webView = null!;
            var activeTab = _activeTabProvider();
            if (activeTab?.IsOfficeDocumentViewer != true)
            {
                return false;
            }

            if (_viewerWebViews.TryGetValue(activeTab.Id, out var viewer) && viewer != null)
            {
                webView = viewer;
                return true;
            }

            return false;
        }

        private async Task InitializeAsync(OpenedTab tab, WebView2 webView)
        {
            try
            {
                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await webView.EnsureCoreWebView2Async(env);

                if (!_viewerWebViews.TryGetValue(tab.Id, out var registeredWebView) ||
                    !ReferenceEquals(registeredWebView, webView))
                {
                    return;
                }

                await ConfigureAsync(webView);
                await NavigateAsync(tab, webView);
            }
            catch
            {
            }
        }

        private async Task ConfigureAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.WebMessageReceived += OnWebMessageReceived;
            WebViewAppearanceService.ApplyPreferredColorScheme(webView.CoreWebView2, _settingsService.CurrentSettings.Theme);
            await InstallShortcutBridgeAsync(webView);
        }

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) ||
                    !string.Equals(typeProp.GetString(), "shortcut", StringComparison.Ordinal) ||
                    !root.TryGetProperty("name", out var nameProp))
                {
                    return;
                }

                string name = nameProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sender.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                }
            }
            catch
            {
            }
        }

        private static async Task InstallShortcutBridgeAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutBridgeScript);
                await webView.CoreWebView2.ExecuteScriptAsync(ShortcutBridgeScript);
            }
            catch
            {
            }
        }

        private async Task NavigateAsync(OpenedTab tab, WebView2 webView)
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return;
            }

            try
            {
                string html = await _viewerService.BuildHtmlAsync(tab.FilePath);
                string htmlPath = await WriteViewerHtmlAsync(tab.Id, html);
                webView.Source = new Uri(htmlPath, UriKind.Absolute);
            }
            catch
            {
            }
        }

        private async Task<string> WriteViewerHtmlAsync(string tabId, string html)
        {
            DeleteViewerHtml(tabId);

            string folder = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "OfficeViewer");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, tabId + ".html");
            await File.WriteAllTextAsync(path, html, Encoding.UTF8);
            _viewerHtmlPaths[tabId] = path;
            return path;
        }

        private void DeleteViewerHtml(string tabId)
        {
            if (!_viewerHtmlPaths.TryGetValue(tabId, out string? path))
            {
                return;
            }

            _viewerHtmlPaths.Remove(tabId);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static async Task TriggerViewerFindAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                string result = await webView.CoreWebView2.ExecuteScriptAsync(
                    "Boolean(window.__txtAiEditorOfficeFind && window.__txtAiEditorOfficeFind.open && window.__txtAiEditorOfficeFind.open())");
                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
            }

            await TryTriggerNativeFindAsync(webView);
        }

        private static async Task TryTriggerNativeFindAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            const string keyDown = "{\"type\":\"keyDown\",\"modifiers\":2,\"windowsVirtualKeyCode\":70,\"nativeVirtualKeyCode\":70,\"code\":\"KeyF\",\"key\":\"f\"}";
            const string keyUp = "{\"type\":\"keyUp\",\"modifiers\":2,\"windowsVirtualKeyCode\":70,\"nativeVirtualKeyCode\":70,\"code\":\"KeyF\",\"key\":\"f\"}";

            try
            {
                await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
                await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
            }
            catch
            {
            }
        }

        private const string ShortcutBridgeScript = @"
(() => {
    if (window.__txtAiEditorOfficeShortcutBridge) return;
    window.__txtAiEditorOfficeShortcutBridge = true;

    function post(name) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'shortcut', name });
            }
        } catch {}
    }

    function handleKeyDown(event) {
        const ctrl = !!(event.ctrlKey || event.metaKey);
        const alt = !!event.altKey;
        const shift = !!event.shiftKey;
        const key = String(event.key || '').toLowerCase();
        const code = String(event.code || '');

        let name = '';

        if (!ctrl && !alt) {
            if (key === 'f3' || code === 'F3') {
                name = 'f3';
            } else if (key === 'f4' || code === 'F4') {
                name = 'f4';
            } else if (key === 'f7' || code === 'F7') {
                name = 'f7';
            } else if (key === 'f9' || code === 'F9') {
                name = 'f9';
            } else if (key === 'f10' || code === 'F10') {
                name = 'f10';
            } else if (key === 'f11' || code === 'F11') {
                name = 'f11';
            } else if (key === 'f12' || code === 'F12') {
                name = 'f12';
            }
        } else if (alt && !ctrl && !shift && (key === 'z' || code === 'KeyZ')) {
            name = 'wordWrap';
        } else if (ctrl && !alt) {
            if (key === '1' || code === 'Digit1' || code === 'Numpad1') {
                name = 'toggleLeftPanel';
            } else if (key === '2' || code === 'Digit2' || code === 'Numpad2') {
                name = 'toggleRightPanel';
            } else if (key === '3' || code === 'Digit3' || code === 'Numpad3') {
                name = 'expandRightPanel';
            } else if (key === 'n' || code === 'KeyN') {
                name = 'newTab';
            } else if (key === 's' || code === 'KeyS') {
                name = shift ? 'saveAs' : 'save';
            } else if (key === 'o' || code === 'KeyO') {
                name = 'open';
            } else if (key === 'w' || code === 'KeyW') {
                name = 'closeTab';
            } else if (key === 'p' || code === 'KeyP') {
                name = 'print';
            } else if (key === 'f' || code === 'KeyF') {
                name = shift ? 'searchAll' : 'find';
            } else if (code === 'Backquote' || key === '`' || key === '~' || key === 'dead') {
                name = 'terminal';
            }
        }

        if (!name) return;
        event.preventDefault();
        event.stopPropagation();
        if (event.stopImmediatePropagation) {
            event.stopImmediatePropagation();
        }
        post(name);
    }

    window.addEventListener('keydown', handleKeyDown, true);
    document.addEventListener('keydown', handleKeyDown, true);
})();
";
    }
}
