using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class FileSearchService : IFileSearchService
    {
        private const int DocumentSearchMaxChars = 50_000_000;
        private const int SearchResultBatchSize = 100;

        private static readonly HashSet<string> SkippedFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Compiled / native binaries
            ".exe", ".dll", ".so", ".dylib", ".pdb", ".lib", ".obj", ".o", ".a", ".msixupload", ".msix", ".appx",
            // Documents
            ".pdf", ".doc", ".docx", ".hwp", ".hwpx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".pages", ".key", ".numbers", ".epub", ".mobi",
            // Images
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".svg", ".webp",
            ".tiff", ".tif", ".heic", ".heif", ".raw", ".psd", ".ai", ".eps",
            // Videos & Audio
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mpg", ".mpeg", ".3gp", ".ts", ".m2ts", ".vob",
            ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".wma",
            // Compressed / archive containers
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso",
            ".jar", ".war", ".ear", ".cab", ".lzma", ".tgz", ".tbz2", ".txz",
            ".zst", ".br", ".lz4", ".ace", ".arj"
        };

        private static readonly HashSet<string> AutoDetectedSearchFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".htm", ".html"
        };

        private readonly IFileService _fileService;
        private readonly DocumentTextExtractionService _documentTextExtractionService;

        public FileSearchService(IFileService fileService)
            : this(fileService, new DocumentTextExtractionService())
        {
        }

        public FileSearchService(IFileService fileService, DocumentTextExtractionService documentTextExtractionService)
        {
            _fileService = fileService;
            _documentTextExtractionService = documentTextExtractionService;
        }

        public Regex BuildSearchRegex(string query, FileSearchOptions options)
        {
            string pattern = options.IsRegex ? query : Regex.Escape(query);
            if (options.WholeWord)
            {
                pattern = $"\\b{pattern}\\b";
            }

            var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!options.MatchCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            return new Regex(pattern, regexOptions);
        }

        public async Task<FileSearchSummary> SearchAsync(
            string searchRoot,
            string query,
            long largeFileThresholdBytes,
            FileSearchOptions options,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken = default)
        {
            Regex? regex = options.IsRegex || options.WholeWord
                ? BuildSearchRegex(query, options)
                : null;
            var matcher = SearchMatcher.Create(query, options, regex);
            int foundCount = 0;
            int skippedFiles = 0;

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetSearchDegreeOfParallelism()
            };

            await Parallel.ForEachAsync(EnumerateSearchFiles(searchRoot), parallelOptions, async (file, token) =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    int fileFoundCount = DocumentTextExtractionService.IsSupportedExtension(file)
                        ? await SearchExtractedDocumentAsync(file, matcher, publishResults, token).ConfigureAwait(false)
                        : SearchTextFile(file, matcher, publishResults, token);

                    if (fileFoundCount > 0)
                    {
                        Interlocked.Add(ref foundCount, fileFoundCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsSearchReadException(ex))
                {
                    Interlocked.Increment(ref skippedFiles);
                    Debug.WriteLine($"Skipped search file {file}: {ex.Message}");
                }
            }).ConfigureAwait(false);

            return new FileSearchSummary
            {
                FoundCount = foundCount,
                SkippedFiles = skippedFiles
            };
        }

        private async Task<int> SearchExtractedDocumentAsync(
            string file,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            int foundCount = 0;
            string extracted = await _documentTextExtractionService
                .ExtractTextAsync(file, DocumentSearchMaxChars, normalize: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(extracted))
            {
                return 0;
            }

            var tempResults = new List<SearchResultItem>(SearchResultBatchSize);
            int lineNum = 1;
            foreach (string line in EnumerateNormalizedLines(extracted))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (matcher.TryFind(line, out int indexOfMatch, out int matchLength))
                {
                    tempResults.Add(new SearchResultItem
                    {
                        Path = file,
                        LineNumber = lineNum,
                        LineContent = line,
                        IndexOfMatch = indexOfMatch,
                        MatchLength = matchLength,
                        CanReplace = false
                    });
                    foundCount++;
                    FlushSearchResultsIfNeeded(tempResults, publishResults, cancellationToken);
                }

                lineNum++;
            }

            FlushSearchResults(tempResults, publishResults, cancellationToken);
            return foundCount;
        }

        private static int SearchTextFile(
            string file,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            if (ShouldAutoDetectSearchEncoding(file))
            {
                byte[] bytes = File.ReadAllBytes(file);
                Encoding encoding = TextEncodingService.GetTextEncoding(bytes, "Auto");
                using var detectedStream = new MemoryStream(bytes);
                using var detectedReader = new StreamReader(
                    detectedStream,
                    encoding,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 64 * 1024);

                return SearchTextReader(file, detectedReader, matcher, publishResults, cancellationToken);
            }

            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024);

            return SearchTextReader(file, reader, matcher, publishResults, cancellationToken);
        }

        private static int SearchTextReader(
            string file,
            TextReader reader,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            int foundCount = 0;
            var tempResults = new List<SearchResultItem>(SearchResultBatchSize);

            string? line;
            int lineNum = 1;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (matcher.TryFind(line, out int indexOfMatch, out int matchLength))
                {
                    tempResults.Add(new SearchResultItem
                    {
                        Path = file,
                        LineNumber = lineNum,
                        LineContent = line,
                        IndexOfMatch = indexOfMatch,
                        MatchLength = matchLength
                    });
                    foundCount++;
                    FlushSearchResultsIfNeeded(tempResults, publishResults, cancellationToken);
                }

                lineNum++;
            }

            FlushSearchResults(tempResults, publishResults, cancellationToken);
            return foundCount;
        }

        public string ReplaceSearchMatches(string original, string query, string replace, FileSearchOptions options)
        {
            if (options.IsRegex || options.WholeWord)
            {
                var regex = BuildSearchRegex(query, options);
                return regex.Replace(original, replace);
            }

            var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return original.Replace(query, replace, comparison);
        }

        public async Task ReplaceInLargeFileAsync(string filePath, IEnumerable<SearchResultItem> results, string query, string replace, FileSearchOptions options)
        {
            string tempPath = Path.Combine(Path.GetDirectoryName(filePath) ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backupPath = filePath + ".bak";
            var targetLines = results.Select(r => r.LineNumber).Distinct().ToHashSet();

            try
            {
                using (var reader = new StreamReader(filePath))
                using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
                {
                    string? line;
                    int lineNum = 1;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        string output = targetLines.Contains(lineNum)
                            ? ReplaceSearchMatches(line, query, replace, options)
                            : line;
                        await writer.WriteLineAsync(output);
                        lineNum++;
                    }
                }

                File.Replace(tempPath, filePath, backupPath);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                throw new IOException($"대용량 바꾸기 중 실패: {ex.Message}", ex);
            }
        }

        private static IEnumerable<string> EnumerateSearchFiles(string searchRoot)
        {
            var gitIgnoreStack = new List<GitIgnoreFile>();

            try
            {
                var dirs = new List<string>();
                var parent = Directory.GetParent(searchRoot);
                while (parent != null)
                {
                    dirs.Add(parent.FullName);
                    parent = parent.Parent;
                }
                dirs.Reverse();

                foreach (var dir in dirs)
                {
                    string path = Path.Combine(dir, ".gitignore");
                    if (File.Exists(path))
                    {
                        try
                        {
                            gitIgnoreStack.Add(new GitIgnoreFile(path));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to load parent gitignore at {path}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error resolving parent gitignores: {ex.Message}");
            }

            return EnumerateDirectoryRecursive(searchRoot, searchRoot, gitIgnoreStack);
        }

        private static bool ShouldSkipDirectoryName(string dirName)
        {
            string[] skippedNames = { ".git", ".vs", "bin", "obj", "node_modules", "packages", ".venv" };
            return skippedNames.Any(name => dirName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldSkipFileExtension(string filePath)
        {
            if (DocumentTextExtractionService.IsSupportedExtension(filePath))
            {
                return false;
            }

            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext))
            {
                return false;
            }
            return SkippedFileExtensions.Contains(ext);
        }

        private static bool ShouldAutoDetectSearchEncoding(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            return AutoDetectedSearchFileExtensions.Contains(ext);
        }

        private static IEnumerable<string> EnumerateNormalizedLines(string text)
        {
            using var reader = new StringReader(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }

        private static bool IsSearchReadException(Exception ex)
        {
            return ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException ||
                ex is NotSupportedException ||
                ex is InvalidDataException ||
                ex is XmlException ||
                ex is ArgumentException ||
                ex is InvalidOperationException ||
                ex is System.Runtime.InteropServices.ExternalException ||
                ex is TimeoutException;
        }

        private static bool IsPathIgnored(string absolutePath, List<GitIgnoreFile> gitIgnoreStack, bool isDir)
        {
            bool ignored = false;
            foreach (var gitIgnoreFile in gitIgnoreStack)
            {
                if (!absolutePath.StartsWith(gitIgnoreFile.BaseDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(gitIgnoreFile.BaseDir, absolutePath).Replace('\\', '/');

                foreach (var rule in gitIgnoreFile.Rules)
                {
                    if (rule.TargetDirOnly && !isDir)
                    {
                        continue;
                    }

                    if (rule.Regex.IsMatch(relPath))
                    {
                        ignored = !rule.IsNegated;
                    }
                }
            }
            return ignored;
        }

        private static IEnumerable<string> EnumerateDirectoryRecursive(
            string currentDir,
            string searchRoot,
            List<GitIgnoreFile> gitIgnoreStack)
        {
            string dirName = Path.GetFileName(currentDir);
            if (string.IsNullOrEmpty(dirName))
            {
                dirName = currentDir;
            }
            if (ShouldSkipDirectoryName(dirName))
            {
                yield break;
            }

            if (currentDir != searchRoot)
            {
                if (IsPathIgnored(currentDir, gitIgnoreStack, isDir: true))
                {
                    yield break;
                }
            }

            string gitignorePath = Path.Combine(currentDir, ".gitignore");
            bool pushed = false;
            if (File.Exists(gitignorePath))
            {
                try
                {
                    var localRules = new GitIgnoreFile(gitignorePath);
                    gitIgnoreStack.Add(localRules);
                    pushed = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load gitignore at {gitignorePath}: {ex.Message}");
                }
            }

            string[] files = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Debug.WriteLine($"Failed to list files in {currentDir}: {ex.Message}");
            }

            foreach (var file in files)
            {
                if (IsPathIgnored(file, gitIgnoreStack, isDir: false))
                {
                    continue;
                }
                if (ShouldSkipFileExtension(file))
                {
                    continue;
                }

                yield return file;
            }

            string[] subDirs = Array.Empty<string>();
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Debug.WriteLine($"Failed to list directories in {currentDir}: {ex.Message}");
            }

            foreach (var subDir in subDirs)
            {
                foreach (var f in EnumerateDirectoryRecursive(subDir, searchRoot, gitIgnoreStack))
                {
                    yield return f;
                }
            }

            if (pushed)
            {
                gitIgnoreStack.RemoveAt(gitIgnoreStack.Count - 1);
            }
        }

        private static int GetSearchDegreeOfParallelism()
        {
            return Math.Clamp(Environment.ProcessorCount, 2, 8);
        }

        private static void FlushSearchResultsIfNeeded(List<SearchResultItem> results, Action<IReadOnlyList<SearchResultItem>> publishResults, CancellationToken cancellationToken)
        {
            if (results.Count >= SearchResultBatchSize)
            {
                FlushSearchResults(results, publishResults, cancellationToken);
            }
        }

        private static void FlushSearchResults(List<SearchResultItem> results, Action<IReadOnlyList<SearchResultItem>> publishResults, CancellationToken cancellationToken)
        {
            if (results.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var batch = results.ToList();
            results.Clear();
            publishResults(batch);
        }

        private readonly struct SearchMatcher
        {
            private readonly string _query;
            private readonly StringComparison _comparison;
            private readonly Regex? _regex;

            private SearchMatcher(string query, StringComparison comparison, Regex? regex)
            {
                _query = query;
                _comparison = comparison;
                _regex = regex;
            }

            public static SearchMatcher Create(string query, FileSearchOptions options, Regex? regex)
            {
                if (options.IsRegex || options.WholeWord)
                {
                    return new SearchMatcher(query, StringComparison.Ordinal, regex ?? throw new ArgumentNullException(nameof(regex)));
                }

                var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                return new SearchMatcher(query, comparison, null);
            }

            public bool TryFind(string line, out int indexOfMatch, out int matchLength)
            {
                if (_regex != null)
                {
                    Match match = _regex.Match(line);
                    if (match.Success)
                    {
                        indexOfMatch = match.Index;
                        matchLength = match.Length;
                        return true;
                    }

                    indexOfMatch = -1;
                    matchLength = 0;
                    return false;
                }

                indexOfMatch = line.IndexOf(_query, _comparison);
                matchLength = indexOfMatch >= 0 ? _query.Length : 0;
                return indexOfMatch >= 0;
            }
        }
    }

    internal class GitIgnoreRule
    {
        public string RawPattern { get; }
        public bool IsNegated { get; }
        public bool TargetDirOnly { get; }
        public bool HasSlash { get; }
        public Regex Regex { get; }

        public GitIgnoreRule(string pattern, string baseDir)
        {
            RawPattern = pattern;
            if (pattern.StartsWith("!"))
            {
                IsNegated = true;
                pattern = pattern.Substring(1);
            }

            if (pattern.EndsWith("/"))
            {
                TargetDirOnly = true;
                pattern = pattern.TrimEnd('/');
            }

            pattern = pattern.Replace('\\', '/');
            HasSlash = pattern.Contains('/');

            string regexPattern;
            if (HasSlash)
            {
                if (pattern.StartsWith("/"))
                {
                    pattern = pattern.Substring(1);
                }
                string globRegex = GlobToRegex(pattern);
                regexPattern = "^" + globRegex + "($|/)";
            }
            else
            {
                string globRegex = GlobToRegex(pattern);
                regexPattern = "(^|/)" + globRegex + "($|/)";
            }

            Regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private static string GlobToRegex(string glob)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else if (c == '[')
                {
                    sb.Append('[');
                    i++;
                    while (i < glob.Length && glob[i] != ']')
                    {
                        sb.Append(glob[i]);
                        i++;
                    }
                    if (i < glob.Length)
                    {
                        sb.Append(']');
                    }
                }
                else if (c == '.' || c == '+' || c == '(' || c == ')' || c == '^' || c == '$' || c == '{' || c == '}' || c == '|' || c == '\\')
                {
                    sb.Append('\\').Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }

    internal class GitIgnoreFile
    {
        public string BaseDir { get; }
        public List<GitIgnoreRule> Rules { get; } = new List<GitIgnoreRule>();

        public GitIgnoreFile(string filePath)
        {
            BaseDir = Path.GetDirectoryName(filePath) ?? "";
            if (File.Exists(filePath))
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    Rules.Add(new GitIgnoreRule(trimmed, BaseDir));
                }
            }
        }
    }
}
