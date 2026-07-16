using System;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowPreviewModuleDependencies(
        MainWindowShellControllers Shell);

    internal sealed class MainWindowPreviewModule
    {
        private MainWindowPreviewModule(MainWindowPreviewControllers controllers)
        {
            Controllers = controllers;
        }

        public MainWindowPreviewControllers Controllers { get; }

        public static MainWindowPreviewModule Compose(
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowPreviewModuleDependencies dependencies,
            MainWindowCompositionRootCallbacks callbacks,
            Func<MainWindowToolbarCommandController?> getToolbarCommand,
            Func<string, Task> navigateExplorerToFolderAndRevealAsync)
        {
            var shell = dependencies.Shell;
            var controllers = MainWindowPreviewComposition.Compose(
                ui,
                services,
                viewModel,
                state.TabBridges,
                shell.TabNavigation,
                shell.StickyNoteMode,
                shell.Dialog,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowPreviewCompositionCallbacks(
                    () => getToolbarCommand()?.Find(),
                    () => getToolbarCommand()?.ToggleLivePreview(),
                    () => getToolbarCommand()?.ToggleTheme(),
                    callbacks.ToggleMaximize,
                    () => getToolbarCommand()?.Print(),
                    shell.ShellPanelLayout.TogglePreviewWidth,
                    callbacks.CloseActiveTab,
                    callbacks.LoadFileIntoTabAsync,
                    MainWindowMessageJson.Normalize,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    () => state.ScrollSyncEnabled,
                    callbacks.UpdateRightPanelSelectionContext,
                    navigateExplorerToFolderAndRevealAsync,
                    callbacks.GetLocalizedString));

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
        }
    }
}
