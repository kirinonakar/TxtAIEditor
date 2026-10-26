using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TxtAIEditor.Core.Services
{
    internal sealed record PreviewDocumentResourceLocation(string RootDirectory, string HostName, string BaseHref)
    {
        internal const string HostNameSuffix = "txtaieditor-doc.local";

        public static PreviewDocumentResourceLocation FromDirectory(string directory)
        {
            string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string root = Path.GetPathRoot(fullDirectory)
                ?? throw new ArgumentException("The document directory must have a filesystem root.", nameof(directory));
            string rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16]
                .ToLowerInvariant();
            string host = $"{rootKey}.{HostNameSuffix}";
            string relativeDirectory = Path.GetRelativePath(root, fullDirectory);
            string urlPath = relativeDirectory == "." ? string.Empty : string.Join("/",
                relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Select(Uri.EscapeDataString)) + "/";
            return new PreviewDocumentResourceLocation(root, host, $"http://{host}/{urlPath}");
        }
    }

    internal sealed class PreviewDocumentResourceRegistry
    {
        private readonly Dictionary<string, string> _rootsByHost = new(StringComparer.OrdinalIgnoreCase);

        public void Register(PreviewDocumentResourceLocation location)
        {
            _rootsByHost[location.HostName] = location.RootDirectory;
        }

        public bool TryResolveFile(string requestUrl, out string targetPath)
        {
            targetPath = string.Empty;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !_rootsByHost.TryGetValue(uri.Host, out string? root)) return false;

            string relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath)) return false;

            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return false;
            targetPath = fullPath;
            return true;
        }
    }
}
