using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace TxtAIEditor.Core.Services
{
    internal readonly record struct ComposedImageFrame(
        uint Width,
        uint Height,
        byte[] Pixels);

    internal static class AnimatedImageFrameComposer
    {
        private const string GifLeftProperty = "/imgdesc/Left";
        private const string GifTopProperty = "/imgdesc/Top";
        private const string GifWidthProperty = "/imgdesc/Width";
        private const string GifHeightProperty = "/imgdesc/Height";
        private const string GifDisposalProperty = "/grctlext/Disposal";

        public static bool IsSupportedAnimationPath(string sourcePath)
        {
            string extension = Path.GetExtension(sourcePath);
            return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task ComposeAsync(
            BitmapDecoder decoder,
            string sourcePath,
            uint frameCount,
            Func<uint, ComposedImageFrame, Task> onFrame)
        {
            ArgumentNullException.ThrowIfNull(decoder);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentNullException.ThrowIfNull(onFrame);

            string extension = Path.GetExtension(sourcePath);
            bool isGif = extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            bool isWebp = extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
            if (!isGif && !isWebp)
            {
                throw new NotSupportedException("Only GIF and WebP animations can be composited.");
            }

            uint canvasWidth = Math.Max(1u, decoder.PixelWidth);
            uint canvasHeight = Math.Max(1u, decoder.PixelHeight);
            WebpAnimationInfo webpInfo = default;
            if (isGif)
            {
                (canvasWidth, canvasHeight) = await ReadGifCanvasSizeAsync(decoder, canvasWidth, canvasHeight);
            }
            else
            {
                webpInfo = await ReadWebpAnimationInfoAsync(sourcePath);
                if (webpInfo.CanvasWidth > 0 && webpInfo.CanvasHeight > 0)
                {
                    canvasWidth = webpInfo.CanvasWidth;
                    canvasHeight = webpInfo.CanvasHeight;
                }
            }

            int canvasStride = checked((int)canvasWidth * 4);
            int canvasLength = checked(canvasStride * (int)canvasHeight);
            byte[] canvas = new byte[canvasLength];
            AnimationFrameMetadata previousMetadata = default;
            byte[]? previousRestoreCanvas = null;
            bool hasPreviousFrame = false;

            uint outputFrameCount = Math.Max(1u, frameCount);
            for (uint frameIndex = 0; frameIndex < outputFrameCount; frameIndex++)
            {
                if (hasPreviousFrame)
                {
                    ApplyDisposal(canvas, canvasWidth, canvasHeight, previousMetadata, previousRestoreCanvas);
                }

                BitmapFrame frame = await decoder.GetFrameAsync(frameIndex);
                RawImageFrame rawFrame = await DecodeRawFrameAsync(frame);
                AnimationFrameMetadata metadata = isGif
                    ? await ReadGifFrameMetadataAsync(frame, rawFrame.Width, rawFrame.Height)
                    : GetWebpFrameMetadata(webpInfo.Frames, frameIndex, rawFrame.Width, rawFrame.Height);
                metadata = NormalizeMetadata(metadata, rawFrame.Width, rawFrame.Height, canvasWidth, canvasHeight);

                byte[]? currentRestoreCanvas = metadata.DisposalMethod == 3
                    ? (byte[])canvas.Clone()
                    : null;
                CompositeFrame(canvas, canvasWidth, canvasHeight, rawFrame, metadata);

                await onFrame(
                    frameIndex,
                    new ComposedImageFrame(canvasWidth, canvasHeight, (byte[])canvas.Clone()));

                previousMetadata = metadata;
                previousRestoreCanvas = currentRestoreCanvas;
                hasPreviousFrame = true;
            }
        }

        private static async Task<RawImageFrame> DecodeRawFrameAsync(BitmapFrame frame)
        {
            uint width = Math.Max(1u, frame.PixelWidth);
            uint height = Math.Max(1u, frame.PixelHeight);
            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.NearestNeighbor
            };

            PixelDataProvider pixelData = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            byte[] pixels = pixelData.DetachPixelData();

            long expectedLength = (long)width * height * 4;
            if (pixels.Length < expectedLength)
            {
                uint availableHeight = (uint)(pixels.Length / Math.Max(1L, (long)width * 4));
                height = Math.Max(1u, availableHeight);
            }

            return new RawImageFrame(width, height, pixels);
        }

        private static async Task<AnimationFrameMetadata> ReadGifFrameMetadataAsync(
            BitmapFrame frame,
            uint fallbackWidth,
            uint fallbackHeight)
        {
            int left = await ReadFrameMetadataIntAsync(frame, GifLeftProperty, 0);
            int top = await ReadFrameMetadataIntAsync(frame, GifTopProperty, 0);
            int width = await ReadFrameMetadataIntAsync(frame, GifWidthProperty, checked((int)fallbackWidth));
            int height = await ReadFrameMetadataIntAsync(frame, GifHeightProperty, checked((int)fallbackHeight));
            int disposal = await ReadFrameMetadataIntAsync(frame, GifDisposalProperty, 0);

            return new AnimationFrameMetadata(
                left,
                top,
                width,
                height,
                BlendWithCanvas: true,
                DisposalMethod: (byte)Math.Clamp(disposal, 0, 3));
        }

        private static async Task<int> ReadFrameMetadataIntAsync(
            BitmapFrame frame,
            string propertyName,
            int fallback)
        {
            try
            {
                var properties = await frame.BitmapProperties.GetPropertiesAsync(new[] { propertyName });
                return ConvertToInt32(properties[propertyName].Value, fallback);
            }
            catch (Exception)
            {
                // Some decoders expose only a subset of the WIC metadata tree.
            }

            return fallback;
        }

        private static async Task<(uint Width, uint Height)> ReadGifCanvasSizeAsync(
            BitmapDecoder decoder,
            uint fallbackWidth,
            uint fallbackHeight)
        {
            try
            {
                var properties = await decoder.BitmapContainerProperties.GetPropertiesAsync(
                    new[] { "/logscrdesc/Width", "/logscrdesc/Height" });
                uint width = ConvertToUInt32(properties["/logscrdesc/Width"].Value, fallbackWidth);
                uint height = ConvertToUInt32(properties["/logscrdesc/Height"].Value, fallbackHeight);
                return (Math.Max(1u, width), Math.Max(1u, height));
            }
            catch (Exception)
            {
                return (fallbackWidth, fallbackHeight);
            }
        }

        private static AnimationFrameMetadata GetWebpFrameMetadata(
            IReadOnlyList<AnimationFrameMetadata> frames,
            uint frameIndex,
            uint fallbackWidth,
            uint fallbackHeight)
        {
            if (frames != null && frameIndex < (uint)frames.Count)
            {
                return frames[(int)frameIndex];
            }

            return new AnimationFrameMetadata(
                0,
                0,
                checked((int)fallbackWidth),
                checked((int)fallbackHeight),
                BlendWithCanvas: true,
                DisposalMethod: 0);
        }

        private static AnimationFrameMetadata NormalizeMetadata(
            AnimationFrameMetadata metadata,
            uint rawWidth,
            uint rawHeight,
            uint canvasWidth,
            uint canvasHeight)
        {
            int maxLeft = checked((int)Math.Max(0u, canvasWidth - 1));
            int maxTop = checked((int)Math.Max(0u, canvasHeight - 1));
            int left = Math.Clamp(metadata.Left, 0, maxLeft);
            int top = Math.Clamp(metadata.Top, 0, maxTop);
            int availableWidth = checked((int)canvasWidth - left);
            int availableHeight = checked((int)canvasHeight - top);
            int width = metadata.Width > 0 ? metadata.Width : checked((int)rawWidth);
            int height = metadata.Height > 0 ? metadata.Height : checked((int)rawHeight);

            return metadata with
            {
                Left = left,
                Top = top,
                Width = Math.Clamp(width, 1, Math.Max(1, availableWidth)),
                Height = Math.Clamp(height, 1, Math.Max(1, availableHeight))
            };
        }

        private static void ApplyDisposal(
            byte[] canvas,
            uint canvasWidth,
            uint canvasHeight,
            AnimationFrameMetadata metadata,
            byte[]? restoreCanvas)
        {
            switch (metadata.DisposalMethod)
            {
                case 2:
                    ClearRegion(canvas, canvasWidth, canvasHeight, metadata);
                    break;
                case 3 when restoreCanvas != null:
                    Buffer.BlockCopy(restoreCanvas, 0, canvas, 0, canvas.Length);
                    break;
            }
        }

        private static void ClearRegion(
            byte[] canvas,
            uint canvasWidth,
            uint canvasHeight,
            AnimationFrameMetadata metadata)
        {
            int left = Math.Clamp(metadata.Left, 0, checked((int)canvasWidth));
            int top = Math.Clamp(metadata.Top, 0, checked((int)canvasHeight));
            int width = Math.Clamp(metadata.Width, 0, checked((int)canvasWidth - left));
            int height = Math.Clamp(metadata.Height, 0, checked((int)canvasHeight - top));
            int rowBytes = checked(width * 4);
            int stride = checked((int)canvasWidth * 4);
            for (int row = 0; row < height; row++)
            {
                int offset = checked((top + row) * stride + left * 4);
                Array.Clear(canvas, offset, rowBytes);
            }
        }

        private static void CompositeFrame(
            byte[] canvas,
            uint canvasWidth,
            uint canvasHeight,
            RawImageFrame rawFrame,
            AnimationFrameMetadata metadata)
        {
            int width = Math.Min(metadata.Width, checked((int)rawFrame.Width));
            int height = Math.Min(metadata.Height, checked((int)rawFrame.Height));
            int sourceStride = checked((int)rawFrame.Width * 4);
            int destinationStride = checked((int)canvasWidth * 4);
            int maximumRows = checked((int)canvasHeight - metadata.Top);
            int maximumColumns = checked((int)canvasWidth - metadata.Left);
            width = Math.Clamp(width, 0, maximumColumns);
            height = Math.Clamp(height, 0, maximumRows);

            for (int row = 0; row < height; row++)
            {
                int sourceOffset = checked(row * sourceStride);
                int destinationOffset = checked((metadata.Top + row) * destinationStride + metadata.Left * 4);
                if (!metadata.BlendWithCanvas)
                {
                    Buffer.BlockCopy(rawFrame.Pixels, sourceOffset, canvas, destinationOffset, width * 4);
                    continue;
                }

                for (int column = 0; column < width; column++)
                {
                    int sourcePixel = sourceOffset + column * 4;
                    int destinationPixel = destinationOffset + column * 4;
                    int sourceAlpha = rawFrame.Pixels[sourcePixel + 3];
                    if (sourceAlpha == 255)
                    {
                        Buffer.BlockCopy(rawFrame.Pixels, sourcePixel, canvas, destinationPixel, 4);
                        continue;
                    }

                    if (sourceAlpha == 0)
                    {
                        continue;
                    }

                    int inverseAlpha = 255 - sourceAlpha;
                    int destinationAlpha = canvas[destinationPixel + 3];
                    canvas[destinationPixel] = (byte)Math.Min(
                        255,
                        rawFrame.Pixels[sourcePixel] + (canvas[destinationPixel] * inverseAlpha + 127) / 255);
                    canvas[destinationPixel + 1] = (byte)Math.Min(
                        255,
                        rawFrame.Pixels[sourcePixel + 1] + (canvas[destinationPixel + 1] * inverseAlpha + 127) / 255);
                    canvas[destinationPixel + 2] = (byte)Math.Min(
                        255,
                        rawFrame.Pixels[sourcePixel + 2] + (canvas[destinationPixel + 2] * inverseAlpha + 127) / 255);
                    canvas[destinationPixel + 3] = (byte)Math.Min(
                        255,
                        sourceAlpha + (destinationAlpha * inverseAlpha + 127) / 255);
                }
            }
        }

        private static async Task<WebpAnimationInfo> ReadWebpAnimationInfoAsync(string sourcePath)
        {
            try
            {
                byte[] data = await File.ReadAllBytesAsync(sourcePath);
                if (data.Length < 12 ||
                    !HasChunkId(data, 0, "RIFF") ||
                    !HasChunkId(data, 8, "WEBP"))
                {
                    return default;
                }

                uint canvasWidth = 0;
                uint canvasHeight = 0;
                var frames = new List<AnimationFrameMetadata>();
                int offset = 12;
                while (offset <= data.Length - 8)
                {
                    uint chunkSize = ReadUInt32LittleEndian(data, offset + 4);
                    int payloadOffset = offset + 8;
                    if (chunkSize > (uint)(data.Length - payloadOffset))
                    {
                        break;
                    }

                    int payloadLength = checked((int)chunkSize);
                    if (HasChunkId(data, offset, "VP8X") && payloadLength >= 10)
                    {
                        canvasWidth = 1u + ReadUInt24LittleEndian(data, payloadOffset + 4);
                        canvasHeight = 1u + ReadUInt24LittleEndian(data, payloadOffset + 7);
                    }
                    else if (HasChunkId(data, offset, "ANMF") && payloadLength >= 16)
                    {
                        int left = checked((int)ReadUInt24LittleEndian(data, payloadOffset)) * 2;
                        int top = checked((int)ReadUInt24LittleEndian(data, payloadOffset + 3)) * 2;
                        int width = checked((int)ReadUInt24LittleEndian(data, payloadOffset + 6)) + 1;
                        int height = checked((int)ReadUInt24LittleEndian(data, payloadOffset + 9)) + 1;
                        byte flags = data[payloadOffset + 15];
                        frames.Add(new AnimationFrameMetadata(
                            left,
                            top,
                            width,
                            height,
                            BlendWithCanvas: (flags & 0x01) == 0,
                            DisposalMethod: (flags & 0x02) != 0 ? (byte)2 : (byte)0));
                    }

                    int paddedPayloadLength = checked(payloadLength + (payloadLength & 1));
                    if (paddedPayloadLength > data.Length - payloadOffset - 0)
                    {
                        break;
                    }

                    offset = checked(payloadOffset + paddedPayloadLength);
                }

                return new WebpAnimationInfo(canvasWidth, canvasHeight, frames);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static bool HasChunkId(byte[] data, int offset, string chunkId)
        {
            if (offset < 0 || offset + 4 > data.Length || chunkId.Length != 4)
            {
                return false;
            }

            return data[offset] == chunkId[0] &&
                   data[offset + 1] == chunkId[1] &&
                   data[offset + 2] == chunkId[2] &&
                   data[offset + 3] == chunkId[3];
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
        }

        private static uint ReadUInt24LittleEndian(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16));
        }

        private static int ConvertToInt32(object? value, int fallback)
        {
            try
            {
                return value == null
                    ? fallback
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static uint ConvertToUInt32(object? value, uint fallback)
        {
            try
            {
                return value == null
                    ? fallback
                    : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private readonly record struct RawImageFrame(
            uint Width,
            uint Height,
            byte[] Pixels);

        private readonly record struct AnimationFrameMetadata(
            int Left,
            int Top,
            int Width,
            int Height,
            bool BlendWithCanvas,
            byte DisposalMethod);

        private readonly record struct WebpAnimationInfo(
            uint CanvasWidth,
            uint CanvasHeight,
            IReadOnlyList<AnimationFrameMetadata> Frames);
    }
}
