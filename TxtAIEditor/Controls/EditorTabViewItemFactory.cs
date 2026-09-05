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
            void LoadMediaViewerWhenReady(object sender, RoutedEventArgs args)
            {
                mediaWebView.Loaded -= LoadMediaViewerWhenReady;
                _ = LoadMediaSourceAsync(mediaWebView, tab.FilePath, editorBackgroundColor);
            }

            mediaWebView.Loaded += LoadMediaViewerWhenReady;

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

                var metadataTask = Task.Run(() => MediaMetadataReader.ReadAsync(filePath));

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

                string sourceUrl;
                string extension = Path.GetExtension(fileName);
                if (contentKind == ViewerContentKind.Image &&
                    (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)))
                {
                    using var pngStream = await ConvertTiffToPngStreamAsync(filePath);
                    if (pngStream != null)
                    {
                        string base64 = Convert.ToBase64String(pngStream.ToArray());
                        sourceUrl = $"data:image/png;base64,{base64}";
                    }
                    else
                    {
                        sourceUrl = $"https://{hostName}/{Uri.EscapeDataString(fileName)}";
                    }
                }
                else
                {
                    sourceUrl = $"https://{hostName}/{Uri.EscapeDataString(fileName)}";
                }

                string html;
                if (contentKind == ViewerContentKind.Audio)
                {
                    var metadata = await metadataTask;
                    string? albumArtDataUri = metadata.AlbumArtDataUri;
                    string? trackTitle = metadata.Tags.TryGetValue("Title", out var titleVal) ? titleVal : Path.GetFileNameWithoutExtension(fileName);
                    string? trackArtist = metadata.Tags.TryGetValue("Artist", out var artistVal) ? artistVal : null;

                    html = BuildAudioViewerHtml(sourceUrl, backgroundColor, albumArtDataUri, trackTitle, trackArtist);
                }
                else if (contentKind == ViewerContentKind.Image)
                {
                    html = BuildImageViewerHtml(sourceUrl, backgroundColor);
                }
                else
                {
                    html = BuildMediaViewerHtml(sourceUrl, backgroundColor, isAudio: false);
                }
                await NavigateHtmlSafelyAsync(webView, html);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load viewer source: {ex.Message}");
            }
        }

        private static async Task NavigateHtmlSafelyAsync(WebView2 webView, string html)
        {
            var coreWebView = webView.CoreWebView2;
            if (coreWebView == null)
            {
                webView.NavigateToString(html);
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? handler = null;

            handler = (s, e) =>
            {
                if (e.IsSuccess)
                {
                    tcs.TrySetResult(true);
                }
                else
                {
                    tcs.TrySetResult(false);
                }
            };

            coreWebView.NavigationCompleted += handler;
            try
            {
                coreWebView.NavigateToString(html);
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(1500));
                bool success = completedTask == tcs.Task && await tcs.Task;

                if (!success)
                {
                    await Task.Delay(100);
                    coreWebView.NavigateToString(html);
                }
            }
            catch
            {
                webView.NavigateToString(html);
            }
            finally
            {
                coreWebView.NavigationCompleted -= handler;
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

                using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                bool isBgra8 = softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                               softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied;
                using SoftwareBitmap convertedBitmap = isBgra8
                    ? softwareBitmap
                    : SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                var output = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(convertedBitmap);
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
            if (isAudio)
            {
                return BuildAudioViewerHtml(sourceUrl, backgroundColor, null, null, null);
            }

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
video {{
    width: 100vw;
    height: 100vh;
    object-fit: contain;
    background: #000;
}}
</style>
</head>
<body>
<video controls preload=""metadata"" src=""{src}""></video>
</body>
</html>";
        }

        private static string BuildAudioViewerHtml(
            string sourceUrl,
            Windows.UI.Color backgroundColor,
            string? albumArtDataUri,
            string? trackTitle,
            string? trackArtist)
        {
            string src = WebUtility.HtmlEncode(sourceUrl);
            string background = ToCssColor(backgroundColor);

            bool hasArt = !string.IsNullOrWhiteSpace(albumArtDataUri);
            string imgTag = hasArt
                ? $@"<img class=""album-art-img"" src=""{albumArtDataUri}"" alt=""Album Art"" draggable=""false"" style=""display:none;"" onload=""this.style.display='block'; var p=document.getElementById('placeholder'); if(p) p.style.display='none';"" onerror=""this.style.display='none'; var p=document.getElementById('placeholder'); if(p) p.style.display='flex';"" />"
                : string.Empty;

            string albumArtContent = $@"{imgTag}
<div id=""placeholder"" class=""album-art-placeholder"">
    <svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.5"" stroke-linecap=""round"" stroke-linejoin=""round"">
        <circle cx=""12"" cy=""12"" r=""9""></circle>
        <circle cx=""12"" cy=""12"" r=""3""></circle>
        <path d=""M12 15v-6l3 2""></path>
    </svg>
</div>";

            string titleHtml = !string.IsNullOrWhiteSpace(trackTitle)
                ? $@"<div class=""track-title"">{WebUtility.HtmlEncode(trackTitle)}</div>"
                : string.Empty;

            string artistHtml = !string.IsNullOrWhiteSpace(trackArtist)
                ? $@"<div class=""track-artist"">{WebUtility.HtmlEncode(trackArtist)}</div>"
                : string.Empty;

            string infoBlockHtml = (!string.IsNullOrWhiteSpace(titleHtml) || !string.IsNullOrWhiteSpace(artistHtml))
                ? $@"<div class=""track-info"">
    {titleHtml}
    {artistHtml}
</div>"
                : string.Empty;

            return $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<style>
:root {{ --viewer-bg: {background}; }}
html, body {{
    margin: 0;
    padding: 0;
    width: 100%;
    height: 100%;
    background: var(--viewer-bg);
    overflow: auto;
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    color-scheme: light dark;
}}
body {{
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    padding: 32px 24px;
    box-sizing: border-box;
}}
.player-container {{
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    width: 100%;
    max-width: 520px;
    gap: 20px;
}}
.album-art-card {{
    width: min(300px, 55vw);
    height: min(300px, 55vw);
    aspect-ratio: 1 / 1;
    border-radius: 20px;
    box-shadow: 0 16px 36px rgba(0, 0, 0, 0.3), 0 4px 12px rgba(0, 0, 0, 0.15);
    overflow: hidden;
    background: rgba(128, 128, 128, 0.12);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border: 1px solid rgba(255, 255, 255, 0.12);
    display: flex;
    align-items: center;
    justify-content: center;
    transition: transform 0.3s cubic-bezier(0.2, 0, 0, 1), box-shadow 0.3s ease;
    user-select: none;
    -webkit-user-select: none;
    flex-shrink: 0;
}}
.album-art-card:hover {{
    transform: translateY(-4px) scale(1.02);
    box-shadow: 0 22px 44px rgba(0, 0, 0, 0.4), 0 6px 16px rgba(0, 0, 0, 0.2);
}}
.album-art-img {{
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
}}
.album-art-placeholder {{
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: linear-gradient(135deg, rgba(99, 102, 241, 0.25) 0%, rgba(168, 85, 247, 0.25) 50%, rgba(236, 72, 153, 0.25) 100%);
    color: rgba(255, 255, 255, 0.85);
}}
.album-art-placeholder svg {{
    width: 88px;
    height: 88px;
    filter: drop-shadow(0 4px 12px rgba(0, 0, 0, 0.3));
}}
.track-info {{
    text-align: center;
    width: 100%;
    padding: 0 8px;
}}
.track-title {{
    font-size: 1.15rem;
    font-weight: 650;
    line-height: 1.35;
    opacity: 0.95;
    word-break: break-word;
}}
.track-artist {{
    font-size: 0.92rem;
    font-weight: 400;
    margin-top: 4px;
    opacity: 0.7;
    word-break: break-word;
}}
audio {{
    width: 100%;
    max-width: 480px;
    height: 48px;
    border-radius: 24px;
    outline: none;
}}
</style>
</head>
<body>
<div class=""player-container"">
    <div class=""album-art-card"">
        {albumArtContent}
    </div>
    {infoBlockHtml}
    <audio controls preload=""metadata"" src=""{src}""></audio>
</div>
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

        public PdfViewerTabParts CreateNotebookViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu,
            string? workspaceFolderPath = null)
        {
            var notebookWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                UseSystemFocusVisuals = false
            };

            var notebookHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            notebookHost.Children.Add(notebookWebView);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip, workspaceFolderPath);
            tabHeader.EncryptionMenuRequested += (_, args) =>
                showEncryptionMenu(args.Tab, args.Target, args.RoutedArgs);

            var tabItem = new TabViewItem
            {
                Content = notebookHost,
                Tag = tab.Id,
                Header = tabHeader,
                ContentTransitions = new TransitionCollection(),
                Transitions = new TransitionCollection(),
                Opacity = 1
            };
            tabItem.RightTapped += (_, args) => showTabContextMenu(tabItem, args);
            ApplyUiFont(tabItem, uiFontFamily);

            return new PdfViewerTabParts(tabItem, notebookWebView);
        }

        private static void ApplyUiFont(TabViewItem tabItem, string? uiFontFamily)
        {
            // The native close button occupies a separate template column. Stretch
            // the custom header into the remaining column so its title can trim
            // instead of drawing over that close-button slot after tabs resize.
            tabItem.HorizontalContentAlignment = HorizontalAlignment.Stretch;

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
        } else if (alt && !ctrl && !shift && (key === 'arrowleft' || code === 'ArrowLeft')) {
            name = 'previousTab';
        } else if (alt && !ctrl && !shift && (key === 'arrowright' || code === 'ArrowRight')) {
            name = 'nextTab';
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
