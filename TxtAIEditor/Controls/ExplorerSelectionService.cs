using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerSelectionService
    {
        private readonly LeftSidebarPane _leftSidebar;

        public ExplorerSelectionService(LeftSidebarPane leftSidebar)
        {
            _leftSidebar = leftSidebar;
        }

        public IReadOnlyList<ExplorerItem> GetSelectedItems(object? source)
        {
            ExplorerItem? contextItem = source as ExplorerItem;
            if (contextItem == null && source != null)
            {
                contextItem = GetItem(source);
            }

            var selectedItems = _leftSidebar.FileList.SelectedItems
                .OfType<ExplorerItem>()
                .ToList();
            if (contextItem != null && selectedItems.Any(item => ReferenceEquals(item, contextItem)))
            {
                return selectedItems;
            }

            var selectedTreeItems = GetTreeSelectedItems();
            if (contextItem != null && selectedTreeItems.Any(item => ReferenceEquals(item, contextItem)))
            {
                return selectedTreeItems;
            }

            return contextItem != null
                ? new[] { contextItem }
                : selectedTreeItems.Count > 0 ? selectedTreeItems : selectedItems;
        }

        public IReadOnlyList<ExplorerItem> GetTreeSelectedItems()
        {
            return _leftSidebar.ExplorerTree.SelectedItems
                .Select(GetTreeItem)
                .Where(item => item != null)
                .Cast<ExplorerItem>()
                .ToList();
        }

        public ExplorerItem? GetItem(object source)
        {
            if (source is FrameworkElement element)
            {
                ExplorerItem? dataContextItem = GetTreeItem(element.DataContext);
                if (dataContextItem != null)
                {
                    return dataContextItem;
                }

                if (element.Tag is ExplorerItem tagItem)
                {
                    return tagItem;
                }
            }

            return GetTreeItem(_leftSidebar.ExplorerTree.SelectedItem)
                ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        public TreeViewNode? FindTreeNodeFromElement(DependencyObject source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is TreeViewItem treeViewItem)
                {
                    return _leftSidebar.ExplorerTree.NodeFromContainer(treeViewItem);
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        public static ExplorerItem? GetTreeItem(object? value)
        {
            return value as ExplorerItem
                ?? (value as TreeViewNode)?.Content as ExplorerItem;
        }
    }
}
