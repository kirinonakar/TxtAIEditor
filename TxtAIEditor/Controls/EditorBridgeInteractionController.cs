using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorBridgeInteractionController
    {
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly StatusBarController _statusBarController;
        private readonly TabSelectionController _tabSelectionController;
        private readonly LivePreviewController _livePreviewController;
        private readonly Action<string, OpenedTab, int, int> _selectionContextUpdater;
        private readonly Func<bool> _isScrollSyncEnabled;
        private readonly Action<bool> _setScrollSyncEnabled;
        private readonly object _scrollSyncGate = new();
        private (OpenedTab Tab, int FirstLine, double Offset)? _pendingScrollSync;
        private bool _scrollSyncDispatchQueued;

        public EditorBridgeInteractionController(
            EditorWorkspacePane editorWorkspace,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            DispatcherQueue dispatcherQueue,
            Func<OpenedTab?> activeTabProvider,
            StatusBarController statusBarController,
            TabSelectionController tabSelectionController,
            LivePreviewController livePreviewController,
            Action<string, OpenedTab, int, int> selectionContextUpdater,
            Func<bool> isScrollSyncEnabled,
            Action<bool> setScrollSyncEnabled)
        {
            _editorWorkspace = editorWorkspace;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _tabBridges = tabBridges;
            _dispatcherQueue = dispatcherQueue;
            _activeTabProvider = activeTabProvider;
            _statusBarController = statusBarController;
            _tabSelectionController = tabSelectionController;
            _livePreviewController = livePreviewController;
            _selectionContextUpdater = selectionContextUpdater;
            _isScrollSyncEnabled = isScrollSyncEnabled;
            _setScrollSyncEnabled = setScrollSyncEnabled;
        }

        public void HandleCursorChanged(MonacoBridge bridge, OpenedTab tab, int line, int column)
        {
            ActivateOwnerTab(tab);

            if (_activeTabProvider() == tab)
            {
                if (tab.IsHexViewer)
                {
                    _statusBarController.SetHexCursorPosition(tab, line, column);
                }
                else
                {
                    _statusBarController.SetCursorPosition(line, column);
                }

                _ = bridge.RequestSelectionAsync();
            }
        }

        public void HandleSelectionReceived(
            OpenedTab tab,
            string selectedText,
            int selectionStartLine,
            int selectionEndLine,
            long? hexOffset,
            long? hexLength)
        {
            ActivateOwnerTab(tab);

            if (_activeTabProvider() == tab)
            {
                if (tab.IsHexViewer)
                {
                    if (!hexOffset.HasValue &&
                        !hexLength.HasValue &&
                        !string.IsNullOrEmpty(selectedText))
                    {
                        return;
                    }

                    _statusBarController.UpdateHexSelectionStats(tab, hexOffset, hexLength);
                    return;
                }

                _selectionContextUpdater(selectedText, tab, selectionStartLine, selectionEndLine);
            }
        }

        public void HandleScrollChanged(OpenedTab tab, int firstLine, double offset)
        {
            if (!_isScrollSyncEnabled())
            {
                return;
            }

            lock (_scrollSyncGate)
            {
                _pendingScrollSync = (tab, firstLine, offset);
                if (_scrollSyncDispatchQueued)
                {
                    return;
                }

                _scrollSyncDispatchQueued = true;
            }

            if (!_dispatcherQueue.TryEnqueue(ProcessPendingScrollSync))
            {
                lock (_scrollSyncGate)
                {
                    _scrollSyncDispatchQueued = false;
                    _pendingScrollSync = null;
                }
            }
        }

        private void ProcessPendingScrollSync()
        {
            (OpenedTab Tab, int FirstLine, double Offset)? pending;
            lock (_scrollSyncGate)
            {
                pending = _pendingScrollSync;
                _pendingScrollSync = null;
                _scrollSyncDispatchQueued = false;
            }

            if (pending is not { } scroll ||
                !_isScrollSyncEnabled() ||
                _activeTabProvider() != scroll.Tab)
            {
                return;
            }

            _livePreviewController.PostScrollSync(scroll.FirstLine, scroll.Offset);

            if (_editorWorkspace.CurrentSplitMode == EditorSplitMode.None)
            {
                return;
            }

            var otherTabView = GetOtherTabView(scroll.Tab.Id);
            if (otherTabView?.SelectedItem is TabViewItem otherItem &&
                otherItem.Tag is string otherTabId &&
                _tabBridges.TryGetValue(otherTabId, out var otherBridgeGroup) &&
                otherBridgeGroup.Bridge != null)
            {
                _ = otherBridgeGroup.Bridge.SyncScrollFromPreviewAsync(scroll.FirstLine, scroll.Offset);
            }
        }

        public void HandleScrollSyncChanged(bool enabled)
        {
            _dispatcherQueue.TryEnqueue(async () =>
            {
                _setScrollSyncEnabled(enabled);

                foreach (var group in _tabBridges.Values)
                {
                    if (group.Bridge != null)
                    {
                        await group.Bridge.UpdateScrollSyncStateAsync(enabled);
                    }
                }

                _livePreviewController.PostScrollSyncState(enabled);
            });
        }

        private void ActivateOwnerTab(OpenedTab tab)
        {
            var ownerTabView = GetTabViewForTab(tab);
            if (ownerTabView != null && _editorWorkspace.ActiveTabView != ownerTabView)
            {
                _editorWorkspace.ActiveTabView = ownerTabView;
                if (ownerTabView.SelectedItem is TabViewItem activeTabItem)
                {
                    _tabSelectionController.QueueChanged(ownerTabView, activeTabItem);
                }
            }
        }

        private TabView? GetOtherTabView(string tabId)
        {
            if (IsTabInTabView(_primaryTabView, tabId))
            {
                return _secondaryTabView;
            }

            if (IsTabInTabView(_secondaryTabView, tabId))
            {
                return _primaryTabView;
            }

            return null;
        }

        private TabView? GetTabViewForTab(OpenedTab tab)
        {
            if (IsTabInTabView(_primaryTabView, tab.Id))
            {
                return _primaryTabView;
            }

            if (IsTabInTabView(_secondaryTabView, tab.Id))
            {
                return _secondaryTabView;
            }

            return null;
        }

        private static bool IsTabInTabView(TabView tabView, string tabId)
        {
            foreach (var item in tabView.TabItems)
            {
                if (item is TabViewItem tabItem &&
                    string.Equals(tabItem.Tag as string, tabId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
