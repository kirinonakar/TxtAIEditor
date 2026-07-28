using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
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
        private readonly Dictionary<string, NotebookSession> _sessions = new();

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
            string? dir = null;
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                dir = Path.GetDirectoryName(tab.FilePath);
            }
            dir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (_sessions.Remove(tab.Id, out NotebookSession? existingSession))
            {
                _ = existingSession.DisposeAsync();
            }

            var session = new NotebookSession(
                tab.Id,
                webView,
                _kernelService.ResolvePythonExecutable(dir),
                dir);
            _sessions[tab.Id] = session;

            _ = InitializeAsync(tab, session);
        }

        public bool IsActiveViewer()
        {
            return _activeTabProvider()?.IsNotebookViewer == true;
        }

        public async Task<bool> FocusFindInActiveViewerAsync()
        {
            var activeTab = _activeTabProvider();
            if (activeTab?.IsNotebookViewer != true ||
                !_sessions.TryGetValue(activeTab.Id, out var session) ||
                session.WebView.CoreWebView2 == null)
            {
                return false;
            }

            WebView2 webView = session.WebView;
            webView.Focus(FocusState.Programmatic);
            await ExecuteScriptSafeAsync(
                webView,
                "window.__txtAiEditorNotebookFind && window.__txtAiEditorNotebookFind.open();");
            return true;
        }

        public async Task ApplyMarkdownCommandAsync(string tabId, string command, string? color = null)
        {
            if (_sessions.TryGetValue(tabId, out var session) &&
                session.WebView.CoreWebView2 != null)
            {
                WebView2 webView = session.WebView;
                var payload = JsonSerializer.Serialize(new { command, color });
                await webView.CoreWebView2.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('appMarkdownCommand', {{ detail: {payload} }}));");
                webView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }
        }
        public async Task<bool> SaveAsync(OpenedTab tab)
        {
            if (!tab.IsNotebookViewer ||
                !_sessions.TryGetValue(tab.Id, out var session))
            {
                return false;
            }

            WebView2 webView = session.WebView;
            if (webView.CoreWebView2 == null || string.IsNullOrEmpty(tab.FilePath))
            {
                return false;
            }

            try
            {
                string script = "typeof window.getNotebookJson === 'function' ? window.getNotebookJson() : ''";
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
            if (!tab.IsNotebookViewer ||
                !_sessions.TryGetValue(tab.Id, out var session))
            {
                return false;
            }

            tab.IsDirty = false;
            _ = NavigateAsync(tab, session);
            return true;
        }

        public void Close(string tabId)
        {
            if (_sessions.Remove(tabId, out NotebookSession? session))
            {
                _ = session.DisposeAsync();
            }
        }

        public void ApplyPreferredColorScheme(string theme)
        {
            foreach (NotebookSession session in _sessions.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(
                    session.WebView.CoreWebView2,
                    theme);
            }
        }

        private async Task InitializeAsync(OpenedTab tab, NotebookSession session)
        {
            try
            {
                WebView2 webView = session.WebView;
                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await webView.EnsureCoreWebView2Async(env);

                if (!_sessions.TryGetValue(tab.Id, out var registeredSession) ||
                    !ReferenceEquals(registeredSession, session))
                {
                    return;
                }

                await ConfigureAsync(session);
                await NavigateAsync(tab, session);
            }
            catch
            {
            }
        }

        private async Task ConfigureAsync(NotebookSession session)
        {
            WebView2 webView = session.WebView;
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                PreviewWebResourceService.ResourceHostName,
                PreviewWebResourceService.WebResourcesPath,
                CoreWebView2HostResourceAccessKind.Allow);
            void OnSessionWebMessageReceived(
                WebView2 sender,
                CoreWebView2WebMessageReceivedEventArgs args) =>
                OnWebMessageReceived(session, args);
            webView.WebMessageReceived += OnSessionWebMessageReceived;
            session.AttachWebMessageHandler(
                () => webView.WebMessageReceived -= OnSessionWebMessageReceived);
            WebViewAppearanceService.ApplyPreferredColorScheme(webView.CoreWebView2, _settingsService.CurrentSettings.Theme);
            await InstallShortcutBridgeAsync(webView);
        }

        private void OnWebMessageReceived(
            NotebookSession session,
            CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                WebView2 sender = session.WebView;
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

                var tab = _activeTabProvider();
                if (tab == null || tab.Id != session.TabId)
                {
                    return;
                }

                if (string.Equals(type, "executeCell", StringComparison.Ordinal))
                {
                    string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    int cellIndex = root.TryGetProperty("cellIndex", out var ci) ? ci.GetInt32() : 0;
                    _ = ExecuteCellAsync(session, cellIndex, code);
                }
                else if (string.Equals(type, "stopExecution", StringComparison.Ordinal))
                {
                    session.InterruptKernel();
                }
                else if (string.Equals(type, "inputReply", StringComparison.Ordinal))
                {
                    string val = root.TryGetProperty("value", out var vVal) ? vVal.GetString() ?? "" : "";
                    _ = session.SendInputReplyAsync(val);
                }
                else if (string.Equals(type, "getVariables", StringComparison.Ordinal))
                {
                    _ = GetVariablesAsync(session);
                }
                else if (string.Equals(type, "updatePlotView", StringComparison.Ordinal))
                {
                    string figId = root.TryGetProperty("figId", out var f) ? f.GetString() ?? "" : "";
                    double elev = root.TryGetProperty("elev", out var el) ? el.GetDouble() : 30.0;
                    double azim = root.TryGetProperty("azim", out var az) ? az.GetDouble() : -60.0;
                    double panFracX = root.TryGetProperty("panFracX", out var pfx3d) ? pfx3d.GetDouble() : 0.0;
                    double panFracY = root.TryGetProperty("panFracY", out var pfy3d) ? pfy3d.GetDouble() : 0.0;
                    double zoom = root.TryGetProperty("zoom", out var z3d) ? z3d.GetDouble() : 1.0;
                    _ = UpdatePlotViewAsync(session, figId, elev, azim, panFracX, panFracY, zoom);
                }
                else if (string.Equals(type, "update2DView", StringComparison.Ordinal))
                {
                    string figId = root.TryGetProperty("figId", out var f2d) ? f2d.GetString() ?? "" : "";
                    double panFracX = root.TryGetProperty("panFracX", out var pfx) ? pfx.GetDouble() : 0.0;
                    double panFracY = root.TryGetProperty("panFracY", out var pfy) ? pfy.GetDouble() : 0.0;
                    double zoom = root.TryGetProperty("zoom", out var z) ? z.GetDouble() : 1.0;
                    _ = Update2DViewAsync(session, figId, panFracX, panFracY, zoom);
                }
                else if (string.Equals(type, "saveNotebook", StringComparison.Ordinal))
                {
                    string content = root.TryGetProperty("content", out var cont) ? cont.GetString() ?? "" : "";
                    _ = SaveNotebookAsync(sender, tab, content);
                }
                else if (string.Equals(type, "exportPy", StringComparison.Ordinal))
                {
                    string content = root.TryGetProperty("content", out var cont) ? cont.GetString() ?? "" : "";
                    _ = ExportPyAsync(sender, tab, content);
                }
                else if (string.Equals(type, "savePlotImage", StringComparison.Ordinal))
                {
                    string imageData = root.TryGetProperty("imageData", out var id) ? id.GetString() ?? "" : "";
                    string suggestedName = root.TryGetProperty("suggestedName", out var sn) ? sn.GetString() ?? "" : "";
                    _ = SavePlotImageAsync(sender, tab, imageData, suggestedName);
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

        private async Task UpdatePlotViewAsync(
            NotebookSession session,
            string figId,
            double elev,
            double azim,
            double panFracX,
            double panFracY,
            double zoom)
        {
            try
            {
                WebView2 webView = session.WebView;
                string html = await session.RunKernelAsync(
                    _kernelService,
                    kernel => kernel.UpdatePlotViewAsync(
                        figId,
                        elev,
                        azim,
                        panFracX,
                        panFracY,
                        zoom));
                if (!string.IsNullOrEmpty(html))
                {
                    string js = $"window.__notebookReceivePlotUpdate && window.__notebookReceivePlotUpdate({JsonSerializer.Serialize(figId)}, {JsonSerializer.Serialize(html)});";
                    webView.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            await webView.ExecuteScriptAsync(js);
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private async Task Update2DViewAsync(
            NotebookSession session,
            string figId,
            double panFracX,
            double panFracY,
            double zoom)
        {
            try
            {
                WebView2 webView = session.WebView;
                string html = await session.RunKernelAsync(
                    _kernelService,
                    kernel => kernel.Update2DViewAsync(
                        figId,
                        panFracX,
                        panFracY,
                        zoom));
                if (!string.IsNullOrEmpty(html))
                {
                    string js = $"window.__notebookReceivePlotUpdate && window.__notebookReceivePlotUpdate({JsonSerializer.Serialize(figId)}, {JsonSerializer.Serialize(html)});";
                    webView.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            await webView.ExecuteScriptAsync(js);
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private async Task ExecuteCellAsync(
            NotebookSession session,
            int cellIndex,
            string code)
        {
            WebView2 webView = session.WebView;
            try
            {
                var result = await session.RunKernelAsync(
                    _kernelService,
                    kernel => kernel.ExecuteAsync(code, async (prompt) =>
                    {
                        string script = $"window.__notebookReceiveInputRequest && window.__notebookReceiveInputRequest({cellIndex}, {JsonSerializer.Serialize(prompt)});";
                        webView.DispatcherQueue.TryEnqueue(async () =>
                        {
                            try { await webView.ExecuteScriptAsync(script); } catch { }
                        });
                        await Task.CompletedTask;
                    }, async (streamName, streamText) =>
                    {
                        string script = $"window.__notebookReceiveStreamOutput && window.__notebookReceiveStreamOutput({cellIndex}, {JsonSerializer.Serialize(streamName)}, {JsonSerializer.Serialize(streamText)});";
                        await ExecuteScriptOnDispatcherAsync(webView, script);
                    }));

                await SendResultAsync(webView, cellIndex, result.Status, result.Stdout, result.Stderr, result.Result, result.VariablesJson);
            }
            catch (Exception ex)
            {
                await SendResultAsync(webView, cellIndex, "error", "", ex.Message, "", "[]");
            }
        }

        private async Task GetVariablesAsync(NotebookSession session)
        {
            WebView2 webView = session.WebView;
            try
            {
                string varsJson = await session.RunKernelAsync(
                    _kernelService,
                    kernel => kernel.GetVariablesAsync());
                await SendVariablesAsync(webView, varsJson);
            }
            catch
            {
                await SendVariablesAsync(webView, "[]");
            }
        }

        private static async Task SendVariablesAsync(WebView2 webView, string varsJson)
        {
            string script = $"window.__notebookReceiveVariables && window.__notebookReceiveVariables({varsJson});";
            await ExecuteScriptSafeAsync(webView, script);
        }

        private static async Task SendResultAsync(WebView2 webView, int cellIndex, string status, string stdout, string stderr, string result, string variablesJson = "[]")
        {
            string resultJson = JsonSerializer.Serialize(new
            {
                status = status,
                stdout = stdout,
                stderr = stderr,
                result = result
            });

            string script = $"window.__notebookReceiveResult && window.__notebookReceiveResult({cellIndex}, {resultJson}, {variablesJson});";
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

        private async Task ExportPyAsync(WebView2 webView, OpenedTab tab, string content)
        {
            try
            {
                string defaultFileName = !string.IsNullOrEmpty(tab.FilePath)
                    ? Path.GetFileNameWithoutExtension(tab.FilePath) + ".py"
                    : "notebook.py";

                webView.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var picker = new FileSavePicker
                        {
                            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                            SuggestedFileName = defaultFileName
                        };

                        IntPtr hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                        if (hwnd != IntPtr.Zero)
                        {
                            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        }

                        picker.FileTypeChoices.Add("Python Script", new List<string> { ".py" });

                        var file = await picker.PickSaveFileAsync();
                        if (file != null)
                        {
                            await File.WriteAllTextAsync(file.Path, content, Utf8NoBom);
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
            await Task.CompletedTask;
        }

        private async Task SavePlotImageAsync(WebView2 webView, OpenedTab tab, string imageData, string suggestedName)
        {
            bool success = false;
            string savedFileName = string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(imageData))
                {
                    string base64 = imageData;
                    int commaIdx = base64.IndexOf(',');
                    if (commaIdx >= 0)
                    {
                        base64 = base64.Substring(commaIdx + 1);
                    }
                    byte[] bytes = Convert.FromBase64String(base64);

                    string? dir = null;
                    if (_sessions.TryGetValue(tab.Id, out NotebookSession? session) &&
                        Directory.Exists(session.WorkingDirectory))
                    {
                        dir = session.WorkingDirectory;
                    }
                    else if (!string.IsNullOrEmpty(tab.FilePath))
                    {
                        dir = Path.GetDirectoryName(tab.FilePath);
                    }
                    dir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                    string prefix = !string.IsNullOrEmpty(tab.FilePath)
                        ? Path.GetFileNameWithoutExtension(tab.FilePath) + "_plot"
                        : "plot";

                    string ext = ".png";
                    if (imageData.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                        imageData.StartsWith("data:image/jpg", StringComparison.OrdinalIgnoreCase))
                    {
                        ext = ".jpg";
                    }

                    string candidateName = $"{prefix}{ext}";
                    string fullPath = Path.Combine(dir, candidateName);
                    int counter = 1;
                    while (File.Exists(fullPath))
                    {
                        candidateName = $"{prefix}_{counter}{ext}";
                        fullPath = Path.Combine(dir, candidateName);
                        counter++;
                    }

                    await File.WriteAllBytesAsync(fullPath, bytes);
                    savedFileName = candidateName;
                    success = true;
                }
            }
            catch
            {
                success = false;
            }

            string script = $"window.__notebookPlotSavedResult && window.__notebookPlotSavedResult({success.ToString().ToLowerInvariant()}, {JsonSerializer.Serialize(savedFileName)});";
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

        private static Task ExecuteScriptOnDispatcherAsync(WebView2 webView, string script)
        {
            if (webView.DispatcherQueue.HasThreadAccess)
            {
                return ExecuteScriptSafeAsync(webView, script);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = webView.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await ExecuteScriptSafeAsync(webView, script);
                }
                finally
                {
                    completion.TrySetResult(true);
                }
            });

            if (!enqueued)
            {
                completion.TrySetResult(false);
            }

            return completion.Task;
        }

        private async Task NavigateAsync(
            OpenedTab tab,
            NotebookSession session)
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return;
            }

            try
            {
                string html = await _viewerService.BuildHtmlAsync(tab.FilePath);
                string htmlPath = await WriteViewerHtmlAsync(session, html);
                session.WebView.Source = new Uri(htmlPath, UriKind.Absolute);
            }
            catch
            {
            }
        }

        private static async Task<string> WriteViewerHtmlAsync(
            NotebookSession session,
            string html)
        {
            string folder = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "NotebookViewer");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, session.TabId + ".html");
            await File.WriteAllTextAsync(path, html, Encoding.UTF8);
            session.ReplaceHtmlPath(path);
            return path;
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

        if (key === 'f7' || code === 'F7') {
            event.preventDefault();
            event.stopPropagation();
            if (event.stopImmediatePropagation) event.stopImmediatePropagation();
            return;
        }

        let name = '';

        if (!ctrl && !alt) {
            if (key === 'f3' || code === 'F3') {
                name = 'f3';
            } else if (key === 'f4' || code === 'F4') {
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
                if (!shift && window.__txtAiEditorNotebookFind &&
                    window.__txtAiEditorNotebookFind.open()) {
                    event.preventDefault();
                    event.stopPropagation();
                    if (event.stopImmediatePropagation) {
                        event.stopImmediatePropagation();
                    }
                    return;
                }
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
