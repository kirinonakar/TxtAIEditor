using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
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
        private const double HandleSize = 10;
        private const double HandleHitRadius = 12;
        private readonly Func<string, string, string> _getString;
        private readonly ImageConversionSourceInfo _sourceInfo;
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly Image _previewImage = new() { Stretch = Stretch.Uniform, Height = 160 };
        private readonly Grid _surface = new() { Height = 240 };
        private readonly StackPanel _selectionContent = new() { Spacing = 8 };
        private readonly CropCanvas _overlay = new()
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
        private readonly Border[] _handles = new Border[4];
        private readonly TextBlock _selectionStatus = new() { TextWrapping = TextWrapping.Wrap };
        private SoftwareBitmap? _bitmap;
        private ImageCropRegion _savedRegion;
        private bool _disposed;
        private Task? _loadTask;
        private uint? _pointerId;
        private Point _start;
        private ImageCropRegion _previousRegion;
        private ImageCropCorner? _resizeCorner;
        private bool _moveSelection;
        private bool _canEditRegion;
        private bool _previousCanEditRegion;
        private bool _savedCanEditRegion;

        public ImageCropRegion Region { get; private set; } = ImageCropRegion.FullImage;
        public bool IsReady { get; private set; }
        public bool HasSelection { get; private set; }
        public bool IsSelecting => _pointerId.HasValue;
        public bool CanApplySelection => IsReady && _canEditRegion && !IsSelecting;
        public FrameworkElement SelectionContent => _selectionContent;
        public Button ResetButton { get; }
        public Button ClearButton { get; }
        public event EventHandler? SelectionChanged;
        public event EventHandler? SelectionAvailabilityChanged;
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
            for (int index = 0; index < _handles.Length; index++)
            {
                var handle = new Border
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Background = new SolidColorBrush(Colors.White),
                    BorderBrush = new SolidColorBrush(Colors.Black),
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                _handles[index] = handle;
                _overlay.Children.Add(handle);
            }
            _selectionContent.Children.Add(new TextBlock
            {
                Text = getString("ImageConvertCropHint", "드래그하여 영역을 선택하세요. 모서리로 크기를 조절하고 안쪽을 드래그하여 이동할 수 있습니다. 크롭 후 크기 조정이 적용됩니다."),
                TextWrapping = TextWrapping.Wrap
            });
            _selectionContent.Children.Add(_surface);
            _selectionContent.Children.Add(_selectionStatus);
            ResetButton = new Button
            {
                Content = getString("ImageConvertCropReset", "전체 영역 선택")
            };
            ResetButton.Click += (_, __) =>
            {
                CancelSelection();
                Region = ImageCropRegion.FullImage;
                _canEditRegion = true;
                UpdateSelection();
            };
            ClearButton = new Button
            {
                Content = getString("ImageConvertCropClear", "선택 지우기")
            };
            ClearButton.Click += (_, __) =>
            {
                CancelSelection();
                Region = ImageCropRegion.FullImage;
                _canEditRegion = false;
                UpdateSelection();
            };

            _overlay.PointerPressed += OnPointerPressed;
            _overlay.PointerMoved += OnPointerMoved;
            _overlay.PointerEntered += (_, e) => UpdatePointerCursor(e.GetCurrentPoint(_overlay).Position);
            _overlay.PointerExited += (_, __) =>
            {
                if (!IsSelecting)
                {
                    _overlay.SetPointerCursor(InputSystemCursorShape.Arrow);
                }
            };
            _overlay.PointerReleased += OnPointerReleased;
            _overlay.PointerCanceled += (_, __) => CancelSelection();
            _overlay.PointerCaptureLost += (_, __) => CancelSelection();
            _surface.SizeChanged += (_, __) => UpdateSelection();
            _surface.Unloaded += (_, __) => CancelSelection();
        }

        public FrameworkElement BeginSelection(Size availableSize)
        {
            _savedRegion = Region;
            _savedCanEditRegion = _canEditRegion;
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
            _canEditRegion = _savedCanEditRegion;
            UpdateSelection();
        }

        public async Task<bool> ApplySelectionAsync()
        {
            if (_bitmap == null || !CanApplySelection)
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
                _canEditRegion = true;
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
            _canEditRegion = _previousCanEditRegion;
            _resizeCorner = null;
            _moveSelection = false;
            _overlay.ReleasePointerCaptures();
            _overlay.SetPointerCursor(InputSystemCursorShape.Arrow);
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
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return _start;
            }
            Point point = e.GetCurrentPoint(_overlay).Position;
            return new Point(Math.Clamp((point.X - rect.X) / rect.Width, 0, 1),
                Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_overlay);
            Rect imageRect = GetImageRect();
            if (!IsReady || IsSelecting || !point.Properties.IsLeftButtonPressed || imageRect.Width <= 0 || imageRect.Height <= 0)
            {
                return;
            }
            ImageCropCorner? corner = HitTestCorner(point.Position);
            if (!corner.HasValue && !imageRect.Contains(point.Position))
            {
                return;
            }
            if (_overlay.CapturePointer(e.Pointer))
            {
                _previousRegion = Region;
                _previousCanEditRegion = _canEditRegion;
                _resizeCorner = corner;
                _moveSelection = !corner.HasValue && _canEditRegion && GetSelectionRect().Contains(point.Position);
                _start = GetRelativePoint(e);
                _pointerId = e.Pointer.PointerId;
                UpdatePointerCursor(point.Position);
                SelectionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointerId != e.Pointer.PointerId)
            {
                if (!IsSelecting)
                {
                    UpdatePointerCursor(e.GetCurrentPoint(_overlay).Position);
                }
                return;
            }
            UpdateRegion(GetRelativePoint(e));
            e.Handled = true;
        }

        private void UpdateRegion(Point end)
        {
            if (_resizeCorner is ImageCropCorner corner)
            {
                Region = _previousRegion.ResizeCorner(corner, end.X - _start.X, end.Y - _start.Y,
                    _sourceInfo.Width, _sourceInfo.Height);
            }
            else if (_moveSelection)
            {
                Region = _previousRegion.Move(end.X - _start.X, end.Y - _start.Y, _sourceInfo.Width, _sourceInfo.Height);
            }
            else if (Math.Abs(end.X - _start.X) * _sourceInfo.Width < 1 ||
                Math.Abs(end.Y - _start.Y) * _sourceInfo.Height < 1)
            {
                Region = _previousRegion;
                _canEditRegion = _previousCanEditRegion;
            }
            else
            {
                Region = new ImageCropRegion(Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y),
                    Math.Max(_start.X, end.X), Math.Max(_start.Y, end.Y));
                _canEditRegion = true;
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
            _resizeCorner = null;
            _moveSelection = false;
            _overlay.ReleasePointerCapture(e.Pointer);
            UpdatePointerCursor(e.GetCurrentPoint(_overlay).Position);
            SelectionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void UpdateSelection()
        {
            if (_disposed)
            {
                return;
            }
            _selection.Visibility = IsReady && _canEditRegion ? Visibility.Visible : Visibility.Collapsed;
            ResetButton.IsEnabled = IsReady;
            ClearButton.IsEnabled = IsReady && _canEditRegion;
            foreach (Border handle in _handles)
            {
                handle.Visibility = IsReady && _canEditRegion ? Visibility.Visible : Visibility.Collapsed;
            }
            if (!IsReady)
            {
                SelectionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            Rect rect = GetSelectionRect();
            Canvas.SetLeft(_selection, rect.X);
            Canvas.SetTop(_selection, rect.Y);
            _selection.Width = rect.Width;
            _selection.Height = rect.Height;
            for (int index = 0; index < _handles.Length; index++)
            {
                Point corner = GetCornerPoint(rect, (ImageCropCorner)index);
                Canvas.SetLeft(_handles[index], corner.X - HandleSize / 2);
                Canvas.SetTop(_handles[index], corner.Y - HandleSize / 2);
            }
            _selectionStatus.Text = _canEditRegion
                ? GetSelectionText()
                : _getString("ImageConvertCropEmpty", "선택된 영역이 없습니다.");
            SelectionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }

        private Rect GetSelectionRect()
        {
            Rect imageRect = GetImageRect();
            ImageCropBounds bounds = Region.GetPixelBounds(_sourceInfo.Width, _sourceInfo.Height);
            return new Rect(imageRect.X + imageRect.Width * bounds.X / _sourceInfo.Width,
                imageRect.Y + imageRect.Height * bounds.Y / _sourceInfo.Height,
                imageRect.Width * bounds.Width / _sourceInfo.Width, imageRect.Height * bounds.Height / _sourceInfo.Height);
        }

        private static Point GetCornerPoint(Rect rect, ImageCropCorner corner) => corner switch
        {
            ImageCropCorner.TopLeft => new Point(rect.Left, rect.Top),
            ImageCropCorner.TopRight => new Point(rect.Right, rect.Top),
            ImageCropCorner.BottomRight => new Point(rect.Right, rect.Bottom),
            _ => new Point(rect.Left, rect.Bottom)
        };

        private ImageCropCorner? HitTestCorner(Point point)
        {
            if (!IsReady || !_canEditRegion)
            {
                return null;
            }
            Rect rect = GetSelectionRect();
            // Leave an interior move target even when a small selection's handles overlap.
            double radius = Math.Min(HandleHitRadius, Math.Min(rect.Width, rect.Height) / 3);
            double nearestDistance = radius * radius;
            ImageCropCorner? nearest = null;
            for (int index = 0; index < _handles.Length; index++)
            {
                Point corner = GetCornerPoint(rect, (ImageCropCorner)index);
                double dx = point.X - corner.X;
                double dy = point.Y - corner.Y;
                double distance = dx * dx + dy * dy;
                if (distance <= nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = (ImageCropCorner)index;
                }
            }
            return nearest;
        }

        private void UpdatePointerCursor(Point point)
        {
            ImageCropCorner? corner = IsSelecting ? _resizeCorner : HitTestCorner(point);
            InputSystemCursorShape shape;
            if (corner.HasValue)
            {
                shape = corner is ImageCropCorner.TopLeft or ImageCropCorner.BottomRight
                    ? InputSystemCursorShape.SizeNorthwestSoutheast
                    : InputSystemCursorShape.SizeNortheastSouthwest;
            }
            else if (IsSelecting ? _moveSelection : IsReady && _canEditRegion && GetSelectionRect().Contains(point))
            {
                shape = InputSystemCursorShape.SizeAll;
            }
            else
            {
                shape = IsReady && (IsSelecting || GetImageRect().Contains(point))
                    ? InputSystemCursorShape.Cross
                    : InputSystemCursorShape.Arrow;
            }
            _overlay.SetPointerCursor(shape);
        }

        private sealed class CropCanvas : Canvas
        {
            private static readonly InputSystemCursor Arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            private static readonly InputSystemCursor Cross = InputSystemCursor.Create(InputSystemCursorShape.Cross);
            private static readonly InputSystemCursor Move = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            private static readonly InputSystemCursor NorthwestSoutheast = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
            private static readonly InputSystemCursor NortheastSouthwest = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
            private InputSystemCursorShape? _shape;

            public void SetPointerCursor(InputSystemCursorShape shape)
            {
                if (_shape == shape)
                {
                    return;
                }
                _shape = shape;
                ProtectedCursor = shape switch
                {
                    InputSystemCursorShape.Cross => Cross,
                    InputSystemCursorShape.SizeAll => Move,
                    InputSystemCursorShape.SizeNorthwestSoutheast => NorthwestSoutheast,
                    InputSystemCursorShape.SizeNortheastSouthwest => NortheastSouthwest,
                    _ => Arrow
                };
            }
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
