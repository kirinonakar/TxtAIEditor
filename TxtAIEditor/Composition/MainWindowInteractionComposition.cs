using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowInteractionCallbacks(
        Func<string> GetCurrentFolderPath,
        Func<string> GetCurrentRepoPath,
        Action OpenNewTab,
        Action<string> LoadDirectoryRoot,
        Action<string> RefreshExplorerTreeFolder,
        Func<string, Task> LoadFileIntoTabAsync,
        Func<string, Task> OpenFileInExternalViewerAsync,
        Func<string, Task> OpenFileWithDefaultProgramAsync,
        Func<string, int, Task> LoadFileIntoTabAtLineAsync,
        Func<string, bool, Task> NavigateExplorerToFolderAsync,
        Func<string, Task> NavigateExplorerToFolderAndRevealAsync,
        Func<TxtAIEditor.ExplorerItem?> GetSelectedExplorerItem,
        Func<bool> IsExplorerArchiveView,
        Func<bool> IsExplorerRemoteView,
        Func<Task> RefreshRemoteExplorerAsync,
        Func<bool> IsExplorerTreeMode,
        Func<Task> ToggleLeftPanelAsync,
        Func<Task> ToggleRightPanelAsync,
        Action FocusSearchPanel,
        Action CloseActiveTab,
        Action SaveActive,
        Action SaveActiveAs,
        Action OpenFile,
        Action Find,
        Action Print,
        Action ToggleTopMost,
        Action ToggleTheme,
        Action ToggleStickyNote,
        Action ToggleLivePreview,
        Action ToggleExplorerTreeMode,
        Action ToggleCsvTableMode,
        Action TogglePreviewWidth,
        Action ToggleMaximize,
        Action ToggleWordWrap,
        Action<int> ShowLeftSidebarPage,
        Action<OpenedTab, TabViewItem> CloseTabAndCleanup,
        Func<Task> SyncSnippetsToOpenEditorsAsync,
        Action<object> InitializePickerWindow,
        Func<string, string, string> GetLocalizedString,
        Func<ElementTheme> GetCurrentElementTheme,
        Func<OpenedTab, TabViewItem, Task> ReloadTabAsync,
        Func<OpenedTab, bool, Task> SetHexViewModeAsync,
        Func<OpenedTab, bool, Task> SetCsvTableModeAsync,
        Action<OpenedTab, TabViewItem, TabView> CloseRightTabs,
        Action<OpenedTab, TabViewItem, TabView> CloseLeftTabs,
        Action<OpenedTab, TabViewItem, TabView> CloseOtherTabs,
        Func<string, Task> OpenNotebookSourceTabAsync,
        Func<string, Task> OpenNotebookViewerTabAsync,
        Func<OpenedTab, Task> OpenTabInNewWindowAsync);

    internal sealed record MainWindowInteractionControllers(
        ExplorerFileActionsController ExplorerFileActions,
        TabContextMenuController TabContextMenu,
        FileOpenDropController FileOpenDrop,
        RootKeyboardShortcutController RootKeyboardShortcut,
        TerminalPanelController TerminalPanel,
        SnippetsController Snippets);

    internal static class MainWindowInteractionComposition
    {
        public static MainWindowInteractionControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowViewModel viewModel,
            MainWindowShellModule shellModule,
            MainWindowEditorModule editorModule,
            MainWindowWorkspaceModule workspaceModule,
            MainWindowInteractionCallbacks callbacks)
        {
            var shell = shellModule.Composition;
            var tabNavigation = shell.TabNavigation;
            var dialog = shell.Dialog;
            var activeEditorInsertion = editorModule.Foundation.ActiveEditorInsertion;

            var explorerFileActions = new ExplorerFileActionsController(
                ui.LeftSidebar,
                ui.StatusBar,
                viewModel,
                workspaceServices.ArchiveExplorerService,
                workspaceServices.RemoteWorkspaceService,
                ui.EditorTabView,
                ui.EditorTabView2,
                callbacks.GetCurrentFolderPath,
                tabNavigation.GetActiveTab,
                callbacks.LoadDirectoryRoot,
                callbacks.RefreshExplorerTreeFolder,
                callbacks.LoadFileIntoTabAsync,
                callbacks.OpenFileInExternalViewerAsync,
                callbacks.OpenFileWithDefaultProgramAsync,
                activeEditorInsertion.InsertTextAsync,
                callbacks.CloseTabAndCleanup,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                callbacks.GetLocalizedString,
                dialog.ShowErrorMessage,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                callbacks.IsExplorerArchiveView,
                callbacks.IsExplorerRemoteView,
                callbacks.RefreshRemoteExplorerAsync,
                callbacks.InitializePickerWindow,
                () => commonServices.SettingsService.CurrentSettings.HomeFolderPath);

            return ComposeAfterExplorerActions(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
                viewModel,
                shellModule,
                editorModule,
                workspaceModule,
                callbacks,
                explorerFileActions);
        }

        private static MainWindowInteractionControllers ComposeAfterExplorerActions(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowViewModel viewModel,
            MainWindowShellModule shellModule,
            MainWindowEditorModule editorModule,
            MainWindowWorkspaceModule workspaceModule,
            MainWindowInteractionCallbacks callbacks,
            ExplorerFileActionsController explorerFileActions)
        {
            var shell = shellModule.Composition;
            var shellPanelLayout = shell.ShellPanelLayout;
            var terminalShortcut = shell.TerminalShortcut;
            var tabNavigation = shell.TabNavigation;
            var tabEncryption = shell.TabEncryption;
            var dialog = shell.Dialog;
            var activeEditorInsertion = editorModule.Foundation.ActiveEditorInsertion;
            var favoritesRecent = workspaceModule.Composition.FavoritesRecent;

            var terminalPanel = new TerminalPanelController(
                window,
                ui.EditorWorkspace,
                ui.TopToolbar,
                callbacks.GetSelectedExplorerItem,
                callbacks.GetCurrentFolderPath,
                callbacks.GetCurrentRepoPath,
                callbacks.LoadFileIntoTabAtLineAsync,
                callbacks.NavigateExplorerToFolderAndRevealAsync,
                workspaceServices.RemoteWorkspaceService);

            var tabContextMenu = new TabContextMenuController(
                favoritesRecent,
                callbacks.GetLocalizedString,
                callbacks.ShowLeftSidebarPage,
                callbacks.NavigateExplorerToFolderAndRevealAsync,
                callbacks.ReloadTabAsync,
                callbacks.SetHexViewModeAsync,
                callbacks.SetCsvTableModeAsync,
                tabEncryption.EncryptAsync,
                tabEncryption.ChangePasswordAsync,
                tabEncryption.RemoveEncryptionAsync,
                callbacks.CloseRightTabs,
                callbacks.CloseLeftTabs,
                callbacks.CloseOtherTabs,
                terminalPanel.RunFileInTerminal,
                tabNavigation.GetTabViewForItem,
                callbacks.OpenNotebookSourceTabAsync,
                callbacks.OpenNotebookViewerTabAsync,
                callbacks.OpenTabInNewWindowAsync);

            var fileOpenDrop = new FileOpenDropController(
                ui.DragOverlay,
                ui.LeftSidebar,
                ui.PreviewGrid,
                callbacks.InitializePickerWindow,
                callbacks.LoadFileIntoTabAsync,
                callbacks.NavigateExplorerToFolderAsync,
                callbacks.IsExplorerTreeMode,
                () => shellPanelLayout.IsLeftSidebarVisible,
                dialog.ShowErrorMessage,
                callbacks.GetLocalizedString);

            var rootKeyboardShortcut = new RootKeyboardShortcutController(
                callbacks.OpenNewTab,
                callbacks.ToggleLeftPanelAsync,
                callbacks.ToggleRightPanelAsync,
                callbacks.FocusSearchPanel,
                callbacks.CloseActiveTab,
                callbacks.SaveActive,
                callbacks.SaveActiveAs,
                callbacks.OpenFile,
                callbacks.Find,
                callbacks.Print,
                callbacks.ToggleTopMost,
                callbacks.ToggleTheme,
                callbacks.ToggleStickyNote,
                terminalShortcut,
                callbacks.ToggleLivePreview,
                callbacks.ToggleExplorerTreeMode,
                callbacks.TogglePreviewWidth,
                callbacks.ToggleMaximize,
                callbacks.ToggleWordWrap);

            var snippets = new SnippetsController(
                editorServices.SnippetService,
                viewModel,
                ui.LeftSidebar,
                () => ui.RootElement.XamlRoot,
                activeEditorInsertion.InsertTextAsync,
                callbacks.SyncSnippetsToOpenEditorsAsync,
                dialog.ShowErrorMessage,
                callbacks.GetLocalizedString,
                callbacks.InitializePickerWindow,
                beforeDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.ResumeNativeWindows(); });

            return new MainWindowInteractionControllers(
                explorerFileActions,
                tabContextMenu,
                fileOpenDrop,
                rootKeyboardShortcut,
                terminalPanel,
                snippets);
        }
    }
}
