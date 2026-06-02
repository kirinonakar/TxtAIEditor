using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class MarkdownToolbarController
    {
        private readonly TopCommandBarPane _topToolbar;
        private readonly MarkdownToolbarControl _markdownToolbar;
        private readonly TabView _editorTabView;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Func<string, Task<bool>> _insertTextIntoActiveEditorAsync;
        private readonly Action<string, string> _showError;

        public MarkdownToolbarController(
            TopCommandBarPane topToolbar,
            MarkdownToolbarControl markdownToolbar,
            TabView editorTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<string, Task> loadFileIntoTabAsync,
            Func<string, Task<bool>> insertTextIntoActiveEditorAsync,
            Action<string, string> showError)
        {
            _topToolbar = topToolbar;
            _markdownToolbar = markdownToolbar;
            _editorTabView = editorTabView;
            _tabBridges = tabBridges;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _insertTextIntoActiveEditorAsync = insertTextIntoActiveEditorAsync;
            _showError = showError;

            _markdownToolbar.CommandRequested += OnMarkdownToolbarCommandRequested;
            _topToolbar.ToggleMarkdownToolbarClick += OnToggleMarkdownToolbarClick;
        }

        public async Task ApplyCommandToActiveEditorAsync(string command, string? color = null)
        {
            if (_editorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup) &&
                bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.ApplyMarkdownCommandAsync(command, color);
            }
        }

        public void ApplyVisibility(bool show)
        {
            _markdownToolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnMarkdownToolbarCommandRequested(object? sender, MarkdownCommandRequestedEventArgs e)
        {
            if (e.Command == "charmap")
            {
                OpenCharacterMap();
                return;
            }

            if (e.Command == "emoji")
            {
                await OpenEmojiReferenceAsync();
                return;
            }

            if (e.Command == "currentDate")
            {
                await _insertTextIntoActiveEditorAsync(DateTime.Now.ToString("yyyy-MM-dd"));
                return;
            }

            await ApplyCommandToActiveEditorAsync(e.Command, e.Color);
        }

        private void OnToggleMarkdownToolbarClick(object sender, RoutedEventArgs e)
        {
            ApplyVisibility(_topToolbar.MarkdownToolbarIsChecked);
        }

        private void OpenCharacterMap()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "charmap.exe",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _showError("문자표 실행 실패", ex.Message);
            }
        }

        private async Task OpenEmojiReferenceAsync()
        {
            try
            {
                string emojiFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "md", "standard-unicode-emoji-17-no-private.md");
                if (File.Exists(emojiFilePath))
                {
                    await _loadFileIntoTabAsync(emojiFilePath);
                }
            }
            catch (Exception ex)
            {
                _showError("이모지 파일 열기 실패", ex.Message);
            }
        }
    }
}
