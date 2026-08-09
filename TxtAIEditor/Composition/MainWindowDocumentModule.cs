using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowDocumentModuleDependencies(
        MainWindowShellModule Shell,
        MainWindowEditorModule Editor,
        MainWindowPreviewModule Preview);

    internal sealed class MainWindowDocumentModule :
        ITabSaveCommands,
        ITabCloseCommands,
        IAutoSaveLifecycle
    {
        private readonly MainWindowDocumentCommandControllers _controllers;

        private MainWindowDocumentModule(MainWindowDocumentCommandControllers controllers)
        {
            _controllers = controllers;
        }

        public static MainWindowDocumentModule Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowDocumentModuleDependencies dependencies,
            MainWindowWorkspaceModule workspace,
            IMainWindowShellFacade shellFacade,
            IMainWindowEditorFacade editorFacade,
            IMainWindowDocumentFacade documentFacade,
            IMainWindowPreviewFacade previewFacade,
            Func<Task> saveUiLayoutSettingsAsync)
        {
            var shell = dependencies.Shell.Composition;
            var preview = dependencies.Preview.Composition;
            var callbacks = new MainWindowDocumentCommandCallbacks(
                editorFacade.UpdateLanguageUi,
                workspace.RefreshGitStatusUiAsync,
                shellFacade.UpdateWindowTitle,
                () => state.CurrentFolderPath,
                workspace.LoadDirectoryRoot,
                workspace.GetSearchRoot,
                () => state.CurrentRepoPath,
                documentFacade.OpenEmptyTab,
                previewFacade.CloseReadOnlyViewer,
                saveUiLayoutSettingsAsync,
                shellFacade.GetCurrentElementTheme,
                shellFacade.GetLocalizedString);

            var controllers = MainWindowDocumentCommandComposition.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                shell.StatusBar,
                shell.TabNavigation,
                preview.LivePreview,
                dependencies.Editor.Foundation.TabDirtyState,
                shell.TabEncryption,
                workspace.Composition.FavoritesRecent,
                shell.Dialog,
                preview.NotebookViewer,
                callbacks);

            return new MainWindowDocumentModule(controllers);
        }

        public Task<bool> SaveAsync(OpenedTab tab) =>
            _controllers.TabSave.SaveAsync(tab);

        public Task<bool> SaveAsAsync(OpenedTab tab) =>
            _controllers.TabSave.SaveAsAsync(tab);

        public void UpdateAutoSaveStatus() =>
            _controllers.AutoSave.UpdateStatus();

        public void Stop() =>
            _controllers.AutoSave.Stop();

        public void CloseRequested(TabViewTabCloseRequestedEventArgs args) =>
            _controllers.TabClose.CloseRequested(args);

        public void CloseActive(TabView activeTabView) =>
            _controllers.TabClose.CloseActive(activeTabView);

        public void CloseAndCleanup(OpenedTab tab, TabViewItem tabItem) =>
            _controllers.TabClose.CloseAndCleanup(tab, tabItem);

        public void CloseRightTabs(TabViewItem tabItem, TabView tabView) =>
            _controllers.TabClose.CloseRightTabs(tabItem, tabView);

        public void CloseLeftTabs(TabViewItem tabItem, TabView tabView) =>
            _controllers.TabClose.CloseLeftTabs(tabItem, tabView);

        public void CloseOtherTabs(TabViewItem tabItem, TabView tabView) =>
            _controllers.TabClose.CloseOtherTabs(tabItem, tabView);

        public void SetAdditionalTabCleanup(Action<string> cleanup) =>
            _controllers.TabClose.SetAdditionalTabCleanup(cleanup);

        public void MoveActiveTabLeft() =>
            _controllers.TabMove.MoveLeft();

        public void MoveActiveTabRight() =>
            _controllers.TabMove.MoveRight();

        public Task HandleWindowClosingAsync(
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args,
            bool saveUiLayoutSettings = true) =>
            _controllers.WindowClose.HandleClosingAsync(args, saveUiLayoutSettings);
    }
}
