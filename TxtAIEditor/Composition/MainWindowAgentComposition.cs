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
        MainWindowShellControllers Shell,
        MainWindowEditorFoundationControllers Editor,
        MainWindowDocumentCommandControllers Documents,
        MainWindowPreviewModule Preview);

    internal sealed record MainWindowAgentModuleFacade(
        LlmAssistantController LlmAssistant,
        AgentController Agent);

    internal static class MainWindowAgentComposition
    {
        public static MainWindowAgentModuleFacade Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowAgentModuleDependencies dependencies,
            MainWindowWorkspaceControllers workspace,
            MainWindowCompositionRootCallbacks callbacks)
        {
            var explorerNavigation = workspace.ExplorerNavigation;
            return Compose(
                window,
                ui,
                services,
                viewModel,
                state.TabBridges,
                state.EditorSessions,
                dependencies,
                new MainWindowAgentCompositionCallbacks(
                    callbacks.GetSelectedExplorerItem,
                    () => state.CurrentFolderPath,
                    () => state.CurrentRepoPath,
                    explorerNavigation.LoadDirectoryRoot,
                    workspace.GitStatusRefresh.QueueRefresh,
                    callbacks.GetAgentSessionEdits,
                    callbacks.LoadFileIntoTabForAgentAsync,
                    folderPath => explorerNavigation.NavigateToFolderAsync(folderPath, revealInLeftPanel: true),
                    callbacks.OpenGeneratedTab,
                    callbacks.SaveTabAsync,
                    callbacks.InitializePickerWindow,
                    explorerNavigation.RefreshCurrentFolder,
                    callbacks.GetLocalizedString,
                    callbacks.UpdateWindowTitle));
        }

        public static MainWindowAgentModuleFacade Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            MainWindowAgentModuleDependencies dependencies,
            MainWindowAgentCompositionCallbacks callbacks)
        {
            var shell = dependencies.Shell;
            var editor = dependencies.Editor;
            var documents = dependencies.Documents;
            var preview = dependencies.Preview.Controllers;

            var llmAssistant = new LlmAssistantController(
                services.LlmService,
                services.SettingsService,
                services.LanguageDetectionService,
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
                    services,
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
                documents.TabClose,
                editor.SearchReplaceTabSync,
                preview.CompareTab,
                callbacks);

            var agent = new AgentController(
                services.LlmService,
                services.SettingsService,
                services.CredentialService,
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
                    services,
                    editor.TabDirtyState,
                    callbacks),
                shell.Dialog.ShowErrorMessage,
                callbacks.GetLocalizedString,
                new AgentFileToolService(agentFileWorkflow.GetWorkspaceRoot, callbacks.GetLocalizedString),
                services.PdfTextExtractionService,
                callbacks.InitializePickerWindow,
                path => services.GitService.FindRepositoryRoot(path) != null,
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
                        tab.Language = services.LanguageDetectionService.GetMonacoLanguageName(targetPath);
                    }

                    return await callbacks.SaveTabAsync(tab);
                },
                editTabAsync: async (tab, newContent) =>
                {
                    tab.Content = newContent;
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

            return new MainWindowAgentModuleFacade(llmAssistant, agent);
        }

        private static OpenedTab CreateGeneratedTab(
            string? title,
            string content,
            bool ensureUniqueUntitledName,
            MainWindowViewModel viewModel,
            MainWindowServices services,
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
                ? services.LanguageDetectionService.GetMonacoLanguageName(title)
                : "plaintext";
            tab.OriginalContent = string.Empty;
            tabDirtyState.MarkTabDirty(tab);
            callbacks.UpdateWindowTitle();
            return tab;
        }
    }
}
