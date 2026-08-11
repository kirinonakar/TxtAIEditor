using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Core.Services
{
    public enum ImageConversionOutputFormat
    {
        Png,
        Jpeg
    }

    public sealed class ImageConversionOptions
    {
        public ImageConversionOutputFormat OutputFormat { get; init; }
        public int Quality { get; init; } = 90;
        public bool ResizeEnabled { get; init; }
        public uint? TargetWidth { get; init; }
        public uint? TargetHeight { get; init; }
        public bool KeepAspectRatio { get; init; } = true;
        public BitmapInterpolationMode InterpolationMode { get; init; } = BitmapInterpolationMode.Fant;
        public bool ExtractFrames { get; init; }
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
            return new ImageConversionSourceInfo(width, height, frameCount);
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

            string extension = outputFormat == ImageConversionOutputFormat.Png ? ".png" : ".jpg";
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
                    (uint outputWidth, uint outputHeight) = CalculateOutputSize(
                        composedFrame.Width,
                        composedFrame.Height,
                        options);
                    byte[] pixels = composedFrame.Pixels;
                    if (outputWidth != composedFrame.Width || outputHeight != composedFrame.Height)
                    {
                        pixels = await ResizePixelsAsync(
                            pixels,
                            composedFrame.Width,
                            composedFrame.Height,
                            outputWidth,
                            outputHeight,
                            options.InterpolationMode);
                    }

                    await EncodeFrameAsync(
                        outputPaths[(int)frameIndex],
                        outputWidth,
                        outputHeight,
                        pixels,
                        options);
                });
        }

        private static async Task<byte[]> ResizePixelsAsync(
            byte[] pixels,
            uint sourceWidth,
            uint sourceHeight,
            uint outputWidth,
            uint outputHeight,
            BitmapInterpolationMode interpolationMode)
        {
            using var intermediate = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                intermediate);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
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
                BitmapAlphaMode.Premultiplied,
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
            ImageConversionOptions options)
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var outputFile = new FileStream(
                    temporaryPath,
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
                        BitmapAlphaMode.Premultiplied,
                        width,
                        height,
                        96,
                        96,
                        pixels);

                    await encoder.FlushAsync();
                }

                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
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
                options.OutputFormat != ImageConversionOutputFormat.Jpeg)
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
