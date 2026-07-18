using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentAttachmentState
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string? TextContent { get; set; }
        public LlmMessageAttachment? ImageContent { get; set; }
        public int EstimatedTokens { get; set; }
        public bool IsPathOnlyDocument { get; set; }
        public bool IsImage => ImageContent != null;
    }

    internal sealed class AgentAttachmentController
    {
        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly Func<bool> _isRunningProvider;
        private readonly Action _contextChanged;
        private readonly Func<string, double> _estimateTokenCount;
        private readonly PdfTextExtractionService _pdfTextExtractionService;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly List<AgentAttachmentState> _attachments = new();

        public AgentAttachmentController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentDisplayLocalizer displayText,
            Func<bool> isRunningProvider,
            Action contextChanged,
            Func<string, double> estimateTokenCount,
            PdfTextExtractionService pdfTextExtractionService,
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _displayText = displayText;
            _isRunningProvider = isRunningProvider;
            _contextChanged = contextChanged;
            _estimateTokenCount = estimateTokenCount;
            _pdfTextExtractionService = pdfTextExtractionService;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
        }

        public IReadOnlyList<AgentAttachmentState> Attachments => _attachments;

        public int Count => _attachments.Count;

        public int EstimatedImageTokens =>
            _attachments.Where(attachment => attachment.IsImage).Sum(attachment => attachment.EstimatedTokens);

        public IReadOnlyList<LlmMessageAttachment> GetImageAttachments()
        {
            return _attachments
                .Select(attachment => attachment.ImageContent)
                .Where(attachment => attachment != null)
                .Cast<LlmMessageAttachment>()
                .ToList();
        }

        public List<AgentAttachmentState> GetState()
        {
            return _attachments.Select(CloneAttachment).ToList();
        }

        public void Replace(IEnumerable<AgentAttachmentState>? attachments)
        {
            _attachments.Clear();
            if (attachments != null)
            {
                _attachments.AddRange(attachments.Select(CloneAttachment));
            }

            RefreshAttachments();
            _contextChanged();
        }

        public async Task AddAttachmentsAsync()
        {
            if (_isRunningProvider())
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);

            foreach (string extension in GetAttachmentPickerExtensions())
            {
                picker.FileTypeFilter.Add(extension);
            }

            try
            {
                _beforeDialog?.Invoke();
                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                await AddStorageFilesAsync(files);
            }
            finally
            {
                _afterDialog?.Invoke();
            }
        }

        public async Task AddDroppedFilesAsync(IEnumerable<string> filePaths)
        {
            if (_isRunningProvider())
            {
                return;
            }

            var storageFiles = new List<StorageFile>();
            foreach (string filePath in filePaths)
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    storageFiles.Add(file);
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("AgentAttachmentErrorTitle", "Attachment Error"),
                        string.Format(
                            _getString("AgentAttachmentErrorFormat", "An error occurred while adding the attachment: {0}"),
                            ex.Message));
                }
            }

            if (storageFiles.Count > 0)
            {
                await AddStorageFilesAsync(storageFiles);
            }
        }

        private async Task AddStorageFilesAsync(IEnumerable<StorageFile> files)
        {
            foreach (var file in files)
            {
                try
                {
                    var attachment = await CreateAttachmentAsync(file);
                    if (attachment != null)
                    {
                        _attachments.RemoveAll(existing =>
                            string.Equals(existing.Path, attachment.Path, StringComparison.OrdinalIgnoreCase));
                        _attachments.Add(attachment);
                    }
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("AgentAttachmentErrorTitle", "Attachment Error"),
                        string.Format(
                            _getString("AgentAttachmentErrorFormat", "An error occurred while adding the attachment: {0}"),
                            ex.Message));
                }
            }

            RefreshAttachments();
            _contextChanged();
        }

        public void RemoveAttachment(string id)
        {
            if (_isRunningProvider())
            {
                return;
            }

            _attachments.RemoveAll(attachment => string.Equals(attachment.Id, id, StringComparison.Ordinal));
            RefreshAttachments();
            _contextChanged();
        }

        public void Clear()
        {
            _attachments.Clear();
            RefreshAttachments();
        }

        private static AgentAttachmentState CloneAttachment(AgentAttachmentState attachment)
        {
            return new AgentAttachmentState
            {
                Id = attachment.Id,
                Path = attachment.Path,
                DisplayName = attachment.DisplayName,
                Detail = attachment.Detail,
                TextContent = attachment.TextContent,
                ImageContent = CloneImageAttachment(attachment.ImageContent),
                EstimatedTokens = attachment.EstimatedTokens,
                IsPathOnlyDocument = attachment.IsPathOnlyDocument
            };
        }

        private static LlmMessageAttachment? CloneImageAttachment(LlmMessageAttachment? attachment)
        {
            if (attachment == null)
            {
                return null;
            }

            return new LlmMessageAttachment
            {
                DisplayName = attachment.DisplayName,
                MimeType = attachment.MimeType,
                Base64Data = attachment.Base64Data,
                Width = attachment.Width,
                Height = attachment.Height,
                EstimatedTokens = attachment.EstimatedTokens
            };
        }

        private static IReadOnlyList<string> GetAttachmentPickerExtensions()
        {
            return new[] { "*" };
        }

        private Task<AgentAttachmentState?> CreateAttachmentAsync(StorageFile file)
        {
            string displayName = file.Name;
            string path = file.Path ?? displayName;

            int pathOnlyEstimatedTokens = (int)Math.Round(_estimateTokenCount(path));
            return Task.FromResult<AgentAttachmentState?>(new AgentAttachmentState
            {
                Path = path,
                DisplayName = displayName,
                Detail = _getString("AgentAttachmentDocumentPathOnlyDetail", "Document, path only"),
                EstimatedTokens = pathOnlyEstimatedTokens,
                IsPathOnlyDocument = true
            });
        }

        private void RefreshAttachments()
        {
            var items = _attachments
                .Select(attachment => new AgentAttachmentItem
                {
                    Id = attachment.Id,
                    DisplayName = attachment.DisplayName,
                    Detail = attachment.Detail,
                    TokenText = _displayText.FormatInlineTokenCount(attachment.EstimatedTokens),
                    RemoveTooltip = _getString("AgentRemoveAttachmentTooltip", "Remove attachment"),
                    IconGlyph = attachment.IsImage ? "\uEB9F" : "\uE8A5"
                })
                .ToList();
            _agentPane.UpdateAttachments(items);
        }
    }
}
