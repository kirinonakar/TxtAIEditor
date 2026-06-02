using System;
using System.IO;
using System.Linq;
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
        private readonly Func<string> _searchRootProvider;
        private readonly Func<long> _largeFileThresholdBytesProvider;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Action<string, string> _showError;
        private readonly Func<SearchResultItem, string, Task> _loadAndHighlightResultAsync;
        private readonly Func<Task> _refreshGitStatusAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private string _lastSearchQuery = string.Empty;
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
            Func<string> searchRootProvider,
            Func<long> largeFileThresholdBytesProvider,
            Func<XamlRoot> xamlRootProvider,
            Action<string, string> showError,
            Func<SearchResultItem, string, Task> loadAndHighlightResultAsync,
            Func<Task> refreshGitStatusAsync,
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
            _searchRootProvider = searchRootProvider;
            _largeFileThresholdBytesProvider = largeFileThresholdBytesProvider;
            _xamlRootProvider = xamlRootProvider;
            _showError = showError;
            _loadAndHighlightResultAsync = loadAndHighlightResultAsync;
            _refreshGitStatusAsync = refreshGitStatusAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
        }

        public async Task SearchAllFilesAsync()
        {
            string query = _searchQueryInput.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            string searchRoot = _searchRootProvider();
            if (string.IsNullOrEmpty(searchRoot))
            {
                _showError("검색 실패", "먼저 탐색기에서 작업할 폴더를 선택하십시오.");
                return;
            }

            _lastSearchQuery = query;
            _viewModel.SearchResults.Clear();

            FileSearchSummary summary;
            try
            {
                summary = await _fileSearchService.SearchAsync(
                    searchRoot,
                    query,
                    _largeFileThresholdBytesProvider(),
                    GetSearchOptions(),
                    PublishSearchResults);
            }
            catch (ArgumentException ex)
            {
                _showError("검색 실패", $"정규식이 올바르지 않습니다.\n{ex.Message}");
                return;
            }

            if (summary.FoundCount == 0 && summary.SkippedFiles > 0)
            {
                _showError("검색 완료", $"검색 결과가 없습니다.\n읽을 수 없어 건너뛴 파일: {summary.SkippedFiles:N0}개");
            }
            else if (summary.FoundCount > 0)
            {
                _searchResultsList.DispatcherQueue.TryEnqueue(async () =>
                {
                    _searchResultsList.SelectedIndex = 0;
                    _searchResultsList.ScrollIntoView(_searchResultsList.SelectedItem);
                    if (_searchResultsList.SelectedItem is SearchResultItem selectedItem)
                    {
                        await _loadAndHighlightResultAsync(selectedItem, _lastSearchQuery);
                    }
                });
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

            var options = GetSearchOptions();
            try
            {
                _fileSearchService.BuildSearchRegex(query, options);
            }
            catch (ArgumentException ex)
            {
                _showError("바꾸기 실패", $"정규식이 올바르지 않습니다.\n{ex.Message}");
                return;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = "전체 바꾸기 경고",
                Content = $"{_viewModel.SearchResults.Count}개의 일치 항목을 '{replace}'(으)로 일괄 바꾸기하시겠습니까?",
                PrimaryButtonText = "바꾸기 실행",
                CloseButtonText = "취소",
                XamlRoot = _xamlRootProvider()
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            long thresholdBytes = _largeFileThresholdBytesProvider();
            var grouped = _viewModel.SearchResults.GroupBy(r => r.Path).ToList();
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

            _viewModel.SearchResults.Clear();
            _showError("바꾸기 완료", "모든 매칭 항목의 바꾸기 처리가 완료되었습니다.");
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

            var options = GetSearchOptions();
            try
            {
                _fileSearchService.BuildSearchRegex(query, options);
            }
            catch (ArgumentException ex)
            {
                _showError("바꾸기 실패", $"정규식이 올바르지 않습니다.\n{ex.Message}");
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
                await _refreshGitStatusAsync();
            }
            catch (Exception ex)
            {
                _showError("바꾸기 실패", $"대체 실패: {ex.Message}");
            }
        }

        public async Task OpenSearchResultAsync(object originalSource)
        {
            var item = VisualTreeDataContext.FindFromOriginalSource<SearchResultItem>(originalSource) ??
                       _searchResultsList.SelectedItem as SearchResultItem;
            if (item != null)
            {
                await _loadAndHighlightResultAsync(item, _lastSearchQuery);
            }
        }

        public async Task HandleSearchQueryEnterAsync()
        {
            string query = _searchQueryInput.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            if (_viewModel.SearchResults.Count == 0 || query != _lastSearchQuery)
            {
                await SearchAllFilesAsync();
                return;
            }

            int nextIndex = 0;
            if (_searchResultsList.SelectedIndex >= 0)
            {
                nextIndex = (_searchResultsList.SelectedIndex + 1) % _viewModel.SearchResults.Count;
            }

            _searchResultsList.SelectedIndex = nextIndex;
            _searchResultsList.ScrollIntoView(_searchResultsList.SelectedItem);

            if (_searchResultsList.SelectedItem is SearchResultItem selectedItem)
            {
                await _loadAndHighlightResultAsync(selectedItem, _lastSearchQuery);
            }
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

        private void PublishSearchResults(System.Collections.Generic.IReadOnlyList<SearchResultItem> results)
        {
            _searchResultsList.DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var item in results)
                {
                    _viewModel.SearchResults.Add(item);
                }
            });
        }
    }
}
