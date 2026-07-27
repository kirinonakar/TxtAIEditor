using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Core.Models
{
    public class OpenedTab : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; } = Guid.NewGuid().ToString();
        public string TabId => Id;

        private EditorDocument? _document;
        public string? DocumentId => _document?.DocumentId;

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

                    if (Origin is UntitledOrigin or LocalFileOrigin)
                    {
                        SetOrigin(string.IsNullOrWhiteSpace(value)
                            ? UntitledOrigin.Instance
                            : new LocalFileOrigin(value));
                    }
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
                    OnPropertyChanged(nameof(TabHeaderTitle));
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        // This is a bounded UI/search cache. EditorDocument.TextModel is the
        // authoritative text source for editor tabs.
        public string ContentPreview { get; set; } = string.Empty;

        private string _originalContent = string.Empty;
        public string OriginalContent
        {
            get => _document?.OriginalContent ?? _originalContent;
            set
            {
                if (_document != null)
                {
                    _document.OriginalContent = value ?? string.Empty;
                }
                else if (_originalContent != value)
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
                if (_document != null)
                {
                    return _document.OriginalLines;
                }

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
            get => _document?.OriginalLineEnding ?? _originalLineEnding;
            set
            {
                if (_document != null)
                {
                    _document.OriginalLineEnding = value;
                }
                else if (_originalLineEnding != value)
                {
                    _originalLineEnding = value;
                    OnPropertyChanged();
                }
            }
        }

        private string? _originalEncodingName;
        public string? OriginalEncodingName
        {
            get => _document?.OriginalEncodingName ?? _originalEncodingName;
            set
            {
                if (_document != null)
                {
                    _document.OriginalEncodingName = value;
                }
                else if (_originalEncodingName != value)
                {
                    _originalEncodingName = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDirty = false;
        public bool IsDirty
        {
            get => _document?.IsDirty ?? _isDirty;
            set
            {
                if (_document != null)
                {
                    _document.IsDirty = value;
                }
                else if (_isDirty != value)
                {
                    _isDirty = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string Language { get; set; } = "plaintext";
        public bool IsLanguageManuallySelected { get; set; } = false;
        private string _encodingName = "UTF-8";
        public string EncodingName
        {
            get => _document?.EncodingName ?? _encodingName;
            set
            {
                if (_document != null)
                {
                    _document.EncodingName = value ?? string.Empty;
                }
                else if (_encodingName != value)
                {
                    _encodingName = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        private bool _encodingWasAutoDetected = true;
        public bool EncodingWasAutoDetected
        {
            get => _document?.EncodingWasAutoDetected ?? _encodingWasAutoDetected;
            set
            {
                if (_document != null)
                {
                    _document.EncodingWasAutoDetected = value;
                }
                else if (_encodingWasAutoDetected != value)
                {
                    _encodingWasAutoDetected = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool InlineLivePreviewEnabled { get; set; } = false;
        private TabContentKind _contentKind = TabContentKind.Text;
        public TabContentKind ContentKind
        {
            get => _contentKind;
            set
            {
                if (_contentKind == value)
                {
                    return;
                }

                _contentKind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImageViewer));
                OnPropertyChanged(nameof(IsMediaViewer));
                OnPropertyChanged(nameof(IsPdfViewer));
                OnPropertyChanged(nameof(IsDocxViewer));
                OnPropertyChanged(nameof(IsOfficeDocumentViewer));
                OnPropertyChanged(nameof(IsNotebookViewer));
                OnPropertyChanged(nameof(IsHexViewer));
                OnPropertyChanged(nameof(IsCsvTableModeEnabled));
                OnPropertyChanged(nameof(IsReadOnlyViewer));
                OnPropertyChanged(nameof(TabHeaderTitle));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }

        public bool IsImageViewer
        {
            get => ContentKind == TabContentKind.Image;
            set => SetContentKindFlag(TabContentKind.Image, value);
        }

        public bool IsMediaViewer
        {
            get => ContentKind == TabContentKind.Media;
            set => SetContentKindFlag(TabContentKind.Media, value);
        }

        public bool IsPdfViewer
        {
            get => ContentKind == TabContentKind.Pdf;
            set => SetContentKindFlag(TabContentKind.Pdf, value);
        }

        public bool IsDocxViewer
        {
            get => ContentKind == TabContentKind.ExtractedDocumentText;
            set => SetContentKindFlag(TabContentKind.ExtractedDocumentText, value);
        }

        public bool IsOfficeDocumentViewer
        {
            get => ContentKind == TabContentKind.OfficeDocument;
            set => SetContentKindFlag(TabContentKind.OfficeDocument, value);
        }

        public bool IsNotebookViewer
        {
            get => ContentKind == TabContentKind.Notebook;
            set => SetContentKindFlag(TabContentKind.Notebook, value);
        }

        public bool IsHexViewer
        {
            get => ContentKind == TabContentKind.Hex;
            set => SetContentKindFlag(TabContentKind.Hex, value);
        }

        public bool IsCsvTableModeEnabled
        {
            get => ContentKind == TabContentKind.CsvTable;
            set => SetContentKindFlag(TabContentKind.CsvTable, value);
        }
        public string? HexSourceFilePath { get; set; }
        public bool IsReadOnlyTextFile { get; set; } = false;
        private DocumentOrigin _origin = UntitledOrigin.Instance;
        public DocumentOrigin Origin => _document?.Origin ?? _origin;

        public string? ArchiveSourcePath =>
            Origin is ArchiveEntryOrigin archive ? archive.ArchivePath : null;

        public string? ArchiveEntryPath =>
            Origin is ArchiveEntryOrigin archive ? archive.EntryPath : null;

        public bool IsArchiveEntry => Origin is ArchiveEntryOrigin;

        public string? RemotePath =>
            Origin is RemoteFileOrigin remote ? remote.RemotePath : null;

        public bool IsRemoteFile => Origin is RemoteFileOrigin;

        public void SetUntitledOrigin()
        {
            SetOrigin(UntitledOrigin.Instance);
        }

        public void SetLocalFileOrigin(string path)
        {
            SetOrigin(new LocalFileOrigin(path));
        }

        public void SetRemoteFileOrigin(string remotePath, string? cachePath = null)
        {
            SetOrigin(new RemoteFileOrigin(remotePath, cachePath ?? FilePath));
        }

        public void SetArchiveEntryOrigin(string archivePath, string entryPath)
        {
            SetOrigin(new ArchiveEntryOrigin(archivePath, entryPath));
        }

        public bool IsReadOnlyViewer => IsImageViewer || IsMediaViewer || IsPdfViewer || IsDocxViewer || IsOfficeDocumentViewer || IsHexViewer || IsReadOnlyTextFile || IsNotebookViewer;
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

        public string TabHeaderTitle => IsHexViewer ? $"[H] {Title}" : Title;

        public string DisplayTitle
        {
            get
            {
                return IsDirty ? $"{TabHeaderTitle} *" : TabHeaderTitle;
            }
        }

        internal void AttachDocument(EditorDocument document)
        {
            if (ReferenceEquals(_document, document))
            {
                return;
            }

            if (_document != null)
            {
                _document.StateChanged -= OnDocumentStateChanged;
            }

            _document = document;
            _document.StateChanged += OnDocumentStateChanged;
            OnPropertyChanged(nameof(DocumentId));
            OnPropertyChanged(nameof(EncodingName));
            OnPropertyChanged(nameof(EncodingWasAutoDetected));
            OnPropertyChanged(nameof(OriginalContent));
            OnPropertyChanged(nameof(OriginalLines));
            OnPropertyChanged(nameof(OriginalLineEnding));
            OnPropertyChanged(nameof(OriginalEncodingName));
            OnPropertyChanged(nameof(Origin));
            OnPropertyChanged(nameof(ArchiveSourcePath));
            OnPropertyChanged(nameof(ArchiveEntryPath));
            OnPropertyChanged(nameof(IsArchiveEntry));
            OnPropertyChanged(nameof(RemotePath));
            OnPropertyChanged(nameof(IsRemoteFile));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        }

        public void SetOrigin(DocumentOrigin origin)
        {
            DocumentOrigin normalized = origin ?? UntitledOrigin.Instance;
            if (_document != null)
            {
                _document.Origin = normalized;
                return;
            }

            if (_origin == normalized)
            {
                return;
            }

            _origin = normalized;
            NotifyOriginChanged();
        }

        private void SetContentKindFlag(TabContentKind kind, bool enabled)
        {
            if (enabled)
            {
                ContentKind = kind;
            }
            else if (ContentKind == kind)
            {
                ContentKind = TabContentKind.Text;
            }
        }

        private void OnDocumentStateChanged(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(EditorDocument.EncodingName):
                    OnPropertyChanged(nameof(EncodingName));
                    break;
                case nameof(EditorDocument.EncodingWasAutoDetected):
                    OnPropertyChanged(nameof(EncodingWasAutoDetected));
                    break;
                case nameof(EditorDocument.OriginalContent):
                    OnPropertyChanged(nameof(OriginalContent));
                    break;
                case nameof(EditorDocument.OriginalLines):
                    OnPropertyChanged(nameof(OriginalLines));
                    break;
                case nameof(EditorDocument.OriginalLineEnding):
                    OnPropertyChanged(nameof(OriginalLineEnding));
                    break;
                case nameof(EditorDocument.OriginalEncodingName):
                    OnPropertyChanged(nameof(OriginalEncodingName));
                    break;
                case nameof(EditorDocument.Origin):
                    NotifyOriginChanged();
                    break;
                case nameof(EditorDocument.IsDirty):
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(DisplayTitle));
                    break;
            }
        }

        private void NotifyOriginChanged()
        {
            OnPropertyChanged(nameof(Origin));
            OnPropertyChanged(nameof(ArchiveSourcePath));
            OnPropertyChanged(nameof(ArchiveEntryPath));
            OnPropertyChanged(nameof(IsArchiveEntry));
            OnPropertyChanged(nameof(RemotePath));
            OnPropertyChanged(nameof(IsRemoteFile));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
