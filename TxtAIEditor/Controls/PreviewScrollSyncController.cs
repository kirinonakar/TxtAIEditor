using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class PreviewScrollSyncController
    {
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly System.Func<OpenedTab?> _activeTabProvider;
        private readonly System.Func<OpenedTab, TabView?> _oppositeTabViewProvider;

        public PreviewScrollSyncController(
            EditorWorkspacePane editorWorkspace,
            Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            System.Func<OpenedTab?> activeTabProvider,
            System.Func<OpenedTab, TabView?> oppositeTabViewProvider)
        {
            _editorWorkspace = editorWorkspace;
            _tabBridges = tabBridges;
            _activeTabProvider = activeTabProvider;
            _oppositeTabViewProvider = oppositeTabViewProvider;
        }

        public void SyncToEditors(int firstLine, double offset)
        {
            var activeTab = _activeTabProvider();
            if (activeTab == null)
            {
                return;
            }

            if (_tabBridges.TryGetValue(activeTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                _ = bridgeGroup.Bridge.SyncScrollFromPreviewAsync(firstLine, offset);
            }

            if (_editorWorkspace.CurrentSplitMode == EditorSplitMode.None)
            {
                return;
            }

            var otherTabView = _oppositeTabViewProvider(activeTab);
            if (otherTabView?.SelectedItem is TabViewItem otherItem &&
                otherItem.Tag is string otherTabId &&
                _tabBridges.TryGetValue(otherTabId, out var otherBridgeGroup) &&
                otherBridgeGroup.Bridge != null)
            {
                _ = otherBridgeGroup.Bridge.SyncScrollFromPreviewAsync(firstLine, offset);
            }
        }
    }
}
