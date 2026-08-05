using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class TabCloseController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TabView _editorTabView;
        private readonly TabView _editorTabView2;
        private readonly Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly LivePreviewController _livePreviewController;
        private readonly UnsavedChangesDialogService _unsavedChangesDialogService;
        private readonly Func<XamlRoot?> _xamlRootProvider;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly Func<string, string, string> _getString;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Action<OpenedTab> _forgetEncryptionPassword;
        private readonly Func<OpenedTab, Task<bool>> _saveTabAsync;
        private readonly Func<OpenedTab> _openNewTab;
        private readonly Action<string> _closeReadOnlyViewer;
        private readonly Action _updateWindowTitle;
        private Action<string>? _additionalTabCleanup;

        private readonly DispatcherQueue? _dispatcherQueue;
        public TabCloseController(
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            TabView editorTabView,
            TabView editorTabView2,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            LivePreviewController livePreviewController,
            UnsavedChangesDialogService unsavedChangesDialogService,
            Func<XamlRoot?> xamlRootProvider,
            Func<ElementTheme> getCurrentElementTheme,
            Func<string, string, string> getString,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Action<OpenedTab> forgetEncryptionPassword,
            Func<OpenedTab, Task<bool>> saveTabAsync,
            Func<OpenedTab> openNewTab,
            Action<string> closeReadOnlyViewer,
            Action updateWindowTitle)
        {
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _editorTabView = editorTabView;
            _editorTabView2 = editorTabView2;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _livePreviewController = livePreviewController;
            _unsavedChangesDialogService = unsavedChangesDialogService;
            _xamlRootProvider = xamlRootProvider;
            _getCurrentElementTheme = getCurrentElementTheme;
            _getString = getString;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _forgetEncryptionPassword = forgetEncryptionPassword;
            _saveTabAsync = saveTabAsync;
            _openNewTab = openNewTab;
            _closeReadOnlyViewer = closeReadOnlyViewer;
            _updateWindowTitle = updateWindowTitle;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public void CloseRequested(TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is not TabViewItem tabItem || tabItem.Tag is not string tabId)
            {
                return;
            }

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                CloseOrWarn(tab, tabItem);
            }
        }

        public void CloseActive(TabView activeTabView)
        {
            if (activeTabView.SelectedItem is not TabViewItem tabItem || tabItem.Tag is not string tabId)
            {
                return;
            }

            var tab = _viewModel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                CloseOrWarn(tab, tabItem);
            }
        }

        public void CloseOtherTabs(TabViewItem tabItem, TabView tabView)
        {
            foreach (var item in tabView.TabItems.Cast<TabViewItem>().ToList())
            {
                if (item == tabItem)
                {
                    continue;
                }

                CloseItemOrWarn(item);
            }
        }

        public void CloseRightTabs(TabViewItem tabItem, TabView tabView)
        {
            var items = tabView.TabItems.Cast<TabViewItem>().ToList();
            int currentIndex = items.IndexOf(tabItem);
            if (currentIndex < 0)
            {
                return;
            }

            for (int i = items.Count - 1; i > currentIndex; i--)
            {
                CloseItemOrWarn(items[i]);
            }
        }

        public void CloseLeftTabs(TabViewItem tabItem, TabView tabView)
        {
            var items = tabView.TabItems.Cast<TabViewItem>().ToList();
            int currentIndex = items.IndexOf(tabItem);
            if (currentIndex < 0)
            {
                return;
            }

            for (int i = currentIndex - 1; i >= 0; i--)
            {
                CloseItemOrWarn(items[i]);
            }
        }

        public async void WarnUnsavedAndClose(OpenedTab tab, TabViewItem tabItem)
        {
            bool terminalWasVisible = _isTerminalVisible();
            try
            {
                if (terminalWasVisible)
                {
                    _suspendTerminal();
                }

                XamlRoot? xamlRoot = _xamlRootProvider();
                if (xamlRoot == null)
                {
                    return;
                }

                var result = await _unsavedChangesDialogService.ShowAsync(
                    _getString("UnsavedChangesTabCloseTitle", "변경 내용 저장"),
                    string.Format(_getString("UnsavedChangesTabCloseMessage", "파일 '{0}'의 변경 내용이 저장되지 않았습니다. 닫으시겠습니까?"), tab.Title),
                    _getString("UnsavedChangesTabCloseDiscard", "저장하지 않고 닫기"),
                    _getString("UnsavedChangesTabCloseSave", "저장"),
                    _getString("UnsavedChangesCancel", "취소"),
                    xamlRoot,
                    _getCurrentElementTheme());

                if (result == UnsavedChangesDialogResult.Discard)
                {
                    CloseAndCleanup(tab, tabItem);
                }
                else if (result == UnsavedChangesDialogResult.Save)
                {
                    bool saved = await _saveTabAsync(tab);
                    if (saved)
                    {
                        CloseAndCleanup(tab, tabItem);
                    }
                }
            }
            finally
            {
                if (terminalWasVisible)
                {
                    _resumeTerminal();
                }
            }
        }

        public void CloseAndCleanup(OpenedTab tab, TabViewItem tabItem)
        {
            _additionalTabCleanup?.Invoke(tab.Id);
            _viewModel.Tabs.Remove(tab);
            _forgetEncryptionPassword(tab);

            // Remove the tab before any WebView2 teardown. The workspace disables
            // the native tab transitions before the mutation and forces the
            // affected TabView layout immediately.
            _editorWorkspace.RemoveTabItemImmediately(tabItem);

            WebView2? bridgeWebView = null;
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup))
            {
                _livePreviewController.ForgetEditorTab(tab.Id, bridgeGroup.WebView.CoreWebView2);
                bridgeWebView = bridgeGroup.WebView;
                _tabBridges.Remove(tab.Id);
            }

            _editorSessions.Remove(tab.Id);

            // Expensive WebView2 teardown is deferred so it cannot block the frame
            // that shows the tab bar width change. Tab ids are unique per open, so
            // the deferred callbacks cannot touch a newly opened tab.
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        EditorTabViewItemFactory.ReleaseViewerResources(tabItem);
                        bridgeWebView?.Close();
                        _closeReadOnlyViewer(tab.Id);
                    }
                    catch
                    {
                        // Teardown must never throw on the UI thread.
                    }
                });
            }
            else
            {
                EditorTabViewItemFactory.ReleaseViewerResources(tabItem);
                bridgeWebView?.Close();
                _closeReadOnlyViewer(tab.Id);
            }

            if (_editorTabView.TabItems.Count == 0 && _editorTabView2.TabItems.Count == 0)
            {
                _openNewTab();
            }

            _updateWindowTitle();
        }

        public void SetAdditionalTabCleanup(Action<string> cleanup)
        {
            _additionalTabCleanup = cleanup;
        }

        private void CloseItemOrWarn(TabViewItem item)
        {
            if (item.Tag is not string tabId)
            {
                return;
            }

            var tab = _viewModel.Tabs.FirstOrDefault(x => x.Id == tabId);
            if (tab != null)
            {
                CloseOrWarn(tab, item);
            }
        }

        private void CloseOrWarn(OpenedTab tab, TabViewItem tabItem)
        {
            if (tab.IsDirty)
            {
                WarnUnsavedAndClose(tab, tabItem);
            }
            else
            {
                CloseAndCleanup(tab, tabItem);
            }
        }
    }
}
