using System;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowPreviewModuleDependencies(
        MainWindowShellModule Shell);

    internal sealed class MainWindowPreviewModule
    {
        private MainWindowPreviewModule(MainWindowPreviewControllers controllers)
        {
            Composition = controllers;
        }

        internal MainWindowPreviewControllers Composition { get; }

        public static MainWindowPreviewModule Compose(
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowPreviewModuleDependencies dependencies,
            IMainWindowShellFacade shellFacade,
            IMainWindowEditorFacade editorFacade,
            IMainWindowDocumentFacade documentFacade,
            Func<MainWindowToolbarCommandController?> getToolbarCommand,
            Func<Task> toggleLeftPanelAsync,
            Func<Task> toggleRightPanelAsync,
            Action toggleExplorerTreeMode,
            Func<string, Task> navigateExplorerToFolderAndRevealAsync)
        {
            var shell = dependencies.Shell.Composition;
            var controllers = MainWindowPreviewComposition.Compose(
                ui,
                commonServices,
                documentServices,
                viewModel,
                state.TabBridges,
                shell.TabNavigation,
                shell.StickyNoteMode,
                shell.Dialog,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowPreviewCompositionCallbacks(
                    () => getToolbarCommand()?.Find(),
                    shellFacade.FocusSearchPanel,
                    () => documentFacade.OpenEmptyTab(),
                    () => getToolbarCommand()?.SaveActive(),
                    () => getToolbarCommand()?.SaveActiveAs(),
                    () => getToolbarCommand()?.OpenFile(),
                    toggleExplorerTreeMode,
                    () => getToolbarCommand()?.ToggleLivePreview(),
                    () => getToolbarCommand()?.ToggleCsvTableMode(),
                    () => getToolbarCommand()?.ToggleTheme(),
                    shellFacade.ToggleMaximize,
                    () => getToolbarCommand()?.Print(),
                    () => _ = toggleLeftPanelAsync(),
                    () => _ = toggleRightPanelAsync(),
                    () => getToolbarCommand()?.ToggleTerminal(),
                    () => getToolbarCommand()?.ToggleWordWrap(),
                    shell.ShellPanelLayout.TogglePreviewWidth,
                    documentFacade.CloseActiveTab,
                    documentFacade.MoveActiveTabLeft,
                    documentFacade.MoveActiveTabRight,
                    documentFacade.LoadFileIntoTabAsync,
                    MainWindowMessageJson.Normalize,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    () => state.ScrollSyncEnabled,
                    editorFacade.UpdateRightPanelSelectionContext,
                    navigateExplorerToFolderAndRevealAsync,
                    shellFacade.GetLocalizedString,
                    shellFacade.UpdateWindowTitle));

            return new MainWindowPreviewModule(controllers);
        }

        public void Schedule(OpenedTab tab) =>
            Composition.LivePreview.Schedule(tab);

        public void Render(OpenedTab tab) =>
            Composition.LivePreview.Render(tab);

        public string GetPreviewBaseHref(OpenedTab tab) =>
            Composition.LivePreview.GetPreviewBaseHref(tab);

        public Task RevealFileLineAsync(string filePath, int lineNumber) =>
            Composition.EditorLineNavigation.RevealFileLineAsync(filePath, lineNumber);

        public Task RevealTabLineAsync(string tabId, int lineNumber) =>
            Composition.EditorLineNavigation.RevealTabLineAsync(tabId, lineNumber);

        public void RefreshActivePreview() =>
            Composition.LivePreview.EnsureVisiblePreviewRendered();

        public Task OpenFileInExternalViewerAsync(string filePath) =>
            Composition.LivePreview.OpenFileInExternalViewerAsync(filePath);

        public Task OpenFileWithDefaultProgramAsync(string filePath) =>
            Composition.LivePreview.OpenFileWithDefaultProgramAsync(filePath);

        public void CloseReadOnlyViewer(string tabId)
        {
            Composition.PdfViewer.Close(tabId);
            Composition.OfficeDocumentViewer.Close(tabId);
            Composition.NotebookViewer.Close(tabId);
        }
    }
}
