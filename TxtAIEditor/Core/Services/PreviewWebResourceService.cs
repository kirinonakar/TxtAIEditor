using System;
using System.IO;
using System.Linq;

namespace TxtAIEditor.Core.Services
{
    public static class PreviewWebResourceService
    {
        private static readonly string[] EditorResources =
        {
            "editor.html",
            "editor.css",
            "editor-core.js",
            "editor-ime-state.js",
            "editor-highlighter.js",
            "editor-selection.js",
            "editor-caret.js",
            "editor-composition.js",
            "editor-commands.js",
            "editor-ui.js",
            "markdown-preview-renderer.js",
            "hangul-autocomplete.js"
        };

        public const string ResourceHostName = "txtaieditor.local";
        public const string DocumentHostName = "txtaieditor-doc.local";

        public static string WebResourcesPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebResources");

        public static string GetWebResourceVersion(string fileName)
        {
            try
            {
                string path = Path.Combine(WebResourcesPath, fileName);
                return File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path).Ticks.ToString()
                    : DateTime.UtcNow.Ticks.ToString();
            }
            catch
            {
                return DateTime.UtcNow.Ticks.ToString();
            }
        }

        public static string GetEditorResourceVersion()
        {
            try
            {
                long latestTicks = EditorResources
                    .Select(fileName => Path.Combine(WebResourcesPath, fileName))
                    .Where(File.Exists)
                    .Select(path => File.GetLastWriteTimeUtc(path).Ticks)
                    .DefaultIfEmpty(DateTime.UtcNow.Ticks)
                    .Max();

                return latestTicks.ToString();
            }
            catch
            {
                return DateTime.UtcNow.Ticks.ToString();
            }
        }

        public static Windows.Storage.Streams.IRandomAccessStream CreateEmptyResourceStream()
        {
            return new MemoryStream(Array.Empty<byte>()).AsRandomAccessStream();
        }

        public static string GetContentType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                ".avif" => "image/avif",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".html" or ".htm" => "text/html; charset=utf-8",
                _ => "application/octet-stream"
            };
        }
    }
}
