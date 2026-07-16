using System;
using TxtAIEditor.Controls;

namespace TxtAIEditor.Composition
{
    /// <summary>
    /// Holds the few module references that participate in deferred composition callbacks.
    /// Each module is bound once, immediately after its factory completes.
    /// </summary>
    internal sealed class MainWindowModuleBindings
    {
        private MainWindowPreviewModule? _preview;
        private MainWindowWorkspaceModule? _workspace;
        private MainWindowEditorRuntimeControllers? _editorRuntime;
        private MainWindowStartupControllers? _startup;

        public LivePreviewController LivePreview =>
            Require(_preview, nameof(MainWindowPreviewModule)).Controllers.LivePreview;

        public ExplorerNavigationController ExplorerNavigation =>
            Require(_workspace, nameof(MainWindowWorkspaceModule)).Controllers.ExplorerNavigation;

        public ShellPaneController ShellPane =>
            Require(_editorRuntime, nameof(MainWindowEditorRuntimeComposition)).ShellPane;

        public MainWindowSettingsController Settings =>
            Require(_startup, nameof(MainWindowStartupComposition)).Settings;

        public MainWindowToolbarCommandController? ToolbarCommand =>
            _startup?.ToolbarCommand;

        public void Bind(MainWindowPreviewModule preview) =>
            _preview = BindOnce(_preview, preview, nameof(MainWindowPreviewModule));

        public void Bind(MainWindowWorkspaceModule workspace) =>
            _workspace = BindOnce(_workspace, workspace, nameof(MainWindowWorkspaceModule));

        public void Bind(MainWindowEditorRuntimeControllers editorRuntime) =>
            _editorRuntime = BindOnce(_editorRuntime, editorRuntime, nameof(MainWindowEditorRuntimeComposition));

        public void Bind(MainWindowStartupControllers startup) =>
            _startup = BindOnce(_startup, startup, nameof(MainWindowStartupComposition));

        public void ValidateComplete()
        {
            _ = Require(_preview, nameof(MainWindowPreviewModule));
            _ = Require(_workspace, nameof(MainWindowWorkspaceModule));
            _ = Require(_editorRuntime, nameof(MainWindowEditorRuntimeComposition));
            _ = Require(_startup, nameof(MainWindowStartupComposition));
        }

        private static T BindOnce<T>(T? current, T value, string moduleName)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(value);
            if (current != null)
            {
                throw new InvalidOperationException($"{moduleName} has already been bound.");
            }

            return value;
        }

        private static T Require<T>(T? value, string moduleName)
            where T : class =>
            value ?? throw new InvalidOperationException($"{moduleName} has not been composed yet.");
    }
}
