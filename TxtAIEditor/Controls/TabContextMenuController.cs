using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        private readonly Func<OpenedTab, Task> _openHexViewAsync;
        private readonly Func<OpenedTab, Task> _encryptTabAsync;
        private readonly Func<OpenedTab, Task> _changeEncryptionPasswordAsync;
        private readonly Func<OpenedTab, Task> _removeEncryptionAsync;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeRightTabs;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeLeftTabs;
        private readonly Action<OpenedTab, TabViewItem, TabView> _closeOtherTabs;
        private readonly Func<TabViewItem, TabView?> _tabViewResolver;

        public TabContextMenuController(
            FavoritesRecentController favoritesRecentController,
            Func<string, string, string> getString,
            Action<int> showLeftSidebarPage,
            Func<string, Task> navigateExplorerToFolderAsync,
            Func<OpenedTab, TabViewItem, Task> reloadTabAsync,
            Func<OpenedTab, Task> openHexViewAsync,
            Func<OpenedTab, Task> encryptTabAsync,
            Func<OpenedTab, Task> changeEncryptionPasswordAsync,
            Func<OpenedTab, Task> removeEncryptionAsync,
            Action<OpenedTab, TabViewItem, TabView> closeRightTabs,
            Action<OpenedTab, TabViewItem, TabView> closeLeftTabs,
            Action<OpenedTab, TabViewItem, TabView> closeOtherTabs,
            Func<TabViewItem, TabView?> tabViewResolver)
        {
            _favoritesRecentController = favoritesRecentController;
            _getString = getString;
            _showLeftSidebarPage = showLeftSidebarPage;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _reloadTabAsync = reloadTabAsync;
            _openHexViewAsync = openHexViewAsync;
            _encryptTabAsync = encryptTabAsync;
            _changeEncryptionPasswordAsync = changeEncryptionPasswordAsync;
            _removeEncryptionAsync = removeEncryptionAsync;
            _closeRightTabs = closeRightTabs;
            _closeLeftTabs = closeLeftTabs;
            _closeOtherTabs = closeOtherTabs;
            _tabViewResolver = tabViewResolver;
        }

        public MenuFlyout CreateContextFlyout(OpenedTab tab, TabViewItem tabItem, TabView targetTabView)
        {
            var menu = new MenuFlyout();
            string? fileActionPath = GetFileActionPath(tab);

            var copyFileNameItem = new MenuFlyoutItem { Text = _getString("TabMenuCopyFileName", "파일이름 복사"), Icon = new SymbolIcon(Symbol.Copy) };
            copyFileNameItem.IsEnabled = !string.IsNullOrEmpty(fileActionPath);
            copyFileNameItem.Click += (_, __) => CopyFileName(tab);
            menu.Items.Add(copyFileNameItem);

            var copyFilePathItem = new MenuFlyoutItem { Text = _getString("TabMenuCopyFilePath", "경로 복사"), Icon = new SymbolIcon(Symbol.Link) };
            copyFilePathItem.IsEnabled = !string.IsNullOrEmpty(fileActionPath);
            copyFilePathItem.Click += (_, __) => CopyFilePath(tab);
            menu.Items.Add(copyFilePathItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var addToFavoritesItem = new MenuFlyoutItem { Text = _getString("TabMenuAddToFavorites", "즐겨찾기 추가"), Icon = new SymbolIcon(Symbol.Favorite) };
            addToFavoritesItem.IsEnabled = !string.IsNullOrEmpty(fileActionPath);
            addToFavoritesItem.Click += async (_, __) => await AddBookmarkAsync(tab);
            menu.Items.Add(addToFavoritesItem);

            var openFolderItem = new MenuFlyoutItem { Text = _getString("TabMenuOpenFolder", "해당 폴더로 이동"), Icon = new SymbolIcon(Symbol.Folder) };
            openFolderItem.Click += async (_, __) => await OpenFolderAsync(tab);
            menu.Items.Add(openFolderItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var reloadItem = new MenuFlyoutItem { Text = _getString("TabMenuReload", "새로고침"), Icon = new SymbolIcon(Symbol.Refresh) };
            reloadItem.IsEnabled = !string.IsNullOrEmpty(fileActionPath);
            reloadItem.Click += async (_, __) => await _reloadTabAsync(tab, tabItem);
            menu.Items.Add(reloadItem);

            var hexViewItem = new MenuFlyoutItem
            {
                Text = _getString("TabMenuHexView", "Hex 보기"),
                Icon = new FontIcon
                {
                    Glyph = "H",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                }
            };
            hexViewItem.IsEnabled = !tab.IsHexViewer &&
                                    !string.IsNullOrEmpty(fileActionPath) &&
                                    File.Exists(fileActionPath);
            hexViewItem.Click += async (_, __) => await _openHexViewAsync(tab);
            menu.Items.Add(hexViewItem);

            if (!tab.IsReadOnlyViewer)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                if (tab.IsEncrypted)
                {
                    var changePasswordItem = new MenuFlyoutItem { Text = _getString("TabMenuChangeEncryptionPassword", "암호 변경"), Icon = new SymbolIcon(Symbol.Permissions) };
                    changePasswordItem.Click += async (_, __) => await _changeEncryptionPasswordAsync(tab);
                    menu.Items.Add(changePasswordItem);

                    var removeEncryptionItem = new MenuFlyoutItem { Text = _getString("TabMenuRemoveEncryption", "암호 해제"), Icon = new SymbolIcon(Symbol.Permissions) };
                    removeEncryptionItem.Click += async (_, __) => await _removeEncryptionAsync(tab);
                    menu.Items.Add(removeEncryptionItem);
                }
                else
                {
                    var encryptItem = new MenuFlyoutItem { Text = _getString("TabMenuEncrypt", "암호화"), Icon = new SymbolIcon(Symbol.Permissions) };
                    encryptItem.Click += async (_, __) => await _encryptTabAsync(tab);
                    menu.Items.Add(encryptItem);
                }
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var closeRightItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseRight", "오른쪽 탭 닫기"), Icon = new SymbolIcon(Symbol.Forward) };
            closeRightItem.Click += (_, __) => _closeRightTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeRightItem);

            var closeLeftItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseLeft", "왼쪽 탭 닫기"), Icon = new SymbolIcon(Symbol.Back) };
            closeLeftItem.Click += (_, __) => _closeLeftTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeLeftItem);

            var closeOthersItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseOthers", "다른 탭 닫기"), Icon = new SymbolIcon(Symbol.Cancel) };
            closeOthersItem.Click += (_, __) => _closeOtherTabs(tab, tabItem, targetTabView);
            menu.Items.Add(closeOthersItem);

            return menu;
        }

        public void ShowContextMenu(
            OpenedTab tab,
            TabViewItem tabItem,
            TabView fallbackTabView,
            FrameworkElement target,
            RightTappedRoutedEventArgs args)
        {
            args.Handled = true;

            var ownerTabView = _tabViewResolver(tabItem) ?? fallbackTabView;
            var flyout = CreateContextFlyout(tab, tabItem, ownerTabView);
            CursorResetHelper.AttachToFlyout(flyout, target);
            CursorResetHelper.ResetToArrow(target);
            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = args.GetPosition(target)
            });
            CursorResetHelper.ResetToArrow(target);
        }

        private static void CopyFileName(OpenedTab tab)
        {
            string? filePath = GetFileActionPath(tab);
            if (!string.IsNullOrEmpty(filePath))
            {
                SetClipboardText(Path.GetFileName(filePath));
            }
        }

        private static void CopyFilePath(OpenedTab tab)
        {
            string? filePath = GetFileActionPath(tab);
            if (!string.IsNullOrEmpty(filePath))
            {
                SetClipboardText(filePath);
            }
        }

        private async Task AddBookmarkAsync(OpenedTab tab)
        {
            string? filePath = GetFileActionPath(tab);
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            await _favoritesRecentController.AddFavoritePathAsync(filePath, true);
            _showLeftSidebarPage(1);
        }

        private async Task OpenFolderAsync(OpenedTab tab)
        {
            string? filePath = GetFileActionPath(tab);
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            string? folderPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                await _navigateExplorerToFolderAsync(folderPath);
            }
        }

        private static string? GetFileActionPath(OpenedTab tab)
        {
            return !string.IsNullOrWhiteSpace(tab.FilePath)
                ? tab.FilePath
                : tab.HexSourceFilePath;
        }

        private static void SetClipboardText(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
    }
}
