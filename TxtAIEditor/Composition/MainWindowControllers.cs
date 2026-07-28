using TxtAIEditor.Controls;

namespace TxtAIEditor.Composition
{
    internal sealed record EditorControllers(
        MainWindowEditorFoundationControllers Foundation,
        MainWindowEditorRuntimeControllers Runtime);

    internal sealed record LifecycleControllers(
        MainWindowLifecycleController Window,
        MainWindowSettingsController Settings,
        MainWindowStartupController Startup,
        MainWindowShellInteractionController ShellInteraction,
        MainWindowToolbarCommandController ToolbarCommand)
    {
        public static LifecycleControllers From(MainWindowStartupControllers controllers) =>
            new(
                controllers.Lifecycle,
                controllers.Settings,
                controllers.Startup,
                controllers.ShellInteraction,
                controllers.ToolbarCommand);
    }

    internal sealed record MainWindowControllers(
        MainWindowShellModule Shell,
        EditorControllers Editor,
        MainWindowDocumentModule Documents,
        MainWindowPreviewModule Preview,
        MainWindowAgentModuleFacade Agents,
        MainWindowWorkspaceModule Workspace,
        LifecycleControllers Lifecycle);
}
