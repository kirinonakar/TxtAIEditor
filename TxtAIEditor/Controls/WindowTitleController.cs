using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class WindowTitleController
    {
        private readonly Window _window;
        private readonly TextBlock _titleTextBlock;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;

        public WindowTitleController(
            Window window,
            TextBlock titleTextBlock,
            Func<OpenedTab?> activeTabProvider,
            RemoteWorkspaceService remoteWorkspaceService)
        {
            _window = window;
            _titleTextBlock = titleTextBlock;
            _activeTabProvider = activeTabProvider;
            _remoteWorkspaceService = remoteWorkspaceService;
        }

        public void Update()
        {
            var activeTab = _activeTabProvider();
            string pathOrTitle = GetDisplayPath(activeTab);

            string newTitle = string.IsNullOrEmpty(pathOrTitle)
                ? "TxtAIEditor"
                : $"TxtAIEditor - {pathOrTitle}";

            _window.Title = newTitle;
            _titleTextBlock.Text = newTitle;
        }

        private string GetDisplayPath(OpenedTab? tab)
        {
            if (tab == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(tab.RemotePath))
            {
                return RemotePath.GetDisplayPath(tab.RemotePath);
            }

            if (tab.IsArchiveEntry &&
                !string.IsNullOrWhiteSpace(tab.ArchiveSourcePath) &&
                !string.IsNullOrWhiteSpace(tab.ArchiveEntryPath) &&
                _remoteWorkspaceService.TryGetVirtualPath(
                    tab.ArchiveSourcePath,
                    out string remoteArchivePath))
            {
                string archivePath =
                    _remoteWorkspaceService.GetDisplayPath(remoteArchivePath);
                string entryPath =
                    ArchiveExplorerService.NormalizeEntryPath(tab.ArchiveEntryPath);
                return $"{archivePath}!/{entryPath}";
            }

            return !string.IsNullOrEmpty(tab.FilePath)
                ? tab.FilePath
                : tab.Title;
        }
    }
}
