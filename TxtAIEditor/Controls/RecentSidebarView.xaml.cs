using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TxtAIEditor.Controls
{
    public sealed partial class RecentSidebarView : UserControl
    {
        public RecentSidebarView()
        {
            InitializeComponent();
        }

        public Grid Root => RootGrid;
        public TextBlock Header => RecentFilesHeaderText;
        public ListView Items => RecentFilesListView;
        public ToggleButton FileTab => RecentFileTab;
        public ToggleButton FolderTab => RecentFolderTab;

        public event ItemClickEventHandler? ItemClick;
        public event RoutedEventHandler? RemoveClick;
        public event RoutedEventHandler? TabClick;

        public void Localize(Func<string, string, string> getString)
        {
            RecentFilesHeaderText.Text = getString("RecentFilesHeader", "최근 파일");
            RecentFileTab.Content = getString("RecentFileTab", "파일");
            RecentFolderTab.Content = getString("RecentFolderTab", "폴더");
        }

        private void OnRecentFileItemClick(object sender, ItemClickEventArgs e) => ItemClick?.Invoke(sender, e);
        private void OnRemoveRecentFileClick(object sender, RoutedEventArgs e) => RemoveClick?.Invoke(sender, e);
        private void OnRecentTabClick(object sender, RoutedEventArgs e) => TabClick?.Invoke(sender, e);
    }
}
