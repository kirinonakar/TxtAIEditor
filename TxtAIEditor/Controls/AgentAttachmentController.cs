using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
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
        private const int MaxAttachmentTextChars = 120_000;
        private const int MaxImageDimension = 1024;

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
            finally
            {
                _afterDialog?.Invoke();
            }
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

        private static IReadOnlyList<string> GetAttachmentPickerExtensions()
        {
            return new[] { "*" };
        }

        private async Task<AgentAttachmentState?> CreateAttachmentAsync(StorageFile file)
        {
            string displayName = file.Name;
            string path = file.Path ?? displayName;
            string mimeType = GetMimeType(file);

            if (IsDocumentFile(file))
            {
                int pathOnlyEstimatedTokens = (int)Math.Round(_estimateTokenCount(path));
                return new AgentAttachmentState
                {
                    Path = path,
                    DisplayName = displayName,
                    Detail = _getString("AgentAttachmentDocumentPathOnlyDetail", "Document, path only"),
                    EstimatedTokens = pathOnlyEstimatedTokens,
                    IsPathOnlyDocument = true
                };
            }

            if (IsImageFile(file, mimeType))
            {
                var image = await CreateImageAttachmentAsync(file, displayName, mimeType);
                return new AgentAttachmentState
                {
                    Path = path,
                    DisplayName = displayName,
                    Detail = $"{image.MimeType}, {image.Width}x{image.Height}",
                    ImageContent = image,
                    EstimatedTokens = image.EstimatedTokens
                };
            }

            string text = await ReadAttachmentTextAsync(file);
            int estimatedTokens = (int)Math.Round(_estimateTokenCount(text));
            bool truncated = text.Length >= MaxAttachmentTextChars;
            return new AgentAttachmentState
            {
                Path = path,
                DisplayName = displayName,
                Detail = truncated
                    ? string.Format(_getString("AgentAttachmentFileTruncatedDetail", "File, included first {0:N0} chars"), text.Length)
                    : string.Format(_getString("AgentAttachmentFileDetail", "File, {0:N0} chars"), text.Length),
                TextContent = text,
                EstimatedTokens = estimatedTokens
            };
        }

        private async Task<LlmMessageAttachment> CreateImageAttachmentAsync(StorageFile file, string displayName, string mimeType)
        {
            using IRandomAccessStream input = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            uint originalWidth = decoder.PixelWidth;
            uint originalHeight = decoder.PixelHeight;
            uint outputWidth = originalWidth;
            uint outputHeight = originalHeight;
            string outputMimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
            byte[] bytes;

            if (Math.Max(originalWidth, originalHeight) > MaxImageDimension)
            {
                double scale = MaxImageDimension / (double)Math.Max(originalWidth, originalHeight);
                outputWidth = Math.Max(1, (uint)Math.Round(originalWidth * scale));
                outputHeight = Math.Max(1, (uint)Math.Round(originalHeight * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = outputWidth,
                    ScaledHeight = outputHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                using var output = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    outputWidth,
                    outputHeight,
                    96,
                    96,
                    pixelData.DetachPixelData());
                await encoder.FlushAsync();
                output.Seek(0);
                bytes = await ReadRandomAccessStreamAsync(output);
                outputMimeType = "image/jpeg";
            }
            else
            {
                input.Seek(0);
                bytes = await ReadRandomAccessStreamAsync(input);
            }

            int estimatedTokens = EstimateImageTokens((int)outputWidth, (int)outputHeight);
            return new LlmMessageAttachment
            {
                DisplayName = displayName,
                MimeType = outputMimeType,
                Base64Data = Convert.ToBase64String(bytes),
                Width = (int)outputWidth,
                Height = (int)outputHeight,
                EstimatedTokens = estimatedTokens
            };
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

        private static async Task<byte[]> ReadRandomAccessStreamAsync(IRandomAccessStream stream)
        {
            using var managedStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await managedStream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static async Task<string> ReadAttachmentTextAsync(StorageFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                char[] buffer = new char[MaxAttachmentTextChars + 1];
                int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                string text = new string(buffer, 0, Math.Min(read, MaxAttachmentTextChars));
                return StripBinaryControlCharacters(text);
            }

            string fallback = await FileIO.ReadTextAsync(file);
            if (fallback.Length > MaxAttachmentTextChars)
            {
                fallback = fallback.Substring(0, MaxAttachmentTextChars);
            }

            return StripBinaryControlCharacters(fallback);
        }

        private static int EstimateImageTokens(int width, int height)
        {
            int tilesWide = Math.Max(1, (int)Math.Ceiling(width / 512.0));
            int tilesHigh = Math.Max(1, (int)Math.Ceiling(height / 512.0));
            return 85 + (tilesWide * tilesHigh * 170);
        }

        private static string StripBinaryControlCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t' || ch >= ' ')
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString();
        }

        private static bool IsImageFile(StorageFile file, string mimeType)
        {
            if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string extension = Path.GetExtension(file.Name);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDocumentFile(StorageFile file)
        {
            string extension = Path.GetExtension(file.Name);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMimeType(StorageFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.ContentType) &&
                !file.ContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                return file.ContentType;
            }

            string extension = Path.GetExtension(file.Name).ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                _ => "text/plain"
            };
        }
    }
}
