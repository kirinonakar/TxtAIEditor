using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class AgentFileWorkflowController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly TabCloseController _tabCloseController;
        private readonly SearchReplaceTabSyncController _tabSyncController;
        private readonly CompareTabController _compareTabController;
        private readonly Func<IReadOnlyList<AgentFileEditPreview>> _sessionEditsProvider;
        private readonly Func<ExplorerItem?> _selectedExplorerItemProvider;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<string> _currentRepoProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Action _queueGitStatusRefresh;
        private readonly Func<string, string, string> _getString;

        public AgentFileWorkflowController(
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabCloseController tabCloseController,
            SearchReplaceTabSyncController tabSyncController,
            CompareTabController compareTabController,
            Func<IReadOnlyList<AgentFileEditPreview>> sessionEditsProvider,
            Func<ExplorerItem?> selectedExplorerItemProvider,
            Func<string> currentFolderProvider,
            Func<string> currentRepoProvider,
            Action<string> loadDirectoryRoot,
            Action queueGitStatusRefresh,
            Func<string, string, string> getString)
        {
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _tabCloseController = tabCloseController;
            _tabSyncController = tabSyncController;
            _compareTabController = compareTabController;
            _sessionEditsProvider = sessionEditsProvider;
            _selectedExplorerItemProvider = selectedExplorerItemProvider;
            _currentFolderProvider = currentFolderProvider;
            _currentRepoProvider = currentRepoProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _queueGitStatusRefresh = queueGitStatusRefresh;
            _getString = getString;
        }

        public async Task HandleFileModifiedAsync(string filePath)
        {
            await _tabSyncController.HandleFileModifiedAsync(filePath);

            string currentFolder = _currentFolderProvider();
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                _loadDirectoryRoot(currentFolder);
            }

            _queueGitStatusRefresh();

            var edit = _sessionEditsProvider()
                .FirstOrDefault(e => string.Equals(e.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
            if (edit != null)
            {
                string title = $"{_getString("AgentDiffTitle", "Agent 변경 비교")}: {Path.GetFileName(edit.RelativePath)}";
                await _compareTabController.UpdateCompareTabIfOpenAsync(
                    title,
                    edit.FullPath,
                    edit.FullPath,
                    edit.OldContent,
                    edit.NewContent,
                    labelA: _getString("DiffOriginalLabel", "원본"),
                    labelB: _getString("DiffModifiedLabel", "수정본"));
            }
        }

        public Task OpenDiffViewAsync(AgentFileEditPreview preview)
        {
            return _compareTabController.OpenCompareTabAsync(
                preview.FullPath,
                preview.FullPath,
                preview.OldContent,
                preview.NewContent,
                customTitle: $"{_getString("AgentDiffTitle", "Agent 변경 비교")}: {Path.GetFileName(preview.RelativePath)}",
                labelA: _getString("DiffOriginalLabel", "원본"),
                labelB: _getString("DiffModifiedLabel", "수정본"));
        }

        public void CloseTabById(string tabId)
        {
            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null)
            {
                return;
            }

            var tabItem = FindTabItem(tab.Id);
            if (tabItem != null)
            {
                _tabCloseController.CloseAndCleanup(tab, tabItem);
            }
        }

        public async Task RevertTabOrFileAsync(string pathOrId, string oldContent, bool isNewFile)
        {
            var tab = _viewModel.Tabs.FirstOrDefault(t =>
                t.Id == pathOrId ||
                string.Equals(t.FilePath, pathOrId, StringComparison.OrdinalIgnoreCase));
            if (tab != null)
            {
                await RevertOpenTabAsync(tab, oldContent, isNewFile);
                return;
            }

            if (isNewFile)
            {
                TryDeleteFile(pathOrId);
                return;
            }

            if (Path.IsPathRooted(pathOrId))
            {
                await File.WriteAllTextAsync(pathOrId, oldContent);
            }
        }

        public string GetWorkspaceRoot()
        {
            if (_selectedExplorerItemProvider() is ExplorerItem selectedItem)
            {
                if (selectedItem.IsFolder && Directory.Exists(selectedItem.Path))
                {
                    return selectedItem.Path;
                }

                string? selectedFileDirectory = Path.GetDirectoryName(selectedItem.Path);
                if (!string.IsNullOrWhiteSpace(selectedFileDirectory) && Directory.Exists(selectedFileDirectory))
                {
                    return selectedFileDirectory;
                }
            }

            string currentFolder = _currentFolderProvider();
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                return currentFolder;
            }

            string currentRepo = _currentRepoProvider();
            if (!string.IsNullOrWhiteSpace(currentRepo) && Directory.Exists(currentRepo))
            {
                return currentRepo;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private async Task RevertOpenTabAsync(OpenedTab tab, string oldContent, bool isNewFile)
        {
            if (isNewFile)
            {
                var tabItem = FindTabItem(tab.Id);
                if (tabItem != null)
                {
                    _tabCloseController.CloseAndCleanup(tab, tabItem);
                }

                if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    TryDeleteFile(tab.FilePath);
                }

                return;
            }

            tab.Content = oldContent;
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                session.UpdateContentFromSync(oldContent);
            }

            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.SetTextAsync(oldContent, shouldFocus: false);
            }
        }

        private TabViewItem? FindTabItem(string tabId)
        {
            return _primaryTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tabId)
                ?? _secondaryTabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => t.Tag as string == tabId);
        }

        private static void TryDeleteFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
        }
    }
}
