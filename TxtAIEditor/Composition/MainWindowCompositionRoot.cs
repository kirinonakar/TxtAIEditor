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
            var moduleBindings = new MainWindowModuleBindings();

            Task SaveUiLayoutSettingsAsync(ShellPanelLayoutService shellPanelLayout) =>
                MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(
                    window.AppWindow,
                    services.SettingsService,
                    ui.EditorWorkspace,
                    shellPanelLayout);

            void ApplyLeftSidebarVisibility(bool show) =>
                moduleBindings.ShellPane.ApplyLeftSidebarVisibility(show);

            void ApplyPreviewVisibility(bool show) =>
                MainWindowLayoutOperations.ApplyPreviewVisibility(
                    show,
                    moduleBindings.ShellPane,
                    callbacks.IsStartupInitializationComplete(),
                    moduleBindings.LivePreview);

            Task ToggleLeftPanelAsync() =>
                moduleBindings.ShellPane.ToggleLeftPanelAsync();

            Task ToggleRightPanelAsync() =>
                moduleBindings.ShellPane.ToggleRightPanelAsync();

            void LoadDirectoryRoot(string folderPath) =>
                moduleBindings.ExplorerNavigation.LoadDirectoryRoot(folderPath);

            Task NavigateExplorerToFolderAsync(string folderPath, bool revealInLeftPanel = true) =>
                moduleBindings.ExplorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel);

            Task NavigateExplorerToFolderAndRevealAsync(string folderPath) =>
                moduleBindings.ExplorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel: true);

            void QueueGitStatusRefresh() =>
                moduleBindings.GitStatusRefresh.QueueRefresh();

            string GetSearchRoot() =>
                MainWindowWorkspaceOperations.GetSearchRoot(state);

            long GetLargeFileThresholdBytes() =>
                MainWindowWorkspaceOperations.GetLargeFileThresholdBytes(services.SettingsService);

            void ApplyEditorSurfaceBackground(EditorSettings settings) =>
                moduleBindings.Settings.ApplyEditorSurfaceBackground(settings);

            var shellControllers = MainWindowShellComposition.Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowShellCompositionCallbacks(
                    SaveUiLayoutSettingsAsync,
                    () => moduleBindings.ToolbarCommand?.ToggleTerminal(),
                    callbacks.GetCurrentElementTheme,
                    callbacks.GetLocalizedString,
                    callbacks.UpdateWindowTitle,
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    callbacks.ReloadTabWithEncodingAsync,
                    callbacks.MarkTabDirtyFromStatusBar,
                    callbacks.PerformLineNavigationAsync,
                    callbacks.UpdateLivePreview));
            var shellPanelLayoutService = shellControllers.ShellPanelLayout;
            var tabNavigationController = shellControllers.TabNavigation;
            var terminalShortcutService = shellControllers.TerminalShortcut;
            var dialogController = shellControllers.Dialog;
            var tabEncryptionController = shellControllers.TabEncryption;
            var stickyNoteModeController = shellControllers.StickyNoteMode;
            var statusBarController = shellControllers.StatusBar;

            Task SaveSidebarVisibilitySettingsAsync() =>
                MainWindowLayoutOperations.SaveSidebarVisibilitySettingsAsync(
                    services.SettingsService,
                    shellPanelLayoutService);

            void ApplySavedPanelWidths(EditorSettings settings) =>
                shellPanelLayoutService.ApplySavedPanelWidths(settings.LeftSidebarWidth, settings.RightSidebarWidth);

            void TogglePreviewWidth() =>
                shellPanelLayoutService.TogglePreviewWidth();

            Task RefreshGitStatusUIAsync() =>
                MainWindowWorkspaceOperations.RefreshGitStatusUiAsync(
                    state,
                    services.GitService,
                    moduleBindings.GitAutoRefreshTimer,
                    tabNavigationController,
                    moduleBindings.GitStatusRefresh,
                    moduleBindings.ExplorerNavigation,
                    callbacks.SetCurrentRepoPath);

            string GetCurrentRepoPathForGitRefresh() =>
                MainWindowWorkspaceOperations.GetCurrentRepoPathForGitRefresh(
                    state,
                    services.GitService,
                    tabNavigationController,
                    callbacks.SetCurrentRepoPath);

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
                    () => moduleBindings.ToolbarCommand?.Find(),
                    () => moduleBindings.ToolbarCommand?.ToggleLivePreview(),
                    () => moduleBindings.ToolbarCommand?.ToggleTheme(),
                    callbacks.ToggleMaximize,
                    () => moduleBindings.ToolbarCommand?.Print(),
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
            moduleBindings.Bind(previewControllers);
            var compareTabController = previewControllers.CompareTab;
            var livePreviewController = previewControllers.LivePreview;
            var editorWebViewInitializationController = previewControllers.EditorWebViewInitialization;
            var editorLineNavigationController = previewControllers.EditorLineNavigation;
            var pdfViewerController = previewControllers.PdfViewer;
            var officeDocumentViewerController = previewControllers.OfficeDocumentViewer;
            var editorLinkNavigationController = previewControllers.EditorLinkNavigation;

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
                    () => moduleBindings.ToolbarCommand?.ToggleLivePreview(),
                    () => moduleBindings.ToolbarCommand?.ToggleTheme(),
                    callbacks.ToggleMaximize,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    TogglePreviewWidth,
                    () => callbacks.OpenNewTab(),
                    () => moduleBindings.ToolbarCommand?.SaveActive(),
                    () => moduleBindings.ToolbarCommand?.SaveActiveAs(),
                    () => moduleBindings.ToolbarCommand?.OpenFile(),
                    callbacks.CloseActiveTab,
                    () => moduleBindings.ToolbarCommand?.Print(),
                    callbacks.FocusSearchPanel,
                    callbacks.UpdateLivePreview,
                    callbacks.UpdateLanguageUi,
                    callbacks.SchedulePreview,
                    callbacks.UpdateWindowTitle,
                    callbacks.LoadFileIntoTabAsync,
                    GetSearchRoot,
                    GetLargeFileThresholdBytes,
                    RefreshGitStatusUIAsync,
                    callbacks.GetLocalizedString));
            var tabReloadController = editorFoundationControllers.TabReload;
            var tabDirtyStateController = editorFoundationControllers.TabDirtyState;
            var activeEditorInsertionController = editorFoundationControllers.ActiveEditorInsertion;
            var tabTextContextProvider = editorFoundationControllers.TabTextContext;
            var editorBridgeShortcutController = editorFoundationControllers.EditorBridgeShortcut;
            var searchReplaceTabSyncController = editorFoundationControllers.SearchReplaceTabSync;
            var searchReplaceController = editorFoundationControllers.SearchReplace;
            var splitImeSyncController = editorFoundationControllers.SplitImeSync;

            Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing) =>
                splitImeSyncController.SyncLineChangeToOtherTabsAsync(sourceTab, lineNumber, text, isComposing);

            Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true) =>
                splitImeSyncController.SyncEditsToOtherTabsAsync(sourceTab, updateUi);

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
                    () => moduleBindings.ToolbarCommand?.ToggleTheme(),
                    stickyNoteModeController.ToggleMode,
                    GetCurrentRepoPathForGitRefresh,
                    () => state.CurrentFolderPath,
                    callbacks.GetLocalizedString,
                    () => moduleBindings.ExplorerNavigation,
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
            moduleBindings.Bind(workspaceControllers);
            var functionKeyShortcutService = workspaceControllers.FunctionKeyShortcut;
            var gitAutoRefreshTimer = workspaceControllers.GitAutoRefreshTimer;
            var gitPanelController = workspaceControllers.GitPanel;
            gitPanelController.FileRestored += callbacks.GitFileRestored;
            var explorerNavigationController = workspaceControllers.ExplorerNavigation;
            var favoritesRecentController = workspaceControllers.FavoritesRecent;

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
                    callbacks.UpdateLanguageUi,
                    RefreshGitStatusUIAsync,
                    callbacks.UpdateWindowTitle,
                    () => state.CurrentFolderPath,
                    LoadDirectoryRoot,
                    GetSearchRoot,
                    () => state.CurrentRepoPath,
                    callbacks.OpenNewTab,
                    callbacks.CloseReadOnlyViewer,
                    () => SaveUiLayoutSettingsAsync(shellPanelLayoutService),
                    callbacks.GetCurrentElementTheme,
                    callbacks.GetLocalizedString));
            var tabSaveController = documentCommandControllers.TabSave;
            var autoSaveController = documentCommandControllers.AutoSave;
            var tabCloseController = documentCommandControllers.TabClose;
            var tabMoveController = documentCommandControllers.TabMove;

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
                    () => explorerNavigationController.IsViewingArchive,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    callbacks.FocusSearchPanel,
                    callbacks.CloseActiveTab,
                    () => moduleBindings.ToolbarCommand?.SaveActive(),
                    () => moduleBindings.ToolbarCommand?.SaveActiveAs(),
                    () => moduleBindings.ToolbarCommand?.OpenFile(),
                    () => moduleBindings.ToolbarCommand?.Find(),
                    () => moduleBindings.ToolbarCommand?.Print(),
                    stickyNoteModeController.ToggleTopMostFromShortcut,
                    () => moduleBindings.ToolbarCommand?.ToggleTheme(),
                    stickyNoteModeController.ToggleMode,
                    () => moduleBindings.ToolbarCommand?.ToggleLivePreview(),
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
                        moduleBindings.ToolbarCommand?.SyncCsvTableMode(tab);
                    },
                    async (tab, enabled) =>
                    {
                        var toolbarCommand = moduleBindings.ToolbarCommand;
                        if (toolbarCommand != null)
                        {
                            await toolbarCommand.SetCsvTableModeAsync(tab, enabled);
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
            var tabContextMenuController = interactionControllers.TabContextMenu;
            var fileOpenDropController = interactionControllers.FileOpenDrop;
            var rootKeyboardShortcutController = interactionControllers.RootKeyboardShortcut;
            var terminalPanelController = interactionControllers.TerminalPanel;
            var snippetsController = interactionControllers.Snippets;

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
            var llmAssistantController = agentControllers.LlmAssistant;
            var agentController = agentControllers.Agent;

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
                    SyncLineChangeToOtherTabsAsync,
                    tab => SyncEditsToOtherTabsAsync(tab),
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
                    () => moduleBindings.ToolbarCommand?.LivePreviewEnabled == true,
                    tab => moduleBindings.ToolbarCommand?.SyncCsvTableMode(tab),
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
            moduleBindings.Bind(editorRuntimeControllers);
            var shellPaneController = editorRuntimeControllers.ShellPane;

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
                livePreviewController,
                pdfViewerController,
                officeDocumentViewerController,
                statusBarController,
                llmAssistantController,
                agentController,
                tabNavigationController,
                editorFoundationControllers.TabDirtyState,
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
                    callbacks.LocalizeUi,
                    callbacks.SyncAgentSettingsAfterLoad,
                    RefreshGitStatusUIAsync,
                    callbacks.UpdateAutoSaveStatus,
                    callbacks.GetLocalizedString,
                    callbacks.GetCurrentElementTheme,
                    callbacks.InitializePickerWindow,
                    OpenTextInEditor,
                    callbacks.GetPreviewBaseHref));
            moduleBindings.Bind(startupControllers);
            var toolbarCommandController = startupControllers.ToolbarCommand;

            moduleBindings.ValidateComplete();

            MainWindowEventBinder.Bind(
                ui,
                searchReplaceController,
                tabMoveController,
                tabCloseController,
                toolbarCommandController,
                () => callbacks.OpenNewTab(),
                () => SaveUiLayoutSettingsAsync(shellPanelLayoutService));

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
