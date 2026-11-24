using System;
using System.Diagnostics;
using System.IO;

namespace TxtAIEditor.Core.Services
{
    internal static class AppTemporaryFiles
    {
        private const string RootName = "TxtAIEditor";
        private const string LegacyOfficeProbe = "TxtAIEditor-LegacyOfficeProbe";
        internal const string SearchDirectoryPrefix = "TxtAIEditor-Search-";

        internal static string Root => Path.Combine(Path.GetTempPath(), RootName);

        internal static void CleanUp(string tempPath, bool preserveIpc)
        {
            try
            {
                var tempRoot = new DirectoryInfo(tempPath);
                if (!tempRoot.Exists || IsLink(tempRoot)) return;

                foreach (FileSystemInfo entry in tempRoot.EnumerateFileSystemInfos("TxtAIEditor*"))
                {
                    if (entry is DirectoryInfo directory)
                    {
                        bool isAppRoot = string.Equals(directory.Name, RootName, StringComparison.OrdinalIgnoreCase);
                        if (isAppRoot || IsCacheDirectory(directory.Name))
                        {
                            DeleteDirectory(directory, preserveIpc && isAppRoot);
                        }
                    }
                    else if (IsGeneratedName(entry.Name, "TxtAIEditor-agent-", ".tmp") ||
                        IsGeneratedName(entry.Name, "TxtAIEditor-settings-export-", ".zip"))
                    {
                        DeleteFile(entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not enumerate TxtAIEditor temporary files: {ex.Message}");
            }
        }

        private static bool IsCacheDirectory(string name) =>
            name.StartsWith(SearchDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TxtAIEditor-Print-", StringComparison.OrdinalIgnoreCase) ||
            IsGeneratedName(name, "TxtAIEditor-settings-import-", string.Empty) ||
            name.StartsWith(LegacyOfficeProbe, StringComparison.OrdinalIgnoreCase);

        private static bool IsGeneratedName(string name, string prefix, string suffix) =>
            name.Length == prefix.Length + 32 + suffix.Length &&
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _);

        private static bool IsLink(FileSystemInfo entry) =>
            (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0;

        private static void DeleteDirectory(DirectoryInfo directory, bool preserveIpc = false)
        {
            try
            {
                if (IsLink(directory)) return;

                bool hasIpc = false;
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    // Launch requests can arrive while the primary instance is starting.
                    if (preserveIpc && string.Equals(entry.Name, "IPC", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIpc = true;
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        DeleteDirectory(child);
                    }
                    else
                    {
                        DeleteFile(entry);
                    }
                }

                if (!hasIpc) directory.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not remove temporary directory '{directory.FullName}': {ex.Message}");
            }
        }

        private static void DeleteFile(FileSystemInfo entry)
        {
            try
            {
                if (!IsLink(entry)) entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not remove temporary file '{entry.FullName}': {ex.Message}");
            }
        }
    }
}
