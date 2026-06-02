using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class GitPanelController
    {
        public event EventHandler<string>? FileRestored;

        private readonly IGitService _gitService;
        private readonly IFileService _fileService;
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly TextBlock _statusGitBranch;
        private readonly Func<string> _repoPathProvider;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, bool> _isGitNotDetected;
        private readonly Action<string, string> _showError;
        private readonly Action _startAutoRefresh;
        private readonly Func<string, string, string?, string?, string?, string?, string?, Task> _openCompareTabAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;

        public GitPanelController(
            IGitService gitService,
            IFileService fileService,
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            TextBlock statusGitBranch,
            Func<string> repoPathProvider,
            Func<XamlRoot> xamlRootProvider,
            Func<string, string, string> getString,
            Func<string, bool> isGitNotDetected,
            Action<string, string> showError,
            Action startAutoRefresh,
            Func<string, string, string?, string?, string?, string?, string?, Task> openCompareTabAsync,
            Action? beforeDialog = null,
            Action? afterDialog = null)
        {
            _gitService = gitService;
            _fileService = fileService;
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _statusGitBranch = statusGitBranch;
            _repoPathProvider = repoPathProvider;
            _xamlRootProvider = xamlRootProvider;
            _getString = getString;
            _isGitNotDetected = isGitNotDetected;
            _showError = showError;
            _startAutoRefresh = startAutoRefresh;
            _openCompareTabAsync = openCompareTabAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            _leftSidebar.GitChangedFiles.ItemsSource = _viewModel.GitFiles;
            WireEvents();
        }

        public Task RefreshAsync()
        {
            return RefreshAsync(_repoPathProvider());
        }

        public async Task RefreshAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                string notDetected = _getString("GitNotDetected", "Git: 감지 안됨");
                _leftSidebar.GitPanelBranch.Text = notDetected;
                _statusGitBranch.Text = notDetected;
                _viewModel.GitFiles.Clear();
                _leftSidebar.GitBranches.Items.Clear();
                _leftSidebar.GitHistory.Items.Clear();
                return;
            }

            string branch = await _gitService.GetCurrentBranchAsync(repoPath);
            string localizedBranch = _isGitNotDetected(branch) ? _getString("GitNotDetected", "Git: 감지 안됨") : branch;
            _leftSidebar.GitPanelBranch.Text = localizedBranch;
            _statusGitBranch.Text = localizedBranch;
            _startAutoRefresh();

            _viewModel.GitFiles.Clear();
            _leftSidebar.GitBranches.Items.Clear();
            int selectedIndex = -1;
            int i = 0;
            foreach (var branchName in await _gitService.GetBranchesAsync(repoPath))
            {
                string cleanedBranchName = branchName.Trim();
                _leftSidebar.GitBranches.Items.Add(cleanedBranchName);
                if (cleanedBranchName.StartsWith("*"))
                {
                    selectedIndex = i;
                }
                i++;
            }
            if (selectedIndex >= 0)
            {
                _leftSidebar.GitBranches.SelectedIndex = selectedIndex;
            }

            _leftSidebar.GitHistory.Items.Clear();
            foreach (var history in await _gitService.GetRecentHistoryAsync(repoPath))
            {
                _leftSidebar.GitHistory.Items.Add(history);
            }

            var fileStatuses = await _gitService.GetFileStatusesAsync(repoPath);
            foreach (var kvp in fileStatuses)
            {
                _viewModel.GitFiles.Add(CreateGitFileItem(kvp.Key, kvp.Value));
            }
        }

        public async Task StageAllAsync(string repoPath)
        {
            bool success = await _gitService.StageAllAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError("Git Stage 실패", "전체 Stage 처리에 실패했습니다.");
            }
        }

        public async Task ToggleStageAsync(object sender, string repoPath)
        {
            if (sender is not Button { Tag: string filePath })
            {
                return;
            }

            var item = _viewModel.GitFiles.FirstOrDefault(f => f.Path == filePath);
            if (item == null)
            {
                return;
            }

            bool success = item.IsStaged
                ? await _gitService.UnstageFileAsync(repoPath, filePath)
                : await _gitService.StageFileAsync(repoPath, filePath);

            if (success)
            {
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError("Git Stage 변경 실패", "Git CLI 명령 처리에 실패했습니다.");
            }
        }

        public async Task OpenChangedFileDiffAsync(string repoPath)
        {
            if (_leftSidebar.GitChangedFiles.SelectedItem is not GitFileItem item)
            {
                return;
            }

            string originalContent = await _gitService.GetGitFileContentAsync(repoPath, item.Path);
            string currentContent = File.Exists(item.Path)
                ? await _fileService.ReadTextFileAsync(item.Path)
                : string.Empty;

            string fileName = Path.GetFileName(item.Path);
            await _openCompareTabAsync(
                item.Path,
                item.Path,
                originalContent,
                currentContent,
                string.Format(_getString("GitCompareDiffTitleFormat", "Git 비교: {0}"), fileName),
                string.Format(_getString("GitPreviousVersionFormat", "{0} (이전 버전)"), fileName),
                string.Format(_getString("GitCurrentChangesFormat", "{0} (현재 변경 사항)"), fileName));
        }

        public async Task RestoreFileAsync(object sender, string repoPath)
        {
            if (sender is not Button { Tag: string filePath })
            {
                return;
            }

            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = "Git 파일 복원",
                Content = $"{Path.GetFileName(filePath)} 변경 사항을 복원합니다. Untracked 파일은 삭제됩니다.",
                PrimaryButtonText = "복원",
                CloseButtonText = "취소",
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _afterDialog?.Invoke();
                return;
            }
            _afterDialog?.Invoke();

            bool success = await _gitService.RestoreFileAsync(repoPath, filePath);
            if (success)
            {
                await RefreshAsync(repoPath);
                FileRestored?.Invoke(this, filePath);
            }
            else
            {
                _showError("Git Restore 실패", "파일 복원 처리에 실패했습니다.");
            }
        }

        public async Task CommitAsync(string repoPath)
        {
            string message = _leftSidebar.GitCommitMessage.Text;
            if (string.IsNullOrEmpty(message))
            {
                _showError("Git 커밋", "커밋 메시지를 채워주십시오.");
                return;
            }

            bool success = await _gitService.CommitAsync(repoPath, message);
            if (success)
            {
                _leftSidebar.GitCommitMessage.Text = string.Empty;
                await RefreshAsync(repoPath);
                _showError(
                    _getString("GitCommitSuccessTitle", "Git 커밋"),
                    _getString("GitCommitSuccessMessage", "성공적으로 커밋 완료되었습니다!"));
            }
            else
            {
                _showError("Git 커밋 실패", "커밋 도중 에러가 났습니다. 변경 조각(Staged)이 등록되었는지 확인하십시오.");
            }
        }

        public async Task PushAsync(string repoPath)
        {
            bool success = await _gitService.PushAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
                _showError("Git Push", "Push가 완료되었습니다.");
            }
            else
            {
                _showError("Git Push 실패", "Push 처리에 실패했습니다. 원격 저장소/인증/업스트림 설정을 확인하십시오.");
            }
        }

        public async Task RestoreAllAsync(string repoPath)
        {
            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = "Git 전체 복원",
                Content = "모든 변경 사항을 복원합니다. Untracked 파일은 삭제됩니다.",
                PrimaryButtonText = "전체 복원",
                CloseButtonText = "취소",
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _afterDialog?.Invoke();
                return;
            }
            _afterDialog?.Invoke();

            bool success = await _gitService.RestoreAllAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
                FileRestored?.Invoke(this, string.Empty);
            }
            else
            {
                _showError("Git Restore 실패", "전체 복원 처리에 실패했습니다.");
            }
        }

        public async Task ShowHistoryItemAsync(string repoPath)
        {
            if (_leftSidebar.GitHistory.SelectedItem is not string historyItem || string.IsNullOrEmpty(repoPath))
            {
                return;
            }

            var match = Regex.Match(historyItem, @"\b[0-9a-fA-F]{7,40}\b");
            if (!match.Success)
            {
                return;
            }

            string hash = match.Value;
            try
            {
                string commitInfo = await _gitService.RunGitCommandAsync(repoPath, $"show --quiet --format=fuller {hash}");
                var changedFiles = await _gitService.GetCommitChangedFilesAsync(repoPath, hash);
                await ShowCommitDialogAsync(repoPath, hash, commitInfo, changedFiles);
            }
            catch (Exception ex)
            {
                _showError("커밋 상세 조회 실패", ex.Message);
            }
        }

        private async Task ShowCommitDialogAsync(
            string repoPath,
            string hash,
            string commitInfo,
            System.Collections.Generic.IReadOnlyList<(string Status, string Path)> changedFiles)
        {
            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            var dialog = new ContentDialog
            {
                Title = string.Format(_getString("GitHistoryItemDialogTitle", "커밋 정보 [{0}]"), hash.Substring(0, 7)),
                CloseButtonText = _getString("GitHistoryItemClose", "닫기"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            var fileListView = BuildCommitChangedFilesList(changedFiles, isDarkTheme);
            string currentHash = hash;
            fileListView.DoubleTapped += async (_, _) =>
            {
                if (fileListView.SelectedItem is ListViewItem clickedItem && clickedItem.Tag is ValueTuple<string, string> fileTuple)
                {
                    dialog.Hide();
                    await OpenCommitFileCompareAsync(repoPath, currentHash, fileTuple.Item1, fileTuple.Item2);
                }
            };

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8),
                Content = new TextBlock
                {
                    Text = commitInfo,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            });
            stack.Children.Add(new TextBlock
            {
                Text = _getString("GitHistoryItemDialogHeader", "변경된 파일 목록 (더블클릭 시 비교 뷰어 열림):"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 5)
            });
            stack.Children.Add(fileListView);

            dialog.Content = stack;
            _beforeDialog?.Invoke();
            await dialog.ShowAsync();
            _afterDialog?.Invoke();
        }

        private ListView BuildCommitChangedFilesList(System.Collections.Generic.IEnumerable<(string Status, string Path)> changedFiles, bool isDarkTheme)
        {
            var fileListView = new ListView
            {
                Height = 220,
                SelectionMode = ListViewSelectionMode.Single,
                Margin = new Thickness(0, 5, 0, 0)
            };

            foreach (var file in changedFiles)
            {
                fileListView.Items.Add(new ListViewItem
                {
                    Content = CreateCommitFileRow(file.Status, file.Path, isDarkTheme),
                    Tag = file
                });
            }

            return fileListView;
        }

        private static Grid CreateCommitFileRow(string status, string path, bool isDarkTheme)
        {
            Windows.UI.Color statusColor;
            if (isDarkTheme)
            {
                // Soft, desaturated premium colors for Dark Theme (GitHub style)
                statusColor = status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                    ? Windows.UI.Color.FromArgb(255, 63, 185, 80)    // soft green (#3fb950)
                    : status.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                        ? Windows.UI.Color.FromArgb(255, 248, 81, 73)   // soft red (#f85149)
                        : Windows.UI.Color.FromArgb(255, 88, 166, 255);  // soft blue (#58a6ff)
            }
            else
            {
                // Harmonious professional colors for Light Theme
                statusColor = status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                    ? Windows.UI.Color.FromArgb(255, 34, 134, 58)    // desaturated dark green (#22863a)
                    : status.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                        ? Windows.UI.Color.FromArgb(255, 203, 36, 49)   // desaturated dark red (#cb2431)
                        : Windows.UI.Color.FromArgb(255, 3, 102, 214);   // elegant blue (#0366d6)
            }

            var grid = new Grid { Padding = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var border = new Border
            {
                Background = new SolidColorBrush(statusColor),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = status,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                }
            };

            var pathText = new TextBlock
            {
                Text = path,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(border, 0);
            Grid.SetColumn(pathText, 1);
            grid.Children.Add(border);
            grid.Children.Add(pathText);
            return grid;
        }

        private async Task OpenCommitFileCompareAsync(string repoPath, string currentHash, string status, string relativePath)
        {
            string fullPath = Path.Combine(repoPath, relativePath);
            string parentHash = $"{currentHash}^";
            string contentA = string.Empty;
            string contentB = string.Empty;

            if (!status.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                contentA = await _gitService.GetCommitFileContentAsync(repoPath, parentHash, relativePath);
            }

            if (!status.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                contentB = await _gitService.GetCommitFileContentAsync(repoPath, currentHash, relativePath);
            }

            string fileName = Path.GetFileName(relativePath);
            string shortHash = currentHash.Substring(0, 7);
            await _openCompareTabAsync(
                fullPath,
                fullPath,
                contentA,
                contentB,
                string.Format(_getString("GitCompareTitleFormat", "비교 [{0}]: {1}"), shortHash, fileName),
                string.Format(_getString("GitPreviousCommitFormat", "{0} (이전 커밋)"), fileName),
                string.Format(_getString("GitCommitHashFormat", "{0} (커밋 {1})"), fileName, shortHash));
        }

        private static GitFileItem CreateGitFileItem(string fullPath, string status)
        {
            bool isStaged = status.Length > 0 && status[0] != ' ' && status != "??";
            bool isUnstaged = status.Length > 1 && status[1] != ' ';
            string statusDesc = isStaged ? "Staged" : "Unstaged";
            if (status == "??") statusDesc = "Untracked";
            else if (status.Contains("D")) statusDesc = isStaged ? "Deleted staged" : "Deleted";
            else if (status.Contains("R")) statusDesc = "Renamed";
            else if (status.Contains("A")) statusDesc = isStaged ? "Added staged" : "Added";
            else if (isStaged && isUnstaged) statusDesc = "Staged + Unstaged";

            return new GitFileItem
            {
                Name = Path.GetFileName(fullPath),
                Path = fullPath,
                StatusText = $"{statusDesc} ({status.Trim()})",
                ActionGlyph = isStaged ? "\xE108" : "\xE109",
                IsStaged = isStaged
            };
        }

        private void WireEvents()
        {
            _leftSidebar.GitFileDoubleTapped += OnGitFileDoubleTapped;
            _leftSidebar.GitStageToggleClick += OnGitStageToggleClick;
            _leftSidebar.GitRestoreFileClick += OnGitRestoreFileClick;
            _leftSidebar.GitCommitClick += OnGitCommitClick;
            _leftSidebar.GitStageAllClick += OnGitStageAllClick;
            _leftSidebar.GitRestoreAllClick += OnGitRestoreAllClick;
            _leftSidebar.GitPushClick += OnGitPushClick;
            _leftSidebar.GitRefreshClick += OnGitRefreshClick;
            _leftSidebar.GitHistoryItemDoubleTapped += OnGitHistoryItemDoubleTapped;
        }

        private async void OnGitStageAllClick(object sender, RoutedEventArgs e)
        {
            await StageAllAsync(_repoPathProvider());
        }

        private async void OnGitStageToggleClick(object sender, RoutedEventArgs e)
        {
            await ToggleStageAsync(sender, _repoPathProvider());
        }

        private async void OnGitFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            await OpenChangedFileDiffAsync(_repoPathProvider());
        }

        private async void OnGitRestoreFileClick(object sender, RoutedEventArgs e)
        {
            await RestoreFileAsync(sender, _repoPathProvider());
        }

        private async void OnGitCommitClick(object sender, RoutedEventArgs e)
        {
            await CommitAsync(_repoPathProvider());
        }

        private async void OnGitPushClick(object sender, RoutedEventArgs e)
        {
            await PushAsync(_repoPathProvider());
        }

        private async void OnGitRestoreAllClick(object sender, RoutedEventArgs e)
        {
            await RestoreAllAsync(_repoPathProvider());
        }

        private async void OnGitRefreshClick(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async void OnGitHistoryItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            await ShowHistoryItemAsync(_repoPathProvider());
        }
    }
}
