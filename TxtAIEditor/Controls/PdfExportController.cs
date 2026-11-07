using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    internal static class PdfExportController
    {
        private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(120);

        public static async Task ExportAsync(
            Panel host, string path, object document, bool fullHtml,
            Action<CoreWebView2> registerDocumentResources)
        {
            // A separate, noninteractive document avoids changing the editor DOM,
            // IME composition, selection, scroll position, or dirty state.
            var webView = new WebView2
            {
                Width = 794,
                Height = 1123,
                Opacity = 0,
                IsHitTestVisible = false,
                IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            string temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".txtaieditor-{Guid.NewGuid():N}.pdf");
            host.Children.Add(webView);
            try
            {
                var environment = await WebViewEnvironmentProvider.GetSharedAsync();
                await webView.EnsureCoreWebView2Async(environment);
                var core = webView.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping(
                    PreviewWebResourceService.ResourceHostName,
                    PreviewWebResourceService.WebResourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                registerDocumentResources(core);
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;

                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                string requestId = Guid.NewGuid().ToString("N");
                string previewUrl = $"http://{PreviewWebResourceService.ResourceHostName}/preview.html?pdfExport=1&v={PreviewWebResourceService.GetWebResourceVersion("preview.html")}";
                core.WebMessageReceived += (_, e) =>
                {
                    if (!string.Equals(e.Source, previewUrl, StringComparison.Ordinal)) return;
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    var root = message.RootElement;
                    if (!root.TryGetProperty("type", out var type)) return;
                    if (type.GetString() == "previewReady") ready.TrySetResult(true);
                    if (type.GetString() != "pdfExportPrepared" ||
                        !root.TryGetProperty("requestId", out var id) || id.GetString() != requestId) return;
                    if (root.TryGetProperty("error", out var error))
                        prepared.TrySetException(new InvalidOperationException(error.GetString()));
                    else
                        prepared.TrySetResult(true);
                };
                core.NavigationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess)
                    {
                        var error = new InvalidOperationException($"PDF document navigation failed: {e.WebErrorStatus}");
                        ready.TrySetException(error);
                        prepared.TrySetException(error);
                    }
                };
                core.Navigate(previewUrl);
                await ready.Task.WaitAsync(PreparationTimeout);
                string payload = JsonSerializer.Serialize(document);

                // HTML/SVG must be a top-level document so its own print styles and
                // pagination work; printing a viewport-sized iframe can clip pages.
                using var htmlStream = new MemoryStream();
                if (fullHtml)
                {
                    string result = await core.ExecuteScriptAsync($"window.buildPdfExportHtml({payload})");
                    string html = JsonSerializer.Deserialize<string>(result)
                        ?? throw new InvalidOperationException("PDF HTML document preparation failed.");
                    byte[] bytes = Encoding.UTF8.GetBytes(html);
                    await htmlStream.WriteAsync(bytes);
                    htmlStream.Position = 0;
                    string htmlUrl = $"http://{PreviewWebResourceService.ResourceHostName}/pdf-export-{requestId}.html";
                    core.AddWebResourceRequestedFilter(htmlUrl, CoreWebView2WebResourceContext.Document);
                    core.WebResourceRequested += (_, e) =>
                    {
                        if (e.Request.Uri == htmlUrl)
                        {
                            htmlStream.Position = 0;
                            e.Response = environment.CreateWebResourceResponse(
                                htmlStream.AsRandomAccessStream(), 200, "OK", "Content-Type: text/html; charset=utf-8");
                        }
                    };
                    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess) loaded.TrySetResult(true);
                        else loaded.TrySetException(new InvalidOperationException($"PDF HTML navigation failed: {e.WebErrorStatus}"));
                    };
                    core.Navigate(htmlUrl);
                    await loaded.Task.WaitAsync(PreparationTimeout);
                    await WaitForHtmlAssetsAsync(core);
                }
                else
                {
                    await core.ExecuteScriptAsync($"window.preparePdfExport({payload}, {JsonSerializer.Serialize(requestId)})");
                    await prepared.Task.WaitAsync(PreparationTimeout);
                }

                var printSettings = environment.CreatePrintSettings();
                printSettings.ShouldPrintBackgrounds = true;
                printSettings.ShouldPrintHeaderAndFooter = false;
                printSettings.PageWidth = 210.0 / 25.4;
                printSettings.PageHeight = 297.0 / 25.4;
                printSettings.MarginTop = printSettings.MarginBottom = 0.5;
                printSettings.MarginLeft = printSettings.MarginRight = 0.5;
                if (!await core.PrintToPdfAsync(temporaryPath, printSettings))
                    throw new InvalidOperationException("WebView2 PDF export failed.");

                // Complete generation before replacing an existing destination.
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                webView.Close();
                host.Children.Remove(webView);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static async Task WaitForHtmlAssetsAsync(CoreWebView2 core)
        {
            // ExecuteScriptAsync does not await JavaScript Promises. Poll a small
            // completion state instead of exporting before fonts/images finish.
            await core.ExecuteScriptAsync("""
                window.__pdfAssetsReady = false;
                window.__pdfAssetsError = null;
                (async () => {
                    try {
                        await document.fonts.ready;
                        await Promise.all([...document.images].map(image => {
                            image.loading = 'eager';
                            if (image.complete) return;
                            return new Promise(resolve => {
                                image.addEventListener('load', resolve, { once: true });
                                image.addEventListener('error', resolve, { once: true });
                            });
                        }));
                        await document.fonts.ready;
                        window.__pdfAssetsReady = true;
                    } catch (error) {
                        window.__pdfAssetsError = String(error);
                    }
                })();
                """);
            var deadline = DateTime.UtcNow + PreparationTimeout;
            while (DateTime.UtcNow < deadline)
            {
                string result = await core.ExecuteScriptAsync("JSON.stringify({ready: window.__pdfAssetsReady, error: window.__pdfAssetsError})");
                using var status = JsonDocument.Parse(JsonSerializer.Deserialize<string>(result)!);
                if (status.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException(error.GetString());
                if (status.RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True) return;
                await Task.Delay(100);
            }
            throw new TimeoutException("PDF document assets did not finish loading.");
        }
    }
}
