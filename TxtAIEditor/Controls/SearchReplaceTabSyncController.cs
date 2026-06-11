using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        public SearchReplaceTabSyncController(
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabDirtyStateController tabDirtyStateController,
            Func<OpenedTab?> activeTabProvider,
            Func<string, Task> loadFileAsync,
            Action<OpenedTab> updateLivePreview)
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

                foreach (var tab in matchedTabs)
                {
                    if (_editorSessions.TryGetValue(tab.Id, out var session))
                    {
                        session.UpdateContentFromSync(content);
                    }

                    tab.Content = content;
                    tab.IsDirty = false;

                    if (IsTabCurrentlyVisible(tab))
                    {
                        tab.IsPendingReload = false;
                        if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            await bridgeGroup.Bridge.SetTextAsync(tab.Content, shouldFocus: false);
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

            string? targetTabId = _viewModel.Tabs
                .FirstOrDefault(tab => string.Equals(tab.FilePath, item.Path, StringComparison.OrdinalIgnoreCase))
                ?.Id;

            if (targetTabId != null &&
                _tabBridges.TryGetValue(targetTabId, out var bridgeGroup) &&
                bridgeGroup.WebView?.CoreWebView2 != null)
            {
                var revealMsg = new
                {
                    action = "revealLine",
                    lineNumber = item.LineNumber,
                    indexOfMatch = item.IndexOfMatch,
                    matchLength = item.MatchLength,
                    query
                };
                string json = JsonSerializer.Serialize(revealMsg);
                try
                {
                    bridgeGroup.WebView.CoreWebView2.PostWebMessageAsJson(json);
                }
                catch
                {
                }
            }
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
