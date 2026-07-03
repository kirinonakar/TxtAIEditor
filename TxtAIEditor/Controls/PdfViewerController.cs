using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly Func<string, string, string> _getString;
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();

        public PdfViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider,
            Action<string, OpenedTab, int, int> selectionContextUpdater,
            Func<string, string, string> getString)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
            _selectionContextUpdater = selectionContextUpdater;
            _getString = getString;
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
            await TriggerPdfFindPanelAsync(pdfWebView);
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

        public async Task NavigateToPageAsync(OpenedTab tab, int pageNumber)
        {
            if (!tab.IsPdfViewer ||
                string.IsNullOrWhiteSpace(tab.FilePath) ||
                !_viewerWebViews.TryGetValue(tab.Id, out var pdfWebView))
            {
                return;
            }

            pageNumber = Math.Max(1, pageNumber);
            try
            {
                if (pdfWebView.CoreWebView2 == null)
                {
                    var env = await MonacoBridge.GetSharedEnvironmentAsync();
                    await pdfWebView.EnsureCoreWebView2Async(env);
                }

                pdfWebView.Focus(FocusState.Programmatic);
                if (!await TryNavigatePdfViewerPageAsync(pdfWebView, pageNumber))
                {
                    await TryNavigatePdfViewerPageWithKeyboardAsync(pdfWebView, pageNumber);
                }
            }
            catch
            {
            }
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
            pdfWebView.CoreWebView2.WebMessageReceived += (_, args) => OnWebMessageReceived(tab, pdfWebView, args);

            WebViewAppearanceService.ApplyPreferredColorScheme(pdfWebView.CoreWebView2, _settingsService.CurrentSettings.Theme);

            _ = InstallSelectionBridgeAsync(pdfWebView);
            _ = InstallPdfFindBridgeAsync(pdfWebView, BuildPdfFindBridgeScript());
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

        private void OnWebMessageReceived(OpenedTab tab, WebView2 pdfWebView, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                {
                    return;
                }

                string type = typeProp.GetString() ?? string.Empty;
                if (string.Equals(type, "pdfFind", StringComparison.Ordinal))
                {
                    string action = root.TryGetProperty("action", out var actionProp)
                        ? actionProp.GetString() ?? string.Empty
                        : string.Empty;
                    string query = root.TryGetProperty("query", out var queryProp)
                        ? queryProp.GetString() ?? string.Empty
                        : string.Empty;
                    bool matchCase = root.TryGetProperty("matchCase", out var matchCaseProp) &&
                        matchCaseProp.ValueKind == JsonValueKind.True;

                    _ = HandlePdfFindMessageAsync(pdfWebView, action, query, matchCase);
                    return;
                }

                if (!string.Equals(type, "pdfSelection", StringComparison.Ordinal))
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

        private async Task TriggerPdfFindPanelAsync(WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await InstallPdfFindBridgeAsync(pdfWebView, BuildPdfFindBridgeScript());
                string result = await pdfWebView.CoreWebView2.ExecuteScriptAsync(
                    "Boolean(window.__txtAiEditorPdfFind && window.__txtAiEditorPdfFind.open && window.__txtAiEditorPdfFind.open())");
                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
            }

            await TryTriggerNativeFindAsync(pdfWebView);
        }

        private static async Task InstallPdfFindBridgeAsync(WebView2 pdfWebView, string script)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await pdfWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                await pdfWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch
            {
            }
        }

        private async Task HandlePdfFindMessageAsync(
            WebView2 pdfWebView,
            string action,
            string query,
            bool matchCase)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                var find = pdfWebView.CoreWebView2.Find;
                switch (action)
                {
                    case "start":
                        if (string.IsNullOrEmpty(query))
                        {
                            find.Stop();
                            await SendPdfFindStateAsync(pdfWebView);
                            return;
                        }

                        var options = pdfWebView.CoreWebView2.Environment.CreateFindOptions();
                        options.FindTerm = query;
                        options.IsCaseSensitive = matchCase;
                        options.ShouldHighlightAllMatches = true;
                        options.ShouldMatchWord = false;
                        options.SuppressDefaultFindDialog = true;
                        find.Stop();
                        await find.StartAsync(options);
                        await RefocusFindInputAsync(pdfWebView);
                        break;
                    case "next":
                        find.FindNext();
                        await Task.Delay(60);
                        break;
                    case "previous":
                        find.FindPrevious();
                        await Task.Delay(60);
                        break;
                    case "close":
                        find.Stop();
                        break;
                }

                await SendPdfFindStateAsync(pdfWebView);
            }
            catch
            {
            }
        }

        private static async Task SendPdfFindStateAsync(WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                int matchCount = pdfWebView.CoreWebView2.Find.MatchCount;
                int activeMatchIndex = pdfWebView.CoreWebView2.Find.ActiveMatchIndex;
                string script = "window.__txtAiEditorPdfFind && window.__txtAiEditorPdfFind.update(" +
                    matchCount.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    activeMatchIndex.ToString(CultureInfo.InvariantCulture) +
                    ")";
                await pdfWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch
            {
            }
        }

        private static async Task RefocusFindInputAsync(WebView2 pdfWebView)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await pdfWebView.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('txt-pdf-find-input')?.focus()");
            }
            catch
            {
            }
        }

        private string BuildPdfFindBridgeScript()
        {
            string optionsJson = JsonSerializer.Serialize(new
            {
                findPlaceholder = _getString("EditorFindPlaceholder", "찾기"),
                findClearTooltip = _getString("EditorFindClearTooltip", "지우기"),
                findMatchCaseTooltip = _getString("EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)"),
                findPrevTooltip = _getString("EditorFindPrevTooltip", "이전"),
                findNextTooltip = _getString("EditorFindNextTooltip", "다음"),
                findCloseTooltip = _getString("EditorFindCloseTooltip", "닫기")
            });

            return $$"""
(() => {
    if (window.__txtAiEditorPdfFind) return;

    const options = {{optionsJson}};
    let panel = null;
    let input = null;
    let clearButton = null;
    let matchCaseButton = null;
    let previousButton = null;
    let nextButton = null;
    let closeButton = null;
    let status = null;
    let matchCase = false;
    let lastSearchedQuery = null;
    let lastSearchedCase = false;

    function post(action, payload = {}) {
        try {
            chrome.webview.postMessage({ type: 'pdfFind', action, ...payload });
        } catch {}
    }

    function ensureStyle() {
        if (document.getElementById('txt-pdf-find-style')) return;
        const style = document.createElement('style');
        style.id = 'txt-pdf-find-style';
        style.textContent = `
#txt-pdf-find-panel {
    position: fixed;
    top: 10px;
    right: 18px;
    z-index: 2147483647;
    display: flex;
    align-items: center;
    gap: 6px;
    max-width: min(388px, calc(100vw - 24px));
    padding: 6px;
    border: 1px solid color-mix(in srgb, CanvasText 35%, transparent);
    border-radius: 4px;
    background: color-mix(in srgb, Canvas 92%, CanvasText 8%);
    color: CanvasText;
    box-shadow: 0 8px 24px rgba(0, 0, 0, .28);
    font: 12px/1.2 system-ui, "Segoe UI", sans-serif;
}
#txt-pdf-find-panel[hidden] { display: none; }
#txt-pdf-find-panel * { box-sizing: border-box; }
#txt-pdf-find-input-wrap {
    position: relative;
    display: flex;
    align-items: center;
    min-width: 168px;
    width: min(228px, calc(100vw - 156px));
}
#txt-pdf-find-input {
    width: 100%;
    height: 28px;
    border: 1px solid color-mix(in srgb, CanvasText 45%, transparent);
    border-radius: 3px;
    padding: 4px 36px 4px 8px;
    outline: 0;
    background: Canvas;
    color: CanvasText;
    font: inherit;
}
#txt-pdf-find-input:focus {
    border-color: rgba(0, 120, 212, .75);
    box-shadow: 0 0 0 1px rgba(0, 120, 212, .45);
}
#txt-pdf-find-input-actions {
    position: absolute;
    right: 5px;
    z-index: 1;
    display: flex;
    gap: 2px;
    align-items: center;
}
.txt-pdf-find-clear,
.txt-pdf-find-option,
.txt-pdf-find-button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    height: 20px;
    border-radius: 3px;
    background: transparent;
    color: CanvasText;
    cursor: pointer;
    font: inherit;
}
.txt-pdf-find-clear,
.txt-pdf-find-option {
    min-width: 20px;
    padding: 0 4px;
    border: 1px solid transparent;
    opacity: .5;
    font-weight: 700;
}
.txt-pdf-find-clear {
    width: 20px;
    padding: 0;
    font-size: 14px;
    font-weight: 400;
    line-height: 1;
}
.txt-pdf-find-button {
    width: 28px;
    height: 28px;
    padding: 0;
    border: 1px solid color-mix(in srgb, CanvasText 28%, transparent);
    font-size: 13px;
    line-height: 1;
}
.txt-pdf-find-clear:hover,
.txt-pdf-find-option:hover,
.txt-pdf-find-button:hover {
    background: color-mix(in srgb, CanvasText 16%, transparent);
    opacity: .85;
}
.txt-pdf-find-option.active {
    border-color: rgba(0, 120, 212, .4);
    background: rgba(0, 120, 212, .25);
    opacity: 1;
}
.txt-pdf-find-clear:focus,
.txt-pdf-find-option:focus,
.txt-pdf-find-button:focus {
    outline: none;
    box-shadow: none;
}
#txt-pdf-find-status {
    min-width: 42px;
    color: color-mix(in srgb, CanvasText 64%, transparent);
    text-align: center;
    white-space: nowrap;
}
#txt-pdf-find-close {
    position: relative;
    overflow: hidden;
    color: transparent;
}
#txt-pdf-find-close::before,
#txt-pdf-find-close::after {
    content: "";
    position: absolute;
    left: 50%;
    top: 50%;
    width: 13px;
    height: 1.5px;
    border-radius: 1px;
    background: CanvasText;
    transform-origin: center;
}
#txt-pdf-find-close::before { transform: translate(-50%, -50%) rotate(45deg); }
#txt-pdf-find-close::after { transform: translate(-50%, -50%) rotate(-45deg); }
@media (max-width: 520px) {
    #txt-pdf-find-panel {
        left: 8px;
        right: 8px;
        max-width: none;
    }
    #txt-pdf-find-input-wrap {
        min-width: 0;
        width: 100%;
    }
}`;
        (document.head || document.documentElement).appendChild(style);
    }

    function ensurePanel() {
        ensureStyle();
        panel = document.getElementById('txt-pdf-find-panel');
        if (!panel) {
            panel = document.createElement('div');
            panel.id = 'txt-pdf-find-panel';
            panel.hidden = true;
            panel.innerHTML = `
<div id="txt-pdf-find-input-wrap">
    <input id="txt-pdf-find-input" type="text" autocomplete="off" spellcheck="false">
    <div id="txt-pdf-find-input-actions">
        <button id="txt-pdf-find-clear" class="txt-pdf-find-clear" type="button">×</button>
        <button id="txt-pdf-find-match-case" class="txt-pdf-find-option" type="button">Aa</button>
    </div>
</div>
<span id="txt-pdf-find-status">0/0</span>
<button id="txt-pdf-find-prev" class="txt-pdf-find-button" type="button">↑</button>
<button id="txt-pdf-find-next" class="txt-pdf-find-button" type="button">↓</button>
<button id="txt-pdf-find-close" class="txt-pdf-find-button" type="button">×</button>`;
            document.body.appendChild(panel);

            input = document.getElementById('txt-pdf-find-input');
            clearButton = document.getElementById('txt-pdf-find-clear');
            matchCaseButton = document.getElementById('txt-pdf-find-match-case');
            previousButton = document.getElementById('txt-pdf-find-prev');
            nextButton = document.getElementById('txt-pdf-find-next');
            closeButton = document.getElementById('txt-pdf-find-close');
            status = document.getElementById('txt-pdf-find-status');

            input.placeholder = options.findPlaceholder || '';
            clearButton.title = options.findClearTooltip || '';
            matchCaseButton.title = options.findMatchCaseTooltip || '';
            previousButton.title = options.findPrevTooltip || '';
            nextButton.title = options.findNextTooltip || '';
            closeButton.title = options.findCloseTooltip || '';

            input.addEventListener('keydown', event => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    handleFindEnter(event.shiftKey);
                } else if (event.key === 'Escape') {
                    event.preventDefault();
                    close();
                }
            });
            clearButton.addEventListener('click', () => {
                input.value = '';
                input.focus();
                start();
            });
            matchCaseButton.addEventListener('click', () => {
                matchCase = !matchCase;
                matchCaseButton.classList.toggle('active', matchCase);
                start();
            });
            previousButton.addEventListener('click', () => post('previous'));
            nextButton.addEventListener('click', () => post('next'));
            closeButton.addEventListener('click', close);
        }
    }

    function handleFindEnter(shiftKey) {
        ensurePanel();
        const q = input ? (input.value || '') : '';
        if (!q) return;
        if (q !== lastSearchedQuery || matchCase !== lastSearchedCase) {
            start();
        } else {
            post(shiftKey ? 'previous' : 'next');
        }
        setTimeout(() => { if (input) input.focus(); }, 120);
    }

    function start() {
        ensurePanel();
        lastSearchedQuery = input.value || '';
        lastSearchedCase = matchCase;
        post('start', { query: lastSearchedQuery, matchCase });
    }

    function open() {
        ensurePanel();
        panel.hidden = false;
        input.focus();
        input.select();
        return true;
    }

    function close() {
        ensurePanel();
        panel.hidden = true;
        lastSearchedQuery = null;
        post('close');
    }

    function update(matchCount, activeMatchIndex) {
        ensurePanel();
        const count = Number(matchCount || 0);
        const index = Number(activeMatchIndex || -1);
        status.textContent = count > 0 && index > 0 ? `${index}/${count}` : `0/${count}`;
    }

    document.addEventListener('keydown', event => {
        const ctrl = event.ctrlKey || event.metaKey;
        const key = event.key ? event.key.toLowerCase() : '';
        if (ctrl && key === 'f') {
            event.preventDefault();
            event.stopPropagation();
            open();
            return;
        }
        if (panel && !panel.hidden) {
            if (event.key === 'Enter') {
                event.preventDefault();
                handleFindEnter(event.shiftKey);
            } else if (event.key === 'Escape') {
                event.preventDefault();
                close();
            }
        }
    }, true);

    window.__txtAiEditorPdfFind = { open, close, update };
    ensurePanel();
})();
""";
        }

        private static async Task TryTriggerNativeFindAsync(WebView2 pdfWebView)
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

        private static async Task<bool> TryNavigatePdfViewerPageAsync(WebView2 pdfWebView, int pageNumber)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return false;
            }

            string script = PdfPageNavigationScript.Replace(
                "__PAGE_NUMBER__",
                pageNumber.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

            try
            {
                string result = await pdfWebView.CoreWebView2.ExecuteScriptAsync(script);
                return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> TryNavigatePdfViewerPageWithKeyboardAsync(WebView2 pdfWebView, int pageNumber)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return false;
            }

            try
            {
                await SendKeyChordAsync(pdfWebView, "KeyG", 71, "g", modifiers: 3);
                await Task.Delay(80);
                await InsertTextAsync(pdfWebView, pageNumber.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(30);
                await SendKeyAsync(pdfWebView, "Enter", 13, "Enter");
                return true;
            }
            catch
            {
            }

            try
            {
                await SendKeyChordAsync(pdfWebView, "KeyG", 71, "g", modifiers: 2);
                await Task.Delay(80);
                await InsertTextAsync(pdfWebView, pageNumber.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(30);
                await SendKeyAsync(pdfWebView, "Enter", 13, "Enter");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task SendKeyChordAsync(
            WebView2 pdfWebView,
            string code,
            int virtualKeyCode,
            string key,
            int modifiers)
        {
            await DispatchKeyEventAsync(pdfWebView, "rawKeyDown", code, virtualKeyCode, key, modifiers);
            await DispatchKeyEventAsync(pdfWebView, "keyUp", code, virtualKeyCode, key, modifiers);
        }

        private static async Task SendKeyAsync(
            WebView2 pdfWebView,
            string code,
            int virtualKeyCode,
            string key)
        {
            await DispatchKeyEventAsync(pdfWebView, "rawKeyDown", code, virtualKeyCode, key, 0);
            await DispatchKeyEventAsync(pdfWebView, "keyUp", code, virtualKeyCode, key, 0);
        }

        private static async Task DispatchKeyEventAsync(
            WebView2 pdfWebView,
            string type,
            string code,
            int virtualKeyCode,
            string key,
            int modifiers)
        {
            var payload = new
            {
                type,
                modifiers,
                windowsVirtualKeyCode = virtualKeyCode,
                nativeVirtualKeyCode = virtualKeyCode,
                code,
                key
            };

            await pdfWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.dispatchKeyEvent",
                JsonSerializer.Serialize(payload));
        }

        private static async Task InsertTextAsync(WebView2 pdfWebView, string text)
        {
            await pdfWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.insertText",
                JsonSerializer.Serialize(new { text }));
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

        private const string PdfPageNavigationScript = @"
(() => {
    const targetPage = __PAGE_NUMBER__;
    const pageIndex = Math.max(0, targetPage - 1);

    function dispatchViewportEvent(root) {
        const eventTargets = [];
        if (root) {
            eventTargets.push(root);
            if (root.body) eventTargets.push(root.body);
            if (root.host) eventTargets.push(root.host);
        }
        eventTargets.push(document, document.body);

        let dispatched = false;
        for (const target of eventTargets) {
            if (!target) continue;
            try {
                target.dispatchEvent(new CustomEvent('change-page-and-xy', {
                    bubbles: true,
                    composed: true,
                    detail: { page: pageIndex, x: 0, y: 0 }
                }));
                dispatched = true;
            } catch {}
        }

        return dispatched;
    }

    function callPageMethod(owner) {
        if (!owner) return false;
        const objects = [
            owner,
            owner.viewport,
            owner.viewport_,
            owner.viewer,
            owner.viewer_,
            owner.pdfViewer,
            owner.pdfViewer_
        ];

        for (const obj of objects) {
            if (!obj) continue;
            for (const method of ['goToPage', 'goToPageIndex', 'scrollToPage', 'setPage', 'setPageNumber']) {
                try {
                    if (typeof obj[method] === 'function') {
                        const argument = method === 'setPage' || method === 'setPageNumber'
                            ? targetPage
                            : pageIndex;
                        obj[method](argument);
                        return true;
                    }
                } catch {}
            }

            try {
                if (typeof obj.goToPageAndXY === 'function') {
                    obj.goToPageAndXY(pageIndex, 0, 0);
                    return true;
                }
            } catch {}

            try {
                if (typeof obj.goToPageAndXy === 'function') {
                    obj.goToPageAndXy(pageIndex, 0, 0);
                    return true;
                }
            } catch {}

            try {
                if (typeof obj.currentPageNumber !== 'undefined') {
                    obj.currentPageNumber = targetPage;
                    return true;
                }
            } catch {}

            try {
                if (typeof obj.pageNo !== 'undefined') {
                    obj.pageNo = targetPage;
                    return true;
                }
            } catch {}
        }

        return false;
    }

    function setPageInput(root) {
        if (!root) return false;
        const selectors = [
            'viewer-page-selector input',
            'viewer-page-indicator input',
            '#pageSelector input',
            '#page-selector input',
            'input[type=""number""]',
            'input'
        ];

        for (const selector of selectors) {
            const input = root.querySelector(selector);
            if (!input) continue;
            try {
                input.focus();
                input.value = String(targetPage);
                input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: String(targetPage) }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                input.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, key: 'Enter', code: 'Enter', keyCode: 13, which: 13 }));
                input.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, key: 'Enter', code: 'Enter', keyCode: 13, which: 13 }));
                return true;
            } catch {}
        }

        return false;
    }

    function collectRoots(root, roots = []) {
        if (!root || roots.includes(root)) return roots;
        roots.push(root);
        try {
            root.querySelectorAll('*').forEach(element => {
                if (element.shadowRoot) collectRoots(element.shadowRoot, roots);
            });
        } catch {}
        return roots;
    }

    function findScrollContainer(element) {
        let current = element;
        while (current) {
            try {
                if (current.scrollHeight > current.clientHeight + 8) return current;
            } catch {}

            current = current.parentElement || current.getRootNode().host || null;
        }

        return document.scrollingElement || document.documentElement || document.body;
    }

    function scrollToPageElement(root) {
        if (!root) return false;
        const selectors = [
            `[data-page-number=""${targetPage}""]`,
            `[page-number=""${targetPage}""]`,
            `[data-page-index=""${pageIndex}""]`,
            `[aria-label*=""Page ${targetPage}""]`,
            `#pageContainer${targetPage}`,
            `.page:nth-of-type(${targetPage})`
        ];

        for (const selector of selectors) {
            let page = null;
            try {
                page = root.querySelector(selector);
            } catch {}

            if (!page) continue;
            try {
                const scroller = findScrollContainer(page);
                if (scroller && page.offsetTop >= 0) {
                    scroller.scrollTo({ top: page.offsetTop, behavior: 'auto' });
                }
                page.scrollIntoView({ block: 'start', inline: 'nearest', behavior: 'auto' });
                return true;
            } catch {}
        }

        return false;
    }

    const viewer = document.querySelector('pdf-viewer') ||
        document.querySelector('embed[type=""application/pdf""]') ||
        document.querySelector('embed[type=""application/x-google-chrome-pdf""]');

    dispatchViewportEvent(document);
    if (callPageMethod(viewer)) return true;
    if (viewer && viewer.shadowRoot && (callPageMethod(viewer.shadowRoot.querySelector('#viewer')) || setPageInput(viewer.shadowRoot))) {
        return true;
    }
    if (setPageInput(document)) return true;

    for (const root of collectRoots(document)) {
        dispatchViewportEvent(root);
        if (callPageMethod(root.querySelector && root.querySelector('#viewer'))) return true;
        if (callPageMethod(root.host)) return true;
        if (setPageInput(root)) return true;
        if (scrollToPageElement(root)) return true;
    }

    try {
        window.location.hash = 'page=' + targetPage;
        window.dispatchEvent(new HashChangeEvent('hashchange'));
    } catch {}

    return false;
})();
";
    }
}
