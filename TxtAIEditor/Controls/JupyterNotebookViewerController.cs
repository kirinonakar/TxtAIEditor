using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Composition;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class JupyterNotebookViewerController
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly ISettingsService _settingsService;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string> _shortcutHandler;
        private readonly Func<string, string, string> _getString;
        private readonly Action? _updateWindowTitle;
        private readonly JupyterNotebookViewerService _viewerService;
        private readonly JupyterNotebookKernelService _kernelService;
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();
        private readonly Dictionary<string, string> _viewerHtmlPaths = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _tabPythonExecutables = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _tabWorkingDirectories = new Dictionary<string, string>();
        private readonly Dictionary<WebView2, string> _webViewToTabId = new Dictionary<WebView2, string>();

        public JupyterNotebookViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider,
            Action<string> shortcutHandler,
            Func<string, string, string> getString,
            JupyterNotebookKernelService kernelService,
            Action? updateWindowTitle = null)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
            _shortcutHandler = shortcutHandler;
            _getString = getString;
            _kernelService = kernelService;
            _updateWindowTitle = updateWindowTitle;
            _viewerService = new JupyterNotebookViewerService(getString);
        }

        public void Register(OpenedTab tab, WebView2 webView)
        {
            _viewerWebViews[tab.Id] = webView;
            _webViewToTabId[webView] = tab.Id;

            string? dir = null;
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                dir = Path.GetDirectoryName(tab.FilePath);
            }
            dir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            _tabWorkingDirectories[tab.Id] = dir;
            _tabPythonExecutables[tab.Id] = _kernelService.ResolvePythonExecutable(dir);

            _ = InitializeAsync(tab, webView);
        }

        public bool IsActiveViewer()
        {
            return _activeTabProvider()?.IsNotebookViewer == true;
        }

        public async Task ApplyMarkdownCommandAsync(string tabId, string command, string? color = null)
        {
            if (_viewerWebViews.TryGetValue(tabId, out var webView) && webView.CoreWebView2 != null)
            {
                var payload = JsonSerializer.Serialize(new { command, color });
                await webView.CoreWebView2.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('appMarkdownCommand', {{ detail: {payload} }}));");
                webView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }
        }
public async Task<bool> SaveAsync(OpenedTab tab)
        {
            if (!tab.IsNotebookViewer || !_viewerWebViews.TryGetValue(tab.Id, out var webView))
            {
                return false;
            }

            if (webView.CoreWebView2 == null || string.IsNullOrEmpty(tab.FilePath))
            {
                return false;
            }

            try
            {
                string script = "(() => { const container = document.getElementById('cells-container'); const cells = []; container.querySelectorAll('.cell').forEach(cellDiv => { const type = cellDiv.getAttribute('data-cell-type'); const input = cellDiv.querySelector('.cell-input-area, .markdown-cell, .raw-cell'); const source = input ? input.innerText : ''; const sourceLines = source.split('\\n').map((l, i, arr) => i < arr.length - 1 ? l + '\\n' : l); if (type === 'code') { cells.push({cell_type:'code',source:sourceLines,outputs:[],metadata:{},execution_count:null}); } else if (type === 'markdown') { cells.push({cell_type:'markdown',source:sourceLines,metadata:{}}); } else { cells.push({cell_type:'raw',source:sourceLines,metadata:{}}); } }); return JSON.stringify({cells:cells,metadata:{},nbformat:4,nbformat_minor:5}, null, 1); })();";
                string jsonResult = await webView.CoreWebView2.ExecuteScriptAsync(script);

                string? content = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonResult);
                    content = doc.RootElement.GetString();
                }
                catch
                {
                    return false;
                }

                if (string.IsNullOrEmpty(content))
                {
                    return false;
                }

                await File.WriteAllTextAsync(tab.FilePath, content, Utf8NoBom);
                tab.IsDirty = false;
                webView.DispatcherQueue.TryEnqueue(() => _updateWindowTitle?.Invoke());
                await ExecuteScriptSafeAsync(webView, "window.__notebookSaveResult && window.__notebookSaveResult(true, '');");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool Reload(OpenedTab tab)
        {
            if (!tab.IsNotebookViewer || !_viewerWebViews.TryGetValue(tab.Id, out var webView))
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

            _kernelService.CloseSession(tabId);
            _tabPythonExecutables.Remove(tabId);
            _tabWorkingDirectories.Remove(tabId);
            DeleteViewerHtml(tabId);
        }

        public void ApplyPreferredColorScheme(string theme)
        {
            foreach (var webView in _viewerWebViews.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(webView?.CoreWebView2, theme);
            }
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
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.WebMessageReceived += OnWebMessageReceived;
            WebViewAppearanceService.ApplyPreferredColorScheme(webView.CoreWebView2, _settingsService.CurrentSettings.Theme);
            await InstallShortcutBridgeAsync(webView);
        }

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string raw = MainWindowMessageJson.Normalize(args);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                {
                    return;
                }

                string type = typeProp.GetString() ?? string.Empty;

                if (!_webViewToTabId.TryGetValue(sender, out string? tabId))
                {
                    return;
                }

                var tab = _activeTabProvider();
                if (tab == null || tab.Id != tabId)
                {
                    return;
                }

                if (string.Equals(type, "executeCell", StringComparison.Ordinal))
                {
                    string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    int cellIndex = root.TryGetProperty("cellIndex", out var ci) ? ci.GetInt32() : 0;
                    _ = ExecuteCellAsync(sender, tab, cellIndex, code);
                }
                else if (string.Equals(type, "saveNotebook", StringComparison.Ordinal))
                {
                    string content = root.TryGetProperty("content", out var cont) ? cont.GetString() ?? "" : "";
                    _ = SaveNotebookAsync(sender, tab, content);
                }
                else if (string.Equals(type, "markDirty", StringComparison.Ordinal))
                {
                    sender.DispatcherQueue.TryEnqueue(() =>
                    {
                        tab.IsDirty = true;
                        _updateWindowTitle?.Invoke();
                    });
                }
                else if (string.Equals(type, "shortcut", StringComparison.Ordinal))
                {
                    string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        sender.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                    }
                }
            }
            catch
            {
            }
        }

        private async Task ExecuteCellAsync(WebView2 webView, OpenedTab tab, int cellIndex, string code)
        {
            try
            {
                if (!_tabPythonExecutables.TryGetValue(tab.Id, out var python) ||
                    !_tabWorkingDirectories.TryGetValue(tab.Id, out var workDir))
                {
                    await SendResultAsync(webView, cellIndex, "error", "", "Kernel session not found.", "");
                    return;
                }

                var result = await _kernelService.ExecuteAsync(tab.Id, python, workDir, code);

                await SendResultAsync(webView, cellIndex, result.Status, result.Stdout, result.Stderr, result.Result);
            }
            catch (Exception ex)
            {
                await SendResultAsync(webView, cellIndex, "error", "", ex.Message, "");
            }
        }

        private static async Task SendResultAsync(WebView2 webView, int cellIndex, string status, string stdout, string stderr, string result)
        {
            string resultJson = JsonSerializer.Serialize(new
            {
                status = status,
                stdout = stdout,
                stderr = stderr,
                result = result
            });

            string script = $"window.__notebookReceiveResult && window.__notebookReceiveResult({cellIndex}, {resultJson});";
            await ExecuteScriptSafeAsync(webView, script);
        }

        private async Task SaveNotebookAsync(WebView2 webView, OpenedTab tab, string content)
        {
            bool success = false;
            try
            {
                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    await File.WriteAllTextAsync(tab.FilePath, content, Utf8NoBom);
                    tab.IsDirty = false;
                    success = true;
                }
            }
            catch
            {
                success = false;
            }

            if (success)
            {
                webView.DispatcherQueue.TryEnqueue(() =>
                {
                    _updateWindowTitle?.Invoke();
                });
            }

            string script = $"window.__notebookSaveResult && window.__notebookSaveResult({success.ToString().ToLowerInvariant()}, '');";
            await ExecuteScriptSafeAsync(webView, script);
        }

        private static async Task ExecuteScriptSafeAsync(WebView2 webView, string script)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync(script);
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

            string folder = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "NotebookViewer");
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

        private const string ShortcutBridgeScript = @"
(() => {
    if (window.__txtAiEditorNotebookShortcutBridge) return;
    window.__txtAiEditorNotebookShortcutBridge = true;

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
            if (key === 'f4' || code === 'F4') {
                name = 'f4';
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