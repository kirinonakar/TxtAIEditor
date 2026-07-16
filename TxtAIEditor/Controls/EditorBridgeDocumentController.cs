using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
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
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;
        private readonly Dictionary<string, DeferredContentRefresh> _contentRefreshTimers = new();
        private readonly Dictionary<MonacoBridge, CancellationTokenSource> _textOperationCancellations = new();
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
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync)
        {
            _tabDirtyStateController = tabDirtyStateController;
            _statusBarController = statusBarController;
            _tocController = tocController;
            _schedulePreview = schedulePreview;
            _updateLanguage = updateLanguage;
            _syncEditsToOtherTabsAsync = syncEditsToOtherTabsAsync;
        }

        public async Task HandleEditRequestedAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            TabViewItem tabItem,
            EditorDocumentSession session,
            EditorEditRequest request)
        {
            if (!request.TryNormalize(session.Model, out EditorEditCommand command))
            {
                var invalidResult = new EditorEditCommandResult(
                    request.EditId,
                    IsAccepted: false,
                    session.DocumentVersion,
                    Math.Min(Math.Max(0, request.BaseVersion), session.DocumentVersion),
                    Change: null);
                await bridge.SendEditRejectedAsync(invalidResult);
                await ResynchronizeRejectedEditAsync(bridge, tab, session);
                return;
            }

            EditorEditCommandResult result = session.ApplyEditCommand(command);
            if (!result.IsAccepted)
            {
                await bridge.SendEditRejectedAsync(result);
                await ResynchronizeRejectedEditAsync(bridge, tab, session);
                return;
            }

            await bridge.SendEditAcceptedAsync(result);
            MarkDirty(tab, tabItem);
            await _syncEditsToOtherTabsAsync(tab);
            _schedulePreview(tab);
            _statusBarController.UpdateTotalLines(tab);
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
            CancellationTokenSource cancellation = BeginTextOperation(bridge);
            var progress = new Progress<TextOperationProgress>(value =>
            {
                if (IsCurrentTextOperation(bridge, cancellation))
                {
                    _statusBarController.ShowTextOperationProgress(
                        "findAll",
                        value,
                        () => TryCancelTextOperation(cancellation));
                }
            });
            try
            {
                var results = await session.FindAllAsync(
                    query,
                    matchCase,
                    isRegex,
                    currentLine,
                    cancellation.Token,
                    progress);
                if (IsCurrentTextOperation(bridge, cancellation))
                {
                    await bridge.SendFindAllResultsAsync(results, query);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RegexMatchTimeoutException ex)
            {
                Debug.WriteLine($"Find All regex timeout: {ex.Message}");
            }
            finally
            {
                CompleteTextOperation(bridge, cancellation);
            }
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
            CancellationTokenSource cancellation = BeginTextOperation(bridge);
            var progress = new Progress<TextOperationProgress>(value =>
            {
                if (IsCurrentTextOperation(bridge, cancellation))
                {
                    _statusBarController.ShowTextOperationProgress(
                        "replaceAll",
                        value);
                }
            });
            await bridge.SetTextOperationLockAsync(locked: true);
            try
            {
                ReplaceAllResult result = await session.ReplaceAllAsync(
                    query,
                    replace,
                    matchCase,
                    isRegex,
                    cancellation.Token,
                    progress);
                if (result.Change == null)
                {
                    return;
                }

                await bridge.ApplyLineReplacementsAsync(
                    result.Replacements,
                    result.Change.DocumentId,
                    result.Change.BaseVersion,
                    result.Change.Version,
                    result.Change.SourceViewId);
                await _syncEditsToOtherTabsAsync(tab);

                MarkDirty(tab, tabItem);
                _schedulePreview(tab);
                _statusBarController.UpdateTotalLines(tab);

                try
                {
                    var matches = await session.FindAllAsync(
                        query,
                        matchCase,
                        isRegex,
                        1,
                        CancellationToken.None);
                    await bridge.SendFindAllResultsAsync(matches, query);
                }
                catch (OperationCanceledException)
                {
                    await bridge.SendFindAllResultsAsync(Array.Empty<TextSearchResult>(), query);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RegexMatchTimeoutException ex)
            {
                Debug.WriteLine($"Replace All regex timeout: {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"Replace All invalid regex: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Replace All stopped by safety limit: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Replace All failed: {ex.Message}");
            }
            finally
            {
                await bridge.SetTextOperationLockAsync(locked: false);
                CompleteTextOperation(bridge, cancellation);
            }
        }

        private CancellationTokenSource BeginTextOperation(MonacoBridge bridge)
        {
            if (_textOperationCancellations.Remove(bridge, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var current = new CancellationTokenSource();
            _textOperationCancellations[bridge] = current;
            return current;
        }

        private bool IsCurrentTextOperation(MonacoBridge bridge, CancellationTokenSource cancellation)
        {
            return !cancellation.IsCancellationRequested &&
                _textOperationCancellations.TryGetValue(bridge, out CancellationTokenSource? current) &&
                ReferenceEquals(current, cancellation);
        }

        private void CompleteTextOperation(
            MonacoBridge bridge,
            CancellationTokenSource cancellation)
        {
            if (IsCurrentTextOperation(bridge, cancellation))
            {
                _textOperationCancellations.Remove(bridge);
                _statusBarController.HideTextOperationProgress();
            }

            cancellation.Dispose();
        }

        private static void TryCancelTextOperation(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
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

        private static Task ResynchronizeRejectedEditAsync(
            MonacoBridge bridge,
            OpenedTab tab,
            EditorDocumentSession session)
        {
            return bridge.SetTextAsync(
                session.GetText(),
                shouldFocus: false,
                session.DocumentId,
                session.DocumentVersion,
                tab.Id);
        }

        private void MarkDirty(OpenedTab tab, TabViewItem tabItem)
        {
            // Full dirty-line reconciliation scans the document. Keep the input path
            // constant-time and reconcile once after the edit burst becomes idle.
            _tabDirtyStateController.MarkTabDirtyOptimistically(tab);
        }
    }
}
