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

        public async Task<Dictionary<string, string>?> GetStatusesAsync(string folderPath)
        {
            string repoPath = _gitService.FindRepositoryRoot(folderPath) ?? string.Empty;
            if (string.IsNullOrEmpty(repoPath))
            {
                return null;
            }

            return await _gitService.GetFileStatusesAsync(
                repoPath,
                includeAllUntrackedFiles: true,
                matchIgnoredDirectories: true);
        }

        public void ApplyStatuses(
            IEnumerable<ExplorerItem> items,
            Dictionary<string, string>? statuses,
            bool isDark)
        {
            foreach (ExplorerItem item in items)
            {
                item.IsDark = isDark;
                item.GitStatus = statuses == null
                    ? ExplorerItem.GitStatusType.Clean
                    : ResolveStatus(item, statuses);
            }
        }

        private static ExplorerItem.GitStatusType ResolveStatus(
            ExplorerItem item,
            Dictionary<string, string> statuses)
        {
            if (!item.IsFolder)
            {
                if (statuses.TryGetValue(item.Path, out string? fileStatus))
                {
                    string status = fileStatus.Trim();
                    if (status == "??")
                    {
                        return ExplorerItem.GitStatusType.Added;
                    }

                    return status == "!!"
                        ? ExplorerItem.GitStatusType.Ignored
                        : ExplorerItem.GitStatusType.Modified;
                }

                return IsPathIgnored(item.Path, statuses)
                    ? ExplorerItem.GitStatusType.Ignored
                    : ExplorerItem.GitStatusType.Clean;
            }

            bool hasModified = false;
            bool hasAdded = false;
            if (statuses.TryGetValue(item.Path, out string? folderStatus))
            {
                string status = folderStatus.Trim();
                if (status == "??")
                {
                    hasAdded = true;
                }
                else if (status != "!!")
                {
                    hasModified = true;
                }
            }

            string folderPathWithSlash = item.Path.EndsWith(Path.DirectorySeparatorChar)
                ? item.Path
                : item.Path + Path.DirectorySeparatorChar;
            foreach (KeyValuePair<string, string> statusEntry in statuses)
            {
                if (!statusEntry.Key.StartsWith(folderPathWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string status = statusEntry.Value.Trim();
                if (status == "??")
                {
                    hasAdded = true;
                }
                else if (status != "!!")
                {
                    hasModified = true;
                }
            }

            if (hasModified)
            {
                return ExplorerItem.GitStatusType.Modified;
            }

            if (hasAdded)
            {
                return ExplorerItem.GitStatusType.Added;
            }

            return IsPathIgnored(item.Path, statuses)
                ? ExplorerItem.GitStatusType.Ignored
                : ExplorerItem.GitStatusType.Clean;
        }

        private static bool IsPathIgnored(string path, Dictionary<string, string> statuses)
        {
            if (statuses.TryGetValue(path, out string? status) && status.Trim() == "!!")
            {
                return true;
            }

            foreach (KeyValuePair<string, string> statusEntry in statuses)
            {
                if (statusEntry.Value.Trim() != "!!")
                {
                    continue;
                }

                string ignoredPathWithSlash = statusEntry.Key.EndsWith(Path.DirectorySeparatorChar)
                    ? statusEntry.Key
                    : statusEntry.Key + Path.DirectorySeparatorChar;
                if (path.StartsWith(ignoredPathWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
