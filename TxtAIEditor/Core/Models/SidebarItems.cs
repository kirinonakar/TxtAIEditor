using System.Collections.Generic;

namespace TxtAIEditor.Core.Models
{
    public class RecentFileItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string LastOpenedText { get; set; } = string.Empty;
        public string IconGlyph => "\uE7C3";
    }

    public class FavoriteItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
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
        public string LineHeader => $"Line {LineNumber}";
    }

    public class SearchResultGroup : System.Collections.Generic.List<SearchResultItem>
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
    }
}
