using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
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
            SearchLocalCore(rootPaths, query, hideUnwantedFolders, true, publishResult, cancellationToken);
        }

        public void SearchLocal(
            string rootPath,
            string query,
            bool hideUnwantedFolders,
            Action<ExplorerItem> publishResult,
            CancellationToken cancellationToken = default)
        {
            SearchLocalCore(new[] { rootPath }, query, hideUnwantedFolders, false, publishResult, cancellationToken);
        }

        private void SearchLocalCore(
            IEnumerable<string> rootPaths,
            string query,
            bool hideUnwantedFolders,
            bool includeRootInSubPath,
            Action<ExplorerItem> publishResult,
            CancellationToken cancellationToken)
        {
            var matcher = new NameMatcher(query);
            // Scan all roots and nearby folders before descending into a large subtree.
            var directories = new Queue<(string Path, string Root)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rootPath in rootPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string fullPath = Path.GetFullPath(rootPath);
                    if (visited.Add(fullPath))
                    {
                        directories.Enqueue((fullPath, fullPath));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error resolving search root {rootPath}: {ex.Message}");
                }
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden,
                ReturnSpecialDirectories = false,
                BufferSize = 16 * 1024
            };
            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string currentDirectory, string rootPath) = directories.Dequeue();
                try
                {
                    // Use the enumeration's metadata directly: one pass per folder, and no
                    // FileInfo/DirectoryInfo or full-path allocation for nonmatching files.
                    var entries = new FileSystemEnumerable<ExplorerItem?>(
                        currentDirectory,
                        (ref FileSystemEntry entry) =>
                        {
                            bool isFolder = entry.IsDirectory;
                            string fullPath = entry.ToFullPath();
                            if (isFolder && visited.Add(fullPath))
                            {
                                directories.Enqueue((fullPath, rootPath));
                            }

                            if (isFolder && !matcher.IsMatch(entry.FileName))
                            {
                                return null;
                            }

                            string displayPath = includeRootInSubPath
                                ? fullPath
                                : Path.GetRelativePath(rootPath, fullPath);
                            return new ExplorerItem
                            {
                                Name = entry.FileName.ToString(),
                                Path = fullPath,
                                IsFolder = isFolder,
                                IsArchive = !isFolder && _archiveExplorerService.IsSupportedArchiveFile(fullPath),
                                ModifiedTime = entry.LastWriteTimeUtc.LocalDateTime,
                                FileSize = isFolder ? null : entry.Length,
                                SubPath = Path.GetDirectoryName(displayPath) ?? string.Empty
                            };
                        },
                        options)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return entry.IsDirectory
                                ? !ShouldSkipDirectory(ref entry, hideUnwantedFolders)
                                : matcher.IsMatch(entry.FileName);
                        }
                    };

                    foreach (ExplorerItem? item in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (item != null)
                        {
                            publishResult(item);
                        }
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
            return new NameMatcher(pattern).IsMatch(name.AsSpan());
        }

        public static Func<string, bool> CreateNameMatcher(string pattern)
        {
            var matcher = new NameMatcher(pattern);
            return name => matcher.IsMatch(name.AsSpan());
        }

        private sealed class NameMatcher
        {
            private readonly string _pattern;
            private readonly Regex? _regex;

            public NameMatcher(string pattern)
            {
                _pattern = pattern;
                if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    string regexPattern = "^" + Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    _regex = new Regex(regexPattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                }
            }

            public bool IsMatch(ReadOnlySpan<char> name)
            {
                return _regex?.IsMatch(name) ?? name.Contains(_pattern.AsSpan(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ShouldSkipDirectory(ref FileSystemEntry directory, bool hideUnwantedFolders)
        {
            return (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                IgnoredFolderNames.Contains(directory.FileName.ToString()) ||
                (hideUnwantedFolders && directory.FileName.StartsWith(".", StringComparison.Ordinal));
        }
    }
}
