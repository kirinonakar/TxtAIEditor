using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorTabViewItemFactory
    {
        private readonly ILocalizationService _localizationService;

        public EditorTabViewItemFactory(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
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

            var bridge = new MonacoBridge(editorWebView, _localizationService);
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
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _ = LoadImageSourceAsync(image, tab.FilePath);

            var imageHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            imageHost.Children.Add(image);

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

        private static async Task LoadImageSourceAsync(Image image, string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                using var fileStream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var randomAccessStream = fileStream.AsRandomAccessStream();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(randomAccessStream);
                image.Source = bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load image viewer source: {ex.Message}");
            }
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

            var pdfHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            pdfHost.Children.Add(pdfWebView);

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

            return new PdfViewerTabParts(tabItem, pdfWebView);
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
    }

    public sealed class EditorTabViewItemParts
    {
        public EditorTabViewItemParts(
            TabViewItem tabItem,
            WebView2 webView,
            Border loadCover,
            MonacoBridge bridge)
        {
            TabItem = tabItem;
            WebView = webView;
            LoadCover = loadCover;
            Bridge = bridge;
        }

        public TabViewItem TabItem { get; }
        public WebView2 WebView { get; }
        public Border LoadCover { get; }
        public MonacoBridge Bridge { get; }
    }

    public sealed class PdfViewerTabParts
    {
        public PdfViewerTabParts(TabViewItem tabItem, WebView2 webView)
        {
            TabItem = tabItem;
            WebView = webView;
        }

        public TabViewItem TabItem { get; }
        public WebView2 WebView { get; }
    }
}
