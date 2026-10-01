using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class SearchReplaceTabSyncController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly TabDirtyStateController _tabDirtyStateController;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string, Task> _loadFileAsync;
        private readonly Action<OpenedTab> _updateLivePreview;
        private readonly EditorLineNavigationController _lineNavigationController;
        private readonly Func<OpenedTab, Task> _syncEditsToOtherTabsAsync;

        public SearchReplaceTabSyncController(
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabDirtyStateController tabDirtyStateController,
            Func<OpenedTab?> activeTabProvider,
            Func<string, Task> loadFileAsync,
            Action<OpenedTab> updateLivePreview,
            EditorLineNavigationController lineNavigationController,
            Func<OpenedTab, Task> syncEditsToOtherTabsAsync)
        {
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _tabDirtyStateController = tabDirtyStateController;
            _activeTabProvider = activeTabProvider;
            _loadFileAsync = loadFileAsync;
            _updateLivePreview = updateLivePreview;
            _lineNavigationController = lineNavigationController;
            _syncEditsToOtherTabsAsync = syncEditsToOtherTabsAsync;
        }

        public async Task<FileSearchSummary> SearchOpenedFileAsync(
            OpenedTab tab,
            string query,
            FileSearchOptions options,
            IFileSearchService fileSearchService,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            if (!_editorSessions.TryGetValue(tab.Id, out var session))
            {
                return new FileSearchSummary();
            }

            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup))
            {
                await bridgeGroup.Bridge.FlushPendingEditForSaveAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var regex = fileSearchService.BuildSearchRegex(query, options);
            string[] lines = session.Model.GetLines(1, session.Model.LineCount).ToArray();
            string path = tab.FilePath ?? tab.RemotePath ?? tab.Title;
            return await Task.Run(() =>
            {
                var results = new List<SearchResultItem>();
                int foundCount = 0;
                for (int index = 0; index < lines.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var match = regex.Match(lines[index]);
                    if (!match.Success)
                    {
                        continue;
                    }

                    results.Add(new SearchResultItem
                    {
                        Path = path,
                        LineNumber = index + 1,
                        LineContent = lines[index],
                        IndexOfMatch = match.Index,
                        MatchLength = match.Length,
                        CanReplace = !tab.IsReadOnlyViewer
                    });
                    foundCount++;
                    if (results.Count >= 100)
                    {
                        publishResults(results.ToArray());
                        results.Clear();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count > 0)
                {
                    publishResults(results.ToArray());
                }

                return new FileSearchSummary { FoundCount = foundCount };
            }, cancellationToken);
        }

        public async Task ReplaceOpenedFileAsync(
            OpenedTab tab,
            IReadOnlyList<SearchResultItem> results,
            string query,
            string replacement,
            FileSearchOptions options,
            IFileSearchService fileSearchService)
        {
            if (tab.IsReadOnlyViewer || !_editorSessions.TryGetValue(tab.Id, out var session) ||
                !_tabBridges.TryGetValue(tab.Id, out var bridgeGroup))
            {
                throw new InvalidOperationException();
            }

            await bridgeGroup.Bridge.FlushPendingEditForSaveAsync();
            var replacements = results.GroupBy(item => item.LineNumber)
                .Select(group => group.First())
                .Where(item => item.LineNumber >= 1 && item.LineNumber <= session.Model.LineCount &&
                    session.Model.GetLine(item.LineNumber) == item.LineContent)
                .Select(item => new LineReplacement(item.LineNumber, item.LineContent,
                    fileSearchService.ReplaceSearchMatches(item.LineContent, query, replacement, options)))
                .Where(item => item.BeforeText != item.AfterText)
                .OrderByDescending(item => item.LineNumber)
                .ToList();

            session.BeginUndoGroup();
            try
            {
                foreach (LineReplacement item in replacements)
                {
                    session.ApplyRangeEdit(item.LineNumber, 1, item.LineNumber, item.BeforeText.Length + 1, item.AfterText);
                }
            }
            finally
            {
                session.EndUndoGroup();
            }

            if (replacements.Count > 0)
            {
                session.RefreshTabContentPreview();
                _tabDirtyStateController.MarkTabDirty(tab);
                await bridgeGroup.Bridge.ResynchronizeModelAsync(session);
                await _syncEditsToOtherTabsAsync(tab);
                _updateLivePreview(tab);
            }
        }

        public async Task HighlightOpenedFileResultAsync(OpenedTab tab, SearchResultItem item, string query)
        {
            TabViewItem? tabItem = FindTabItem(tab.Id);
            if (tabItem != null)
            {
                if (_primaryTabView.TabItems.Contains(tabItem))
                {
                    _primaryTabView.SelectedItem = tabItem;
                }
                else
                {
                    _secondaryTabView.SelectedItem = tabItem;
                }
            }

            await _lineNavigationController.RevealTabLineAsync(
                tab.Id, item.LineNumber, item.IndexOfMatch, item.MatchLength, query);
        }

        public async Task HandleFileModifiedAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            var matchedTabs = _viewModel.Tabs
                .Where(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchedTabs.Count == 0)
            {
                return;
            }

            try
            {
                var readResult = await LineArrayTextModel.LoadFromFileAsync(filePath, "Auto");
                EditorDocumentSession? sharedSession = matchedTabs
                    .Select(tab => _editorSessions.TryGetValue(tab.Id, out var session) ? session : null)
                    .FirstOrDefault(session => session != null);
                sharedSession?.UpdateModelFromSync(readResult.Model);

                foreach (var tab in matchedTabs)
                {
                    EditorDocumentSession? session = null;
                    if (_editorSessions.TryGetValue(tab.Id, out session))
                    {
                        if (sharedSession != null &&
                            !session.SharesDocumentWith(sharedSession))
                        {
                            session.ShareDocumentWith(
                                sharedSession,
                                markViewSynchronized: false);
                        }
                        else
                        {
                            session.RefreshTabContentPreview();
                        }
                    }

                    tab.IsDirty = false;

                    if (IsTabCurrentlyVisible(tab))
                    {
                        tab.IsPendingReload = false;
                        if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            if (session != null)
                            {
                                await bridgeGroup.Bridge.ResynchronizeModelAsync(session);
                            }
                        }
                    }
                    else
                    {
                        tab.IsPendingReload = true;
                    }

                    if (FindTabItem(tab.Id) != null)
                    {
                        _tabDirtyStateController.CleanDirtyStateOnOtherTabs(tab);
                    }

                    if (tab == _activeTabProvider())
                    {
                        _updateLivePreview(tab);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to hot-reload replaced file '{filePath}': {ex.Message}");
            }
        }

        public async Task LoadAndHighlightAsync(SearchResultItem item, string query)
        {
            await _loadFileAsync(item.Path);
            await Task.Delay(250);
            await _lineNavigationController.RevealFileLineAsync(
                item.Path,
                item.LineNumber,
                item.IndexOfMatch,
                item.MatchLength,
                query);
        }

        private bool IsTabCurrentlyVisible(OpenedTab tab)
        {
            if (_primaryTabView.SelectedItem is TabViewItem primaryItem &&
                string.Equals(primaryItem.Tag as string, tab.Id, StringComparison.Ordinal))
            {
                return true;
            }

            if (_secondaryTabView.Visibility == Visibility.Visible &&
                _secondaryTabView.SelectedItem is TabViewItem secondaryItem &&
                string.Equals(secondaryItem.Tag as string, tab.Id, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private TabViewItem? FindTabItem(string tabId)
        {
            return _primaryTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tabId)
                ?? _secondaryTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tabId);
        }
    }
}
