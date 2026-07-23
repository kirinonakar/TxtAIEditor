using System;
using System.Linq;

namespace TxtAIEditor.Core.Models
{
    public static class RemotePath
    {
        private const string Scheme = "txtaieditor-remote";

        public static string Create(Guid serverId, string path, bool isDirectory = false)
        {
            string normalized = Normalize(path);
            string escapedPath = string.Join(
                "/",
                normalized.Split('/').Select(Uri.EscapeDataString));
            string directoryHint = isDirectory ? "?directory=1" : string.Empty;
            return $"{Scheme}://{serverId:N}{escapedPath}{directoryHint}";
        }

        public static bool TryParse(string? value, out Guid serverId, out string path)
        {
            serverId = Guid.Empty;
            path = "/";
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(uri.Host, out serverId))
            {
                return false;
            }

            path = Normalize(Uri.UnescapeDataString(uri.AbsolutePath));
            return true;
        }

        public static bool IsRemote(string? value) => TryParse(value, out _, out _);

        public static bool IsDirectory(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                   string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) &&
                   uri.Query.Contains("directory=1", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetParent(string value)
        {
            if (!TryParse(value, out Guid serverId, out string path))
            {
                return string.Empty;
            }

            string normalized = Normalize(path);
            int separator = normalized.LastIndexOf('/');
            string parent = separator <= 0 ? "/" : normalized[..separator];
            return Create(serverId, parent, isDirectory: true);
        }

        public static string GetName(string value)
        {
            if (!TryParse(value, out _, out string path))
            {
                return string.Empty;
            }

            string name = path.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? "/" : name;
        }

        public static string Combine(string parent, string name, bool isDirectory = false)
        {
            if (!TryParse(parent, out Guid serverId, out string path))
            {
                throw new ArgumentException("The parent is not a remote path.", nameof(parent));
            }

            return Create(serverId, $"{Normalize(path).TrimEnd('/')}/{name.Trim('/')}", isDirectory);
        }

        private static string Normalize(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? "/"
                : "/" + normalized.Trim('/');
        }
    }
}
