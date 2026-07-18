using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class SearchReplaceController
    {
        private readonly IFileSearchService _fileSearchService;
        private readonly MainWindowViewModel _viewModel;
        private readonly TextBox _searchQueryInput;
        private readonly TextBox _replaceQueryInput;
        private readonly ToggleButton _matchCaseToggle;
        private readonly ToggleButton _wholeWordToggle;
        private readonly ToggleButton _regexToggle;
        private readonly ListView _searchResultsList;
        private readonly TextBlock _searchHeaderLabel;
        private readonly FrameworkElement _searchProgressIndicator;
        private readonly Func<string> _searchRootProvider;
        private readonly Func<long> _largeFileThresholdBytesProvider;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Action<string, string> _showError;
        private readonly Func<SearchResultItem, string, Task> _loadAndHighlightResultAsync;
        private readonly Func<Task> _refreshGitStatusAsync;
        private readonly Func<string, string, string> _getString;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private string _lastSearchQuery = string.Empty;
        private CancellationTokenSource? _searchCancellationTokenSource;
        private int _searchVersion;
        public event Func<string, Task>? FileModified;

        public SearchReplaceController(
            IFileSearchService fileSearchService,
            MainWindowViewModel viewModel,
            TextBox searchQueryInput,
            TextBox replaceQueryInput,
            ToggleButton matchCaseToggle,
            ToggleButton wholeWordToggle,
            ToggleButton regexToggle,
            ListView searchResultsList,
            TextBlock searchHeaderLabel,
            FrameworkElement searchProgressIndicator,
            Func<string> searchRootProvider,
            Func<long> largeFileThresholdBytesProvider,
            Func<XamlRoot> xamlRootProvider,
            Action<string, string> showError,
            Func<SearchResultItem, string, Task> loadAndHighlightResultAsync,
            Func<Task> refreshGitStatusAsync,
            Func<string, string, string>? getString = null,
            Action? beforeDialog = null,
            Action? afterDialog = null)
        {
            _fileSearchService = fileSearchService;
            _viewModel = viewModel;
            _searchQueryInput = searchQueryInput;
            _replaceQueryInput = replaceQueryInput;
            _matchCaseToggle = matchCaseToggle;
            _wholeWordToggle = wholeWordToggle;
            _regexToggle = regexToggle;
            _searchResultsList = searchResultsList;
            _searchHeaderLabel = searchHeaderLabel;
            _searchProgressIndicator = searchProgressIndicator;
            _searchRootProvider = searchRootProvider;
            _largeFileThresholdBytesProvider = largeFileThresholdBytesProvider;
            _xamlRootProvider = xamlRootProvider;
            _showError = showError;
            _loadAndHighlightResultAsync = loadAndHighlightResultAsync;
            _refreshGitStatusAsync = refreshGitStatusAsync;
            _getString = getString ?? ((_, fallback) => fallback);
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
        }

        public async Task SearchAllFilesAsync()
        {
            string query = _searchQueryInput.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                CancelActiveSearch();
                return;
            }

            string searchRoot = _searchRootProvider();
            if (string.IsNullOrEmpty(searchRoot))
            {
                CancelActiveSearch();
                _showError(
                    _getString("SearchFailedTitle", "검색 실패"),
                    _getString("SearchNoFolderSelectedMessage", "먼저 탐색기에서 작업할 폴더를 선택하십시오."));
                return;
            }

            // Cancel any active search and reset state
            CancelActiveSearch();

            _lastSearchQuery = query;
            _viewModel.SearchResults.Clear();
            _viewModel.SearchResultsGrouped.Clear();

            var searchCancellationTokenSource = new CancellationTokenSource();
            _searchCancellationTokenSource = searchCancellationTokenSource;
            int searchVersion = unchecked(++_searchVersion);
            CancellationToken cancellationToken = searchCancellationTokenSource.Token;
            SetSearchHeaderIsSearching(searchVersion, true);

            FileSearchSummary summary;
            try
            {
                summary = await _fileSearchService.SearchAsync(
                    searchRoot,
                    query,
                    _largeFileThresholdBytesProvider(),
                    GetSearchOptions(),
                    results => PublishSearchResults(results, searchVersion, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ArgumentException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _showError(
                    _getString("SearchFailedTitle", "검색 실패"),
                    string.Format(_getString("SearchInvalidRegexMessageFormat", "정규식이 올바르지 않습니다.\n{0}"), ex.Message));
                return;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _showError(
                    _getString("SearchFailedTitle", "검색 실패"),
                    string.Format(_getString("SearchErrorMessageFormat", "검색 중 오류가 발생했습니다.\n{0}"), ex.Message));
                return;
            }
            finally
            {
                if (ReferenceEquals(_searchCancellationTokenSource, searchCancellationTokenSource) || _searchCancellationTokenSource == null)
                {
                    _searchCancellationTokenSource = null;
                    SetSearchHeaderText(isSearching: false);
                }

                searchCancellationTokenSource.Dispose();
            }

            if (cancellationToken.IsCancellationRequested || searchVersion != _searchVersion)
            {
                return;
            }

            if (summary.FoundCount == 0 && summary.SkippedFiles > 0)
            {
                _showError(
                    _getString("SearchCompletedTitle", "검색 완료"),
                    string.Format(_getString("SearchNoResultsSkippedFormat", "검색 결과가 없습니다.\n읽을 수 없어 건너뛴 파일: {0:N0}개"), summary.SkippedFiles));
            }
        }

        public async Task ReplaceAllAsync()
        {
            string query = _searchQueryInput.Text;
            string replace = _replaceQueryInput.Text;
            if (string.IsNullOrEmpty(query) || _viewModel.SearchResults.Count == 0)
            {
                return;
            }

            var editableResults = _viewModel.SearchResults.Where(r => r.CanReplace).ToList();
            if (editableResults.Count == 0)
            {
                _showError(
                    _getString("SearchReplaceReadOnlyTitle", "바꾸기 불가"),
                    _getString("SearchReplaceReadOnlyContent", "문서 파일의 검색 결과는 내용 검색 전용이라 바꾸기할 수 없습니다."));
                return;
            }

            var options = GetSearchOptions();
            try
            {
                _fileSearchService.BuildSearchRegex(query, options);
            }
            catch (ArgumentException ex)
            {
                _showError(
                    _getString("ReplaceFailedTitle", "바꾸기 실패"),
                    string.Format(_getString("SearchInvalidRegexMessageFormat", "정규식이 올바르지 않습니다.\n{0}"), ex.Message));
                return;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("ReplaceAllWarningTitle", "전체 바꾸기 경고"),
                Content = string.Format(_getString("ReplaceAllWarningContentFormat", "{0:N0}개의 일치 항목을 '{1}'(으)로 일괄 바꾸기하시겠습니까?"), editableResults.Count, replace),
                PrimaryButtonText = _getString("ReplaceAllConfirmButton", "바꾸기 실행"),
                CloseButtonText = _getString("UnsavedChangesCancel", "취소"),
                XamlRoot = _xamlRootProvider()
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            long thresholdBytes = _largeFileThresholdBytesProvider();
            var grouped = editableResults.GroupBy(r => r.Path).ToList();
            foreach (var group in grouped)
            {
                string filePath = group.Key;
                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Length > thresholdBytes)
                    {
                        await _fileSearchService.ReplaceInLargeFileAsync(filePath, group.ToList(), query, replace, options);
                    }
                    else
                    {
                        var lines = File.ReadAllLines(filePath).ToList();
                        foreach (int lineNumber in group.Select(r => r.LineNumber).Distinct())
                        {
                            int index = lineNumber - 1;
                            if (index >= 0 && index < lines.Count)
                            {
                                lines[index] = _fileSearchService.ReplaceSearchMatches(lines[index], query, replace, options);
                            }
                        }

                        await File.WriteAllLinesAsync(filePath, lines);
                    }

                    if (FileModified != null)
                    {
                        await FileModified.Invoke(filePath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed replace in {filePath}: {ex.Message}");
                }
            }

            foreach (SearchResultItem replacedItem in editableResults)
            {
                _viewModel.SearchResults.Remove(replacedItem);
            }

            UpdateGroupedResults();
            _showError(
                _getString("ReplaceCompletedTitle", "바꾸기 완료"),
                _getString("ReplaceCompletedMessage", "모든 매칭 항목의 바꾸기 처리가 완료되었습니다."));
            await _refreshGitStatusAsync();
        }

        public async Task ReplaceOneAsync(SearchResultItem item)
        {
            if (item == null)
            {
                return;
            }

            string query = _searchQueryInput.Text;
            string replace = _replaceQueryInput.Text;
            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            if (!item.CanReplace)
            {
                _showError(
                    _getString("SearchReplaceReadOnlyTitle", "바꾸기 불가"),
                    _getString("SearchReplaceReadOnlyContent", "문서 파일의 검색 결과는 내용 검색 전용이라 바꾸기할 수 없습니다."));
                return;
            }

            var options = GetSearchOptions();
            try
            {
                _fileSearchService.BuildSearchRegex(query, options);
            }
            catch (ArgumentException ex)
            {
                _showError(
                    _getString("ReplaceFailedTitle", "바꾸기 실패"),
                    string.Format(_getString("SearchInvalidRegexMessageFormat", "정규식이 올바르지 않습니다.\n{0}"), ex.Message));
                return;
            }

            long thresholdBytes = _largeFileThresholdBytesProvider();
            string filePath = item.Path;
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > thresholdBytes)
                {
                    await _fileSearchService.ReplaceInLargeFileAsync(filePath, new[] { item }, query, replace, options);
                }
                else
                {
                    var lines = File.ReadAllLines(filePath).ToList();
                    int index = item.LineNumber - 1;
                    if (index >= 0 && index < lines.Count)
                    {
                        lines[index] = _fileSearchService.ReplaceSearchMatches(lines[index], query, replace, options);
                    }
                    await File.WriteAllLinesAsync(filePath, lines);
                }

                if (FileModified != null)
                {
                    await FileModified.Invoke(filePath);
                }

                _viewModel.SearchResults.Remove(item);
                UpdateGroupedResults();
                await _refreshGitStatusAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ReplaceFailedTitle", "바꾸기 실패"),
                    string.Format(_getString("ReplaceOneFailureFormat", "대체 실패: {0}"), ex.Message));
            }
        }

        public async Task OpenSearchResultAsync(SearchResultItem item)
        {
            if (item != null)
            {
                _searchResultsList.SelectedItem = item;
                await _loadAndHighlightResultAsync(item, _lastSearchQuery);
            }
        }

        public async Task HandleSearchQueryEnterAsync()
        {
            await SearchAllFilesAsync();
        }

        private FileSearchOptions GetSearchOptions()
        {
            return new FileSearchOptions
            {
                IsRegex = _regexToggle.IsChecked == true,
                MatchCase = _matchCaseToggle.IsChecked == true,
                WholeWord = _wholeWordToggle.IsChecked == true
            };
        }

        public void CancelActiveSearch()
        {
            CancellationTokenSource? cancellationTokenSource = _searchCancellationTokenSource;
            cancellationTokenSource?.Cancel();
            if (cancellationTokenSource != null)
            {
                _searchCancellationTokenSource = null;
            }

            unchecked
            {
                _searchVersion++;
            }

            SetSearchHeaderText(isSearching: false);
        }

        private void SetSearchHeaderIsSearching(int searchVersion, bool isSearching)
        {
            QueueSearchHeaderText(isSearching, searchVersion);
        }

        private void SetSearchHeaderText(bool isSearching)
        {
            QueueSearchHeaderText(isSearching, expectedSearchVersion: null);
        }

        private void QueueSearchHeaderText(bool isSearching, int? expectedSearchVersion)
        {
            void ApplyIfCurrent()
            {
                if (expectedSearchVersion.HasValue && expectedSearchVersion.Value != _searchVersion)
                {
                    return;
                }

                ApplySearchHeaderText(isSearching);
            }

            if (_searchHeaderLabel.DispatcherQueue.HasThreadAccess)
            {
                ApplyIfCurrent();
                return;
            }

            _searchHeaderLabel.DispatcherQueue.TryEnqueue(ApplyIfCurrent);
        }

        private void ApplySearchHeaderText(bool isSearching)
        {
            _searchHeaderLabel.Text = _getString("SearchHeader", "폴더 전체 검색 및 바꾸기");
            _searchProgressIndicator.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PublishSearchResults(System.Collections.Generic.IReadOnlyList<SearchResultItem> results, int searchVersion, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || searchVersion != _searchVersion)
            {
                return;
            }

            _searchResultsList.DispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || searchVersion != _searchVersion)
                {
                    return;
                }

                if (_viewModel.SearchResults is BulkObservableCollection<SearchResultItem> bulk)
                {
                    bulk.AddRange(results);
                }
                else
                {
                    foreach (var item in results)
                    {
                        _viewModel.SearchResults.Add(item);
                    }
                }

                AppendGroupedResults(results);
            });
        }

        private void UpdateGroupedResults()
        {
            SynchronizeGroupedResults();
        }

        private void AppendGroupedResults(System.Collections.Generic.IReadOnlyList<SearchResultItem> results)
        {
            foreach (var resultGroup in results.GroupBy(item => item.Path))
            {
                SearchResultGroup? existingGroup = _viewModel.SearchResultsGrouped.FirstOrDefault(
                    group => string.Equals(group.Path, resultGroup.Key, StringComparison.OrdinalIgnoreCase));

                if (existingGroup == null)
                {
                    _viewModel.SearchResultsGrouped.Add(new SearchResultGroup(
                        resultGroup.Key,
                        resultGroup,
                        GetRelativeDirectory(resultGroup.Key)));
                    continue;
                }

                foreach (SearchResultItem item in resultGroup)
                {
                    existingGroup.Add(item);
                }
            }
        }

        private void SynchronizeGroupedResults()
        {
            var currentItemsByPath = _viewModel.SearchResults
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            for (int groupIndex = _viewModel.SearchResultsGrouped.Count - 1; groupIndex >= 0; groupIndex--)
            {
                SearchResultGroup group = _viewModel.SearchResultsGrouped[groupIndex];
                if (!currentItemsByPath.TryGetValue(group.Path, out var currentItems))
                {
                    _viewModel.SearchResultsGrouped.RemoveAt(groupIndex);
                    continue;
                }

                for (int itemIndex = group.Count - 1; itemIndex >= 0; itemIndex--)
                {
                    if (!currentItems.Contains(group[itemIndex]))
                    {
                        group.RemoveAt(itemIndex);
                    }
                }

                foreach (SearchResultItem item in currentItems)
                {
                    if (!group.Contains(item))
                    {
                        group.Add(item);
                    }
                }

                currentItemsByPath.Remove(group.Path);
            }

            foreach (var remainingGroup in currentItemsByPath)
            {
                _viewModel.SearchResultsGrouped.Add(new SearchResultGroup(
                    remainingGroup.Key,
                    remainingGroup.Value,
                    GetRelativeDirectory(remainingGroup.Key)));
            }
        }

        private string GetRelativeDirectory(string path)
        {
            string searchRoot = _searchRootProvider();
            try
            {
                if (!string.IsNullOrEmpty(searchRoot) && path.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string relPath = Path.GetRelativePath(searchRoot, path);
                    string? directory = Path.GetDirectoryName(relPath);
                    return string.IsNullOrEmpty(directory) || directory == "." ? "" : directory.Replace('\\', '/');
                }

                string? fullDirectory = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(fullDirectory) ? "" : fullDirectory.Replace('\\', '/');
            }
            catch
            {
                return Path.GetDirectoryName(path) ?? "";
            }
        }
    }
}
