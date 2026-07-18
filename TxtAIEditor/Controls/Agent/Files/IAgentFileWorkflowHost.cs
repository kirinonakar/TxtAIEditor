using System.Collections.Generic;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public interface IAgentFileWorkflowHost
    {
        IReadOnlyList<AgentFileEditPreview> GetSessionEdits();

        ExplorerItem? GetSelectedExplorerItem();

        string GetCurrentFolderPath();

        string GetCurrentRepoPath();

        void LoadDirectoryRoot(string folderPath);

        void QueueGitStatusRefresh();

        string GetLocalizedString(string key, string fallback);
    }
}
