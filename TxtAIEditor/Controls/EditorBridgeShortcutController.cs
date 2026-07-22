using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorBridgeShortcutController
    {
        private readonly Action _toggleLivePreview;
        private readonly Action _toggleTopMost;
        private readonly Action _toggleTheme;
        private readonly Action _toggleMaximize;
        private readonly Action _toggleStickyNote;
        private readonly Action _toggleWordWrap;
        private readonly Func<Task> _toggleLeftPanelAsync;
        private readonly Func<Task> _toggleRightPanelAsync;
        private readonly Action _togglePreviewWidth;
        private readonly Action _openNewTab;
        private readonly Action _saveFile;
        private readonly Action _saveFileAs;
        private readonly Action _openFile;
        private readonly Action _requestTerminalToggle;
        private readonly Action _closeActiveTab;
        private readonly Action _print;
        private readonly Action _focusSearchPanel;
        private readonly TabDirtyStateController _tabDirtyStateController;
        private readonly StatusBarController _statusBarController;
        private readonly Action<OpenedTab> _schedulePreview;
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;
        private bool _undoRedoInProgress;

        public EditorBridgeShortcutController(
            Action toggleLivePreview,
            Action toggleTopMost,
            Action toggleTheme,
            Action toggleMaximize,
            Action toggleStickyNote,
            Action toggleWordWrap,
            Func<Task> toggleLeftPanelAsync,
            Func<Task> toggleRightPanelAsync,
            Action togglePreviewWidth,
            Action openNewTab,
            Action saveFile,
            Action saveFileAs,
            Action openFile,
            Action requestTerminalToggle,
            Action closeActiveTab,
            Action print,
            Action focusSearchPanel,
            TabDirtyStateController tabDirtyStateController,
            StatusBarController statusBarController,
            Action<OpenedTab> schedulePreview,
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync)
        {
            _toggleLivePreview = toggleLivePreview;
            _toggleTopMost = toggleTopMost;
            _toggleTheme = toggleTheme;
            _toggleMaximize = toggleMaximize;
            _toggleStickyNote = toggleStickyNote;
            _toggleWordWrap = toggleWordWrap;
            _toggleLeftPanelAsync = toggleLeftPanelAsync;
            _toggleRightPanelAsync = toggleRightPanelAsync;
            _togglePreviewWidth = togglePreviewWidth;
            _openNewTab = openNewTab;
            _saveFile = saveFile;
            _saveFileAs = saveFileAs;
            _openFile = openFile;
            _requestTerminalToggle = requestTerminalToggle;
            _closeActiveTab = closeActiveTab;
            _print = print;
            _focusSearchPanel = focusSearchPanel;
            _tabDirtyStateController = tabDirtyStateController;
            _statusBarController = statusBarController;
            _schedulePreview = schedulePreview;
            _syncEditsToOtherTabsAsync = syncEditsToOtherTabsAsync;
        }

        public void Handle(
            string shortcutName,
            CustomEditorBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session)
        {
            switch (shortcutName)
            {
                case "f4":
                    _toggleLivePreview();
                    break;
                case "f9":
                    _toggleTopMost();
                    break;
                case "f10":
                    _toggleTheme();
                    break;
                case "f11":
                    _toggleMaximize();
                    break;
                case "f12":
                    _toggleStickyNote();
                    break;
                case "wordWrap":
                    _toggleWordWrap();
                    break;
                case "toggleLeftPanel":
                    _ = _toggleLeftPanelAsync();
                    break;
                case "toggleRightPanel":
                    _ = _toggleRightPanelAsync();
                    break;
                case "expandRightPanel":
                    _togglePreviewWidth();
                    break;
                case "newTab":
                    _openNewTab();
                    break;
                case "save":
                    _saveFile();
                    break;
                case "saveAs":
                    _saveFileAs();
                    break;
                case "open":
                    _openFile();
                    break;
                case "terminal":
                    _requestTerminalToggle();
                    break;
                case "closeTab":
                    _closeActiveTab();
                    break;
                case "print":
                    _print();
                    break;
                case "searchAll":
                    _focusSearchPanel();
                    break;
                case "undo":
                    _ = ApplyUndoRedoAsync(session, isUndo: true, bridge, tab, tabItem);
                    break;
                case "redo":
                    _ = ApplyUndoRedoAsync(session, isUndo: false, bridge, tab, tabItem);
                    break;
            }
        }

        private async Task ApplyUndoRedoAsync(
            EditorDocumentSession session,
            bool isUndo,
            CustomEditorBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem)
        {
            if (_undoRedoInProgress)
            {
                return;
            }

            _undoRedoInProgress = true;
            var progress = new Progress<TextOperationProgress>(value =>
                _statusBarController.ShowTextOperationProgress(isUndo ? "undo" : "redo", value));
            await bridge.SetTextOperationLockAsync(locked: true);
            try
            {
                UndoResult? result = isUndo
                    ? await session.UndoAsync(progress)
                    : await session.RedoAsync(progress);
                if (result == null)
                {
                    return;
                }

                _schedulePreview(tab);
                EditorDocumentChange? change = session.LastChange;
                await bridge.ApplyEditResultAsync(
                    result,
                    change?.DocumentId,
                    change?.BaseVersion,
                    change?.Version,
                    change?.SourceViewId);
                await _syncEditsToOtherTabsAsync(tab);
                // Applying the undo/redo patch can optimistically add dirty markers in
                // WebView, especially when a large document has only a partial original-line
                // cache. Send the host's authoritative markers after every view is updated.
                _tabDirtyStateController.MarkTabDirty(tab, tabItem);
                _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
            }
            finally
            {
                await bridge.SetTextOperationLockAsync(locked: false);
                _statusBarController.HideTextOperationProgress();
                _undoRedoInProgress = false;
            }
        }
    }
}
