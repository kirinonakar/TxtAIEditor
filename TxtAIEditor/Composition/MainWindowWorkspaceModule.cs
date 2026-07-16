using System;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowWorkspaceModuleDependencies(
        MainWindowShellControllers Shell,
        MainWindowPreviewModule Preview);

    internal sealed class MainWindowWorkspaceModule
    {
        private readonly MainWindowState _state;
        private readonly MainWindowServices _services;
        private readonly TabNavigationController _tabNavigation;
        private readonly Action<string> _setCurrentRepoPath;
        private MainWindowWorkspaceControllers? _controllers;

        private MainWindowWorkspaceModule(
            MainWindowState state,
            MainWindowServices services,
            TabNavigationController tabNavigation,
            Action<string> setCurrentRepoPath)
        {
            _state = state;
            _services = services;
            _tabNavigation = tabNavigation;
            _setCurrentRepoPath = setCurrentRepoPath;
        }

        public MainWindowWorkspaceControllers Controllers =>
            _controllers ?? throw new InvalidOperationException("Workspace module has not been composed yet.");

        public static MainWindowWorkspaceModule Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowWorkspaceModuleDependencies dependencies,
            MainWindowCompositionRootCallbacks callbacks,
            Func<MainWindowToolbarCommandController?> getToolbarCommand)
        {
            var shell = dependencies.Shell;
            var module = new MainWindowWorkspaceModule(
                state,
                services,
                shell.TabNavigation,
                callbacks.SetCurrentRepoPath);

            var controllers = MainWindowWorkspaceComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                shell.TabEncryption,
                dependencies.Preview.Controllers.CompareTab,
                shell.Dialog,
                new MainWindowWorkspaceCompositionCallbacks(
                    shell.StickyNoteMode.ToggleTopMostFromShortcut,
                    () => getToolbarCommand()?.ToggleTheme(),
                    shell.StickyNoteMode.ToggleMode,
                    module.GetCurrentRepoPathForGitRefresh,
                    () => state.CurrentFolderPath,
                    callbacks.GetLocalizedString,
                    module.TryGetExplorerNavigation,
                    callbacks.SetCurrentRepoPath,
                    callbacks.SetCurrentFolderPath,
                    module.RefreshGitStatusUiAsync,
                    callbacks.EnsureLeftPanelVisible,
                    callbacks.ShowLeftSidebarPage,
                    callbacks.LoadFileIntoTabAsync,
                    callbacks.InitializePickerWindow,
                    folderPath => module.NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel: true),
                    callbacks.OpenNewTabFromRequest,
                    callbacks.OpenImageTab,
                    callbacks.OpenMediaTab,
                    callbacks.OpenPdfTab,
                    callbacks.OpenOfficeDocumentTab,
                    callbacks.OpenHexTab,
                    module.QueueGitStatusRefresh));

            module.Bind(controllers);
            controllers.GitPanel.FileRestored += callbacks.GitFileRestored;
            return module;
        }

        public void LoadDirectoryRoot(string folderPath) =>
            Controllers.ExplorerNavigation.LoadDirectoryRoot(folderPath);

        public Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true) =>
            Controllers.ExplorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel);

        public void QueueGitStatusRefresh() =>
            Controllers.GitStatusRefresh.QueueRefresh();

        public void AddRecentFolder(string folderPath) =>
            Controllers.FavoritesRecent.AddRecentFolder(folderPath);

        public bool TryOpenArchive(string filePath) =>
            Controllers.ExplorerNavigation.TryOpenArchive(filePath);

        public Task<OpenedTab?> LoadFileAsync(string filePath) =>
            Controllers.FileTabLoad.LoadAsync(filePath);

        public Task<FileTabLoadResult> LoadFileWithResultAsync(string filePath) =>
            Controllers.FileTabLoad.LoadWithResultAsync(filePath);

        public Task OpenShellPathAsync(
            string path,
            ShellPanelLayoutService shellPanelLayout,
            Func<string, Task> loadFileIntoTabAsync) =>
            MainWindowWorkspaceOperations.OpenShellPathAsync(
                path,
                shellPanelLayout,
                Controllers.ExplorerNavigation,
                loadFileIntoTabAsync);

        public string GetSearchRoot() =>
            MainWindowWorkspaceOperations.GetSearchRoot(_state);

        public long GetLargeFileThresholdBytes() =>
            MainWindowWorkspaceOperations.GetLargeFileThresholdBytes(_services.SettingsService);

        public Task RefreshGitStatusUiAsync() =>
            MainWindowWorkspaceOperations.RefreshGitStatusUiAsync(
                _state,
                _services.GitService,
                Controllers.GitAutoRefreshTimer,
                _tabNavigation,
                Controllers.GitStatusRefresh,
                Controllers.ExplorerNavigation,
                _setCurrentRepoPath);

        public void RefreshCurrentFolder(string folderPath) =>
            Controllers.ExplorerNavigation.LoadDirectoryRoot(folderPath);

        private string GetCurrentRepoPathForGitRefresh() =>
            MainWindowWorkspaceOperations.GetCurrentRepoPathForGitRefresh(
                _state,
                _services.GitService,
                _tabNavigation,
                _setCurrentRepoPath);

        private ExplorerNavigationController? TryGetExplorerNavigation() =>
            _controllers?.ExplorerNavigation;

        private void Bind(MainWindowWorkspaceControllers controllers)
        {
            if (_controllers != null)
            {
                throw new InvalidOperationException("Workspace module has already been composed.");
            }

            _controllers = controllers;
        }
    }
}
