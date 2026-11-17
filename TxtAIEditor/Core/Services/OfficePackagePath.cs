using System;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficePackagePath
    {
        internal static string NormalizeLocalZipPath(string basePath, string? target)
        {
            if (target != null &&
                (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                 target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
            {
                return string.Empty;
            }

            return NormalizeZipPath(basePath, target);
        }

        internal static string NormalizeZipPath(string basePath, string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return string.Empty;
            }

            string combined = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : $"{basePath.TrimEnd('/')}/{target}";
            var parts = new List<string>();
            foreach (string part in combined.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    continue;
                }

                parts.Add(part);
            }

            return string.Join("/", parts);
        }
    }
}
