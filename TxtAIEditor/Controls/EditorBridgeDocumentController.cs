using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
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
        private readonly Action<OpenedTab> _recordPendingSplitImeStructuralEdit;
        private readonly Func<OpenedTab, bool> _hasOtherTabForSameFile;
        private readonly Func<OpenedTab, Task> _flushOtherTabsPendingSyncsAsync;
        private readonly Dictionary<string, DeferredContentRefresh> _contentRefreshTimers = new();
        private static readonly TimeSpan ContentRefreshDebounce = TimeSpan.FromMilliseconds(350);

        private sealed class DeferredContentRefresh
        {
            public DeferredContentRefresh(DispatcherQueueTimer timer, OpenedTab tab)
            {
                Timer = timer;
                Tab = tab;
            }

            public DispatcherQueueTimer Timer { get; }

            public OpenedTab Tab { get; set; }
        }

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
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync,
            Action<OpenedTab> recordPendingSplitImeStructuralEdit,
            Func<OpenedTab, bool> hasOtherTabForSameFile,
            Func<OpenedTab, Task> flushOtherTabsPendingSyncsAsync)
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
            _recordPendingSplitImeStructuralEdit = recordPendingSplitImeStructuralEdit;
            _hasOtherTabForSameFile = hasOtherTabForSameFile;
            _flushOtherTabsPendingSyncsAsync = flushOtherTabsPendingSyncsAsync;
        }

        public async Task HandleLineChangedAsync(
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            string text,
            bool isComposing)
        {
            await _flushOtherTabsPendingSyncsAsync(tab);
            session.ReplaceLine(lineNumber, text, trackUndo: !isComposing, isComposing: isComposing);

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
            await _flushOtherTabsPendingSyncsAsync(tab);
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
            await _flushOtherTabsPendingSyncsAsync(tab);
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
            await _flushOtherTabsPendingSyncsAsync(tab);
            int lineCount = session.MergeLineWithPrevious(lineNumber);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount);
        }

        public async Task HandleDeleteLineRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            bool isComposing = false)
        {
            await _flushOtherTabsPendingSyncsAsync(tab);
            int lineCount = session.DeleteLine(lineNumber);
            await CompleteStructuralEditAsync(bridge, tab, tabItem, lineCount, isComposing);
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
                ScheduleDeferredContentRefresh(tab, tabItem);
            }

            _schedulePreview(tab);
        }

        private void ScheduleDeferredContentRefresh(OpenedTab tab, TabViewItem tabItem)
        {
            if (!_contentRefreshTimers.TryGetValue(tab.Id, out var refresh))
            {
                var timer = tabItem.DispatcherQueue.CreateTimer();
                timer.IsRepeating = false;
                timer.Interval = ContentRefreshDebounce;
                refresh = new DeferredContentRefresh(timer, tab);
                _contentRefreshTimers[tab.Id] = refresh;
                string tabId = tab.Id;
                timer.Tick += (_, _) => RunDeferredContentRefresh(tabId);
            }
            else
            {
                refresh.Timer.Stop();
                refresh.Tab = tab;
            }

            refresh.Timer.Start();
        }

        private void RunDeferredContentRefresh(string tabId)
        {
            if (!_contentRefreshTimers.TryGetValue(tabId, out var refresh))
            {
                return;
            }

            refresh.Timer.Stop();
            _contentRefreshTimers.Remove(tabId);

            OpenedTab tab = refresh.Tab;
            _updateLanguage(tab);
            _tocController.RefreshToc(tab);
            _statusBarController.UpdateTotalLines(tab);
            _scheduleDeferredPendingSplitImeSync(tab);
        }

        private async Task CompleteStructuralEditAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            int lineCount,
            bool isComposing = false)
        {
            MarkDirty(tab, tabItem);
            await bridge.UpdateLineCountAsync(lineCount);
            _schedulePreview(tab);

            if (_hasOtherTabForSameFile(tab))
            {
                if (isComposing)
                {
                    _recordPendingSplitImeStructuralEdit(tab);
                }
                else
                {
                    await _syncEditsToOtherTabsAsync(tab);
                }
            }
            else
            {
                await _syncEditsToOtherTabsAsync(tab);
            }

            _statusBarController.UpdateTotalLines(tab);
        }

        private void MarkDirty(OpenedTab tab, TabViewItem tabItem)
        {
            _tabDirtyStateController.MarkTabDirty(tab, tabItem);
            _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
        }
    }
}
