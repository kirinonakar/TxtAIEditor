using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using Windows.System;

namespace TxtAIEditor.Controls
{
    internal sealed class QuickNotePreview(Action failed) : Grid, IDisposable
    {
        private static readonly Uri Page = new("https://txtaieditor-quick-note.local/quick-note-preview.html");
        private WebView2? _webView;
        private string _message = string.Empty;
        private bool _ready;

        public void Update(string markdown, string background, string foreground)
        {
            string message = JsonSerializer.Serialize(new { markdown, background, foreground });
            if (_webView == null)
            {
                _message = message;
                _ = InitializeAsync();
            }
            else if (_message != message)
            {
                _message = message;
                Render();
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                var view = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
                _webView = view;
                Children.Add(view);
                var environment = await WebViewEnvironmentProvider.GetSharedAsync();
                if (_webView != view)
                {
                    return;
                }
                await view.EnsureCoreWebView2Async(environment);
                if (_webView != view)
                {
                    return;
                }

                var core = view.CoreWebView2 ?? throw new InvalidOperationException("Quick note WebView2 failed to initialize.");
                core.SetVirtualHostNameToFolderMapping(Page.Host,
                    PreviewWebResourceService.WebResourcesPath, CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Page.AbsoluteUri;
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (sender, args) =>
                {
                    args.Handled = true;
                    if (args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? link) &&
                        (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp))
                    {
                        _ = AppErrorHandler.RunAsync(async () => { await Launcher.LaunchUriAsync(link); });
                    }
                };
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.WebMessageReceived += (_, args) =>
                {
                    if (_webView == view && args.Source == Page.AbsoluteUri && args.WebMessageAsJson == "\"renderFailed\"")
                    {
                        Fail(new InvalidOperationException("Quick note Markdown rendering failed."));
                    }
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (_webView != view)
                    {
                        return;
                    }
                    if (!args.IsSuccess)
                    {
                        Fail(new InvalidOperationException($"Quick note preview navigation failed: {args.WebErrorStatus}."));
                        return;
                    }
                    _ready = true;
                    Render();
                };
                core.Navigate(Page.AbsoluteUri);
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                Fail(ex);
            }
        }

        private void Render()
        {
            try
            {
                if (_ready)
                {
                    _webView?.CoreWebView2.PostWebMessageAsJson(_message);
                }
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                Fail(ex);
            }
        }

        private void Fail(Exception exception)
        {
            AppErrorHandler.Report(exception);
            Dispose();
            failed();
        }

        public void Dispose()
        {
            if (_webView is WebView2 view)
            {
                _webView = null;
                _ready = false;
                try
                {
                    Children.Remove(view);
                    view.Close();
                }
                catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
                {
                    AppErrorHandler.Report(ex);
                }
            }
        }
    }
}
