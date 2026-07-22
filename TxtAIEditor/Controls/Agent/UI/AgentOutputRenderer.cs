using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentOutputRenderer
    {
        private readonly RichTextBlock _outputText;
        private readonly FrameworkElement _resourceOwner;
        private readonly Action<string> _explicitSelectionChanged;
        private readonly List<string> _renderedLines = new List<string>();
        private bool _lineBlocksMatchRenderedLines = true;
        private Func<string, string, string>? _getString;

        public AgentOutputRenderer(
            RichTextBlock outputText,
            FrameworkElement resourceOwner,
            Action<string> explicitSelectionChanged)
        {
            _outputText = outputText;
            _resourceOwner = resourceOwner;
            _explicitSelectionChanged = explicitSelectionChanged;
        }

        public bool HideHtmlCodeBlocks { get; set; }

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public void UpdateRichText(string rawText)
        {
            string normalized = (rawText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            List<string> displayLines = new List<string>();
            if (HideHtmlCodeBlocks)
            {
                bool inHtmlBlock = false;
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (!inHtmlBlock)
                    {
                        if (trimmed.StartsWith("```html", StringComparison.OrdinalIgnoreCase))
                        {
                            inHtmlBlock = true;
                            displayLines.Add(
                                _getString?.Invoke(
                                    "AgentHtmlCodeBlockHidden",
                                    "[HTML 코드 블록 숨겨짐 (상세 출력 비활성화)]") ??
                                "[HTML 코드 블록 숨겨짐 (상세 출력 비활성화)]");
                        }
                        else
                        {
                            displayLines.Add(line);
                        }
                    }
                    else
                    {
                        if (trimmed.StartsWith("```", StringComparison.Ordinal))
                        {
                            inHtmlBlock = false;
                        }
                    }
                }
            }
            else
            {
                displayLines.AddRange(lines);
            }

            _renderedLines.Clear();
            _renderedLines.AddRange(displayLines);
            _lineBlocksMatchRenderedLines = !RequiresGroupedRendering(displayLines, 0);

            RenderDisplayLinesToBlocks(displayLines);
        }

        public void AppendText(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] parts = normalized.Split('\n');

            EnsureRenderedLineExists();

            int firstChangedLineIndex = _renderedLines.Count - 1;
            int lastIndex = _renderedLines.Count - 1;
            _renderedLines[lastIndex] = _renderedLines[lastIndex] + parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                _renderedLines.Add(parts[i]);
            }

            if (HideHtmlCodeBlocks ||
                !_lineBlocksMatchRenderedLines ||
                RequiresGroupedRendering(_renderedLines, Math.Max(0, firstChangedLineIndex - 1)))
            {
                UpdateRichText(string.Join("\n", _renderedLines));
                return;
            }

            PatchRenderedRegularLines(firstChangedLineIndex);
        }

        public bool TrySetLastLine(string line)
        {
            if (_renderedLines.Count == 0)
            {
                return false;
            }

            SetRenderedLine(_renderedLines.Count - 1, line);
            return true;
        }

        private void RenderDisplayLinesToBlocks(List<string> displayLines)
        {
            _outputText.Blocks.Clear();
            RenderLinesToBlocksInternal(displayLines);
        }

        private void PatchRenderedRegularLines(int firstChangedLineIndex)
        {
            firstChangedLineIndex = Math.Max(0, firstChangedLineIndex);
            if (!_lineBlocksMatchRenderedLines ||
                _outputText.Blocks.Count < firstChangedLineIndex)
            {
                UpdateRichText(string.Join("\n", _renderedLines));
                return;
            }

            while (_outputText.Blocks.Count > firstChangedLineIndex)
            {
                _outputText.Blocks.RemoveAt(_outputText.Blocks.Count - 1);
            }

            for (int i = firstChangedLineIndex; i < _renderedLines.Count; i++)
            {
                _outputText.Blocks.Add(RenderRegularLine(_renderedLines[i]));
            }
        }

        private void RenderLinesToBlocksInternal(List<string> lines)
        {
            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```"))
                {
                    int j = i + 1;
                    List<string> codeLines = new List<string>();
                    while (j < lines.Count && !lines[j].Trim().StartsWith("```"))
                    {
                        codeLines.Add(lines[j]);
                        j++;
                    }

                    bool containsTable = false;
                    for (int k = 0; k + 1 < codeLines.Count; k++)
                    {
                        if (IsTableRow(codeLines[k]) && IsTableSeparatorRow(codeLines[k + 1]))
                        {
                            containsTable = true;
                            break;
                        }
                    }

                    if (containsTable)
                    {
                        RenderLinesToBlocksInternal(codeLines);
                    }
                    else
                    {
                        Block codeBlock = RenderCodeBlock(codeLines, trimmed.Substring(3).Trim());
                        _outputText.Blocks.Add(codeBlock);
                    }

                    if (j < lines.Count)
                    {
                        i = j + 1;
                    }
                    else
                    {
                        i = j;
                    }
                }
                else if (i + 1 < lines.Count && IsTableRow(line) && IsTableSeparatorRow(lines[i + 1]))
                {
                    List<string> tableLines = new List<string>();
                    tableLines.Add(line);
                    tableLines.Add(lines[i + 1]);

                    int j = i + 2;
                    while (j < lines.Count && IsTableRow(lines[j]))
                    {
                        tableLines.Add(lines[j]);
                        j++;
                    }

                    Block tableBlock = RenderTable(tableLines);
                    _outputText.Blocks.Add(tableBlock);
                    i = j;
                }
                else
                {
                    Block lineBlock = RenderRegularLine(line);
                    _outputText.Blocks.Add(lineBlock);
                    i++;
                }
            }
        }

        private Block RenderCodeBlock(List<string> codeLines, string language)
        {
            string codeText = string.Join("\n", codeLines);

            var codeForeground = GetBrushResource("AgentCodeForeground", Microsoft.UI.Colors.Black);
            var textBlock = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = codeForeground,
                IsTextSelectionEnabled = true
            };

            var codeFontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI Emoji, Segoe UI Symbol");
            string[] codeTextLines = codeText.Split('\n');
            for (int li = 0; li < codeTextLines.Length; li++)
            {
                if (li > 0)
                {
                    textBlock.Inlines.Add(new LineBreak());
                }
                AddTextRunsWithEmojiSupport(codeTextLines[li], textBlock.Inlines, false, 0, codeFontFamily, codeForeground);
            }

            textBlock.SelectionChanged += OnTextBlockSelectionChanged;

            var border = new Border
            {
                Margin = new Thickness(0, 6, 0, 6),
                Padding = new Thickness(10, 8, 10, 8),
                Background = GetBrushResource("AgentCodeBackground", Microsoft.UI.Colors.Transparent),
                BorderBrush = GetBrushResource("AgentButtonBorderBrush", Microsoft.UI.Colors.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = textBlock
            };

            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new InlineUIContainer { Child = border });
            return paragraph;
        }

        private static bool IsTableRow(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith("|") && trimmed.EndsWith("|") && trimmed.Length > 1;
        }

        private static bool IsTableSeparatorRow(string line)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("|") || !trimmed.EndsWith("|")) return false;
            foreach (char c in trimmed)
            {
                if (c != '|' && c != '-' && c != ':' && c != ' ' && c != '\t')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool RequiresGroupedRendering(IReadOnlyList<string> lines, int startIndex)
        {
            startIndex = Math.Max(0, startIndex);
            for (int i = startIndex; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    return true;
                }

                if (IsTableRow(lines[i]) &&
                    i + 1 < lines.Count &&
                    IsTableSeparatorRow(lines[i + 1]))
                {
                    return true;
                }

                if (IsTableSeparatorRow(lines[i]) &&
                    i > 0 &&
                    IsTableRow(lines[i - 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> ParseTableRow(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var cells = trimmed.Split('|');
            var result = new List<string>();
            foreach (var cell in cells)
            {
                result.Add(cell.Trim());
            }
            return result;
        }

        private static List<HorizontalAlignment> ParseTableAlignments(string separatorLine, int colCount)
        {
            var cells = ParseTableRow(separatorLine);
            var alignments = new List<HorizontalAlignment>();
            for (int i = 0; i < colCount; i++)
            {
                if (i >= cells.Count)
                {
                    alignments.Add(HorizontalAlignment.Left);
                    continue;
                }

                string cell = cells[i].Trim();
                bool left = cell.StartsWith(":");
                bool right = cell.EndsWith(":");
                if (left && right)
                {
                    alignments.Add(HorizontalAlignment.Center);
                }
                else if (right)
                {
                    alignments.Add(HorizontalAlignment.Right);
                }
                else
                {
                    alignments.Add(HorizontalAlignment.Left);
                }
            }
            return alignments;
        }

        private static double MeasureCellDisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 1;

            double width = 0;
            foreach (char c in text)
            {
                width += IsWideCharacter(c) ? 2.0 : 1.0;
            }
            return Math.Max(1, width);
        }

        private static bool IsWideCharacter(char c)
        {
            return (c >= 0x1100 && c <= 0x115F) ||
                   (c >= 0x2E80 && c <= 0x303E) ||
                   (c >= 0x3041 && c <= 0x33FF) ||
                   (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0x4E00 && c <= 0x9FFF) ||
                   (c >= 0xA000 && c <= 0xA4CF) ||
                   (c >= 0xAC00 && c <= 0xD7A3) ||
                   (c >= 0xF900 && c <= 0xFAFF) ||
                   (c >= 0xFE30 && c <= 0xFE4F) ||
                   (c >= 0xFF00 && c <= 0xFF60) ||
                   (c >= 0xFFE0 && c <= 0xFFE6);
        }

        private Block RenderTable(List<string> tableLines)
        {
            var headerCells = ParseTableRow(tableLines[0]);
            int colCount = headerCells.Count;
            if (colCount == 0)
            {
                return RenderRegularLine(string.Join("\n", tableLines));
            }

            var alignments = ParseTableAlignments(tableLines[1], colCount);

            // Calculate content-proportional column weights so columns fit their content
            double[] colWeights = new double[colCount];
            for (int c = 0; c < colCount; c++)
            {
                colWeights[c] = MeasureCellDisplayWidth(headerCells[c]);
            }
            for (int r = 2; r < tableLines.Count; r++)
            {
                var cells = ParseTableRow(tableLines[r]);
                for (int c = 0; c < colCount && c < cells.Count; c++)
                {
                    double w = MeasureCellDisplayWidth(cells[c]);
                    if (w > colWeights[c]) colWeights[c] = w;
                }
            }

            var grid = new Grid
            {
                Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = GetBrushResource("AgentOutputBackground", Microsoft.UI.Colors.Transparent),
                BorderBrush = GetBrushResource("AgentButtonBorderBrush", Microsoft.UI.Colors.LightGray),
                BorderThickness = new Thickness(1, 1, 0, 0),
                CornerRadius = new CornerRadius(4)
            };

            for (int c = 0; c < colCount; c++)
            {
                // Narrow columns use Auto so they always fit their content
                // and never disappear when the panel is narrow.
                // Wider columns use Star with proportional weights to share
                // the remaining space.
                if (colWeights[c] <= 12)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }
                else
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(colWeights[c], 50), GridUnitType.Star) });
                }
            }

            int rowCount = 0;

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headerBg = GetBrushResource("AgentButtonBackground", Microsoft.UI.Colors.LightGray);

            for (int c = 0; c < colCount; c++)
            {
                var cellText = headerCells[c];
                var cellContent = CreateCellElement(cellText, alignments[c], isHeader: true);

                var cellBorder = new Border
                {
                    Background = headerBg,
                    BorderBrush = GetBrushResource("AgentButtonBorderBrush", Microsoft.UI.Colors.LightGray),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = cellContent,
                    Padding = new Thickness(8, 6, 8, 6)
                };

                Grid.SetRow(cellBorder, rowCount);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
            rowCount++;

            for (int r = 2; r < tableLines.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cells = ParseTableRow(tableLines[r]);

                while (cells.Count < colCount) cells.Add(string.Empty);

                for (int c = 0; c < colCount; c++)
                {
                    var cellText = cells[c];
                    var cellContent = CreateCellElement(cellText, alignments[c], isHeader: false);

                    Brush? rowBg = null;
                    if (rowCount % 2 == 1)
                    {
                        rowBg = GetBrushResource("AgentButtonBackground", Microsoft.UI.Colors.Transparent);
                    }

                    var cellBorder = new Border
                    {
                        Background = rowBg,
                        BorderBrush = GetBrushResource("AgentButtonBorderBrush", Microsoft.UI.Colors.LightGray),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Child = cellContent,
                        Padding = new Thickness(8, 5, 8, 5)
                    };

                    Grid.SetRow(cellBorder, rowCount);
                    Grid.SetColumn(cellBorder, c);
                    grid.Children.Add(cellBorder);
                }
                rowCount++;
            }

            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new InlineUIContainer { Child = grid });
            return paragraph;
        }

        private UIElement CreateCellElement(string cellText, HorizontalAlignment alignment, bool isHeader)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 12,
                Foreground = GetBrushResource("AgentOutputForeground", Microsoft.UI.Colors.Black),
                IsTextSelectionEnabled = true
            };

            if (isHeader)
            {
                textBlock.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            textBlock.SelectionChanged += OnTextBlockSelectionChanged;

            ParseLineToInlines(cellText, textBlock.Inlines);
            return textBlock;
        }

        private Block RenderRegularLine(string line)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 2)
            };

            if (string.IsNullOrEmpty(line))
            {
                paragraph.Inlines.Add(new Run { Text = string.Empty });
                return paragraph;
            }

            bool isHeading = TryParseMarkdownHeading(line, out int headingLevel, out string displayLine);
            double headingFontSize = GetMarkdownHeadingFontSize(headingLevel);
            line = displayLine;

            if (isHeading)
            {
                paragraph.Margin = new Thickness(0, 8, 0, 4);
                AddTextRunsWithEmojiSupport(line, paragraph.Inlines, isBold: true, headingFontSize);
                return paragraph;
            }

            int spaces = 0;
            while (spaces < line.Length && (line[spaces] == ' ' || line[spaces] == '\t'))
            {
                spaces++;
            }

            string trimmedStart = line.Substring(spaces);
            bool isListItem = false;
            int indentLevel = spaces / 2;
            string listBullet = "";
            string itemText = trimmedStart;

            if (trimmedStart.StartsWith("* ") || trimmedStart.StartsWith("- ") || trimmedStart.StartsWith("+ "))
            {
                isListItem = true;
                listBullet = "• ";
                itemText = trimmedStart.Substring(2);
            }
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(trimmedStart, @"^(\d+)\.\s+");
                if (match.Success)
                {
                    isListItem = true;
                    listBullet = match.Value;
                    itemText = trimmedStart.Substring(match.Length);
                }
            }

            if (isListItem)
            {
                paragraph.Margin = new Thickness(12 + indentLevel * 16, 2, 0, 2);

                var bulletRun = new Run
                {
                    Text = listBullet,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold
                };
                paragraph.Inlines.Add(bulletRun);

                ParseLineToInlines(itemText, paragraph.Inlines);
            }
            else
            {
                ParseLineToInlines(line, paragraph.Inlines);
            }

            return paragraph;
        }

        private void EnsureRenderedLineExists()
        {
            if (_renderedLines.Count > 0)
            {
                return;
            }

            _renderedLines.Clear();
            _outputText.Blocks.Clear();
            _renderedLines.Add(string.Empty);
        }

        private void SetRenderedLine(int index, string line)
        {
            if (index < 0)
            {
                return;
            }

            while (_renderedLines.Count <= index)
            {
                _renderedLines.Add(string.Empty);
            }

            if (_renderedLines[index] == line)
            {
                return;
            }

            _renderedLines[index] = line;

            string raw = string.Join("\n", _renderedLines);
            UpdateRichText(raw);
        }

        private void ParseLineToInlines(string line, InlineCollection inlines)
        {
            if (string.IsNullOrEmpty(line))
            {
                inlines.Add(new Run { Text = string.Empty });
                return;
            }

            bool isHeading = TryParseMarkdownHeading(line, out int headingLevel, out string displayLine);
            double headingFontSize = GetMarkdownHeadingFontSize(headingLevel);
            line = displayLine;

            if (string.IsNullOrEmpty(line))
            {
                AddTextRunsWithEmojiSupport(string.Empty, inlines, isHeading, headingFontSize);
                return;
            }

            int i = 0;
            bool isBold = false;
            bool isCode = false;
            var currentSegment = new StringBuilder();

            void FlushCurrentSegment()
            {
                if (currentSegment.Length == 0)
                {
                    return;
                }

                string text = currentSegment.ToString();
                currentSegment.Clear();

                if (isCode)
                {
                    AddTextRunsWithEmojiSupport($"[{text}]", inlines, isHeading || isBold, headingFontSize,
                        new FontFamily("Consolas, Cascadia Mono, Segoe UI Emoji, Segoe UI Symbol"),
                        GetBrushResource("AgentCodeForeground", Microsoft.UI.Colors.DarkRed));
                }
                else
                {
                    AddTextRunsWithEmojiSupport(text, inlines, isHeading || isBold, headingFontSize);
                }
            }

            while (i < line.Length)
            {
                if (isCode)
                {
                    if (line[i] == '`')
                    {
                        FlushCurrentSegment();
                        isCode = false;
                        i++;
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
                else
                {
                    if (line[i] == '`')
                    {
                        if (line.IndexOf('`', i + 1) >= 0)
                        {
                            FlushCurrentSegment();
                            isCode = true;
                            i++;
                        }
                        else
                        {
                            currentSegment.Append(line[i]);
                            i++;
                        }
                    }
                    else if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                    {
                        if (isBold)
                        {
                            FlushCurrentSegment();
                            isBold = false;
                            i += 2;
                        }
                        else if (line.IndexOf("**", i + 2, StringComparison.Ordinal) >= 0)
                        {
                            FlushCurrentSegment();
                            isBold = true;
                            i += 2;
                        }
                        else
                        {
                            currentSegment.Append("**");
                            i += 2;
                        }
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
            }

            FlushCurrentSegment();
        }

        private static Run CreateTextRun(string text, bool isBold, double fontSize)
        {
            var run = new Run { Text = text };
            if (isBold)
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            if (fontSize > 0)
            {
                run.FontSize = fontSize;
            }

            return run;
        }

        private static readonly FontFamily EmojiFontFamily = new FontFamily("Segoe UI Emoji");

        private static bool IsEmojiGrapheme(string grapheme)
        {
            for (int i = 0; i < grapheme.Length; i++)
            {
                char ch = grapheme[i];
                uint code = ch;

                if (char.IsHighSurrogate(ch) && i + 1 < grapheme.Length && char.IsLowSurrogate(grapheme[i + 1]))
                {
                    uint supplementary = 0x10000 + ((uint)(ch - 0xD800) << 10) + (uint)(grapheme[i + 1] - 0xDC00);
                    if (supplementary >= 0x1F000 && supplementary <= 0x1FFFF) return true;
                    i++;
                    continue;
                }

                if (code == 0xFE0F) return true;
                if (code == 0x20E3) return true;
                if (code >= 0x2600 && code <= 0x27BF) return true;
            }
            return false;
        }

        private void AddTextRunsWithEmojiSupport(string text, InlineCollection inlines, bool isBold, double fontSize, FontFamily? fontFamily = null, Brush? foreground = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                var run = new Run { Text = text ?? string.Empty };
                if (isBold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                if (fontSize > 0) run.FontSize = fontSize;
                if (fontFamily != null) run.FontFamily = fontFamily;
                if (foreground != null) run.Foreground = foreground;
                inlines.Add(run);
                return;
            }

            var indexes = StringInfo.ParseCombiningCharacters(text);
            if (indexes.Length == 0)
            {
                var run = CreateTextRun(text, isBold, fontSize);
                if (fontFamily != null) run.FontFamily = fontFamily;
                if (foreground != null) run.Foreground = foreground;
                inlines.Add(run);
                return;
            }

            var segment = new StringBuilder();
            bool? segmentIsEmoji = null;

            for (int i = 0; i < indexes.Length; i++)
            {
                int start = indexes[i];
                int end = i + 1 < indexes.Length ? indexes[i + 1] : text.Length;
                string grapheme = text.Substring(start, end - start);
                bool isEmoji = IsEmojiGrapheme(grapheme);

                if (segmentIsEmoji == null)
                {
                    segmentIsEmoji = isEmoji;
                }

                if (isEmoji != segmentIsEmoji)
                {
                    FlushEmojiSegment(segment.ToString(), segmentIsEmoji.Value, inlines, isBold, fontSize, fontFamily, foreground);
                    segment.Clear();
                    segmentIsEmoji = isEmoji;
                }

                segment.Append(grapheme);
            }

            if (segment.Length > 0 && segmentIsEmoji.HasValue)
            {
                FlushEmojiSegment(segment.ToString(), segmentIsEmoji.Value, inlines, isBold, fontSize, fontFamily, foreground);
            }
        }

        private void FlushEmojiSegment(string text, bool isEmoji, InlineCollection inlines, bool isBold, double fontSize, FontFamily? fontFamily, Brush? foreground)
        {
            var run = new Run { Text = text };
            if (isBold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            if (fontSize > 0) run.FontSize = fontSize;
            if (isEmoji)
            {
                run.FontFamily = EmojiFontFamily;
            }
            else if (fontFamily != null)
            {
                run.FontFamily = fontFamily;
            }
            if (foreground != null)
            {
                run.Foreground = foreground;
            }
            inlines.Add(run);
        }

        private static bool TryParseMarkdownHeading(string line, out int level, out string displayLine)
        {
            level = 0;
            displayLine = line;

            int hashCount = 0;
            while (hashCount < line.Length && hashCount < 6 && line[hashCount] == '#')
            {
                hashCount++;
            }

            if (hashCount == 0 ||
                hashCount >= line.Length ||
                (line[hashCount] != ' ' && line[hashCount] != '\t'))
            {
                return false;
            }

            level = hashCount;
            displayLine = line.Substring(hashCount).TrimStart(' ', '\t');
            return true;
        }

        private static double GetMarkdownHeadingFontSize(int level)
        {
            return level switch
            {
                1 => 17,
                2 => 16,
                3 => 15,
                >= 4 and <= 6 => 14,
                _ => 0
            };
        }

        private Brush GetBrushResource(string key, Windows.UI.Color fallbackColor)
        {
            string themeName = "Light";
            var theme = _resourceOwner.ActualTheme;
            if (theme == ElementTheme.Default)
            {
                themeName = Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";
            }
            else
            {
                themeName = theme == ElementTheme.Dark ? "Dark" : "Light";
            }

            object? dictObj;
            object? resource;

            if (_resourceOwner.Resources.ThemeDictionaries.TryGetValue(themeName, out dictObj) &&
                dictObj is ResourceDictionary themeDict &&
                themeDict.TryGetValue(key, out resource) &&
                resource is Brush brush1)
            {
                return brush1;
            }

            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeName, out dictObj) &&
                dictObj is ResourceDictionary appThemeDict &&
                appThemeDict.TryGetValue(key, out resource) &&
                resource is Brush brush2)
            {
                return brush2;
            }

            if (_resourceOwner.Resources.TryGetValue(key, out resource) && resource is Brush brush3)
            {
                return brush3;
            }
            if (Application.Current.Resources.TryGetValue(key, out resource) && resource is Brush brush4)
            {
                return brush4;
            }

            return new SolidColorBrush(fallbackColor);
        }

        private void OnTextBlockSelectionChanged(object sender, RoutedEventArgs args)
        {
            if (sender is TextBlock tb && !string.IsNullOrEmpty(tb.SelectedText))
            {
                _explicitSelectionChanged(tb.SelectedText);
            }
        }
    }
}
