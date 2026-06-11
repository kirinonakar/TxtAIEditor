using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorBridgeDocumentController
    {
        private readonly TabDirtyStateController _tabDirtyStateController;
        private readonly StatusBarController _statusBarController;
        private readonly TocController _tocController;
        private readonly Action<OpenedTab> _schedulePreview;
        private readonly Action<OpenedTab> _updateLanguage;
        private readonly Func<OpenedTab, int, string, bool> _queuePendingSplitImeLineSync;
        private readonly Func<OpenedTab, int, string, bool> _schedulePendingSplitImeCompletionSync;
        private readonly Action<OpenedTab> _scheduleDeferredPendingSplitImeSync;
        private readonly Func<OpenedTab, int, string, bool, Task> _syncLineChangeToOtherTabsAsync;
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;

        public EditorBridgeDocumentController(
            TabDirtyStateController tabDirtyStateController,
            StatusBarController statusBarController,
            TocController tocController,
            Action<OpenedTab> schedulePreview,
            Action<OpenedTab> updateLanguage,
            Func<OpenedTab, int, string, bool> queuePendingSplitImeLineSync,
            Func<OpenedTab, int, string, bool> schedulePendingSplitImeCompletionSync,
            Action<OpenedTab> scheduleDeferredPendingSplitImeSync,
            Func<OpenedTab, int, string, bool, Task> syncLineChangeToOtherTabsAsync,
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync)
        {
            _tabDirtyStateController = tabDirtyStateController;
            _statusBarController = statusBarController;
            _tocController = tocController;
            _schedulePreview = schedulePreview;
            _updateLanguage = updateLanguage;
            _queuePendingSplitImeLineSync = queuePendingSplitImeLineSync;
            _schedulePendingSplitImeCompletionSync = schedulePendingSplitImeCompletionSync;
            _scheduleDeferredPendingSplitImeSync = scheduleDeferredPendingSplitImeSync;
            _syncLineChangeToOtherTabsAsync = syncLineChangeToOtherTabsAsync;
            _syncEditsToOtherTabsAsync = syncEditsToOtherTabsAsync;
        }

        public async Task HandleLineChangedAsync(
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            string text,
            bool isComposing)
        {
            session.ReplaceLine(lineNumber, text);

            if (!isComposing)
            {
                MarkDirty(tab, tabItem);
            }

            _schedulePreview(tab);

            if (isComposing && _queuePendingSplitImeLineSync(tab, lineNumber, text))
            {
                return;
            }

            if (!isComposing && _schedulePendingSplitImeCompletionSync(tab, lineNumber, text))
            {
                return;
            }

            await _syncLineChangeToOtherTabsAsync(tab, lineNumber, text, isComposing);
        }

        public async Task HandleLineInsertRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            string text)
        {
            int lineCount = session.InsertLine(lineNumber, text);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount);
        }

        public async Task HandleLineSplitRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            string before,
            string after)
        {
            int lineCount = session.SplitLine(lineNumber, before, after);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount);
        }

        public async Task HandleMergeLineWithPreviousRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber)
        {
            int lineCount = session.MergeLineWithPrevious(lineNumber);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount);
        }

        public async Task HandleDeleteLineRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber)
        {
            int lineCount = session.DeleteLine(lineNumber);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount);
        }

        public async Task HandleFindRequestedAsync(
            MonacoBridge bridge,
            EditorDocumentSession session,
            string query,
            int startLine,
            int startColumn,
            bool reverse,
            bool matchCase,
            bool isRegex)
        {
            var result = session.Find(query, startLine, startColumn, reverse, matchCase, isRegex);
            await bridge.SendFindResultAsync(result, query);
        }

        public async Task HandleFindAllRequestedAsync(
            MonacoBridge bridge,
            EditorDocumentSession session,
            string query,
            bool matchCase,
            bool isRegex)
        {
            var results = session.FindAll(query, matchCase, isRegex);
            await bridge.SendFindAllResultsAsync(results, query);
        }

        public async Task HandleReplaceAllRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            string query,
            string replace,
            bool matchCase,
            bool isRegex)
        {
            session.ReplaceAll(query, replace, matchCase, isRegex);
            string updatedText = session.GetText();
            await bridge.SetTextAsync(updatedText, shouldFocus: false);
            await _syncEditsToOtherTabsAsync(tab);
            await bridge.SendFindAllResultsAsync(session.FindAll(query, matchCase, isRegex), query);

            MarkDirty(tab, tabItem);
            _schedulePreview(tab);
            _statusBarController.UpdateTotalLines(tab);
        }

        public void HandleContentChanged(OpenedTab tab, TabViewItem tabItem, bool isComposing)
        {
            if (!isComposing)
            {
                MarkDirty(tab, tabItem);
                _updateLanguage(tab);
                _tocController.RefreshToc(tab);
                _statusBarController.UpdateTotalLines(tab);
                _scheduleDeferredPendingSplitImeSync(tab);
            }

            _schedulePreview(tab);
        }

        private async Task CompleteStructuralEditAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            int lineCount)
        {
            MarkDirty(tab, tabItem);
            await bridge.UpdateLineCountAsync(lineCount);
            _schedulePreview(tab);
            await _syncEditsToOtherTabsAsync(tab);
            _statusBarController.UpdateTotalLines(tab);
        }

        private void MarkDirty(OpenedTab tab, TabViewItem tabItem)
        {
            _tabDirtyStateController.MarkTabDirty(tab, tabItem);
            _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
        }
    }
}
