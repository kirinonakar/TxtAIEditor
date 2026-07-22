using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
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
        private const int FindVirtualKey = 0x46;
        private const int CloseTabVirtualKey = 0x57;
        private const int ControlVirtualKey = 0x11;
        private const int LeftControlVirtualKey = 0xA2;
        private const int RightControlVirtualKey = 0xA3;
        private const int KeyboardHookType = 2;
        private const int KeyTransitionStateMask = unchecked((int)0x80000000);
        private const short KeyDownMask = unchecked((short)0x8000);

        private readonly ISettingsService _settingsService;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string, OpenedTab, int, int> _selectionContextUpdater;
        private readonly Action<string> _shortcutHandler;
        private readonly Func<string, string, string> _getString;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly HookProc _viewerShortcutHookProc;
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();
        private readonly Dictionary<string, PdfFindControl> _findControls = new Dictionary<string, PdfFindControl>();
        private IntPtr _viewerShortcutHook;
        private bool _findShortcutWasDown;
        private bool _closeTabShortcutWasDown;

        public PdfViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider,
            Action<string, OpenedTab, int, int> selectionContextUpdater,
            Action<string> shortcutHandler,
            Func<string, string, string> getString)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
            _selectionContextUpdater = selectionContextUpdater;
            _shortcutHandler = shortcutHandler;
            _getString = getString;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _viewerShortcutHookProc = OnViewerShortcutHook;
            _viewerShortcutHook = SetWindowsHookEx(
                KeyboardHookType,
                _viewerShortcutHookProc,
                IntPtr.Zero,
                GetCurrentThreadId());
        }

        ~PdfViewerController()
        {
            if (_viewerShortcutHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_viewerShortcutHook);
                _viewerShortcutHook = IntPtr.Zero;
            }
        }

        public IReadOnlyDictionary<string, WebView2> ViewerWebViews => _viewerWebViews;

        public void Register(OpenedTab tab, WebView2 pdfWebView, PdfFindControl findControl)
        {
            _viewerWebViews[tab.Id] = pdfWebView;
            _findControls[tab.Id] = findControl;
            _ = InitializeAsync(tab, pdfWebView, findControl);
        }

        public bool IsActiveViewer()
        {
            return _activeTabProvider()?.IsPdfViewer == true;
        }

        public async Task<bool> FocusFindInActiveViewerAsync()
        {
            var activeTab = _activeTabProvider();
            if (activeTab == null || !activeTab.IsPdfViewer)
            {
                return false;
            }

            if (_findControls.TryGetValue(activeTab.Id, out var findControl))
            {
                findControl.ShowAndFocus();
                return true;
            }

            return false;
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
                    var env = await WebViewEnvironmentProvider.GetSharedAsync();
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
            if (_findControls.TryGetValue(tabId, out var findControl))
            {
                _findControls.Remove(tabId);
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

        private async Task InitializeAsync(OpenedTab tab, WebView2 pdfWebView, PdfFindControl findControl)
        {
            try
            {
                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await pdfWebView.EnsureCoreWebView2Async(env);

                if (!_viewerWebViews.TryGetValue(tab.Id, out var registeredWebView) ||
                    !ReferenceEquals(registeredWebView, pdfWebView))
                {
                    return;
                }

                Configure(tab, pdfWebView, findControl);

                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    pdfWebView.Source = new Uri(tab.FilePath, UriKind.Absolute);
                }
            }
            catch
            {
            }
        }

        private void Configure(OpenedTab tab, WebView2 pdfWebView, PdfFindControl findControl)
        {
            if (pdfWebView.CoreWebView2 == null)
            {
                return;
            }

            pdfWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            pdfWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            pdfWebView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            HideNativePdfSearchButton(pdfWebView.CoreWebView2.Settings);
            pdfWebView.CoreWebView2.WebMessageReceived += (_, args) => OnWebMessageReceived(tab, pdfWebView, args);

            WebViewAppearanceService.ApplyPreferredColorScheme(pdfWebView.CoreWebView2, _settingsService.CurrentSettings.Theme);

            _ = InstallSelectionBridgeAsync(pdfWebView);
            findControl.Initialize(pdfWebView, _getString);
            _ = InstallPdfFindBridgeAsync(pdfWebView, BuildPdfFindBridgeScript());
        }

        private static void HideNativePdfSearchButton(CoreWebView2Settings settings)
        {
            try
            {
                settings.HiddenPdfToolbarItems |= CoreWebView2PdfToolbarItems.Search;
            }
            catch
            {
            }
        }

        private IntPtr OnViewerShortcutHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && TryHandleViewerShortcutMessage((int)wParam, lParam))
            {
                return new IntPtr(1);
            }

            return CallNextHookEx(_viewerShortcutHook, code, wParam, lParam);
        }

        private bool TryHandleViewerShortcutMessage(int virtualKey, IntPtr lParam)
        {
            if (virtualKey == CloseTabVirtualKey)
            {
                return TryHandleCloseTabShortcutMessage(lParam);
            }

            return TryHandleFindShortcutMessage(virtualKey, lParam);
        }

        private bool TryHandleCloseTabShortcutMessage(IntPtr lParam)
        {
            bool isKeyUp = (((int)lParam) & KeyTransitionStateMask) != 0;
            if (isKeyUp)
            {
                _closeTabShortcutWasDown = false;
                return false;
            }

            var activeTab = _activeTabProvider();
            if (!IsCtrlDown() ||
                activeTab == null ||
                (!activeTab.IsPdfViewer && !activeTab.IsMediaViewer))
            {
                return false;
            }

            if (_closeTabShortcutWasDown)
            {
                return true;
            }

            _closeTabShortcutWasDown = true;
            _dispatcherQueue.TryEnqueue(() => _shortcutHandler("closeTab"));
            return true;
        }

        private bool TryHandleFindShortcutMessage(int virtualKey, IntPtr lParam)
        {
            if (virtualKey != FindVirtualKey)
            {
                return false;
            }

            bool isKeyUp = (((int)lParam) & KeyTransitionStateMask) != 0;
            if (isKeyUp)
            {
                _findShortcutWasDown = false;
                return false;
            }

            if (!IsCtrlDown() || _activeTabProvider()?.IsPdfViewer != true)
            {
                return false;
            }

            if (_findShortcutWasDown)
            {
                return true;
            }

            var activeTab = _activeTabProvider();
            if (activeTab == null || !_findControls.TryGetValue(activeTab.Id, out var findControl))
            {
                return false;
            }

            _findShortcutWasDown = true;
            findControl.DispatcherQueue.TryEnqueue(() => findControl.ShowAndFocus());
            return true;
        }

        private static bool IsCtrlDown()
        {
            return IsAsyncKeyDown(ControlVirtualKey) ||
                IsAsyncKeyDown(LeftControlVirtualKey) ||
                IsAsyncKeyDown(RightControlVirtualKey);
        }

        private static bool IsAsyncKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            HookProc lpfn,
            IntPtr hmod,
            int dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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
                if (string.Equals(type, "shortcut", StringComparison.Ordinal) &&
                    root.TryGetProperty("name", out var nameProp))
                {
                    string name = nameProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        pdfWebView.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                    }
                    return;
                }

                if (string.Equals(type, "pdfFindTrigger", StringComparison.Ordinal))
                {
                    if (_findControls.TryGetValue(tab.Id, out var findControl))
                    {
                        pdfWebView.DispatcherQueue.TryEnqueue(() => findControl.ShowAndFocus());
                    }
                    return;
                }

                if (string.Equals(type, "pdfFindEscape", StringComparison.Ordinal))
                {
                    if (_findControls.TryGetValue(tab.Id, out var findControl))
                    {
                        pdfWebView.DispatcherQueue.TryEnqueue(() => findControl.HideAndStop());
                    }
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

        private string BuildPdfFindBridgeScript()
        {
            return @"
(() => {
    if (window.__txtAiEditorPdfFindShortcuts) return;
    window.__txtAiEditorPdfFindShortcuts = true;

    function handleKeyDown(event) {
        const ctrl = event.ctrlKey || event.metaKey;
        const key = event.key ? event.key.toLowerCase() : '';
        if (ctrl && key === 'f') {
            event.preventDefault();
            event.stopPropagation();
            event.stopImmediatePropagation?.();
            try {
                chrome.webview.postMessage({ type: 'pdfFindTrigger' });
            } catch {}
            return;
        }
        if (ctrl && key === 'w') {
            event.preventDefault();
            event.stopPropagation();
            event.stopImmediatePropagation?.();
            try {
                chrome.webview.postMessage({ type: 'shortcut', name: 'closeTab' });
            } catch {}
            return;
        }
        if (key === 'escape') {
            try {
                chrome.webview.postMessage({ type: 'pdfFindEscape' });
            } catch {}
        }
    }

    window.addEventListener('keydown', handleKeyDown, true);
    document.addEventListener('keydown', handleKeyDown, true);
})();
";
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
