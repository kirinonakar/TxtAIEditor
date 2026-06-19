using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        private readonly OfficeDocumentViewerService _viewerService = new();
        private readonly Dictionary<string, WebView2> _viewerWebViews = new Dictionary<string, WebView2>();
        private readonly Dictionary<string, string> _viewerHtmlPaths = new Dictionary<string, string>();

        public OfficeDocumentViewerController(
            ISettingsService settingsService,
            Func<OpenedTab?> activeTabProvider)
        {
            _settingsService = settingsService;
            _activeTabProvider = activeTabProvider;
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
            await TryTriggerFindAsync(webView);
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
                var env = await MonacoBridge.GetSharedEnvironmentAsync();
                await webView.EnsureCoreWebView2Async(env);

                if (!_viewerWebViews.TryGetValue(tab.Id, out var registeredWebView) ||
                    !ReferenceEquals(registeredWebView, webView))
                {
                    return;
                }

                Configure(webView);
                await NavigateAsync(tab, webView);
            }
            catch
            {
            }
        }

        private void Configure(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            WebViewAppearanceService.ApplyPreferredColorScheme(webView.CoreWebView2, _settingsService.CurrentSettings.Theme);
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

        private static async Task TryTriggerFindAsync(WebView2 webView)
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
    }
}
