using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;
using WinRT.Interop;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowWorkspaceCompositionCallbacks(
        Action TopMostRequested,
        Action ThemeRequested,
        Action StickyNoteRequested,
        Func<string> GetCurrentRepoPathForGitRefresh,
        Func<string> GetCurrentFolderPath,
        Func<string, string, string> GetLocalizedString,
        Func<ExplorerNavigationController?> GetExplorerNavigation,
        Action<string> SetCurrentRepoPath,
        Action<string> SetCurrentFolderPath,
        Func<Task> RefreshGitStatusAsync,
        Action EnsureLeftPanelVisible,
        Action<int> ShowLeftSidebarPage,
        Func<string, Task> LoadFileIntoTabAsync,
        Action<object> InitializePickerWindow,
        Func<string, Task> NavigateExplorerToFolderAndRevealAsync,
        Func<FileTabOpenRequest, OpenedTab> OpenNewTabFromRequest,
        Func<string, OpenedTab> OpenImageTab,
        Func<string, OpenedTab> OpenMediaTab,
        Func<string, OpenedTab> OpenPdfTab,
        Func<string, OpenedTab> OpenOfficeDocumentTab,
        Func<string, OpenedTab> OpenHexTab,
        Func<string, OpenedTab> OpenNotebookTab,
        Action QueueGitStatusRefresh);

    internal sealed record MainWindowWorkspaceControllers(
        FunctionKeyShortcutService FunctionKeyShortcut,
        DispatcherTimer GitAutoRefreshTimer,
        GitPanelController GitPanel,
        GitStatusRefreshController GitStatusRefresh,
        FileTabLoadController FileTabLoad,
        ExplorerNavigationController ExplorerNavigation,
        FavoritesRecentController FavoritesRecent);

    internal static class MainWindowWorkspaceComposition
    {
        public static MainWindowWorkspaceControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            TabEncryptionController tabEncryption,
            CompareTabController compareTab,
            WindowDialogController dialog,
            MainWindowWorkspaceCompositionCallbacks callbacks)
        {
            var functionKeyShortcut = new FunctionKeyShortcutService(() => WindowNative.GetWindowHandle(window));
            functionKeyShortcut.TopMostRequested += (_, _) => callbacks.TopMostRequested();
            functionKeyShortcut.ThemeRequested += (_, _) => callbacks.ThemeRequested();
            functionKeyShortcut.StickyNoteRequested += (_, _) => callbacks.StickyNoteRequested();

            var gitAutoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };

            var gitPanel = new GitPanelController(
                workspaceServices.GitService,
                documentServices.FileService,
                viewModel,
                ui.LeftSidebar,
                ui.StatusBar.GitBranchText,
                callbacks.GetCurrentRepoPathForGitRefresh,
                callbacks.GetCurrentFolderPath,
                () => commonServices.SettingsService.CurrentSettings.StripJupyterOutputsOnCommit,
                () => ui.RootElement.XamlRoot,
                callbacks.GetLocalizedString,
                dialog.ShowErrorMessage,
                () => gitAutoRefreshTimer.Start(),
                compareTab.OpenCompareTabAsync,
                beforeDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.ResumeNativeWindows(); },
                refreshExplorerGitStatus: async () =>
                {
                    var explorerNavigation = callbacks.GetExplorerNavigation();
                    if (explorerNavigation != null)
                    {
                        await explorerNavigation.UpdateGitStatusesAsync();
                    }
                });

            var gitStatusRefresh = new GitStatusRefreshController(
                window.DispatcherQueue,
                gitAutoRefreshTimer,
                callbacks.GetCurrentRepoPathForGitRefresh,
                gitPanel.RefreshAsync);

            var fileTabLoad = new FileTabLoadController(
                workspaceServices.GitService,
                documentServices.SecureNoteEncryptionService,
                workspaceServices.ArchiveExplorerService,
                viewModel,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                callbacks.SetCurrentRepoPath,
                callbacks.GetLocalizedString,
                tabEncryption.PromptPasswordAsync,
                callbacks.OpenNewTabFromRequest,
                callbacks.OpenImageTab,
                callbacks.OpenMediaTab,
                callbacks.OpenPdfTab,
                callbacks.OpenOfficeDocumentTab,
                callbacks.OpenHexTab,
                callbacks.OpenNotebookTab,
                callbacks.QueueGitStatusRefresh,
                dialog.ShowErrorMessage);

            var explorerNavigation = new ExplorerNavigationController(
                ui.LeftSidebar,
                viewModel,
                workspaceServices.ExplorerDirectoryService,
                workspaceServices.ArchiveExplorerService,
                workspaceServices.RemoteWorkspaceService,
                workspaceServices.GitService,
                callbacks.InitializePickerWindow,
                callbacks.SetCurrentFolderPath,
                callbacks.SetCurrentRepoPath,
                callbacks.RefreshGitStatusAsync,
                callbacks.EnsureLeftPanelVisible,
                callbacks.ShowLeftSidebarPage,
                callbacks.LoadFileIntoTabAsync,
                async (archivePath, entryPath) => { await fileTabLoad.LoadArchiveEntryAsync(archivePath, entryPath); },
                commonServices.LocalizationService,
                () => commonServices.SettingsService.CurrentSettings.HomeFolderPath);

            fileTabLoad.PreserveWorkspaceOnFileOpenProvider = () => explorerNavigation.IsTreeMode;

            var favoritesRecent = new FavoritesRecentController(
                commonServices.SettingsService,
                workspaceServices.RecentFilesService,
                workspaceServices.RemoteWorkspaceService,
                viewModel,
                ui.LeftSidebar,
                callback => window.DispatcherQueue.TryEnqueue(() => callback()),
                async path =>
                {
                    if (RemotePath.IsRemote(path))
                    {
                        await explorerNavigation.NavigateRemoteVirtualPathAsync(path);
                    }
                    else
                    {
                        await callbacks.NavigateExplorerToFolderAndRevealAsync(path);
                    }
                },
                () => explorerNavigation.IsTreeMode,
                path => RemotePath.IsRemote(path)
                    ? explorerNavigation.OpenRemoteFileAsync(path)
                    : callbacks.LoadFileIntoTabAsync(path),
                dialog.ShowErrorMessage,
                callbacks.GetLocalizedString);

            return new MainWindowWorkspaceControllers(
                functionKeyShortcut,
                gitAutoRefreshTimer,
                gitPanel,
                gitStatusRefresh,
                fileTabLoad,
                explorerNavigation,
                favoritesRecent);
        }
    }
}
