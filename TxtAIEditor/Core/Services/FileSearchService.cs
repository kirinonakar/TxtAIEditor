using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
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
        private const int SearchResultLimit = 10_000;
        private const int SearchPreviewLength = 512;
        private const int SearchReadBufferSize = 16 * 1024;
        private const int EncodingSampleSize = 256 * 1024;
        private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", "packages", ".venv"
        };

        private static readonly HashSet<string> SkippedFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Compiled / native binaries
            ".exe", ".dll", ".so", ".dylib", ".pdb", ".lib", ".obj", ".o", ".a", ".msixupload", ".msix", ".appx",
            // Documents
            ".pdf", ".doc", ".docx", ".hwp", ".hwpx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".pages", ".key", ".numbers", ".epub", ".mobi",
            // Images
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".svg", ".webp", ".avif",
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
        private readonly object _folderSearchGate = new();
        private DocumentSearchTextCache? _folderSearchCache;

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

            return new Regex(pattern, regexOptions, TimeSpan.FromSeconds(2));
        }

        public void ClearFolderSearchCache()
        {
            DocumentSearchTextCache? cache;
            lock (_folderSearchGate)
            {
                cache = _folderSearchCache;
                _folderSearchCache = null;
            }
            cache?.Dispose();
        }

        public async Task<FileSearchSummary> SearchAsync(
            string searchRoot,
            string query,
            long largeFileThresholdBytes,
            FileSearchOptions options,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(searchRoot));
            DocumentSearchTextCache? previous = null;
            DocumentSearchTextCache cache;
            CancellationTokenSource searchCancellation;
            lock (_folderSearchGate)
            {
                if (_folderSearchCache is null || _folderSearchCache.FolderPath != folderPath)
                {
                    previous = _folderSearchCache;
                    _folderSearchCache = new DocumentSearchTextCache(folderPath);
                }
                cache = _folderSearchCache;
                searchCancellation = cache.BeginSearch(cancellationToken);
            }

            // Preparing the enumerator reads ancestor .gitignore files. Regex compilation
            // and all filesystem access must happen after leaving the caller's UI thread.
            try
            {
                previous?.Dispose();
                CancellationToken token = searchCancellation.Token;
                return await Task.Run(() => SearchCoreAsync(folderPath, query, options, publishResults, cache, token), token)
                    .ConfigureAwait(false);
            }
            finally
            {
                searchCancellation.Dispose();
                cache.EndSearch();
            }
        }

        private async Task<FileSearchSummary> SearchCoreAsync(
            string searchRoot,
            string query,
            FileSearchOptions options,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            DocumentSearchTextCache cache,
            CancellationToken cancellationToken)
        {
            Regex? regex = options.IsRegex || options.WholeWord
                ? BuildSearchRegex(query, options)
                : null;
            var matcher = SearchMatcher.Create(query, options, regex);
            int foundCount = 0;
            int skippedFiles = 0;
            int resultCount = 0;

            void PublishLimitedResults(IReadOnlyList<SearchResultItem> results)
            {
                int start = Interlocked.Add(ref resultCount, results.Count) - results.Count;
                int remaining = SearchResultLimit - start;
                if (remaining >= results.Count)
                {
                    publishResults(results);
                }
                else if (remaining > 0)
                {
                    publishResults(results.Take(remaining).ToArray());
                }
            }

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetSearchDegreeOfParallelism()
            };

            using var memoryWatch = FolderSearchMemoryMonitor.Shared.WatchSearch();
            await Parallel.ForEachAsync(EnumerateSearchFiles(searchRoot, options.IncludeHiddenFiles, cancellationToken), parallelOptions, async (file, token) =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    int fileFoundCount = DocumentTextExtractionService.IsSupportedExtension(file)
                        ? await SearchExtractedDocumentAsync(file, cache, matcher, PublishLimitedResults, token).ConfigureAwait(false)
                        : SearchTextFile(file, cache, matcher, PublishLimitedResults, token);

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
                SkippedFiles = skippedFiles,
                ResultLimitReached = resultCount > SearchResultLimit
            };
        }

        private async Task<int> SearchExtractedDocumentAsync(
            string file,
            DocumentSearchTextCache cache,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            using TextReader? reader = await _documentTextExtractionService
                .OpenFolderSearchTextAsync(file, DocumentSearchMaxChars, cache, cancellationToken)
                .ConfigureAwait(false);
            return reader is null ? 0 : SearchTextReader(file, reader, cache, matcher, publishResults, cancellationToken, canReplace: false);
        }

        private static int SearchTextFile(
            string file,
            DocumentSearchTextCache cache,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);
            Encoding encoding = Encoding.UTF8;
            if (ShouldAutoDetectSearchEncoding(file))
            {
                using var sampleReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                byte[] sample = sampleReader.ReadBytes((int)Math.Min(stream.Length, EncodingSampleSize));
                encoding = TextEncodingService.GetTextEncoding(sample, "Auto");
                stream.Position = 0;
            }

            using var reader = new StreamReader(
                stream,
                encoding,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: stream.Length < 64 * 1024 ? 4096 : 64 * 1024);

            return SearchTextReader(file, reader, cache, matcher, publishResults, cancellationToken);
        }

        private static int SearchTextReader(
            string file,
            TextReader reader,
            DocumentSearchTextCache cache,
            SearchMatcher matcher,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken,
            bool canReplace = true)
        {
            int foundCount = 0;
            var tempResults = new List<SearchResultItem>(SearchResultBatchSize);
            var buffer = new char[SearchReadBufferSize];
            using var continuedLine = new LongSearchLine(matcher, cache);
            bool hasContinuation = false;
            bool skipLineFeed = false;
            int lineNum = 1;

            void FinishLine(ReadOnlySpan<char> text)
            {
                int indexOfMatch;
                int matchLength;
                string? preview = null;
                bool found;
                if (hasContinuation)
                {
                    continuedLine.Append(text);
                    found = continuedLine.TryFind(out indexOfMatch, out matchLength, out preview);
                }
                else
                {
                    found = matcher.TryFind(text, out indexOfMatch, out matchLength);
                }

                if (found)
                {
                    tempResults.Add(new SearchResultItem
                    {
                        Path = file,
                        LineNumber = lineNum,
                        LineContent = preview ?? CreateSearchPreview(text, indexOfMatch, text.Length, textOffset: 0),
                        IndexOfMatch = indexOfMatch,
                        MatchLength = matchLength,
                        CanReplace = canReplace
                    });
                    foundCount++;
                    FlushSearchResultsIfNeeded(tempResults, publishResults, cancellationToken);
                }

                if (hasContinuation)
                {
                    continuedLine.Reset();
                    hasContinuation = false;
                }
                lineNum++;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0) break;

                ReadOnlySpan<char> remaining = buffer.AsSpan(0, read);
                if (skipLineFeed && remaining[0] == '\n') remaining = remaining[1..];
                skipLineFeed = false;

                while (!remaining.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int end = remaining.IndexOfAny('\r', '\n');
                    if (end < 0)
                    {
                        continuedLine.Append(remaining);
                        hasContinuation = true;
                        break;
                    }

                    FinishLine(remaining[..end]);
                    bool carriageReturn = remaining[end] == '\r';
                    remaining = remaining[(end + 1)..];
                    if (carriageReturn)
                    {
                        if (remaining.IsEmpty) skipLineFeed = true;
                        else if (remaining[0] == '\n') remaining = remaining[1..];
                    }
                }
            }

            if (hasContinuation) FinishLine(ReadOnlySpan<char>.Empty);
            FlushSearchResults(tempResults, publishResults, cancellationToken);
            return foundCount;
        }

        private static string CreateSearchPreview(ReadOnlySpan<char> text, int matchIndex, int lineLength, int textOffset)
        {
            if (lineLength <= SearchPreviewLength)
            {
                return text.ToString();
            }

            int start = Math.Clamp(matchIndex - SearchPreviewLength / 4, 0, lineLength - SearchPreviewLength);
            return string.Concat(start > 0 ? "…" : "", text.Slice(start - textOffset, SearchPreviewLength),
                start + SearchPreviewLength < lineLength ? "…" : "");
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

                throw new IOException(string.Format(LocalizedServiceMessages.Get("LargeReplaceErrorFormat", "Replace in large file failed: {0}"), ex.Message), ex);
            }
        }

        private static IEnumerable<string> EnumerateSearchFiles(string searchRoot, bool includeHiddenFiles, CancellationToken cancellationToken)
        {
            var gitIgnoreStack = new List<GitIgnoreFile>();
            if (includeHiddenFiles)
            {
                return EnumerateDirectoryRecursive(searchRoot, searchRoot, gitIgnoreStack, includeHiddenFiles, cancellationToken);
            }

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

            return EnumerateDirectoryRecursive(searchRoot, searchRoot, gitIgnoreStack, includeHiddenFiles, cancellationToken);
        }

        private static bool ShouldSkipDirectoryName(string dirName, bool includeHiddenFiles)
        {
            return !includeHiddenFiles && SkippedDirectoryNames.Contains(dirName);
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
                ex is OutOfMemoryException ||
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
            List<GitIgnoreFile> gitIgnoreStack,
            bool includeHiddenFiles,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(currentDir));
            if (ShouldSkipDirectoryName(dirName, includeHiddenFiles) || DocumentSearchTextCache.IsSearchTempDirectory(currentDir, dirName))
            {
                yield break;
            }

            if (!includeHiddenFiles && currentDir != searchRoot)
            {
                if (IsPathIgnored(currentDir, gitIgnoreStack, isDir: true))
                {
                    yield break;
                }
            }

            string gitignorePath = Path.Combine(currentDir, ".gitignore");
            bool pushed = false;
            if (!includeHiddenFiles && File.Exists(gitignorePath))
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

            var subDirs = new List<string>();
            foreach (var entry in EnumerateDirectoryEntries(currentDir, includeHiddenFiles, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    subDirs.Add(entry.Path);
                    continue;
                }
                if (!includeHiddenFiles && IsPathIgnored(entry.Path, gitIgnoreStack, isDir: false))
                {
                    continue;
                }

                yield return entry.Path;
            }

            foreach (var subDir in subDirs)
            {
                foreach (var f in EnumerateDirectoryRecursive(subDir, searchRoot, gitIgnoreStack, includeHiddenFiles, cancellationToken))
                {
                    yield return f;
                }
            }

            if (pushed)
            {
                gitIgnoreStack.RemoveAt(gitIgnoreStack.Count - 1);
            }
        }

        private static IEnumerable<(string Path, bool IsDirectory)> EnumerateDirectoryEntries(
            string directory, bool includeHiddenFiles, CancellationToken cancellationToken)
        {
            IEnumerator<(string Path, bool IsDirectory)>? iterator = null;
            try
            {
                var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(directory,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = includeHiddenFiles ? 0 : FileAttributes.Hidden
                    })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return entry.IsDirectory
                            ? (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                              !ShouldSkipDirectoryName(entry.FileName.ToString(), includeHiddenFiles)
                            : !ShouldSkipFileExtension(entry.FileName.ToString());
                    }
                };
                iterator = entries.GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Debug.WriteLine($"Failed to enumerate {directory}: {ex.Message}");
            }

            if (iterator == null) yield break;
            using (iterator)
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = iterator.MoveNext(); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        Debug.WriteLine($"Failed to enumerate {directory}: {ex.Message}");
                        yield break;
                    }
                    if (!hasNext) yield break;
                    yield return iterator.Current;
                }
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

        private sealed class LongSearchLine : IDisposable
        {
            private const int RegexBufferSize = 2 * SearchReadBufferSize;
            private readonly SearchMatcher _matcher;
            private readonly DocumentSearchTextCache _cache;
            private readonly int _overlap;
            private char[]? _window;
            private char[]? _context;
            private char[]? _regexBuffer;
            private FileStream? _regexInput;
            private int _length;
            private int _windowLength;
            private int _contextStart;
            private int _contextLength;
            private int _matchIndex = -1;
            private int _matchLength;

            public LongSearchLine(SearchMatcher matcher, DocumentSearchTextCache cache)
            {
                _matcher = matcher;
                _cache = cache;
                // Retain enough input for a match crossing the read boundary and its preview.
                _overlap = Math.Max(0, matcher.QueryLength - 1) + SearchPreviewLength;
            }

            public void Append(ReadOnlySpan<char> text)
            {
                if (text.IsEmpty) return;
                if (text.Length > int.MaxValue - _length) throw new InvalidDataException("Search line exceeds the supported character length.");

                if (_matcher.RequiresFullLine)
                {
                    AppendRegexInput(text);
                }
                else if (_matchIndex < 0)
                {
                    _window ??= new char[SearchReadBufferSize + _overlap];
                    text.CopyTo(_window.AsSpan(_windowLength));
                    int windowStart = _length - _windowLength;
                    ReadOnlySpan<char> window = _window.AsSpan(0, _windowLength + text.Length);
                    if (_matcher.TryFind(window, out int index, out _matchLength))
                    {
                        _matchIndex = windowStart + index;
                        _contextStart = Math.Max(0, _matchIndex - SearchPreviewLength);
                        AppendContext(window[(_contextStart - windowStart)..]);
                    }
                    else
                    {
                        _windowLength = Math.Min(window.Length, _overlap);
                        window[^_windowLength..].CopyTo(_window);
                    }
                }
                else
                {
                    AppendContext(text);
                }

                _length += text.Length;
            }

            private void AppendRegexInput(ReadOnlySpan<char> text)
            {
                if (_regexInput is null && _length + text.Length <= RegexBufferSize)
                {
                    _regexBuffer ??= new char[RegexBufferSize];
                    text.CopyTo(_regexBuffer.AsSpan(_length));
                    return;
                }

                // Store UTF-16 so the mapped text can be matched without decoding the whole line into a string.
                if (_regexInput is null)
                {
                    _regexInput = _cache.CreateScratchStream();
                    _regexInput.Write(MemoryMarshal.AsBytes(_regexBuffer.AsSpan(0, _length)));
                }
                _regexInput.Write(MemoryMarshal.AsBytes(text));
            }

            private void AppendContext(ReadOnlySpan<char> text)
            {
                _context ??= new char[2 * SearchPreviewLength];
                int length = Math.Min(text.Length, _context.Length - _contextLength);
                text[..length].CopyTo(_context.AsSpan(_contextLength));
                _contextLength += length;
            }

            public bool TryFind(out int index, out int matchLength, out string? preview)
            {
                if (_matcher.RequiresFullLine)
                {
                    return _regexInput is null
                        ? FindRegexMatch(_regexBuffer.AsSpan(0, _length), out index, out matchLength, out preview)
                        : FindMappedRegexMatch(out index, out matchLength, out preview);
                }

                index = _matchIndex;
                matchLength = _matchLength;
                preview = index >= 0 ? CreateSearchPreview(_context.AsSpan(0, _contextLength), index, _length, _contextStart) : null;
                return index >= 0;
            }

            private bool FindRegexMatch(ReadOnlySpan<char> line, out int index, out int matchLength, out string? preview)
            {
                bool found = _matcher.TryFind(line, out index, out matchLength);
                preview = found ? CreateSearchPreview(line, index, _length, textOffset: 0) : null;
                return found;
            }

            private unsafe bool FindMappedRegexMatch(out int index, out int matchLength, out string? preview)
            {
                _regexInput!.Flush();
                using var mapped = MemoryMappedFile.CreateFromFile(_regexInput, mapName: null, capacity: 0,
                    MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                using var view = mapped.CreateViewAccessor(0, (long)_length * sizeof(char), MemoryMappedFileAccess.Read);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    return FindRegexMatch(new ReadOnlySpan<char>(pointer + view.PointerOffset, _length), out index, out matchLength, out preview);
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            public void Reset()
            {
                Dispose();
                _length = 0;
                _windowLength = 0;
                _contextLength = 0;
                _matchIndex = -1;
            }

            public void Dispose()
            {
                _regexInput?.Dispose();
                _regexInput = null;
            }
        }

        private readonly struct SearchMatcher
        {
            private readonly string _query;
            private readonly StringComparison _comparison;
            private readonly Regex? _regex;

            public bool RequiresFullLine => _regex is not null;
            public int QueryLength => _query.Length;

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

            public bool TryFind(ReadOnlySpan<char> line, out int indexOfMatch, out int matchLength)
            {
                if (_regex != null)
                {
                    var matches = _regex.EnumerateMatches(line);
                    if (matches.MoveNext())
                    {
                        indexOfMatch = matches.Current.Index;
                        matchLength = matches.Current.Length;
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
