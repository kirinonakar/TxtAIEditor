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
        Func<string, Task> LoadFileIntoTabAsync,
        Action OpenNewTab,
        Action<bool> ApplyLeftSidebarVisibility,
        Action<bool> ApplyPreviewVisibility,
        Action<EditorSettings> ApplySavedPanelWidths,
        Action<EditorSettings> ApplyUiPersonalization,
        Action LocalizeUi,
        Action<EditorSettings> ApplyToolbarSettings,
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
            MainWindowServices services,
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TerminalShortcutService terminalShortcut,
            FunctionKeyShortcutService functionKeyShortcut,
            AutoSaveController autoSave,
            DispatcherTimer gitAutoRefreshTimer,
            SplitImeSyncController splitImeSync,
            LivePreviewController livePreview,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            StatusBarController statusBar,
            LlmAssistantController llmAssistant,
            AgentController agent,
            TabNavigationController tabNavigation,
            SnippetsController snippets,
            FavoritesRecentController favoritesRecent,
            FileOpenDropController fileOpenDrop,
            ShellPanelLayoutService shellPanelLayout,
            RootKeyboardShortcutController rootKeyboardShortcut,
            TabSaveController tabSave,
            TerminalPanelController terminalPanel,
            StickyNoteModeController stickyNoteMode,
            ShellPaneController shellPane,
            CompareTabController compareTab,
            WindowDialogController dialog,
            MainWindowStartupCallbacks callbacks)
        {
            var lifecycle = new MainWindowLifecycleController(
                window,
                ui.AppTitleBar,
                terminalShortcut,
                functionKeyShortcut,
                autoSave,
                gitAutoRefreshTimer,
                splitImeSync,
                ui.EditorWorkspace,
                tabBridges,
                livePreview);

            var settings = new MainWindowSettingsController(
                window.AppWindow,
                () => window.Content as FrameworkElement,
                () => ui.RootElement.XamlRoot,
                callbacks.GetCurrentElementTheme,
                services.SettingsService,
                services.SettingsDialogService,
                services.UiPersonalizationService,
                services.LocalizationService,
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
                services.SettingsService,
                viewModel,
                ui.EditorWorkspace,
                ui.TopToolbar,
                ui.StatusBar.LeftPanelToggleButton,
                ui.StatusBar.RightPanelToggleButton,
                ui.MarkdownToolbar,
                ui.PreviewGrid.PreviewMode,
                gitAutoRefreshTimer,
                livePreview,
                snippets,
                favoritesRecent,
                callbacks.GetCurrentRepoPath,
                callbacks.NavigateExplorerToFolderAsync,
                callbacks.LoadFileIntoTabAsync,
                callbacks.OpenNewTab,
                callbacks.ApplyLeftSidebarVisibility,
                callbacks.ApplyPreviewVisibility,
                callbacks.ApplySavedPanelWidths,
                callbacks.ApplyUiPersonalization,
                callbacks.LocalizeUi,
                callbacks.ApplyToolbarSettings,
                callbacks.SyncAgentSettingsAfterLoad,
                callbacks.RefreshGitStatusUiAsync,
                callbacks.UpdateAutoSaveStatus,
                callbacks.GetLocalizedString,
                dialog.ShowErrorMessage);

            var shellInteraction = new MainWindowShellInteractionController(
                ui.RootGrid,
                ui.DragOverlay,
                ui.LeftSplitter,
                ui.RightSplitter,
                fileOpenDrop,
                shellPanelLayout,
                rootKeyboardShortcut);

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
                services.SettingsService,
                fileOpenDrop,
                tabNavigation,
                tabSave,
                terminalPanel,
                settings,
                stickyNoteMode,
                pdfViewer,
                officeDocumentViewer,
                shellPane,
                services.CompareSelectionDialogService,
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
