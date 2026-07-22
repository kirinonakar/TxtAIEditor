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
        private readonly Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly TabCloseController _tabCloseController;
        private readonly SearchReplaceTabSyncController _tabSyncController;
        private readonly CompareTabController _compareTabController;
        private readonly IAgentFileWorkflowHost _host;

        public AgentFileWorkflowController(
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            TabCloseController tabCloseController,
            SearchReplaceTabSyncController tabSyncController,
            CompareTabController compareTabController,
            IAgentFileWorkflowHost host)
        {
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _tabCloseController = tabCloseController;
            _tabSyncController = tabSyncController;
            _compareTabController = compareTabController;
            _host = host;
        }

        private async Task RunOnUiAsync(Func<Task> action)
        {
            var dispatcher = _primaryTabView.DispatcherQueue;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource();
                dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        await action();
                        tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                await tcs.Task;
            }
            else
            {
                await action();
            }
        }

        public async Task HandleFileModifiedAsync(string filePath)
        {
            await RunOnUiAsync(async () =>
            {
                await _tabSyncController.HandleFileModifiedAsync(filePath);

                string currentFolder = _host.GetCurrentFolderPath();
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                {
                    _host.LoadDirectoryRoot(currentFolder);
                }

                _host.QueueGitStatusRefresh();

                var edit = _host.GetSessionEdits()
                    .LastOrDefault(e => string.Equals(e.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
                if (edit != null)
                {
                    string displayName = edit.TotalModifications > 1
                        ? $"({edit.ModificationNumber}) {Path.GetFileName(edit.RelativePath)}"
                        : Path.GetFileName(edit.RelativePath);
                    string title = $"{_host.GetLocalizedString("AgentDiffTitle", "Agent 변경 비교")}: {displayName}";
                    await _compareTabController.UpdateCompareTabIfOpenAsync(
                        title,
                        edit.FullPath,
                        edit.FullPath,
                        edit.OldContent,
                        edit.NewContent,
                        labelA: _host.GetLocalizedString("DiffOriginalLabel", "원본"),
                        labelB: _host.GetLocalizedString("DiffModifiedLabel", "수정본"));
                }
            });
        }

        public async Task OpenDiffViewAsync(AgentFileEditPreview preview)
        {
            await RunOnUiAsync(async () =>
            {
                string displayName = preview.TotalModifications > 1
                    ? $"({preview.ModificationNumber}) {Path.GetFileName(preview.RelativePath)}"
                    : Path.GetFileName(preview.RelativePath);

                await _compareTabController.OpenCompareTabAsync(
                    preview.FullPath,
                    preview.FullPath,
                    preview.OldContent,
                    preview.NewContent,
                    customTitle: $"{_host.GetLocalizedString("AgentDiffTitle", "Agent 변경 비교")}: {displayName}",
                    labelA: _host.GetLocalizedString("DiffOriginalLabel", "원본"),
                    labelB: _host.GetLocalizedString("DiffModifiedLabel", "수정본"));
            });
        }

        public void CloseTabById(string tabId)
        {
            var dispatcher = _primaryTabView.DispatcherQueue;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => CloseTabByIdInternal(tabId));
            }
            else
            {
                CloseTabByIdInternal(tabId);
            }
        }

        private void CloseTabByIdInternal(string tabId)
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
            await RunOnUiAsync(async () =>
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
            });
        }

        public string GetWorkspaceRoot()
        {
            string currentFolder = _host.GetCurrentFolderPath();
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                return currentFolder;
            }

            string currentRepo = _host.GetCurrentRepoPath();
            if (!string.IsNullOrWhiteSpace(currentRepo) && Directory.Exists(currentRepo))
            {
                return currentRepo;
            }

            if (_host.GetSelectedExplorerItem() is ExplorerItem selectedItem)
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
                await bridgeGroup.Bridge.SetTextAsync(
                    oldContent,
                    shouldFocus: false,
                    session?.DocumentId,
                    session?.DocumentVersion,
                    tab.Id);
                session?.MarkViewSynchronized(session.DocumentVersion);
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
