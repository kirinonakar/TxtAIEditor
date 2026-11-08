using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerGitStatusService
    {
        private readonly IGitService _gitService;

        public ExplorerGitStatusService(IGitService gitService)
        {
            _gitService = gitService;
        }

        public Task<StatusSnapshot?> GetStatusesAsync(string folderPath) =>
            Task.Run(() => GetStatusesCoreAsync(folderPath));

        private async Task<StatusSnapshot?> GetStatusesCoreAsync(string folderPath)
        {
            string repoPath = _gitService.FindRepositoryRoot(folderPath) ?? string.Empty;
            if (string.IsNullOrEmpty(repoPath))
            {
                return null;
            }

            var statuses = await _gitService.GetFileStatusesAsync(
                repoPath,
                includeAllUntrackedFiles: true,
                matchIgnoredDirectories: true);
            return new StatusSnapshot(statuses);
        }

        public void ApplyStatus(
            ExplorerItem item,
            StatusSnapshot? statuses,
            bool isDark)
        {
            item.IsDark = isDark;
            item.GitStatus = statuses?.ResolveStatus(item.Path, item.IsFolder)
                ?? ExplorerItem.GitStatusType.Clean;
        }

        // Build once on the worker thread. UI lookups only visit path ancestors,
        // rather than scanning every changed/ignored path for every visible item.
        internal sealed class StatusSnapshot
        {
            private readonly Dictionary<string, ExplorerItem.GitStatusType> _files =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ExplorerItem.GitStatusType> _folders =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _ignoredPaths = new(StringComparer.OrdinalIgnoreCase);

            public StatusSnapshot(Dictionary<string, string> statuses)
            {
                foreach (var entry in statuses)
                {
                    string path = Path.TrimEndingDirectorySeparator(entry.Key);
                    string status = entry.Value.Trim();
                    var resolved = status == "??" ? ExplorerItem.GitStatusType.Added
                        : status == "!!" ? ExplorerItem.GitStatusType.Ignored
                        : ExplorerItem.GitStatusType.Modified;
                    _files[path] = resolved;
                    if (resolved == ExplorerItem.GitStatusType.Ignored)
                    {
                        _ignoredPaths.Add(path);
                        continue;
                    }

                    for (string? parent = path; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    {
                        if (!_folders.TryGetValue(parent, out var existing) ||
                            existing != ExplorerItem.GitStatusType.Modified)
                        {
                            _folders[parent] = resolved;
                        }
                    }
                }
            }

            public ExplorerItem.GitStatusType ResolveStatus(string path, bool isFolder)
            {
                path = Path.TrimEndingDirectorySeparator(path);
                var index = isFolder ? _folders : _files;
                if (index.TryGetValue(path, out var status))
                {
                    return status;
                }

                for (string? parent = path; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    if (_ignoredPaths.Contains(parent))
                    {
                        return ExplorerItem.GitStatusType.Ignored;
                    }
                }

                return ExplorerItem.GitStatusType.Clean;
            }
        }
    }
}
