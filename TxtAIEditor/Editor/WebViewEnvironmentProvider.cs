using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace TxtAIEditor.Editor
{
    internal static class WebViewEnvironmentProvider
    {
        private static readonly object EnvironmentLock = new object();
        private static CoreWebView2Environment? _sharedEnvironment;

        public static async Task<CoreWebView2Environment> GetSharedAsync()
        {
            if (_sharedEnvironment != null)
            {
                return _sharedEnvironment;
            }

            CoreWebView2Environment environment;
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheFolder = Path.Combine(localAppData, "TxtAIEditor", "WebView2Cache");
                Directory.CreateDirectory(cacheFolder);
                environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, cacheFolder, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create shared WebView2 environment: {ex.Message}");
                string fallbackCacheFolder = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "WebView2Cache");
                Directory.CreateDirectory(fallbackCacheFolder);
                environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackCacheFolder, null);
            }

            lock (EnvironmentLock)
            {
                _sharedEnvironment ??= environment;
                return _sharedEnvironment;
            }
        }
    }
}
