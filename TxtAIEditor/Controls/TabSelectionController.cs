using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class TabSelectionController
    {
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly LlmAssistantController _llmAssistantController;
        private readonly AgentController _agentController;
        private readonly TextBlock _selectionStatsText;
        private readonly StatusBarController _statusBarController;
        private readonly Func<string, string, string> _getString;
        private readonly Action<OpenedTab> _updateLivePreview;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly Action<OpenedTab> _syncCsvTableModeUi;
        private readonly TocController _tocController;
        private readonly Action _updateWindowTitle;
        private int _selectionUpdateVersion;

        public TabSelectionController(
            EditorWorkspacePane editorWorkspace,
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            DispatcherQueue dispatcherQueue,
            LlmAssistantController llmAssistantController,
            AgentController agentController,
            TextBlock selectionStatsText,
            StatusBarController statusBarController,
            Func<string, string, string> getString,
            Action<OpenedTab> updateLivePreview,
            Action<OpenedTab> updateLanguageUi,
            Action<OpenedTab> syncCsvTableModeUi,
            TocController tocController,
            Action updateWindowTitle)
        {
            _editorWorkspace = editorWorkspace;
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _dispatcherQueue = dispatcherQueue;
            _llmAssistantController = llmAssistantController;
            _agentController = agentController;
            _selectionStatsText = selectionStatsText;
            _statusBarController = statusBarController;
            _getString = getString;
            _updateLivePreview = updateLivePreview;
            _updateLanguageUi = updateLanguageUi;
            _syncCsvTableModeUi = syncCsvTableModeUi;
            _tocController = tocController;
            _updateWindowTitle = updateWindowTitle;

            _editorWorkspace.PrimarySelectionChanged += OnPrimarySelectionChanged;
        }

        public void QueueChanged(TabView tabView, TabViewItem activeTabItem)
        {
            int version = ++_selectionUpdateVersion;

            void RunSelectionUpdate()
            {
                _ = HandleQueuedAsync(tabView, activeTabItem, version);
            }

            if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunSelectionUpdate))
            {
                RunSelectionUpdate();
            }
        }

        public void ClearQueue()
        {
            _selectionUpdateVersion++;
        }

        private async Task HandleQueuedAsync(TabView tabView, TabViewItem activeTabItem, int version)
        {
            try
            {
                if (version != _selectionUpdateVersion ||
                    tabView.SelectedItem is not TabViewItem selectedItem ||
                    !ReferenceEquals(selectedItem, activeTabItem))
                {
                    return;
                }

                await HandleSelectionChangedAsync(activeTabItem);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Queued tab selection update failed: {ex.Message}");
            }
        }

        private async Task HandleSelectionChangedAsync(TabViewItem activeTabItem)
        {
            _llmAssistantController.ClearSelection();
            _agentController.ClearSelection();
            _selectionStatsText.Text = _getString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");

            if (activeTabItem.Tag is string tabId)
            {
                var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    FocusDocumentViewer(activeTabItem, tab);
                    _dispatcherQueue.TryEnqueue(
                        DispatcherQueuePriority.Low,
                        () => FocusDocumentViewer(activeTabItem, tab));

                    _statusBarController.UpdateFileStats(tab);
                    _statusBarController.UpdateTotalLines(tab);
                    _statusBarController.UpdateSelectionStats(null);

                    _updateLivePreview(tab);

                    _updateLanguageUi(tab);
                    _syncCsvTableModeUi(tab);
                    _statusBarController.SyncEncodingCombo(tab);
                    _statusBarController.SyncLineEndingText(tab);

                    if (tab.IsPendingReload)
                    {
                        tab.IsPendingReload = false;
                        if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            _editorSessions.TryGetValue(tab.Id, out var session);
                            await bridgeGroup.Bridge.SetTextAsync(
                                tab.Content,
                                shouldFocus: false,
                                session?.DocumentId,
                                session?.DocumentVersion,
                                tab.Id);
                            session?.MarkViewSynchronized(session.DocumentVersion);
                        }
                    }

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup2) && bridgeGroup2.Bridge != null)
                    {
                        await bridgeGroup2.Bridge.RequestSelectionAsync();
                    }

                    _tocController.RefreshToc(tab);
                }
            }
            _updateWindowTitle();
        }

        private static void FocusDocumentViewer(TabViewItem tabItem, OpenedTab tab)
        {
            if ((!tab.IsNotebookViewer &&
                 !tab.IsOfficeDocumentViewer &&
                 !tab.IsPdfViewer &&
                 !tab.IsDocxViewer) ||
                !tabItem.IsSelected ||
                tabItem.Content is not DependencyObject content)
            {
                return;
            }

            FindFirstWebView(content)?.Focus(FocusState.Programmatic);
        }

        private static WebView2? FindFirstWebView(DependencyObject root)
        {
            if (root is WebView2 webView)
            {
                return webView;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                if (FindFirstWebView(VisualTreeHelper.GetChild(root, i)) is WebView2 childWebView)
                {
                    return childWebView;
                }
            }

            return null;
        }

        private void OnPrimarySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _editorWorkspace.ActiveTabView = _primaryTabView;
            if (_primaryTabView.SelectedItem is TabViewItem activeTabItem)
            {
                QueueChanged(_primaryTabView, activeTabItem);
            }
            else
            {
                ClearQueue();
            }
        }
    }
}
