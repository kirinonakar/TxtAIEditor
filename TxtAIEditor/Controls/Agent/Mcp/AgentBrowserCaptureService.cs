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

        private async Task AttachImageAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (_addImageAttachment == null || !File.Exists(imagePath))
            {
                return;
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
            }
            catch
            {
                // Image attachment failure should not fail the Browser Use capture itself.
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
