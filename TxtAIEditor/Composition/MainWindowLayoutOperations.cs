using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowLayoutOperations
    {
        public static async Task SaveUiLayoutSettingsAsync(
            AppWindow appWindow,
            ISettingsService settingsService,
            EditorWorkspacePane editorWorkspace,
            ShellPanelLayoutService shellPanelLayout)
        {
            try
            {
                var settings = settingsService.CurrentSettings;
                WindowPlacementService.CaptureRestoredWindowPlacement(appWindow, settings);
                settings.TerminalPanelHeight = editorWorkspace.PersistedTerminalPanelHeight;
                settings.LeftSidebarVisible = shellPanelLayout.IsLeftSidebarVisible;
                settings.RightSidebarVisible = shellPanelLayout.IsRightSidebarVisible;
                settings.LeftSidebarWidth = shellPanelLayout.LeftSidebarWidth;
                settings.RightSidebarWidth = shellPanelLayout.RightSidebarWidth;

                await settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save UI layout settings: {ex.Message}");
            }
        }

        public static async Task SaveSidebarVisibilitySettingsAsync(
            ISettingsService settingsService,
            ShellPanelLayoutService shellPanelLayout)
        {
            try
            {
                var settings = settingsService.CurrentSettings;
                settings.LeftSidebarVisible = shellPanelLayout.IsLeftSidebarVisible;
                settings.RightSidebarVisible = shellPanelLayout.IsRightSidebarVisible;
                settings.LeftSidebarWidth = shellPanelLayout.LeftSidebarWidth;
                settings.RightSidebarWidth = shellPanelLayout.RightSidebarWidth;

                await settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save sidebar visibility settings: {ex.Message}");
            }
        }

        public static void ApplyPreviewVisibility(
            bool show,
            ShellPaneController shellPaneController,
            bool startupInitializationComplete,
            LivePreviewController livePreviewController)
        {
            shellPaneController.ApplyPreviewVisibility(show);
            if (show && startupInitializationComplete)
            {
                _ = livePreviewController.InitializeAsync();
            }
        }

        public static void ToggleMaximize(AppWindow appWindow)
        {
            if (appWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
        }
    }
}
