using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Imazen.WebP;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Core.Services
{
    public enum ImageConversionOutputFormat
    {
        Png,
        Jpeg,
        Webp
    }

    public sealed class ImageConversionOptions
    {
        public ImageConversionOutputFormat OutputFormat { get; init; }
        public int Quality { get; init; } = 90;
        public bool WebpLossless { get; init; }
        public bool ResizeEnabled { get; init; }
        public uint? TargetWidth { get; init; }
        public uint? TargetHeight { get; init; }
        public bool KeepAspectRatio { get; init; } = true;
        public BitmapInterpolationMode InterpolationMode { get; init; } = BitmapInterpolationMode.Fant;
        public bool ExtractFrames { get; init; }
        public ImageCropRegion? CropRegion { get; init; }
    }

    public readonly record struct ImageConversionSourceInfo(
        uint Width,
        uint Height,
        uint FrameCount);

    public sealed class ImageConversionService
    {
        private const uint MaxDimension = 100_000;

        public async Task<ImageConversionSourceInfo> ReadSourceInfoAsync(string sourcePath)
        {
            ValidateSourcePath(sourcePath);

            using var sourceFile = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using IRandomAccessStream input = sourceFile.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            BitmapFrame frame = await decoder.GetFrameAsync(0);

            uint width = frame.OrientedPixelWidth > 0 ? frame.OrientedPixelWidth : decoder.PixelWidth;
            uint height = frame.OrientedPixelHeight > 0 ? frame.OrientedPixelHeight : decoder.PixelHeight;
            uint frameCount = Math.Max(1u, decoder.FrameCount);
            if (frameCount > 1 && AnimatedImageFrameComposer.IsSupportedAnimationPath(sourcePath))
            {
                await AnimatedImageFrameComposer.ComposeAsync(decoder, sourcePath, 1, (_, composedFrame) =>
                {
                    width = composedFrame.Width;
                    height = composedFrame.Height;
                    return Task.CompletedTask;
                });
            }
            return new ImageConversionSourceInfo(width, height, frameCount);
        }

        public async Task<SoftwareBitmap> ReadPreviewAsync(string sourcePath)
        {
            ValidateSourcePath(sourcePath);
            using var sourceFile = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using IRandomAccessStream input = sourceFile.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            if (decoder.FrameCount > 1 && AnimatedImageFrameComposer.IsSupportedAnimationPath(sourcePath))
            {
                SoftwareBitmap? preview = null;
                await AnimatedImageFrameComposer.ComposeAsync(decoder, sourcePath, 1, async (_, frame) =>
                {
                    double scale = Math.Min(1, 2048d / Math.Max(frame.Width, frame.Height));
                    uint width = Math.Max(1u, (uint)Math.Round(frame.Width * scale));
                    uint height = Math.Max(1u, (uint)Math.Round(frame.Height * scale));
                    byte[] pixels = scale < 1
                        ? await ResizePixelsAsync(frame.Pixels, frame.Width, frame.Height, width, height, BitmapInterpolationMode.Fant)
                        : frame.Pixels;
                    preview = SoftwareBitmap.CreateCopyFromBuffer(
                        pixels.AsBuffer(), BitmapPixelFormat.Bgra8, (int)width, (int)height, BitmapAlphaMode.Premultiplied);
                });
                return preview ?? throw new InvalidDataException("Unable to read the image preview.");
            }

            // Scaling precedes EXIF rotation, so use the unrotated dimensions here.
            double previewScale = Math.Min(1, 2048d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * previewScale)),
                    ScaledHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * previewScale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
        }

        public static IReadOnlyList<string> BuildOutputPaths(
            string sourcePath,
            ImageConversionOutputFormat outputFormat,
            bool extractFrames,
            uint frameCount)
        {
            ValidateSourcePath(sourcePath);

            string? directory = Path.GetDirectoryName(sourcePath);
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "image";
            }

            string extension = outputFormat switch
            {
                ImageConversionOutputFormat.Png => ".png",
                ImageConversionOutputFormat.Jpeg => ".jpg",
                ImageConversionOutputFormat.Webp => ".webp",
                _ => throw new ArgumentOutOfRangeException(nameof(outputFormat))
            };
            uint outputCount = extractFrames ? Math.Max(1u, frameCount) : 1u;
            var outputPaths = new List<string>(checked((int)outputCount));

            for (uint index = 0; index < outputCount; index++)
            {
                string fileName = extractFrames
                    ? $"{baseName}_convert_frame_{index + 1:D3}{extension}"
                    : $"{baseName}_convert{extension}";
                outputPaths.Add(Path.Combine(directory ?? string.Empty, fileName));
            }

            return outputPaths;
        }

        public async Task<IReadOnlyList<string>> ConvertAsync(
            string sourcePath,
            ImageConversionOptions options)
        {
            ValidateSourcePath(sourcePath);
            ArgumentNullException.ThrowIfNull(options);

            ValidateOptions(options);

            using var sourceFile = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using IRandomAccessStream input = sourceFile.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

            uint frameCount = Math.Max(1u, decoder.FrameCount);
            uint outputFrameCount = options.ExtractFrames ? frameCount : 1u;
            IReadOnlyList<string> outputPaths = BuildOutputPaths(
                sourcePath,
                options.OutputFormat,
                options.ExtractFrames,
                frameCount);

            if (frameCount > 1 && AnimatedImageFrameComposer.IsSupportedAnimationPath(sourcePath))
            {
                await ConvertCompositedFramesAsync(
                    decoder,
                    sourcePath,
                    outputFrameCount,
                    outputPaths,
                    options);
                return outputPaths;
            }

            for (uint frameIndex = 0; frameIndex < outputFrameCount; frameIndex++)
            {
                BitmapFrame frame = await decoder.GetFrameAsync(frameIndex);
                uint sourceWidth = frame.OrientedPixelWidth > 0 ? frame.OrientedPixelWidth : frame.PixelWidth;
                uint sourceHeight = frame.OrientedPixelHeight > 0 ? frame.OrientedPixelHeight : frame.PixelHeight;
                // Crop coordinates and WebP input pixels use the displayed EXIF orientation before resizing.
                if (options.CropRegion.HasValue || options.OutputFormat != ImageConversionOutputFormat.Jpeg)
                {
                    // Preserve straight color values for lossless output, including semitransparent pixels.
                    BitmapAlphaMode alphaMode = options.OutputFormat == ImageConversionOutputFormat.Png || options.WebpLossless
                        ? BitmapAlphaMode.Straight
                        : BitmapAlphaMode.Premultiplied;
                    PixelDataProvider originalPixels = await frame.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        alphaMode,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb);
                    await ConvertPixelsAsync(outputPaths[(int)frameIndex], sourceWidth, sourceHeight,
                        originalPixels.DetachPixelData(), options, alphaMode);
                    continue;
                }

                (uint outputWidth, uint outputHeight) = CalculateOutputSize(sourceWidth, sourceHeight, options);

                var transform = new BitmapTransform
                {
                    ScaledWidth = outputWidth,
                    ScaledHeight = outputHeight,
                    InterpolationMode = options.InterpolationMode
                };

                PixelDataProvider pixelData = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                await EncodeFrameAsync(
                    outputPaths[(int)frameIndex],
                    outputWidth,
                    outputHeight,
                    pixelData.DetachPixelData(),
                    options);
            }

            return outputPaths;
        }

        private static async Task ConvertCompositedFramesAsync(
            BitmapDecoder decoder,
            string sourcePath,
            uint outputFrameCount,
            IReadOnlyList<string> outputPaths,
            ImageConversionOptions options)
        {
            await AnimatedImageFrameComposer.ComposeAsync(
                decoder,
                sourcePath,
                outputFrameCount,
                async (frameIndex, composedFrame) =>
                {
                    await ConvertPixelsAsync(outputPaths[(int)frameIndex], composedFrame.Width,
                        composedFrame.Height, composedFrame.Pixels, options);
                });
        }

        private static async Task ConvertPixelsAsync(
            string outputPath, uint width, uint height, byte[] pixels, ImageConversionOptions options,
            BitmapAlphaMode alphaMode = BitmapAlphaMode.Premultiplied)
        {
            if (options.CropRegion is ImageCropRegion region)
            {
                ImageCropBounds bounds = region.GetPixelBounds(width, height);
                if (bounds.X != 0 || bounds.Y != 0 || bounds.Width != width || bounds.Height != height)
                {
                    int stride = checked((int)width * 4);
                    int cropStride = checked((int)bounds.Width * 4);
                    byte[] cropped = new byte[checked(cropStride * (int)bounds.Height)];
                    for (int row = 0; row < bounds.Height; row++)
                    {
                        System.Buffer.BlockCopy(pixels, checked(((int)bounds.Y + row) * stride + (int)bounds.X * 4),
                            cropped, row * cropStride, cropStride);
                    }
                    pixels = cropped;
                    width = bounds.Width;
                    height = bounds.Height;
                }
            }

            (uint outputWidth, uint outputHeight) = CalculateOutputSize(width, height, options);
            if (outputWidth != width || outputHeight != height)
            {
                pixels = await ResizePixelsAsync(pixels, width, height, outputWidth, outputHeight, options.InterpolationMode, alphaMode);
            }
            await EncodeFrameAsync(outputPath, outputWidth, outputHeight, pixels, options, alphaMode);
        }

        private static async Task<byte[]> ResizePixelsAsync(
            byte[] pixels,
            uint sourceWidth,
            uint sourceHeight,
            uint outputWidth,
            uint outputHeight,
            BitmapInterpolationMode interpolationMode,
            BitmapAlphaMode alphaMode = BitmapAlphaMode.Premultiplied)
        {
            using var intermediate = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                intermediate);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                alphaMode,
                sourceWidth,
                sourceHeight,
                96,
                96,
                pixels);
            await encoder.FlushAsync();

            intermediate.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(intermediate);
            var transform = new BitmapTransform
            {
                ScaledWidth = outputWidth,
                ScaledHeight = outputHeight,
                InterpolationMode = interpolationMode
            };
            PixelDataProvider resizedPixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                alphaMode,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            return resizedPixels.DetachPixelData();
        }

        private static async Task EncodeFrameAsync(
            string outputPath,
            uint width,
            uint height,
            byte[] pixels,
            ImageConversionOptions options,
            BitmapAlphaMode alphaMode = BitmapAlphaMode.Premultiplied)
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string pngIntermediatePath = temporaryPath + ".png";
            try
            {
                if (options.OutputFormat == ImageConversionOutputFormat.Webp)
                {
                    if (width > 16383 || height > 16383)
                    {
                        throw new ArgumentOutOfRangeException(nameof(width), "WebP dimensions cannot exceed 16383 pixels.");
                    }
                    await Task.Run(() =>
                    {
                        // libwebp expects straight BGRA, while WIC and the frame composer supply premultiplied BGRA.
                        for (int index = 0; alphaMode == BitmapAlphaMode.Premultiplied && index < pixels.Length; index += 4)
                        {
                            int alpha = pixels[index + 3];
                            if (alpha == 255)
                            {
                                continue;
                            }
                            for (int channel = 0; channel < 3; channel++)
                            {
                                pixels[index + channel] = alpha == 0
                                    ? (byte)0
                                    : (byte)Math.Min(255, (pixels[index + channel] * 255 + alpha / 2) / alpha);
                            }
                        }
                        using var outputFile = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        if (options.WebpLossless)
                        {
                            var config = new WebPEncoderConfig()
                                .SetLosslessPreset((options.Quality - 1) * 9 / 99)
                                .SetQuality(options.Quality)
                                // SetQuality resets Imazen's lossless flag; restore it after setting effort.
                                .SetLossless()
                                .SetExact(true);
                            WebPEncoder.Encode(pixels, (int)width, (int)height, checked((int)width * 4),
                                WebPPixelFormat.Bgra, config, outputFile);
                        }
                        else
                        {
                            WebPEncoder.Encode(pixels, (int)width, (int)height, checked((int)width * 4),
                                WebPPixelFormat.Bgra, options.Quality, outputFile);
                        }
                    });
                    File.Move(temporaryPath, outputPath, true);
                    return;
                }

                using (var outputFile = new FileStream(
                    options.OutputFormat == ImageConversionOutputFormat.Png ? pngIntermediatePath : temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                using (IRandomAccessStream output = outputFile.AsRandomAccessStream())
                {
                    Guid encoderId = options.OutputFormat == ImageConversionOutputFormat.Png
                        ? BitmapEncoder.PngEncoderId
                        : BitmapEncoder.JpegEncoderId;
                    BitmapEncoder encoder;
                    if (options.OutputFormat == ImageConversionOutputFormat.Jpeg)
                    {
                        var encodingOptions = new BitmapPropertySet();
                        encodingOptions["ImageQuality"] = new BitmapTypedValue(
                            Math.Clamp(options.Quality, 1, 100) / 100f,
                            PropertyType.Single);
                        encoder = await BitmapEncoder.CreateAsync(encoderId, output, encodingOptions);
                    }
                    else
                    {
                        encoder = await BitmapEncoder.CreateAsync(encoderId, output);
                    }

                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        alphaMode,
                        width,
                        height,
                        96,
                        96,
                        pixels);

                    await encoder.FlushAsync();
                }

                if (options.OutputFormat == ImageConversionOutputFormat.Png)
                {
                    await Task.Run(() =>
                    {
                        using var inputFile = File.OpenRead(pngIntermediatePath);
                        using var outputFile = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        PngCompression.Recompress(inputFile, outputFile, options.Quality);
                    });
                }
                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                if (File.Exists(pngIntermediatePath))
                {
                    File.Delete(pngIntermediatePath);
                }
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static (uint Width, uint Height) CalculateOutputSize(
            uint sourceWidth,
            uint sourceHeight,
            ImageConversionOptions options)
        {
            if (sourceWidth == 0 || sourceHeight == 0)
            {
                throw new InvalidDataException("The image has invalid dimensions.");
            }

            if (!options.ResizeEnabled)
            {
                return (sourceWidth, sourceHeight);
            }

            uint? requestedWidth = NormalizeDimension(options.TargetWidth);
            uint? requestedHeight = NormalizeDimension(options.TargetHeight);
            if (requestedWidth == null && requestedHeight == null)
            {
                return (sourceWidth, sourceHeight);
            }

            if (!options.KeepAspectRatio)
            {
                return (
                    requestedWidth ?? sourceWidth,
                    requestedHeight ?? sourceHeight);
            }

            if (requestedWidth.HasValue && requestedHeight.HasValue)
            {
                double scale = Math.Min(
                    requestedWidth.Value / (double)sourceWidth,
                    requestedHeight.Value / (double)sourceHeight);
                return (
                    ToDimension(sourceWidth * scale),
                    ToDimension(sourceHeight * scale));
            }

            if (requestedWidth.HasValue)
            {
                double scale = requestedWidth.Value / (double)sourceWidth;
                return (requestedWidth.Value, ToDimension(sourceHeight * scale));
            }

            double heightScale = requestedHeight!.Value / (double)sourceHeight;
            return (ToDimension(sourceWidth * heightScale), requestedHeight.Value);
        }

        private static uint? NormalizeDimension(uint? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            if (value.Value == 0 || value.Value > MaxDimension)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Image dimensions must be between 1 and {MaxDimension} pixels.");
            }

            return value;
        }

        private static uint ToDimension(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return Math.Clamp((uint)Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero)), 1u, MaxDimension);
        }

        private static void ValidateOptions(ImageConversionOptions options)
        {
            if (options.OutputFormat != ImageConversionOutputFormat.Png &&
                options.OutputFormat != ImageConversionOutputFormat.Jpeg &&
                options.OutputFormat != ImageConversionOutputFormat.Webp)
            {
                throw new ArgumentOutOfRangeException(nameof(options.OutputFormat));
            }

            if (options.InterpolationMode != BitmapInterpolationMode.NearestNeighbor &&
                options.InterpolationMode != BitmapInterpolationMode.Linear &&
                options.InterpolationMode != BitmapInterpolationMode.Cubic &&
                options.InterpolationMode != BitmapInterpolationMode.Fant)
            {
                throw new ArgumentOutOfRangeException(nameof(options.InterpolationMode));
            }

            NormalizeDimension(options.TargetWidth);
            NormalizeDimension(options.TargetHeight);
            options.CropRegion?.GetPixelBounds(1, 1);
            if (options.Quality < 1 || options.Quality > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(options.Quality));
            }
        }

        private static void ValidateSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("An image source path is required.", nameof(sourcePath));
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The image source file was not found.", sourcePath);
            }
        }
    }
}
