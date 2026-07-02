using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public static class TextBlockExtensions
    {
        public static readonly DependencyProperty GitHistoryItemProperty =
            DependencyProperty.RegisterAttached(
                "GitHistoryItem",
                typeof(GitHistoryItem),
                typeof(TextBlockExtensions),
                new PropertyMetadata(null, OnGitHistoryItemChanged));

        public static GitHistoryItem GetGitHistoryItem(DependencyObject obj) =>
            (GitHistoryItem)obj.GetValue(GitHistoryItemProperty);

        public static void SetGitHistoryItem(DependencyObject obj, GitHistoryItem value) =>
            obj.SetValue(GitHistoryItemProperty, value);

        private static void OnGitHistoryItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Grid grid)
            {
                // Always clean up the previous handler first
                grid.ActualThemeChanged -= OnGridThemeChanged;

                if (e.NewValue is GitHistoryItem item)
                {
                    PopulateGrid(grid, item);
                    grid.ActualThemeChanged += OnGridThemeChanged;
                }
                else
                {
                    grid.Children.Clear();
                    grid.ColumnDefinitions.Clear();
                }
            }
        }

        private static void OnGridThemeChanged(FrameworkElement sender, object args)
        {
            if (sender is Grid grid)
            {
                var item = GetGitHistoryItem(grid);
                if (item != null)
                {
                    PopulateGrid(grid, item);
                }
            }
        }

        private static void PopulateGrid(Grid grid, GitHistoryItem item)
        {
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            string text = item.DisplayText;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Step 1: Extract graph part
            int index = 0;
            while (index < text.Length && (IsGitGraphCharacter(text[index]) || text[index] == '\u2191' || text[index] == ' '))
            {
                index++;
            }

            string graphStr = text.Substring(0, index);
            string remaining = text.Substring(index);

            // Set up Grid columns:
            // Column 0: Canvas for git graph
            // Column 1: TextBlock for commit info
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            bool isDark = grid.ActualTheme == ElementTheme.Dark;

            // Draw the graph
            var canvas = new Canvas();
            DrawGraph(canvas, graphStr, isDark);
            Grid.SetColumn(canvas, 0);
            grid.Children.Add(canvas);

            // Populate the text
            var textBlock = new TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            PopulateInlines(textBlock, remaining, isDark);
            
            // Set ToolTip
            ToolTipService.SetToolTip(grid, text);

            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);
        }

        private static void DrawGraph(Canvas canvas, string graphStr, bool isDark)
        {
            canvas.Children.Clear();
            if (string.IsNullOrEmpty(graphStr)) return;

            // Values tuned for pixel-perfect connected alignment (matching ListViewItem height 18)
            double cellWidth = 10.0;
            double itemHeight = 18.0;

            // Overlap Y-coordinates to avoid subpixel antialiasing gaps between list items
            double yTop = -1.0;
            double yBottom = 19.0;

            canvas.Width = graphStr.Length * cellWidth;
            canvas.Height = itemHeight;

            // Curated colorful palette with Blue at index 0 and Red at index 1 & 2 to unify branch colors
            Color[] graphLineColors = isDark ? new Color[]
            {
                Color.FromArgb(255, 66, 165, 245),  // Blue (index 0 - Main branch)
                Color.FromArgb(255, 239, 83, 80),   // Red (index 1)
                Color.FromArgb(255, 239, 83, 80),   // Red (index 2 - Unified with index 1 to prevent branch color shifts)
                Color.FromArgb(255, 255, 202, 40),  // Yellow
                Color.FromArgb(255, 171, 71, 188),  // Purple
                Color.FromArgb(255, 38, 166, 154)   // Teal
            } : new Color[]
            {
                Color.FromArgb(255, 25, 118, 210),  // Blue (index 0 - Main branch)
                Color.FromArgb(255, 211, 47, 47),   // Red (index 1)
                Color.FromArgb(255, 211, 47, 47),   // Red (index 2 - Unified with index 1 to prevent branch color shifts)
                Color.FromArgb(255, 230, 124, 0),   // Orange
                Color.FromArgb(255, 123, 31, 162),  // Purple
                Color.FromArgb(255, 0, 121, 107)    // Teal
            };

            Color unpushedColor = isDark ? Color.FromArgb(255, 255, 215, 0) : Color.FromArgb(255, 184, 134, 11);
            var unpushedBrush = new SolidColorBrush(unpushedColor);
            
            // Resolve background color dynamically from resources if possible to overlay node dots perfectly
            var bgBrush = Application.Current.Resources["SidebarBackgroundBrush"] as Brush 
                ?? new SolidColorBrush(isDark ? Color.FromArgb(255, 30, 30, 30) : Color.FromArgb(255, 250, 250, 250));

            for (int i = 0; i < graphStr.Length; i++)
            {
                char c = graphStr[i];
                double x = i * cellWidth + cellWidth / 2;
                var color = graphLineColors[i % graphLineColors.Length];
                var brush = new SolidColorBrush(color);

                if (c == '|')
                {
                    canvas.Children.Add(new Line
                    {
                        X1 = x,
                        Y1 = yTop,
                        X2 = x,
                        Y2 = yBottom,
                        Stroke = brush,
                        StrokeThickness = 2.0
                    });
                }
                else if (c == '*')
                {
                    // Draw vertical branch line behind the node
                    canvas.Children.Add(new Line
                    {
                        X1 = x,
                        Y1 = yTop,
                        X2 = x,
                        Y2 = yBottom,
                        Stroke = brush,
                        StrokeThickness = 2.0
                    });

                    // Draw node circle with background mask and colored border
                    canvas.Children.Add(new Ellipse
                    {
                        Width = 7.0,
                        Height = 7.0,
                        Margin = new Thickness(x - 3.5, itemHeight / 2.0 - 3.5, 0, 0),
                        Stroke = brush,
                        Fill = bgBrush,
                        StrokeThickness = 2.0
                    });
                }
                else if (c == '/')
                {
                    canvas.Children.Add(new Line
                    {
                        X1 = x + cellWidth,
                        Y1 = yTop,
                        X2 = x,
                        Y2 = yBottom,
                        Stroke = brush,
                        StrokeThickness = 2.0
                    });
                }
                else if (c == '\\')
                {
                    canvas.Children.Add(new Line
                    {
                        X1 = x,
                        Y1 = yTop,
                        X2 = x + cellWidth,
                        Y2 = yBottom,
                        Stroke = brush,
                        StrokeThickness = 2.0
                    });
                }
                else if (c == '_' || c == '-')
                {
                    canvas.Children.Add(new Line
                    {
                        X1 = x - cellWidth / 2.0,
                        X2 = x + cellWidth / 2.0,
                        Y1 = itemHeight / 2.0,
                        Y2 = itemHeight / 2.0,
                        Stroke = brush,
                        StrokeThickness = 2.0
                    });
                }
                else if (c == '\u2191') // Unpushed arrow
                {
                    // Draw background vertical line
                    canvas.Children.Add(new Line
                    {
                        X1 = x,
                        Y1 = yTop,
                        X2 = x,
                        Y2 = yBottom,
                        Stroke = unpushedBrush,
                        StrokeThickness = 2.0
                    });

                    // Draw golden up arrowhead triangle
                    var path = new Path();
                    var geometry = new PathGeometry();
                    var figure = new PathFigure { StartPoint = new Point(x - 3.0, 5.0) };
                    figure.Segments.Add(new LineSegment { Point = new Point(x, 1.0) });
                    figure.Segments.Add(new LineSegment { Point = new Point(x + 3.0, 5.0) });
                    figure.IsClosed = true;
                    geometry.Figures.Add(figure);
                    path.Data = geometry;
                    path.Fill = unpushedBrush;
                    canvas.Children.Add(path);
                }
            }
        }

        private static bool IsMainBranch(string branchName)
        {
            if (string.IsNullOrEmpty(branchName)) return false;
            string name = branchName.Trim();
            return name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("master", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("origin/main", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("origin/master", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("/main", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("/master", StringComparison.OrdinalIgnoreCase);
        }

        private static void PopulateInlines(TextBlock textBlock, string remaining, bool isDark)
        {
            textBlock.Inlines.Clear();
            if (string.IsNullOrEmpty(remaining)) return;

            // Curated, beautiful HSL-tailored colors for premium look
            Color defaultColor = isDark ? Color.FromArgb(255, 220, 224, 232) : Color.FromArgb(255, 31, 41, 55);       // High-contrast content text
            Color parenColor = isDark ? Color.FromArgb(255, 140, 148, 160) : Color.FromArgb(255, 110, 118, 128);       // Muted gray for parentheses/delimiters
            Color headColor = isDark ? Color.FromArgb(255, 86, 180, 230) : Color.FromArgb(255, 0, 110, 190);           // Sky blue vs Slate blue
            
            // Explicit premium blue color for main/master branches
            Color mainBranchColor = isDark ? Color.FromArgb(255, 100, 180, 255) : Color.FromArgb(255, 26, 115, 232);  // Bright sky blue vs Royal blue
            
            Color localBranchColor = isDark ? Color.FromArgb(255, 78, 201, 176) : Color.FromArgb(255, 14, 122, 83);     // Teal vs Forest green
            Color remoteBranchColor = isDark ? Color.FromArgb(255, 240, 110, 110) : Color.FromArgb(255, 200, 30, 40);   // Soft red vs Crimson red
            Color tagColor = isDark ? Color.FromArgb(255, 230, 190, 110) : Color.FromArgb(255, 150, 100, 0);          // Golden-yellow vs Deep yellow-brown
            Color dateColor = isDark ? Color.FromArgb(255, 130, 140, 150) : Color.FromArgb(255, 120, 128, 136);       // Cool slate gray for timestamps

            var defaultBrush = new SolidColorBrush(defaultColor);
            var parenBrush = new SolidColorBrush(parenColor);
            var headBrush = new SolidColorBrush(headColor);
            var mainBranchBrush = new SolidColorBrush(mainBranchColor);
            var localBranchBrush = new SolidColorBrush(localBranchColor);
            var remoteBranchBrush = new SolidColorBrush(remoteBranchColor);
            var tagBrush = new SolidColorBrush(tagColor);
            var dateBrush = new SolidColorBrush(dateColor);

            if (remaining.StartsWith(" "))
            {
                textBlock.Inlines.Add(new Run { Text = " ", Foreground = defaultBrush });
                remaining = remaining.TrimStart();
            }

            if (remaining.StartsWith("("))
            {
                int closeParenIndex = remaining.IndexOf(')');
                if (closeParenIndex > 0)
                {
                    textBlock.Inlines.Add(new Run { Text = "(", Foreground = parenBrush });

                    string decorationContent = remaining.Substring(1, closeParenIndex - 1);
                    string[] refs = decorationContent.Split(new[] { ", " }, StringSplitOptions.None);
                    for (int i = 0; i < refs.Length; i++)
                    {
                        if (i > 0)
                        {
                            textBlock.Inlines.Add(new Run { Text = ", ", Foreground = parenBrush });
                        }

                        string refItem = refs[i];
                        if (refItem.Contains("->"))
                        {
                            int arrowIdx = refItem.IndexOf("->");
                            string left = refItem.Substring(0, arrowIdx).Trim();
                            string right = refItem.Substring(arrowIdx + 2).Trim();

                            if (left == "HEAD")
                            {
                                textBlock.Inlines.Add(new Run { Text = left, Foreground = headBrush, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                            }
                            else
                            {
                                textBlock.Inlines.Add(new Run { Text = left, Foreground = IsMainBranch(left) ? mainBranchBrush : localBranchBrush });
                            }

                            textBlock.Inlines.Add(new Run { Text = " -> ", Foreground = parenBrush });

                            if (right.StartsWith("origin/") || right.Contains("/"))
                            {
                                textBlock.Inlines.Add(new Run { Text = right, Foreground = IsMainBranch(right) ? mainBranchBrush : remoteBranchBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                            }
                            else
                            {
                                textBlock.Inlines.Add(new Run { Text = right, Foreground = IsMainBranch(right) ? mainBranchBrush : localBranchBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                            }
                        }
                        else if (refItem.StartsWith("tag: "))
                        {
                            textBlock.Inlines.Add(new Run { Text = "tag: ", Foreground = parenBrush });
                            textBlock.Inlines.Add(new Run { Text = refItem.Substring(5), Foreground = tagBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        }
                        else if (refItem.StartsWith("refs/tags/"))
                        {
                            textBlock.Inlines.Add(new Run { Text = "tag: ", Foreground = parenBrush });
                            textBlock.Inlines.Add(new Run { Text = refItem.Substring(10), Foreground = tagBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        }
                        else if (refItem == "HEAD")
                        {
                            textBlock.Inlines.Add(new Run { Text = refItem, Foreground = headBrush, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
                        }
                        else if (refItem.StartsWith("origin/") || refItem.Contains("/"))
                        {
                            textBlock.Inlines.Add(new Run { Text = refItem, Foreground = IsMainBranch(refItem) ? mainBranchBrush : remoteBranchBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        }
                        else
                        {
                            textBlock.Inlines.Add(new Run { Text = refItem, Foreground = IsMainBranch(refItem) ? mainBranchBrush : localBranchBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        }
                    }

                    textBlock.Inlines.Add(new Run { Text = ")", Foreground = parenBrush });
                    remaining = remaining.Substring(closeParenIndex + 1);
                }
            }

            if (!string.IsNullOrEmpty(remaining))
            {
                int lastDashIndex = remaining.LastIndexOf(" - ");
                if (lastDashIndex >= 0)
                {
                    string message = remaining.Substring(0, lastDashIndex);
                    string date = remaining.Substring(lastDashIndex);

                    textBlock.Inlines.Add(new Run { Text = message, Foreground = defaultBrush });
                    textBlock.Inlines.Add(new Run { Text = date, Foreground = dateBrush });
                }
                else
                {
                    textBlock.Inlines.Add(new Run { Text = remaining, Foreground = defaultBrush });
                }
            }
        }

        private static bool IsGitGraphCharacter(char value)
        {
            return value is ' ' or '*' or '|' or '/' or '\\' or '_' or '-';
        }
    }
}
