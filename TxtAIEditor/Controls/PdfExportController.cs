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
            Action<CoreWebView2> registerDocumentResources, bool officeDocument = false)
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
                    string htmlUrl = $"http://pdf-document.{PreviewWebResourceService.ResourceHostName}/pdf-export-{requestId}.html";
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

                JsonElement? officeLayout = null;
                if (officeDocument)
                {
                    officeLayout = await PrepareOfficeDocumentAsync(core);
                    // Workbook export creates the other sheets and their images.
                    await WaitForHtmlAssetsAsync(core);
                }
                await PdfExportFontEmbedding.EmbedAsync(core, fullHtml);

                var printSettings = environment.CreatePrintSettings();
                printSettings.ShouldPrintBackgrounds = true;
                printSettings.ShouldPrintHeaderAndFooter = false;
                printSettings.PageWidth = 210.0 / 25.4;
                printSettings.PageHeight = 297.0 / 25.4;
                printSettings.MarginTop = printSettings.MarginBottom = 0.5;
                printSettings.MarginLeft = printSettings.MarginRight = 0.5;
                if (officeLayout is JsonElement layout)
                {
                    printSettings.PageWidth = layout.GetProperty("width").GetDouble();
                    printSettings.PageHeight = layout.GetProperty("height").GetDouble();
                    printSettings.MarginTop = layout.GetProperty("top").GetDouble();
                    printSettings.MarginRight = layout.GetProperty("right").GetDouble();
                    printSettings.MarginBottom = layout.GetProperty("bottom").GetDouble();
                    printSettings.MarginLeft = layout.GetProperty("left").GetDouble();
                }
                if (officeLayout is JsonElement officePrintLayout &&
                    officePrintLayout.TryGetProperty("preferCssPageSize", out var preferCssPageSize) &&
                    preferCssPageSize.ValueKind == JsonValueKind.True)
                {
                    await PrintWithCssPageSizesAsync(core, temporaryPath, printSettings);
                }
                else if (!await core.PrintToPdfAsync(temporaryPath, printSettings))
                {
                    throw new InvalidOperationException("WebView2 PDF export failed.");
                }

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

        private static async Task PrintWithCssPageSizesAsync(
            CoreWebView2 core, string path, CoreWebView2PrintSettings settings)
        {
            // WebView2's print settings describe one paper size. Request CSS
            // page sizes explicitly so mixed-size HWPX sections retain theirs.
            string printResult = await core.CallDevToolsProtocolMethodAsync("Page.printToPDF", JsonSerializer.Serialize(new
            {
                preferCSSPageSize = true,
                transferMode = "ReturnAsStream",
                printBackground = settings.ShouldPrintBackgrounds,
                displayHeaderFooter = settings.ShouldPrintHeaderAndFooter,
                paperWidth = settings.PageWidth,
                paperHeight = settings.PageHeight,
                marginTop = settings.MarginTop,
                marginRight = settings.MarginRight,
                marginBottom = settings.MarginBottom,
                marginLeft = settings.MarginLeft
            }));
            using var result = JsonDocument.Parse(printResult);
            string handle = result.RootElement.GetProperty("stream").GetString()
                ?? throw new InvalidOperationException("WebView2 did not return a PDF stream.");
            try
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                bool eof;
                do
                {
                    string chunk = await core.CallDevToolsProtocolMethodAsync(
                        "IO.read", JsonSerializer.Serialize(new { handle, size = 512 * 1024 }));
                    using var response = JsonDocument.Parse(chunk);
                    JsonElement part = response.RootElement;
                    string? data = part.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(data))
                    {
                        if (!part.TryGetProperty("base64Encoded", out var encoded) || encoded.ValueKind != JsonValueKind.True)
                        {
                            throw new InvalidDataException("WebView2 returned an invalid PDF stream.");
                        }
                        await output.WriteAsync(Convert.FromBase64String(data));
                    }
                    eof = part.TryGetProperty("eof", out var end) && end.ValueKind == JsonValueKind.True;
                } while (!eof);
            }
            finally
            {
                await core.CallDevToolsProtocolMethodAsync("IO.close", JsonSerializer.Serialize(new { handle }));
            }
        }

        private static async Task<JsonElement> PrepareOfficeDocumentAsync(CoreWebView2 core)
        {
            string url = $"http://{PreviewWebResourceService.ResourceHostName}/office-pdf-export.js?v={PreviewWebResourceService.GetWebResourceVersion("office-pdf-export.js")}";
            string json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = $"(async () => {{ const module = await import({JsonSerializer.Serialize(url)}); return module.prepareOfficePdf(); }})()",
                awaitPromise = true, returnByValue = true
            }));
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
            {
                string message = error.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                    ? description.GetString() ?? "Office PDF preparation failed." : "Office PDF preparation failed.";
                throw new InvalidOperationException(message);
            }
            return result.RootElement.GetProperty("result").GetProperty("value").Clone();
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
