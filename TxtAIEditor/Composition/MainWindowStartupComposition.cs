using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowStartupCallbacks(
        Func<string> GetCurrentRepoPath,
        Func<string> GetCurrentFolderPath,
        Func<string, bool, Task> NavigateExplorerToFolderAsync,
        Action<bool> SetExplorerTreeMode,
        Func<string, Task> LoadFileIntoTabAsync,
        Action OpenNewTab,
        Action<bool> ApplyLeftSidebarVisibility,
        Action<bool> ApplyPreviewVisibility,
        Action<EditorSettings> ApplySavedPanelWidths,
        Action LocalizeUi,
        Action SyncAgentSettingsAfterLoad,
        Func<Task> RefreshGitStatusUiAsync,
        Action UpdateAutoSaveStatus,
        Func<string, string, string> GetLocalizedString,
        Func<ElementTheme> GetCurrentElementTheme,
        Action<object> InitializePickerWindow,
        Action<string, string> OpenTextInEditor,
        Func<OpenedTab, string> GetPreviewBaseHref);

    internal sealed record MainWindowStartupControllers(
        MainWindowLifecycleController Lifecycle,
        MainWindowSettingsController Settings,
        MainWindowStartupController Startup,
        MainWindowShellInteractionController ShellInteraction,
        MainWindowToolbarCommandController ToolbarCommand);

    internal static class MainWindowStartupComposition
    {
        public static MainWindowStartupControllers Compose(
            MainWindow window,
            MainWindowUiRefs ui,
            MainWindowCommonServices commonServices,
            MainWindowShellServices shellServices,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            MainWindowShellModule shellModule,
            MainWindowEditorModule editorModule,
            FunctionKeyShortcutService functionKeyShortcut,
            IAutoSaveLifecycle autoSaveLifecycle,
            DispatcherTimer gitAutoRefreshTimer,
            LivePreviewController livePreview,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            JupyterNotebookViewerController notebookViewer,
            LlmAssistantController llmAssistant,
            AgentController agent,
            SnippetsController snippets,
            FavoritesRecentController favoritesRecent,
            FileOpenDropController fileOpenDrop,
            RootKeyboardShortcutController rootKeyboardShortcut,
            ITabSaveCommands tabSaveCommands,
            TerminalPanelController terminalPanel,
            CompareTabController compareTab,
            MainWindowStartupCallbacks callbacks)
        {
            var shell = shellModule.Composition;
            var terminalShortcut = shell.TerminalShortcut;
            var statusBar = shell.StatusBar;
            var tabNavigation = shell.TabNavigation;
            var shellPanelLayout = shell.ShellPanelLayout;
            var stickyNoteMode = shell.StickyNoteMode;
            var dialog = shell.Dialog;
            var tabDirtyState = editorModule.Foundation.TabDirtyState;
            var shellPane = editorModule.Runtime.ShellPane;

            var lifecycle = new MainWindowLifecycleController(
                window,
                ui.AppTitleBar,
                terminalShortcut,
                functionKeyShortcut,
                autoSaveLifecycle,
                gitAutoRefreshTimer,
                ui.EditorWorkspace,
                tabBridges,
                livePreview);

            var settings = new MainWindowSettingsController(
                window.AppWindow,
                () => window.Content as FrameworkElement,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                commonServices.SettingsService,
                shellServices.SettingsDialogService,
                shellServices.UiPersonalizationService,
                commonServices.LocalizationService,
                ui.TopToolbar,
                ui.MarkdownToolbar,
                ui.MarkdownToolbarHost,
                ui.EditorWorkspace,
                ui.LeftSidebar,
                ui.StatusBar,
                ui.PreviewGrid,
                ui.EditorWorkspace.StickyNoteBarControl,
                ui.LeftSplitter,
                ui.RightSplitter,
                tabBridges,
                tabDirtyState,
                pdfViewer,
                officeDocumentViewer,
                statusBar,
                livePreview,
                llmAssistant,
                agent,
                tabNavigation.GetActiveTab,
                callbacks.GetCurrentFolderPath,
                callbacks.GetLocalizedString,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows(),
                callbacks.ApplyPreviewVisibility,
                callbacks.UpdateAutoSaveStatus,
                lifecycle.CleanupBeforeRestart,
                ui.EditorWorkspace.RefreshSplitters,
                callbacks.InitializePickerWindow,
                callbacks.OpenTextInEditor);

            var startup = new MainWindowStartupController(
                window,
                commonServices.SettingsService,
                viewModel,
                ui.EditorWorkspace,
                ui.TopToolbar,
                ui.StatusBar.LeftPanelToggleButton,
                ui.StatusBar.RightPanelToggleButton,
                ui.MarkdownToolbar,
                ui.PreviewGrid,
                ui.PreviewGrid.PreviewMode,
                gitAutoRefreshTimer,
                livePreview,
                snippets,
                favoritesRecent,
                callbacks.GetCurrentRepoPath,
                callbacks.NavigateExplorerToFolderAsync,
                callbacks.SetExplorerTreeMode,
                callbacks.LoadFileIntoTabAsync,
                callbacks.OpenNewTab,
                callbacks.ApplyLeftSidebarVisibility,
                callbacks.ApplyPreviewVisibility,
                callbacks.ApplySavedPanelWidths,
                settings.ApplyUiPersonalization,
                callbacks.LocalizeUi,
                settings.ApplyToolbarSettings,
                callbacks.SyncAgentSettingsAfterLoad,
                callbacks.RefreshGitStatusUiAsync,
                callbacks.UpdateAutoSaveStatus,
                callbacks.GetLocalizedString,
                dialog.ShowErrorMessage);

            ui.PreviewGrid.SelectedTabChanged += async (_, selectedTabKey) =>
            {
                EditorSettings currentSettings = commonServices.SettingsService.CurrentSettings;
                currentSettings.RightSidebarSelectedTab = selectedTabKey;
                await commonServices.SettingsService.SaveSettingsAsync(currentSettings);
            };

            var shellInteraction = new MainWindowShellInteractionController(
                ui.RootGrid,
                ui.DragOverlay,
                ui.LeftSplitter,
                ui.RightSplitter,
                fileOpenDrop,
                shellPanelLayout,
                rootKeyboardShortcut);
            ui.TerminalPane.FunctionKeyShortcutPressed += rootKeyboardShortcut.HandleFunctionKeyShortcut;

            if (window.Content is FrameworkElement rootElement)
            {
                rootElement.DataContext = viewModel;
            }

            ui.LeftSidebar.FileList.ItemsSource = viewModel.ExplorerItems;
            var groupedSource = new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = viewModel.SearchResultsGrouped
            };
            ui.LeftSidebar.SearchResults.ItemsSource = groupedSource.View;
            statusBar.InitializeEncodings(TextEncodingService.SupportedEncodingNames, "UTF-8");

            var toolbarCommand = new MainWindowToolbarCommandController(
                window,
                ui.TopToolbar,
                ui.EditorTabView,
                ui.LeftSidebar.SearchQuery,
                viewModel,
                commonServices.SettingsService,
                fileOpenDrop,
                tabNavigation,
                tabSaveCommands,
                terminalPanel,
                settings,
                stickyNoteMode,
                pdfViewer,
                officeDocumentViewer,
                notebookViewer,
                shellPane,
                shellServices.CompareSelectionDialogService,
                compareTab,
                dialog,
                tabBridges,
                editorSessions,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                callbacks.GetLocalizedString,
                callbacks.GetPreviewBaseHref,
                () => ui.EditorWorkspace.IsTerminalVisible,
                () => ui.TerminalPane.SuspendNativeWindows(),
                () => ui.TerminalPane.ResumeNativeWindows());

            return new MainWindowStartupControllers(
                lifecycle,
                settings,
                startup,
                shellInteraction,
                toolbarCommand);
        }
    }
}
