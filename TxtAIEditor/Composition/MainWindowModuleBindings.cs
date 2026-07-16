using System;
using Microsoft.UI.Xaml;
using TxtAIEditor.Controls;

namespace TxtAIEditor.Composition
{
    /// <summary>
    /// Holds the few module references that participate in deferred composition callbacks.
    /// Each module is bound once, immediately after its factory completes.
    /// </summary>
    internal sealed class MainWindowModuleBindings
    {
        private MainWindowPreviewControllers? _preview;
        private MainWindowWorkspaceControllers? _workspace;
        private MainWindowEditorRuntimeControllers? _editorRuntime;
        private MainWindowStartupControllers? _startup;

        public LivePreviewController LivePreview =>
            Require(_preview, nameof(MainWindowPreviewComposition)).LivePreview;

        public ExplorerNavigationController ExplorerNavigation =>
            Require(_workspace, nameof(MainWindowWorkspaceComposition)).ExplorerNavigation;

        public GitStatusRefreshController GitStatusRefresh =>
            Require(_workspace, nameof(MainWindowWorkspaceComposition)).GitStatusRefresh;

        public DispatcherTimer GitAutoRefreshTimer =>
            Require(_workspace, nameof(MainWindowWorkspaceComposition)).GitAutoRefreshTimer;

        public ShellPaneController ShellPane =>
            Require(_editorRuntime, nameof(MainWindowEditorRuntimeComposition)).ShellPane;

        public MainWindowSettingsController Settings =>
            Require(_startup, nameof(MainWindowStartupComposition)).Settings;

        public MainWindowToolbarCommandController? ToolbarCommand =>
            _startup?.ToolbarCommand;

        public void Bind(MainWindowPreviewControllers preview) =>
            _preview = BindOnce(_preview, preview, nameof(MainWindowPreviewComposition));

        public void Bind(MainWindowWorkspaceControllers workspace) =>
            _workspace = BindOnce(_workspace, workspace, nameof(MainWindowWorkspaceComposition));

        public void Bind(MainWindowEditorRuntimeControllers editorRuntime) =>
            _editorRuntime = BindOnce(_editorRuntime, editorRuntime, nameof(MainWindowEditorRuntimeComposition));

        public void Bind(MainWindowStartupControllers startup) =>
            _startup = BindOnce(_startup, startup, nameof(MainWindowStartupComposition));

        public void ValidateComplete()
        {
            _ = Require(_preview, nameof(MainWindowPreviewComposition));
            _ = Require(_workspace, nameof(MainWindowWorkspaceComposition));
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
