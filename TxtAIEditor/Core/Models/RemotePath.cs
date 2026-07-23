using System;
using System.Linq;

namespace TxtAIEditor.Core.Models
{
    public static class RemotePath
    {
        private const string Scheme = "txtaieditor-remote";

        public static string Create(
            Guid serverId,
            string path,
            bool isDirectory = false,
            string? serverName = null)
        {
            string normalized = Normalize(path);
            string escapedPath = string.Join(
                "/",
                normalized.Split('/').Select(Uri.EscapeDataString));
            string[] query = new[]
            {
                isDirectory ? "directory=1" : string.Empty,
                string.IsNullOrWhiteSpace(serverName)
                    ? string.Empty
                    : $"server={Uri.EscapeDataString(serverName)}"
            };
            string queryString = string.Join("&", query.Where(value => value.Length > 0));
            return $"{Scheme}://{serverId:N}{escapedPath}" +
                   (queryString.Length > 0 ? $"?{queryString}" : string.Empty);
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

        public static string? GetServerNameHint(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (string part in uri.Query.TrimStart('?').Split('&'))
            {
                string[] pair = part.Split('=', 2);
                if (pair.Length == 2 &&
                    string.Equals(pair[0], "server", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return null;
        }

        public static string GetDisplayPath(string value)
        {
            if (!TryParse(value, out _, out string path))
            {
                return value;
            }

            return $"{GetServerNameHint(value) ?? "Remote"}:{path}";
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
            return Create(
                serverId,
                parent,
                isDirectory: true,
                serverName: GetServerNameHint(value));
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

            return Create(
                serverId,
                $"{Normalize(path).TrimEnd('/')}/{name.Trim('/')}",
                isDirectory,
                GetServerNameHint(parent));
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
