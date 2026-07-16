using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class SplitImeSyncController
    {
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Func<OpenedTab, IEnumerable<OpenedTab>> _getTabsForSameFile;
        private readonly Action<OpenedTab> _schedulePreview;
        private readonly Action<OpenedTab, bool> _setDirtyStateForFileGroup;

        public SplitImeSyncController(
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            Func<OpenedTab, IEnumerable<OpenedTab>> getTabsForSameFile,
            Action<OpenedTab> schedulePreview,
            Action<OpenedTab, bool> setDirtyStateForFileGroup)
        {
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _getTabsForSameFile = getTabsForSameFile;
            _schedulePreview = schedulePreview;
            _setDirtyStateForFileGroup = setDirtyStateForFileGroup;
        }

        public async Task SyncEditsToOtherTabsAsync(OpenedTab sourceTab, bool updateUi = true)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return;

            var otherTabs = _getTabsForSameFile(sourceTab)
                .Where(t => t.Id != sourceTab.Id)
                .ToList();

            if (otherTabs.Count == 0) return;

            if (!_editorSessions.TryGetValue(sourceTab.Id, out var sourceSession)) return;
            bool sourceDirty = sourceTab.IsDirty;

            foreach (var otherTab in otherTabs)
            {
                bool hasOtherSession = _editorSessions.TryGetValue(otherTab.Id, out var otherSession);
                bool sharesDocument = hasOtherSession && otherSession!.SharesDocumentWith(sourceSession);

                if (hasOtherSession && !sharesDocument)
                {
                    string updatedText = sourceSession.GetText();
                    otherSession!.UpdateContentFromSync(updatedText);
                    otherTab.Content = updatedText;
                }
                else if (hasOtherSession)
                {
                    otherSession!.RefreshTabContentPreview();
                }

                if (updateUi && _tabBridges.TryGetValue(otherTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    if (sharesDocument && otherSession != null &&
                        otherSession.ViewVersion < sourceSession.DocumentVersion &&
                        sourceSession.TryGetChangesSince(
                            otherSession.ViewVersion,
                            out IReadOnlyList<EditorDocumentChange> replayChanges))
                    {
                        foreach (EditorDocumentChange replayChange in replayChanges)
                        {
                            await ApplyDocumentChangeAsync(bridgeGroup.Bridge, replayChange);
                            otherSession.MarkViewSynchronized(replayChange.Version);
                        }
                    }
                    else if (!sharesDocument ||
                        (otherSession != null && otherSession.ViewVersion < sourceSession.DocumentVersion))
                    {
                        string updatedText = sourceSession.GetText();
                        await bridgeGroup.Bridge.SetTextAsync(
                            updatedText,
                            shouldFocus: false,
                            sourceSession.DocumentId,
                            sourceSession.DocumentVersion,
                            otherTab.Id);
                        otherSession?.MarkViewSynchronized(sourceSession.DocumentVersion);
                    }
                }

                if (updateUi)
                {
                    _schedulePreview(otherTab);
                }
            }

            _setDirtyStateForFileGroup(sourceTab, sourceDirty);
        }

        private static Task ApplyDocumentChangeAsync(
            MonacoBridge bridge,
            EditorDocumentChange change)
        {
            if (change.LinePatches is { Count: > 0 } patches)
            {
                return bridge.ApplyLinePatchesAsync(
                    patches,
                    change.DocumentId,
                    change.BaseVersion,
                    change.Version,
                    change.SourceViewId);
            }

            return bridge.ApplyEditResultAsync(new UndoResult(
                    change.StartLine,
                    change.OldLineCount,
                    change.DocumentLineCount,
                    change.Lines,
                    null),
                change.DocumentId,
                change.BaseVersion,
                change.Version,
                change.SourceViewId);
        }

    }
}
