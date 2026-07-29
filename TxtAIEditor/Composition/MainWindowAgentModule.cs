using System.Collections.Generic;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed class MainWindowAgentModule : IAgentUiCommands
    {
        private MainWindowAgentModule(MainWindowAgentControllers controllers)
        {
            Composition = controllers;
        }

        internal MainWindowAgentControllers Composition { get; }

        public static MainWindowAgentModule Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowWorkspaceServices workspaceServices,
            MainWindowEditorServices editorServices,
            MainWindowAgentServices agentServices,
            MainWindowViewModel viewModel,
            MainWindowState state,
            MainWindowAgentModuleDependencies dependencies,
            MainWindowWorkspaceModule workspaceModule,
            IMainWindowShellFacade shellFacade,
            IMainWindowDocumentFacade documentFacade,
            IMainWindowAgentFacade agentFacade,
            IMainWindowWorkspaceFacade workspaceFacade)
        {
            return new MainWindowAgentModule(
                MainWindowAgentComposition.Compose(
                    window,
                    ui,
                    commonServices,
                    workspaceServices,
                    editorServices,
                    agentServices,
                    viewModel,
                    state,
                    dependencies,
                    workspaceModule,
                    shellFacade,
                    documentFacade,
                    agentFacade,
                    workspaceFacade));
        }

        public void SetSelectionContext(
            string selectedText,
            OpenedTab tab,
            int startLine,
            int endLine)
        {
            Composition.LlmAssistant.SetSelectionText(selectedText);
            Composition.Agent.SetSelectionText(selectedText, tab, startLine, endLine);
        }

        public void ClearSelectionContext()
        {
            Composition.LlmAssistant.ClearSelection();
            Composition.Agent.ClearSelection();
        }

        public IReadOnlyList<AgentFileEditPreview> GetSessionEdits() =>
            Composition.Agent.SessionEdits;

        public void ApplyRuntimeSettings()
        {
            Composition.LlmAssistant.UpdateModelDisplay();
            Composition.Agent.UpdateModelDisplay(true);
            Composition.Agent.UpdateContextStats();
        }

        public void RefreshLocalizedModelDisplay()
        {
            Composition.LlmAssistant.UpdateModelDisplay();
            Composition.Agent.UpdateModelDisplay();
        }

        public void SyncSettingsAfterLoad()
        {
            Composition.Agent.UpdateModelDisplay(true);
            Composition.Agent.UpdateContextStats();
        }
    }
}
