using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowPreviewCompositionCallbacks(
        Action FindRequested,
        Action SearchAllRequested,
        Action NewTabRequested,
        Action SaveRequested,
        Action SaveAsRequested,
        Action OpenRequested,
        Action ToggleExplorerTreeModeRequested,
        Action ToggleLivePreviewRequested,
        Action ToggleCsvTableModeRequested,
        Action ToggleThemeRequested,
        Action ToggleMaximizeRequested,
        Action PrintRequested,
        Action ToggleLeftPanelRequested,
        Action ToggleRightPanelRequested,
        Action ToggleTerminalRequested,
        Action ToggleWordWrapRequested,
        Action TogglePreviewWidthRequested,
        Action CloseActiveTabRequested,
        Func<string, Task> LoadFileAsync,
        Func<CoreWebView2WebMessageReceivedEventArgs, string> NormalizeWebMessageJson,
        Func<string> GetCurrentFolderPath,
        Func<string> GetCurrentRepoPath,
        Func<bool> IsScrollSyncEnabled,
        Action<string, OpenedTab, int, int> UpdateRightPanelSelectionContext,
        Func<string, Task> NavigateExplorerToFolderAndRevealAsync,
        Func<string, string, string> GetLocalizedString,
        Action? UpdateWindowTitle = null);

    internal sealed record MainWindowPreviewControllers(
        WebViewShortcutController WebViewShortcut,
        PreviewScrollSyncController PreviewScrollSync,
        CompareTabController CompareTab,
        LivePreviewController LivePreview,
        EditorWebViewInitializationController EditorWebViewInitialization,
        EditorLineNavigationController EditorLineNavigation,
        PdfViewerController PdfViewer,
        OfficeDocumentViewerController OfficeDocumentViewer,
        EditorLinkNavigationController EditorLinkNavigation,
        JupyterNotebookViewerController NotebookViewer);

    internal static class MainWindowPreviewComposition
    {
        public static MainWindowPreviewControllers Compose(
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            TabNavigationController tabNavigation,
            StickyNoteModeController stickyNoteMode,
            WindowDialogController dialog,
            Func<string, EditorDocumentSession?> getEditorSession,
            MainWindowPreviewCompositionCallbacks callbacks)
        {
            var webViewShortcut = new WebViewShortcutController(
                new PreviewWebViewShortcutCommands(callbacks, stickyNoteMode));

            var previewScrollSync = new PreviewScrollSyncController(
                ui.EditorWorkspace,
                tabBridges,
                tabNavigation.GetActiveTab,
                tabNavigation.GetOppositeTabView);

            var compareTab = new CompareTabController(
                documentServices.FileService,
                commonServices.SettingsService,
                callbacks.LoadFileAsync,
                viewModel,
                ui.EditorWorkspace,
                ui.EditorTabView,
                tabBridges,
                callbacks.GetLocalizedString,
                callbacks.NormalizeWebMessageJson,
                webViewShortcut.Handle);

            var livePreview = new LivePreviewController(
                ui.PreviewGrid,
                commonServices.SettingsService,
                tabBridges,
                tabNavigation.GetActiveTab,
                getEditorSession,
                callbacks.GetCurrentFolderPath,
                callbacks.GetCurrentRepoPath,
                callbacks.IsScrollSyncEnabled,
                callbacks.NormalizeWebMessageJson,
                webViewShortcut.Handle,
                previewScrollSync.SyncToEditors,
                dialog.ShowErrorMessage,
                callbacks.GetLocalizedString);

            var editorWebViewInitialization = new EditorWebViewInitializationController(
                commonServices.SettingsService,
                livePreview);

            var editorLineNavigation = new EditorLineNavigationController(
                viewModel,
                tabBridges);

            var pdfViewer = new PdfViewerController(
                commonServices.SettingsService,
                tabNavigation.GetActiveTab,
                callbacks.UpdateRightPanelSelectionContext,
                webViewShortcut.Handle,
                callbacks.GetLocalizedString);

            var officeDocumentViewer = new OfficeDocumentViewerController(
                commonServices.SettingsService,
                tabNavigation.GetActiveTab,
                webViewShortcut.Handle,
                callbacks.GetLocalizedString);

            var notebookKernelService = new JupyterNotebookKernelService(callbacks.GetLocalizedString);
            var notebookViewer = new JupyterNotebookViewerController(
                commonServices.SettingsService,
                tabNavigation.GetActiveTab,
                webViewShortcut.Handle,
                callbacks.GetLocalizedString,
                notebookKernelService,
                callbacks.UpdateWindowTitle);

            var editorLinkNavigation = new EditorLinkNavigationController(
                tabNavigation.GetActiveTab,
                callbacks.NavigateExplorerToFolderAndRevealAsync);

            return new MainWindowPreviewControllers(
                webViewShortcut,
                previewScrollSync,
                compareTab,
                livePreview,
                editorWebViewInitialization,
                editorLineNavigation,
                pdfViewer,
                officeDocumentViewer,
                editorLinkNavigation,
                notebookViewer);
        }

        private sealed class PreviewWebViewShortcutCommands : IWebViewShortcutCommands
        {
            private readonly MainWindowPreviewCompositionCallbacks _callbacks;
            private readonly StickyNoteModeController _stickyNoteMode;

            public PreviewWebViewShortcutCommands(
                MainWindowPreviewCompositionCallbacks callbacks,
                StickyNoteModeController stickyNoteMode)
            {
                _callbacks = callbacks;
                _stickyNoteMode = stickyNoteMode;
            }

            public void Find() => _callbacks.FindRequested();

            public void SearchAll() => _callbacks.SearchAllRequested();

            public void NewTab() => _callbacks.NewTabRequested();

            public void Save() => _callbacks.SaveRequested();

            public void SaveAs() => _callbacks.SaveAsRequested();

            public void Open() => _callbacks.OpenRequested();

            public void ToggleExplorerTreeMode() => _callbacks.ToggleExplorerTreeModeRequested();

            public void ToggleLivePreview() => _callbacks.ToggleLivePreviewRequested();

            public void ToggleCsvTableMode() => _callbacks.ToggleCsvTableModeRequested();

            public void ToggleTopMost() => _stickyNoteMode.ToggleTopMostFromShortcut();

            public void ToggleTheme() => _callbacks.ToggleThemeRequested();

            public void ToggleMaximize() => _callbacks.ToggleMaximizeRequested();

            public void ToggleStickyNote() => _stickyNoteMode.ToggleMode();

            public void Print() => _callbacks.PrintRequested();

            public void ToggleLeftPanel() => _callbacks.ToggleLeftPanelRequested();

            public void ToggleRightPanel() => _callbacks.ToggleRightPanelRequested();

            public void ToggleTerminal() => _callbacks.ToggleTerminalRequested();

            public void ToggleWordWrap() => _callbacks.ToggleWordWrapRequested();

            public void TogglePreviewWidth() => _callbacks.TogglePreviewWidthRequested();

            public void CloseActiveTab() => _callbacks.CloseActiveTabRequested();
        }
    }
}
