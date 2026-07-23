using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorTabViewItemFactory
    {
        private const string ImageViewerHostTag = "ImageViewerHost";
        private const string ImageViewerWebViewTag = "ImageViewerWebView";
        private const string ImageViewerHostName = "txtaieditor-image-viewer.local";
        private const string MediaViewerHostTag = "MediaViewerHost";
        private const string MediaViewerWebViewTag = "MediaViewerWebView";
        private const string MediaViewerHostName = "txtaieditor-media-viewer.local";

        private readonly ILocalizationService _localizationService;
        private readonly Action<string>? _viewerShortcutHandler;

        public EditorTabViewItemFactory(
            ILocalizationService localizationService,
            Action<string>? viewerShortcutHandler = null)
        {
            _localizationService = localizationService;
            _viewerShortcutHandler = viewerShortcutHandler;
        }

        public EditorTabViewItemParts Create(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var editorWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                Opacity = 0,
                UseSystemFocusVisuals = false
            };

            var editorLoadCover = new Border
            {
                Background = new SolidColorBrush(editorBackgroundColor),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Tag = "EditorLoadCover"
            };

            var editorHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            editorHost.Children.Add(editorWebView);
            editorHost.Children.Add(editorLoadCover);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = editorHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            var bridge = new CustomEditorBridge(editorWebView, _localizationService);
            return new EditorTabViewItemParts(tabItem, editorWebView, editorLoadCover, bridge);
        }

        public TabViewItem CreateImageViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var imageWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                UseSystemFocusVisuals = false,
                Tag = ImageViewerWebViewTag
            };
            AttachViewerShortcutBridge(imageWebView);
            _ = LoadImageSourceAsync(imageWebView, tab.FilePath, editorBackgroundColor);

            var imageHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor),
                Tag = ImageViewerHostTag
            };
            imageHost.Children.Add(imageWebView);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = imageHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            return tabItem;
        }

        public TabViewItem CreateMediaViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var mediaWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                UseSystemFocusVisuals = false,
                Tag = MediaViewerWebViewTag
            };
            AttachViewerShortcutBridge(mediaWebView);
            _ = LoadMediaSourceAsync(mediaWebView, tab.FilePath, editorBackgroundColor);

            var mediaHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor),
                Tag = MediaViewerHostTag
            };
            mediaHost.Children.Add(mediaWebView);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = mediaHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            return tabItem;
        }

        private static Task LoadImageSourceAsync(WebView2 imageWebView, string? filePath, Windows.UI.Color backgroundColor)
        {
            return LoadViewerSourceAsync(imageWebView, filePath, backgroundColor, ViewerContentKind.Image);
        }

        private static Task LoadMediaSourceAsync(WebView2 mediaWebView, string? filePath, Windows.UI.Color backgroundColor)
        {
            var contentKind = !string.IsNullOrWhiteSpace(filePath) && SupportedFileTypes.IsAudioFile(filePath)
                ? ViewerContentKind.Audio
                : ViewerContentKind.Video;
            return LoadViewerSourceAsync(mediaWebView, filePath, backgroundColor, contentKind);
        }

        private static async Task LoadViewerSourceAsync(
            WebView2 webView,
            string? filePath,
            Windows.UI.Color backgroundColor,
            ViewerContentKind contentKind)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                string? folderPath = Path.GetDirectoryName(filePath);
                string fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(fileName))
                {
                    return;
                }

                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await webView.EnsureCoreWebView2Async(env);

                var coreWebView = webView.CoreWebView2;
                if (coreWebView == null)
                {
                    return;
                }

                ConfigureViewerWebView(coreWebView);
                await InstallViewerShortcutBridgeAsync(webView);

                string hostName = contentKind == ViewerContentKind.Image
                    ? ImageViewerHostName
                    : MediaViewerHostName;
                coreWebView.SetVirtualHostNameToFolderMapping(
                    hostName,
                    folderPath,
                    CoreWebView2HostResourceAccessKind.Allow);

                if (contentKind == ViewerContentKind.Image)
                {
                    coreWebView.AddWebResourceRequestedFilter(
                        $"https://{ImageViewerHostName}/*",
                        CoreWebView2WebResourceContext.All);

                    string capturedFolderPath = folderPath;
                    coreWebView.WebResourceRequested += (sender, args) =>
                        OnImageViewerWebResourceRequested(sender, args, capturedFolderPath);
                }

                string sourceUrl = $"https://{hostName}/{Uri.EscapeDataString(fileName)}";
                string html = contentKind switch
                {
                    ViewerContentKind.Image => BuildImageViewerHtml(sourceUrl, backgroundColor),
                    ViewerContentKind.Audio => BuildMediaViewerHtml(sourceUrl, backgroundColor, isAudio: true),
                    _ => BuildMediaViewerHtml(sourceUrl, backgroundColor, isAudio: false)
                };
                webView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load viewer source: {ex.Message}");
            }
        }

        private static async void OnImageViewerWebResourceRequested(
            CoreWebView2 sender,
            CoreWebView2WebResourceRequestedEventArgs args,
            string folderPath)
        {
            try
            {
                var uriObj = new Uri(args.Request.Uri);
                string relativePath = Uri.UnescapeDataString(uriObj.AbsolutePath.TrimStart('/'));
                string extension = Path.GetExtension(relativePath);
                if (!extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var deferral = args.GetDeferral();
                try
                {
                    string fullPath = Path.Combine(folderPath, relativePath);
                    MemoryStream? pngStream = await ConvertTiffToPngStreamAsync(fullPath);
                    if (pngStream != null)
                    {
                        var response = sender.Environment.CreateWebResourceResponse(
                            pngStream.AsRandomAccessStream(),
                            200,
                            "OK",
                            "Content-Type: image/png");
                        args.Response = response;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to intercept Tiff web resource: {ex.Message}");
            }
        }

        private static async Task<MemoryStream?> ConvertTiffToPngStreamAsync(string tiffFilePath)
        {
            try
            {
                if (!File.Exists(tiffFilePath))
                {
                    return null;
                }

                using var fileStream = new FileStream(tiffFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using IRandomAccessStream input = fileStream.AsRandomAccessStream();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                var output = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    decoder.OrientedPixelWidth,
                    decoder.OrientedPixelHeight,
                    decoder.DpiX,
                    decoder.DpiY,
                    pixelData.DetachPixelData());

                await encoder.FlushAsync();
                output.Seek(0);

                var memoryStream = new MemoryStream();
                using var managedStream = output.AsStreamForRead();
                await managedStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;
                return memoryStream;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to convert Tiff to PNG: {ex.Message}");
                return null;
            }
        }

        public static async Task ReloadImageAsync(TabViewItem tabItem, string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            WebView2? imageWebView = FindTaggedWebView(tabItem.Content as FrameworkElement, ImageViewerWebViewTag);
            if (imageWebView == null)
            {
                return;
            }

            await LoadImageSourceAsync(imageWebView, filePath, GetViewerBackgroundColor(tabItem.Content as FrameworkElement));
        }

        public static async Task ReloadMediaAsync(TabViewItem tabItem, string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            WebView2? mediaWebView = FindTaggedWebView(tabItem.Content as FrameworkElement, MediaViewerWebViewTag);
            if (mediaWebView == null)
            {
                return;
            }

            await LoadMediaSourceAsync(mediaWebView, filePath, GetViewerBackgroundColor(tabItem.Content as FrameworkElement));
        }

        public static void ApplyImageViewerBackground(TabViewItem tabItem, Windows.UI.Color backgroundColor)
        {
            ApplyViewerBackground(tabItem.Content as FrameworkElement, backgroundColor);
        }

        public static void ReleaseViewerResources(TabViewItem tabItem)
        {
            CloseTaggedWebViews(tabItem.Content as FrameworkElement);
        }

        public static void ReleaseViewerResources(FrameworkElement? content)
        {
            CloseTaggedWebViews(content);
        }

        private static void ConfigureViewerWebView(CoreWebView2 coreWebView)
        {
            coreWebView.Settings.IsWebMessageEnabled = true;
            coreWebView.Settings.IsScriptEnabled = true;
            coreWebView.Settings.AreDefaultContextMenusEnabled = true;
            coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            coreWebView.Settings.AreDevToolsEnabled = false;
            coreWebView.Settings.IsStatusBarEnabled = true;
        }

        private void AttachViewerShortcutBridge(WebView2 webView)
        {
            if (_viewerShortcutHandler == null)
            {
                return;
            }

            webView.WebMessageReceived += OnViewerWebMessageReceived;
        }

        private void OnViewerWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var shortcutHandler = _viewerShortcutHandler;
            if (shortcutHandler == null)
            {
                return;
            }

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
                    sender.DispatcherQueue.TryEnqueue(() => shortcutHandler(name));
                }
            }
            catch
            {
            }
        }

        private static async Task InstallViewerShortcutBridgeAsync(WebView2 webView)
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ViewerShortcutBridgeScript);
                await webView.CoreWebView2.ExecuteScriptAsync(ViewerShortcutBridgeScript);
            }
            catch
            {
            }
        }

        private static string BuildImageViewerHtml(string sourceUrl, Windows.UI.Color backgroundColor)
        {
            string src = WebUtility.HtmlEncode(sourceUrl);
            string background = ToCssColor(backgroundColor);
            return $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<style>
:root {{ --viewer-bg: {background}; }}
html, body {{
    margin: 0;
    width: 100%;
    height: 100%;
    background: var(--viewer-bg);
    overflow: hidden;
}}
body {{
    display: flex;
    align-items: center;
    justify-content: center;
}}
img {{
    max-width: 100vw;
    max-height: 100vh;
    width: auto;
    height: auto;
    object-fit: contain;
}}
</style>
</head>
<body>
<img src=""{src}"" draggable=""false"">
</body>
</html>";
        }

        private static string BuildMediaViewerHtml(string sourceUrl, Windows.UI.Color backgroundColor, bool isAudio)
        {
            string src = WebUtility.HtmlEncode(sourceUrl);
            string background = ToCssColor(backgroundColor);
            string mediaElement = isAudio
                ? $@"<audio controls preload=""metadata"" src=""{src}""></audio>"
                : $@"<video controls preload=""metadata"" src=""{src}""></video>";

            return $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<style>
:root {{ --viewer-bg: {background}; }}
html, body {{
    margin: 0;
    width: 100%;
    height: 100%;
    background: var(--viewer-bg);
    overflow: hidden;
}}
body {{
    display: flex;
    align-items: center;
    justify-content: center;
}}
video {{
    width: 100vw;
    height: 100vh;
    object-fit: contain;
    background: #000;
}}
audio {{
    width: min(720px, calc(100vw - 48px));
}}
</style>
</head>
<body>
{mediaElement}
</body>
</html>";
        }

        private static void ApplyViewerBackground(FrameworkElement? root, Windows.UI.Color backgroundColor)
        {
            if (root is Panel panel &&
                IsViewerHostTag(panel.Tag as string))
            {
                panel.Background = new SolidColorBrush(backgroundColor);
            }

            if (root is Panel childPanel)
            {
                foreach (var child in childPanel.Children)
                {
                    if (child is FrameworkElement childElement)
                    {
                        ApplyViewerBackground(childElement, backgroundColor);
                    }
                }
            }

            if (root is WebView2 webView && IsViewerWebViewTag(webView.Tag as string))
            {
                _ = ApplyViewerWebViewBackgroundAsync(webView, backgroundColor);
            }
        }

        private static async Task ApplyViewerWebViewBackgroundAsync(WebView2 webView, Windows.UI.Color backgroundColor)
        {
            webView.DefaultBackgroundColor = backgroundColor;
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                string cssColorJson = JsonSerializer.Serialize(ToCssColor(backgroundColor));
                await webView.CoreWebView2.ExecuteScriptAsync(
                    $"document.documentElement.style.setProperty('--viewer-bg', {cssColorJson});");
            }
            catch
            {
            }
        }

        private static void CloseTaggedWebViews(FrameworkElement? root)
        {
            if (root is WebView2 webView && IsViewerWebViewTag(webView.Tag as string))
            {
                webView.Close();
                return;
            }

            if (root is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is FrameworkElement childElement)
                    {
                        CloseTaggedWebViews(childElement);
                    }
                }
            }
        }

        private static WebView2? FindTaggedWebView(FrameworkElement? root, string tag)
        {
            if (root is WebView2 webView &&
                string.Equals(webView.Tag as string, tag, StringComparison.Ordinal))
            {
                return webView;
            }

            if (root is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is FrameworkElement childElement &&
                        FindTaggedWebView(childElement, tag) is WebView2 found)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        private static Windows.UI.Color GetViewerBackgroundColor(FrameworkElement? root)
        {
            if (root is Panel panel &&
                IsViewerHostTag(panel.Tag as string) &&
                panel.Background is SolidColorBrush brush)
            {
                return brush.Color;
            }

            if (root is Panel parent)
            {
                foreach (var child in parent.Children)
                {
                    if (child is FrameworkElement childElement)
                    {
                        var color = GetViewerBackgroundColor(childElement);
                        if (color.A != 0)
                        {
                            return color;
                        }
                    }
                }
            }

            return Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        private static bool IsViewerHostTag(string? tag)
        {
            return string.Equals(tag, ImageViewerHostTag, StringComparison.Ordinal) ||
                   string.Equals(tag, MediaViewerHostTag, StringComparison.Ordinal);
        }

        private static bool IsViewerWebViewTag(string? tag)
        {
            return string.Equals(tag, ImageViewerWebViewTag, StringComparison.Ordinal) ||
                   string.Equals(tag, MediaViewerWebViewTag, StringComparison.Ordinal);
        }

        private static string ToCssColor(Windows.UI.Color color)
        {
            string alpha = (color.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
            return $"rgba({color.R}, {color.G}, {color.B}, {alpha})";
        }

        private enum ViewerContentKind
        {
            Image,
            Audio,
            Video
        }

        public PdfViewerTabParts CreatePdfViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var pdfWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                UseSystemFocusVisuals = false
            };

            var findControl = new PdfFindControl
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };

            var pdfHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            pdfHost.Children.Add(pdfWebView);
            pdfHost.Children.Add(findControl);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = pdfHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            return new PdfViewerTabParts(tabItem, pdfWebView, findControl);
        }

        public PdfViewerTabParts CreateOfficeDocumentViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var officeWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                UseSystemFocusVisuals = false
            };

            var officeHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            officeHost.Children.Add(officeWebView);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = officeHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            return new PdfViewerTabParts(tabItem, officeWebView);
        }

        private static void ApplyUiFont(TabViewItem tabItem, string? uiFontFamily)
        {
            try
            {
                if (!string.IsNullOrEmpty(uiFontFamily))
                {
                    tabItem.FontFamily = new FontFamily(uiFontFamily);
                }
            }
            catch
            {
            }
        }

        private const string ViewerShortcutBridgeScript = @"
(() => {
    if (window.__txtAiEditorViewerShortcutBridge) return;
    window.__txtAiEditorViewerShortcutBridge = true;

    function post(name) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'shortcut', name });
            }
        } catch {}
    }

    function handleKeyDown(event) {
        let name = '';
        if (event.key === 'F4') {
            name = 'f4';
        } else if (event.key === 'F9') {
            name = 'f9';
        } else if (event.key === 'F10') {
            name = 'f10';
        } else if (event.key === 'F11') {
            name = 'f11';
        } else if (event.key === 'F12') {
            name = 'f12';
        } else {
            const ctrl = event.ctrlKey || event.metaKey;
            const key = event.key ? event.key.toLowerCase() : '';
            if (ctrl && key === '3') {
                name = 'expandRightPanel';
            } else if (ctrl && key === 'f') {
                name = 'find';
            } else if (ctrl && key === 'p') {
                name = 'print';
            } else if (ctrl && key === 'w') {
                name = 'closeTab';
            }
        }

        if (!name) return;
        event.preventDefault();
        event.stopPropagation();
        if (event.stopImmediatePropagation) event.stopImmediatePropagation();
        post(name);
    }

    window.addEventListener('keydown', handleKeyDown, true);
    document.addEventListener('keydown', handleKeyDown, true);
})();
";
    }

    public sealed class EditorTabViewItemParts
    {
        public EditorTabViewItemParts(
            TabViewItem tabItem,
            WebView2 webView,
            Border loadCover,
            CustomEditorBridge bridge)
        {
            TabItem = tabItem;
            WebView = webView;
            LoadCover = loadCover;
            Bridge = bridge;
        }

        public TabViewItem TabItem { get; }
        public WebView2 WebView { get; }
        public Border LoadCover { get; }
        public CustomEditorBridge Bridge { get; }
    }

    public sealed class PdfViewerTabParts
    {
        public PdfViewerTabParts(TabViewItem tabItem, WebView2 webView, PdfFindControl? findControl = null)
        {
            TabItem = tabItem;
            WebView = webView;
            FindControl = findControl;
        }

        public TabViewItem TabItem { get; }
        public WebView2 WebView { get; }
        public PdfFindControl? FindControl { get; }
    }
}
