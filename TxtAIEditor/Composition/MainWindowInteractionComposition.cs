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
        Func<string, Task> LoadFileIntoTabAsync,
        Func<string, Task> OpenFileInExternalViewerAsync,
        Func<string, Task> OpenFileWithDefaultProgramAsync,
        Func<string, int, Task> LoadFileIntoTabAtLineAsync,
        Func<string, bool, Task> NavigateExplorerToFolderAsync,
        Func<string, Task> NavigateExplorerToFolderAndRevealAsync,
        Func<TxtAIEditor.ExplorerItem?> GetSelectedExplorerItem,
        Func<bool> IsExplorerArchiveView,
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
        Action TogglePreviewWidth,
        Action ToggleMaximize,
        Action<int> ShowLeftSidebarPage,
        Action<OpenedTab, TabViewItem> CloseTabAndCleanup,
        Func<Task> SyncSnippetsToOpenEditorsAsync,
        Action<object> InitializePickerWindow,
        Func<string, string, string> GetLocalizedString,
        Func<ElementTheme> GetCurrentElementTheme,
        Func<OpenedTab, TabViewItem, Task> ReloadTabAsync,
        Func<OpenedTab, Task> OpenHexViewAsync,
        Action<OpenedTab, TabViewItem, TabView> CloseRightTabs,
        Action<OpenedTab, TabViewItem, TabView> CloseLeftTabs,
        Action<OpenedTab, TabViewItem, TabView> CloseOtherTabs);

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
            MainWindowServices services,
            MainWindowViewModel viewModel,
            ShellPanelLayoutService shellPanelLayout,
            TerminalShortcutService terminalShortcut,
            TabNavigationController tabNavigation,
            TabEncryptionController tabEncryption,
            ActiveEditorInsertionController activeEditorInsertion,
            FavoritesRecentController favoritesRecent,
            WindowDialogController dialog,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            MainWindowInteractionCallbacks callbacks)
        {
            var explorerFileActions = new ExplorerFileActionsController(
                ui.LeftSidebar,
                ui.StatusBar,
                viewModel,
                services.ArchiveExplorerService,
                ui.EditorTabView,
                ui.EditorTabView2,
                callbacks.GetCurrentFolderPath,
                tabNavigation.GetActiveTab,
                callbacks.LoadDirectoryRoot,
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
                callbacks.IsExplorerArchiveView);

            return ComposeAfterExplorerActions(
                window,
                ui,
                services,
                viewModel,
                shellPanelLayout,
                terminalShortcut,
                tabNavigation,
                tabEncryption,
                activeEditorInsertion,
                favoritesRecent,
                dialog,
                pdfViewer,
                officeDocumentViewer,
                callbacks,
                explorerFileActions);
        }

        private static MainWindowInteractionControllers ComposeAfterExplorerActions(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            ShellPanelLayoutService shellPanelLayout,
            TerminalShortcutService terminalShortcut,
            TabNavigationController tabNavigation,
            TabEncryptionController tabEncryption,
            ActiveEditorInsertionController activeEditorInsertion,
            FavoritesRecentController favoritesRecent,
            WindowDialogController dialog,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            MainWindowInteractionCallbacks callbacks,
            ExplorerFileActionsController explorerFileActions)
        {
            var tabContextMenu = new TabContextMenuController(
                favoritesRecent,
                callbacks.GetLocalizedString,
                callbacks.ShowLeftSidebarPage,
                callbacks.NavigateExplorerToFolderAndRevealAsync,
                callbacks.ReloadTabAsync,
                callbacks.OpenHexViewAsync,
                tabEncryption.EncryptAsync,
                tabEncryption.ChangePasswordAsync,
                tabEncryption.RemoveEncryptionAsync,
                callbacks.CloseRightTabs,
                callbacks.CloseLeftTabs,
                callbacks.CloseOtherTabs,
                tabNavigation.GetTabViewForItem);

            var fileOpenDrop = new FileOpenDropController(
                ui.DragOverlay,
                ui.LeftSidebar,
                ui.PreviewGrid,
                callbacks.InitializePickerWindow,
                callbacks.LoadFileIntoTabAsync,
                callbacks.NavigateExplorerToFolderAsync,
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
                callbacks.TogglePreviewWidth,
                callbacks.ToggleMaximize);

            var terminalPanel = new TerminalPanelController(
                window,
                ui.EditorWorkspace,
                ui.TopToolbar,
                callbacks.GetSelectedExplorerItem,
                callbacks.GetCurrentFolderPath,
                callbacks.GetCurrentRepoPath,
                callbacks.LoadFileIntoTabAtLineAsync,
                callbacks.NavigateExplorerToFolderAndRevealAsync);

            var snippets = new SnippetsController(
                services.SnippetService,
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
