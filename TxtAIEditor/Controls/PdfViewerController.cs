using System;
using System.Collections.Generic;
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
    public sealed class PdfViewerController
    {
        private readonly ISettingsService _settingsService;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string, OpenedTab, int, int> _selectionContextUpdater;
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();

        public PdfViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider,
            Action<string, OpenedTab, int, int> selectionContextUpdater)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
            _selectionContextUpdater = selectionContextUpdater;
        }

        public IReadOnlyDictionary<string, WebView2> ViewerWebViews => _viewerWebViews;

        public void Register(OpenedTab tab, WebView2 pdfWebView)
        {
            _viewerWebViews[tab.Id] = pdfWebView;
            _ = InitializeAsync(tab, pdfWebView);
        }

        public bool IsActiveViewer()
        {
            return _activeTabProvider()?.IsPdfViewer == true;
        }

        public async Task<bool> FocusFindInActiveViewerAsync()
        {
            if (!TryGetActiveViewer(out var pdfWebView))
            {
                return false;
            }

            pdfWebView.Focus(FocusState.Programmatic);
            await TryTriggerFindAsync(pdfWebView);
            return true;
        }

        public bool Reload(OpenedTab tab)
        {
            if (!tab.IsPdfViewer || !_viewerWebViews.TryGetValue(tab.Id, out var pdfWebView))
            {
                return false;
            }

            pdfWebView.Reload();
            return true;
        }

        public void Close(string tabId)
        {
            if (_viewerWebViews.TryGetValue(tabId, out var pdfWebView))
            {
                pdfWebView.Close();
                _viewerWebViews.Remove(tabId);
            }
        }

        public void ApplyPreferredColorScheme(string theme)
        {
            foreach (var pdfWebView in _viewerWebViews.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(pdfWebView?.CoreWebView2, theme);
            }
        }

        private bool TryGetActiveViewer(out WebView2 pdfWebView)
        {
            pdfWebView = null!;
            var activeTab = _activeTabProvider();
            if (activeTab?.IsPdfViewer != true)
            {
                return false;
            }

            if (_viewerWebViews.TryGetValue(activeTab.Id, out var viewer) && viewer != null)
            {
                pdfWebView = viewer;
                return true;
            }

            return false;
        }

        private async Task InitializeAsync(OpenedTab tab, WebView2 pdfWebView)
        {
            try
            {
                var env = await MonacoBridge.GetSharedEnvironmentAsync();
                await pdfWebView.EnsureCoreWebView2Async(env);

                if (!_viewerWebViews.TryGetValue(tab.Id, out var registeredWebView) ||
                    !ReferenceEquals(registeredWebView, pdfWebView))
                {
                    return;
                }

                Configure(tab, pdfWebView);

                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    pdfWebView.Source = new Uri(tab.FilePath, UriKind.Absolute);
                }
            }
            catch
            {
            }
        }

        private void Configure(OpenedTab tab, WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            pdfWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            pdfWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            pdfWebView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            pdfWebView.CoreWebView2.WebMessageReceived += (_, args) => OnWebMessageReceived(tab, args);

            WebViewAppearanceService.ApplyPreferredColorScheme(pdfWebView.CoreWebView2, _settingsService.CurrentSettings.Theme);

            _ = InstallSelectionBridgeAsync(pdfWebView);
        }

        private static async Task InstallSelectionBridgeAsync(WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await pdfWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SelectionBridgeScript);
                await pdfWebView.CoreWebView2.ExecuteScriptAsync(SelectionBridgeScript);
            }
            catch
            {
            }
        }

        private void OnWebMessageReceived(OpenedTab tab, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) ||
                    !string.Equals(typeProp.GetString(), "pdfSelection", StringComparison.Ordinal))
                {
                    return;
                }

                string selectedText = root.TryGetProperty("text", out var textProp)
                    ? textProp.GetString() ?? string.Empty
                    : string.Empty;

                if (_activeTabProvider() == tab)
                {
                    _selectionContextUpdater(selectedText, tab, 0, 0);
                }
            }
            catch
            {
            }
        }

        private static async Task TryTriggerFindAsync(WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            const string keyDown = "{\"type\":\"keyDown\",\"modifiers\":2,\"windowsVirtualKeyCode\":70,\"nativeVirtualKeyCode\":70,\"code\":\"KeyF\",\"key\":\"f\"}";
            const string keyUp = "{\"type\":\"keyUp\",\"modifiers\":2,\"windowsVirtualKeyCode\":70,\"nativeVirtualKeyCode\":70,\"code\":\"KeyF\",\"key\":\"f\"}";

            try
            {
                await pdfWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
                await pdfWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
            }
            catch
            {
            }
        }

        private const string SelectionBridgeScript = @"
(() => {
    if (window.__txtAiEditorPdfSelectionBridge) return;
    window.__txtAiEditorPdfSelectionBridge = true;
    let lastText = '';
    let timer = 0;

    function selectedTextFromRoot(root) {
        try {
            const selection = root && root.getSelection ? root.getSelection() : null;
            const text = selection ? String(selection.toString() || '') : '';
            if (text) return text;
        } catch {}

        try {
            const active = root && root.activeElement;
            if (active && active.shadowRoot) {
                return selectedTextFromRoot(active.shadowRoot);
            }
        } catch {}

        return '';
    }

    function readSelection() {
        let text = selectedTextFromRoot(window);
        if (!text) {
            try {
                const viewer = document.querySelector('pdf-viewer');
                if (viewer && viewer.shadowRoot) text = selectedTextFromRoot(viewer.shadowRoot);
            } catch {}
        }

        if (text !== lastText) {
            lastText = text;
            try {
                chrome.webview.postMessage({ type: 'pdfSelection', text });
            } catch {}
        }
    }

    function scheduleRead() {
        clearTimeout(timer);
        timer = setTimeout(readSelection, 80);
    }

    document.addEventListener('selectionchange', scheduleRead, true);
    document.addEventListener('mouseup', scheduleRead, true);
    document.addEventListener('pointerup', scheduleRead, true);
    document.addEventListener('keyup', scheduleRead, true);
    window.addEventListener('focus', scheduleRead, true);
    scheduleRead();
})();
";
    }
}
