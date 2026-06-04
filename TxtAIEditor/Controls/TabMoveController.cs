using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace TxtAIEditor.Controls
{
    public sealed class TabMoveController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly Func<TabView> _activeTabViewProvider;

        public TabMoveController(
            MainWindowViewModel viewModel,
            Func<TabView> activeTabViewProvider)
        {
            _viewModel = viewModel;
            _activeTabViewProvider = activeTabViewProvider;
        }

        public void MoveLeft()
        {
            var activeTabView = _activeTabViewProvider();
            if (activeTabView == null || activeTabView.TabItems.Count <= 1)
            {
                return;
            }

            int index = activeTabView.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            if (ShouldReorderTab())
            {
                ReorderTab(activeTabView, index, index - 1);
            }
            else
            {
                activeTabView.SelectedIndex = index > 0
                    ? index - 1
                    : activeTabView.TabItems.Count - 1;
            }
        }

        public void MoveRight()
        {
            var activeTabView = _activeTabViewProvider();
            if (activeTabView == null || activeTabView.TabItems.Count <= 1)
            {
                return;
            }

            int index = activeTabView.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            if (ShouldReorderTab())
            {
                ReorderTab(activeTabView, index, index + 1);
            }
            else
            {
                activeTabView.SelectedIndex = index < activeTabView.TabItems.Count - 1
                    ? index + 1
                    : 0;
            }
        }

        private void ReorderTab(TabView tabView, int fromIndex, int toIndex)
        {
            if (toIndex < 0 || toIndex >= tabView.TabItems.Count)
            {
                return;
            }

            if (tabView.TabItems[fromIndex] is not TabViewItem item)
            {
                return;
            }

            tabView.TabItems.RemoveAt(fromIndex);
            tabView.TabItems.Insert(toIndex, item);
            tabView.SelectedIndex = toIndex;
            ReorderViewModelTab(item, toIndex > fromIndex ? 1 : -1);
        }

        private void ReorderViewModelTab(TabViewItem item, int offset)
        {
            if (item.Tag is not string tabId)
            {
                return;
            }

            OpenedTab? tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null)
            {
                return;
            }

            int tabIndex = _viewModel.Tabs.IndexOf(tab);
            int targetIndex = tabIndex + offset;
            if (tabIndex < 0 || targetIndex < 0 || targetIndex >= _viewModel.Tabs.Count)
            {
                return;
            }

            _viewModel.Tabs.RemoveAt(tabIndex);
            _viewModel.Tabs.Insert(targetIndex, tab);
        }

        private static bool ShouldReorderTab()
        {
            var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
                        CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
                         CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            return ctrl || shift;
        }
    }
}
