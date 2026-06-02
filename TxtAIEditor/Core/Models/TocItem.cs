using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TxtAIEditor.Core.Models
{
    public sealed class TocItem : INotifyPropertyChanged
    {
        private string _displayText = string.Empty;
        private int _lineNumber;
        private string _iconGlyph = "\uE9D2";
        private Thickness _margin = new Thickness(0, 2, 0, 2);

        public string DisplayText
        {
            get => _displayText;
            set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    OnPropertyChanged();
                }
            }
        }

        public int LineNumber
        {
            get => _lineNumber;
            set
            {
                if (_lineNumber != value)
                {
                    _lineNumber = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LineDisplay));
                }
            }
        }

        public string LineDisplay => $"L{LineNumber}";

        public string IconGlyph
        {
            get => _iconGlyph;
            set
            {
                if (_iconGlyph != value)
                {
                    _iconGlyph = value;
                    OnPropertyChanged();
                }
            }
        }

        public Thickness Margin
        {
            get => _margin;
            set
            {
                if (_margin != value)
                {
                    _margin = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
