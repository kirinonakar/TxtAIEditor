using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class FileSearchService : IFileSearchService
    {
        private readonly IFileService _fileService;

        public FileSearchService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public Regex BuildSearchRegex(string query, FileSearchOptions options)
        {
            string pattern = options.IsRegex ? query : Regex.Escape(query);
            if (options.WholeWord)
            {
                pattern = $"\\b{pattern}\\b";
            }

            var regexOptions = options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            return new Regex(pattern, regexOptions);
        }

        public async Task<FileSearchSummary> SearchAsync(
            string searchRoot,
            string query,
            long largeFileThresholdBytes,
            FileSearchOptions options,
            Action<IReadOnlyList<SearchResultItem>> publishResults)
        {
            var searchRegex = BuildSearchRegex(query, options);
            int foundCount = 0;
            int skippedFiles = 0;

            await Task.Run(() =>
            {
                var tempResults = new List<SearchResultItem>();

                foreach (var file in EnumerateSearchFiles(searchRoot))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > largeFileThresholdBytes)
                        {
                            var largeResults = _fileService.SearchLargeFileAsync(
                                file,
                                query,
                                options.IsRegex,
                                options.MatchCase,
                                options.WholeWord).GetAwaiter().GetResult();

                            foreach (var lr in largeResults)
                            {
                                tempResults.Add(new SearchResultItem
                                {
                                    Path = file,
                                    LineNumber = lr.LineNumber,
                                    LineContent = lr.LineContent,
                                    IndexOfMatch = lr.IndexOfMatch,
                                    MatchLength = lr.MatchLength
                                });
                                foundCount++;
                                FlushSearchResultsIfNeeded(tempResults, publishResults);
                            }

                            continue;
                        }

                        int lineNum = 1;
                        foreach (var line in File.ReadLines(file))
                        {
                            var match = searchRegex.Match(line);
                            if (match.Success)
                            {
                                tempResults.Add(new SearchResultItem
                                {
                                    Path = file,
                                    LineNumber = lineNum,
                                    LineContent = line,
                                    IndexOfMatch = match.Index,
                                    MatchLength = match.Length
                                });
                                foundCount++;
                                FlushSearchResultsIfNeeded(tempResults, publishResults);
                            }

                            lineNum++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException)
                    {
                        skippedFiles++;
                        Debug.WriteLine($"Skipped search file {file}: {ex.Message}");
                    }
                }

                FlushSearchResults(tempResults, publishResults);
            });

            return new FileSearchSummary
            {
                FoundCount = foundCount,
                SkippedFiles = skippedFiles
            };
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
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(searchRoot, "*", options);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is DirectoryNotFoundException)
            {
                Debug.WriteLine($"Search root unavailable: {ex.Message}");
                yield break;
            }

            foreach (var file in files)
            {
                if (!ShouldSkipSearchPath(file))
                {
                    yield return file;
                }
            }
        }

        private static bool ShouldSkipSearchPath(string filePath)
        {
            string normalized = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] skippedSegments =
            {
                $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}"
            };

            return skippedSegments.Any(segment => normalized.Contains(segment, StringComparison.OrdinalIgnoreCase));
        }

        private static void FlushSearchResultsIfNeeded(List<SearchResultItem> results, Action<IReadOnlyList<SearchResultItem>> publishResults)
        {
            if (results.Count >= 30)
            {
                FlushSearchResults(results, publishResults);
            }
        }

        private static void FlushSearchResults(List<SearchResultItem> results, Action<IReadOnlyList<SearchResultItem>> publishResults)
        {
            if (results.Count == 0)
            {
                return;
            }

            var batch = results.ToList();
            results.Clear();
            publishResults(batch);
        }
    }
}
