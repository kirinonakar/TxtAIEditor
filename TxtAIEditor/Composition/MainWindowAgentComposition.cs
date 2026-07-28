using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowAgentCompositionCallbacks(
        Func<ExplorerItem?> GetSelectedExplorerItem,
        Func<string> GetCurrentFolderPath,
        Func<string> GetCurrentRepoPath,
        Action<string> LoadDirectoryRoot,
        Action QueueGitStatusRefresh,
        Func<IReadOnlyList<AgentFileEditPreview>> GetAgentSessionEdits,
        Func<string, Task<AgentOpenFileResult>> LoadFileIntoTabForAgentAsync,
        Func<string, Task> NavigateExplorerToFolderAndRevealAsync,
        Func<string, OpenedTab> OpenGeneratedTab,
        Func<OpenedTab, Task<bool>> SaveTabAsync,
        Action<object> InitializePickerWindow,
        Action RefreshCurrentExplorerFolder,
        Func<string, string, string> GetLocalizedString,
        Action UpdateWindowTitle) : IAgentFileWorkflowHost
    {
        IReadOnlyList<AgentFileEditPreview> IAgentFileWorkflowHost.GetSessionEdits() => GetAgentSessionEdits();

        ExplorerItem? IAgentFileWorkflowHost.GetSelectedExplorerItem() => GetSelectedExplorerItem();

        string IAgentFileWorkflowHost.GetCurrentFolderPath() => GetCurrentFolderPath();

        string IAgentFileWorkflowHost.GetCurrentRepoPath() => GetCurrentRepoPath();

        void IAgentFileWorkflowHost.LoadDirectoryRoot(string folderPath) => LoadDirectoryRoot(folderPath);

        void IAgentFileWorkflowHost.QueueGitStatusRefresh() => QueueGitStatusRefresh();

        string IAgentFileWorkflowHost.GetLocalizedString(string key, string fallback) =>
            GetLocalizedString(key, fallback);
    }

    internal sealed record MainWindowAgentModuleDependencies(
        MainWindowShellModule Shell,
        MainWindowEditorModule Editor,
        ITabCloseCommands Documents,
        MainWindowPreviewModule Preview);

    internal sealed record MainWindowAgentModuleFacade(
        LlmAssistantController LlmAssistant,
        AgentController Agent);

    internal static class MainWindowAgentComposition
    {
        public static MainWindowAgentModuleFacade Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowAgentServices agentServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowAgentModuleDependencies dependencies,
            MainWindowWorkspaceControllers workspace,
            IMainWindowShellFacade shellFacade,
            IMainWindowDocumentFacade documentFacade,
            IMainWindowAgentFacade agentFacade,
            IMainWindowWorkspaceFacade workspaceFacade)
        {
            var explorerNavigation = workspace.ExplorerNavigation;
            return Compose(
                window,
                ui,
                commonServices,
                workspaceServices,
                editorServices,
                agentServices,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                dependencies,
                new MainWindowAgentCompositionCallbacks(
                    workspaceFacade.GetSelectedExplorerItem,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    explorerNavigation.LoadDirectoryRoot,
                    workspace.GitStatusRefresh.QueueRefresh,
                    agentFacade.GetAgentSessionEdits,
                    agentFacade.LoadFileIntoTabForAgentAsync,
                    folderPath => explorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel: true),
                    documentFacade.OpenGeneratedTab,
                    documentFacade.SaveTabAsync,
                    shellFacade.InitializePickerWindow,
                    explorerNavigation.RefreshCurrentFolder,
                    shellFacade.GetLocalizedString,
                    shellFacade.UpdateWindowTitle));
        }

        public static MainWindowAgentModuleFacade Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowAgentServices agentServices,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            MainWindowAgentModuleDependencies dependencies,
            MainWindowAgentCompositionCallbacks callbacks)
        {
            var shell = dependencies.Shell.Composition;
            var editor = dependencies.Editor.Foundation;
            var documents = dependencies.Documents;
            var preview = dependencies.Preview.Controllers;

            var llmAssistant = new LlmAssistantController(
                agentServices.LlmService,
                commonServices.SettingsService,
                commonServices.LanguageDetectionService,
                ui.PreviewGrid,
                () => ui.RootElement.XamlRoot,
                shell.TabNavigation.GetActiveTab,
                editor.TabTextContext.GetText,
                editor.ActiveEditorInsertion.InsertTextAsync,
                (title, content) => CreateGeneratedTab(
                    title,
                    content,
                    ensureUniqueUntitledName: false,
                    viewModel,
                    commonServices,
                    editorSessions,
                    editor.TabDirtyState,
                    callbacks),
                shell.Dialog.ShowErrorMessage,
                callbacks.GetLocalizedString,
                callbacks.InitializePickerWindow,
                beforeDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.ResumeNativeWindows(); },
                onFileSaved: () =>
                {
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        callbacks.RefreshCurrentExplorerFolder();
                    });
                });

            var agentFileWorkflow = new AgentFileWorkflowController(
                viewModel,
                ui.EditorTabView,
                ui.EditorTabView2,
                tabBridges,
                editorSessions,
                documents,
                editor.SearchReplaceTabSync,
                preview.CompareTab,
                workspaceServices.RemoteWorkspaceService,
                callbacks);

            var agentFileTools = new AgentFileToolService(
                agentFileWorkflow.GetWorkspaceRoot,
                callbacks.GetLocalizedString)
            {
                FileDisplayPathProvider = fullPath =>
                    workspaceServices.RemoteWorkspaceService.TryGetVirtualPath(
                        fullPath,
                        out string remotePath)
                        ? workspaceServices.RemoteWorkspaceService.GetDisplayPath(remotePath)
                        : null
            };

            var agent = new AgentController(
                agentServices.LlmService,
                commonServices.SettingsService,
                agentServices.CredentialService,
                ui.PreviewGrid.AgentPane,
                shell.TabNavigation.GetActiveTab,
                () => viewModel.Tabs.ToList(),
                editor.TabTextContext.GetText,
                editor.ActiveEditorInsertion.InsertTextAsync,
                (title, content) => CreateGeneratedTab(
                    title,
                    content,
                    ensureUniqueUntitledName: true,
                    viewModel,
                    commonServices,
                    editorSessions,
                    editor.TabDirtyState,
                    callbacks),
                shell.Dialog.ShowErrorMessage,
                callbacks.GetLocalizedString,
                agentFileTools,
                editorServices.PdfTextExtractionService,
                callbacks.InitializePickerWindow,
                path => workspaceServices.GitService.FindRepositoryRoot(path) != null,
                agentFileWorkflow.OpenDiffViewAsync,
                agentFileWorkflow.HandleFileModifiedAsync,
                openFileInEditorAsync: callbacks.LoadFileIntoTabForAgentAsync,
                beforeDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.SuspendNativeWindows(); },
                afterDialog: () => { if (ui.EditorWorkspace.IsTerminalVisible) ui.TerminalPane.ResumeNativeWindows(); },
                revertTabOrFileAsync: agentFileWorkflow.RevertTabOrFileAsync,
                closeTabById: agentFileWorkflow.CloseTabById,
                navigateToFolderAsync: callbacks.NavigateExplorerToFolderAndRevealAsync,
                saveTabAsync: async (tab, targetPath) =>
                {
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        tab.FilePath = targetPath;
                        tab.Title = Path.GetFileName(targetPath);
                        tab.Language = commonServices.LanguageDetectionService.GetEditorLanguageName(targetPath);
                    }

                    return await callbacks.SaveTabAsync(tab);
                },
                editTabAsync: async (tab, newContent) =>
                {
                    tab.ContentPreview = newContent;
                    EditorDocumentSession? session = null;
                    if (editorSessions.TryGetValue(tab.Id, out session))
                    {
                        session.UpdateContentFromSync(newContent, markUnsaved: true);
                    }

                    if (tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        await bridgeGroup.Bridge.SetTextAsync(
                            newContent,
                            shouldFocus: false,
                            session?.DocumentId,
                            session?.DocumentVersion,
                            tab.Id);
                        session?.MarkViewSynchronized(session.DocumentVersion);
                    }

                    editor.TabDirtyState.MarkTabDirty(tab);
                    callbacks.UpdateWindowTitle();
                    return true;
                },
                beginStreamIntoActiveEditorAsync: editor.ActiveEditorInsertion.BeginStreamAsync,
                streamTextIntoActiveEditorAsync: editor.ActiveEditorInsertion.InsertStreamTextAsync,
                endStreamIntoActiveEditorAsync: editor.ActiveEditorInsertion.EndStreamAsync);

            window.Closed += (_, _) => agent.CloseMcpSessions();

            return new MainWindowAgentModuleFacade(llmAssistant, agent);
        }

        private static OpenedTab CreateGeneratedTab(
            string? title,
            string content,
            bool ensureUniqueUntitledName,
            MainWindowViewModel viewModel,
            MainWindowCommonServices commonServices,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabDirtyStateController tabDirtyState,
            MainWindowAgentCompositionCallbacks callbacks)
        {
            string uniqueTitle = string.IsNullOrWhiteSpace(title)
                ? callbacks.GetLocalizedString("UntitledNewTab", "제목 없음")
                : title;

            if (ensureUniqueUntitledName && !string.IsNullOrWhiteSpace(title))
            {
                string extension = string.Empty;
                string baseName = title;
                int lastDot = title.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    baseName = title.Substring(0, lastDot);
                    extension = title.Substring(lastDot);
                }

                int counter = 1;
                while (viewModel.Tabs.Any(t =>
                    string.IsNullOrEmpty(t.FilePath) &&
                    string.Equals(t.Title, uniqueTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    counter++;
                    uniqueTitle = $"{baseName} ({counter}){extension}";
                }
            }

            var tab = callbacks.OpenGeneratedTab(content);
            tab.Title = uniqueTitle;
            tab.Language = !string.IsNullOrWhiteSpace(title)
                ? commonServices.LanguageDetectionService.GetEditorLanguageName(title)
                : "plaintext";
            if (editorSessions.TryGetValue(tab.Id, out var session))
            {
                session.SetSavedBaseline(new[] { string.Empty });
                session.MarkUnsavedState();
            }
            tabDirtyState.MarkTabDirty(tab);
            callbacks.UpdateWindowTitle();
            return tab;
        }
    }
}
