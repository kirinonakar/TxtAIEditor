using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerSearchService
    {
        private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "obj", ".git", ".vs", ".idea", "dist", "build", "out"
        };

        private readonly ArchiveExplorerService _archiveExplorerService;

        public ExplorerSearchService(ArchiveExplorerService archiveExplorerService)
        {
            _archiveExplorerService = archiveExplorerService;
        }

        public void SearchLocalRoots(
            IEnumerable<string> rootPaths,
            string query,
            bool hideUnwantedFolders,
            Action<ExplorerItem> publishResult,
            CancellationToken cancellationToken = default)
        {
            foreach (string rootPath in rootPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchLocal(rootPath, query, hideUnwantedFolders, item =>
                {
                    // Include the drive so results from different roots are distinguishable.
                    item.SubPath = Path.GetDirectoryName(item.Path) ?? rootPath;
                    publishResult(item);
                }, cancellationToken);
            }
        }

        public void SearchLocal(
            string rootPath,
            string query,
            bool hideUnwantedFolders,
            Action<ExplorerItem> publishResult,
            CancellationToken cancellationToken = default)
        {
            var directories = new Stack<string>();
            directories.Push(rootPath);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                visited.Add(Path.GetFullPath(rootPath));
            }
            catch
            {
                visited.Add(rootPath);
            }

            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string currentDirectory = directories.Pop();
                try
                {
                    var directoryInfo = new DirectoryInfo(currentDirectory);
                    if (currentDirectory != rootPath &&
                        ShouldSkipDirectory(directoryInfo, hideUnwantedFolders))
                    {
                        continue;
                    }

                    foreach (FileInfo file in directoryInfo.EnumerateFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (file.Attributes.HasFlag(FileAttributes.Hidden) ||
                            !MatchesPattern(file.Name, query))
                        {
                            continue;
                        }

                        string relativePath = Path.GetRelativePath(rootPath, file.FullName);
                        publishResult(new ExplorerItem
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            IsFolder = false,
                            IsArchive = _archiveExplorerService.IsSupportedArchiveFile(file.FullName),
                            ModifiedTime = file.LastWriteTime,
                            FileSize = file.Length,
                            SubPath = Path.GetDirectoryName(relativePath) ?? string.Empty
                        });
                    }

                    foreach (DirectoryInfo subdirectory in directoryInfo.EnumerateDirectories())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (ShouldSkipDirectory(subdirectory, hideUnwantedFolders))
                        {
                            continue;
                        }

                        string canonicalPath;
                        try
                        {
                            canonicalPath = Path.GetFullPath(subdirectory.FullName);
                        }
                        catch
                        {
                            canonicalPath = subdirectory.FullName;
                        }

                        if (!visited.Add(canonicalPath))
                        {
                            continue;
                        }

                        if (MatchesPattern(subdirectory.Name, query))
                        {
                            string relativePath = Path.GetRelativePath(rootPath, subdirectory.FullName);
                            publishResult(new ExplorerItem
                            {
                                Name = subdirectory.Name,
                                Path = subdirectory.FullName,
                                IsFolder = true,
                                ModifiedTime = subdirectory.LastWriteTime,
                                SubPath = Path.GetDirectoryName(relativePath) ?? string.Empty
                            });
                        }

                        directories.Push(subdirectory.FullName);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error scanning folder {currentDirectory}: {ex.Message}");
                }
            }
        }

        public static bool IsHiddenFolderName(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal) ||
                string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesPattern(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                string regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return Regex.IsMatch(
                    name,
                    regexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipDirectory(DirectoryInfo directory, bool hideUnwantedFolders)
        {
            return directory.Attributes.HasFlag(FileAttributes.Hidden) ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                IgnoredFolderNames.Contains(directory.Name) ||
                (hideUnwantedFolders && IsHiddenFolderName(directory.Name));
        }
    }
}
