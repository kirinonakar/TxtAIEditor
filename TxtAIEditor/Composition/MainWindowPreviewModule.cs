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
            Controllers = controllers;
        }

        public MainWindowPreviewControllers Controllers { get; }

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
            Controllers.LivePreview.Schedule(tab);

        public void Render(OpenedTab tab) =>
            Controllers.LivePreview.Render(tab);

        public string GetPreviewBaseHref(OpenedTab tab) =>
            Controllers.LivePreview.GetPreviewBaseHref(tab);

        public Task RevealFileLineAsync(string filePath, int lineNumber) =>
            Controllers.EditorLineNavigation.RevealFileLineAsync(filePath, lineNumber);

        public Task RevealTabLineAsync(string tabId, int lineNumber) =>
            Controllers.EditorLineNavigation.RevealTabLineAsync(tabId, lineNumber);

        public void RefreshActivePreview() =>
            Controllers.LivePreview.EnsureVisiblePreviewRendered();

        public void CloseReadOnlyViewer(string tabId)
        {
            Controllers.PdfViewer.Close(tabId);
            Controllers.OfficeDocumentViewer.Close(tabId);
            Controllers.NotebookViewer.Close(tabId);
        }
    }
}
