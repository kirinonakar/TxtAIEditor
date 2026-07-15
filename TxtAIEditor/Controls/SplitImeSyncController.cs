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

        public async Task SyncLineChangeToOtherTabsAsync(OpenedTab sourceTab, int lineNumber, string text, bool isComposing)
        {
            if (string.IsNullOrEmpty(sourceTab.FilePath)) return;
            if (!_editorSessions.TryGetValue(sourceTab.Id, out var sourceSession)) return;

            var otherTabs = _getTabsForSameFile(sourceTab)
                .Where(t => t.Id != sourceTab.Id)
                .ToList();

            if (otherTabs.Count == 0) return;

            bool sourceDirty = sourceTab.IsDirty;
            EditorDocumentChange? change = sourceSession.LastChange;

            foreach (var otherTab in otherTabs)
            {
                if (_editorSessions.TryGetValue(otherTab.Id, out var otherSession))
                {
                    if (!otherSession.SharesDocumentWith(sourceSession))
                    {
                        otherSession.ReplaceLine(lineNumber, text, trackUndo: !isComposing, isComposing: isComposing);
                    }
                    otherSession.RefreshTabContentPreview();
                }
                else
                {
                    otherTab.Content = text;
                }

                if (_tabBridges.TryGetValue(otherTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                {
                    if (!_editorSessions.TryGetValue(otherTab.Id, out var targetSession) ||
                        !targetSession.SharesDocumentWith(sourceSession) ||
                        targetSession.ViewVersion < sourceSession.DocumentVersion)
                    {
                        await bridgeGroup.Bridge.UpdateLineAsync(
                            lineNumber,
                            text,
                            isComposing,
                            sourceSession.DocumentId,
                            change?.BaseVersion,
                            change?.Version ?? sourceSession.DocumentVersion);
                        targetSession?.MarkViewSynchronized(sourceSession.DocumentVersion);
                    }
                }

                if (!isComposing)
                {
                    _schedulePreview(otherTab);
                }
            }

            _setDirtyStateForFileGroup(sourceTab, sourceDirty);
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
            EditorDocumentChange? change = sourceSession.LastChange;

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
                    if (sharesDocument && otherSession != null && change != null &&
                        otherSession.ViewVersion == change.BaseVersion)
                    {
                        await bridgeGroup.Bridge.ApplyEditResultAsync(new UndoResult(
                            change.StartLine,
                            change.OldLineCount,
                            change.DocumentLineCount,
                            change.Lines,
                            null),
                            change.DocumentId,
                            change.BaseVersion,
                            change.Version,
                            change.SourceViewId);
                        otherSession.MarkViewSynchronized(change.Version);
                    }
                    else if (!sharesDocument ||
                        (otherSession != null && change != null && otherSession.ViewVersion < change.Version))
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

    }
}
