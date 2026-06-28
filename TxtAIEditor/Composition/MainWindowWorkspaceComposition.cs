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
        Func<string, OpenedTab> OpenPdfTab,
        Func<string, OpenedTab> OpenOfficeDocumentTab,
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
            MainWindowServices services,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            TabEncryptionController tabEncryption,
            CompareTabController compareTab,
            WindowDialogController dialog,
            MainWindowWorkspaceCompositionCallbacks callbacks)
        {
            var functionKeyShortcut = new FunctionKeyShortcutService(WindowNative.GetWindowHandle(window));
            functionKeyShortcut.TopMostRequested += (_, _) => callbacks.TopMostRequested();
            functionKeyShortcut.ThemeRequested += (_, _) => callbacks.ThemeRequested();
            functionKeyShortcut.StickyNoteRequested += (_, _) => callbacks.StickyNoteRequested();

            var gitAutoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };

            var gitPanel = new GitPanelController(
                services.GitService,
                services.FileService,
                viewModel,
                ui.LeftSidebar,
                ui.StatusBar.GitBranchText,
                callbacks.GetCurrentRepoPathForGitRefresh,
                callbacks.GetCurrentFolderPath,
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
                services.GitService,
                services.SecureNoteEncryptionService,
                viewModel,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                callbacks.SetCurrentRepoPath,
                callbacks.GetLocalizedString,
                tabEncryption.PromptPasswordAsync,
                callbacks.OpenNewTabFromRequest,
                callbacks.OpenImageTab,
                callbacks.OpenPdfTab,
                callbacks.OpenOfficeDocumentTab,
                callbacks.QueueGitStatusRefresh,
                dialog.ShowErrorMessage);

            var explorerNavigation = new ExplorerNavigationController(
                ui.LeftSidebar,
                viewModel,
                services.ExplorerDirectoryService,
                services.GitService,
                callbacks.InitializePickerWindow,
                callbacks.SetCurrentFolderPath,
                callbacks.SetCurrentRepoPath,
                callbacks.RefreshGitStatusAsync,
                callbacks.EnsureLeftPanelVisible,
                callbacks.ShowLeftSidebarPage,
                callbacks.LoadFileIntoTabAsync,
                services.LocalizationService,
                () => services.SettingsService.CurrentSettings.HomeFolderPath);

            var favoritesRecent = new FavoritesRecentController(
                services.SettingsService,
                services.RecentFilesService,
                viewModel,
                ui.LeftSidebar,
                callback => window.DispatcherQueue.TryEnqueue(() => callback()),
                callbacks.NavigateExplorerToFolderAndRevealAsync,
                callbacks.LoadFileIntoTabAsync,
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
