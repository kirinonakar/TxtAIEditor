using System;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowWorkspaceModuleDependencies(
        MainWindowShellModule Shell,
        MainWindowPreviewModule Preview);

    internal sealed class MainWindowWorkspaceModule
    {
        private readonly MainWindowState _state;
        private readonly MainWindowCommonServices _commonServices;
        private readonly MainWindowWorkspaceServices _workspaceServices;
        private readonly TabNavigationController _tabNavigation;
        private readonly Action<string> _setCurrentRepoPath;
        private MainWindowWorkspaceControllers? _controllers;

        private MainWindowWorkspaceModule(
            MainWindowState state,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            TabNavigationController tabNavigation,
            Action<string> setCurrentRepoPath)
        {
            _state = state;
            _commonServices = commonServices;
            _workspaceServices = workspaceServices;
            _tabNavigation = tabNavigation;
            _setCurrentRepoPath = setCurrentRepoPath;
        }

        internal MainWindowWorkspaceControllers Composition =>
            _controllers ?? throw new InvalidOperationException("Workspace module has not been composed yet.");

        public static MainWindowWorkspaceModule Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowWorkspaceModuleDependencies dependencies,
            IMainWindowShellFacade shellFacade,
            IMainWindowDocumentFacade documentFacade,
            IMainWindowPreviewFacade previewFacade,
            IMainWindowWorkspaceFacade workspaceFacade,
            Func<MainWindowToolbarCommandController?> getToolbarCommand)
        {
            var shell = dependencies.Shell.Composition;
            var module = new MainWindowWorkspaceModule(
                state,
                commonServices,
                workspaceServices,
                shell.TabNavigation,
                workspaceFacade.SetCurrentRepoPath);

            var controllers = MainWindowWorkspaceComposition.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state.TabBridges,
                shell.TabEncryption,
                dependencies.Preview.Composition.CompareTab,
                shell.Dialog,
                new MainWindowWorkspaceCompositionCallbacks(
                    shell.StickyNoteMode.ToggleTopMostFromShortcut,
                    () => getToolbarCommand()?.ToggleTheme(),
                    shell.StickyNoteMode.ToggleMode,
                    module.GetCurrentRepoPathForGitRefresh,
                    () => state.CurrentFolderPath,
                    shellFacade.GetLocalizedString,
                    module.TryGetExplorerNavigation,
                    workspaceFacade.SetCurrentRepoPath,
                    workspaceFacade.SetCurrentFolderPath,
                    module.RefreshGitStatusUiAsync,
                    shellFacade.EnsureLeftPanelVisible,
                    shellFacade.ShowLeftSidebarPage,
                    documentFacade.LoadFileIntoTabAsync,
                    shellFacade.InitializePickerWindow,
                    folderPath => module.NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel: true),
                    documentFacade.OpenNewTab,
                    previewFacade.OpenImageTab,
                    previewFacade.OpenMediaTab,
                    previewFacade.OpenPdfTab,
                    previewFacade.OpenOfficeDocumentTab,
                    previewFacade.OpenHexTab,
                    previewFacade.OpenNotebookTab,
                    module.QueueGitStatusRefresh));

            module.Bind(controllers);
            controllers.GitPanel.FileRestored += workspaceFacade.HandleGitFileRestored;
            return module;
        }

        public void LoadDirectoryRoot(string folderPath) =>
            Composition.ExplorerNavigation.LoadDirectoryRoot(folderPath);

        public void SetExplorerTreeMode(bool enableTreeMode) =>
            Composition.ExplorerNavigation.SetTreeMode(enableTreeMode);

        public void ToggleExplorerTreeMode() =>
            Composition.ExplorerNavigation.SetTreeMode(!Composition.ExplorerNavigation.IsTreeMode);

        public void RefreshTreeFolder(string folderPath) =>
            Composition.ExplorerNavigation.RefreshTreeFolder(folderPath);

        public bool IsViewingArchive =>
            Composition.ExplorerNavigation.IsViewingArchive;

        public bool IsViewingRemote =>
            Composition.ExplorerNavigation.IsViewingRemote;

        public bool IsTreeMode =>
            Composition.ExplorerNavigation.IsTreeMode;

        public Task RefreshRemoteDirectoryAsync() =>
            Composition.ExplorerNavigation.RefreshRemoteDirectoryAsync();

        public async Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true)
        {
            if (RemotePath.IsRemote(folderPath))
            {
                await Composition.ExplorerNavigation.NavigateRemoteVirtualPathAsync(folderPath, revealInLeftPanel);
            }
            else
            {
                await Composition.ExplorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel);
            }
        }

        public void QueueGitStatusRefresh() =>
            Composition.GitStatusRefresh.QueueRefresh();

        public void AddRecentFolder(string folderPath) =>
            Composition.FavoritesRecent.AddRecentFolder(folderPath);

        public bool TryOpenArchive(string filePath) =>
            Composition.ExplorerNavigation.TryOpenArchive(filePath);

        public Task<OpenedTab?> LoadFileAsync(string filePath) =>
            Composition.FileTabLoad.LoadAsync(filePath);

        public Task<FileTabLoadResult> LoadFileWithResultAsync(string filePath) =>
            Composition.FileTabLoad.LoadWithResultAsync(filePath);

        public Task OpenShellPathAsync(
            string path,
            MainWindowShellModule shell,
            Func<string, Task> loadFileIntoTabAsync) =>
            MainWindowWorkspaceOperations.OpenShellPathAsync(
                path,
                shell.Composition.ShellPanelLayout,
                Composition.ExplorerNavigation,
                loadFileIntoTabAsync);

        public string GetSearchRoot() =>
            MainWindowWorkspaceOperations.GetSearchRoot(_state);

        public long GetLargeFileThresholdBytes() =>
            MainWindowWorkspaceOperations.GetLargeFileThresholdBytes(_commonServices.SettingsService);

        public Task RefreshGitStatusUiAsync() =>
            MainWindowWorkspaceOperations.RefreshGitStatusUiAsync(
                _state,
                _workspaceServices.GitService,
                Composition.GitAutoRefreshTimer,
                _tabNavigation,
                Composition.GitStatusRefresh,
                Composition.ExplorerNavigation,
                _setCurrentRepoPath);

        public void RefreshCurrentFolder(string folderPath) =>
            Composition.ExplorerNavigation.LoadDirectoryRoot(folderPath);

        private string GetCurrentRepoPathForGitRefresh() =>
            MainWindowWorkspaceOperations.GetCurrentRepoPathForGitRefresh(
                _state,
                _workspaceServices.GitService,
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
