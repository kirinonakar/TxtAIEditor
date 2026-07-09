using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowEditorFoundationCallbacks(
        Action ToggleLivePreviewRequested,
        Action ToggleThemeRequested,
        Action ToggleMaximizeRequested,
        Func<Task> ToggleLeftPanelAsync,
        Func<Task> ToggleRightPanelAsync,
        Action TogglePreviewWidthRequested,
        Action OpenNewTabRequested,
        Action SaveActiveRequested,
        Action OpenFileRequested,
        Action CloseActiveTabRequested,
        Action PrintRequested,
        Action FocusSearchPanelRequested,
        Action<OpenedTab> UpdateLivePreview,
        Action<OpenedTab> UpdateLanguageUi,
        Action<OpenedTab> SchedulePreview,
        Action UpdateWindowTitle,
        Func<OpenedTab, Task> SyncEditsToOtherTabsAsync,
        Func<string, Task> LoadFileAsync,
        Func<string> GetSearchRoot,
        Func<long> GetLargeFileThresholdBytes,
        Func<Task> RefreshGitStatusAsync,
        Func<string, string, string> GetLocalizedString);

    internal sealed record MainWindowEditorFoundationControllers(
        TabReloadController TabReload,
        TabDirtyStateController TabDirtyState,
        ActiveEditorInsertionController ActiveEditorInsertion,
        TabTextContextProvider TabTextContext,
        EditorBridgeShortcutController EditorBridgeShortcut,
        SearchReplaceTabSyncController SearchReplaceTabSync,
        SearchReplaceController SearchReplace,
        SplitImeSyncController SplitImeSync);

    internal static class MainWindowEditorFoundationComposition
    {
        public static MainWindowEditorFoundationControllers Compose(
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabNavigationController tabNavigation,
            TabEncryptionController tabEncryption,
            StickyNoteModeController stickyNoteMode,
            StatusBarController statusBar,
            WindowDialogController dialog,
            TerminalShortcutService terminalShortcut,
            EditorLineNavigationController editorLineNavigation,
            int initialEditorLineWarmupCount,
            Func<string, EditorDocumentSession?> getEditorSession,
            MainWindowEditorFoundationCallbacks callbacks)
        {
            var tabReload = new TabReloadController(
                services.SecureNoteEncryptionService,
                services.ArchiveExplorerService,
                services.SettingsService,
                tabBridges,
                editorSessions,
                statusBar,
                initialEditorLineWarmupCount,
                tabEncryption.PromptPasswordAsync,
                callbacks.GetLocalizedString,
                callbacks.UpdateLivePreview,
                callbacks.UpdateLanguageUi,
                callbacks.SchedulePreview,
                callbacks.UpdateWindowTitle,
                dialog.ShowErrorMessage);

            var tabDirtyState = new TabDirtyStateController(
                viewModel,
                tabBridges,
                editorSessions,
                callbacks.UpdateWindowTitle);

            var activeEditorInsertion = new ActiveEditorInsertionController(
                viewModel,
                tabBridges,
                tabNavigation.GetCurrentActiveTabView,
                tabDirtyState);

            var tabTextContext = new TabTextContextProvider(
                services.PdfTextExtractionService,
                getEditorSession);

            var editorBridgeShortcut = new EditorBridgeShortcutController(
                callbacks.ToggleLivePreviewRequested,
                stickyNoteMode.ToggleTopMostFromShortcut,
                callbacks.ToggleThemeRequested,
                callbacks.ToggleMaximizeRequested,
                stickyNoteMode.ToggleMode,
                callbacks.ToggleLeftPanelAsync,
                callbacks.ToggleRightPanelAsync,
                callbacks.TogglePreviewWidthRequested,
                callbacks.OpenNewTabRequested,
                callbacks.SaveActiveRequested,
                callbacks.OpenFileRequested,
                terminalShortcut.RequestToggle,
                callbacks.CloseActiveTabRequested,
                callbacks.PrintRequested,
                callbacks.FocusSearchPanelRequested,
                tabDirtyState,
                callbacks.SchedulePreview,
                callbacks.SyncEditsToOtherTabsAsync);

            var searchReplaceTabSync = new SearchReplaceTabSyncController(
                viewModel,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                editorSessions,
                tabDirtyState,
                tabNavigation.GetActiveTab,
                callbacks.LoadFileAsync,
                callbacks.UpdateLivePreview,
                editorLineNavigation);

            var searchReplace = new SearchReplaceController(
                services.FileSearchService,
                viewModel,
                ui.LeftSidebar.SearchQuery,
                ui.LeftSidebar.ReplaceQuery,
                ui.LeftSidebar.SearchMatchCase,
                ui.LeftSidebar.SearchWholeWord,
                ui.LeftSidebar.SearchRegex,
                ui.LeftSidebar.SearchResults,
                ui.LeftSidebar.SearchHeaderLabel,
                ui.LeftSidebar.SearchProgressIndicator,
                callbacks.GetSearchRoot,
                callbacks.GetLargeFileThresholdBytes,
                () => ui.RootElement.XamlRoot,
                dialog.ShowErrorMessage,
                searchReplaceTabSync.LoadAndHighlightAsync,
                callbacks.RefreshGitStatusAsync,
                getString: callbacks.GetLocalizedString,
                beforeDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.ResumeNativeWindows(); });
            searchReplace.FileModified += searchReplaceTabSync.HandleFileModifiedAsync;

            var splitImeSync = new SplitImeSyncController(
                tabBridges,
                editorSessions,
                tabDirtyState.GetTabsForSameFile,
                callbacks.SchedulePreview,
                tabDirtyState.SetDirtyStateForFileGroup);

            return new MainWindowEditorFoundationControllers(
                tabReload,
                tabDirtyState,
                activeEditorInsertion,
                tabTextContext,
                editorBridgeShortcut,
                searchReplaceTabSync,
                searchReplace,
                splitImeSync);
        }
    }
}
