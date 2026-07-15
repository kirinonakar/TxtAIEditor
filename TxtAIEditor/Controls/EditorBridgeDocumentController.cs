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
        private readonly Func<OpenedTab, int, string, bool, Task> _syncLineChangeToOtherTabsAsync;
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;
        private readonly Dictionary<string, DeferredContentRefresh> _contentRefreshTimers = new();
        private static readonly TimeSpan ContentRefreshDebounce = TimeSpan.FromMilliseconds(350);

        private sealed class DeferredContentRefresh
        {
            public DeferredContentRefresh(
                DispatcherQueueTimer timer,
                OpenedTab tab,
                EditorDocumentSession session)
            {
                Timer = timer;
                Tab = tab;
                Session = session;
            }

            public DispatcherQueueTimer Timer { get; }

            public OpenedTab Tab { get; set; }

            public EditorDocumentSession Session { get; set; }
        }

        public EditorBridgeDocumentController(
            TabDirtyStateController tabDirtyStateController,
            StatusBarController statusBarController,
            TocController tocController,
            Action<OpenedTab> schedulePreview,
            Action<OpenedTab> updateLanguage,
            Func<OpenedTab, int, string, bool, Task> syncLineChangeToOtherTabsAsync,
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync)
        {
            _tabDirtyStateController = tabDirtyStateController;
            _statusBarController = statusBarController;
            _tocController = tocController;
            _schedulePreview = schedulePreview;
            _updateLanguage = updateLanguage;
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
            if (isComposing)
            {
                // compositionstart~compositionupdate는 WebView 로컬 투영이다.
                // compositionend에서 오는 확정 편집만 공유 DocumentBuffer에 적용한다.
                return;
            }

            session.ReplaceLine(lineNumber, text);

            MarkDirty(tab, tabItem);

            _schedulePreview(tab);
            await _syncLineChangeToOtherTabsAsync(tab, lineNumber, text, false);
        }

        public Task HandleLineEditAsync(
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int lineNumber,
            int startColumn,
            int endColumn,
            string replacementText,
            bool isComposing)
        {
            string currentText = session.Model.GetLine(lineNumber);
            int start = Math.Clamp(startColumn - 1, 0, currentText.Length);
            int end = Math.Clamp(endColumn - 1, start, currentText.Length);
            string updatedText = string.Concat(
                currentText.AsSpan(0, start),
                replacementText.AsSpan(),
                currentText.AsSpan(end));

            return HandleLineChangedAsync(
                tab,
                tabItem,
                session,
                lineNumber,
                updatedText,
                isComposing);
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

        public async Task HandleRangeEditRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            int startLine,
            int startColumn,
            int endLine,
            int endColumn,
            string text)
        {
            int lineCount = session.ApplyRangeEdit(
                startLine,
                startColumn,
                endLine,
                endColumn,
                text);
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
            int lineNumber,
            bool isComposing = false)
        {
            if (isComposing) return;
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
            bool isRegex,
            int currentLine)
        {
            var results = session.FindAll(query, matchCase, isRegex, currentLine);
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
            await bridge.SetTextAsync(
                updatedText,
                shouldFocus: false,
                session.DocumentId,
                session.DocumentVersion,
                tab.Id);
            await _syncEditsToOtherTabsAsync(tab);
            await bridge.SendFindAllResultsAsync(session.FindAll(query, matchCase, isRegex, 1), query);

            MarkDirty(tab, tabItem);
            _schedulePreview(tab);
            _statusBarController.UpdateTotalLines(tab);
        }

        public void HandleContentChanged(
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            bool isComposing)
        {
            if (!isComposing)
            {
                MarkDirty(tab, tabItem);
                ScheduleDeferredContentRefresh(tab, tabItem, session);
            }

            _schedulePreview(tab);
        }

        private void ScheduleDeferredContentRefresh(
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session)
        {
            if (!_contentRefreshTimers.TryGetValue(tab.Id, out var refresh))
            {
                var timer = tabItem.DispatcherQueue.CreateTimer();
                timer.IsRepeating = false;
                timer.Interval = ContentRefreshDebounce;
                refresh = new DeferredContentRefresh(timer, tab, session);
                _contentRefreshTimers[tab.Id] = refresh;
                string tabId = tab.Id;
                timer.Tick += (_, _) => RunDeferredContentRefresh(tabId);
            }
            else
            {
                refresh.Timer.Stop();
                refresh.Tab = tab;
                refresh.Session = session;
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
            refresh.Session.RefreshTabContentPreview();
            _tabDirtyStateController.ReconcileTabDirtyState(tab);
            _updateLanguage(tab);
            _tocController.RefreshTocAfterDocumentChange(tab);
            _statusBarController.UpdateTotalLines(tab);
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
            // Full dirty-line reconciliation scans the document. Keep the input path
            // constant-time and reconcile once after the edit burst becomes idle.
            _tabDirtyStateController.MarkTabDirtyOptimistically(tab);
        }
    }
}
