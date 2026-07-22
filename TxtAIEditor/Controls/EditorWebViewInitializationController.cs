using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorWebViewInitializationController
    {
        private readonly ISettingsService _settingsService;
        private readonly LivePreviewController _livePreviewController;

        public EditorWebViewInitializationController(
            ISettingsService settingsService,
            LivePreviewController livePreviewController)
        {
            _settingsService = settingsService;
            _livePreviewController = livePreviewController;
        }

        public async Task InitializeAsync(WebView2 webView, CustomEditorBridge bridge)
        {
            try
            {
                WebViewAppearanceService.ApplyEditorHostBackground(
                    webView,
                    WebViewAppearanceService.ResolveEditorBackgroundColor(_settingsService.CurrentSettings));
                await bridge.InitializeAsync();

                var coreWebView = webView.CoreWebView2;
                if (coreWebView == null)
                {
                    throw new InvalidOperationException("CoreWebView2 failed to initialize.");
                }

                WebViewAppearanceService.ApplyPreferredColorScheme(coreWebView, _settingsService.CurrentSettings.Theme);

                coreWebView.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.ResourceHostName,
                    PreviewWebResourceService.WebResourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                _livePreviewController.RegisterDocumentResourceAccess(coreWebView);

                bridge.LoadEditor(BuildEditorUrl());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed initialization of editor: {ex.Message}");
            }
        }

        private string BuildEditorUrl()
        {
            var settings = _settingsService.CurrentSettings;
            string url = $"http://{PreviewWebResourceService.ResourceHostName}/editor.html?v={PreviewWebResourceService.GetEditorResourceVersion()}" +
                $"&theme={Uri.EscapeDataString(settings.Theme)}" +
                $"&fontSize={settings.FontSize}" +
                $"&fontFamily={Uri.EscapeDataString(settings.FontFamily)}" +
                $"&wordWrap={(settings.WordWrap ? "pre-wrap" : "pre")}";

            if (!string.IsNullOrEmpty(settings.CustomBackgroundColor))
            {
                url += $"&customBg={Uri.EscapeDataString(settings.CustomBackgroundColor)}";
            }

            if (!string.IsNullOrEmpty(settings.CustomForegroundColor))
            {
                url += $"&customFg={Uri.EscapeDataString(settings.CustomForegroundColor)}";
            }

            return url;
        }
    }
}
