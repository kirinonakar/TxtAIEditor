using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public sealed class FileSearchOptions
    {
        public bool IsRegex { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
    }

    public sealed class FileSearchSummary
    {
        public int FoundCount { get; set; }
        public int SkippedFiles { get; set; }
    }

    public interface IFileSearchService
    {
        Regex BuildSearchRegex(string query, FileSearchOptions options);
        Task<FileSearchSummary> SearchAsync(
            string searchRoot,
            string query,
            long largeFileThresholdBytes,
            FileSearchOptions options,
            Action<IReadOnlyList<SearchResultItem>> publishResults,
            CancellationToken cancellationToken = default);
        string ReplaceSearchMatches(string original, string query, string replace, FileSearchOptions options);
        Task ReplaceInLargeFileAsync(string filePath, IEnumerable<SearchResultItem> results, string query, string replace, FileSearchOptions options);
    }
}
