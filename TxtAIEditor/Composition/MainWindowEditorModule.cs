using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowEditorRuntimeModuleDependencies(
        MainWindowShellModule Shell,
        MainWindowPreviewModule Preview,
        MainWindowDocumentModule Documents,
        MainWindowInteractionControllers Interaction,
        MainWindowWorkspaceControllers Workspace,
        MainWindowAgentModuleFacade Agents);

    internal sealed class MainWindowEditorModule
    {
        private MainWindowEditorRuntimeControllers? _runtime;

        private MainWindowEditorModule(MainWindowEditorFoundationControllers foundation)
        {
            Foundation = foundation;
        }

        internal MainWindowEditorFoundationControllers Foundation { get; }

        internal MainWindowEditorRuntimeControllers Runtime =>
            _runtime ?? throw new InvalidOperationException("Editor runtime has not been composed yet.");

        public static MainWindowEditorModule ComposeFoundation(
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowShellModule shell,
            MainWindowPreviewModule preview,
            int initialEditorLineWarmupCount,
            IMainWindowShellFacade shellFacade,
            IMainWindowEditorFacade editorFacade,
            IMainWindowDocumentFacade documentFacade,
            MainWindowWorkspaceModule workspace,
            Func<MainWindowToolbarCommandController?> getToolbarCommand,
            Func<Task> toggleLeftPanelAsync,
            Func<Task> toggleRightPanelAsync,
            Action togglePreviewWidth)
        {
            var callbacks = new MainWindowEditorFoundationCallbacks(
                () => getToolbarCommand()?.ToggleLivePreview(),
                () => getToolbarCommand()?.ToggleTheme(),
                shellFacade.ToggleMaximize,
                () => getToolbarCommand()?.ToggleWordWrap(),
                () => workspace.SetExplorerTreeMode(!workspace.Controllers.ExplorerNavigation.IsTreeMode),
                () => getToolbarCommand()?.ToggleCsvTableMode(),
                toggleLeftPanelAsync,
                toggleRightPanelAsync,
                togglePreviewWidth,
                () => documentFacade.OpenEmptyTab(),
                () => getToolbarCommand()?.SaveActive(),
                () => getToolbarCommand()?.SaveActiveAs(),
                () => getToolbarCommand()?.OpenFile(),
                documentFacade.CloseActiveTab,
                () => getToolbarCommand()?.Print(),
                shellFacade.FocusSearchPanel,
                editorFacade.UpdateLivePreview,
                editorFacade.UpdateLanguageUi,
                editorFacade.SchedulePreview,
                shellFacade.UpdateWindowTitle,
                documentFacade.LoadFileIntoTabAsync,
                workspace.GetSearchRoot,
                workspace.GetLargeFileThresholdBytes,
                workspace.RefreshGitStatusUiAsync,
                shellFacade.GetLocalizedString);

            var foundation = MainWindowEditorFoundationComposition.Compose(
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                shell,
                preview.Controllers.EditorLineNavigation,
                initialEditorLineWarmupCount,
                tabId => state.EditorSessions.TryGetValue(tabId, out var session) ? session : null,
                callbacks);

            return new MainWindowEditorModule(foundation);
        }

        public void ComposeRuntime(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowDocumentServices documentServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowEditorRuntimeModuleDependencies dependencies,
            int initialEditorLineWarmupCount,
            IMainWindowShellFacade shellFacade,
            IMainWindowEditorFacade editorFacade,
            IMainWindowDocumentFacade documentFacade,
            IMainWindowPreviewFacade previewFacade,
            Func<MainWindowToolbarCommandController?> getToolbarCommand,
            Action<EditorSettings> applyEditorSurfaceBackground)
        {
            if (_runtime != null)
            {
                throw new InvalidOperationException("Editor runtime has already been composed.");
            }

            var preview = dependencies.Preview.Controllers;
            var callbacks = new MainWindowEditorRuntimeCallbacks(
                editorFacade.SchedulePreview,
                editorFacade.UpdateLanguageUi,
                tab => SyncEditsToOtherTabsAsync(tab),
                dependencies.Shell.SaveSidebarVisibilitySettingsAsync,
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
                () => getToolbarCommand()?.LivePreviewEnabled == true,
                tab => getToolbarCommand()?.SyncCsvTableMode(tab),
                shellFacade.GetCurrentElementTheme,
                documentFacade.SaveTabAsync,
                previewFacade.GetPreviewBaseHref,
                shellFacade.GetLocalizedString,
                applyEditorSurfaceBackground,
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
                (_, args) => dependencies.Documents.CloseRequested(args));

            _runtime = MainWindowEditorRuntimeComposition.Compose(
                window,
                ui,
                commonServices,
                documentServices,
                workspaceServices,
                editorServices,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                dependencies.Shell,
                Foundation.TabDirtyState,
                preview.LivePreview,
                preview.PdfViewer,
                preview.OfficeDocumentViewer,
                preview.NotebookViewer,
                preview.WebViewShortcut,
                preview.EditorWebViewInitialization,
                preview.EditorLineNavigation,
                Foundation.EditorBridgeShortcut,
                preview.EditorLinkNavigation,
                Foundation.ActiveEditorInsertion,
                dependencies.Interaction.TabContextMenu,
                dependencies.Workspace.FavoritesRecent,
                dependencies.Agents.LlmAssistant,
                dependencies.Agents.Agent,
                initialEditorLineWarmupCount,
                callbacks);
        }

        public void CancelActiveSearch() =>
            Foundation.SearchReplace.CancelActiveSearch();

        public Task HandleSearchQueryEnterAsync() =>
            Foundation.SearchReplace.HandleSearchQueryEnterAsync();

        public Task SearchAllFilesAsync() =>
            Foundation.SearchReplace.SearchAllFilesAsync();

        public Task ReplaceAllAsync() =>
            Foundation.SearchReplace.ReplaceAllAsync();

        public Task ReplaceOneAsync(SearchResultItem item) =>
            Foundation.SearchReplace.ReplaceOneAsync(item);

        public Task OpenSearchResultAsync(SearchResultItem item) =>
            Foundation.SearchReplace.OpenSearchResultAsync(item);

        public OpenedTab OpenNewTab(
            string? filePath,
            string content,
            bool isReadOnly,
            string encodingName,
            bool encodingWasAutoDetected,
            ITextModel? textModel,
            bool isEncrypted,
            string? encryptionPassword) =>
            Runtime.EditorTabOpen.OpenNewTab(
                filePath,
                content,
                isReadOnly,
                encodingName,
                encodingWasAutoDetected,
                textModel,
                isEncrypted,
                encryptionPassword);

        public OpenedTab OpenPdfTab(string filePath) =>
            Runtime.EditorTabOpen.OpenPdfTab(filePath);

        public OpenedTab OpenOfficeDocumentTab(string filePath) =>
            Runtime.EditorTabOpen.OpenOfficeDocumentTab(filePath);

        public OpenedTab OpenHexTab(string filePath) =>
            Runtime.EditorTabOpen.OpenHexTab(filePath);

        public OpenedTab OpenNotebookTab(string filePath) =>
            Runtime.EditorTabOpen.OpenNotebookTab(filePath);

        public Task OpenNotebookSourceTabAsync(string filePath) =>
            Runtime.EditorTabOpen.OpenNotebookSourceTabAsync(filePath);

        public Task OpenNotebookViewerTabAsync(string filePath) =>
            Runtime.EditorTabOpen.OpenNotebookViewerTabAsync(filePath);

        public OpenedTab OpenImageTab(string filePath) =>
            Runtime.EditorTabOpen.OpenImageTab(filePath);

        public OpenedTab OpenMediaTab(string filePath) =>
            Runtime.EditorTabOpen.OpenMediaTab(filePath);

        public Task SetHexViewModeAsync(OpenedTab tab, bool enabled) =>
            Runtime.EditorTabOpen.SetHexViewModeAsync(tab, enabled);

        public void ShowLeftSidebarPage(int index) =>
            Runtime.ShellPane.ShowLeftSidebarPage(index);

        public void EnsureLeftPanelVisible() =>
            Runtime.ShellPane.EnsureLeftPanelVisible();

        public void FocusSearchPanel() =>
            Runtime.ShellPane.FocusSearchPanel();

        public void ForgetHexViewState(string tabId) =>
            Runtime.EditorTabOpen.ForgetHexViewState(tabId);

        public void MarkTabDirty(OpenedTab tab, TabViewItem tabItem) =>
            Foundation.TabDirtyState.MarkTabDirty(tab, tabItem);

        public Task ReloadWithEncodingAsync(OpenedTab tab, string encodingName) =>
            Foundation.TabReload.ReloadWithEncodingAsync(tab, encodingName);

        public Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true) =>
            Foundation.SplitImeSync.SyncEditsToOtherTabsAsync(sourceTab, updateUi);

        public void QueueTabSelectionChanged(TabView tabView, TabViewItem tabItem) =>
            Runtime.TabSelection.QueueChanged(tabView, tabItem);
    }
}
