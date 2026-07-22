using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TxtAIEditor.Controls
{
    public sealed partial class FavoritesSidebarView : UserControl
    {
        public FavoritesSidebarView()
        {
            InitializeComponent();
        }

        public Grid Root => RootGrid;
        public TextBlock Header => FavoritesHeaderText;
        public ListView Items => FavoritesListView;
        public ToggleButton FileTab => FavoritesFileTab;
        public ToggleButton FolderTab => FavoritesFolderTab;

        public event ItemClickEventHandler? ItemClick;
        public event RoutedEventHandler? RemoveClick;
        public event RoutedEventHandler? PinClick;
        public event RoutedEventHandler? TabClick;

        public void Localize(Func<string, string, string> getString)
        {
            FavoritesHeaderText.Text = getString("FavoritesHeader", "즐겨찾기 목록");
            FavoritesFileTab.Content = getString("FavoritesFileTab", "파일");
            FavoritesFolderTab.Content = getString("FavoritesFolderTab", "폴더");
        }

        private void OnFavoriteItemClick(object sender, ItemClickEventArgs e) => ItemClick?.Invoke(sender, e);
        private void OnRemoveFavoriteClick(object sender, RoutedEventArgs e) => RemoveClick?.Invoke(sender, e);
        private void OnFavoritePinClick(object sender, RoutedEventArgs e) => PinClick?.Invoke(sender, e);
        private void OnFavoritesTabClick(object sender, RoutedEventArgs e) => TabClick?.Invoke(sender, e);
    }
}
