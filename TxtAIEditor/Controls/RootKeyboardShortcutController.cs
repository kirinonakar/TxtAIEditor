using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class RootKeyboardShortcutController
    {
        private readonly Action _openNewTab;
        private readonly Func<Task> _toggleLeftPanelAsync;
        private readonly Func<Task> _toggleRightPanelAsync;
        private readonly Action _focusSearchPanel;
        private readonly Action _closeActiveTab;
        private readonly Action _saveActiveTab;
        private readonly Action _saveActiveTabAs;
        private readonly Action _openFile;
        private readonly Action _find;
        private readonly Action _print;
        private readonly Func<bool> _letActiveContentHandleFind;
        private readonly Action _toggleTopMost;
        private readonly Action _toggleTheme;
        private readonly Action _toggleStickyNote;
        private readonly TerminalShortcutService _terminalShortcutService;
        private readonly Action _toggleLivePreview;
        private readonly Action _togglePreviewWidth;
        private readonly Action _toggleMaximize;

        public RootKeyboardShortcutController(
            Action openNewTab,
            Func<Task> toggleLeftPanelAsync,
            Func<Task> toggleRightPanelAsync,
            Action focusSearchPanel,
            Action closeActiveTab,
            Action saveActiveTab,
            Action saveActiveTabAs,
            Action openFile,
            Action find,
            Action print,
            Func<bool> letActiveContentHandleFind,
            Action toggleTopMost,
            Action toggleTheme,
            Action toggleStickyNote,
            TerminalShortcutService terminalShortcutService,
            Action toggleLivePreview,
            Action togglePreviewWidth,
            Action toggleMaximize)
        {
            _openNewTab = openNewTab;
            _toggleLeftPanelAsync = toggleLeftPanelAsync;
            _toggleRightPanelAsync = toggleRightPanelAsync;
            _focusSearchPanel = focusSearchPanel;
            _closeActiveTab = closeActiveTab;
            _saveActiveTab = saveActiveTab;
            _saveActiveTabAs = saveActiveTabAs;
            _openFile = openFile;
            _find = find;
            _print = print;
            _letActiveContentHandleFind = letActiveContentHandleFind;
            _toggleTopMost = toggleTopMost;
            _toggleTheme = toggleTheme;
            _toggleStickyNote = toggleStickyNote;
            _terminalShortcutService = terminalShortcutService;
            _toggleLivePreview = toggleLivePreview;
            _togglePreviewWidth = togglePreviewWidth;
            _toggleMaximize = toggleMaximize;
        }

        public void HandleKeyDown(KeyRoutedEventArgs e)
        {
            if (TryHandleFunctionKeyShortcut(e.Key))
            {
                e.Handled = true;
                return;
            }

            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (!ctrl)
            {
                return;
            }

            if (e.Key == Windows.System.VirtualKey.N)
            {
                e.Handled = true;
                _openNewTab();
            }
            else if (e.Key == Windows.System.VirtualKey.Number1)
            {
                e.Handled = true;
                _ = _toggleLeftPanelAsync();
            }
            else if (e.Key == Windows.System.VirtualKey.Number2)
            {
                e.Handled = true;
                _ = _toggleRightPanelAsync();
            }
            else if (e.Key == Windows.System.VirtualKey.Number3)
            {
                e.Handled = true;
                _togglePreviewWidth();
            }
            else if (shift && e.Key == Windows.System.VirtualKey.F)
            {
                e.Handled = true;
                _focusSearchPanel();
            }
            else if (e.Key == Windows.System.VirtualKey.W)
            {
                e.Handled = true;
                _closeActiveTab();
            }
            else if (e.Key == Windows.System.VirtualKey.S)
            {
                e.Handled = true;
                if (shift)
                {
                    _saveActiveTabAs();
                }
                else
                {
                    _saveActiveTab();
                }
            }
            else if (e.Key == Windows.System.VirtualKey.O)
            {
                e.Handled = true;
                _openFile();
            }
            else if (e.Key == Windows.System.VirtualKey.F)
            {
                if (!shift && _letActiveContentHandleFind())
                {
                    return;
                }

                e.Handled = true;
                _find();
            }
            else if (e.Key == Windows.System.VirtualKey.P)
            {
                e.Handled = true;
                _print();
            }
            else if (TerminalShortcutService.IsTerminalToggleKey(e))
            {
                e.Handled = true;
                _terminalShortcutService.RequestToggle();
            }
        }

        private bool TryHandleFunctionKeyShortcut(Windows.System.VirtualKey key)
        {
            switch (key)
            {
                case Windows.System.VirtualKey.F4:
                    _toggleLivePreview();
                    return true;
                case Windows.System.VirtualKey.F9:
                    _toggleTopMost();
                    return true;
                case Windows.System.VirtualKey.F10:
                    _toggleTheme();
                    return true;
                case Windows.System.VirtualKey.F11:
                    _toggleMaximize();
                    return true;
                case Windows.System.VirtualKey.F12:
                    _toggleStickyNote();
                    return true;
            }

            return false;
        }
    }
}
