using System;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowWorkspaceOperations
    {
        public static async Task OpenShellPathAsync(
            string path,
            ShellPanelLayoutService shellPanelLayout,
            ExplorerNavigationController explorerNavigation,
            Func<string, Task> loadFileIntoTabAsync)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string cleanedPath = path.Trim().Trim('"', '\'');
            if (File.Exists(cleanedPath))
            {
                string? folderPath = Path.GetDirectoryName(cleanedPath);
                if (!explorerNavigation.IsTreeMode &&
                    !string.IsNullOrWhiteSpace(folderPath) &&
                    Directory.Exists(folderPath))
                {
                    await explorerNavigation.NavigateToFolderAsync(
                        folderPath,
                        revealInLeftPanel: shellPanelLayout.IsLeftSidebarVisible);
                }

                await loadFileIntoTabAsync(cleanedPath);
            }
            else if (Directory.Exists(cleanedPath))
            {
                await explorerNavigation.NavigateToFolderAsync(
                    cleanedPath,
                    revealInLeftPanel: shellPanelLayout.IsLeftSidebarVisible);
            }
        }

        public static string GetSearchRoot(MainWindowState state)
        {
            return state.CurrentFolderPath ?? string.Empty;
        }

        public static long GetLargeFileThresholdBytes(ISettingsService settingsService)
        {
            return settingsService.CurrentSettings.LargeFileThresholdMB * 1024L * 1024L;
        }

        public static (string? RepoPath, bool FolderExists) FindGitWorkspace(
            (string FolderPath, string? ActiveFilePath) location,
            IGitService gitService)
        {
            var (folderPath, activeFilePath) = location;
            bool folderExists = !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath);
            string? searchPath = folderExists ? folderPath : activeFilePath;
            if (!folderExists && !string.IsNullOrWhiteSpace(activeFilePath) && File.Exists(activeFilePath))
            {
                searchPath = Path.GetDirectoryName(activeFilePath);
            }

            return (string.IsNullOrWhiteSpace(searchPath) ? null : gitService.FindRepositoryRoot(searchPath) ?? string.Empty, folderExists);
        }
    }
}
