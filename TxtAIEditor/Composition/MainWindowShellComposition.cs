using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;
using WinRT.Interop;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowShellCompositionCallbacks(
        Func<Task> SaveUiLayoutSettingsAsync,
        Action ToggleTerminalRequested,
        Func<ElementTheme> GetCurrentElementTheme,
        Func<string, string, string> GetLocalizedString,
        Action UpdateWindowTitle,
        Action<bool> ApplyLeftSidebarVisibility,
        Action<bool> ApplyPreviewVisibility,
        Func<OpenedTab, string, Task> ReloadTabWithEncodingAsync,
        Action<OpenedTab> MarkTabDirtyFromStatusBar,
        Func<string, int, Task> PerformLineNavigationAsync,
        Action<OpenedTab> UpdateLivePreview);

    internal sealed record MainWindowShellControllers(
        ShellPanelLayoutService ShellPanelLayout,
        TabNavigationController TabNavigation,
        TerminalShortcutService TerminalShortcut,
        WindowDialogController Dialog,
        WindowTitleController WindowTitle,
        TabEncryptionController TabEncryption,
        StickyNoteModeController StickyNoteMode,
        StatusBarController StatusBar);

    internal static class MainWindowShellComposition
    {
        public static MainWindowShellControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowServices services,
            MainWindowViewModel viewModel,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<string, EditorDocumentSession?> getEditorSession,
            MainWindowShellCompositionCallbacks callbacks)
        {
            var shellPanelLayout = new ShellPanelLayoutService(
                ui.MainWorkGrid,
                ui.ExplorerColumn,
                ui.PreviewColumn,
                ui.LeftSplitter,
                ui.RightSplitter,
                ui.LeftSidebar,
                ui.PreviewGrid);
            shellPanelLayout.PanelWidthsChanged += async (_, _) => await callbacks.SaveUiLayoutSettingsAsync();

            var tabNavigation = new TabNavigationController(
                viewModel,
                ui.EditorWorkspace,
                ui.EditorTabView,
                ui.EditorTabView2);

            var terminalShortcut = new TerminalShortcutService(() => WindowNative.GetWindowHandle(window));
            terminalShortcut.ToggleRequested += (_, _) => callbacks.ToggleTerminalRequested();

            var dialog = new WindowDialogController(
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                callbacks.GetLocalizedString);

            var windowTitle = new WindowTitleController(
                window,
                ui.AppTitleTextBlock,
                tabNavigation.GetActiveTab);

            var tabEncryption = new TabEncryptionController(
                callbacks.GetLocalizedString,
                dialog.WaitForDialogXamlRootAsync,
                callbacks.GetCurrentElementTheme,
                callbacks.UpdateWindowTitle,
                dialog.ShowErrorMessage);

            var stickyNoteMode = new StickyNoteModeController(
                window,
                ui.AppTitleBar,
                ui.TitleBarRow,
                ui.EditorWorkspace.StickyNoteBarControl,
                ui.TopToolbar,
                ui.MarkdownToolbar,
                ui.StatusBar,
                shellPanelLayout,
                ui.StatusBar.LeftPanelToggleButton,
                services.StickyNoteService,
                callbacks.ApplyLeftSidebarVisibility,
                callbacks.ApplyPreviewVisibility);

            var statusBar = new StatusBarController(
                ui.StatusBar,
                tabNavigation.GetActiveTab,
                tab => tabNavigation.GetActiveTab() == tab,
                getEditorSession,
                services.LanguageDetectionService,
                tabBridges,
                callbacks.GetLocalizedString,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                callbacks.ReloadTabWithEncodingAsync,
                callbacks.MarkTabDirtyFromStatusBar,
                callbacks.PerformLineNavigationAsync,
                callbacks.UpdateLivePreview);

            return new MainWindowShellControllers(
                shellPanelLayout,
                tabNavigation,
                terminalShortcut,
                dialog,
                windowTitle,
                tabEncryption,
                stickyNoteMode,
                statusBar);
        }
    }
}
