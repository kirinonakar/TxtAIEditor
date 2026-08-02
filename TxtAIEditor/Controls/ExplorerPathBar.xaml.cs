using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        private const double HorizontalPadding = 2.0;
        private const int MaxRows = 2;
        private const string SeparatorText = "› "; // 구분자 뒤에 한 칸 공백

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

                // 아주 긴 경로: 최소 글자 크기에서도 두 줄을 넘으면 남은 부분을 생략 부호로 표시한다.
                List<PathToken> minTokens = BuildTokens(MinFontSize, availableWidth);
                if (Wrap(minTokens, availableWidth) > MaxRows)
                {
                    minTokens = TruncateToTwoRows(minTokens, availableWidth);
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
                var elements = new List<TextBlock>(2);
                double width = 0;

                if (i > 0)
                {
                    var separator = new TextBlock
                    {
                        Text = SeparatorText,
                        FontSize = fontSize,
                        Style = (Style)Resources["PathSeparatorStyle"]
                    };
                    separator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    elements.Add(separator);
                    width += separator.DesiredSize.Width + TokenSpacing;
                }

                var segmentBlock = new TextBlock
                {
                    Text = segment.Name,
                    FontSize = fontSize,
                    MaxWidth = availableWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Tag = segment
                };
                ToolTipService.SetToolTip(segmentBlock, segment.Path);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(segmentBlock, segment.Name);
                segmentBlock.Tapped += OnSegmentTapped;
                segmentBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                elements.Add(segmentBlock);
                width += segmentBlock.DesiredSize.Width;

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

        private List<PathToken> TruncateToTwoRows(List<PathToken> tokens, double availableWidth)
        {
            var kept = new List<PathToken>(tokens.Count);
            double x = 0;
            int row = 0;
            int lastRow = 0;
            double lastX = 0;
            foreach (PathToken token in tokens)
            {
                if (x > 0 && x + token.Width > availableWidth)
                {
                    row++;
                    x = 0;
                }

                if (row >= MaxRows)
                {
                    break;
                }

                token.Row = row;
                token.X = x;
                kept.Add(token);
                x += token.Width + TokenSpacing;
                lastRow = row;
                lastX = x;
            }

            if (kept.Count < tokens.Count)
            {
                var ellipsis = new TextBlock
                {
                    Text = "…",
                    FontSize = MinFontSize,
                    Style = (Style)Resources["PathSeparatorStyle"]
                };
                ellipsis.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                kept.Add(new PathToken(new List<TextBlock> { ellipsis }, ellipsis.DesiredSize.Width)
                {
                    Row = lastRow,
                    X = lastX
                });
            }

            return kept;
        }

        private void PlaceTokens(List<PathToken> tokens)
        {
            double rowHeight = 0;
            foreach (PathToken token in tokens)
            {
                foreach (TextBlock element in token.Elements)
                {
                    rowHeight = Math.Max(rowHeight, element.DesiredSize.Height);
                }
            }

            double height = 0;
            foreach (PathToken token in tokens)
            {
                double top = token.Row * (rowHeight + RowSpacing);
                double x = token.X;
                foreach (TextBlock element in token.Elements)
                {
                    Canvas.SetLeft(element, x);
                    Canvas.SetTop(element, top);
                    PathCanvas.Children.Add(element);
                    x += element.DesiredSize.Width + TokenSpacing;
                }

                height = Math.Max(height, top + rowHeight);
            }

            PathCanvas.Height = height;
        }

        private void OnSegmentTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is TextBlock { Tag: ExplorerBreadcrumbSegment segment })
            {
                SegmentClicked?.Invoke(this, new ExplorerPathSegmentClickedEventArgs(segment));
            }
        }

        private sealed class PathToken
        {
            public PathToken(List<TextBlock> elements, double width)
            {
                Elements = elements;
                Width = width;
            }

            public List<TextBlock> Elements { get; }
            public double Width { get; }
            public int Row { get; set; }
            public double X { get; set; }
        }
    }
}
