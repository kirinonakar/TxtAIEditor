using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;

namespace TxtAIEditor
{
    public class CustomSplitter : Grid
    {
        private const string SplitterBackgroundBrushKey = "SplitterBackgroundBrush";
        private const string SplitterHoverBackgroundBrushKey = "SplitterHoverBackgroundBrush";

        public static readonly DependencyProperty VisualThicknessProperty =
            DependencyProperty.Register(
                nameof(VisualThickness),
                typeof(double),
                typeof(CustomSplitter),
                new PropertyMetadata(double.NaN, OnVisualThicknessChanged));

        public static readonly DependencyProperty OpaqueBackgroundProperty =
            DependencyProperty.Register(
                nameof(OpaqueBackground),
                typeof(bool),
                typeof(CustomSplitter),
                new PropertyMetadata(false, OnOpaqueBackgroundChanged));

        private readonly Border _visualLine = new();

        private bool _isHorizontalSplitter;
        private bool _isPointerOver;
        private bool _isDragging;

        public CustomSplitter()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _visualLine.IsHitTestVisible = false;
            Children.Add(_visualLine);

            this.PointerEntered += CustomSplitter_PointerEntered;
            this.PointerExited += CustomSplitter_PointerExited;
            this.PointerPressed += CustomSplitter_PointerPressed;
            this.PointerReleased += CustomSplitter_PointerReleased;
            this.PointerCanceled += CustomSplitter_PointerCanceled;
            this.PointerCaptureLost += CustomSplitter_PointerCaptureLost;
            this.Loaded += CustomSplitter_Loaded;
            this.Unloaded += CustomSplitter_Unloaded;
            this.SizeChanged += CustomSplitter_SizeChanged;
            this.ActualThemeChanged += CustomSplitter_ActualThemeChanged;
        }

        public double VisualThickness
        {
            get => (double)GetValue(VisualThicknessProperty);
            set => SetValue(VisualThicknessProperty, value);
        }

        public bool OpaqueBackground
        {
            get => (bool)GetValue(OpaqueBackgroundProperty);
            set => SetValue(OpaqueBackgroundProperty, value);
        }

        private static void OnVisualThicknessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CustomSplitter splitter)
            {
                splitter.UpdateVisualLineLayout();
            }
        }

        private static void OnOpaqueBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CustomSplitter splitter)
            {
                splitter.RefreshTheme();
            }
        }

        private void CustomSplitter_ActualThemeChanged(FrameworkElement sender, object args)
        {
            RefreshTheme();
        }

        public void RefreshTheme()
        {
            ApplyBackground(IsResizePointerActive
                ? SplitterHoverBackgroundBrushKey
                : SplitterBackgroundBrushKey);
        }

        private void CustomSplitter_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateVisualLineLayout();
            ApplyBackground(SplitterBackgroundBrushKey);
        }

        private void CustomSplitter_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVisualLineLayout();
        }

        private void CustomSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            if (!_isDragging)
            {
                _isHorizontalSplitter = IsHorizontalSplitter();
            }

            UpdatePointerVisualState();
        }

        private void CustomSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            UpdatePointerVisualState();
        }

        private void CustomSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDragging = true;
            _isHorizontalSplitter = IsHorizontalSplitter();
            UpdatePointerVisualState();
        }

        private void CustomSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            _isDragging = false;
            _isPointerOver = IsPointerWithinBounds(e);
            UpdatePointerVisualState();
        }

        private void CustomSplitter_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _isPointerOver = IsPointerWithinBounds(e);
            UpdatePointerVisualState();
        }

        private void CustomSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDragging = false;
            _isPointerOver = IsPointerWithinBounds(e);
            UpdatePointerVisualState();
        }

        private void CustomSplitter_Unloaded(object sender, RoutedEventArgs e)
        {
            ClearPointerState();
        }

        private void ClearPointerState()
        {
            _isPointerOver = false;
            _isDragging = false;
            this.ProtectedCursor = null;
            ApplyBackground(SplitterBackgroundBrushKey);
        }

        private bool IsResizePointerActive => _isPointerOver || _isDragging;

        private void UpdatePointerVisualState()
        {
            if (IsResizePointerActive)
            {
                this.ProtectedCursor = InputSystemCursor.Create(_isHorizontalSplitter
                    ? InputSystemCursorShape.SizeNorthSouth
                    : InputSystemCursorShape.SizeWestEast);
                ApplyBackground(SplitterHoverBackgroundBrushKey);
                return;
            }

            this.ProtectedCursor = null;
            ApplyBackground(SplitterBackgroundBrushKey);
        }

        private bool IsPointerWithinBounds(PointerRoutedEventArgs e)
        {
            var position = e.GetCurrentPoint(this).Position;
            return position.X >= 0 &&
                position.Y >= 0 &&
                position.X <= ActualWidth &&
                position.Y <= ActualHeight;
        }

        private void UpdateVisualLineLayout()
        {
            bool isHorizontalSplitter = IsHorizontalSplitter();
            double visualThickness = GetVisualThickness(isHorizontalSplitter);

            if (isHorizontalSplitter)
            {
                _visualLine.Width = double.NaN;
                _visualLine.Height = visualThickness;
                _visualLine.HorizontalAlignment = HorizontalAlignment.Stretch;
                _visualLine.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                _visualLine.Width = visualThickness;
                _visualLine.Height = double.NaN;
                _visualLine.HorizontalAlignment = HorizontalAlignment.Center;
                _visualLine.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }

        private bool IsHorizontalSplitter()
        {
            double width = GetCurrentSize(ActualWidth, Width);
            double height = GetCurrentSize(ActualHeight, Height);
            return width > height * 4;
        }

        private double GetVisualThickness(bool isHorizontalSplitter)
        {
            if (!double.IsNaN(VisualThickness) && VisualThickness > 0)
            {
                return VisualThickness;
            }

            double currentThickness = isHorizontalSplitter
                ? GetCurrentSize(ActualHeight, Height)
                : GetCurrentSize(ActualWidth, Width);

            return currentThickness > 0 ? currentThickness : 2;
        }

        private static double GetCurrentSize(double actualSize, double requestedSize)
        {
            if (actualSize > 0)
            {
                return actualSize;
            }

            return !double.IsNaN(requestedSize) && requestedSize > 0 ? requestedSize : 0;
        }

        private void ApplyBackground(string resourceKey)
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            // If a custom theme is active, resolve resource from Application.Current.Resources
            if (Application.Current.Resources.TryGetValue("ActiveTheme", out var activeThemeObj) &&
                activeThemeObj is string activeTheme &&
                activeTheme == "PastelDark")
            {
                if (Application.Current.Resources.TryGetValue(resourceKey, out object resource) && resource is Brush brush)
                {
                    if (OpaqueBackground)
                        Background = brush;
                    _visualLine.Background = brush;
                    return;
                }
            }

            // Otherwise, fall back to standard theme dictionaries based on ActualTheme
            string themeKey = this.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj) &&
                dictObj is ResourceDictionary themeDict &&
                themeDict.TryGetValue(resourceKey, out var brushObj) &&
                brushObj is Brush themeBrush)
            {
                if (OpaqueBackground)
                    Background = themeBrush;
                _visualLine.Background = themeBrush;
                return;
            }

            if (this.Resources.TryGetValue(resourceKey, out object localResource) && localResource is Brush localBrush)
            {
                if (OpaqueBackground)
                    Background = localBrush;
                _visualLine.Background = localBrush;
            }
        }
    }
}
