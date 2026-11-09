using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    // A small literal grammar, never a shell program. Unknown flags/stages fail closed.
    internal sealed class RgCommandPlan
    {
        private const string ArchiveExtensions = "zip,7z,rar,tar,gz,bz2,xz,zst,tgz,tbz,tbz2,jar,xpi,kra,snagx,als,lz4,br";
        private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
        {
            "--no-config", "--files", "--hidden", "--no-hidden", "--no-ignore", "--no-ignore-vcs",
            "--no-ignore-parent", "--no-ignore-global", "--no-ignore-dot", "--ignore-case", "--case-sensitive",
            "--smart-case", "--fixed-strings", "--line-number", "--no-line-number", "--with-filename",
            "--no-filename", "--only-matching", "--invert-match", "--word-regexp", "--line-regexp",
            "--count", "--count-matches", "--files-with-matches", "--files-without-match", "--quiet",
            "--json", "--vimgrep", "--heading", "--no-heading", "--trim", "--stats", "--no-messages",
            "--text", "--binary", "--follow", "--multiline", "--multiline-dotall", "--pcre2",
            "--crlf", "--null", "--null-data", "--byte-offset", "--column", "--passthru",
            "--line-buffered", "--block-buffered", "--no-unicode", "--unicode", "--type-list",
            "--help", "--version", "--no-require-git", "--glob-case-insensitive", "--ignore-file-case-insensitive",
            "--max-columns-preview", "--no-max-columns-preview"
        };
        private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
        {
            "--regexp", "--file", "--glob", "--iglob", "--type", "--type-not", "--type-add", "--type-clear",
            "--max-count", "--max-depth", "--max-filesize", "--max-columns", "--after-context", "--before-context",
            "--context", "--context-separator", "--field-match-separator", "--field-context-separator",
            "--color", "--colors", "--encoding", "--engine", "--threads", "--sort", "--sortr",
            "--replace", "--ignore-file", "--path-separator", "--regex-size-limit", "--dfa-size-limit"
        };
        private const string ShortSwitches = "nNiIsSFwxcClLqovHh0aUbupP.";
        private const string ShortValues = "egtfmABCjMTr";
        private readonly List<Func<IEnumerable<string>, IEnumerable<string>>> _filters = new();
        internal bool HasFilters => _filters.Count > 0;
        internal List<string> Arguments { get; } = new() { "--no-config" };
        internal string? ScopeNote { get; private set; }
        private readonly List<string> _excludedExplicitPaths = new();

        internal void ValidateExplicitPaths(string workspaceRoot)
        {
            foreach (string path in _excludedExplicitPaths)
            {
                // ripgrep bypasses glob exclusions for explicit file operands.
                // Preserve real directories whose names happen to have an excluded suffix.
                if (!Directory.Exists(Path.GetFullPath(path, workspaceRoot)))
                    throw new ArgumentException("The explicit file is outside the default rga scope: " + path +
                        ". Set includeNotebooks=true for .ipynb or includeArchives=true for archives.");
            }
        }

        internal static RgCommandPlan Parse(string input, bool rga = false,
            bool includeNotebooks = false, bool includeArchives = false)
        {
            var stages = Tokenize(input);
            var plan = new RgCommandPlan();
            if (rga)
            {
                // rga extracts its own flags even after -- or in an rg option value.
                if (stages.SelectMany(stage => stage).Any(arg => arg.StartsWith("--rga-", StringComparison.Ordinal)))
                    throw new ArgumentException("rga configuration options are not allowed.");
                plan.Arguments.Insert(0, "--rga-config-file=" + Path.Combine(AppContext.BaseDirectory, "Assets", "SafeRgaConfig.json"));
                // User -M/--max-columns and preview switches later in argv override these defaults.
                plan.Arguments.Add("--max-columns=300");
                plan.Arguments.Add("--max-columns-preview");
            }
            bool positionalOnly = false;
            int? optionInsertionIndex = null;
            var positionalArguments = new List<string>();
            bool hasExplicitPattern = false;
            bool listsFiles = false;
            List<string> arguments = stages[0];
            for (int i = 0; i < arguments.Count; i++)
            {
                string arg = arguments[i];
                if (positionalOnly || !arg.StartsWith('-') || arg == "-")
                {
                    positionalArguments.Add(arg);
                    plan.Arguments.Add(arg);
                    continue;
                }
                if (arg == "--")
                {
                    positionalOnly = true;
                    optionInsertionIndex = plan.Arguments.Count;
                    plan.Arguments.Add(arg);
                    continue;
                }
                bool takesValue = false;
                bool inlineValue = false;
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    int equal = arg.IndexOf('=');
                    string name = equal < 0 ? arg : arg[..equal];
                    hasExplicitPattern |= name is "--regexp" or "--file";
                    listsFiles |= name == "--files";
                    takesValue = ValueOptions.Contains(name);
                    inlineValue = equal >= 0;
                    if (!takesValue && (!Switches.Contains(name) || inlineValue))
                        throw new ArgumentException($"Option is not allowed: {name}");
                }
                else
                {
                    for (int j = 1; j < arg.Length; j++)
                    {
                        if (ShortValues.Contains(arg[j]))
                        {
                            hasExplicitPattern |= arg[j] is 'e' or 'f';
                            takesValue = true;
                            inlineValue = j + 1 < arg.Length;
                            break;
                        }
                        if (!ShortSwitches.Contains(arg[j]))
                            throw new ArgumentException($"Option is not allowed: -{arg[j]}");
                    }
                }
                plan.Arguments.Add(arg);
                if (takesValue && !inlineValue)
                {
                    if (++i == arguments.Count) throw new ArgumentException($"Missing value for {arg}.");
                    plan.Arguments.Add(arguments[i]);
                }
            }
            if (rga)
            {
                var scopeOptions = new List<string>();
                if (!includeNotebooks)
                    scopeOptions.Add("--iglob=!*.ipynb");
                else
                    // Search notebook JSON literally instead of using incompatible pandoc readers.
                    // This pre-glob is app-owned; user preprocessor/executable options remain blocked.
                    scopeOptions.Add("--pre-glob=!*.[iI][pP][yY][nN][bB]");
                if (!includeArchives)
                    scopeOptions.Add("--iglob=!*.{" + ArchiveExtensions + "}");
                foreach (string path in positionalArguments.Skip(hasExplicitPattern || listsFiles ? 0 : 1))
                {
                    string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if ((!includeNotebooks && extension == "ipynb") ||
                        (!includeArchives && ArchiveExtensions.Split(',').Contains(extension, StringComparer.Ordinal)))
                        plan._excludedExplicitPaths.Add(path);
                }
                // Apply the explicit scope after user globs, before a real -- delimiter.
                // A regex/option value equal to -- must not be mistaken for that delimiter.
                plan.Arguments.InsertRange(optionInsertionIndex ?? plan.Arguments.Count, scopeOptions);
                plan.ScopeNote = "[rga_scope] " +
                    (includeNotebooks ? "Notebooks included as raw JSON. " : "Notebooks excluded (includeNotebooks=false). ") +
                    (includeArchives ? "Archives included." : "Archives excluded (includeArchives=false).");
            }
            foreach (List<string> stage in stages.Skip(1))
            {
                string kind = stage[0].ToLowerInvariant();
                if (kind == "sort")
                    plan._filters.Add(ParseSort(stage));
                else if (kind == "sort-object" && stage.Count == 1)
                    plan._filters.Add(lines => lines.Order(StringComparer.Ordinal));
                else if (kind == "wc" && stage.Count == 2 && stage[1] == "-l")
                    plan._filters.Add(lines => new[] { lines.Count().ToString(CultureInfo.InvariantCulture) });
                else
                {
                    string? count = null;
                    if (kind is "head" or "tail")
                    {
                        count = stage.Count == 1 ? "10" : stage.Count == 2 ? stage[1] :
                            stage.Count == 3 && stage[1] == "-n" ? stage[2] : null;
                        if (stage.Count == 2 && count?.StartsWith('-') == true) count = count[1..];
                    }
                    else if ((kind is "select-object" or "select") && stage.Count == 3)
                    {
                        kind = stage[1].Equals("-First", StringComparison.OrdinalIgnoreCase) ? "head" :
                            stage[1].Equals("-Last", StringComparison.OrdinalIgnoreCase) ? "tail" : "invalid";
                        count = stage[2];
                    }
                    if (count == null || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int size) ||
                        size < 0 || size > 100000 || kind is not ("head" or "tail"))
                        throw new ArgumentException("Unsupported pipeline. Use head/tail [N or -n N], sort [-t CHAR] [-k N] [-n] [-r], wc -l, or Select-Object -First/-Last N. Use run_powershell for other commands.");
                    plan._filters.Add(kind == "head" ? lines => lines.Take(size) : lines => lines.TakeLast(size));
                }
            }
            return plan;
        }

        internal string ApplyFilters(string stdout)
        {
            if (_filters.Count == 0) return stdout;
            var lines = new List<string>();
            using (var reader = new StringReader(stdout))
                while (reader.ReadLine() is string line) lines.Add(line);
            foreach (var filter in _filters)
                lines = filter(lines).ToList();
            return string.Join(Environment.NewLine, lines);
        }

        private static Func<IEnumerable<string>, IEnumerable<string>> ParseSort(IReadOnlyList<string> stage)
        {
            const string syntax = "Use sort [-t CHAR] [-k N] [-n] [-r], for example sort -t: -k2 -nr.";
            char? separator = null;
            int? field = null;
            bool numeric = false;
            bool reverse = false;

            for (int i = 1; i < stage.Count; i++)
            {
                string option = stage[i];
                switch (option)
                {
                    case "-n":
                        numeric = true;
                        continue;
                    case "-r":
                        reverse = true;
                        continue;
                    case "-nr":
                    case "-rn":
                        numeric = reverse = true;
                        continue;
                }

                if (option.StartsWith("-t", StringComparison.Ordinal) || option.StartsWith("-k", StringComparison.Ordinal))
                {
                    string value = option.Length > 2 ? option[2..] : ++i < stage.Count ? stage[i] : string.Empty;
                    if (option[1] == 't' && separator == null && value.Length == 1)
                    {
                        separator = value[0];
                        continue;
                    }
                    if (option[1] == 'k' && field == null &&
                        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
                    {
                        field = number;
                        continue;
                    }
                }

                throw new ArgumentException($"Unsupported sort option: {option}. {syntax}");
            }

            string Key(string line) => GetSortKey(line, separator, field ?? 1);
            return lines =>
            {
                var ordered = numeric
                    ? lines.OrderBy(line => GetNumericSortKey(Key(line)))
                    : lines.OrderBy(Key, StringComparer.Ordinal);
                var withTieBreak = ordered.ThenBy(line => line, StringComparer.Ordinal);
                return reverse ? withTieBreak.Reverse() : withTieBreak;
            };
        }

        private static string GetSortKey(string line, char? separator, int field)
        {
            int start = 0;
            for (int i = 1; i < field; i++)
            {
                if (separator is char delimiter)
                {
                    int next = line.IndexOf(delimiter, start);
                    if (next < 0) return string.Empty;
                    start = next + 1;
                }
                else
                {
                    while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
                    while (start < line.Length && !char.IsWhiteSpace(line[start])) start++;
                    if (start == line.Length) return string.Empty;
                }
            }
            return line[start..];
        }

        private static double GetNumericSortKey(string key)
        {
            // A key can include later fields. Read only its leading number.
            var match = Regex.Match(key.TrimStart(), @"^-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)");
            double.TryParse(match.ValueSpan, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double number);
            return number;
        }

        private static List<List<string>> Tokenize(string input)
        {
            var stages = new List<List<string>> { new() };
            var token = new StringBuilder();
            char quote = '\0';
            bool started = false;
            void Flush()
            {
                if (started) { stages[^1].Add(token.ToString()); token.Clear(); started = false; }
            }
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\0' || c is '\r' or '\n') throw new ArgumentException("Control characters are not allowed.");
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else if (quote == '"' && c == '\\' && i + 1 < input.Length && input[i + 1] == '"') token.Append(input[++i]);
                    else token.Append(c);
                }
                else if (c is '\'' or '"') { quote = c; started = true; }
                else if (char.IsWhiteSpace(c)) Flush();
                else if (c == '|')
                {
                    Flush();
                    if (stages[^1].Count == 0) throw new ArgumentException("Empty pipeline stage.");
                    stages.Add(new());
                }
                else if (c is ';' or '&' or '<' or '>' or '`' or '$' or '{' or '}')
                    throw new ArgumentException("Shell syntax is not supported. Quote literal search text or use run_powershell.");
                else { started = true; token.Append(c); }
            }
            if (quote != '\0') throw new ArgumentException("Unclosed quote.");
            Flush();
            if (stages[^1].Count == 0) throw new ArgumentException("Empty arguments or pipeline stage.");
            return stages;
        }
    }
}
