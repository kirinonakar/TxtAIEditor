using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class EditorSplitLayoutController
    {
        public delegate OpenedTab OpenEditorTabCallback(
            string? filePath,
            string content,
            bool isReadOnly,
            string encodingName,
            bool encodingWasAutoDetected,
            bool isEncrypted,
            string? encryptionPassword);

        private readonly TopCommandBarPane _topToolbar;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Func<OpenedTab?> _getActiveTab;
        private readonly Func<OpenedTab> _openBlankTab;
        private readonly OpenEditorTabCallback _openEditorTab;
        private readonly Func<OpenedTab, bool> _isAnySameFileTabDirty;
        private readonly Action<OpenedTab, bool> _setDirtyStateForFileGroup;
        private readonly Action<OpenedTab> _reconcileTabDirtyState;
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;
        private readonly Action<OpenedTab, TabViewItem> _closeTabAndCleanup;
        private readonly Action<TabView, TabViewTabCloseRequestedEventArgs> _closeTabRequested;
        private readonly Action<TabView, TabViewItem> _queueTabSelectionChanged;
        private readonly Action _clearTabSelectionQueue;
        private readonly Action _updateWindowTitle;
        private readonly int _initialEditorLineWarmupCount;

        public EditorSplitLayoutController(
            TopCommandBarPane topToolbar,
            EditorWorkspacePane editorWorkspace,
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            Func<OpenedTab?> getActiveTab,
            Func<OpenedTab> openBlankTab,
            OpenEditorTabCallback openEditorTab,
            Func<OpenedTab, bool> isAnySameFileTabDirty,
            Action<OpenedTab, bool> setDirtyStateForFileGroup,
            Action<OpenedTab> reconcileTabDirtyState,
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync,
            Action<OpenedTab, TabViewItem> closeTabAndCleanup,
            Action<TabView, TabViewTabCloseRequestedEventArgs> closeTabRequested,
            Action<TabView, TabViewItem> queueTabSelectionChanged,
            Action clearTabSelectionQueue,
            Action updateWindowTitle,
            int initialEditorLineWarmupCount)
        {
            _topToolbar = topToolbar;
            _editorWorkspace = editorWorkspace;
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _getActiveTab = getActiveTab;
            _openBlankTab = openBlankTab;
            _openEditorTab = openEditorTab;
            _isAnySameFileTabDirty = isAnySameFileTabDirty;
            _setDirtyStateForFileGroup = setDirtyStateForFileGroup;
            _reconcileTabDirtyState = reconcileTabDirtyState;
            _syncEditsToOtherTabsAsync = syncEditsToOtherTabsAsync;
            _closeTabAndCleanup = closeTabAndCleanup;
            _closeTabRequested = closeTabRequested;
            _queueTabSelectionChanged = queueTabSelectionChanged;
            _clearTabSelectionQueue = clearTabSelectionQueue;
            _updateWindowTitle = updateWindowTitle;
            _initialEditorLineWarmupCount = initialEditorLineWarmupCount;

            WireEvents();
        }

        public void HandleTabViewGotFocus(object sender)
        {
            if (sender is not TabView tabView)
            {
                return;
            }

            _editorWorkspace.ActiveTabView = tabView;
            if (tabView.SelectedItem is TabViewItem activeTabItem)
            {
                _queueTabSelectionChanged(tabView, activeTabItem);
            }
            else
            {
                _clearTabSelectionQueue();
                _updateWindowTitle();
            }
        }

        private void WireEvents()
        {
            _topToolbar.SplitNoneClick += OnSplitNoneClick;
            _topToolbar.SplitVerticalClick += OnSplitVerticalClick;
            _topToolbar.SplitHorizontalClick += OnSplitHorizontalClick;
            _editorWorkspace.SecondaryAddTabButtonClick += OnEditorTabView2AddTabClick;
            _editorWorkspace.SecondaryTabCloseRequested += OnEditorTabView2TabCloseRequested;
            _editorWorkspace.SecondarySelectionChanged += OnEditorTabView2SelectionChanged;
            _editorWorkspace.TabViewGotFocus += OnTabViewGotFocus;
        }

        private void OpenSplitNewTab(OpenedTab? sourceTab = null)
        {
            var activeTab = sourceTab ?? _getActiveTab();

            if (activeTab == null)
            {
                _openBlankTab();
                return;
            }

            string? path = activeTab.FilePath;
            bool hasSourceSession = _editorSessions.TryGetValue(activeTab.Id, out var sourceSession);
            string content = hasSourceSession ? string.Empty : activeTab.Content ?? string.Empty;

            bool isDirty = _isAnySameFileTabDirty(activeTab);
            var newTab = _openEditorTab(
                path,
                content,
                false,
                activeTab.EncodingName,
                activeTab.EncodingWasAutoDetected,
                activeTab.IsEncrypted,
                activeTab.EncryptionPassword);

            // Split panes are two views of the same document, so the new view must use
            // the source view's resolved/manual language from its first render. The split
            // tab is intentionally created with empty content before sharing the session;
            // detecting its language at that point leaves extensionless/untitled files as
            // plaintext until the pane is clicked and StatusBarController detects it again.
            newTab.Language = activeTab.Language;
            newTab.IsLanguageManuallySelected = activeTab.IsLanguageManuallySelected;
            if (_tabBridges.TryGetValue(newTab.Id, out var newBridgeGroup) && newBridgeGroup.Bridge != null)
            {
                _ = newBridgeGroup.Bridge.SetLanguageAsync(newTab.Language);
            }

            if (hasSourceSession && sourceSession != null &&
                _editorSessions.TryGetValue(newTab.Id, out var splitViewSession))
            {
                splitViewSession.ShareDocumentWith(sourceSession);
                newTab.Content = activeTab.Content ?? string.Empty;
                newTab.OriginalContent = activeTab.OriginalContent;
                newTab.OriginalLineEnding = activeTab.OriginalLineEnding;
                newTab.OriginalEncodingName = activeTab.OriginalEncodingName;

                if (_tabBridges.TryGetValue(newTab.Id, out var splitBridgeGroup) && splitBridgeGroup.Bridge != null)
                {
                    _ = splitBridgeGroup.Bridge.ResetOriginalLinesAsync(
                        newTab.OriginalLines.Take(_initialEditorLineWarmupCount).ToArray());
                }
            }

            newTab.IsDirty = isDirty;
            _setDirtyStateForFileGroup(activeTab, isDirty);
            _reconcileTabDirtyState(newTab);
        }

        private async void OnSplitNoneClick(object sender, RoutedEventArgs e)
        {
            var preferredTab = _getActiveTab();
            if (preferredTab != null)
            {
                await _syncEditsToOtherTabsAsync(preferredTab);
            }

            _editorWorkspace.SetSplitMode(EditorSplitMode.None, () => _openBlankTab());
            UpdateEditorSplitViewState();
            MergeDuplicateFileTabsAfterUnsplit(preferredTab?.Id);
        }

        private void OnSplitVerticalClick(object sender, RoutedEventArgs e)
        {
            var sourceTab = _getActiveTab();
            _editorWorkspace.SetSplitMode(EditorSplitMode.Vertical, () => OpenSplitNewTab(sourceTab));
            UpdateEditorSplitViewState();
        }

        private void OnSplitHorizontalClick(object sender, RoutedEventArgs e)
        {
            var sourceTab = _getActiveTab();
            _editorWorkspace.SetSplitMode(EditorSplitMode.Horizontal, () => OpenSplitNewTab(sourceTab));
            UpdateEditorSplitViewState();
        }

        private void UpdateEditorSplitViewState()
        {
            bool isSplitView = _editorWorkspace.CurrentSplitMode != EditorSplitMode.None;
            foreach (var bridgeGroup in _tabBridges.Values)
            {
                if (bridgeGroup.Bridge != null)
                {
                    _ = bridgeGroup.Bridge.SetSplitViewAsync(isSplitView);
                }
            }
        }

        private void MergeDuplicateFileTabsAfterUnsplit(string? preferredTabId)
        {
            var tabItems = _primaryTabView.TabItems
                .OfType<TabViewItem>()
                .Where(item => item.Tag is string)
                .ToList();

            var groups = tabItems
                .Select(item =>
                {
                    string tabId = (string)item.Tag!;
                    var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                    return new { Item = item, Tab = tab, PathKey = NormalizeTabPath(tab?.FilePath) };
                })
                .Where(entry => entry.Tab != null && entry.PathKey != null)
                .GroupBy(entry => entry.PathKey!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

            foreach (var group in groups)
            {
                var keeper = group.FirstOrDefault(entry => entry.Tab!.Id == preferredTabId) ?? group.First();
                foreach (var duplicate in group.Where(entry => entry.Tab!.Id != keeper.Tab!.Id).ToList())
                {
                    MergeDuplicateTabState(keeper.Tab!, duplicate.Tab!);
                    _closeTabAndCleanup(duplicate.Tab!, duplicate.Item);
                }

                _primaryTabView.SelectedItem = keeper.Item;
            }

            _updateWindowTitle();
        }

        private void MergeDuplicateTabState(OpenedTab keeper, OpenedTab duplicate)
        {
            if (duplicate.IsDirty && !keeper.IsDirty &&
                _editorSessions.TryGetValue(duplicate.Id, out var duplicateSession))
            {
                string duplicateText = duplicateSession.GetText();
                if (_editorSessions.TryGetValue(keeper.Id, out var keeperSession))
                {
                    if (!keeperSession.SharesDocumentWith(duplicateSession))
                    {
                        keeperSession.UpdateContentFromSync(duplicateText, markUnsaved: true);
                    }
                    else
                    {
                        keeperSession.RefreshTabContentPreview();
                    }
                }
                keeper.Content = duplicateText;

                if (_tabBridges.TryGetValue(keeper.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    _editorSessions.TryGetValue(keeper.Id, out var keeperBridgeSession);
                    _ = bridgeGroup.Bridge.SetTextAsync(
                        duplicateText,
                        shouldFocus: false,
                        keeperBridgeSession?.DocumentId,
                        keeperBridgeSession?.DocumentVersion,
                        keeper.Id);
                    keeperBridgeSession?.MarkViewSynchronized(keeperBridgeSession.DocumentVersion);
                }
            }

            keeper.IsDirty |= duplicate.IsDirty;
            keeper.OriginalContent = duplicate.OriginalContent;
            keeper.OriginalLineEnding = duplicate.OriginalLineEnding;
            keeper.OriginalEncodingName = duplicate.OriginalEncodingName;
            keeper.EncodingName = duplicate.EncodingName;
            keeper.EncodingWasAutoDetected = duplicate.EncodingWasAutoDetected;
        }

        private void OnEditorTabView2AddTabClick(TabView sender, object args)
        {
            _editorWorkspace.ActiveTabView = sender;
            _openBlankTab();
            UpdateEditorSplitViewState();
        }

        private void OnEditorTabView2TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            _editorWorkspace.ActiveTabView = sender;
            _closeTabRequested(sender, args);
        }

        private void OnEditorTabView2SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _editorWorkspace.ActiveTabView = _secondaryTabView;
            if (_secondaryTabView.SelectedItem is TabViewItem activeTabItem)
            {
                _queueTabSelectionChanged(_secondaryTabView, activeTabItem);
            }
            else
            {
                _clearTabSelectionQueue();
            }
        }

        private void OnTabViewGotFocus(object sender, RoutedEventArgs e)
        {
            HandleTabViewGotFocus(sender);
        }

        private static string? NormalizeTabPath(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch
            {
                return filePath;
            }
        }
    }
}
