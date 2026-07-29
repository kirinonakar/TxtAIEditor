using System;
using System.Threading.Tasks;
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
        private MainWindowEditorModule? _editor;
        private MainWindowStartupControllers? _startup;

        public ShellPaneController ShellPane =>
            Require(_editor, nameof(MainWindowEditorModule)).Runtime.ShellPane;

        public MainWindowSettingsController Settings =>
            Require(_startup, nameof(MainWindowStartupComposition)).Settings;

        public MainWindowToolbarCommandController? ToolbarCommand =>
            _startup?.ToolbarCommand;

        public void ApplyPreviewVisibility(bool show, bool startupInitializationComplete) =>
            MainWindowLayoutOperations.ApplyPreviewVisibility(
                show,
                ShellPane,
                startupInitializationComplete,
                Require(_preview, nameof(MainWindowPreviewModule)).Composition.LivePreview);

        public void ToggleExplorerTreeMode() =>
            Require(_workspace, nameof(MainWindowWorkspaceModule)).ToggleExplorerTreeMode();

        public Task NavigateExplorerToFolderAndRevealAsync(string folderPath) =>
            Require(_workspace, nameof(MainWindowWorkspaceModule))
                .NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel: true);

        public void Bind(MainWindowPreviewModule preview) =>
            _preview = BindOnce(_preview, preview, nameof(MainWindowPreviewModule));

        public void Bind(MainWindowWorkspaceModule workspace) =>
            _workspace = BindOnce(_workspace, workspace, nameof(MainWindowWorkspaceModule));

        public void Bind(MainWindowEditorModule editor) =>
            _editor = BindOnce(_editor, editor, nameof(MainWindowEditorModule));

        public void Bind(MainWindowStartupControllers startup) =>
            _startup = BindOnce(_startup, startup, nameof(MainWindowStartupComposition));

        public void ValidateComplete()
        {
            _ = Require(_preview, nameof(MainWindowPreviewModule));
            _ = Require(_workspace, nameof(MainWindowWorkspaceModule));
            _ = Require(_editor, nameof(MainWindowEditorModule));
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
