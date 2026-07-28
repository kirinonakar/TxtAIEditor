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
            MainWindowHostFacades host)
        {
            var moduleBindings = new MainWindowModuleBindings();
            var commonServices = services.Common;
            var documentServices = services.Documents;
            var workspaceServices = services.Workspace;
            var editorServices = services.Editor;
            var agentServices = services.Agents;
            var shellServices = services.Shell;
            var shellFacade = host.Shell;
            var editorFacade = host.Editor;
            var documentFacade = host.Documents;
            var previewFacade = host.Preview;
            var agentFacade = host.Agents;
            var workspaceFacade = host.Workspace;
            var lifecycleFacade = host.Lifecycle;

            Task SaveUiLayoutSettingsAsync(ShellPanelLayoutService shellPanelLayout) =>
                MainWindowLayoutOperations.SaveUiLayoutSettingsAsync(
                    window.AppWindow,
                    commonServices.SettingsService,
                    ui.EditorWorkspace,
                    shellPanelLayout);

            void ApplyLeftSidebarVisibility(bool show) =>
                moduleBindings.ShellPane.ApplyLeftSidebarVisibility(show);

            void ApplyPreviewVisibility(bool show) =>
                MainWindowLayoutOperations.ApplyPreviewVisibility(
                    show,
                    moduleBindings.ShellPane,
                    lifecycleFacade.IsStartupInitializationComplete,
                    moduleBindings.LivePreview);

            Task ToggleLeftPanelAsync() =>
                moduleBindings.ShellPane.ToggleLeftPanelAsync();

            Task ToggleRightPanelAsync() =>
                moduleBindings.ShellPane.ToggleRightPanelAsync();

            void ApplyEditorSurfaceBackground(EditorSettings settings) =>
                moduleBindings.Settings.ApplyEditorSurfaceBackground(settings);

            var shellControllers = MainWindowShellComposition.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                shellServices,
                viewModel,
                state.TabBridges,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                new MainWindowShellCompositionCallbacks(
                    SaveUiLayoutSettingsAsync,
                    () => moduleBindings.ToolbarCommand?.ToggleTerminal(),
                    shellFacade.GetCurrentElementTheme,
                    shellFacade.GetLocalizedString,
                    shellFacade.UpdateWindowTitle,
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    editorFacade.ReloadTabWithEncodingAsync,
                    editorFacade.MarkTabDirtyFromStatusBar,
                    editorFacade.PerformLineNavigationAsync,
                    editorFacade.UpdateLivePreview));
            var shellPanelLayoutService = shellControllers.ShellPanelLayout;
            var tabNavigationController = shellControllers.TabNavigation;
            var terminalShortcutService = shellControllers.TerminalShortcut;
            var dialogController = shellControllers.Dialog;
            var tabEncryptionController = shellControllers.TabEncryption;
            var stickyNoteModeController = shellControllers.StickyNoteMode;
            var statusBarController = shellControllers.StatusBar;

            Task SaveSidebarVisibilitySettingsAsync() =>
                MainWindowLayoutOperations.SaveSidebarVisibilitySettingsAsync(
                    commonServices.SettingsService,
                    shellPanelLayoutService);

            void ApplySavedPanelWidths(EditorSettings settings) =>
                shellPanelLayoutService.ApplySavedPanelWidths(settings.LeftSidebarWidth, settings.RightSidebarWidth);

            void TogglePreviewWidth() =>
                shellPanelLayoutService.TogglePreviewWidth();

            var previewModule = MainWindowPreviewModule.Compose(
                ui,
                commonServices,
                documentServices,
                viewModel,
                state,
                new MainWindowPreviewModuleDependencies(shellControllers),
                shellFacade,
                editorFacade,
                documentFacade,
                () => moduleBindings.ToolbarCommand,
                ToggleLeftPanelAsync,
                ToggleRightPanelAsync,
                folderPath => moduleBindings.ExplorerNavigation.NavigateToFolderAsync(
                    folderPath,
                    revealInLeftPanel: true));
            var previewControllers = previewModule.Controllers;
            moduleBindings.Bind(previewModule);
            var compareTabController = previewControllers.CompareTab;
            var livePreviewController = previewControllers.LivePreview;
            var editorWebViewInitializationController = previewControllers.EditorWebViewInitialization;
            var editorLineNavigationController = previewControllers.EditorLineNavigation;
            var pdfViewerController = previewControllers.PdfViewer;
            var officeDocumentViewerController = previewControllers.OfficeDocumentViewer;
            var editorLinkNavigationController = previewControllers.EditorLinkNavigation;
            var notebookViewerController = previewControllers.NotebookViewer;

            var workspaceModule = MainWindowWorkspaceModule.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state,
                new MainWindowWorkspaceModuleDependencies(shellControllers, previewModule),
                shellFacade,
                documentFacade,
                previewFacade,
                workspaceFacade,
                () => moduleBindings.ToolbarCommand);
            var workspaceControllers = workspaceModule.Controllers;
            moduleBindings.Bind(workspaceModule);
            var functionKeyShortcutService = workspaceControllers.FunctionKeyShortcut;
            var gitAutoRefreshTimer = workspaceControllers.GitAutoRefreshTimer;
            var explorerNavigationController = workspaceControllers.ExplorerNavigation;
            var favoritesRecentController = workspaceControllers.FavoritesRecent;

            var editorFoundationControllers = MainWindowEditorFoundationComposition.Compose(
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
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
                    shellFacade.ToggleMaximize,
                    () => moduleBindings.ToolbarCommand?.ToggleWordWrap(),
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    TogglePreviewWidth,
                    () => documentFacade.OpenEmptyTab(),
                    () => moduleBindings.ToolbarCommand?.SaveActive(),
                    () => moduleBindings.ToolbarCommand?.SaveActiveAs(),
                    () => moduleBindings.ToolbarCommand?.OpenFile(),
                    documentFacade.CloseActiveTab,
                    () => moduleBindings.ToolbarCommand?.Print(),
                    shellFacade.FocusSearchPanel,
                    editorFacade.UpdateLivePreview,
                    editorFacade.UpdateLanguageUi,
                    editorFacade.SchedulePreview,
                    shellFacade.UpdateWindowTitle,
                    documentFacade.LoadFileIntoTabAsync,
                    workspaceModule.GetSearchRoot,
                    workspaceModule.GetLargeFileThresholdBytes,
                    workspaceModule.RefreshGitStatusUiAsync,
                    shellFacade.GetLocalizedString));
            var tabReloadController = editorFoundationControllers.TabReload;
            var tabDirtyStateController = editorFoundationControllers.TabDirtyState;
            var activeEditorInsertionController = editorFoundationControllers.ActiveEditorInsertion;
            var editorBridgeShortcutController = editorFoundationControllers.EditorBridgeShortcut;
            var searchReplaceController = editorFoundationControllers.SearchReplace;
            var splitImeSyncController = editorFoundationControllers.SplitImeSync;

            Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true) =>
                splitImeSyncController.SyncEditsToOtherTabsAsync(sourceTab, updateUi);

            var documentModule = MainWindowDocumentModule.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state,
                new MainWindowDocumentModuleDependencies(
                    statusBarController,
                    tabNavigationController,
                    livePreviewController,
                    tabDirtyStateController,
                    tabEncryptionController,
                    favoritesRecentController,
                    dialogController,
                    notebookViewerController),
                workspaceModule,
                shellFacade,
                editorFacade,
                documentFacade,
                previewFacade,
                () => SaveUiLayoutSettingsAsync(shellPanelLayoutService));

            var interactionControllers = MainWindowInteractionComposition.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
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
                    () => documentFacade.OpenEmptyTab(),
                    workspaceModule.LoadDirectoryRoot,
                    explorerNavigationController.RefreshTreeFolder,
                    documentFacade.LoadFileIntoTabAsync,
                    livePreviewController.OpenFileInExternalViewerAsync,
                    livePreviewController.OpenFileWithDefaultProgramAsync,
                    documentFacade.LoadFileIntoTabAsync,
                    workspaceModule.NavigateExplorerToFolderAsync,
                    folderPath => workspaceModule.NavigateExplorerToFolderAsync(
                        folderPath,
                        revealInLeftPanel: true),
                    workspaceFacade.GetSelectedExplorerItem,
                    () => explorerNavigationController.IsViewingArchive,
                    () => explorerNavigationController.IsViewingRemote,
                    explorerNavigationController.RefreshRemoteDirectoryAsync,
                    () => explorerNavigationController.IsTreeMode,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    shellFacade.FocusSearchPanel,
                    documentFacade.CloseActiveTab,
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
                    shellFacade.ToggleMaximize,
                    () => moduleBindings.ToolbarCommand?.ToggleWordWrap(),
                    shellFacade.ShowLeftSidebarPage,
                    documentFacade.CloseTabAndCleanup,
                    editorFacade.SyncSnippetsToOpenEditorsAsync,
                    shellFacade.InitializePickerWindow,
                    shellFacade.GetLocalizedString,
                    shellFacade.GetCurrentElementTheme,
                    (tab, tabItem) => MainWindowTabOperations.ReloadAsync(
                        tab,
                        tabItem,
                        statusBarController,
                        pdfViewerController,
                        officeDocumentViewerController,
                        notebookViewerController,
                        tabReloadController,
                        editorFacade.UpdateLanguageUi,
                        shellFacade.UpdateWindowTitle),
                    async (tab, enabled) =>
                    {
                        await editorFacade.SetHexViewModeAsync(tab, enabled);
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
                    (_, tabItem, tabView) => documentModule.CloseRightTabs(tabItem, tabView),
                    (_, tabItem, tabView) => documentModule.CloseLeftTabs(tabItem, tabView),
                    (_, tabItem, tabView) => documentModule.CloseOtherTabs(tabItem, tabView),
                    previewFacade.OpenNotebookSourceTabAsync,
                    previewFacade.OpenNotebookViewerTabAsync));
            var tabContextMenuController = interactionControllers.TabContextMenu;
            var fileOpenDropController = interactionControllers.FileOpenDrop;
            var rootKeyboardShortcutController = interactionControllers.RootKeyboardShortcut;
            var terminalPanelController = interactionControllers.TerminalPanel;
            var snippetsController = interactionControllers.Snippets;

            var agentControllers = MainWindowAgentComposition.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
                agentServices,
                viewModel,
                state,
                new MainWindowAgentModuleDependencies(
                    shellControllers,
                    editorFoundationControllers,
                    documentModule,
                    previewModule),
                workspaceControllers,
                shellFacade,
                documentFacade,
                agentFacade,
                workspaceFacade);
            var llmAssistantController = agentControllers.LlmAssistant;
            var agentController = agentControllers.Agent;

            var editorRuntimeControllers = MainWindowEditorRuntimeComposition.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
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
                notebookViewerController,
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
                    editorFacade.SchedulePreview,
                    editorFacade.UpdateLanguageUi,
                    tab => SyncEditsToOtherTabsAsync(tab),
                    SaveSidebarVisibilitySettingsAsync,
                    previewFacade.RefreshActivePreview,
                    documentFacade.LoadFileIntoTabAsync,
                    editorFacade.UpdateRightPanelSelectionContext,
                    () => state.ScrollSyncEnabled,
                    async enabled =>
                    {
                        state.ScrollSyncEnabled = enabled;
                        var settings = commonServices.SettingsService.CurrentSettings;
                        if (settings.ScrollSyncEnabled != enabled)
                        {
                            settings.ScrollSyncEnabled = enabled;
                            await commonServices.SettingsService.SaveSettingsAsync(settings);
                        }
                    },
                    () => state.CurrentFolderPath,
                    () => moduleBindings.ToolbarCommand?.LivePreviewEnabled == true,
                    tab => moduleBindings.ToolbarCommand?.SyncCsvTableMode(tab),
                    shellFacade.GetCurrentElementTheme,
                    documentFacade.SaveTabAsync,
                    previewFacade.GetPreviewBaseHref,
                    shellFacade.GetLocalizedString,
                    ApplyEditorSurfaceBackground,
                    shellFacade.UpdateWindowTitle,
                    documentFacade.OpenEmptyTab,
                    (filePath, content, isReadOnly, encodingName, encodingWasAutoDetected, isEncrypted, encryptionPassword) =>
                        documentFacade.OpenNewTab(new FileTabOpenRequest
                        {
                            FilePath = filePath,
                            Content = content,
                            IsReadOnly = isReadOnly,
                            EncodingName = encodingName,
                            EncodingWasAutoDetected = encodingWasAutoDetected,
                            IsEncrypted = isEncrypted,
                            EncryptionPassword = encryptionPassword
                        }),
                    documentFacade.CloseTabAndCleanup,
                    (_, args) => documentModule.CloseRequested(args)));
            documentModule.SetAdditionalTabCleanup(editorRuntimeControllers.EditorTabOpen.ForgetHexViewState);
            moduleBindings.Bind(editorRuntimeControllers);
            var shellPaneController = editorRuntimeControllers.ShellPane;

            void OpenTextInEditor(string title, string content)
            {
                string uniqueTitle = CreateUniqueGeneratedTitle(title, viewModel);
                var tab = documentFacade.OpenGeneratedTab(content);
                tab.Title = uniqueTitle;
                tab.Language = commonServices.LanguageDetectionService.GetEditorLanguageName(uniqueTitle);
                shellFacade.UpdateWindowTitle();

                if (string.Equals(tab.Language, "html", StringComparison.OrdinalIgnoreCase))
                {
                    moduleBindings.ToolbarCommand?.EnableLivePreview();
                }
            }

            var startupControllers = MainWindowStartupComposition.Compose(
                window,
                ui,
                commonServices,
                shellServices,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                terminalShortcutService,
                functionKeyShortcutService,
                documentModule,
                gitAutoRefreshTimer,
                livePreviewController,
                pdfViewerController,
                officeDocumentViewerController,
                notebookViewerController,
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
                documentModule,
                terminalPanelController,
                stickyNoteModeController,
                shellPaneController,
                compareTabController,
                dialogController,
                new MainWindowStartupCallbacks(
                    () => state.CurrentRepoPath,
                    () => state.CurrentFolderPath,
                    workspaceModule.NavigateExplorerToFolderAsync,
                    workspaceModule.SetExplorerTreeMode,
                    documentFacade.LoadFileIntoTabAsync,
                    () => documentFacade.OpenEmptyTab(),
                    ApplyLeftSidebarVisibility,
                    ApplyPreviewVisibility,
                    ApplySavedPanelWidths,
                    lifecycleFacade.LocalizeUi,
                    agentFacade.SyncAgentSettingsAfterLoad,
                    workspaceModule.RefreshGitStatusUiAsync,
                    lifecycleFacade.UpdateAutoSaveStatus,
                    shellFacade.GetLocalizedString,
                    shellFacade.GetCurrentElementTheme,
                    shellFacade.InitializePickerWindow,
                    OpenTextInEditor,
                    previewFacade.GetPreviewBaseHref));
            moduleBindings.Bind(startupControllers);
            var toolbarCommandController = startupControllers.ToolbarCommand;

            moduleBindings.ValidateComplete();

            MainWindowEventBinder.Bind(
                ui,
                searchReplaceController,
                documentModule,
                toolbarCommandController,
                () => documentFacade.OpenEmptyTab(),
                () => SaveUiLayoutSettingsAsync(shellPanelLayoutService));

            return new MainWindowControllers(
                new ShellControllers(shellControllers, interactionControllers),
                new EditorControllers(editorFoundationControllers, editorRuntimeControllers),
                documentModule,
                previewModule,
                agentControllers,
                workspaceModule,
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
