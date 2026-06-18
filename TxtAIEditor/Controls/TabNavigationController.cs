using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class TabNavigationController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;

        public TabNavigationController(
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            TabView primaryTabView,
            TabView secondaryTabView)
        {
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
        }

        public TabView GetCurrentActiveTabView()
        {
            return _editorWorkspace.GetCurrentActiveTabView();
        }

        public OpenedTab? GetActiveTab()
        {
            var activeTabView = GetCurrentActiveTabView();
            if (activeTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId)
            {
                return _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            }

            return null;
        }

        public bool IsOpen(OpenedTab tab)
        {
            return Contains(_primaryTabView, tab.Id) || Contains(_secondaryTabView, tab.Id);
        }

        public bool Contains(TabView tabView, string tabId)
        {
            return FindItem(tabView, tabId) != null;
        }

        public TabView? GetTabView(OpenedTab tab)
        {
            if (Contains(_primaryTabView, tab.Id))
            {
                return _primaryTabView;
            }

            if (Contains(_secondaryTabView, tab.Id))
            {
                return _secondaryTabView;
            }

            return null;
        }

        public TabView? GetOppositeTabView(OpenedTab tab)
        {
            if (Contains(_primaryTabView, tab.Id))
            {
                return _secondaryTabView;
            }

            if (Contains(_secondaryTabView, tab.Id))
            {
                return _primaryTabView;
            }

            return null;
        }

        public TabView? GetTabViewForItem(TabViewItem tabItem)
        {
            if (_primaryTabView.TabItems.Contains(tabItem))
            {
                return _primaryTabView;
            }

            if (_secondaryTabView.TabItems.Contains(tabItem))
            {
                return _secondaryTabView;
            }

            return null;
        }

        public static TabViewItem? FindItem(TabView tabView, string tabId)
        {
            foreach (var item in tabView.TabItems)
            {
                if (item is TabViewItem tabItem &&
                    string.Equals(tabItem.Tag as string, tabId, StringComparison.Ordinal))
                {
                    return tabItem;
                }
            }

            return null;
        }
    }
}
