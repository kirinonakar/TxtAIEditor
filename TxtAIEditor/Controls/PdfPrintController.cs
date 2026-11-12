using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using TxtAIEditor.Core.Services;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TxtAIEditor.Controls
{
    internal sealed class PdfPrintController
    {
        private const int PreviewDpi = 96;
        private const int MaxPrintDpi = 300;
        private const int MaxPreviewPages = 8;
        private readonly PdfDocument _pdf;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _documentSource;
        private readonly PrintOrientation _orientation;
        private readonly Canvas _previewCanvas = new() { Opacity = 0, IsHitTestVisible = false };
        private readonly Dictionary<int, Canvas> _previewPages = new();
        private readonly Queue<int> _previewOrder = new();
        private readonly SemaphoreSlim _renderGate = new(1);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _title;
        private readonly string _failedMessage;
        private PrintPageDescription _previewDescription;
        private int _paginationVersion;
        private PrintTask? _printTask;

        private PdfPrintController(PdfDocument pdf, string title, string failedMessage)
        {
            _pdf = pdf;
            _title = title;
            _failedMessage = failedMessage;
            _documentSource = _document.DocumentSource;
            using var firstPage = pdf.GetPage(0);
            _orientation = firstPage.Size.Width > firstPage.Size.Height
                ? PrintOrientation.Landscape : PrintOrientation.Portrait;
        }

        public static async Task ShowAsync(Window owner, string path, string title, string failedMessage)
        {
            if (!PrintManager.IsSupported() || owner.Content is not Panel host)
            {
                throw new InvalidOperationException(failedMessage);
            }

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var source = await file.OpenReadAsync();
            var pdf = await PdfDocument.LoadFromStreamAsync(source);
            if (pdf.PageCount == 0)
            {
                throw new InvalidDataException(failedMessage);
            }

            var job = new PdfPrintController(pdf, title, failedMessage);
            await job.ShowDialogAsync(owner, host);
        }

        private async Task ShowDialogAsync(Window owner, Panel host)
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            var manager = PrintManagerInterop.GetForWindow(handle);
            using var renderGate = _renderGate;
            using var stop = _stop;

            try
            {
                // Keep preview pages in the window's visual tree for XAML printing.
                host.Children.Add(_previewCanvas);
                _document.Paginate += OnPaginate;
                _document.GetPreviewPage += OnGetPreviewPage;
                _document.AddPages += OnAddPages;
                manager.PrintTaskRequested += OnPrintTaskRequested;
                owner.Closed += OnOwnerClosed;

                if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(handle))
                {
                    throw new InvalidOperationException(_failedMessage);
                }
                await _completed.Task;
            }
            finally
            {
                owner.Closed -= OnOwnerClosed;
                manager.PrintTaskRequested -= OnPrintTaskRequested;
                if (_printTask != null) _printTask.Completed -= OnPrintTaskCompleted;
                _document.Paginate -= OnPaginate;
                _document.GetPreviewPage -= OnGetPreviewPage;
                _document.AddPages -= OnAddPages;
                _stop.Cancel();
                // Finish any in-flight rendering before releasing the PDF stream.
                await _renderGate.WaitAsync();
                _previewCanvas.Children.Clear();
                host.Children.Remove(_previewCanvas);
            }
        }

        private void OnOwnerClosed(object sender, WindowEventArgs args) => _completed.TrySetCanceled();

        private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            try
            {
                _printTask = args.Request.CreatePrintTask(_title, request => request.SetSource(_documentSource));
                _printTask.Completed += OnPrintTaskCompleted;
                _printTask.IsPreviewEnabled = true;

                var options = _printTask.Options;
                if (!options.DisplayedOptions.Contains(StandardPrintTaskOptions.CustomPageRanges))
                {
                    options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);
                }
                options.PageRangeOptions.AllowAllPages = true;
                options.PageRangeOptions.AllowCurrentPage = true;
                options.PageRangeOptions.AllowCustomSetOfPages = true;
                options.Orientation = _orientation;
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                _completed.TrySetException(ex);
            }
        }

        private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        {
            if (args.Completion == PrintTaskCompletion.Failed)
            {
                _completed.TrySetException(new InvalidOperationException(_failedMessage));
            }
            else
            {
                _completed.TrySetResult();
            }
        }

        private void OnPaginate(object sender, PaginateEventArgs args)
        {
            try
            {
                var description = args.PrintTaskOptions.GetPageDescription(0);
                if (_paginationVersion == 0 || !_previewDescription.Equals(description))
                {
                    _paginationVersion++;
                    _previewDescription = description;
                    _previewPages.Clear();
                    _previewOrder.Clear();
                    _previewCanvas.Children.Clear();
                }
                _document.SetPreviewPageCount(checked((int)_pdf.PageCount), PreviewPageCountType.Final);
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                _completed.TrySetException(ex);
            }
        }

        private async void OnGetPreviewPage(object sender, GetPreviewPageEventArgs args)
        {
            try
            {
                int pageNumber = args.PageNumber;
                int version = _paginationVersion;
                if (!_previewPages.TryGetValue(pageNumber, out var page))
                {
                    var rendered = await RenderPageAsync(pageNumber, _previewDescription, PreviewDpi);
                    // Print settings may have changed while the page was rendering.
                    if (_stop.IsCancellationRequested || version != _paginationVersion) return;

                    if (!_previewPages.TryGetValue(pageNumber, out page))
                    {
                        page = rendered;
                        if (_previewPages.Count == MaxPreviewPages)
                        {
                            int oldest = _previewOrder.Dequeue();
                            _previewCanvas.Children.Remove(_previewPages[oldest]);
                            _previewPages.Remove(oldest);
                        }
                        _previewPages.Add(pageNumber, page);
                        _previewOrder.Enqueue(pageNumber);
                        _previewCanvas.Children.Add(page);
                        _previewCanvas.UpdateLayout();
                    }
                }
                _document.SetPreviewPage(pageNumber, page);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                _completed.TrySetException(ex);
            }
        }

        private async void OnAddPages(object sender, AddPagesEventArgs args)
        {
            try
            {
                var options = args.PrintTaskOptions;
                var description = options.GetPageDescription(0);
                int dpi = description.DpiX == 0 || description.DpiY == 0 ? MaxPrintDpi
                    : (int)Math.Min((uint)MaxPrintDpi, Math.Min(description.DpiX, description.DpiY));
                bool added = false;
                foreach (int pageNumber in GetPagesToPrint(checked((int)_pdf.PageCount), options.CustomPageRanges))
                {
                    var page = await RenderPageAsync(pageNumber, description, dpi);
                    if (_stop.IsCancellationRequested) return;
                    _document.AddPage(page);
                    added = true;
                }
                if (!added) throw new InvalidOperationException(_failedMessage);
                _document.AddPagesComplete();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                // A failed render must not submit the pages already added.
                _completed.TrySetException(ex);
            }
        }

        internal static IEnumerable<int> GetPagesToPrint(int pageCount, IList<PrintPageRange> ranges)
        {
            if (ranges.Count == 0)
            {
                for (int page = 1; page <= pageCount; page++) yield return page;
                yield break;
            }

            foreach (var range in ranges)
            {
                int last = Math.Min(range.LastPageNumber, pageCount);
                for (int page = Math.Max(1, range.FirstPageNumber); page <= last; page++)
                {
                    yield return page;
                }
            }
        }

        private async Task<Canvas> RenderPageAsync(int pageNumber, PrintPageDescription description, int dpi)
        {
            CancellationToken cancellation = _stop.Token;
            await _renderGate.WaitAsync(cancellation);
            try
            {
                using var page = _pdf.GetPage(checked((uint)(pageNumber - 1)));
                Rect bounds = FitPage(page.Size, description.PageSize, description.ImageableRect);
                uint pixelWidth = (uint)Math.Max(1, Math.Round(bounds.Width * dpi / 96));
                uint pixelHeight = (uint)Math.Max(1, Math.Round(bounds.Height * dpi / 96));
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                {
                    DestinationWidth = pixelWidth,
                    DestinationHeight = pixelHeight,
                    BitmapEncoderId = BitmapEncoder.PngEncoderId,
                    BackgroundColor = Microsoft.UI.Colors.White,
                    IsIgnoringHighContrast = true
                }).AsTask(cancellation);
                stream.Seek(0);
                // Printing can consume a page before XAML has loaded a lazy BitmapImage.
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
                // The PDF renderer takes DIPs. Decode to the requested pixel size regardless of display scale.
                var transform = new BitmapTransform { ScaledWidth = pixelWidth, ScaledHeight = pixelHeight };
                using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellation);
                cancellation.ThrowIfCancellationRequested();
                var bitmap = new WriteableBitmap(pixels.PixelWidth, pixels.PixelHeight);
                pixels.CopyToBuffer(bitmap.PixelBuffer);
                bitmap.Invalidate();

                var paper = new Canvas
                {
                    Width = description.PageSize.Width,
                    Height = description.PageSize.Height,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White)
                };
                var image = new Image { Source = bitmap, Width = bounds.Width, Height = bounds.Height, Stretch = Stretch.Uniform };
                Canvas.SetLeft(image, bounds.X);
                Canvas.SetTop(image, bounds.Y);
                paper.Children.Add(image);
                paper.Measure(description.PageSize);
                paper.Arrange(new Rect(0, 0, paper.Width, paper.Height));
                return paper;
            }
            finally
            {
                _renderGate.Release();
            }
        }

        internal static Rect FitPage(Size page, Size paper, Rect imageable)
        {
            double left = Math.Max(0, imageable.X);
            double top = Math.Max(0, imageable.Y);
            double width = Math.Min(paper.Width, imageable.Right) - left;
            double height = Math.Min(paper.Height, imageable.Bottom) - top;
            if (width <= 0 || height <= 0 || page.Width <= 0 || page.Height <= 0)
            {
                throw new InvalidDataException();
            }

            double scale = Math.Min(1, Math.Min(width / page.Width, height / page.Height));
            double pageWidth = page.Width * scale;
            double pageHeight = page.Height * scale;
            return new Rect(left + (width - pageWidth) / 2, top + (height - pageHeight) / 2, pageWidth, pageHeight);
        }
    }
}
