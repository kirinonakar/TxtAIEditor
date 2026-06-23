using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace TxtAIEditor
{
    public class ExplorerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsFolder { get; set; } = false;
        public DateTime ModifiedTime { get; set; } = DateTime.MinValue;

        private string _subPath = string.Empty;
        public string SubPath
        {
            get => _subPath;
            set
            {
                if (_subPath != value)
                {
                    _subPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubPathVisibility));
                }
            }
        }

        public Microsoft.UI.Xaml.Visibility SubPathVisibility =>
            string.IsNullOrEmpty(_subPath) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        public enum GitStatusType
        {
            Clean,
            Modified,
            Added,
            Ignored
        }

        private GitStatusType _gitStatus = GitStatusType.Clean;
        public GitStatusType GitStatus
        {
            get => _gitStatus;
            set
            {
                if (_gitStatus != value)
                {
                    _gitStatus = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ForegroundColor));
                    OnPropertyChanged(nameof(GitBadge));
                    OnPropertyChanged(nameof(GitBadgeVisibility));
                }
            }
        }

        private bool _isDark = false;
        public bool IsDark
        {
            get => _isDark;
            set
            {
                if (_isDark != value)
                {
                    _isDark = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ForegroundColor));
                }
            }
        }

        public Microsoft.UI.Xaml.Media.Brush ForegroundColor
        {
            get
            {
                return _gitStatus switch
                {
                    GitStatusType.Modified => new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        _isDark ? Windows.UI.Color.FromArgb(255, 226, 192, 141) : Windows.UI.Color.FromArgb(255, 176, 125, 5)),
                    GitStatusType.Added => new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        _isDark ? Windows.UI.Color.FromArgb(255, 129, 184, 139) : Windows.UI.Color.FromArgb(255, 40, 167, 69)),
                    GitStatusType.Ignored => new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        _isDark ? Windows.UI.Color.FromArgb(255, 106, 115, 125) : Windows.UI.Color.FromArgb(255, 149, 157, 165)),
                    _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        _isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black)
                };
            }
        }

        public string GitBadge => _gitStatus switch
        {
            GitStatusType.Modified => "M",
            GitStatusType.Added => "U",
            GitStatusType.Ignored => "I",
            _ => string.Empty
        };

        public Microsoft.UI.Xaml.Visibility GitBadgeVisibility =>
            _gitStatus == GitStatusType.Clean ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        public string IconGlyph => IsFolder ? "\uED41" : GetFileIconGlyph(Name);

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

        public ObservableCollection<ExplorerItem> Children { get; } = new ObservableCollection<ExplorerItem>();

        public bool HasUnrealizedChildren
        {
            get => IsFolder && Children.Count == 0;
            set
            {
                // Unused but needed for XAML binding syntax
            }
        }

        private static string GetFileIconGlyph(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".txt" => "\uE8A5", // Document icon
                ".md" => "\uE8A5",
                ".markdown" => "\uE8A5",
                ".csv" => "\uE9D2",
                ".xlsx" => "\uE9D2",
                ".xls" => "\uE9D2",
                ".pptx" => "\uE161",
                ".ppt" => "\uE161",
                ".html" => "\uE743", // Web icon
                ".htm" => "\uE743",
                ".css" => "\uE743",
                ".js" => "\uE94A",  // Code icon
                ".ts" => "\uE94A",
                ".cs" => "\uE74C",  // C# Developer icon or custom code glyph
                ".xaml" => "\uF158",
                ".xml" => "\uF158",
                ".json" => "\uE94A",
                ".yaml" => "\uE94A",
                ".yml" => "\uE94A",
                ".png" => "\uEB9F", // Picture icon
                ".jpg" => "\uEB9F",
                ".jpeg" => "\uEB9F",
                ".gif" => "\uEB9F",
                ".bmp" => "\uEB9F",
                ".webp" => "\uEB9F",
                ".pdf" => "\uE12A",
                ".docx" => "\uE161",
                ".doc" => "\uE161",
                ".hwpx" => "\uE161",
                _ => "\uE160"       // Generic file icon
            };
        }
    }
}
