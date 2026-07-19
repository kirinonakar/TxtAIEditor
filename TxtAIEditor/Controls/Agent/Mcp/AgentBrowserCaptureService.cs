using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentBrowserCaptureService
    {
        private const int MaxCaptureDimension = 1024;
        private const uint Srccopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint DibRgbColors = 0;
        private const int Halftone = 4;
        private const int TargetMarkerArmLength = 24;
        private const int TargetMarkerThickness = 5;

        private readonly Action<LlmMessageAttachment>? _addImageAttachment;

        public AgentBrowserCaptureService(Action<LlmMessageAttachment>? addImageAttachment)
        {
            _addImageAttachment = addImageAttachment;
        }

        public async Task<AgentBrowserCapture> CaptureAsync(IntPtr window, CancellationToken cancellationToken)
        {
            if (!GetWindowRect(window, out Rect rect))
            {
                throw new InvalidOperationException("Cannot read the controlled window bounds.");
            }

            int windowWidth = rect.Right - rect.Left;
            int windowHeight = rect.Bottom - rect.Top;
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                throw new InvalidOperationException("The controlled window has invalid bounds.");
            }

            double scale = Math.Min(1.0, MaxCaptureDimension / (double)Math.Max(windowWidth, windowHeight));
            int imageWidth = Math.Max(1, (int)Math.Round(windowWidth * scale));
            int imageHeight = Math.Max(1, (int)Math.Round(windowHeight * scale));
            byte[] pixels = CaptureWindowPixels(rect, windowWidth, windowHeight, imageWidth, imageHeight);

            byte[] paddedPixels = new byte[MaxCaptureDimension * MaxCaptureDimension * 4];
            for (int i = 3; i < paddedPixels.Length; i += 4)
            {
                paddedPixels[i] = 255;
            }

            int offsetX = (MaxCaptureDimension - imageWidth) / 2;
            int offsetY = (MaxCaptureDimension - imageHeight) / 2;
            for (int y = 0; y < imageHeight; y++)
            {
                int sourceOffset = y * imageWidth * 4;
                int destinationOffset = ((y + offsetY) * MaxCaptureDimension + offsetX) * 4;
                Array.Copy(pixels, sourceOffset, paddedPixels, destinationOffset, imageWidth * 4);
            }

            string captureDirectory = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "BrowserUse");
            Directory.CreateDirectory(captureDirectory);
            string imagePath = Path.Combine(
                captureDirectory,
                $"browser-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await SavePngAsync(
                imagePath,
                MaxCaptureDimension,
                MaxCaptureDimension,
                paddedPixels,
                cancellationToken);
            CleanupOldCaptures(captureDirectory);
            await AttachImageAsync(imagePath, cancellationToken);

            return new AgentBrowserCapture
            {
                Window = window,
                ImagePath = imagePath,
                PixelData = paddedPixels,
                ImageWidth = MaxCaptureDimension,
                ImageHeight = MaxCaptureDimension,
                OriginalImageWidth = imageWidth,
                OriginalImageHeight = imageHeight,
                PaddingLeft = offsetX,
                PaddingTop = offsetY,
                WindowWidth = windowWidth,
                WindowHeight = windowHeight
            };
        }

        public async Task<string> MarkTargetAsync(
            AgentBrowserCapture capture,
            int x,
            int y,
            CancellationToken cancellationToken)
        {
            if (capture.PixelData.Length != capture.ImageWidth * capture.ImageHeight * 4)
            {
                throw new InvalidOperationException("The latest browser capture pixel data is unavailable. Capture the window again.");
            }

            if (x < 0 || x >= capture.ImageWidth || y < 0 || y >= capture.ImageHeight)
            {
                throw new InvalidOperationException(
                    $"Target coordinates ({x}, {y}) are outside the {capture.ImageWidth}x{capture.ImageHeight} capture.");
            }

            int contentRight = capture.PaddingLeft + capture.OriginalImageWidth;
            int contentBottom = capture.PaddingTop + capture.OriginalImageHeight;
            if (x < capture.PaddingLeft || x >= contentRight || y < capture.PaddingTop || y >= contentBottom)
            {
                throw new InvalidOperationException(
                    $"Target coordinates ({x}, {y}) are in the capture padding. Choose a point inside the controlled window content: " +
                    $"x={capture.PaddingLeft}..{contentRight - 1}, y={capture.PaddingTop}..{contentBottom - 1}.");
            }

            byte[] markedPixels = (byte[])capture.PixelData.Clone();
            DrawTargetMarker(markedPixels, capture.ImageWidth, capture.ImageHeight, x, y);

            string captureDirectory = Path.GetDirectoryName(capture.ImagePath)
                ?? Path.Combine(Path.GetTempPath(), "TxtAIEditor", "BrowserUse");
            Directory.CreateDirectory(captureDirectory);
            string imagePath = Path.Combine(
                captureDirectory,
                $"browser-target-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await SavePngAsync(
                imagePath,
                capture.ImageWidth,
                capture.ImageHeight,
                markedPixels,
                cancellationToken);
            CleanupOldCaptures(captureDirectory);
            bool attached = await AttachImageAsync(imagePath, cancellationToken);
            if (!attached)
            {
                throw new InvalidOperationException("The target marker image was created but could not be attached to the model context.");
            }

            return imagePath;
        }

        private static void DrawTargetMarker(byte[] pixels, int width, int height, int centerX, int centerY)
        {
            int halfThickness = TargetMarkerThickness / 2;
            FillRectangle(
                pixels,
                width,
                height,
                centerX - TargetMarkerArmLength,
                centerY - halfThickness,
                centerX + TargetMarkerArmLength,
                centerY + halfThickness);
            FillRectangle(
                pixels,
                width,
                height,
                centerX - halfThickness,
                centerY - TargetMarkerArmLength,
                centerX + halfThickness,
                centerY + TargetMarkerArmLength);
        }

        private static void FillRectangle(
            byte[] pixels,
            int width,
            int height,
            int left,
            int top,
            int right,
            int bottom)
        {
            int clippedLeft = Math.Clamp(left, 0, width - 1);
            int clippedTop = Math.Clamp(top, 0, height - 1);
            int clippedRight = Math.Clamp(right, 0, width - 1);
            int clippedBottom = Math.Clamp(bottom, 0, height - 1);
            for (int y = clippedTop; y <= clippedBottom; y++)
            {
                for (int x = clippedLeft; x <= clippedRight; x++)
                {
                    int offset = ((y * width) + x) * 4;
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 255;
                    pixels[offset + 3] = 255;
                }
            }
        }

        private static byte[] CaptureWindowPixels(
            Rect windowBounds,
            int windowWidth,
            int windowHeight,
            int imageWidth,
            int imageHeight)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cannot acquire the Windows screen drawing surface.");
            }

            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                bitmap = CreateCompatibleBitmap(screenDc, imageWidth, imageHeight);
                if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Cannot allocate the browser capture surface.");
                }

                oldBitmap = SelectObject(memoryDc, bitmap);
                SetStretchBltMode(memoryDc, Halftone);
                if (!StretchBlt(
                    memoryDc,
                    0,
                    0,
                    imageWidth,
                    imageHeight,
                    screenDc,
                    windowBounds.Left,
                    windowBounds.Top,
                    windowWidth,
                    windowHeight,
                    Srccopy | CaptureBlt))
                {
                    throw new InvalidOperationException("Windows failed to capture the browser window.");
                }

                var bitmapInfo = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = imageWidth,
                        Height = -imageHeight,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                        SizeImage = (uint)(imageWidth * imageHeight * 4)
                    }
                };
                byte[] pixels = new byte[imageWidth * imageHeight * 4];
                int scanLines = GetDIBits(
                    memoryDc,
                    bitmap,
                    0,
                    (uint)imageHeight,
                    pixels,
                    ref bitmapInfo,
                    DibRgbColors);
                if (scanLines != imageHeight)
                {
                    throw new InvalidOperationException("Windows returned an incomplete browser capture.");
                }

                return pixels;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
                {
                    SelectObject(memoryDc, oldBitmap);
                }

                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }

                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }

                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static async Task SavePngAsync(
            string imagePath,
            int imageWidth,
            int imageHeight,
            byte[] pixels,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)imageWidth,
                (uint)imageHeight,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
            output.Seek(0);
            await using var source = output.AsStreamForRead();
            await using var destination = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await source.CopyToAsync(destination, cancellationToken);
        }

        private static void CleanupOldCaptures(string captureDirectory)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(captureDirectory, "browser-*.png")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .Skip(20))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private async Task<bool> AttachImageAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (_addImageAttachment == null || !File.Exists(imagePath))
            {
                return false;
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                _addImageAttachment(new LlmMessageAttachment
                {
                    DisplayName = Path.GetFileName(imagePath),
                    MimeType = "image/png",
                    Base64Data = Convert.ToBase64String(bytes),
                    Width = MaxCaptureDimension,
                    Height = MaxCaptureDimension,
                    EstimatedTokens = EstimateImageTokens(MaxCaptureDimension, MaxCaptureDimension)
                });
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Image attachment failure should not fail the Browser Use capture itself.
                return false;
            }
        }

        private static int EstimateImageTokens(int width, int height)
        {
            int tilesWide = Math.Max(1, (int)Math.Ceiling(width / 512.0));
            int tilesHigh = Math.Max(1, (int)Math.Ceiling(height / 512.0));
            return 85 + (tilesWide * tilesHigh * 170);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPixelsPerMeter;
            public int YPixelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(
            IntPtr destinationDeviceContext,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            IntPtr sourceDeviceContext,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            uint rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr deviceContext,
            IntPtr bitmap,
            uint startScan,
            uint scanLineCount,
            [Out] byte[] bits,
            ref BitmapInfo bitmapInfo,
            uint usage);
    }

    internal sealed class AgentBrowserCapture
    {
        public IntPtr Window { get; set; }
        public string ImagePath { get; set; } = string.Empty;
        public byte[] PixelData { get; set; } = Array.Empty<byte>();
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int OriginalImageWidth { get; set; }
        public int OriginalImageHeight { get; set; }
        public int PaddingLeft { get; set; }
        public int PaddingTop { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
    }
}
