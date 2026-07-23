using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed class WindowTitleController
    {
        private readonly Window _window;
        private readonly TextBlock _titleTextBlock;
        private readonly Func<OpenedTab?> _activeTabProvider;

        public WindowTitleController(
            Window window,
            TextBlock titleTextBlock,
            Func<OpenedTab?> activeTabProvider)
        {
            _window = window;
            _titleTextBlock = titleTextBlock;
            _activeTabProvider = activeTabProvider;
        }

        public void Update()
        {
            var activeTab = _activeTabProvider();
            string pathOrTitle = activeTab != null
                ? (!string.IsNullOrWhiteSpace(activeTab.RemotePath)
                    ? RemotePath.GetDisplayPath(activeTab.RemotePath)
                    : !string.IsNullOrEmpty(activeTab.FilePath)
                        ? activeTab.FilePath
                        : activeTab.Title)
                : string.Empty;

            string newTitle = string.IsNullOrEmpty(pathOrTitle)
                ? "TxtAIEditor"
                : $"TxtAIEditor - {pathOrTitle}";

            _window.Title = newTitle;
            _titleTextBlock.Text = newTitle;
        }
    }
}
