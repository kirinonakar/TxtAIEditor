using TxtAIEditor.Controls;

namespace TxtAIEditor.Composition
{
    internal sealed record ShellControllers(
        MainWindowShellControllers Core,
        MainWindowInteractionControllers Interaction);

    internal sealed record EditorControllers(
        MainWindowEditorFoundationControllers Foundation,
        MainWindowEditorRuntimeControllers Runtime);

    internal sealed record DocumentControllers(
        TabSaveController TabSave,
        AutoSaveController AutoSave,
        TabCloseController TabClose,
        TabMoveController TabMove,
        WindowCloseController WindowClose)
    {
        public static DocumentControllers From(MainWindowDocumentCommandControllers controllers) =>
            new(
                controllers.TabSave,
                controllers.AutoSave,
                controllers.TabClose,
                controllers.TabMove,
                controllers.WindowClose);
    }

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
        ShellControllers Shell,
        EditorControllers Editor,
        DocumentControllers Documents,
        MainWindowPreviewModule Preview,
        MainWindowAgentModuleFacade Agents,
        MainWindowWorkspaceModule Workspace,
        LifecycleControllers Lifecycle);
}
