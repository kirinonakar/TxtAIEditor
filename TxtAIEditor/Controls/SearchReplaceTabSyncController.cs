using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class SearchReplaceTabSyncController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly TabDirtyStateController _tabDirtyStateController;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string, Task> _loadFileAsync;
        private readonly Action<OpenedTab> _updateLivePreview;
        private readonly EditorLineNavigationController _lineNavigationController;

        public SearchReplaceTabSyncController(
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabDirtyStateController tabDirtyStateController,
            Func<OpenedTab?> activeTabProvider,
            Func<string, Task> loadFileAsync,
            Action<OpenedTab> updateLivePreview,
            EditorLineNavigationController lineNavigationController)
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
                string content = readResult.Model.GetText();
                var updatedDocumentIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var tab in matchedTabs)
                {
                    EditorDocumentSession? session = null;
                    if (_editorSessions.TryGetValue(tab.Id, out session))
                    {
                        if (updatedDocumentIds.Add(session.DocumentId))
                        {
                            session.UpdateContentFromSync(content);
                        }
                        else
                        {
                            session.RefreshTabContentPreview();
                        }
                    }

                    tab.Content = content;
                    tab.IsDirty = false;

                    if (IsTabCurrentlyVisible(tab))
                    {
                        tab.IsPendingReload = false;
                        if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            await bridgeGroup.Bridge.SetTextAsync(
                                tab.Content,
                                shouldFocus: false,
                                session?.DocumentId,
                                session?.DocumentVersion,
                                tab.Id);
                            session?.MarkViewSynchronized(session.DocumentVersion);
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
