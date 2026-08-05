using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowDocumentCommandCallbacks(
        Action<OpenedTab> UpdateLanguageUi,
        Func<Task> RefreshGitStatusAsync,
        Action UpdateWindowTitle,
        Func<string> GetCurrentFolderPath,
        Action<string> LoadDirectoryRoot,
        Func<string> GetSearchRoot,
        Func<string> GetCurrentRepoPath,
        Func<OpenedTab> OpenNewTab,
        Action<string> CloseReadOnlyViewer,
        Func<Task> SaveUiLayoutSettingsAsync,
        Func<ElementTheme> GetCurrentElementTheme,
        Func<string, string, string> GetLocalizedString);

    internal sealed record MainWindowDocumentCommandControllers(
        TabSaveController TabSave,
        AutoSaveController AutoSave,
        TabCloseController TabClose,
        TabMoveController TabMove,
        WindowCloseController WindowClose);

    internal static class MainWindowDocumentCommandComposition
    {
        public static MainWindowDocumentCommandControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            StatusBarController statusBar,
            TabNavigationController tabNavigation,
            LivePreviewController livePreview,
            TabDirtyStateController tabDirtyState,
            TabEncryptionController tabEncryption,
            FavoritesRecentController favoritesRecent,
            WindowDialogController dialog,
            JupyterNotebookViewerController notebookViewer,
            MainWindowDocumentCommandCallbacks callbacks)
        {
            var tabSave = new TabSaveController(
                window,
                documentServices.FileService,
                documentServices.FileSaveDialogService,
                documentServices.SecureNoteEncryptionService,
                workspaceServices.RemoteWorkspaceService,
                commonServices.LanguageDetectionService,
                statusBar,
                tabNavigation.IsOpen,
                tabId => editorSessions.TryGetValue(tabId, out var session) ? session : null,
                tabId => tabBridges.TryGetValue(tabId, out var bridgeGroup) ? bridgeGroup : null,
                tabDirtyState.CleanDirtyStateOnOtherTabs,
                callbacks.UpdateLanguageUi,
                callbacks.RefreshGitStatusAsync,
                callbacks.UpdateWindowTitle,
                favoritesRecent.AddRecentFile,
                callbacks.GetCurrentFolderPath,
                callbacks.LoadDirectoryRoot,
                callbacks.GetLocalizedString,
                dialog.ShowErrorMessage,
                saveNotebookAsync: tab => notebookViewer.SaveAsync(tab));

            var autoSave = new AutoSaveController(
                viewModel,
                () => commonServices.SettingsService.CurrentSettings,
                callbacks.GetCurrentRepoPath,
                callbacks.GetSearchRoot,
                tabSave.SaveAsync);

            var tabClose = new TabCloseController(
                viewModel,
                ui.EditorWorkspace,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                editorSessions,
                livePreview,
                documentServices.UnsavedChangesDialogService,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                callbacks.GetLocalizedString,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                tabEncryption.ForgetPassword,
                tabSave.SaveAsync,
                callbacks.OpenNewTab,
                callbacks.CloseReadOnlyViewer,
                callbacks.UpdateWindowTitle);

            var tabMove = new TabMoveController(
                viewModel,
                tabNavigation.GetCurrentActiveTabView);

            var windowClose = new WindowCloseController(
                viewModel,
                documentServices.UnsavedChangesDialogService,
                callbacks.SaveUiLayoutSettingsAsync,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                callbacks.GetLocalizedString,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                tabSave.SaveAsync,
                window.Close);

            return new MainWindowDocumentCommandControllers(
                tabSave,
                autoSave,
                tabClose,
                tabMove,
                windowClose);
        }
    }
}
