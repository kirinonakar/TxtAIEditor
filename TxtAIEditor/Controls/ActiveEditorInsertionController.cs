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
    public sealed class ActiveEditorInsertionController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Func<TabView> _activeTabViewProvider;
        private readonly TabDirtyStateController _tabDirtyStateController;

        private string? _activeStreamTabId;

        public ActiveEditorInsertionController(
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<TabView> activeTabViewProvider,
            TabDirtyStateController tabDirtyStateController)
        {
            _viewModel = viewModel;
            _tabBridges = tabBridges;
            _activeTabViewProvider = activeTabViewProvider;
            _tabDirtyStateController = tabDirtyStateController;
        }

        public async Task<bool> InsertTextAsync(string text)
        {
            if (!TryGetCurrentActiveEditorBridge(
                out string tabId,
                out TabViewItem? activeTabItem,
                out var bridgeGroup) ||
                activeTabItem == null)
            {
                return false;
            }

            FocusEditor(bridgeGroup.WebView, "editor");
            await bridgeGroup.Bridge.InsertTextAsync(text);

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                _tabDirtyStateController.MarkTabDirty(tab, activeTabItem);
                _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
            }

            return true;
        }

        public async Task<bool> BeginStreamAsync(string? targetTabId)
        {
            if (string.IsNullOrEmpty(targetTabId))
            {
                await EndStreamAsync(null);
            }

            if (!TryGetStreamEditorBridge(targetTabId, out string tabId, out var bridgeGroup))
            {
                return false;
            }

            if (string.IsNullOrEmpty(targetTabId))
            {
                _activeStreamTabId = tabId;
                FocusEditor(bridgeGroup.WebView, "editor for stream insert");
            }

            await bridgeGroup.Bridge.BeginStreamInsertAsync();
            return true;
        }

        public async Task<bool> InsertStreamTextAsync(string? targetTabId, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            string? tabId = string.IsNullOrEmpty(targetTabId) ? _activeStreamTabId : targetTabId;
            if (string.IsNullOrEmpty(tabId) ||
                !_tabBridges.TryGetValue(tabId, out var bridgeGroup) ||
                bridgeGroup.Bridge == null)
            {
                return false;
            }

            await bridgeGroup.Bridge.InsertStreamTextAsync(text);

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                _tabDirtyStateController.MarkTabDirty(tab);
                _tabDirtyStateController.PropagateDirtyStateToOtherTabs(tab);
            }

            return true;
        }

        public async Task EndStreamAsync(string? targetTabId)
        {
            string? tabId = string.IsNullOrEmpty(targetTabId) ? _activeStreamTabId : targetTabId;
            if (string.IsNullOrEmpty(targetTabId))
            {
                _activeStreamTabId = null;
            }

            if (!string.IsNullOrEmpty(tabId) &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup) &&
                bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.EndStreamInsertAsync();
            }
        }

        private bool TryGetCurrentActiveEditorBridge(
            out string tabId,
            out TabViewItem? activeTabItem,
            out (WebView2 WebView, MonacoBridge Bridge) bridgeGroup)
        {
            tabId = string.Empty;
            activeTabItem = null;
            bridgeGroup = default;

            var activeTabView = _activeTabViewProvider();
            if (activeTabView.SelectedItem is not TabViewItem selectedTabItem ||
                selectedTabItem.Tag is not string selectedTabId ||
                !_tabBridges.TryGetValue(selectedTabId, out var selectedBridgeGroup) ||
                selectedBridgeGroup.Bridge == null)
            {
                return false;
            }

            tabId = selectedTabId;
            activeTabItem = selectedTabItem;
            bridgeGroup = selectedBridgeGroup;
            return true;
        }

        private bool TryGetStreamEditorBridge(
            string? targetTabId,
            out string tabId,
            out (WebView2 WebView, MonacoBridge Bridge) bridgeGroup)
        {
            tabId = string.Empty;
            bridgeGroup = default;

            if (!string.IsNullOrEmpty(targetTabId) &&
                _tabBridges.TryGetValue(targetTabId, out var targetBridgeGroup) &&
                targetBridgeGroup.Bridge != null)
            {
                tabId = targetTabId;
                bridgeGroup = targetBridgeGroup;
                return true;
            }

            return TryGetCurrentActiveEditorBridge(out tabId, out _, out bridgeGroup);
        }

        private static void FocusEditor(WebView2 webView, string context)
        {
            try
            {
                webView.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to focus {context}: {ex.Message}");
            }
        }
    }
}
