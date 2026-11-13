using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace TxtAIEditor.Controls
{
    internal sealed class QuickNoteWindow : Window
    {
        private const int MinClientWidth = 256;
        private const int MinClientHeight = 244;
        private const int FoldedHeight = 46;
        private const int ButtonSize = 28;
        private static readonly string[] BackgroundColors = { "#FFF1A6", "#FFD5E6", "#C8E7FF", "#CDEFD4", "#E9DBFF", "#28272B" };
        private static readonly string[] ForegroundColors = { "#26201B", "#FFFFFF", "#A02640", "#1A54A0", "#13633C", "#7140A8" };

        private readonly QuickNote _note;
        private readonly Func<string, string, string> _text;
        private readonly Func<string> _displayTitle;
        private readonly Action _changed;
        private readonly IntPtr _hwnd;
        private readonly OverlappedPresenter _presenter;
        private readonly Grid _root = new() { Padding = new Thickness(6, 4, 6, 4), RowSpacing = 4 };
        private readonly Grid _body = new();
        private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        private readonly TextBox _titleInput;
        private readonly TextBox _source;
        private readonly TextBlock _foldedTitle = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Visibility = Visibility.Collapsed };
        private readonly Button _foldButton;
        private readonly Button _closeButton;
        private readonly ToggleButton _liveButton;
        private readonly QuickNotePreview _preview;
        private readonly SolidColorBrush _paper = new();
        private readonly SolidColorBrush _ink = new();
        private readonly FontIcon _dragIcon = Icon("\uE7C2");
        private Color _background;
        private Color _foreground;
        private string _storageError = string.Empty;
        private string _previewError = string.Empty;
        private bool _applyingPlacement;
        private bool _closed;
        private double _scale;
        private PointInt32 _dragOrigin;
        private PointInt32 _dragCursor;
        private uint? _dragPointer;

        public QuickNoteWindow(
            QuickNote note, Func<string, string, string> text, Func<string> displayTitle,
            Action changed, Action delete, int openCount, ElementTheme appTheme)
        {
            _note = note;
            _text = text;
            _displayTitle = displayTitle;
            _changed = changed;
            _root.RequestedTheme = appTheme;
            _hwnd = WindowNative.GetWindowHandle(this);
            _presenter = OverlappedPresenter.Create();
            _presenter.SetBorderAndTitleBar(true, false);
            _presenter.IsAlwaysOnTop = true;
            _presenter.IsMaximizable = false;
            _presenter.IsMinimizable = false;
            AppWindow.SetPresenter(_presenter);
            AppWindow.IsShownInSwitchers = false;
            _note.Opacity = Math.Clamp(_note.Opacity, 35, 100);

            _titleInput = CreateTextBox(note.Title, text("QuickNotesTitle", "제목"));
            _titleInput.Height = 30;
            _titleInput.MaxLength = 200;
            _titleInput.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            _titleInput.TextChanged += (_, _) =>
            {
                _note.Title = _titleInput.Text;
                UpdateTitle();
                _changed();
            };
            _source = CreateTextBox(note.Text, text("QuickNotesBody", "내용"));
            _source.AcceptsReturn = true;
            _source.TextWrapping = TextWrapping.Wrap;
            _source.VerticalAlignment = VerticalAlignment.Stretch;
            ScrollViewer.SetVerticalScrollBarVisibility(_source, ScrollBarVisibility.Auto);
            _source.TextChanged += (_, _) =>
            {
                _note.Text = _source.Text;
                _changed();
            };
            ConfigureTextColors(_titleInput);
            ConfigureTextColors(_source);
            _dragIcon.Foreground = _ink;
            _foldedTitle.Foreground = _ink;
            _status.Foreground = _ink;
            _root.Background = _paper;

            _foldButton = CreateIconButton("\uE70E", text("QuickNotesFold", "접기"));
            _foldButton.Click += (_, _) => ToggleFold();
            _closeButton = CreateIconButton("\uE711", text("QuickNotesHide", "트레이로 닫기"));
            _closeButton.Click += (_, _) => Close();
            _liveButton = new ToggleButton { Content = Icon("\uE890") };
            SizeIconButton(_liveButton);
            _liveButton.Click += (_, _) =>
            {
                _note.IsPreview = _liveButton.IsChecked == true;
                _previewError = string.Empty;
                UpdateStatus();
                ApplyMode();
                _changed();
            };
            _toolbar.Children.Add(_liveButton);
            _toolbar.Children.Add(CreateColorButton(true));
            _toolbar.Children.Add(CreateColorButton(false));
            _toolbar.Children.Add(CreateOpacityButton());
            var deleteButton = CreateIconButton("\uE74D", text("QuickNotesDelete", "삭제"));
            deleteButton.Click += (_, _) => delete();
            _toolbar.Children.Add(deleteButton);

            _preview = new QuickNotePreview(OnPreviewFailed);
            _body.Children.Add(_source);
            _body.Children.Add(_preview);
            AddRow(CreateHeader(), GridLength.Auto);
            AddRow(_toolbar, GridLength.Auto);
            AddRow(_body, new GridLength(1, GridUnitType.Star));
            AddRow(_status, GridLength.Auto);
            Content = _root;

            UpdateTitle();
            ApplyFold();
            PlaceWindow(openCount);
            ApplyColors();
            ApplyOpacity();
            _root.ActualThemeChanged += (_, _) => ApplyColors();
            _root.Loaded += (_, _) =>
            {
                RefreshSizeLimits();
                ApplyMode();
            };
            AppWindow appWindow = AppWindow;
            appWindow.Changed += OnWindowChanged;
            Closed += (_, _) =>
            {
                _closed = true;
                try
                {
                    appWindow.Changed -= OnWindowChanged;
                }
                catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
                {
                    AppErrorHandler.Report(ex);
                }
                _preview.Dispose();
            };
        }

        public void SetStorageError(string message)
        {
            _storageError = message;
            UpdateStatus();
        }

        public void ApplyAppTheme(ElementTheme theme, bool refreshResources)
        {
            if (refreshResources && _root.RequestedTheme == theme)
            {
                _root.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            }
            _root.RequestedTheme = theme;
        }

        private static Button CreateIconButton(string glyph, string label)
        {
            var button = new Button { Content = Icon(glyph) };
            SizeIconButton(button);
            LabelControl(button, label);
            return button;
        }

        private static void SizeIconButton(ButtonBase button)
        {
            button.MinWidth = 0;
            button.MinHeight = 0;
            button.Width = ButtonSize;
            button.Height = ButtonSize;
            button.Padding = new Thickness(0);
        }

        private static FontIcon Icon(string glyph) => new() { Glyph = glyph, FontSize = 14 };

        private static void LabelControl(FrameworkElement control, string label)
        {
            AutomationProperties.SetName(control, label);
            ToolTipService.SetToolTip(control, label);
        }

        private static TextBox CreateTextBox(string value, string placeholder)
        {
            var box = new TextBox
            {
                Text = value,
                PlaceholderText = placeholder,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(5, 3, 5, 3),
                BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(box, placeholder);
            return box;
        }

        private void AddRow(FrameworkElement element, GridLength height)
        {
            Grid.SetRow(element, _root.RowDefinitions.Count);
            _root.RowDefinitions.Add(new RowDefinition { Height = height });
            _root.Children.Add(element);
        }

        private Grid CreateHeader()
        {
            var header = new Grid { ColumnSpacing = 4 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var handle = new Border
            {
                Child = _dragIcon,
                Width = 22,
                MinHeight = ButtonSize,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ToolTipService.SetToolTip(handle, _text("QuickNotesMove", "끌어서 이동"));
            EnableDrag(handle);
            EnableDrag(_foldedTitle);
            header.Children.Add(handle);
            Grid.SetColumn(_titleInput, 1);
            header.Children.Add(_titleInput);
            Grid.SetColumn(_foldedTitle, 1);
            header.Children.Add(_foldedTitle);
            Grid.SetColumn(_foldButton, 2);
            header.Children.Add(_foldButton);
            Grid.SetColumn(_closeButton, 3);
            header.Children.Add(_closeButton);
            return header;
        }

        private void EnableDrag(UIElement handle)
        {
            handle.PointerPressed += (_, args) =>
            {
                if (_dragPointer.HasValue || !args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
                    !GetCursorPos(out _dragCursor) || !handle.CapturePointer(args.Pointer))
                {
                    return;
                }
                _dragOrigin = AppWindow.Position;
                _dragPointer = args.Pointer.PointerId;
                args.Handled = true;
            };

            handle.PointerMoved += (_, args) =>
            {
                if (_dragPointer == args.Pointer.PointerId && GetCursorPos(out PointInt32 cursor))
                {
                    AppWindow.Move(new PointInt32(_dragOrigin.X + cursor.X - _dragCursor.X, _dragOrigin.Y + cursor.Y - _dragCursor.Y));
                    args.Handled = true;
                }
            };
            handle.PointerReleased += (_, args) => handle.ReleasePointerCapture(args.Pointer);
            handle.PointerCaptureLost += (_, args) =>
            {
                if (_dragPointer == args.Pointer.PointerId)
                {
                    _dragPointer = null;
                }
            };
        }

        private Button CreateColorButton(bool background)
        {
            string label = background ? _text("QuickNotesBackground", "바탕색") : _text("QuickNotesForeground", "글자색");
            var button = CreateIconButton(background ? "\uE790" : "\uE8D3", label);
            AttachOptions(button, label, dismiss => CreateColorOptions(background, dismiss));
            return button;
        }

        private FrameworkElement CreateColorOptions(bool background, Action dismiss)
        {
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var hex = new TextBox
            {
                Header = _text("QuickNotesHex", "색상 코드"),
                Text = ToHex(background ? _background : _foreground),
                MaxLength = 7,
                Width = 104
            };
            var apply = new Button { Content = _text("QuickNotesApply", "적용"), VerticalAlignment = VerticalAlignment.Bottom };
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            inputRow.Children.Add(hex);
            inputRow.Children.Add(apply);
            var reset = new Button { Content = _text("QuickNotesDefault", "기본값") };
            var content = new StackPanel { Spacing = 7 };
            content.Children.Add(swatches);
            content.Children.Add(inputRow);
            content.Children.Add(reset);
            void SelectColor(string? value)
            {
                if (background)
                {
                    _note.Background = value;
                }
                else
                {
                    _note.Foreground = value;
                }
                ApplyColors();
                _changed();
                dismiss();
            }

            foreach (string value in background ? BackgroundColors : ForegroundColors)
            {
                TryColor(value, out Color color);
                var swatch = new Button
                {
                    Width = 25,
                    Height = 25,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Content = new Border { Width = 18, Height = 18, Background = new SolidColorBrush(color) }
                };
                LabelControl(swatch, value);
                swatch.Click += (_, _) => SelectColor(value);
                swatches.Children.Add(swatch);
            }

            hex.TextChanged += (_, _) => apply.IsEnabled = TryColor(hex.Text, out _);
            apply.Click += (_, _) =>
            {
                if (TryColor(hex.Text, out Color color))
                {
                    SelectColor(ToHex(color));
                }
            };
            reset.Click += (_, _) => SelectColor(null);
            return content;
        }

        private Button CreateOpacityButton()
        {
            string label = _text("QuickNotesOpacity", "불투명도");
            var button = CreateIconButton("\uE81A", label);
            AttachOptions(button, label, _ => CreateOpacityOptions(label));
            return button;
        }

        private Slider CreateOpacityOptions(string label)
        {
            var slider = new Slider { Minimum = 35, Maximum = 100, StepFrequency = 5, Width = 170, Value = _note.Opacity };
            AutomationProperties.SetName(slider, label);
            void UpdateLabel() => slider.Header = $"{slider.Value / 100:P0}";
            UpdateLabel();
            slider.ValueChanged += (_, _) =>
            {
                _note.Opacity = (int)slider.Value;
                UpdateLabel();
                ApplyOpacity();
                _changed();
            };
            return slider;
        }

        private void AttachOptions(Button button, string title, Func<Action, FrameworkElement> createContent)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = true };
            button.Click += (_, _) =>
            {
                if (flyout.IsOpen)
                {
                    flyout.Hide();
                }
                else
                {
                    var content = new StackPanel { Spacing = 7 };
                    content.Children.Add(new TextBlock { Text = title });
                    content.Children.Add(createContent(flyout.Hide));
                    flyout.Content = content;
                    flyout.ShowAt(button);
                }
            };
        }

        private void ApplyColors()
        {
            if (_closed)
            {
                return;
            }

            bool hasCustomBackground = TryColor(_note.Background, out Color background);
            _background = hasCustomBackground ? background : _root.ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 64, 55, 29) : Color.FromArgb(255, 255, 241, 166);
            _foreground = TryColor(_note.Foreground, out Color foreground)
                ? foreground
                : IsDark(_background) ? Microsoft.UI.Colors.White : Color.FromArgb(255, 38, 32, 27);
            _paper.Color = _background;
            _ink.Color = _foreground;
            ConfigureTextColors(_titleInput);
            ConfigureTextColors(_source);
            ApplyChromeColors();
            if (_note.IsPreview && !_note.IsCollapsed && _root.IsLoaded)
            {
                _preview.Update(_note.Text, ToHex(_background), ToHex(_foreground));
            }
        }

        private void ApplyChromeColors()
        {
            ApplyButtonColors(_foldButton);
            ApplyButtonColors(_closeButton);
            foreach (UIElement child in _toolbar.Children)
            {
                if (child is Control control)
                {
                    ApplyButtonColors(control);
                }
            }
        }

        private void ApplyButtonColors(Control button)
        {
            var surface = new SolidColorBrush(Blend(_background, _foreground, 0.12));
            var hover = new SolidColorBrush(Blend(_background, _foreground, 0.2));
            var pressed = new SolidColorBrush(Blend(_background, _foreground, 0.32));
            var border = new SolidColorBrush(Blend(_background, _foreground, 0.26));
            var ink = new SolidColorBrush(_foreground);
            button.Background = surface;
            button.Foreground = ink;
            button.BorderBrush = border;
            ApplyStateResources(button, "Button", surface, hover, pressed, border, ink);
            if (button is ToggleButton)
            {
                ApplyStateResources(button, "ToggleButton", surface, hover, pressed, border, ink);
                button.Resources["ToggleButtonBackgroundChecked"] = hover;
                button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = pressed;
                button.Resources["ToggleButtonBackgroundCheckedPressed"] = pressed;
                button.Resources["ToggleButtonForegroundChecked"] = ink;
                button.Resources["ToggleButtonForegroundCheckedPointerOver"] = ink;
                button.Resources["ToggleButtonForegroundCheckedPressed"] = ink;
                button.Resources["ToggleButtonBorderBrushChecked"] = border;
                button.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = border;
                button.Resources["ToggleButtonBorderBrushCheckedPressed"] = border;
            }
        }

        private static void ApplyStateResources(Control button, string prefix,
            Brush surface, Brush hover, Brush pressed, Brush border, Brush ink)
        {
            button.Resources[prefix + "Background"] = surface;
            button.Resources[prefix + "BackgroundPointerOver"] = hover;
            button.Resources[prefix + "BackgroundPressed"] = pressed;
            button.Resources[prefix + "BackgroundDisabled"] = surface;
            button.Resources[prefix + "Foreground"] = ink;
            button.Resources[prefix + "ForegroundPointerOver"] = ink;
            button.Resources[prefix + "ForegroundPressed"] = ink;
            button.Resources[prefix + "ForegroundDisabled"] = ink;
            button.Resources[prefix + "BorderBrush"] = border;
            button.Resources[prefix + "BorderBrushPointerOver"] = border;
            button.Resources[prefix + "BorderBrushPressed"] = border;
            button.Resources[prefix + "BorderBrushDisabled"] = border;
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(255,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

        // Fresh brushes on every call so a color change repaints the text right away.
        private void ConfigureTextColors(TextBox box)
        {
            var paper = new SolidColorBrush(_paper.Color);
            var ink = new SolidColorBrush(_ink.Color);
            box.Background = paper;
            box.Foreground = ink;
            box.PlaceholderForeground = ink;
            box.Resources["TextControlBackgroundPointerOver"] = paper;
            box.Resources["TextControlBackgroundFocused"] = paper;
            box.Resources["TextControlForegroundPointerOver"] = ink;
            box.Resources["TextControlForegroundFocused"] = ink;
            box.Resources["TextControlPlaceholderForegroundPointerOver"] = ink;
            box.Resources["TextControlPlaceholderForegroundFocused"] = ink;
        }

        private static bool IsDark(Color color) => 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B < 128;

        private void UpdateTitle()
        {
            Title = _displayTitle();
            _foldedTitle.Text = Title;
        }

        private void ApplyMode()
        {
            bool live = _note.IsPreview;
            _liveButton.IsChecked = live;
            LabelControl(_liveButton, live ? _text("QuickNotesSource", "원본 보기") : _text("QuickNotesLive", "라이브 마크다운 보기"));
            _source.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            _preview.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            if (live && !_note.IsCollapsed && _root.XamlRoot != null)
            {
                _preview.Update(_note.Text, ToHex(_background), ToHex(_foreground));
            }
        }

        private void OnPreviewFailed()
        {
            if (_closed)
            {
                return;
            }

            _note.IsPreview = false;
            _previewError = _text("QuickNotesPreviewError", "마크다운을 표시할 수 없습니다.");
            ApplyMode();
            UpdateStatus();
            _changed();
        }

        private void UpdateStatus()
        {
            _status.Text = _storageError.Length > 0 ? _storageError : _previewError;
            _status.Visibility = !_note.IsCollapsed && _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(_status, _status.Visibility == Visibility.Visible ? _status.Text : null);
        }

        private void ApplyFold()
        {
            bool folded = _note.IsCollapsed;
            _root.RowSpacing = folded ? 0 : 4;
            _foldedTitle.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
            _titleInput.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            _toolbar.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            _body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            _foldButton.Content = Icon(folded ? "\uE70D" : "\uE70E");
            LabelControl(_foldButton, folded ? _text("QuickNotesExpand", "펼치기") : _text("QuickNotesFold", "접기"));
            UpdateStatus();
        }

        private void ToggleFold()
        {
            _applyingPlacement = true;
            try
            {
                _note.IsCollapsed = !_note.IsCollapsed;
                ApplyFold();
                SizeInt32 minimum = ApplySizeLimits();
                var position = AppWindow.Position;
                var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                int width = Math.Max(AppWindow.Size.Width, minimum.Width);
                int height = _note.IsCollapsed ? minimum.Height :
                    Math.Clamp(_note.Height, minimum.Height, Math.Max(minimum.Height, workArea.Height));
                AppWindow.MoveAndResize(FitToWorkArea(workArea, position.X, position.Y, width, height));
                CapturePlacement();
                ApplyMode();
            }
            finally
            {
                _applyingPlacement = false;
            }
            _changed();
        }

        private void PlaceWindow(int openCount)
        {
            var previous = (_note.X, _note.Y, _note.Width, _note.Height);
            _scale = ReadScale();
            var saved = new PointInt32(_note.X ?? 0, _note.Y ?? 0);
            DisplayArea? display = _note.X.HasValue && _note.Y.HasValue
                ? DisplayArea.GetFromPoint(saved, DisplayAreaFallback.None) : null;
            RectInt32 area = (display ?? DisplayArea.Primary).WorkArea;
            SizeInt32 minimum = ApplySizeLimits();
            int width = Math.Clamp(_note.Width > 0 ? _note.Width : Pixels(350), minimum.Width, Math.Max(minimum.Width, area.Width));
            int height = _note.IsCollapsed ? minimum.Height :
                Math.Clamp(_note.Height > 0 ? _note.Height : Pixels(295), minimum.Height, Math.Max(minimum.Height, area.Height));
            int inset = Pixels(16 + openCount % 8 * 22);
            int x = display != null ? saved.X : area.X + area.Width - width - inset;
            int y = display != null ? saved.Y : area.Y + inset;
            _note.Width = width;
            if (!_note.IsCollapsed)
            {
                _note.Height = height;
            }
            AppWindow.MoveAndResize(FitToWorkArea(area, x, y, width, height));
            RefreshSizeLimits();
            CapturePlacement();
            if (previous != (_note.X, _note.Y, _note.Width, _note.Height))
            {
                _changed();
            }
        }

        private static RectInt32 FitToWorkArea(RectInt32 area, int x, int y, int width, int height) => new(
            Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width)),
            Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height)), width, height);

        private SizeInt32 ApplySizeLimits()
        {
            _presenter.IsResizable = !_note.IsCollapsed;
            // The presenter limits include the window frame, while flyouts fit in the client area.
            SizeInt32 window = AppWindow.Size;
            SizeInt32 client = AppWindow.ClientSize;
            int frameWidth = client.Width > 0 ? Math.Max(0, window.Width - client.Width) : 0;
            int frameHeight = client.Height > 0 ? Math.Max(0, window.Height - client.Height) : 0;
            var minimum = new SizeInt32(
                (int)Math.Ceiling(MinClientWidth * _scale) + frameWidth,
                _note.IsCollapsed ? Pixels(FoldedHeight) : (int)Math.Ceiling(MinClientHeight * _scale) + frameHeight);
            _presenter.PreferredMinimumWidth = minimum.Width;
            _presenter.PreferredMinimumHeight = minimum.Height;
            return minimum;
        }

        private void RefreshSizeLimits()
        {
            _scale = ReadScale();
            SizeInt32 minimum = ApplySizeLimits();
            SizeInt32 size = AppWindow.Size;
            int width = Math.Max(size.Width, minimum.Width);
            int height = _note.IsCollapsed ? minimum.Height : Math.Max(size.Height, minimum.Height);
            if (width != size.Width || height != size.Height)
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                var position = AppWindow.Position;
                AppWindow.MoveAndResize(FitToWorkArea(area, position.X, position.Y, width, height));
            }
        }

        private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_closed || _applyingPlacement || (!args.DidPositionChange && !args.DidSizeChange))
            {
                return;
            }

            if (_scale != ReadScale())
            {
                RefreshSizeLimits();
            }
            CapturePlacement();
            _changed();
        }

        private void CapturePlacement()
        {
            _note.X = AppWindow.Position.X;
            _note.Y = AppWindow.Position.Y;
            if (!_note.IsCollapsed)
            {
                _note.Width = AppWindow.Size.Width;
                _note.Height = AppWindow.Size.Height;
            }
        }

        private double ReadScale() => Math.Max(96u, GetDpiForWindow(_hwnd)) / 96.0;

        private int Pixels(int dips) => (int)Math.Round(dips * _scale);

        private void ApplyOpacity()
        {
            const int extendedStyle = -20;
            const int layered = 0x80000;
            int style = GetWindowLong(_hwnd, extendedStyle);
            if (_note.Opacity == 100)
            {
                if ((style & layered) != 0)
                {
                    SetLayeredWindowAttributes(_hwnd, 0, 255, 2);
                    SetWindowLong(_hwnd, extendedStyle, style & ~layered);
                }
                return;
            }
            if ((style & layered) == 0)
            {
                SetWindowLong(_hwnd, extendedStyle, style | layered);
            }
            if (!SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(_note.Opacity * 255.0 / 100), 2) &&
                (style & layered) == 0)
            {
                SetWindowLong(_hwnd, extendedStyle, style);
            }
        }

        private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static bool TryColor(string? text, out Color color)
        {
            if (text?.Length == 7 && text[0] == '#' &&
                uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            {
                color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                return true;
            }
            color = default;
            return false;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out PointInt32 point);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    }
}
