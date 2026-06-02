using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerFileActionsController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Func<string> _currentFolderProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Action<OpenedTab, TabViewItem> _closeTabAndCleanup;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<ElementTheme> _themeProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly ConditionalWeakTable<MenuFlyout, object> _localizedFlyouts = new ConditionalWeakTable<MenuFlyout, object>();

        public ExplorerFileActionsController(
            LeftSidebarPane leftSidebar,
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Func<string> currentFolderProvider,
            Action<string> loadDirectoryRoot,
            Func<string, Task> loadFileIntoTabAsync,
            Action<OpenedTab, TabViewItem> closeTabAndCleanup,
            Func<XamlRoot> xamlRootProvider,
            Func<ElementTheme> themeProvider,
            Func<string, string, string> getString,
            Action<string, string> showError,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal)
        {
            _leftSidebar = leftSidebar;
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _currentFolderProvider = currentFolderProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _closeTabAndCleanup = closeTabAndCleanup;
            _xamlRootProvider = xamlRootProvider;
            _themeProvider = themeProvider;
            _getString = getString;
            _showError = showError;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;

            WireEvents();
        }

        private void WireEvents()
        {
            _leftSidebar.FileListViewItemRightTapped += OnFileListViewItemRightTapped;
            _leftSidebar.CopyFileNameClick += OnCopyFileNameClick;
            _leftSidebar.CopyFilePathClick += OnCopyFilePathClick;
            _leftSidebar.CopyFolderPathClick += OnCopyFolderPathClick;
            _leftSidebar.RenameClick += OnRenameClick;
            _leftSidebar.DeleteClick += OnDeleteClick;
        }

        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ExplorerItem item })
            {
                _leftSidebar.FileList.SelectedItem = item;
            }

            if (sender is FrameworkElement element && element.ContextFlyout is MenuFlyout flyout && flyout.Items.Count >= 9)
            {
                LocalizeContextFlyout(flyout);
            }

            e.Handled = true;
        }

        private void LocalizeContextFlyout(MenuFlyout flyout)
        {
            if (_localizedFlyouts.TryGetValue(flyout, out _))
            {
                return;
            }

            _localizedFlyouts.Add(flyout, null!);
            ((MenuFlyoutItem)flyout.Items[0]).Text = _getString("ExplorerAddToFavorites", "즐겨찾기에 추가");
            ((MenuFlyoutItem)flyout.Items[1]).Text = _getString("ExplorerAddFolderToFavorites", "폴더를 즐겨찾기에 추가");
            ((MenuFlyoutItem)flyout.Items[3]).Text = _getString("ExplorerCopyFileName", "파일이름 복사");
            ((MenuFlyoutItem)flyout.Items[4]).Text = _getString("ExplorerCopyFilePath", "경로 복사");
            ((MenuFlyoutItem)flyout.Items[5]).Text = _getString("ExplorerCopyFolderPath", "폴더 경로 복사");
            ((MenuFlyoutItem)flyout.Items[7]).Text = _getString("ExplorerRename", "이름 바꾸기");
            ((MenuFlyoutItem)flyout.Items[8]).Text = _getString("ExplorerDelete", "삭제");
        }

        private void OnCopyFileNameClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.Name);
            }
        }

        private void OnCopyFilePathClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.Path);
            }
        }

        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                string folderPath = item.IsFolder ? item.Path : Path.GetDirectoryName(item.Path) ?? string.Empty;
                SetClipboardText(folderPath);
            }
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            string oldPath = item.Path;
            string parentDir = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string oldName = item.Name;

            var textBox = new TextBox
            {
                Text = oldName,
                SelectionStart = 0,
                SelectionLength = Path.GetFileNameWithoutExtension(oldName).Length
            };

            var dialog = new ContentDialog
            {
                Title = _getString("RenameDialogTitle", "이름 바꾸기"),
                Content = textBox,
                PrimaryButtonText = _getString("RenameDialogOK", "확인"),
                CloseButtonText = _getString("RenameDialogCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = textBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName)
            {
                return;
            }

            string newPath = Path.Combine(parentDir, newName);

            try
            {
                if (item.IsFolder)
                {
                    Directory.Move(oldPath, newPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                    CloseOpenTabsForPath(oldPath);
                    await _loadFileIntoTabAsync(newPath);
                }

                _loadDirectoryRoot(_currentFolderProvider());
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("RenameErrorTitle", "이름 바꾸기 오류"),
                    ex.Message);
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            var confirmDialog = new ContentDialog
            {
                Title = _getString("DeleteConfirmTitle", "삭제 확인"),
                Content = string.Format(
                    _getString("DeleteConfirmMessage", "'{0}'을(를) 휴지통으로 이동하시겠습니까?"),
                    item.Name),
                PrimaryButtonText = _getString("DeleteConfirmOK", "삭제"),
                CloseButtonText = _getString("DeleteConfirmCancel", "취소"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                CloseOpenTabsForPath(item.Path);

                if (item.IsFolder)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        item.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        item.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }

                _loadDirectoryRoot(_currentFolderProvider());
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("DeleteErrorTitle", "삭제 오류"),
                    ex.Message);
            }
        }

        private ExplorerItem? GetExplorerItem(object sender)
        {
            if (sender is FrameworkElement element)
            {
                if (element.DataContext is ExplorerItem dataContextItem)
                {
                    return dataContextItem;
                }

                if (element.Tag is ExplorerItem tagItem)
                {
                    return tagItem;
                }
            }

            return _leftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                if (terminalWasVisible)
                {
                    _resumeTerminal();
                }
            }
        }

        private void CloseOpenTabsForPath(string path)
        {
            var tabsToClose = _viewModel.Tabs
                .Where(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var tab in tabsToClose)
            {
                var tabItem = FindTabItem(tab.Id);
                if (tabItem != null)
                {
                    _closeTabAndCleanup(tab, tabItem);
                }
            }
        }

        private TabViewItem? FindTabItem(string tabId)
        {
            return _primaryTabView.TabItems.Cast<TabViewItem>()
                .FirstOrDefault(t => t.Tag as string == tabId)
                ?? _secondaryTabView.TabItems.Cast<TabViewItem>()
                    .FirstOrDefault(t => t.Tag as string == tabId);
        }

        private static void SetClipboardText(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
    }
}
