using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentImageToolService
    {
        private const int MaxReadImageDimension = 1024;
        private const long MaxReadImageFileBytes = 25L * 1024L * 1024L;

        private readonly AgentWorkspaceFileResolver _workspace;

        public AgentImageToolService(AgentWorkspaceFileResolver workspace)
        {
            _workspace = workspace;
        }

        public async Task<AgentReadImageResult> ReadImageAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new AgentReadImageResult
                {
                    TranscriptText = "read_image failed: path is empty. Provide the exact image file path from the user task or list_files."
                };
            }

            string fullPath;
            try
            {
                fullPath = _workspace.ResolveInsideWorkspace(path, allowOutside: true);
            }
            catch (Exception ex)
            {
                return new AgentReadImageResult
                {
                    TranscriptText = $"read_image failed: {ex.Message}"
                };
            }

            if (!File.Exists(fullPath))
            {
                return new AgentReadImageResult
                {
                    TranscriptText = _workspace.BuildMissingFileMessage("read_image", path)
                };
            }

            string mimeType = GetImageMimeType(fullPath);
            if (!IsSupportedImageExtension(fullPath))
            {
                return new AgentReadImageResult
                {
                    TranscriptText = $"read_image failed: unsupported image type '{Path.GetExtension(fullPath)}'. Supported types: .png, .jpg, .jpeg, .webp, .avif, .bmp, .gif, .tif, .tiff."
                };
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxReadImageFileBytes)
            {
                return new AgentReadImageResult
                {
                    TranscriptText = $"read_image failed: image is too large ({fileInfo.Length:N0} bytes). Maximum supported size is {MaxReadImageFileBytes:N0} bytes."
                };
            }

            try
            {
                LlmMessageAttachment attachment = await CreateImageAttachmentAsync(fullPath, mimeType);
                string relativePath = _workspace.RelativePath(fullPath);
                string resizedNote = string.Equals(attachment.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : " The image was resized and converted to JPEG for model input.";

                return new AgentReadImageResult
                {
                    Attachment = attachment,
                    TranscriptText =
                        $"read_image attached: {relativePath}\n" +
                        $"mime: {attachment.MimeType}\n" +
                        $"dimensions: {attachment.Width}x{attachment.Height}\n" +
                        $"estimated_tokens: {attachment.EstimatedTokens}\n" +
                        $"The image is attached to the next model call for visual inspection.{resizedNote}"
                };
            }
            catch (Exception ex)
            {
                return new AgentReadImageResult
                {
                    TranscriptText = $"read_image failed: could not decode image '{path}': {ex.Message}"
                };
            }
        }

        private static async Task<LlmMessageAttachment> CreateImageAttachmentAsync(string fullPath, string mimeType)
        {
            using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using IRandomAccessStream input = fileStream.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            uint originalWidth = decoder.PixelWidth;
            uint originalHeight = decoder.PixelHeight;
            uint outputWidth = originalWidth;
            uint outputHeight = originalHeight;
            string outputMimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
            byte[] bytes;

            bool shouldTranscode =
                Math.Max(originalWidth, originalHeight) > MaxReadImageDimension ||
                outputMimeType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase) ||
                outputMimeType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase);

            if (shouldTranscode)
            {
                double scale = Math.Min(1.0, MaxReadImageDimension / (double)Math.Max(originalWidth, originalHeight));
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

            return new LlmMessageAttachment
            {
                DisplayName = Path.GetFileName(fullPath),
                MimeType = outputMimeType,
                Base64Data = Convert.ToBase64String(bytes),
                Width = (int)outputWidth,
                Height = (int)outputHeight,
                EstimatedTokens = EstimateImageTokens((int)outputWidth, (int)outputHeight)
            };
        }

        private static async Task<byte[]> ReadRandomAccessStreamAsync(IRandomAccessStream stream)
        {
            using var managedStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await managedStream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static int EstimateImageTokens(int width, int height)
        {
            int tilesWide = Math.Max(1, (int)Math.Ceiling(width / 512.0));
            int tilesHigh = Math.Max(1, (int)Math.Ceiling(height / 512.0));
            return 85 + (tilesWide * tilesHigh * 170);
        }

        private static bool IsSupportedImageExtension(string path)
        {
            return SupportedFileTypes.IsImageFile(path);
        }

        private static string GetImageMimeType(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tif" or ".tiff" => "image/tiff",
                _ => "application/octet-stream"
            };
        }
    }
}
