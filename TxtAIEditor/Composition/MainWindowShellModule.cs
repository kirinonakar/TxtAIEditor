using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed class MainWindowShellModule
    {
        private readonly Microsoft.UI.Windowing.AppWindow _appWindow;
        private readonly ISettingsService _settingsService;
        private readonly EditorWorkspacePane _editorWorkspace;
        private MainWindowInteractionControllers? _interactions;

        private MainWindowShellModule(
            MainWindowShellControllers controllers,
            Microsoft.UI.Windowing.AppWindow appWindow,
            ISettingsService settingsService,
            EditorWorkspacePane editorWorkspace)
        {
            Composition = controllers;
            _appWindow = appWindow;
            _settingsService = settingsService;
            _editorWorkspace = editorWorkspace;
        }

        internal MainWindowShellControllers Composition { get; }

        public static MainWindowShellModule Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowShellServices shellServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            IMainWindowShellFacade shellFacade,
            IMainWindowEditorFacade editorFacade,
            Action toggleTerminalRequested,
            Action<bool> applyLeftSidebarVisibility,
            Action<bool> applyPreviewVisibility)
        {
            var callbacks = new MainWindowShellCompositionCallbacks(
                shellPanelLayout => MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(
                    window.AppWindow,
                    commonServices.SettingsService,
                    ui.EditorWorkspace,
                    shellPanelLayout),
                toggleTerminalRequested,
                shellFacade.GetCurrentElementTheme,
                shellFacade.GetLocalizedString,
                shellFacade.UpdateWindowTitle,
                applyLeftSidebarVisibility,
                applyPreviewVisibility,
                editorFacade.ReloadTabWithEncodingAsync,
                editorFacade.MarkTabDirtyFromStatusBar,
                editorFacade.PerformLineNavigationAsync,
                editorFacade.UpdateLivePreview);

            var controllers = MainWindowShellComposition.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                shellServices,
                viewModel,
                state.TabBridges,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                callbacks);

            return new MainWindowShellModule(
                controllers,
                window.AppWindow,
                commonServices.SettingsService,
                ui.EditorWorkspace);
        }

        public void BindInteractions(MainWindowInteractionControllers interactions)
        {
            if (_interactions != null)
            {
                throw new System.InvalidOperationException("Shell interactions have already been bound.");
            }

            _interactions = interactions;
        }

        public Task SaveSidebarVisibilitySettingsAsync() =>
            MainWindowLayoutOperations.SaveSidebarVisibilitySettingsAsync(
                _settingsService,
                Composition.ShellPanelLayout);

        public Task SaveUiLayoutSettingsAsync() =>
            MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(
                _appWindow,
                _settingsService,
                _editorWorkspace,
                Composition.ShellPanelLayout);

        public void ApplySavedPanelWidths(EditorSettings settings) =>
            Composition.ShellPanelLayout.ApplySavedPanelWidths(
                settings.LeftSidebarWidth,
                settings.RightSidebarWidth);

        public void TogglePreviewWidth() =>
            Composition.ShellPanelLayout.TogglePreviewWidth();

        public void ToggleTopMostFromShortcut() =>
            Composition.StickyNoteMode.ToggleTopMostFromShortcut();

        public void ToggleStickyNoteMode() =>
            Composition.StickyNoteMode.ToggleMode();

        public void ShowErrorMessage(string title, string message) =>
            Composition.Dialog.ShowErrorMessage(title, message);

        public void UpdateSelectionStats(string? selectedText) =>
            Composition.StatusBar.UpdateSelectionStats(selectedText);

        public void UpdateReadOnlyTabStatus(OpenedTab tab)
        {
            Composition.StatusBar.UpdateFileStats(tab);
            Composition.StatusBar.UpdateTotalLines(tab);
            Composition.StatusBar.SyncLineEndingText(tab);
        }

        public void UpdateWindowTitle() =>
            Composition.WindowTitle.Update();

        public TabView GetCurrentActiveTabView() =>
            Composition.TabNavigation.GetCurrentActiveTabView();

        public TabView? GetTabView(OpenedTab tab) =>
            Composition.TabNavigation.GetTabView(tab);

        public void UpdateLanguage(OpenedTab tab) =>
            Composition.StatusBar.UpdateLanguage(tab);

        public Task ReloadTabAsync(
            OpenedTab tab,
            TabViewItem tabItem,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            JupyterNotebookViewerController notebookViewer,
            MainWindowEditorModule editor,
            System.Action<OpenedTab> updateLanguageUi,
            System.Action updateWindowTitle) =>
            MainWindowTabOperations.ReloadAsync(
                tab,
                tabItem,
                Composition.StatusBar,
                pdfViewer,
                officeDocumentViewer,
                notebookViewer,
                editor.Foundation.TabReload,
                updateLanguageUi,
                updateWindowTitle);
    }
}
