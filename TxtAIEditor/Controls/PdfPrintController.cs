using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace TxtAIEditor.Controls
{
    internal static class PdfPrintController
    {
        private const uint SelectPages = 0x00000002;
        private const uint NoSelection = 0x00000004;
        private const uint ReturnDeviceContext = 0x00000100;
        private const uint DriverCopiesAndCollation = 0x00040000;
        private const uint HidePrintToFile = 0x00100000;
        private const uint NoCurrentPage = 0x00800000;
        private const uint PrintAction = 1;
        private const int ErrorCancelled = 1223;

        public static async Task ShowAsync(nint owner, string path, string title)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var source = await file.OpenReadAsync();
            var pdf = await PdfDocument.LoadFromStreamAsync(source);
            if (pdf.PageCount == 0)
            {
                throw new InvalidDataException();
            }

            const int maxRanges = 64;
            int rangeSize = Marshal.SizeOf<PageRange>();
            var dialog = new PrintDialog
            {
                Size = (uint)Marshal.SizeOf<PrintDialog>(),
                Owner = owner,
                Flags = ReturnDeviceContext | DriverCopiesAndCollation | NoSelection | NoCurrentPage | HidePrintToFile,
                MaxPageRanges = maxRanges,
                MinPage = 1,
                MaxPage = pdf.PageCount,
                Copies = 1,
                StartPage = uint.MaxValue,
                PageRanges = Marshal.AllocHGlobal(maxRanges * rangeSize)
            };
            try
            {
                var printer = new PrinterSettings();
                if (printer.IsValid)
                {
                    using var firstPage = pdf.GetPage(0);
                    var pageSettings = new PageSettings(printer)
                    {
                        Landscape = firstPage.Size.Width > firstPage.Size.Height
                    };
                    dialog.DevMode = printer.GetHdevmode(pageSettings);
                    dialog.DevNames = printer.GetHdevnames();
                }

                Marshal.ThrowExceptionForHR(PrintDlgExW(ref dialog));
                if (dialog.ResultAction != PrintAction)
                {
                    return;
                }

                var ranges = new List<(uint First, uint Last)>();
                if ((dialog.Flags & SelectPages) != 0)
                {
                    for (int i = 0; i < dialog.PageRangeCount; i++)
                    {
                        var range = Marshal.PtrToStructure<PageRange>(dialog.PageRanges + i * rangeSize);
                        ranges.Add((range.First, range.Last));
                    }
                }
                else
                {
                    ranges.Add((1, pdf.PageCount));
                }

                string? output = null;
                if (string.Equals(GetPrinterName(dialog.DevNames), "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase))
                {
                    var picker = new FileSavePicker
                    {
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                        SuggestedFileName = Path.GetFileNameWithoutExtension(title),
                        DefaultFileExtension = ".pdf"
                    };
                    picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
                    var destination = await picker.PickSaveFileAsync();
                    if (destination == null)
                    {
                        return;
                    }
                    output = destination.Path;
                }

                nint deviceContext = dialog.DeviceContext;
                await Task.Run(() => PrintPages(deviceContext, pdf, ranges, title, output));
            }
            finally
            {
                if (dialog.DeviceContext != 0) DeleteDC(dialog.DeviceContext);
                if (dialog.DevMode != 0) GlobalFree(dialog.DevMode);
                if (dialog.DevNames != 0) GlobalFree(dialog.DevNames);
                Marshal.FreeHGlobal(dialog.PageRanges);
            }
        }

        private static string? GetPrinterName(nint devNames)
        {
            if (devNames == 0) return null;
            nint data = GlobalLock(devNames);
            if (data == 0) throw new Win32Exception();
            try
            {
                // DEVNAMES offsets count UTF-16 characters from the start of the structure.
                ushort deviceOffset = (ushort)Marshal.ReadInt16(data, sizeof(ushort));
                return Marshal.PtrToStringUni(data + deviceOffset * sizeof(char));
            }
            finally
            {
                GlobalUnlock(devNames);
            }
        }

        internal static void PrintPages(nint deviceContext, PdfDocument pdf,
            IReadOnlyList<(uint First, uint Last)> ranges, string title, string? output)
        {
            int width = GetDeviceCaps(deviceContext, 8);  // HORZRES
            int height = GetDeviceCaps(deviceContext, 10); // VERTRES
            int dpiX = GetDeviceCaps(deviceContext, 88);  // LOGPIXELSX
            int dpiY = GetDeviceCaps(deviceContext, 90);  // LOGPIXELSY
            if (width <= 0 || height <= 0 || dpiX <= 0 || dpiY <= 0)
            {
                throw new Win32Exception(6); // ERROR_INVALID_HANDLE
            }

            var info = new DocumentInfo { Size = Marshal.SizeOf<DocumentInfo>(), Title = title, Output = output };
            CheckPrintResult(StartDocW(deviceContext, ref info));
            try
            {
                foreach (var (first, last) in ranges)
                {
                    for (uint pageIndex = first - 1; pageIndex < last; pageIndex++)
                    {
                        using var page = pdf.GetPage(pageIndex);
                        // One PDF page per sheet. Scale uniformly to the printable area.
                        double nativeWidth = page.Size.Width * dpiX / 96;
                        double nativeHeight = page.Size.Height * dpiY / 96;
                        double scale = Math.Min(1, Math.Min(width / nativeWidth, height / nativeHeight));
                        double pageWidth = nativeWidth * scale;
                        double pageHeight = nativeHeight * scale;
                        var bounds = new RectangleF((float)((width - pageWidth) / 2), (float)((height - pageHeight) / 2),
                            (float)pageWidth, (float)pageHeight);

                        const int maxRasterDpi = 300;
                        using var imageStream = new InMemoryRandomAccessStream();
                        page.RenderToStreamAsync(imageStream, new PdfPageRenderOptions
                        {
                            DestinationWidth = (uint)Math.Max(1, Math.Round(pageWidth * Math.Min(1, (double)maxRasterDpi / dpiX))),
                            DestinationHeight = (uint)Math.Max(1, Math.Round(pageHeight * Math.Min(1, (double)maxRasterDpi / dpiY))),
                            BitmapEncoderId = BitmapEncoder.PngEncoderId,
                            IsIgnoringHighContrast = true
                        }).AsTask().GetAwaiter().GetResult();
                        imageStream.Seek(0);
                        using var stream = imageStream.AsStreamForRead();
                        using var image = Image.FromStream(stream);

                        CheckPrintResult(StartPage(deviceContext));
                        using (var graphics = Graphics.FromHdc(deviceContext))
                        {
                            graphics.PageUnit = GraphicsUnit.Pixel;
                            graphics.DrawImage(image, bounds);
                        }
                        CheckPrintResult(EndPage(deviceContext));
                    }
                }
                CheckPrintResult(EndDoc(deviceContext));
            }
            catch
            {
                AbortDoc(deviceContext);
                throw;
            }
        }

        private static void CheckPrintResult(int result)
        {
            if (result > 0) return;
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorCancelled || result == -3) throw new OperationCanceledException();
            throw new Win32Exception(error == 0 ? 31 : error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PageRange
        {
            public uint First;
            public uint Last;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PrintDialog
        {
            public uint Size;
            public nint Owner;
            public nint DevMode;
            public nint DevNames;
            public nint DeviceContext;
            public uint Flags;
            public uint Flags2;
            public uint ExclusionFlags;
            public uint PageRangeCount;
            public uint MaxPageRanges;
            public nint PageRanges;
            public uint MinPage;
            public uint MaxPage;
            public uint Copies;
            public nint Instance;
            public nint TemplateName;
            public nint Callback;
            public uint PropertyPageCount;
            public nint PropertyPages;
            public uint StartPage;
            public uint ResultAction;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DocumentInfo
        {
            public int Size;
            public string Title;
            public string? Output;
            public string? DataType;
            public uint Flags;
        }

        [DllImport("comdlg32.dll", ExactSpelling = true)]
        private static extern int PrintDlgExW(ref PrintDialog dialog);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern nint GlobalLock(nint memory);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool GlobalUnlock(nint memory);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint GlobalFree(nint memory);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int GetDeviceCaps(nint deviceContext, int index);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int StartDocW(nint deviceContext, ref DocumentInfo info);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int StartPage(nint deviceContext);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int EndPage(nint deviceContext);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int EndDoc(nint deviceContext);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int AbortDoc(nint deviceContext);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern bool DeleteDC(nint deviceContext);
    }
}
