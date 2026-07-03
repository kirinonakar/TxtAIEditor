using System;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI.Core;

namespace TxtAIEditor.Controls
{
    public sealed partial class PdfFindControl : UserControl
    {
        private WebView2? _webView;
        private Func<string, string, string>? _getString;

        public PdfFindControl()
        {
            InitializeComponent();
        }

        public void Initialize(WebView2 webView, Func<string, string, string> getString)
        {
            _webView = webView;
            _getString = getString;
            Localize();

            if (_webView.CoreWebView2 != null)
            {
                SubscribeToFindEvents();
            }
            else
            {
                _webView.CoreWebView2Initialized += OnCoreWebView2Initialized;
            }
        }

        private void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            _webView!.CoreWebView2Initialized -= OnCoreWebView2Initialized;
            SubscribeToFindEvents();
        }

        private void SubscribeToFindEvents()
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Find.MatchCountChanged += OnMatchCountChanged;
                _webView.CoreWebView2.Find.ActiveMatchIndexChanged += OnActiveMatchIndexChanged;
            }
        }

        public void Localize()
        {
            if (_getString == null) return;
            FindTextBox.PlaceholderText = _getString("EditorFindPlaceholder", "찾기");
            ToolTipService.SetToolTip(MatchCaseButton, _getString("EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)"));
            ToolTipService.SetToolTip(PrevButton, _getString("EditorFindPrevTooltip", "이전"));
            ToolTipService.SetToolTip(NextButton, _getString("EditorFindNextTooltip", "다음"));
            ToolTipService.SetToolTip(CloseButton, _getString("EditorFindCloseTooltip", "닫기"));
        }

        public void ShowAndFocus()
        {
            Visibility = Visibility.Visible;
            FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        }

        public void HideAndStop()
        {
            Visibility = Visibility.Collapsed;
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Find.Stop();
            }
            _webView?.Focus(FocusState.Programmatic);
        }

        private void OnFindTextChanged(object sender, TextChangedEventArgs e)
        {
            StartFind();
        }

        private void OnFindTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                if (_webView?.CoreWebView2 != null)
                {
                    var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                    bool isShiftDown = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                    if (isShiftDown)
                    {
                        _webView.CoreWebView2.Find.FindPrevious();
                    }
                    else
                    {
                        _webView.CoreWebView2.Find.FindNext();
                    }
                }
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideAndStop();
            }
        }

        private void OnMatchCaseClick(object sender, RoutedEventArgs e)
        {
            StartFind();
        }

        private void OnPrevClick(object sender, RoutedEventArgs e)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Find.FindPrevious();
            }
        }

        private void OnNextClick(object sender, RoutedEventArgs e)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Find.FindNext();
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            HideAndStop();
        }

        private void StartFind()
        {
            if (_webView?.CoreWebView2 == null) return;

            string query = FindTextBox.Text;
            var find = _webView.CoreWebView2.Find;

            if (string.IsNullOrEmpty(query))
            {
                find.Stop();
                StatusText.Text = "0/0";
                return;
            }

            bool matchCase = MatchCaseButton.IsChecked == true;

            var options = _webView.CoreWebView2.Environment.CreateFindOptions();
            options.FindTerm = query;
            options.IsCaseSensitive = matchCase;
            options.ShouldHighlightAllMatches = true;
            options.ShouldMatchWord = false;
            options.SuppressDefaultFindDialog = true;

            find.Stop();
            _ = find.StartAsync(options);
        }

        private void OnMatchCountChanged(CoreWebView2Find sender, object args)
        {
            UpdateStatusText();
        }

        private void OnActiveMatchIndexChanged(CoreWebView2Find sender, object args)
        {
            UpdateStatusText();
        }

        private void UpdateStatusText()
        {
            if (_webView?.CoreWebView2 == null) return;

            int matchCount = _webView.CoreWebView2.Find.MatchCount;
            int activeMatchIndex = _webView.CoreWebView2.Find.ActiveMatchIndex;

            if (matchCount > 0 && activeMatchIndex > 0)
            {
                StatusText.Text = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", activeMatchIndex, matchCount);
            }
            else
            {
                StatusText.Text = string.Format(CultureInfo.InvariantCulture, "0/{0}", matchCount);
            }
        }
    }
}
