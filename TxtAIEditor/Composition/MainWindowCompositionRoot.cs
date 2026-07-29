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

            void ApplyLeftSidebarVisibility(bool show) =>
                moduleBindings.ShellPane.ApplyLeftSidebarVisibility(show);

            void ApplyPreviewVisibility(bool show) =>
                moduleBindings.ApplyPreviewVisibility(
                    show,
                    lifecycleFacade.IsStartupInitializationComplete);

            Task ToggleLeftPanelAsync() =>
                moduleBindings.ShellPane.ToggleLeftPanelAsync();

            Task ToggleRightPanelAsync() =>
                moduleBindings.ShellPane.ToggleRightPanelAsync();

            void ApplyEditorSurfaceBackground(EditorSettings settings) =>
                moduleBindings.Settings.ApplyEditorSurfaceBackground(settings);

            var shellModule = MainWindowShellModule.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                shellServices,
                viewModel,
                state,
                shellFacade,
                editorFacade,
                () => moduleBindings.ToolbarCommand?.ToggleTerminal(),
                ApplyLeftSidebarVisibility,
                ApplyPreviewVisibility);
            void ApplySavedPanelWidths(EditorSettings settings) =>
                shellModule.ApplySavedPanelWidths(settings);

            void TogglePreviewWidth() =>
                shellModule.TogglePreviewWidth();

            var previewModule = MainWindowPreviewModule.Compose(
                ui,
                commonServices,
                documentServices,
                viewModel,
                state,
                new MainWindowPreviewModuleDependencies(shellModule),
                shellFacade,
                editorFacade,
                documentFacade,
                () => moduleBindings.ToolbarCommand,
                ToggleLeftPanelAsync,
                ToggleRightPanelAsync,
                moduleBindings.ToggleExplorerTreeMode,
                moduleBindings.NavigateExplorerToFolderAndRevealAsync);
            moduleBindings.Bind(previewModule);

            var workspaceModule = MainWindowWorkspaceModule.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state,
                new MainWindowWorkspaceModuleDependencies(shellModule, previewModule),
                shellFacade,
                documentFacade,
                previewFacade,
                workspaceFacade,
                () => moduleBindings.ToolbarCommand);
            moduleBindings.Bind(workspaceModule);

            var editorModule = MainWindowEditorModule.ComposeFoundation(
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
                viewModel,
                state,
                shellModule,
                previewModule,
                initialEditorLineWarmupCount,
                shellFacade,
                editorFacade,
                documentFacade,
                workspaceModule,
                () => moduleBindings.ToolbarCommand,
                ToggleLeftPanelAsync,
                ToggleRightPanelAsync,
                TogglePreviewWidth);

            var documentModule = MainWindowDocumentModule.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                viewModel,
                state,
                new MainWindowDocumentModuleDependencies(
                    shellModule,
                    editorModule,
                    previewModule),
                workspaceModule,
                shellFacade,
                editorFacade,
                documentFacade,
                previewFacade,
                shellModule.SaveUiLayoutSettingsAsync);

            var interactionControllers = MainWindowInteractionComposition.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
                viewModel,
                shellModule,
                editorModule,
                workspaceModule,
                new MainWindowInteractionCallbacks(
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    () => documentFacade.OpenEmptyTab(),
                    workspaceModule.LoadDirectoryRoot,
                    workspaceModule.RefreshTreeFolder,
                    documentFacade.LoadFileIntoTabAsync,
                    previewModule.OpenFileInExternalViewerAsync,
                    previewModule.OpenFileWithDefaultProgramAsync,
                    documentFacade.LoadFileIntoTabAsync,
                    workspaceModule.NavigateExplorerToFolderAsync,
                    folderPath => workspaceModule.NavigateExplorerToFolderAsync(
                        folderPath,
                        revealInLeftPanel: true),
                    workspaceFacade.GetSelectedExplorerItem,
                    () => workspaceModule.IsViewingArchive,
                    () => workspaceModule.IsViewingRemote,
                    workspaceModule.RefreshRemoteDirectoryAsync,
                    () => workspaceModule.IsTreeMode,
                    ToggleLeftPanelAsync,
                    ToggleRightPanelAsync,
                    shellFacade.FocusSearchPanel,
                    documentFacade.CloseActiveTab,
                    () => moduleBindings.ToolbarCommand?.SaveActive(),
                    () => moduleBindings.ToolbarCommand?.SaveActiveAs(),
                    () => moduleBindings.ToolbarCommand?.OpenFile(),
                    () => moduleBindings.ToolbarCommand?.Find(),
                    () => moduleBindings.ToolbarCommand?.Print(),
                    shellModule.ToggleTopMostFromShortcut,
                    () => moduleBindings.ToolbarCommand?.ToggleTheme(),
                    shellModule.ToggleStickyNoteMode,
                    () => moduleBindings.ToolbarCommand?.ToggleLivePreview(),
                    workspaceModule.ToggleExplorerTreeMode,
                    () => moduleBindings.ToolbarCommand?.ToggleCsvTableMode(),
                    TogglePreviewWidth,
                    shellFacade.ToggleMaximize,
                    () => moduleBindings.ToolbarCommand?.ToggleWordWrap(),
                    shellFacade.ShowLeftSidebarPage,
                    documentFacade.CloseTabAndCleanup,
                    editorFacade.SyncSnippetsToOpenEditorsAsync,
                    shellFacade.InitializePickerWindow,
                    shellFacade.GetLocalizedString,
                    shellFacade.GetCurrentElementTheme,
                    (tab, tabItem) => shellModule.ReloadTabAsync(
                        tab,
                        tabItem,
                        previewModule,
                        editorModule,
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
            var fileOpenDropController = interactionControllers.FileOpenDrop;
            var rootKeyboardShortcutController = interactionControllers.RootKeyboardShortcut;
            var terminalPanelController = interactionControllers.TerminalPanel;
            var snippetsController = interactionControllers.Snippets;

            var agentModule = MainWindowAgentModule.Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
                agentServices,
                viewModel,
                state,
                new MainWindowAgentModuleDependencies(
                    shellModule,
                    editorModule,
                    documentModule,
                    previewModule),
                workspaceModule,
                shellFacade,
                documentFacade,
                agentFacade,
                workspaceFacade);
            editorModule.ComposeRuntime(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
                viewModel,
                state,
                new MainWindowEditorRuntimeModuleDependencies(
                    shellModule,
                    previewModule,
                    documentModule,
                    interactionControllers,
                    workspaceModule,
                    agentModule),
                initialEditorLineWarmupCount,
                shellFacade,
                editorFacade,
                documentFacade,
                previewFacade,
                () => moduleBindings.ToolbarCommand,
                ApplyEditorSurfaceBackground);
            documentModule.SetAdditionalTabCleanup(editorModule.ForgetHexViewState);
            moduleBindings.Bind(editorModule);

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
                shellModule,
                editorModule,
                workspaceModule,
                documentModule,
                previewModule,
                agentModule,
                snippetsController,
                fileOpenDropController,
                rootKeyboardShortcutController,
                documentModule,
                terminalPanelController,
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
                editorModule,
                documentModule,
                toolbarCommandController,
                () => documentFacade.OpenEmptyTab(),
                shellModule.SaveUiLayoutSettingsAsync);

            shellModule.BindInteractions(interactionControllers);
            return new MainWindowControllers(
                shellModule,
                editorModule,
                documentModule,
                previewModule,
                agentModule,
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
