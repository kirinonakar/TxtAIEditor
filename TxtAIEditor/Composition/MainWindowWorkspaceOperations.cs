using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowWorkspaceOperations
    {
        public static async Task RefreshGitStatusUiAsync(
            MainWindowState state,
            IGitService gitService,
            DispatcherTimer gitAutoRefreshTimer,
            TabNavigationController tabNavigation,
            GitStatusRefreshController gitStatusRefresh,
            ExplorerNavigationController explorerNavigation,
            Action<string> setCurrentRepoPath)
        {
            UpdateCurrentRepoPathFromWorkspace(state, gitService, tabNavigation, setCurrentRepoPath);
            if (!string.IsNullOrWhiteSpace(state.CurrentFolderPath) && Directory.Exists(state.CurrentFolderPath))
            {
                gitAutoRefreshTimer.Start();
            }

            await gitStatusRefresh.RefreshAsync();
            await explorerNavigation.UpdateGitStatusesAsync();
        }

        public static string GetCurrentRepoPathForGitRefresh(
            MainWindowState state,
            IGitService gitService,
            TabNavigationController tabNavigation,
            Action<string> setCurrentRepoPath)
        {
            UpdateCurrentRepoPathFromWorkspace(state, gitService, tabNavigation, setCurrentRepoPath);
            return state.CurrentRepoPath;
        }

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

        private static void UpdateCurrentRepoPathFromWorkspace(
            MainWindowState state,
            IGitService gitService,
            TabNavigationController tabNavigation,
            Action<string> setCurrentRepoPath)
        {
            string searchPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(state.CurrentFolderPath) && Directory.Exists(state.CurrentFolderPath))
            {
                searchPath = state.CurrentFolderPath;
            }
            else
            {
                string? activeFilePath = tabNavigation.GetActiveTab()?.FilePath;
                if (!string.IsNullOrWhiteSpace(activeFilePath))
                {
                    searchPath = File.Exists(activeFilePath)
                        ? Path.GetDirectoryName(activeFilePath) ?? string.Empty
                        : activeFilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(searchPath))
            {
                return;
            }

            setCurrentRepoPath(gitService.FindRepositoryRoot(searchPath) ?? string.Empty);
        }
    }
}
