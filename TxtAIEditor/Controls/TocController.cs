using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class TocController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly Func<OpenedTab?> _getActiveTab;
        private readonly Func<OpenedTab, EditorDocumentSession?> _getSession;
        private readonly Func<bool> _isAozoraMode;
        private readonly Func<int, Task> _revealLineAsync;
        private int _refreshVersion;

        public TocController(
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            Func<OpenedTab?> getActiveTab,
            Func<OpenedTab, EditorDocumentSession?> getSession,
            Func<bool> isAozoraMode,
            Func<int, Task> revealLineAsync)
        {
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _getActiveTab = getActiveTab;
            _getSession = getSession;
            _isAozoraMode = isAozoraMode;
            _revealLineAsync = revealLineAsync;

            _leftSidebar.TocList.ItemsSource = _viewModel.TocItems;
            _leftSidebar.TocItemClick += OnTocItemClick;
        }

        public void RefreshToc(OpenedTab? tab)
        {
            int refreshVersion = ++_refreshVersion;

            if (tab == null)
            {
                _viewModel.TocItems.Clear();
                return;
            }

            var session = _getSession(tab);
            if (session == null)
            {
                _viewModel.TocItems.Clear();
                return;
            }

            int lineCount = session.Model.LineCount;
            var lines = session.GetLines(1, lineCount);
            if (lines == null || lines.Count == 0)
            {
                _viewModel.TocItems.Clear();
                return;
            }

            bool aozoraMode = _isAozoraMode();
            string lang = tab.Language?.ToLowerInvariant() ?? string.Empty;
            bool isTextFile = tab.FilePath != null && tab.FilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

            _ = BuildTocInBackgroundAsync(refreshVersion, tab.Id, lines, aozoraMode, lang, isTextFile);
        }

        private List<TocItem> BuildTocItems(IReadOnlyList<string> lines, bool aozoraMode, string lang, bool isTextFile)
        {
            var newItems = new List<TocItem>();
            if (lang == "markdown")
            {
                ParseMarkdown(lines, newItems);
            }
            else if (aozoraMode || lang == "aozora" || (isTextFile && HasAozoraTags(lines)))
            {
                ParseAozora(lines, newItems);
            }
            else
            {
                ParseCodeOutline(lines, lang, newItems);
            }

            return newItems;
        }

        private async Task BuildTocInBackgroundAsync(
            int refreshVersion,
            string tabId,
            IReadOnlyList<string> lines,
            bool aozoraMode,
            string lang,
            bool isTextFile)
        {
            List<TocItem> newItems;
            try
            {
                newItems = await Task.Run(() => BuildTocItems(lines, aozoraMode, lang, isTextFile));
            }
            catch
            {
                return;
            }

            bool ApplyIfCurrent()
            {
                var activeTab = _getActiveTab();
                if (refreshVersion != _refreshVersion || activeTab?.Id != tabId)
                {
                    return false;
                }

                ApplyTocItems(newItems);
                return true;
            }

            var dispatcher = _leftSidebar.DispatcherQueue;
            if (dispatcher == null || !dispatcher.TryEnqueue(() => ApplyIfCurrent()))
            {
                ApplyIfCurrent();
            }
        }

        private void ApplyTocItems(IReadOnlyList<TocItem> newItems)
        {
            // Check if outline structure changed (additions, deletions, text/margin/icon changes)
            bool structureChanged = false;
            if (newItems.Count != _viewModel.TocItems.Count)
            {
                structureChanged = true;
            }
            else
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    var newItem = newItems[i];
                    var oldItem = _viewModel.TocItems[i];
                    if (newItem.DisplayText != oldItem.DisplayText ||
                        newItem.IconGlyph != oldItem.IconGlyph ||
                        newItem.Margin.Left != oldItem.Margin.Left ||
                        newItem.Margin.Top != oldItem.Margin.Top ||
                        newItem.Margin.Right != oldItem.Margin.Right ||
                        newItem.Margin.Bottom != oldItem.Margin.Bottom)
                    {
                        structureChanged = true;
                        break;
                    }
                }
            }

            if (structureChanged)
            {
                _viewModel.TocItems.Clear();
                foreach (var item in newItems)
                {
                    _viewModel.TocItems.Add(item);
                }
            }
            else
            {
                // Just update line numbers of existing items in place
                for (int i = 0; i < newItems.Count; i++)
                {
                    if (_viewModel.TocItems[i].LineNumber != newItems[i].LineNumber)
                    {
                        _viewModel.TocItems[i].LineNumber = newItems[i].LineNumber;
                    }
                }
            }
        }

        private bool HasAozoraTags(IReadOnlyList<string> lines)
        {
            int checkLimit = Math.Min(lines.Count, 100);
            for (int i = 0; i < checkLimit; i++)
            {
                string line = lines[i];
                if (line.Contains("［＃") || line.Contains("《") && line.Contains("》"))
                {
                    return true;
                }
            }
            return false;
        }

        private void ParseMarkdown(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            var headingRegex = new Regex(@"^\s*(#{1,6})\s+(.+)$", RegexOptions.Compiled);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var match = headingRegex.Match(line);
                if (match.Success)
                {
                    int level = match.Groups[1].Length;
                    string text = match.Groups[2].Value.Trim();

                    // Strip any trailing #s (alternative markdown style)
                    text = text.TrimEnd('#').Trim();

                    newItems.Add(new TocItem
                    {
                        DisplayText = text,
                        LineNumber = i + 1,
                        IconGlyph = "\uE9D2", // Document outline
                        Margin = new Thickness((level - 1) * 12, 2, 0, 2)
                    });
                }
            }
        }

        private void ParseAozora(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            // 1. Regex for inline headings: ［＃「...」は (大/中/小)見出し］
            var inlineHeadingRegex = new Regex(@"［＃「([^」]+)」は\s*(大|中|小)見出し］", RegexOptions.Compiled);

            // 2. Regex for same-line wrapped headings: ［＃(大/中/小)見出し］...［＃(大/中/小)見出し終わり］
            var wrappedHeadingRegex = new Regex(@"［＃(大|中|小)見出し］(.*?)［＃(?:大|中|小)見出し終わり］", RegexOptions.Compiled);

            // 3. Regex for block heading start: ［＃여기서부터(大/中/小)見出し］
            var blockStartRegex = new Regex(@"［＃ここから(大|中|小)見出し］", RegexOptions.Compiled);

            // 4. Regex for block header opening tag on its own line: ［＃(大/中/小)見出し］
            var openHeadingTagRegex = new Regex(@"^［＃(大|中|小)見出し］$", RegexOptions.Compiled);

            bool titleAdded = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // First non-empty line is treated as the book title (usually true for text files)
                if (!titleAdded)
                {
                    if (!trimmed.StartsWith("［＃") && !trimmed.StartsWith("------") && !trimmed.StartsWith("======="))
                    {
                        newItems.Add(new TocItem
                        {
                            DisplayText = CleanAozoraLine(trimmed),
                            LineNumber = i + 1,
                            IconGlyph = "\uE160",
                            Margin = new Thickness(0, 2, 0, 2)
                        });
                        titleAdded = true;
                        continue;
                    }
                }

                // A. Inline match
                var inlineMatch = inlineHeadingRegex.Match(line);
                if (inlineMatch.Success)
                {
                    string title = inlineMatch.Groups[1].Value;
                    string type = inlineMatch.Groups[2].Value;

                    int indent = 0;
                    if (type == "中") indent = 12;
                    else if (type == "小") indent = 24;

                    newItems.Add(new TocItem
                    {
                        DisplayText = CleanAozoraLine(title),
                        LineNumber = i + 1,
                        IconGlyph = "\uE7C3",
                        Margin = new Thickness(indent, 2, 0, 2)
                    });
                    continue;
                }

                // B. Same-line wrapped match
                var wrappedMatch = wrappedHeadingRegex.Match(line);
                if (wrappedMatch.Success)
                {
                    string type = wrappedMatch.Groups[1].Value;
                    string title = wrappedMatch.Groups[2].Value;

                    int indent = 0;
                    if (type == "中") indent = 12;
                    else if (type == "小") indent = 24;

                    newItems.Add(new TocItem
                    {
                        DisplayText = CleanAozoraLine(title),
                        LineNumber = i + 1,
                        IconGlyph = "\uE7C3",
                        Margin = new Thickness(indent, 2, 0, 2)
                    });
                    continue;
                }

                // C. Block heading start match
                var blockStartMatch = blockStartRegex.Match(line);
                if (blockStartMatch.Success)
                {
                    string type = blockStartMatch.Groups[1].Value;
                    int indent = 0;
                    if (type == "中") indent = 12;
                    else if (type == "小") indent = 24;

                    for (int j = i + 1; j < Math.Min(lines.Count, i + 5); j++)
                    {
                        string nextLine = lines[j].Trim();
                        if (!string.IsNullOrEmpty(nextLine) && !nextLine.StartsWith("［＃"))
                        {
                            newItems.Add(new TocItem
                            {
                                DisplayText = CleanAozoraLine(nextLine),
                                LineNumber = j + 1,
                                IconGlyph = "\uE7C3",
                                Margin = new Thickness(indent, 2, 0, 2)
                            });
                            break;
                        }
                    }
                    continue;
                }

                // D. Block heading on its own line match
                var openTagMatch = openHeadingTagRegex.Match(trimmed);
                if (openTagMatch.Success)
                {
                    string type = openTagMatch.Groups[1].Value;
                    int indent = 0;
                    if (type == "中") indent = 12;
                    else if (type == "小") indent = 24;

                    for (int j = i + 1; j < Math.Min(lines.Count, i + 5); j++)
                    {
                        string nextLine = lines[j].Trim();
                        if (!string.IsNullOrEmpty(nextLine) && !nextLine.StartsWith("［＃"))
                        {
                            newItems.Add(new TocItem
                            {
                                DisplayText = CleanAozoraLine(nextLine),
                                LineNumber = j + 1,
                                IconGlyph = "\uE7C3",
                                Margin = new Thickness(indent, 2, 0, 2)
                            });
                            break;
                        }
                    }
                }
            }
        }

        private string CleanAozoraLine(string text)
        {
            // Remove rubies (｜漢字《かんじ》 or 漢字《かんじ》)
            string cleaned = Regex.Replace(text, @"[｜|]([^《]+?)《[^》]+?》", "$1");
            cleaned = Regex.Replace(cleaned, @"([\u4e00-\u9faf\u3400-\u4dbf\uF900-\uFAFF]+?)《[^》]+?》", "$1");

            // Remove other standard tags (e.g. ［＃太字］, ［＃太字終わり］, etc.)
            cleaned = Regex.Replace(cleaned, @"［＃太字］(.*?)［＃太字終わり］", "$1");
            cleaned = Regex.Replace(cleaned, @"［＃斜체］(.*?)［＃斜체終わり］", "$1");
            cleaned = Regex.Replace(cleaned, @"［＃[^］]+?］", "");

            return cleaned.Trim();
        }

        private void ParseCodeOutline(IReadOnlyList<string> lines, string lang, List<TocItem> newItems)
        {
            // Lightweight signature parsers for standard programming languages
            if (lang == "csharp")
            {
                ParseCsharp(lines, newItems);
            }
            else if (lang == "python")
            {
                ParsePython(lines, newItems);
            }
            else if (lang == "javascript" || lang == "typescript")
            {
                ParseJavascript(lines, newItems);
            }
            else if (lang == "go")
            {
                ParseGo(lines, newItems);
            }
            else
            {
                // General fallback: match typical method signatures (e.g. public void foo() or function foo())
                ParseGeneralCode(lines, newItems);
            }
        }

        private void ParseCsharp(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            var classRegex = new Regex(@"^\s*(public|private|protected|internal|static|sealed|abstract|partial)?\s*class\s+(\w+)", RegexOptions.Compiled);
            var methodRegex = new Regex(@"^\s*(public|private|protected|internal|static|async|virtual|override|sealed|abstract)?\s+(async|static|virtual|override|sealed|abstract)?\s*([\w\d_<>\[\]]+)\s+([\w_]+)\s*\((.*?)\)", RegexOptions.Compiled);
            var keywords = new HashSet<string> { "if", "while", "for", "foreach", "switch", "using", "return", "throw", "catch", "lock", "new", "typeof" };

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var classMatch = classRegex.Match(line);
                if (classMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"class {classMatch.Groups[2].Value}",
                        LineNumber = i + 1,
                        IconGlyph = "\uE13C", // Class
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                    continue;
                }

                var methodMatch = methodRegex.Match(line);
                if (methodMatch.Success)
                {
                    string methodName = methodMatch.Groups[4].Value;
                    if (keywords.Contains(methodName)) continue;

                    // Skip field/property initializations like new List<T>()
                    if (line.Contains("=") && line.IndexOf("=") < line.IndexOf("(")) continue;

                    newItems.Add(new TocItem
                    {
                        DisplayText = $"{methodName}(...)",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F", // Method / Function
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                }
            }
        }

        private void ParsePython(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            var classRegex = new Regex(@"^\s*class\s+(\w+)\b", RegexOptions.Compiled);
            var funcRegex = new Regex(@"^\s*def\s+([\w_]+)\s*\((.*?)\):", RegexOptions.Compiled);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var classMatch = classRegex.Match(line);
                if (classMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"class {classMatch.Groups[1].Value}",
                        LineNumber = i + 1,
                        IconGlyph = "\uE13C", // Class
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                    continue;
                }

                var funcMatch = funcRegex.Match(line);
                if (funcMatch.Success)
                {
                    string name = funcMatch.Groups[1].Value;
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"def {name}(...)",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F", // Method
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                }
            }
        }

        private void ParseJavascript(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            var classRegex = new Regex(@"\bclass\s+([\w_]+)\b", RegexOptions.Compiled);
            var funcRegex = new Regex(@"\bfunction\s+([\w_]+)\s*\((.*?)\)", RegexOptions.Compiled);
            var arrowRegex = new Regex(@"const\s+([\w_]+)\s*=\s*(async\s*)?\((.*?)\)\s*=>", RegexOptions.Compiled);
            var methodRegex = new Regex(@"^\s*(async|static|get|set|public|private|protected)?\s*([\w_]+)\s*\((.*?)\)\s*\{", RegexOptions.Compiled);
            var keywords = new HashSet<string> { "if", "while", "for", "switch", "catch", "return", "throw", "class", "function", "const", "let", "var" };

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var classMatch = classRegex.Match(line);
                if (classMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"class {classMatch.Groups[1].Value}",
                        LineNumber = i + 1,
                        IconGlyph = "\uE13C",
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                    continue;
                }

                var funcMatch = funcRegex.Match(line);
                if (funcMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"function {funcMatch.Groups[1].Value}(...)",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F",
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                    continue;
                }

                var arrowMatch = arrowRegex.Match(line);
                if (arrowMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"const {arrowMatch.Groups[1].Value} = (...) =>",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F",
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                    continue;
                }

                var methodMatch = methodRegex.Match(line);
                if (methodMatch.Success)
                {
                    string name = methodMatch.Groups[2].Value;
                    if (keywords.Contains(name)) continue;

                    newItems.Add(new TocItem
                    {
                        DisplayText = $"{name}(...)",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F",
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                }
            }
        }

        private void ParseGo(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            var funcRegex = new Regex(@"^func\s+(?:\([^)]+\)\s+)?([\w_]+)\s*\(", RegexOptions.Compiled);
            var structRegex = new Regex(@"^type\s+([\w_]+)\s+struct\b", RegexOptions.Compiled);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var structMatch = structRegex.Match(line);
                if (structMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"struct {structMatch.Groups[1].Value}",
                        LineNumber = i + 1,
                        IconGlyph = "\uE13C",
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                    continue;
                }

                var funcMatch = funcRegex.Match(line);
                if (funcMatch.Success)
                {
                    newItems.Add(new TocItem
                    {
                        DisplayText = $"func {funcMatch.Groups[1].Value}(...)",
                        LineNumber = i + 1,
                        IconGlyph = "\uE12F",
                        Margin = new Thickness(12, 2, 0, 2)
                    });
                }
            }
        }

        private void ParseGeneralCode(IReadOnlyList<string> lines, List<TocItem> newItems)
        {
            // Simple generic parser that scans for standard declarations
            var funcRegex = new Regex(@"\b(function|def|func|class)\s+([\w_]+)\b|^\s*(public|private|protected)?\s*[\w\d_<>\[\]]+\s+([\w_]+)\s*\((.*?)\)", RegexOptions.Compiled);
            var keywords = new HashSet<string> { "if", "while", "for", "foreach", "switch", "using", "return", "throw", "catch", "lock", "new", "typeof" };

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                var match = funcRegex.Match(line);
                if (match.Success)
                {
                    string declarationType = match.Groups[1].Value;
                    string name = !string.IsNullOrEmpty(declarationType) ? match.Groups[2].Value : match.Groups[4].Value;

                    if (string.IsNullOrEmpty(name) || keywords.Contains(name)) continue;

                    bool isContainer = declarationType == "class";
                    newItems.Add(new TocItem
                    {
                        DisplayText = isContainer ? $"class {name}" : $"{name}(...)",
                        LineNumber = i + 1,
                        IconGlyph = isContainer ? "\uE13C" : "\uE12F",
                        Margin = new Thickness(isContainer ? 0 : 12, 2, 0, 2)
                    });
                }
            }
        }

        private async void OnTocItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as TocItem
                ?? _leftSidebar.TocList.SelectedItem as TocItem;
            if (item == null) return;

            var tab = _getActiveTab();
            if (tab == null) return;

            await _revealLineAsync(item.LineNumber);
        }
    }
}
