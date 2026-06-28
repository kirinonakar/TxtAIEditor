using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace TxtAIEditor.Core.Models
{
    public class OpenedTab : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; } = Guid.NewGuid().ToString();

        public string PreviewResourceVersion { get; private set; } = NewPreviewResourceVersion();

        public void RefreshPreviewResourceVersion()
        {
            PreviewResourceVersion = NewPreviewResourceVersion();
        }

        private static string NewPreviewResourceVersion()
        {
            return Guid.NewGuid().ToString("N");
        }

        private string? _filePath;
        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _title = "제목 없음";
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string Content { get; set; } = string.Empty;

        private string _originalContent = string.Empty;
        public string OriginalContent
        {
            get => _originalContent;
            set
            {
                if (_originalContent != value)
                {
                    _originalContent = value ?? string.Empty;
                    _originalLinesCached = null;
                }
            }
        }

        private string[]? _originalLinesCached;
        public string[] OriginalLines
        {
            get
            {
                if (_originalLinesCached == null)
                {
                    _originalLinesCached = _originalContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                }
                return _originalLinesCached;
            }
        }

        private string? _originalLineEnding;
        public string? OriginalLineEnding
        {
            get => _originalLineEnding;
            set
            {
                if (_originalLineEnding != value)
                {
                    _originalLineEnding = value;
                    OnPropertyChanged();
                }
            }
        }

        private string? _originalEncodingName;
        public string? OriginalEncodingName
        {
            get => _originalEncodingName;
            set
            {
                if (_originalEncodingName != value)
                {
                    _originalEncodingName = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDirty = false;
        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (_isDirty != value)
                {
                    _isDirty = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string Language { get; set; } = "plaintext";
        public string EncodingName { get; set; } = "UTF-8";
        public bool EncodingWasAutoDetected { get; set; } = true;
        public bool InlineLivePreviewEnabled { get; set; } = false;
        public bool IsImageViewer { get; set; } = false;
        public bool IsPdfViewer { get; set; } = false;
        public bool IsDocxViewer { get; set; } = false;
        public bool IsOfficeDocumentViewer { get; set; } = false;
        public bool IsReadOnlyViewer => IsImageViewer || IsPdfViewer || IsDocxViewer || IsOfficeDocumentViewer;
        public string? EncryptionPassword { get; set; }

        private bool _isEncrypted = false;
        public bool IsEncrypted
        {
            get => _isEncrypted;
            set
            {
                if (_isEncrypted != value)
                {
                    _isEncrypted = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isPendingReload = false;
        public bool IsPendingReload
        {
            get => _isPendingReload;
            set
            {
                if (_isPendingReload != value)
                {
                    _isPendingReload = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayTitle => IsDirty ? $"{Title} *" : Title;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
