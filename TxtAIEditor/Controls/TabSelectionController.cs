using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
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
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly LlmAssistantController _llmAssistantController;
        private readonly TextBlock _selectionStatsText;
        private readonly StatusBarController _statusBarController;
        private readonly Func<string, string, string> _getString;
        private readonly Action<OpenedTab> _updateLivePreview;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly TocController _tocController;
        private readonly Action _updateWindowTitle;
        private int _selectionUpdateVersion;

        public TabSelectionController(
            EditorWorkspacePane editorWorkspace,
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            DispatcherQueue dispatcherQueue,
            LlmAssistantController llmAssistantController,
            TextBlock selectionStatsText,
            StatusBarController statusBarController,
            Func<string, string, string> getString,
            Action<OpenedTab> updateLivePreview,
            Action<OpenedTab> updateLanguageUi,
            TocController tocController,
            Action updateWindowTitle)
        {
            _editorWorkspace = editorWorkspace;
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _tabBridges = tabBridges;
            _dispatcherQueue = dispatcherQueue;
            _llmAssistantController = llmAssistantController;
            _selectionStatsText = selectionStatsText;
            _statusBarController = statusBarController;
            _getString = getString;
            _updateLivePreview = updateLivePreview;
            _updateLanguageUi = updateLanguageUi;
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
            _selectionStatsText.Text = _getString("SelectionNoneBlocked", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");

            if (activeTabItem.Tag is string tabId)
            {
                var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    _statusBarController.UpdateFileStats(tab);
                    _statusBarController.UpdateTotalLines(tab);
                    _statusBarController.UpdateSelectionStats(null);
                    _updateLivePreview(tab);
                    _updateLanguageUi(tab);
                    _statusBarController.SyncEncodingCombo(tab);
                    _statusBarController.SyncLineEndingText(tab);

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        await bridgeGroup.Bridge.RequestSelectionAsync();
                    }
                    _tocController.RefreshToc(tab);
                }
            }
            _updateWindowTitle();
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
