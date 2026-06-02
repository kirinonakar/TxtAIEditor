using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class SplitImeSyncController
    {
        private const int DeferredUiSyncDelayMs = 260;

        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Func<OpenedTab, IEnumerable<OpenedTab>> _getTabsForSameFile;
        private readonly Action<OpenedTab> _schedulePreview;
        private readonly Action<OpenedTab, bool> _setDirtyStateForFileGroup;
        private readonly Dictionary<string, PendingSplitImeSyncState> _pendingStates =
            new Dictionary<string, PendingSplitImeSyncState>();

        public SplitImeSyncController(
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            Func<OpenedTab, IEnumerable<OpenedTab>> getTabsForSameFile,
            Action<OpenedTab> schedulePreview,
            Action<OpenedTab, bool> setDirtyStateForFileGroup)
        {
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _getTabsForSameFile = getTabsForSameFile;
            _schedulePreview = schedulePreview;
            _setDirtyStateForFileGroup = setDirtyStateForFileGroup;
        }

        public bool QueuePendingLineSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return false;
            if (!HasOtherTabForSameFile(sourceTab)) return false;

            if (!_pendingStates.TryGetValue(sourceTab.Id, out var state))
            {
                state = new PendingSplitImeSyncState();
                _pendingStates[sourceTab.Id] = state;
            }

            StopPendingTimer(state);
            state.Lines[lineNumber] = text;

            if (state.Lines.Count > 1)
            {
                state.IsColumnEdit = true;
            }

            // IME 조합 중에는 실시간 동기화로 인한 재렌더링 및 자소 분리를 막기 위해 항상 true를 반환하여 동기화를 보류합니다.
            return true;
        }

        public bool ScheduleCompletionSyncIfNeeded(OpenedTab sourceTab, int lineNumber, string text)
        {
            if (!_pendingStates.TryGetValue(sourceTab.Id, out var state))
            {
                return false;
            }

            state.Lines[lineNumber] = text;
            if (!state.IsColumnEdit && state.Lines.Count <= 1)
            {
                Clear(sourceTab.Id);
                return false;
            }

            state.IsColumnEdit = true;
            ScheduleDeferredSync(sourceTab, state);
            return true;
        }

        public bool ScheduleDeferredSyncIfNeeded(OpenedTab sourceTab)
        {
            if (!_pendingStates.TryGetValue(sourceTab.Id, out var state))
            {
                return false;
            }

            if (!state.IsColumnEdit && state.Lines.Count <= 1)
            {
                Clear(sourceTab.Id);
                return false;
            }

            state.IsColumnEdit = true;
            ScheduleDeferredSync(sourceTab, state);
            return true;
        }

        public async Task FlushAsync(OpenedTab sourceTab)
        {
            if (!_pendingStates.TryGetValue(sourceTab.Id, out var state)) return;

            StopPendingTimer(state);
            var pendingLineNumbers = state.Lines.Keys.OrderBy(line => line).ToList();
            Clear(sourceTab.Id);

            foreach (int lineNumber in pendingLineNumbers)
            {
                string lineText = GetCurrentLineText(sourceTab, lineNumber, string.Empty);
                await SyncLineChangeToOtherTabsAsync(sourceTab, lineNumber, lineText, isComposing: false);
            }
        }

        public void Clear(string tabId)
        {
            if (_pendingStates.TryGetValue(tabId, out var state))
            {
                StopPendingTimer(state);
            }

            _pendingStates.Remove(tabId);
        }

        public void ClearAll()
        {
            foreach (var tabId in _pendingStates.Keys.ToList())
            {
                Clear(tabId);
            }
        }

        public async Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return;

            var otherTabs = _getTabsForSameFile(sourceTab)
                .Where(t => t.Id != sourceTab.Id)
                .ToList();

            if (otherTabs.Count == 0) return;

            bool sourceDirty = sourceTab.IsDirty;

            foreach (var otherTab in otherTabs)
            {
                if (_editorSessions.TryGetValue(otherTab.Id, out var otherSession))
                {
                    otherSession.ReplaceLine(lineNumber, text);
                    otherTab.Content = otherSession.GetText();
                }
                else
                {
                    otherTab.Content = text;
                }

                if (_tabBridges.TryGetValue(otherTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    await bridgeGroup.Bridge.UpdateLineAsync(lineNumber, text, isComposing);
                }

                if (!isComposing)
                {
                    _schedulePreview(otherTab);
                }
            }

            _setDirtyStateForFileGroup(sourceTab, sourceDirty);
        }

        public async Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return;

            Clear(sourceTab.Id);

            var otherTabs = _getTabsForSameFile(sourceTab)
                .Where(t => t.Id != sourceTab.Id)
                .ToList();

            if (otherTabs.Count == 0) return;

            if (!_editorSessions.TryGetValue(sourceTab.Id, out var sourceSession)) return;
            string updatedText = sourceSession.GetText();
            bool sourceDirty = sourceTab.IsDirty;

            foreach (var otherTab in otherTabs)
            {
                if (_editorSessions.TryGetValue(otherTab.Id, out var otherSession))
                {
                    otherSession.UpdateContentFromSync(updatedText);
                }
                otherTab.Content = updatedText;

                if (updateUi && _tabBridges.TryGetValue(otherTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    await bridgeGroup.Bridge.SetTextAsync(updatedText, shouldFocus: false);
                }

                if (updateUi)
                {
                    _schedulePreview(otherTab);
                }
            }

            _setDirtyStateForFileGroup(sourceTab, sourceDirty);
        }

        private void ScheduleDeferredSync(OpenedTab sourceTab, PendingSplitImeSyncState state)
        {
            StopPendingTimer(state);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DeferredUiSyncDelayMs)
            };

            state.DeferredSyncTimer = timer;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();

                if (_pendingStates.TryGetValue(sourceTab.Id, out var currentState) &&
                    ReferenceEquals(currentState, state) &&
                    ReferenceEquals(currentState.DeferredSyncTimer, timer))
                {
                    await FlushAsync(sourceTab);
                }
            };
            timer.Start();
        }

        private bool HasOtherTabForSameFile(OpenedTab sourceTab)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return false;
            return _getTabsForSameFile(sourceTab).Any(tab => tab.Id != sourceTab.Id);
        }

        private string GetCurrentLineText(OpenedTab tab, int lineNumber, string fallback)
        {
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                return session.GetLines(lineNumber, 1).FirstOrDefault() ?? string.Empty;
            }

            return fallback;
        }

        private static void StopPendingTimer(PendingSplitImeSyncState state)
        {
            if (state.DeferredSyncTimer != null)
            {
                state.DeferredSyncTimer.Stop();
                state.DeferredSyncTimer = null;
            }
        }

        private sealed class PendingSplitImeSyncState
        {
            public Dictionary<int, string> Lines { get; } = new Dictionary<int, string>();
            public DispatcherTimer? DeferredSyncTimer { get; set; }
            public bool IsColumnEdit { get; set; }
        }
    }
}
