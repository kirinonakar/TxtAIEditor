using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
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
        Action ToggleLivePreviewRequested,
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
        Func<string, string, string> GetLocalizedString);

    internal sealed record MainWindowPreviewControllers(
        WebViewShortcutController WebViewShortcut,
        PreviewScrollSyncController PreviewScrollSync,
        CompareTabController CompareTab,
        LivePreviewController LivePreview,
        EditorWebViewInitializationController EditorWebViewInitialization,
        EditorLineNavigationController EditorLineNavigation,
        PdfViewerController PdfViewer,
        OfficeDocumentViewerController OfficeDocumentViewer,
        EditorLinkNavigationController EditorLinkNavigation);

    internal static class MainWindowPreviewComposition
    {
        public static MainWindowPreviewControllers Compose(
            MainWindowUiRefs ui,
            MainWindowServices services,
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
                services.FileService,
                services.SettingsService,
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
                services.SettingsService,
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
                services.SettingsService,
                livePreview);

            var editorLineNavigation = new EditorLineNavigationController(
                viewModel,
                tabBridges);

            var pdfViewer = new PdfViewerController(
                services.SettingsService,
                tabNavigation.GetActiveTab,
                callbacks.UpdateRightPanelSelectionContext,
                webViewShortcut.Handle,
                callbacks.GetLocalizedString);

            var officeDocumentViewer = new OfficeDocumentViewerController(
                services.SettingsService,
                tabNavigation.GetActiveTab,
                webViewShortcut.Handle,
                callbacks.GetLocalizedString);

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
                editorLinkNavigation);
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

            public void ToggleLivePreview() => _callbacks.ToggleLivePreviewRequested();

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
