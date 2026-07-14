using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowEditorRuntimeCallbacks(
        Action<OpenedTab> SchedulePreview,
        Action<OpenedTab> UpdateLanguageUi,
        Func<OpenedTab, int, string, bool> QueuePendingSplitImeLineSyncIfNeeded,
        Func<OpenedTab, int, string, bool> SchedulePendingSplitImeCompletionSyncIfNeeded,
        Func<OpenedTab, bool> ScheduleDeferredPendingSplitImeSyncIfNeeded,
        Func<OpenedTab, int, string, bool, Task> SyncLineChangeToOtherTabsAsync,
        Func<OpenedTab, Task> SyncEditsToOtherTabsAsync,
        Action<OpenedTab> RecordPendingSplitImeStructuralEdit,
        Func<OpenedTab, bool> HasOtherTabForSameFile,
        Func<OpenedTab, Task> FlushOtherTabsPendingSyncsAsync,
        Func<Task> SaveSidebarVisibilitySettingsAsync,
        Action RefreshActivePreview,
        Func<string, Task> LoadFileIntoTabAsync,
        Action<string, OpenedTab, int, int> UpdateRightPanelSelectionContext,
        Func<bool> IsScrollSyncEnabled,
        Action<bool> SetScrollSyncEnabled,
        Func<string> GetCurrentFolderPath,
        Func<bool> IsLivePreviewEnabled,
        Action<OpenedTab> SyncCsvTableModeUi,
        Func<ElementTheme> GetCurrentElementTheme,
        Func<OpenedTab, Task<bool>> SaveTabAsync,
        Func<OpenedTab, string> GetPreviewBaseHref,
        Func<string, string, string> GetLocalizedString,
        Action<EditorSettings> ApplyEditorSurfaceBackground,
        Action UpdateWindowTitle,
        Func<OpenedTab> OpenBlankTab,
        EditorSplitLayoutController.OpenEditorTabCallback OpenEditorTab,
        Action<OpenedTab, TabViewItem> CloseTabAndCleanup,
        Action<TabView, TabViewTabCloseRequestedEventArgs> CloseTabRequested);

    internal sealed record MainWindowEditorRuntimeControllers(
        TocController Toc,
        EditorBridgeDocumentController EditorBridgeDocument,
        ShellPaneController ShellPane,
        MarkdownToolbarController MarkdownToolbar,
        TabSelectionController TabSelection,
        EditorBridgeInteractionController EditorBridgeInteraction,
        EditorTabOpenController EditorTabOpen,
        EditorSplitLayoutController EditorSplitLayout);

    internal static class MainWindowEditorRuntimeComposition
    {
        public static MainWindowEditorRuntimeControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            StatusBarController statusBar,
            TabNavigationController tabNavigation,
            TabDirtyStateController tabDirtyState,
            TabEncryptionController tabEncryption,
            LivePreviewController livePreview,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            WebViewShortcutController webViewShortcut,
            EditorWebViewInitializationController editorWebViewInitialization,
            EditorLineNavigationController editorLineNavigation,
            EditorBridgeShortcutController editorBridgeShortcut,
            EditorLinkNavigationController editorLinkNavigation,
            ActiveEditorInsertionController activeEditorInsertion,
            TabContextMenuController tabContextMenu,
            FavoritesRecentController favoritesRecent,
            LlmAssistantController llmAssistant,
            AgentController agent,
            WindowDialogController dialog,
            ShellPanelLayoutService shellPanelLayout,
            int initialEditorLineWarmupCount,
            MainWindowEditorRuntimeCallbacks callbacks)
        {
            var toc = new TocController(
                viewModel,
                ui.LeftSidebar,
                tabNavigation.GetActiveTab,
                tab => editorSessions.TryGetValue(tab.Id, out var session) ? session : null,
                () => ui.PreviewGrid.PreviewMode.SelectedIndex == 3,
                async targetLine =>
                {
                    var activeTab = tabNavigation.GetActiveTab();
                    if (activeTab != null)
                    {
                        await editorLineNavigation.RevealTabLineAsync(activeTab.Id, targetLine);
                    }
                },
                async targetPage =>
                {
                    var activeTab = tabNavigation.GetActiveTab();
                    if (activeTab != null)
                    {
                        await pdfViewer.NavigateToPageAsync(activeTab, targetPage);
                    }
                });

            var editorBridgeDocument = new EditorBridgeDocumentController(
                tabDirtyState,
                statusBar,
                toc,
                callbacks.SchedulePreview,
                callbacks.UpdateLanguageUi,
                callbacks.QueuePendingSplitImeLineSyncIfNeeded,
                callbacks.SchedulePendingSplitImeCompletionSyncIfNeeded,
                tab => callbacks.ScheduleDeferredPendingSplitImeSyncIfNeeded(tab),
                callbacks.SyncLineChangeToOtherTabsAsync,
                tab => callbacks.SyncEditsToOtherTabsAsync(tab),
                callbacks.RecordPendingSplitImeStructuralEdit,
                callbacks.HasOtherTabForSameFile,
                callbacks.FlushOtherTabsPendingSyncsAsync);

            var shellPane = new ShellPaneController(
                ui.LeftSidebar,
                ui.StatusBar,
                shellPanelLayout,
                ui.LeftSidebar.SearchQuery,
                callbacks.SaveSidebarVisibilitySettingsAsync,
                () => favoritesRecent.RefreshFavorites(true),
                () => toc.RefreshToc(tabNavigation.GetActiveTab()),
                callbacks.RefreshActivePreview);

            var markdownToolbar = new MarkdownToolbarController(
                ui.TopToolbar,
                ui.MarkdownToolbar,
                ui.EditorTabView,
                tabBridges,
                callbacks.LoadFileIntoTabAsync,
                activeEditorInsertion.InsertTextAsync,
                dialog.ShowErrorMessage,
                callbacks.GetLocalizedString);

            var tabSelection = new TabSelectionController(
                ui.EditorWorkspace,
                viewModel,
                ui.EditorTabView,
                tabBridges,
                window.DispatcherQueue,
                llmAssistant,
                agent,
                ui.PreviewGrid.SelectionStats,
                statusBar,
                callbacks.GetLocalizedString,
                livePreview.Render,
                callbacks.UpdateLanguageUi,
                callbacks.SyncCsvTableModeUi,
                toc,
                callbacks.UpdateWindowTitle);

            var editorBridgeInteraction = new EditorBridgeInteractionController(
                ui.EditorWorkspace,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                window.DispatcherQueue,
                tabNavigation.GetActiveTab,
                statusBar,
                tabSelection,
                livePreview,
                callbacks.UpdateRightPanelSelectionContext,
                callbacks.IsScrollSyncEnabled,
                callbacks.SetScrollSyncEnabled);

            var documentFactory = new EditorTabDocumentFactory(services.LanguageDetectionService, callbacks.GetLocalizedString);
            var itemFactory = new EditorTabViewItemFactory(services.LocalizationService, webViewShortcut.Handle);

            var editorTabOpen = new EditorTabOpenController(
                services.SettingsService,
                services.SnippetService,
                viewModel,
                ui.EditorWorkspace,
                documentFactory,
                itemFactory,
                favoritesRecent,
                statusBar,
                services.UnsavedChangesDialogService,
                tabEncryption,
                pdfViewer,
                officeDocumentViewer,
                editorWebViewInitialization,
                editorBridgeShortcut,
                editorBridgeDocument,
                editorBridgeInteraction,
                editorLinkNavigation,
                tabSelection,
                tabBridges,
                editorSessions,
                window.DispatcherQueue,
                () => ui.RootElement.XamlRoot,
                tabNavigation.GetCurrentActiveTabView,
                tabNavigation.GetActiveTab,
                tabNavigation.GetTabViewForItem,
                callbacks.GetCurrentFolderPath,
                callbacks.IsLivePreviewEnabled,
                callbacks.IsScrollSyncEnabled,
                callbacks.GetCurrentElementTheme,
                callbacks.SaveTabAsync,
                callbacks.GetPreviewBaseHref,
                callbacks.GetLocalizedString,
                callbacks.ApplyEditorSurfaceBackground,
                callbacks.UpdateLanguageUi,
                callbacks.UpdateWindowTitle,
                tabContextMenu.ShowContextMenu,
                initialEditorLineWarmupCount);

            var editorSplitLayout = new EditorSplitLayoutController(
                ui.TopToolbar,
                ui.EditorWorkspace,
                viewModel,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                editorSessions,
                tabNavigation.GetActiveTab,
                callbacks.OpenBlankTab,
                callbacks.OpenEditorTab,
                tabDirtyState.IsAnySameFileTabDirty,
                tabDirtyState.SetDirtyStateForFileGroup,
                callbacks.SyncEditsToOtherTabsAsync,
                callbacks.CloseTabAndCleanup,
                callbacks.CloseTabRequested,
                tabSelection.QueueChanged,
                tabSelection.ClearQueue,
                callbacks.UpdateWindowTitle);

            return new MainWindowEditorRuntimeControllers(
                toc,
                editorBridgeDocument,
                shellPane,
                markdownToolbar,
                tabSelection,
                editorBridgeInteraction,
                editorTabOpen,
                editorSplitLayout);
        }
    }
}
