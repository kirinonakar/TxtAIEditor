using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    public sealed class TabContextMenuController
    {
        private readonly FavoritesRecentController _favoritesRecentController;
        private readonly Func<string, string, string> _getString;
        private readonly Action<int> _showLeftSidebarPage;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;
        private readonly Func<OpenedTab, TabViewItem, Task> _reloadTabAsync;
        private readonly Action<OpenedTab, TabViewItem, bool> _toggleLivePreview;
        private readonly Func<OpenedTab, Task> _encryptTabAsync;
        private readonly Func<OpenedTab, Task> _changeEncryptionPasswordAsync;
        private readonly Func<OpenedTab, Task> _removeEncryptionAsync;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeRightTabs;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeLeftTabs;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeOtherTabs;

        public TabContextMenuController(
            FavoritesRecentController favoritesRecentController,
            Func<string, string, string> getString,
            Action<int> showLeftSidebarPage,
            Func<string, Task> navigateExplorerToFolderAsync,
            Func<OpenedTab, TabViewItem, Task> reloadTabAsync,
            Action<OpenedTab, TabViewItem, bool> toggleLivePreview,
            Func<OpenedTab, Task> encryptTabAsync,
            Func<OpenedTab, Task> changeEncryptionPasswordAsync,
            Func<OpenedTab, Task> removeEncryptionAsync,
            Action<OpenedTab, TabViewItem, TabView> closeRightTabs,
            Action<OpenedTab, TabViewItem, TabView> closeLeftTabs,
            Action<OpenedTab, TabViewItem, TabView> closeOtherTabs)
        {
            _favoritesRecentController = favoritesRecentController;
            _getString = getString;
            _showLeftSidebarPage = showLeftSidebarPage;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _reloadTabAsync = reloadTabAsync;
            _toggleLivePreview = toggleLivePreview;
            _encryptTabAsync = encryptTabAsync;
            _changeEncryptionPasswordAsync = changeEncryptionPasswordAsync;
            _removeEncryptionAsync = removeEncryptionAsync;
            _closeRightTabs = closeRightTabs;
            _closeLeftTabs = closeLeftTabs;
            _closeOtherTabs = closeOtherTabs;
        }

        public MenuFlyout CreateContextFlyout(OpenedTab tab, TabViewItem tabItem, TabView targetTabView)
        {
            var menu = new MenuFlyout();

            var copyFileNameItem = new MenuFlyoutItem { Text = _getString("TabMenuCopyFileName", "파일이름 복사") };
            copyFileNameItem.IsEnabled = !string.IsNullOrEmpty(tab.FilePath);
            copyFileNameItem.Click += (_, __) => CopyFileName(tab);
            menu.Items.Add(copyFileNameItem);

            var copyFilePathItem = new MenuFlyoutItem { Text = _getString("TabMenuCopyFilePath", "경로 복사") };
            copyFilePathItem.IsEnabled = !string.IsNullOrEmpty(tab.FilePath);
            copyFilePathItem.Click += (_, __) => CopyFilePath(tab);
            menu.Items.Add(copyFilePathItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addToFavoritesItem = new MenuFlyoutItem { Text = _getString("TabMenuAddToFavorites", "즐겨찾기 추가") };
            addToFavoritesItem.IsEnabled = !string.IsNullOrEmpty(tab.FilePath);
            addToFavoritesItem.Click += async (_, __) => await AddBookmarkAsync(tab);
            menu.Items.Add(addToFavoritesItem);

            var openFolderItem = new MenuFlyoutItem { Text = _getString("TabMenuOpenFolder", "해당 폴더로 이동") };
            openFolderItem.Click += async (_, __) => await OpenFolderAsync(tab);
            menu.Items.Add(openFolderItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var reloadItem = new MenuFlyoutItem { Text = _getString("TabMenuReload", "새로고침") };
            reloadItem.IsEnabled = !string.IsNullOrEmpty(tab.FilePath);
            reloadItem.Click += async (_, __) => await _reloadTabAsync(tab, tabItem);
            menu.Items.Add(reloadItem);

            if (!tab.IsReadOnlyViewer)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                if (tab.IsEncrypted)
                {
                    var changePasswordItem = new MenuFlyoutItem { Text = _getString("TabMenuChangeEncryptionPassword", "암호 변경") };
                    changePasswordItem.Click += async (_, __) => await _changeEncryptionPasswordAsync(tab);
                    menu.Items.Add(changePasswordItem);

                    var removeEncryptionItem = new MenuFlyoutItem { Text = _getString("TabMenuRemoveEncryption", "암호 해제") };
                    removeEncryptionItem.Click += async (_, __) => await _removeEncryptionAsync(tab);
                    menu.Items.Add(removeEncryptionItem);
                }
                else
                {
                    var encryptItem = new MenuFlyoutItem { Text = _getString("TabMenuEncrypt", "암호화") };
                    encryptItem.Click += async (_, __) => await _encryptTabAsync(tab);
                    menu.Items.Add(encryptItem);
                }

                menu.Items.Add(new MenuFlyoutSeparator());

                var livePreviewItem = new ToggleMenuFlyoutItem
                {
                    Text = _getString("TabMenuLivePreview", "라이브 프리뷰"),
                    IsChecked = tab.InlineLivePreviewEnabled
                };
                livePreviewItem.Click += (_, __) => _toggleLivePreview(tab, tabItem, livePreviewItem.IsChecked);
                menu.Items.Add(livePreviewItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var closeRightItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseRight", "오른쪽 탭 닫기") };
            closeRightItem.Click += (_, __) => _closeRightTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeRightItem);

            var closeLeftItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseLeft", "왼쪽 탭 닫기") };
            closeLeftItem.Click += (_, __) => _closeLeftTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeLeftItem);

            var closeOthersItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseOthers", "다른 탭 닫기") };
            closeOthersItem.Click += (_, __) => _closeOtherTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeOthersItem);

            return menu;
        }

        private static void CopyFileName(OpenedTab tab)
        {
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                SetClipboardText(Path.GetFileName(tab.FilePath));
            }
        }

        private static void CopyFilePath(OpenedTab tab)
        {
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                SetClipboardText(tab.FilePath);
            }
        }

        private async Task AddBookmarkAsync(OpenedTab tab)
        {
            if (string.IsNullOrEmpty(tab.FilePath))
            {
                return;
            }

            await _favoritesRecentController.AddFavoritePathAsync(tab.FilePath, true);
            _showLeftSidebarPage(1);
        }

        private async Task OpenFolderAsync(OpenedTab tab)
        {
            if (string.IsNullOrEmpty(tab.FilePath))
            {
                return;
            }

            string? folderPath = Path.GetDirectoryName(tab.FilePath);
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                await _navigateExplorerToFolderAsync(folderPath);
            }
        }

        private static void SetClipboardText(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
    }
}
