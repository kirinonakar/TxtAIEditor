using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal static class PdfExportFontEmbedding
    {
        public static async Task EmbedAsync(CoreWebView2 core, bool fullHtml)
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"media\":\"print\"}");
            string moduleUrl = $"http://{PreviewWebResourceService.ResourceHostName}/pdf-font-subset.js?v={PreviewWebResourceService.GetWebResourceVersion("pdf-font-subset.js")}";
            string root = fullHtml ? "document.body" : "document.getElementById('print-container')";
            JsonElement requests = await EvaluateAsync(core, $"(async () => {{ await document.fonts.ready; window.__pdfFontSubset = await import({JsonSerializer.Serialize(moduleUrl)}); return window.__pdfFontSubset.collectFontRequests({root}); }})()");
            var fallbackFamilies = await PdfExportFontSource.GetUsedFontFamiliesAsync((method, parameters) =>
                core.CallDevToolsProtocolMethodAsync(method, parameters).AsTask());
            // Virtual-folder mapped hosts bypass WebResourceRequested. Use a
            // separate, request-scoped host for in-memory original font programs.
            string prefix = $"http://pdf-fonts.{PreviewWebResourceService.ResourceHostName}/{Guid.NewGuid():N}/";
            var programs = new Dictionary<string, byte[]>();
            var cache = new Dictionary<string, string?>();
            var sources = new List<object>();
            var streams = new List<Windows.Storage.Streams.IRandomAccessStream>();
            void OnRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                if (!programs.TryGetValue(e.Request.Uri, out var bytes)) return;
                var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
                streams.Add(stream);
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: font/ttf\r\nAccess-Control-Allow-Origin: *");
            }

            core.AddWebResourceRequestedFilter(prefix + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequested;
            try
            {
                foreach (var request in requests.EnumerateArray())
                {
                    int index = 0;
                    var families = new List<string>();
                    foreach (var candidate in request.GetProperty("families").EnumerateArray()) families.Add(candidate.GetString()!);
                    if (fallbackFamilies.TryGetValue(request.GetProperty("id").GetInt32(), out var used))
                        foreach (string family in used)
                            if (!families.Contains(family, StringComparer.OrdinalIgnoreCase)) families.Add(family);
                    foreach (string family in families)
                    {
                        int familyIndex = index++;
                        if (IsGenericFamily(family)) continue;
                        int weight = request.GetProperty("weight").GetInt32();
                        string style = request.GetProperty("style").GetString()!;
                        bool italic = style != "normal";
                        string key = $"{family}|{weight}|{italic}";
                        if (!cache.TryGetValue(key, out string? url))
                        {
                            byte[]? bytes = await Task.Run(() => PdfExportFontSource.Read(family, weight, italic));
                            if (bytes != null)
                            {
                                url = prefix + programs.Count + ".ttf";
                                programs.Add(url, bytes);
                            }
                            cache.Add(key, url);
                        }
                        if (url == null) continue; // e.g. a document's own webfont
                        sources.Add(new
                        {
                            id = request.GetProperty("id").GetInt32(), familyIndex, family, url, weight, style,
                            stretch = request.GetProperty("stretch").GetString(),
                            variations = request.GetProperty("variations").GetString(),
                            size = request.GetProperty("size").GetDouble(),
                            opticalSizing = request.GetProperty("opticalSizing").GetString(),
                            text = request.GetProperty("text").GetString()
                        });
                    }
                }
                await EvaluateAsync(core, $"window.__pdfFontSubset.embedPdfFonts({JsonSerializer.Serialize(sources)})");
            }
            finally
            {
                core.WebResourceRequested -= OnRequested;
                core.RemoveWebResourceRequestedFilter(prefix + "*", CoreWebView2WebResourceContext.All);
                foreach (var stream in streams) stream.Dispose();
            }
        }

        private static bool IsGenericFamily(string family) => family.ToLowerInvariant() is
            "serif" or "sans-serif" or "monospace" or "system-ui" or "cursive" or "fantasy" or
            "emoji" or "math" or "fangsong" or "ui-serif" or "ui-sans-serif" or "ui-monospace" or "ui-rounded";

        private static async Task<JsonElement> EvaluateAsync(CoreWebView2 core, string expression)
        {
            string json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression, awaitPromise = true, returnByValue = true
            })).AsTask().WaitAsync(TimeSpan.FromSeconds(120));
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
            {
                string description = error.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var text)
                    ? text.GetString() ?? "Font subset embedding failed."
                    : "Font subset embedding failed.";
                throw new InvalidOperationException(description);
            }
            return result.RootElement.GetProperty("result").GetProperty("value").Clone();
        }
    }
}
