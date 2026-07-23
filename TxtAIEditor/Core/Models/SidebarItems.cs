using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace TxtAIEditor.Core.Models
{
    public class RecentFileItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string DisplayPath { get; set; } = string.Empty;
        public string LastOpenedText { get; set; } = string.Empty;
        public bool IsFolder { get; set; } = false;
        public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";

        public Windows.UI.Color IconColor => IsFolder
            ? Windows.UI.Color.FromArgb(255, 255, 195, 0)
            : GetFileIconColor();

        private Windows.UI.Color GetFileIconColor()
        {
            try
            {
                if (Microsoft.UI.Xaml.Application.Current?.Resources != null &&
                    Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemControlForegroundAccentBrush", out var accent) &&
                    accent is Microsoft.UI.Xaml.Media.SolidColorBrush scb)
                {
                    return scb.Color;
                }
            }
            catch
            {
                // Fallback for thread access or unit test scenarios
            }
            return Windows.UI.Color.FromArgb(255, 0, 120, 215); // Fallback Accent Blue
        }
    }

    public class FavoriteItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string DisplayPath { get; set; } = string.Empty;
        public bool IsFolder { get; set; } = false;
        public bool IsPinned { get; set; } = false;
        public string IconGlyph => IsFolder ? "\uE8B7" : "\uE734";
        public Windows.UI.Color IconColor => IsFolder
            ? Windows.UI.Color.FromArgb(255, 255, 195, 0)
            : Windows.UI.Color.FromArgb(255, 255, 215, 0);
        public double PinOpacity => IsPinned ? 1.0 : 0.35;
    }

    public class GitFileItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public string ActionGlyph { get; set; } = string.Empty;
        public bool IsStaged { get; set; }
    }

    public class SearchResultItem
    {
        public string HeaderText => $"{System.IO.Path.GetFileName(Path)}:L{LineNumber}";
        public string DisplayPath => Path;
        public string Path { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string LineContent { get; set; } = string.Empty;
        public int IndexOfMatch { get; set; }
        public int MatchLength { get; set; }
        public bool CanReplace { get; set; } = true;
        public string LineHeader => $"Line {LineNumber}";
        public Visibility ReplaceButtonVisibility => CanReplace ? Visibility.Visible : Visibility.Collapsed;
    }

    public class SearchResultGroup : ObservableCollection<SearchResultItem>
    {
        public string Path { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(Path);
        public string DisplayPath => Path;
        public string RelativeDirectory { get; set; } = string.Empty;
        public int MatchCount => this.Count;

        public SearchResultGroup(string path, System.Collections.Generic.IEnumerable<SearchResultItem> items, string relativeDirectory = "") : base(items)
        {
            Path = path;
            RelativeDirectory = relativeDirectory;
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            base.OnCollectionChanged(e);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(MatchCount)));
        }
    }
}
