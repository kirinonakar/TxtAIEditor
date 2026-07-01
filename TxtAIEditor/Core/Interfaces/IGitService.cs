using System.Threading.Tasks;
using System.Collections.Generic;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IGitService
    {
        string? FindRepositoryRoot(string? startPath);
        Task<string> GetCurrentBranchAsync(string repoPath);
        Task<Dictionary<string, string>> GetFileStatusesAsync(string repoPath);
        Task<string> GetFileDiffAsync(string repoPath, string filePath);
        Task<string> GetGitFileContentAsync(string repoPath, string filePath);
        Task<bool> StageFileAsync(string repoPath, string filePath);
        Task<bool> StageAllAsync(string repoPath);
        Task<bool> UnstageFileAsync(string repoPath, string filePath);
        Task<bool> RestoreFileAsync(string repoPath, string filePath);
        Task<bool> RestoreAllAsync(string repoPath);
        Task<bool> CommitAsync(string repoPath, string message);
        Task<bool> PushAsync(string repoPath);
        Task<bool> PullAsync(string repoPath);
        Task<bool> RebaseAsync(string repoPath);
        Task<bool> CheckoutBranchAsync(string repoPath, string branchName);
        Task<string> GetRemoteUrlAsync(string repoPath, string remoteName = "origin");
        Task<bool> SetRemoteUrlAsync(string repoPath, string remoteUrl, string remoteName = "origin");
        Task<bool> LinkCurrentBranchToRemoteAsync(string repoPath, string remoteName = "origin");
        Task<int> GetUnpushedCommitCountAsync(string repoPath);
        Task<IReadOnlyList<GitHistoryItem>> GetRecentHistoryAsync(string repoPath, int maxCount = 50);
        Task<IReadOnlyList<string>> GetBranchesAsync(string repoPath);
        Task<bool> InitRepositoryAsync(string repoPath);
        Task<string> RunGitCommandAsync(string workingDir, string arguments);
        Task<IReadOnlyList<(string Status, string Path)>> GetCommitChangedFilesAsync(string repoPath, string commitHash);
        Task<string> GetCommitFileContentAsync(string repoPath, string commitHash, string filePath);
    }
}
