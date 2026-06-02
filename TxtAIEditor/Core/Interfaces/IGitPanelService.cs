using System.Collections.Generic;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public sealed class GitPanelState
    {
        public bool IsRepoDetected { get; set; }
        public string Branch { get; set; } = "Git: 감지 안됨";
        public IReadOnlyList<string> Branches { get; set; } = new List<string>();
        public IReadOnlyList<string> History { get; set; } = new List<string>();
        public IReadOnlyList<GitFileItem> Files { get; set; } = new List<GitFileItem>();
    }

    public sealed class GitComparisonContent
    {
        public string Path { get; set; } = string.Empty;
        public string OriginalContent { get; set; } = string.Empty;
        public string CurrentContent { get; set; } = string.Empty;
        public string CustomTitle { get; set; } = string.Empty;
        public string LabelA { get; set; } = string.Empty;
        public string LabelB { get; set; } = string.Empty;
    }

    public interface IGitPanelService
    {
        Task<GitPanelState> LoadStateAsync(string repoPath);
        Task<bool> StageAllAsync(string repoPath);
        Task<bool> ToggleStageAsync(string repoPath, GitFileItem item);
        Task<GitComparisonContent> BuildComparisonAsync(string repoPath, string filePath);
        Task<bool> RestoreFileAsync(string repoPath, string filePath);
        Task<bool> CommitAsync(string repoPath, string message);
        Task<bool> PushAsync(string repoPath);
        Task<bool> RestoreAllAsync(string repoPath);
    }
}
