using System;
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
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu)
        {
            var editorWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor,
                Opacity = 0
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
            tabHeader.Configure(tab, encryptedTooltip);
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
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu)
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri(tab.FilePath!, UriKind.Absolute)),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var imageHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            imageHost.Children.Add(image);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip);
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

        public PdfViewerTabParts CreatePdfViewer(
            OpenedTab tab,
            Windows.UI.Color editorBackgroundColor,
            string? uiFontFamily,
            string encryptedTooltip,
            Action<OpenedTab, FrameworkElement, RightTappedRoutedEventArgs> showEncryptionMenu,
            Action<TabViewItem, RightTappedRoutedEventArgs> showTabContextMenu)
        {
            var pdfWebView = new WebView2
            {
                Source = new Uri(tab.FilePath!, UriKind.Absolute),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = editorBackgroundColor
            };

            var pdfHost = new Grid
            {
                Background = new SolidColorBrush(editorBackgroundColor)
            };
            pdfHost.Children.Add(pdfWebView);

            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, encryptedTooltip);
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
