using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TxtAIEditor.Core.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace TxtAIEditor.Controls
{
    internal sealed class ImageCropSelector : StackPanel, IDisposable
    {
        private readonly Func<string, string, string> _getString;
        private readonly ImageConversionSourceInfo _sourceInfo;
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly Image _previewImage = new() { Stretch = Stretch.Uniform, Height = 160 };
        private readonly Grid _surface = new() { Height = 240 };
        private readonly StackPanel _selectionContent = new() { Spacing = 8 };
        private readonly Canvas _overlay = new()
        {
            Background = new SolidColorBrush(Colors.Transparent),
            ManipulationMode = ManipulationModes.None
        };
        // A black and white outline remains visible over both bright and dark images.
        private readonly Border _selection = new()
        {
            BorderBrush = new SolidColorBrush(Colors.Black),
            BorderThickness = new Thickness(1),
            Child = new Border
            {
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1)
            },
            IsHitTestVisible = false
        };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _selectionStatus = new() { TextWrapping = TextWrapping.Wrap };
        private SoftwareBitmap? _bitmap;
        private ImageCropRegion _savedRegion;
        private bool _disposed;
        private Task? _loadTask;
        private uint? _pointerId;
        private Point _start;
        private ImageCropRegion _previousRegion;

        public ImageCropRegion Region { get; private set; } = ImageCropRegion.FullImage;
        public bool IsReady { get; private set; }
        public bool HasSelection { get; private set; }
        public bool IsSelecting => _pointerId.HasValue;
        public FrameworkElement SelectionContent => _selectionContent;
        public event EventHandler? SelectionChanged;
        public event EventHandler? SelectionRequested;

        public ImageCropSelector(
            ImageConversionSourceInfo sourceInfo,
            Func<string, string, string> getString,
            bool isBatch)
        {
            _sourceInfo = sourceInfo;
            _getString = getString;
            Spacing = 6;
            Visibility = Visibility.Collapsed;
            Children.Add(new TextBlock
            {
                Text = getString("ImageConvertCropPreview", "선택 영역 미리보기"),
                TextWrapping = TextWrapping.Wrap
            });
            Children.Add(_previewImage);
            Children.Add(_status);
            var selectButton = new Button
            {
                Content = getString("ImageConvertCropReselect", "영역 다시 선택")
            };
            selectButton.Click += (_, __) => SelectionRequested?.Invoke(this, EventArgs.Empty);
            Children.Add(selectButton);
            if (isBatch)
            {
                Children.Add(new TextBlock
                {
                    Text = getString("ImageConvertCropBatchHint", "첫 이미지에서 선택한 영역을 모든 이미지에 같은 비율로 적용합니다."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                });
            }

            _surface.Children.Add(_image);
            _surface.Children.Add(_overlay);
            _overlay.Children.Add(_selection);
            _selectionContent.Children.Add(new TextBlock
            {
                Text = getString("ImageConvertCropHint", "이미지에서 드래그하여 저장할 영역을 선택하세요. 크롭 후 크기 조정이 적용됩니다."),
                TextWrapping = TextWrapping.Wrap
            });
            _selectionContent.Children.Add(_surface);
            _selectionContent.Children.Add(_selectionStatus);
            var resetButton = new Button
            {
                Content = getString("ImageConvertCropReset", "전체 영역 선택")
            };
            resetButton.Click += (_, __) =>
            {
                CancelSelection();
                Region = ImageCropRegion.FullImage;
                UpdateSelection();
            };
            _selectionContent.Children.Add(resetButton);

            _overlay.PointerPressed += OnPointerPressed;
            _overlay.PointerMoved += OnPointerMoved;
            _overlay.PointerReleased += OnPointerReleased;
            _overlay.PointerCanceled += (_, __) => CancelSelection();
            _overlay.PointerCaptureLost += (_, __) => CancelSelection();
            _surface.SizeChanged += (_, __) => UpdateSelection();
            _surface.Unloaded += (_, __) => CancelSelection();
        }

        public FrameworkElement BeginSelection(Size availableSize)
        {
            _savedRegion = Region;
            UpdateSelectionSize(availableSize);
            return _selectionContent;
        }

        public void UpdateSelectionSize(Size availableSize)
        {
            _selectionContent.Width = Math.Max(160, Math.Min(1600, availableSize.Width - 112));
            _surface.Height = Math.Max(120, Math.Min(1000, availableSize.Height - 280));
            UpdateSelection();
        }

        public void RevertSelection()
        {
            CancelSelection();
            Region = _savedRegion;
            UpdateSelection();
        }

        public async Task<bool> ApplySelectionAsync()
        {
            if (_bitmap == null || !IsReady || IsSelecting)
            {
                return false;
            }
            try
            {
                uint width = (uint)_bitmap.PixelWidth;
                uint height = (uint)_bitmap.PixelHeight;
                byte[] pixels = new byte[checked((int)(width * height * 4))];
                _bitmap.CopyToBuffer(pixels.AsBuffer());
                ImageCropBounds bounds = Region.GetPixelBounds(width, height);
                int stride = checked((int)width * 4);
                int cropStride = checked((int)bounds.Width * 4);
                byte[] cropped = new byte[checked(cropStride * (int)bounds.Height)];
                for (int row = 0; row < bounds.Height; row++)
                {
                    System.Buffer.BlockCopy(pixels, checked(((int)bounds.Y + row) * stride + (int)bounds.X * 4),
                        cropped, row * cropStride, cropStride);
                }
                using var preview = SoftwareBitmap.CreateCopyFromBuffer(cropped.AsBuffer(), BitmapPixelFormat.Bgra8,
                    (int)bounds.Width, (int)bounds.Height, BitmapAlphaMode.Premultiplied);
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(preview);
                _previewImage.Source = source;
                HasSelection = true;
                _status.Text = GetSelectionText();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception)
            {
                _selectionStatus.Text = _getString("ImageConvertCropPreviewError", "미리보기를 불러올 수 없습니다. 크롭을 해제하거나 다시 체크해 주세요.");
                return false;
            }
        }

        public async Task LoadAsync(string sourcePath, ImageConversionService service)
        {
            _loadTask ??= LoadCoreAsync(sourcePath, service);
            await _loadTask;
            if (!IsReady)
            {
                _loadTask = null;
            }
        }

        private async Task LoadCoreAsync(string sourcePath, ImageConversionService service)
        {
            SoftwareBitmap? bitmap = null;
            try
            {
                _selectionStatus.Text = _getString("ImageConvertCropLoading", "미리보기를 불러오는 중...");
                bitmap = await service.ReadPreviewAsync(sourcePath);
                if (_disposed)
                {
                    return;
                }
                var imageSource = new SoftwareBitmapSource();
                await imageSource.SetBitmapAsync(bitmap);
                if (_disposed)
                {
                    return;
                }
                // Dispose must not close a bitmap while SetBitmapAsync is still reading it.
                _bitmap?.Dispose();
                _bitmap = bitmap;
                bitmap = null;
                _image.Source = imageSource;
                IsReady = true;
                UpdateSelection();
            }
            catch (Exception)
            {
                if (!_disposed)
                {
                    _selectionStatus.Text = _getString("ImageConvertCropPreviewError", "미리보기를 불러올 수 없습니다. 크롭을 해제하거나 다시 체크해 주세요.");
                }
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        public void CancelSelection()
        {
            if (!_pointerId.HasValue)
            {
                return;
            }
            _pointerId = null;
            Region = _previousRegion;
            _overlay.ReleasePointerCaptures();
            UpdateSelection();
        }

        private Rect GetImageRect()
        {
            double scale = Math.Min(_surface.ActualWidth / _sourceInfo.Width, _surface.ActualHeight / _sourceInfo.Height);
            double width = _sourceInfo.Width * scale;
            double height = _sourceInfo.Height * scale;
            return new Rect((_surface.ActualWidth - width) / 2, (_surface.ActualHeight - height) / 2, width, height);
        }

        private Point GetRelativePoint(PointerRoutedEventArgs e)
        {
            Rect rect = GetImageRect();
            Point point = e.GetCurrentPoint(_overlay).Position;
            return new Point(Math.Clamp((point.X - rect.X) / rect.Width, 0, 1),
                Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!IsReady || IsSelecting || !e.GetCurrentPoint(_overlay).Properties.IsLeftButtonPressed ||
                !GetImageRect().Contains(e.GetCurrentPoint(_overlay).Position))
            {
                return;
            }
            if (_overlay.CapturePointer(e.Pointer))
            {
                _previousRegion = Region;
                _start = GetRelativePoint(e);
                _pointerId = e.Pointer.PointerId;
                e.Handled = true;
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointerId != e.Pointer.PointerId)
            {
                return;
            }
            UpdateRegion(GetRelativePoint(e));
            e.Handled = true;
        }

        private void UpdateRegion(Point end)
        {
            if (Math.Abs(end.X - _start.X) * _sourceInfo.Width < 1 ||
                Math.Abs(end.Y - _start.Y) * _sourceInfo.Height < 1)
            {
                Region = _previousRegion;
            }
            else
            {
                Region = new ImageCropRegion(Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y),
                    Math.Max(_start.X, end.X), Math.Max(_start.Y, end.Y));
            }
            UpdateSelection();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_pointerId != e.Pointer.PointerId)
            {
                return;
            }
            UpdateRegion(GetRelativePoint(e));
            _pointerId = null;
            _overlay.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void UpdateSelection()
        {
            if (_disposed)
            {
                return;
            }
            _selection.Visibility = IsReady ? Visibility.Visible : Visibility.Collapsed;
            if (!IsReady)
            {
                return;
            }
            Rect rect = GetImageRect();
            ImageCropBounds bounds = Region.GetPixelBounds(_sourceInfo.Width, _sourceInfo.Height);
            Canvas.SetLeft(_selection, rect.X + rect.Width * bounds.X / _sourceInfo.Width);
            Canvas.SetTop(_selection, rect.Y + rect.Height * bounds.Y / _sourceInfo.Height);
            _selection.Width = rect.Width * bounds.Width / _sourceInfo.Width;
            _selection.Height = rect.Height * bounds.Height / _sourceInfo.Height;
            _selectionStatus.Text = GetSelectionText();
        }

        private string GetSelectionText()
        {
            ImageCropBounds bounds = Region.GetPixelBounds(_sourceInfo.Width, _sourceInfo.Height);
            return string.Format(_getString("ImageConvertCropSelection", "선택 영역: X {0}, Y {1} · {2} × {3}px"),
                bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        public void Dispose()
        {
            _disposed = true;
            CancelSelection();
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
