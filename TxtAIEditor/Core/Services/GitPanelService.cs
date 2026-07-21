using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class GitPanelService : IGitPanelService
    {
        private readonly IGitService _gitService;
        private readonly IFileService _fileService;

        public GitPanelService(IGitService gitService, IFileService fileService)
        {
            _gitService = gitService;
            _fileService = fileService;
        }

        public async Task<GitPanelState> LoadStateAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                return new GitPanelState();
            }

            string branch = await _gitService.GetCurrentBranchAsync(repoPath);
            var branches = await _gitService.GetBranchesAsync(repoPath);
            var history = await _gitService.GetRecentHistoryAsync(repoPath);
            var fileStatuses = await _gitService.GetFileStatusesAsync(repoPath, includeAllUntrackedFiles: true);
            var files = new List<GitFileItem>();

            foreach (var kvp in fileStatuses)
            {
                string fullPath = kvp.Key;
                string status = kvp.Value;
                bool isStaged = status.Length > 0 && status[0] != ' ' && status != "??";
                bool isUnstaged = status.Length > 1 && status[1] != ' ';
                string statusDesc = isStaged ? "Staged" : "Unstaged";

                if (status == "??") statusDesc = "Untracked";
                else if (status.Contains("D", StringComparison.Ordinal)) statusDesc = isStaged ? "Deleted staged" : "Deleted";
                else if (status.Contains("R", StringComparison.Ordinal)) statusDesc = "Renamed";
                else if (status.Contains("A", StringComparison.Ordinal)) statusDesc = isStaged ? "Added staged" : "Added";
                else if (isStaged && isUnstaged) statusDesc = "Staged + Unstaged";

                string displayStatus = status == "??" ? "U" : status.Trim();
                files.Add(new GitFileItem
                {
                    Name = GetDisplayName(fullPath),
                    Path = fullPath,
                    StatusText = $"{statusDesc} ({displayStatus})",
                    ActionGlyph = isStaged ? "\xE108" : "\xE109",
                    IsStaged = isStaged
                });
            }

            return new GitPanelState
            {
                IsRepoDetected = !GitBranchStatus.IsNotDetected(branch),
                Branch = branch,
                Branches = branches,
                History = history,
                Files = files
            };
        }

        public Task<bool> StageAllAsync(string repoPath)
        {
            return _gitService.StageAllAsync(repoPath);
        }

        public Task<bool> ToggleStageAsync(string repoPath, GitFileItem item)
        {
            return item.IsStaged
                ? _gitService.UnstageFileAsync(repoPath, item.Path)
                : _gitService.StageFileAsync(repoPath, item.Path);
        }

        public async Task<GitComparisonContent> BuildComparisonAsync(string repoPath, string filePath)
        {
            string originalContent = await _gitService.GetGitFileContentAsync(repoPath, filePath);
            string currentContent = File.Exists(filePath)
                ? await _fileService.ReadTextFileAsync(filePath)
                : string.Empty;

            string fileName = GetDisplayName(filePath);
            return new GitComparisonContent
            {
                Path = filePath,
                OriginalContent = originalContent,
                CurrentContent = currentContent,
                CustomTitle = $"Git 비교: {fileName}",
                LabelA = $"{fileName} (이전 버전)",
                LabelB = $"{fileName} (현재 변경 사항)"
            };
        }

        public Task<bool> RestoreFileAsync(string repoPath, string filePath)
        {
            return _gitService.RestoreFileAsync(repoPath, filePath);
        }

        public Task<bool> CommitAsync(string repoPath, string message)
        {
            return _gitService.CommitAsync(repoPath, message);
        }

        public Task<bool> PushAsync(string repoPath)
        {
            return _gitService.PushAsync(repoPath);
        }

        public Task<bool> PullAsync(string repoPath)
        {
            return _gitService.PullAsync(repoPath);
        }

        public Task<bool> RebaseAsync(string repoPath)
        {
            return _gitService.RebaseAsync(repoPath);
        }

        public Task<bool> GitGcAsync(string repoPath)
        {
            return _gitService.GitGcAsync(repoPath);
        }

        public Task<bool> HardResetAsync(string repoPath)
        {
            return _gitService.HardResetAsync(repoPath);
        }

        public Task<bool> PushForceAsync(string repoPath)
        {
            return _gitService.PushForceAsync(repoPath);
        }

        public Task<bool> RestoreAllAsync(string repoPath)
        {
            return _gitService.RestoreAllAsync(repoPath);
        }

        private static string GetDisplayName(string path)
        {
            string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(normalizedPath);
            return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
        }
    }
}
