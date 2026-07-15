using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class MainWindowLifecycleController
    {
        private readonly Window _window;
        private readonly UIElement _titleBar;
        private readonly TerminalShortcutService _terminalShortcutService;
        private readonly FunctionKeyShortcutService _functionKeyShortcutService;
        private readonly AutoSaveController _autoSaveController;
        private readonly DispatcherTimer _gitAutoRefreshTimer;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly LivePreviewController _livePreviewController;

        public MainWindowLifecycleController(
            Window window,
            UIElement titleBar,
            TerminalShortcutService terminalShortcutService,
            FunctionKeyShortcutService functionKeyShortcutService,
            AutoSaveController autoSaveController,
            DispatcherTimer gitAutoRefreshTimer,
            EditorWorkspacePane editorWorkspace,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            LivePreviewController livePreviewController)
        {
            _window = window;
            _titleBar = titleBar;
            _terminalShortcutService = terminalShortcutService;
            _functionKeyShortcutService = functionKeyShortcutService;
            _autoSaveController = autoSaveController;
            _gitAutoRefreshTimer = gitAutoRefreshTimer;
            _editorWorkspace = editorWorkspace;
            _tabBridges = tabBridges;
            _livePreviewController = livePreviewController;
        }

        public void InitializeTitleBar()
        {
            _window.ExtendsContentIntoTitleBar = true;
            _window.SetTitleBar(_titleBar);
        }

        public void StartShortcuts()
        {
            _terminalShortcutService.Start();
            _functionKeyShortcutService.Start();
        }

        public void HandleActivationChanged(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                StopShortcuts();
            }
            else
            {
                StartShortcuts();
            }
        }

        public void HandleWindowClosed(object sender, WindowEventArgs args)
        {
            CleanupCore(suppressErrors: true);

            try
            {
                if (Application.Current is App app)
                {
                    app.CleanupAppResources();
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        public void CleanupBeforeRestart()
        {
            CleanupCore(suppressErrors: false);
        }

        private void CleanupCore(bool suppressErrors)
        {
            RunCleanup(StopShortcuts, suppressErrors);
            RunCleanup(() =>
            {
                _autoSaveController.Stop();
                _gitAutoRefreshTimer.Stop();
            }, suppressErrors);
            RunCleanup(_editorWorkspace.StopAllTerminalSessions, suppressErrors);
            RunCleanup(CloseEditorBridges, suppressErrors);
            RunCleanup(_livePreviewController.Close, suppressErrors);
        }

        private void StopShortcuts()
        {
            _terminalShortcutService.Stop();
            _functionKeyShortcutService.Stop();
        }

        private void CloseEditorBridges()
        {
            foreach (var bridge in _tabBridges.Values)
            {
                try { bridge.WebView.Close(); }
                catch { }
            }

            _tabBridges.Clear();
        }

        private static void RunCleanup(Action cleanup, bool suppressErrors)
        {
            if (!suppressErrors)
            {
                cleanup();
                return;
            }

            try
            {
                cleanup();
            }
            catch { }
        }
    }
}
