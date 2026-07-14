using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowCompositionRoot
    {
        public static MainWindowControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            MainWindowState state,
            int initialEditorLineWarmupCount,
            MainWindowCompositionRootCallbacks callbacks)
        {
            ShellPanelLayoutService? shellPanelLayoutService = null;
            TerminalShortcutService? terminalShortcutService = null;
            FunctionKeyShortcutService? functionKeyShortcutService = null;
            SearchReplaceController? searchReplaceController = null;
            SearchReplaceTabSyncController? searchReplaceTabSyncController = null;
            GitPanelController? gitPanelController = null;
            GitStatusRefreshController? gitStatusRefreshController = null;
            FavoritesRecentController? favoritesRecentController = null;
            TabContextMenuController? tabContextMenuController = null;
            TabNavigationController? tabNavigationController = null;
            TabEncryptionController? tabEncryptionController = null;
            FileOpenDropController? fileOpenDropController = null;
            RootKeyboardShortcutController? rootKeyboardShortcutController = null;
            SnippetsController? snippetsController = null;
            LlmAssistantController? llmAssistantController = null;
            AgentController? agentController = null;
            ShellPaneController? shellPaneController = null;
            StickyNoteModeController? stickyNoteModeController = null;
            StatusBarController? statusBarController = null;
            TabReloadController? tabReloadController = null;
            CompareTabController? compareTabController = null;
            LivePreviewController? livePreviewController = null;
            PdfViewerController? pdfViewerController = null;
            OfficeDocumentViewerController? officeDocumentViewerController = null;
            EditorBridgeShortcutController? editorBridgeShortcutController = null;
            EditorLinkNavigationController? editorLinkNavigationController = null;
            EditorWebViewInitializationController? editorWebViewInitializationController = null;
            EditorLineNavigationController? editorLineNavigationController = null;
            ActiveEditorInsertionController? activeEditorInsertionController = null;
            TabTextContextProvider? tabTextContextProvider = null;
            SplitImeSyncController? splitImeSyncController = null;
            TabDirtyStateController? tabDirtyStateController = null;
            TabSaveController? tabSaveController = null;
            TabCloseController? tabCloseController = null;
            TabMoveController? tabMoveController = null;
            AutoSaveController? autoSaveController = null;
            TerminalPanelController? terminalPanelController = null;
            ExplorerNavigationController? explorerNavigationController = null;
            WindowDialogController? dialogController = null;
            MainWindowSettingsController? settingsController = null;
            MainWindowToolbarCommandController? toolbarCommandController = null;
            DispatcherTimer? gitAutoRefreshTimer = null;

            Task SaveUiLayoutSettingsAsync() =>
                MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(
                    window.AppWindow,
                    services.SettingsService,
                    ui.EditorWorkspace,
                    shellPanelLayoutService!);

            Task SaveSidebarVisibilitySettingsAsync() =>
                MainWindowLayoutOperations.SaveSidebarVisibilitySettingsAsync(services.SettingsService, shellPanelLayoutService!);

            void ApplyLeftSidebarVisibility(bool show) =>
                shellPaneController!.ApplyLeftSidebarVisibility(show);

            void ApplyPreviewVisibility(bool show) =>
                MainWindowLayoutOperations.ApplyPreviewVisibility(
                    show,
                    shellPaneController!,
                    callbacks.IsStartupInitializationComplete(),
                    livePreviewController!);

            void ApplySavedPanelWidths(EditorSettings settings) =>
                shellPanelLayoutService!.ApplySavedPanelWidths(settings.LeftSidebarWidth, settings.RightSidebarWidth);

            Task ToggleLeftPanelAsync() =>
                shellPaneController!.ToggleLeftPanelAsync();

            Task ToggleRightPanelAsync() =>
                shellPaneController!.ToggleRightPanelAsync();

            void TogglePreviewWidth() =>
                shellPanelLayoutService!.TogglePreviewWidth();

            void LoadDirectoryRoot(string folderPath) =>
                explorerNavigationController!.LoadDirectoryRoot(folderPath);

            Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true) =>
                explorerNavigationController!.NavigateToFolderAsync(folderPath, revealInLeftPanel);

            Task NavigateExplorerToFolderAndRevealAsync(string folderPath) =>
                explorerNavigationController!.NavigateToFolderAsync(folderPath, revealInLeftPanel: true);

            Task RefreshGitStatusUIAsync() =>
                MainWindowWorkspaceOperations.RefreshGitStatusUiAsync(
                    state,
                    services.GitService,
                    gitAutoRefreshTimer!,
                    tabNavigationController!,
                    gitStatusRefreshController!,
                    explorerNavigationController!,
                    callbacks.SetCurrentRepoPath);

            void QueueGitStatusRefresh() =>
                gitStatusRefreshController!.QueueRefresh();

            string GetCurrentRepoPathForGitRefresh() =>
                MainWindowWorkspaceOperations.GetCurrentRepoPathForGitRefresh(
                    state,
                    services.GitService,
                    tabNavigationController!,
                    callbacks.SetCurrentRepoPath);

            string GetSearchRoot() =>
                MainWindowWorkspaceOperations.GetSearchRoot(state);

            long GetLargeFileThresholdBytes() =>
                MainWindowWorkspaceOperations.GetLargeFileThresholdBytes(services.SettingsService);

            bool QueuePendingSplitImeLineSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text) =>
                splitImeSyncController!.QueuePendingLineSyncIfNeeded(sourceTab, lineNumber, text);

            bool SchedulePendingSplitImeCompletionSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text) =>
                splitImeSyncController!.ScheduleCompletionSyncIfNeeded(sourceTab, lineNumber, text);

            bool ScheduleDeferredPendingSplitImeSyncIfNeeded(OpenedTab sourceTab) =>
                splitImeSyncController!.ScheduleDeferredSyncIfNeeded(sourceTab);

            Task FlushPendingSplitImeSyncAsync(OpenedTab sourceTab) =>
                splitImeSyncController!.FlushAsync(sourceTab);

            void ClearPendingSplitImeSync(string tabId) =>
                splitImeSyncController!.Clear(tabId);

            Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing) =>
                splitImeSyncController!.SyncLineChangeToOtherTabsAsync(sourceTab, lineNumber, text, isComposing);

            Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true) =>
                splitImeSyncController!.SyncEditsToOtherTabsAsync(sourceTab, updateUi);

            void RecordPendingSplitImeStructuralEdit(OpenedTab sourceTab) =>
                splitImeSyncController!.RecordStructuralEdit(sourceTab);

            bool HasOtherTabForSameFile(OpenedTab sourceTab) =>
                splitImeSyncController!.HasOtherTabForSameFile(sourceTab);

            Task FlushOtherTabsPendingSyncsAsync(OpenedTab sourceTab) =>
                splitImeSyncController!.FlushOtherTabsPendingSyncsAsync(sourceTab);

            void ApplyUiPersonalization(EditorSettings settings) =>
                settingsController!.ApplyUiPersonalization(settings);

            void ApplyToolbarSettings(EditorSettings settings) =>
                settingsController!.ApplyToolbarSettings(settings);

            void ApplyEditorSurfaceBackground(EditorSettings settings) =>
                settingsController!.ApplyEditorSurfaceBackground(settings);

            var shellControllers = MainWindowShellComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowShellCompositionCallbacks(
                    SaveUiLayoutSettingsAsync,
                    () => toolbarCommandController?.ToggleTerminal(),
                    callbacks.GetCurrentElementTheme,
                    callbacks.GetLocalizedString,
                    callbacks.UpdateWindowTitle,
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    callbacks.ReloadTabWithEncodingAsync,
                    callbacks.MarkTabDirtyFromStatusBar,
                    callbacks.PerformLineNavigationAsync,
                    callbacks.UpdateLivePreview));
            shellPanelLayoutService = shellControllers.ShellPanelLayout;
            tabNavigationController = shellControllers.TabNavigation;
            terminalShortcutService = shellControllers.TerminalShortcut;
            dialogController = shellControllers.Dialog;
            tabEncryptionController = shellControllers.TabEncryption;
            stickyNoteModeController = shellControllers.StickyNoteMode;
            statusBarController = shellControllers.StatusBar;

            var previewControllers = MainWindowPreviewComposition.Compose(
                ui,
                services,
                viewModel,
                state.TabBridges,
                tabNavigationController,
                stickyNoteModeController,
                dialogController,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowPreviewCompositionCallbacks(
                    () => toolbarCommandController?.Find(),
                    () => toolbarCommandController?.ToggleLivePreview(),
                    () => toolbarCommandController?.ToggleTheme(),
                    callbacks.ToggleMaximize,
                    () => toolbarCommandController?.Print(),
                    TogglePreviewWidth,
                    callbacks.CloseActiveTab,
                    callbacks.LoadFileIntoTabAsync,
                    MainWindowMessageJson.Normalize,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    () => state.ScrollSyncEnabled,
                    callbacks.UpdateRightPanelSelectionContext,
                    NavigateExplorerToFolderAndRevealAsync,
                    callbacks.GetLocalizedString));
            compareTabController = previewControllers.CompareTab;
            livePreviewController = previewControllers.LivePreview;
            editorWebViewInitializationController = previewControllers.EditorWebViewInitialization;
            editorLineNavigationController = previewControllers.EditorLineNavigation;
            pdfViewerController = previewControllers.PdfViewer;
            officeDocumentViewerController = previewControllers.OfficeDocumentViewer;
            editorLinkNavigationController = previewControllers.EditorLinkNavigation;

            var editorFoundationControllers = MainWindowEditorFoundationComposition.Compose(
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                tabNavigationController,
                tabEncryptionController,
                stickyNoteModeController,
                statusBarController,
                dialogController,
                terminalShortcutService,
                editorLineNavigationController,
                initialEditorLineWarmupCount,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowEditorFoundationCallbacks(
                    () => toolbarCommandController?.ToggleLivePreview(),
                    () => toolbarCommandController?.ToggleTheme(),
                    callbacks.ToggleMaximize,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    TogglePreviewWidth,
                    () => callbacks.OpenNewTab(),
                    () => toolbarCommandController?.SaveActive(),
                    () => toolbarCommandController?.OpenFile(),
                    callbacks.CloseActiveTab,
                    () => toolbarCommandController?.Print(),
                    callbacks.FocusSearchPanel,
                    callbacks.UpdateLivePreview,
                    callbacks.UpdateLanguageUi,
                    callbacks.SchedulePreview,
                    callbacks.UpdateWindowTitle,
                    tab => SyncEditsToOtherTabsAsync(tab),
                    callbacks.LoadFileIntoTabAsync,
                    GetSearchRoot,
                    GetLargeFileThresholdBytes,
                    RefreshGitStatusUIAsync,
                    callbacks.GetLocalizedString));
            tabReloadController = editorFoundationControllers.TabReload;
            tabDirtyStateController = editorFoundationControllers.TabDirtyState;
            activeEditorInsertionController = editorFoundationControllers.ActiveEditorInsertion;
            tabTextContextProvider = editorFoundationControllers.TabTextContext;
            editorBridgeShortcutController = editorFoundationControllers.EditorBridgeShortcut;
            searchReplaceTabSyncController = editorFoundationControllers.SearchReplaceTabSync;
            searchReplaceController = editorFoundationControllers.SearchReplace;
            splitImeSyncController = editorFoundationControllers.SplitImeSync;

            var workspaceControllers = MainWindowWorkspaceComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                tabEncryptionController,
                compareTabController,
                dialogController,
                new MainWindowWorkspaceCompositionCallbacks(
                    stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => toolbarCommandController?.ToggleTheme(),
                    stickyNoteModeController.ToggleMode,
                    GetCurrentRepoPathForGitRefresh,
                    () => state.CurrentFolderPath,
                    callbacks.GetLocalizedString,
                    () => explorerNavigationController,
                    callbacks.SetCurrentRepoPath,
                    callbacks.SetCurrentFolderPath,
                    RefreshGitStatusUIAsync,
                    callbacks.EnsureLeftPanelVisible,
                    callbacks.ShowLeftSidebarPage,
                    callbacks.LoadFileIntoTabAsync,
                    callbacks.InitializePickerWindow,
                    NavigateExplorerToFolderAndRevealAsync,
                    callbacks.OpenNewTabFromRequest,
                    callbacks.OpenImageTab,
                    callbacks.OpenMediaTab,
                    callbacks.OpenPdfTab,
                    callbacks.OpenOfficeDocumentTab,
                    callbacks.OpenHexTab,
                    QueueGitStatusRefresh));
            functionKeyShortcutService = workspaceControllers.FunctionKeyShortcut;
            gitAutoRefreshTimer = workspaceControllers.GitAutoRefreshTimer;
            gitPanelController = workspaceControllers.GitPanel;
            gitPanelController.FileRestored += callbacks.GitFileRestored;
            gitStatusRefreshController = workspaceControllers.GitStatusRefresh;
            explorerNavigationController = workspaceControllers.ExplorerNavigation;
            favoritesRecentController = workspaceControllers.FavoritesRecent;

            var documentCommandControllers = MainWindowDocumentCommandComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                statusBarController,
                tabNavigationController,
                livePreviewController,
                tabDirtyStateController,
                tabEncryptionController,
                favoritesRecentController,
                dialogController,
                new MainWindowDocumentCommandCallbacks(
                    FlushPendingSplitImeSyncAsync,
                    callbacks.UpdateLanguageUi,
                    RefreshGitStatusUIAsync,
                    callbacks.UpdateWindowTitle,
                    () => state.CurrentFolderPath,
                    LoadDirectoryRoot,
                    GetSearchRoot,
                    () => state.CurrentRepoPath,
                    ClearPendingSplitImeSync,
                    callbacks.OpenNewTab,
                    callbacks.CloseReadOnlyViewer,
                    SaveUiLayoutSettingsAsync,
                    callbacks.GetCurrentElementTheme,
                    callbacks.GetLocalizedString));
            tabSaveController = documentCommandControllers.TabSave;
            autoSaveController = documentCommandControllers.AutoSave;
            tabCloseController = documentCommandControllers.TabClose;
            tabMoveController = documentCommandControllers.TabMove;

            var interactionControllers = MainWindowInteractionComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                shellPanelLayoutService,
                terminalShortcutService,
                tabNavigationController,
                tabEncryptionController,
                activeEditorInsertionController,
                favoritesRecentController,
                dialogController,
                pdfViewerController,
                officeDocumentViewerController,
                new MainWindowInteractionCallbacks(
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    () => callbacks.OpenNewTab(),
                    LoadDirectoryRoot,
                    callbacks.LoadFileIntoTabAsync,
                    livePreviewController.OpenFileInExternalViewerAsync,
                    livePreviewController.OpenFileWithDefaultProgramAsync,
                    callbacks.LoadFileIntoTabAtLineAsync,
                    NavigateExplorerToFolderAsync,
                    NavigateExplorerToFolderAndRevealAsync,
                    callbacks.GetSelectedExplorerItem,
                    () => explorerNavigationController?.IsViewingArchive == true,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    callbacks.FocusSearchPanel,
                    callbacks.CloseActiveTab,
                    () => toolbarCommandController?.SaveActive(),
                    () => toolbarCommandController?.SaveActiveAs(),
                    () => toolbarCommandController?.OpenFile(),
                    () => toolbarCommandController?.Find(),
                    () => toolbarCommandController?.Print(),
                    stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => toolbarCommandController?.ToggleTheme(),
                    stickyNoteModeController.ToggleMode,
                    () => toolbarCommandController?.ToggleLivePreview(),
                    TogglePreviewWidth,
                    callbacks.ToggleMaximize,
                    callbacks.ShowLeftSidebarPage,
                    callbacks.CloseTabAndCleanup,
                    callbacks.SyncSnippetsToOpenEditorsAsync,
                    callbacks.InitializePickerWindow,
                    callbacks.GetLocalizedString,
                    callbacks.GetCurrentElementTheme,
                    (tab, tabItem) => MainWindowTabOperations.ReloadAsync(
                        tab,
                        tabItem,
                        statusBarController,
                        pdfViewerController,
                        officeDocumentViewerController,
                        tabReloadController,
                        callbacks.UpdateLanguageUi,
                        callbacks.UpdateWindowTitle),
                    async (tab, enabled) =>
                    {
                        await callbacks.SetHexViewModeAsync(tab, enabled);
                        toolbarCommandController?.SyncCsvTableMode(tab);
                    },
                    async (tab, enabled) =>
                    {
                        if (toolbarCommandController != null)
                        {
                            await toolbarCommandController.SetCsvTableModeAsync(tab, enabled);
                            return;
                        }

                        tab.IsCsvTableModeEnabled = enabled && !tab.IsHexViewer;
                        if (state.TabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            await bridgeGroup.Bridge.SetCsvTableModeAsync(tab.IsCsvTableModeEnabled);
                        }
                    },
                    (_, tabItem, tabView) => tabCloseController.CloseRightTabs(tabItem, tabView),
                    (_, tabItem, tabView) => tabCloseController.CloseLeftTabs(tabItem, tabView),
                    (_, tabItem, tabView) => tabCloseController.CloseOtherTabs(tabItem, tabView)));
            tabContextMenuController = interactionControllers.TabContextMenu;
            fileOpenDropController = interactionControllers.FileOpenDrop;
            rootKeyboardShortcutController = interactionControllers.RootKeyboardShortcut;
            terminalPanelController = interactionControllers.TerminalPanel;
            snippetsController = interactionControllers.Snippets;

            var agentControllers = MainWindowAgentComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                tabNavigationController,
                tabDirtyStateController,
                tabCloseController,
                searchReplaceTabSyncController,
                compareTabController,
                activeEditorInsertionController,
                tabTextContextProvider,
                dialogController,
                new MainWindowAgentCompositionCallbacks(
                    callbacks.GetSelectedExplorerItem,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    LoadDirectoryRoot,
                    QueueGitStatusRefresh,
                    callbacks.GetAgentSessionEdits,
                    callbacks.LoadFileIntoTabForAgentAsync,
                    NavigateExplorerToFolderAndRevealAsync,
                    callbacks.OpenGeneratedTab,
                    callbacks.SaveTabAsync,
                    callbacks.InitializePickerWindow,
                    () => explorerNavigationController.RefreshCurrentFolder(),
                    callbacks.GetLocalizedString,
                    callbacks.UpdateWindowTitle));
            llmAssistantController = agentControllers.LlmAssistant;
            agentController = agentControllers.Agent;

            var editorRuntimeControllers = MainWindowEditorRuntimeComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                statusBarController,
                tabNavigationController,
                tabDirtyStateController,
                tabEncryptionController,
                livePreviewController,
                pdfViewerController,
                officeDocumentViewerController,
                previewControllers.WebViewShortcut,
                editorWebViewInitializationController,
                editorLineNavigationController,
                editorBridgeShortcutController,
                editorLinkNavigationController,
                activeEditorInsertionController,
                tabContextMenuController,
                favoritesRecentController,
                llmAssistantController,
                agentController,
                dialogController,
                shellPanelLayoutService,
                initialEditorLineWarmupCount,
                new MainWindowEditorRuntimeCallbacks(
                    callbacks.SchedulePreview,
                    callbacks.UpdateLanguageUi,
                    QueuePendingSplitImeLineSyncIfNeeded,
                    SchedulePendingSplitImeCompletionSyncIfNeeded,
                    ScheduleDeferredPendingSplitImeSyncIfNeeded,
                    SyncLineChangeToOtherTabsAsync,
                    tab => SyncEditsToOtherTabsAsync(tab),
                    RecordPendingSplitImeStructuralEdit,
                    HasOtherTabForSameFile,
                    FlushOtherTabsPendingSyncsAsync,
                    SaveSidebarVisibilitySettingsAsync,
                    callbacks.RefreshActivePreview,
                    callbacks.LoadFileIntoTabAsync,
                    callbacks.UpdateRightPanelSelectionContext,
                    () => state.ScrollSyncEnabled,
                    async enabled =>
                    {
                        state.ScrollSyncEnabled = enabled;
                        var settings = services.SettingsService.CurrentSettings;
                        if (settings.ScrollSyncEnabled != enabled)
                        {
                            settings.ScrollSyncEnabled = enabled;
                            await services.SettingsService.SaveSettingsAsync(settings);
                        }
                    },
                    () => state.CurrentFolderPath,
                    () => toolbarCommandController?.LivePreviewEnabled == true,
                    tab => toolbarCommandController?.SyncCsvTableMode(tab),
                    callbacks.GetCurrentElementTheme,
                    callbacks.SaveTabAsync,
                    callbacks.GetPreviewBaseHref,
                    callbacks.GetLocalizedString,
                    ApplyEditorSurfaceBackground,
                    callbacks.UpdateWindowTitle,
                    callbacks.OpenNewTab,
                    (filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted, encryptionPassword) =>
                        callbacks.OpenNewTabFromRequest(new FileTabOpenRequest
                        {
                            FilePath = filePath,
                            Content = content,
                            IsReadOnly = isReadOnly,
                            EncodingName = encodingName,
                            EncodingWasAutoDetected = encodingWasAutoDetected,
                            IsEncrypted = isEncrypted,
                            EncryptionPassword = encryptionPassword
                        }),
                    callbacks.CloseTabAndCleanup,
                    (_, args) => tabCloseController.CloseRequested(args)));
            tabCloseController.SetAdditionalTabCleanup(editorRuntimeControllers.EditorTabOpen.ForgetHexViewState);
            shellPaneController = editorRuntimeControllers.ShellPane;

            void OpenTextInEditor(string title, string content)
            {
                string uniqueTitle = CreateUniqueGeneratedTitle(title, viewModel);
                var tab = callbacks.OpenGeneratedTab(content);
                tab.Title = uniqueTitle;
                tab.Language = services.LanguageDetectionService.GetMonacoLanguageName(uniqueTitle);
                callbacks.UpdateWindowTitle();
            }

            var startupControllers = MainWindowStartupComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                terminalShortcutService,
                functionKeyShortcutService,
                autoSaveController,
                gitAutoRefreshTimer,
                splitImeSyncController,
                livePreviewController,
                pdfViewerController,
                officeDocumentViewerController,
                statusBarController,
                llmAssistantController,
                agentController,
                tabNavigationController,
                snippetsController,
                favoritesRecentController,
                fileOpenDropController,
                shellPanelLayoutService,
                rootKeyboardShortcutController,
                tabSaveController,
                terminalPanelController,
                stickyNoteModeController,
                shellPaneController,
                compareTabController,
                dialogController,
                new MainWindowStartupCallbacks(
                    () => state.CurrentRepoPath,
                    () => state.CurrentFolderPath,
                    NavigateExplorerToFolderAsync,
                    callbacks.LoadFileIntoTabAsync,
                    () => callbacks.OpenNewTab(),
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    ApplySavedPanelWidths,
                    ApplyUiPersonalization,
                    callbacks.LocalizeUi,
                    ApplyToolbarSettings,
                    callbacks.SyncAgentSettingsAfterLoad,
                    RefreshGitStatusUIAsync,
                    callbacks.UpdateAutoSaveStatus,
                    callbacks.GetLocalizedString,
                    callbacks.GetCurrentElementTheme,
                    callbacks.InitializePickerWindow,
                    OpenTextInEditor,
                    callbacks.GetPreviewBaseHref));
            settingsController = startupControllers.Settings;
            toolbarCommandController = startupControllers.ToolbarCommand;

            MainWindowEventBinder.Bind(
                ui,
                searchReplaceController,
                tabMoveController,
                tabCloseController,
                toolbarCommandController,
                () => callbacks.OpenNewTab(),
                SaveUiLayoutSettingsAsync);

            return new MainWindowControllers(
                new ShellControllers(shellControllers, interactionControllers),
                new EditorControllers(editorFoundationControllers, editorRuntimeControllers),
                DocumentControllers.From(documentCommandControllers),
                previewControllers,
                agentControllers,
                workspaceControllers,
                LifecycleControllers.From(startupControllers));
        }

        private static string CreateUniqueGeneratedTitle(string title, MainWindowViewModel viewModel)
        {
            string uniqueTitle = string.IsNullOrWhiteSpace(title) ? "Untitled.txt" : title.Trim();
            string extension = string.Empty;
            string baseName = uniqueTitle;
            int lastDot = uniqueTitle.LastIndexOf('.');
            if (lastDot > 0 && lastDot < uniqueTitle.Length - 1)
            {
                baseName = uniqueTitle.Substring(0, lastDot);
                extension = uniqueTitle.Substring(lastDot);
            }

            int counter = 1;
            while (GeneratedTitleExists(uniqueTitle, viewModel))
            {
                counter++;
                uniqueTitle = $"{baseName} ({counter}){extension}";
            }

            return uniqueTitle;
        }

        private static bool GeneratedTitleExists(string title, MainWindowViewModel viewModel)
        {
            foreach (var tab in viewModel.Tabs)
            {
                if (string.IsNullOrEmpty(tab.FilePath) &&
                    string.Equals(tab.Title, title, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
