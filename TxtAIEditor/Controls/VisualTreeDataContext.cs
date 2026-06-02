using Microsoft.UI.Xaml;

namespace TxtAIEditor.Controls
{
    public static class VisualTreeDataContext
    {
        public static T? FindFromOriginalSource<T>(object originalSource) where T : class
        {
            if (originalSource is not DependencyObject current)
            {
                return null;
            }

            while (current != null)
            {
                if (current is FrameworkElement { DataContext: T item })
                {
                    return item;
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
