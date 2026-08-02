using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerPathSegmentClickedEventArgs : EventArgs
    {
        public ExplorerPathSegmentClickedEventArgs(ExplorerBreadcrumbSegment segment)
        {
            Segment = segment;
        }

        public ExplorerBreadcrumbSegment Segment { get; }
    }

    /// <summary>
    /// 탐색기 경로 표시. 경로가 길어지면 글자 크기를 줄여가며 최대 두 줄로 감싸 보여준다.
    /// 각 세그먼트를 클릭하면 <see cref="SegmentClicked"/> 이벤트로 이동 요청을 전달한다.
    /// </summary>
    public sealed partial class ExplorerPathBar : UserControl
    {
        private const double MaxFontSize = 12.0;
        private const double MinFontSize = 8.0;
        private const double FontStep = 0.5;
        private const double RowSpacing = 2.0;
        private const double TokenSpacing = 5.0;
        private const double SpaceAfterSeparator = 4.0; // 구분자 뒤에 한 칸(픽셀) 추가
        private const double HorizontalPadding = 2.0;
        private const int MaxRows = 2;
        private const string SeparatorText = "›";
        private static readonly Thickness SegmentPadding = new(4, 2, 4, 2);
        private static readonly Thickness SeparatorPadding = new(0, 2, 0, 2);
        private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        private IReadOnlyList<ExplorerBreadcrumbSegment>? _segments;
        private bool _rendering;

        public ExplorerPathBar()
        {
            InitializeComponent();
            SizeChanged += OnPathBarSizeChanged;
            Loaded += OnPathBarLoaded;
        }

        public IEnumerable<ExplorerBreadcrumbSegment>? ItemsSource
        {
            get => _segments;
            set
            {
                _segments = value?.ToList();
                Render();
            }
        }

        public event EventHandler<ExplorerPathSegmentClickedEventArgs>? SegmentClicked;

        private void OnPathBarLoaded(object sender, RoutedEventArgs e) => Render();

        private void OnPathBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0)
            {
                Render();
            }
        }

        private void Render()
        {
            if (_rendering)
            {
                return;
            }

            _rendering = true;
            try
            {
                PathCanvas.Children.Clear();
                if (_segments is not { Count: > 0 })
                {
                    PathCanvas.Height = 0;
                    return;
                }

                double availableWidth = Math.Max(ActualWidth - HorizontalPadding * 2, 80);

                for (double fontSize = MaxFontSize; fontSize >= MinFontSize - 0.001; fontSize -= FontStep)
                {
                    List<PathToken> tokens = BuildTokens(fontSize, availableWidth);
                    if (Wrap(tokens, availableWidth) <= MaxRows)
                    {
                        PlaceTokens(tokens);
                        return;
                    }
                }

                // 아주 긴 경로: 최소 글자 크기에서도 두 줄을 넘으면 앞쪽부터 생략하고
                // 맨 앞의 생략 부호(…)를 클릭하면 숨겨진 앞쪽 경로가 펼쳐진다.
                List<PathToken> minTokens = BuildTokens(MinFontSize, availableWidth);
                if (Wrap(minTokens, availableWidth) > MaxRows)
                {
                    minTokens = TruncateKeepingTail(minTokens, availableWidth);
                }

                PlaceTokens(minTokens);
            }
            finally
            {
                _rendering = false;
            }
        }

        private List<PathToken> BuildTokens(double fontSize, double availableWidth)
        {
            var tokens = new List<PathToken>(_segments!.Count);
            for (int i = 0; i < _segments.Count; i++)
            {
                ExplorerBreadcrumbSegment segment = _segments[i];
                var elements = new List<FrameworkElement>(2);
                double width = 0;

                if (i > 0)
                {
                    var separator = new TextBlock
                    {
                        Text = SeparatorText,
                        FontSize = fontSize,
                        Style = (Style)Resources["PathSeparatorStyle"],
                        Padding = SeparatorPadding
                    };
                    separator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    elements.Add(separator);
                    width += separator.DesiredSize.Width + TokenSpacing + SpaceAfterSeparator;
                }

                var segmentBlock = new TextBlock
                {
                    Text = segment.Name,
                    FontSize = fontSize,
                    MaxWidth = availableWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    IsHitTestVisible = false
                };
                var segmentBorder = new Border
                {
                    Child = segmentBlock,
                    Padding = SegmentPadding,
                    CornerRadius = new CornerRadius(3),
                    MaxWidth = availableWidth,
                    Background = TransparentBrush,
                    Tag = segment
                };
                ToolTipService.SetToolTip(segmentBorder, segment.Path);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(segmentBorder, segment.Name);
                segmentBorder.Tapped += OnSegmentTapped;
                segmentBorder.PointerEntered += OnSegmentPointerEntered;
                segmentBorder.PointerExited += OnSegmentPointerExited;
                segmentBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                elements.Add(segmentBorder);
                width += segmentBorder.DesiredSize.Width;

                tokens.Add(new PathToken(elements, width));
            }

            return tokens;
        }

        private static int Wrap(List<PathToken> tokens, double availableWidth)
        {
            double x = 0;
            int row = 0;
            foreach (PathToken token in tokens)
            {
                if (x > 0 && x + token.Width > availableWidth)
                {
                    row++;
                    x = 0;
                }

                token.Row = row;
                token.X = x;
                x += token.Width + TokenSpacing;
            }

            return row + 1;
        }

        private List<PathToken> TruncateKeepingTail(List<PathToken> tokens, double availableWidth)
        {
            var ellipsisToken = CreateEllipsisToken(availableWidth);
            var best = new List<PathToken> { ellipsisToken };
            int bestTailCount = 0;

            // 뒤(최근 경로)부터 최대한 많은 토큰을 남기되, 맨 앞에 생략 부호(…)를 둬도
            // 두 줄 안에 들어오는 가장 큰 꼬리(tail)를 찾는다.
            for (int tailCount = 0; tailCount <= tokens.Count; tailCount++)
            {
                var candidate = new List<PathToken>(tailCount + 1) { ellipsisToken };
                for (int i = tokens.Count - tailCount; i < tokens.Count; i++)
                {
                    candidate.Add(tokens[i]);
                }

                if (Wrap(candidate, availableWidth) > MaxRows)
                {
                    break;
                }

                best = candidate;
                bestTailCount = tailCount;
            }

            Wrap(best, availableWidth);

            int hiddenCount = tokens.Count - bestTailCount;
            if (hiddenCount > 0)
            {
                var hidden = new List<ExplorerBreadcrumbSegment>(hiddenCount);
                for (int i = 0; i < hiddenCount; i++)
                {
                    if (tokens[i].Elements[^1].Tag is ExplorerBreadcrumbSegment segment)
                    {
                        hidden.Add(segment);
                    }
                }

                ellipsisToken.Elements[0].Tag = hidden;
            }

            return best;
        }

        private PathToken CreateEllipsisToken(double availableWidth)
        {
            var ellipsisText = new TextBlock
            {
                Text = "…",
                FontSize = MinFontSize,
                IsHitTestVisible = false
            };
            var ellipsisBorder = new Border
            {
                Child = ellipsisText,
                Padding = SegmentPadding,
                CornerRadius = new CornerRadius(3),
                MaxWidth = availableWidth,
                Background = TransparentBrush
            };
            ellipsisBorder.Tapped += OnEllipsisTapped;
            ellipsisBorder.PointerEntered += OnSegmentPointerEntered;
            ellipsisBorder.PointerExited += OnSegmentPointerExited;
            ellipsisBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new PathToken(new List<FrameworkElement> { ellipsisBorder }, ellipsisBorder.DesiredSize.Width);
        }

        private void PlaceTokens(List<PathToken> tokens)
        {
            double rowHeight = 0;
            foreach (PathToken token in tokens)
            {
                foreach (FrameworkElement element in token.Elements)
                {
                    rowHeight = Math.Max(rowHeight, element.DesiredSize.Height);
                }
            }

            double height = 0;
            foreach (PathToken token in tokens)
            {
                double top = token.Row * (rowHeight + RowSpacing);
                double x = token.X;
                foreach (FrameworkElement element in token.Elements)
                {
                    Canvas.SetLeft(element, x);
                    Canvas.SetTop(element, top);
                    PathCanvas.Children.Add(element);
                    bool isSeparator = token.Elements.Count > 1 && element == token.Elements[0];
                    x += element.DesiredSize.Width + TokenSpacing + (isSeparator ? SpaceAfterSeparator : 0);
                }

                height = Math.Max(height, top + rowHeight);
            }

            PathCanvas.Height = height;
        }

        private void OnSegmentTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { Tag: ExplorerBreadcrumbSegment segment })
            {
                SegmentClicked?.Invoke(this, new ExplorerPathSegmentClickedEventArgs(segment));
            }
        }

        private void OnEllipsisTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border { Tag: List<ExplorerBreadcrumbSegment> hidden })
            {
                return;
            }

            var flyout = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
            };
            foreach (ExplorerBreadcrumbSegment segment in hidden)
            {
                var item = new MenuFlyoutItem
                {
                    Text = segment.Name,
                    Tag = segment
                };
                ToolTipService.SetToolTip(item, segment.Path);
                item.Click += OnHiddenSegmentClick;
                flyout.Items.Add(item);
            }

            flyout.ShowAt((FrameworkElement)sender);
        }

        private void OnHiddenSegmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: ExplorerBreadcrumbSegment segment })
            {
                SegmentClicked?.Invoke(this, new ExplorerPathSegmentClickedEventArgs(segment));
            }
        }

        private void OnSegmentPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = GetHoverBackgroundBrush(border);
            }
        }

        private void OnSegmentPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = TransparentBrush;
            }
        }

        private static Brush GetHoverBackgroundBrush(FrameworkElement element)
        {
            // Application.Current.Resources 조회는 현재 테마를 따르지 않아 다크모드에서도
            // 라이트용 브러시(검정 반투명)가 반환될 수 있으므로, 테마를 직접 판단한다.
            bool isDark = element.ActualTheme == ElementTheme.Dark;
            byte alpha = isDark ? (byte)0x40 : (byte)0x1A; // 다크: 흰색 25%, 라이트: 검정 10%
            byte channel = isDark ? (byte)0xFF : (byte)0x00;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, channel, channel, channel));
        }

        private sealed class PathToken
        {
            public PathToken(List<FrameworkElement> elements, double width)
            {
                Elements = elements;
                Width = width;
            }

            public List<FrameworkElement> Elements { get; }
            public double Width { get; }
            public int Row { get; set; }
            public double X { get; set; }
        }
    }
}
