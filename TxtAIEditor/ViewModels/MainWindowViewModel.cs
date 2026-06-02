using System.Collections.ObjectModel;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.ViewModels
{
    public sealed class MainWindowViewModel
    {
        public ObservableCollection<FavoriteItem> Favorites { get; } = new ObservableCollection<FavoriteItem>();
        public ObservableCollection<RecentFileItem> RecentFiles { get; } = new ObservableCollection<RecentFileItem>();
        public ObservableCollection<SnippetItem> Snippets { get; } = new ObservableCollection<SnippetItem>();
        public ObservableCollection<GitFileItem> GitFiles { get; } = new ObservableCollection<GitFileItem>();
        public ObservableCollection<SearchResultItem> SearchResults { get; } = new ObservableCollection<SearchResultItem>();
        public ObservableCollection<ExplorerItem> ExplorerItems { get; } = new ObservableCollection<ExplorerItem>();
        public ObservableCollection<OpenedTab> Tabs { get; } = new ObservableCollection<OpenedTab>();
        public ObservableCollection<TocItem> TocItems { get; } = new ObservableCollection<TocItem>();
    }
}
