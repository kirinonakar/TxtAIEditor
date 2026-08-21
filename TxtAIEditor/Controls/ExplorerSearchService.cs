using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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

        public List<ExplorerItem> SearchLocal(string rootPath, string query, bool hideUnwantedFolders)
        {
            var results = new List<ExplorerItem>();
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
                string currentDirectory = directories.Pop();
                try
                {
                    var directoryInfo = new DirectoryInfo(currentDirectory);
                    if (currentDirectory != rootPath &&
                        ShouldSkipDirectory(directoryInfo, hideUnwantedFolders))
                    {
                        continue;
                    }

                    foreach (FileInfo file in directoryInfo.GetFiles())
                    {
                        if (file.Attributes.HasFlag(FileAttributes.Hidden) ||
                            !MatchesPattern(file.Name, query))
                        {
                            continue;
                        }

                        string relativePath = Path.GetRelativePath(rootPath, file.FullName);
                        results.Add(new ExplorerItem
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            IsFolder = false,
                            IsArchive = _archiveExplorerService.IsSupportedArchiveFile(file.FullName),
                            ModifiedTime = file.LastWriteTime,
                            SubPath = Path.GetDirectoryName(relativePath) ?? string.Empty
                        });
                    }

                    foreach (DirectoryInfo subdirectory in directoryInfo.GetDirectories())
                    {
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
                            results.Add(new ExplorerItem
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
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error scanning folder {currentDirectory}: {ex.Message}");
                }
            }

            return results;
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
