using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class TabDirtyStateController
    {
        private const int MaxDirtyDiffCells = 4_000_000;

        private readonly MainWindowViewModel _viewModel;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Func<bool> _showDirtyLines;
        private readonly Action _updateWindowTitle;

        public TabDirtyStateController(
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            Func<bool> showDirtyLines,
            Action updateWindowTitle)
        {
            _viewModel = viewModel;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _showDirtyLines = showDirtyLines;
            _updateWindowTitle = updateWindowTitle;
        }

        public void MarkTabDirty(OpenedTab tab, TabViewItem? tabItem = null)
        {
            CheckAndUpdateDirtyState(tab);
        }

        public void MarkTabDirtyOptimistically(OpenedTab tab)
        {
            SetDirtyStateForFileGroup(tab, true);
        }

        public void ReconcileTabDirtyState(OpenedTab tab)
        {
            CheckAndUpdateDirtyState(tab);
        }

        public void RefreshAllDirtyLineMarkers()
        {
            if (!_showDirtyLines())
            {
                return;
            }

            foreach (var tab in _viewModel.Tabs.ToList())
            {
                if (_editorSessions.ContainsKey(tab.Id))
                {
                    CheckAndUpdateDirtyState(tab);
                }
            }
        }

        public List<OpenedTab> GetTabsForSameFile(OpenedTab sourceTab)
        {
            string? pathKey = NormalizeTabPath(sourceTab.FilePath);
            if (pathKey == null)
            {
                return new List<OpenedTab> { sourceTab };
            }

            var tabs = _viewModel.Tabs
                .Where(tab =>
                {
                    string? otherPathKey = NormalizeTabPath(tab.FilePath);
                    return otherPathKey != null &&
                           string.Equals(otherPathKey, pathKey, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (!tabs.Any(tab => tab.Id == sourceTab.Id))
            {
                tabs.Add(sourceTab);
            }

            return tabs;
        }

        public bool IsAnySameFileTabDirty(OpenedTab sourceTab)
        {
            return GetTabsForSameFile(sourceTab).Any(tab => tab.IsDirty);
        }

        public void SetDirtyStateForFileGroup(OpenedTab sourceTab, bool isDirty)
        {
            bool changed = false;
            string? savedContent = null;
            if (!isDirty)
            {
                if (_editorSessions.TryGetValue(sourceTab.Id, out var session))
                {
                    savedContent = session.Model is HexDumpTextModel ? string.Empty : session.GetText();
                }
                else
                {
                    savedContent = sourceTab.Content;
                }
            }

            foreach (var tab in GetTabsForSameFile(sourceTab))
            {
                if (savedContent != null)
                {
                    tab.OriginalContent = savedContent;
                    bool hasSession = _editorSessions.TryGetValue(tab.Id, out var session);
                    tab.OriginalLineEnding = hasSession ? session!.Model.LineEnding : TextModelFactory.FromText(savedContent).LineEnding;
                    tab.OriginalEncodingName = tab.EncodingName;

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        if (hasSession)
                        {
                            if (session!.Model is HexDumpTextModel)
                            {
                                _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(Array.Empty<string>());
                            }
                            else
                            {
                                var lines = session!.GetLines(1, session.Model.LineCount);
                                _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(lines);
                            }
                        }
                        else
                        {
                            var lines = savedContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                            _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(lines);
                        }
                    }
                }

                if (tab.IsDirty != isDirty)
                {
                    tab.IsDirty = isDirty;
                    changed = true;
                }
            }

            if (changed)
            {
                _updateWindowTitle();
            }
        }

        public void PropagateDirtyStateToOtherTabs(OpenedTab sourceTab)
        {
            CheckAndUpdateDirtyState(sourceTab);
        }

        public void CleanDirtyStateOnOtherTabs(OpenedTab sourceTab)
        {
            SetDirtyStateForFileGroup(sourceTab, false);
        }

        private void CheckAndUpdateDirtyState(OpenedTab tab)
        {
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                if (session.Model is HexDumpTextModel hexModel)
                {
                    bool isDirty = hexModel.HasPendingEdits;
                    SetDirtyStateForFileGroup(tab, isDirty);

                    foreach (var t in GetTabsForSameFile(tab))
                    {
                        if (_tabBridges.TryGetValue(t.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            _ = bridgeGroup.Bridge.UpdateDirtyLinesAsync(new Dictionary<int, string>());
                        }
                    }
                    return;
                }

                var dirtyLines = _showDirtyLines()
                    ? ComputeDirtyLines(tab, session)
                    : null;
                bool hasTextChanges = dirtyLines != null
                    ? dirtyLines.Count > 0
                    : HasTextChanges(tab, session);
                bool isDirtyCurrent = hasTextChanges ||
                               (tab.OriginalLineEnding != null && session.Model.LineEnding != tab.OriginalLineEnding) ||
                               (tab.OriginalEncodingName != null && tab.EncodingName != tab.OriginalEncodingName);
                SetDirtyStateForFileGroup(tab, isDirtyCurrent);

                if (dirtyLines != null)
                {
                    foreach (var t in GetTabsForSameFile(tab))
                    {
                        if (_tabBridges.TryGetValue(t.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                        {
                            _ = bridgeGroup.Bridge.UpdateDirtyLinesAsync(dirtyLines);
                        }
                    }
                }
            }
            else
            {
                SetDirtyStateForFileGroup(tab, true);
            }
        }

        private static Dictionary<int, string> ComputeDirtyLines(OpenedTab tab, EditorDocumentSession session)
        {
            var orig = tab.OriginalLines;
            var current = session.Model.GetLines(1, session.Model.LineCount);
            return ComputeDirtyLines(orig, current);
        }

        private static bool HasTextChanges(OpenedTab tab, EditorDocumentSession session)
        {
            var original = tab.OriginalLines;
            if (original.Length != session.Model.LineCount)
            {
                return true;
            }

            for (int line = 1; line <= session.Model.LineCount; line++)
            {
                if (!string.Equals(original[line - 1], session.Model.GetLine(line), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static Dictionary<int, string> ComputeDirtyLines(IReadOnlyList<string> orig, IReadOnlyList<string> current)
        {
            var markers = new Dictionary<int, string>();
            int prefixMatchCount = 0;
            int maxPrefix = Math.Min(orig.Count, current.Count);
            while (prefixMatchCount < maxPrefix && orig[prefixMatchCount] == current[prefixMatchCount])
            {
                prefixMatchCount++;
            }

            int suffixMatchCount = 0;
            int maxSuffix = Math.Min(orig.Count - prefixMatchCount, current.Count - prefixMatchCount);
            while (suffixMatchCount < maxSuffix &&
                   orig[orig.Count - 1 - suffixMatchCount] == current[current.Count - 1 - suffixMatchCount])
            {
                suffixMatchCount++;
            }

            int unmatchedOrigCount = orig.Count - prefixMatchCount - suffixMatchCount;
            int unmatchedCurrentCount = current.Count - prefixMatchCount - suffixMatchCount;
            int limitOrig = orig.Count - suffixMatchCount;
            int limitCurr = current.Count - suffixMatchCount;

            if (unmatchedOrigCount == 0)
            {
                for (int line = prefixMatchCount; line < limitCurr; line++)
                {
                    markers[line + 1] = "add";
                }

                return markers;
            }

            if (unmatchedCurrentCount == 0)
            {
                MarkDeletion(markers, prefixMatchCount, 0, current.Count);
                return markers;
            }

            long cellCount = (long)unmatchedOrigCount * unmatchedCurrentCount;
            if (cellCount <= MaxDirtyDiffCells)
            {
                var matches = ComputeLcsMatches(orig, prefixMatchCount, limitOrig, current, prefixMatchCount, limitCurr);
                int previousOrig = prefixMatchCount;
                int previousCurrent = prefixMatchCount;

                foreach (var (origIndex, currentIndex) in matches)
                {
                    AddDirtyMarkersForGap(
                        markers,
                        previousOrig,
                        origIndex,
                        previousCurrent,
                        currentIndex,
                        current.Count);
                    previousOrig = origIndex + 1;
                    previousCurrent = currentIndex + 1;
                }

                AddDirtyMarkersForGap(
                    markers,
                    previousOrig,
                    limitOrig,
                    previousCurrent,
                    limitCurr,
                    current.Count);

                return markers;
            }

            return ComputeDirtyLinesGreedy(
                orig,
                current,
                prefixMatchCount,
                suffixMatchCount,
                unmatchedOrigCount,
                unmatchedCurrentCount);
        }

        private static List<(int OrigIndex, int CurrentIndex)> ComputeLcsMatches(
            IReadOnlyList<string> orig,
            int origStart,
            int origEnd,
            IReadOnlyList<string> current,
            int currentStart,
            int currentEnd)
        {
            int origCount = origEnd - origStart;
            int currentCount = currentEnd - currentStart;
            var table = new int[origCount + 1, currentCount + 1];

            for (int oi = origCount - 1; oi >= 0; oi--)
            {
                for (int ci = currentCount - 1; ci >= 0; ci--)
                {
                    table[oi, ci] = orig[origStart + oi] == current[currentStart + ci]
                        ? table[oi + 1, ci + 1] + 1
                        : Math.Max(table[oi + 1, ci], table[oi, ci + 1]);
                }
            }

            var matches = new List<(int OrigIndex, int CurrentIndex)>();
            int o = 0;
            int c = 0;
            while (o < origCount && c < currentCount)
            {
                if (orig[origStart + o] == current[currentStart + c])
                {
                    matches.Add((origStart + o, currentStart + c));
                    o++;
                    c++;
                }
                else if (table[o + 1, c] >= table[o, c + 1])
                {
                    o++;
                }
                else
                {
                    c++;
                }
            }

            return matches;
        }

        private static void AddDirtyMarkersForGap(
            Dictionary<int, string> markers,
            int origStart,
            int origEnd,
            int currentStart,
            int currentEnd,
            int currentLineCount)
        {
            int deletedCount = origEnd - origStart;
            int insertedCount = currentEnd - currentStart;
            if (deletedCount <= 0 && insertedCount <= 0)
            {
                return;
            }

            int modifiedCount = Math.Min(deletedCount, insertedCount);
            for (int i = 0; i < modifiedCount; i++)
            {
                markers[currentStart + i + 1] = "mod";
            }

            for (int i = modifiedCount; i < insertedCount; i++)
            {
                markers[currentStart + i + 1] = "add";
            }

            if (deletedCount > insertedCount)
            {
                MarkDeletion(markers, currentStart, insertedCount, currentLineCount);
            }
        }

        private static void MarkDeletion(
            Dictionary<int, string> markers,
            int currentStart,
            int insertedCount,
            int currentLineCount)
        {
            if (currentLineCount <= 0)
            {
                return;
            }

            int markerLine;
            if (insertedCount == 0)
            {
                markerLine = Math.Clamp(currentStart, 1, currentLineCount);
            }
            else if (currentStart + insertedCount < currentLineCount)
            {
                markerLine = currentStart + insertedCount + 1;
            }
            else
            {
                markerLine = Math.Clamp(currentStart + insertedCount, 1, currentLineCount);
            }

            if (!markers.ContainsKey(markerLine))
            {
                markers[markerLine] = "del";
            }
        }

        private static Dictionary<int, string> ComputeDirtyLinesGreedy(
            IReadOnlyList<string> orig,
            IReadOnlyList<string> current,
            int prefixMatchCount,
            int suffixMatchCount,
            int unmatchedOrigCount,
            int unmatchedCurrentCount)
        {
            var markers = new Dictionary<int, string>();

            int oi = prefixMatchCount;
            int ci = prefixMatchCount;
            int limitOrig = orig.Count - suffixMatchCount;
            int limitCurr = current.Count - suffixMatchCount;

            int scanLimit = Math.Max(100, Math.Abs(unmatchedOrigCount - unmatchedCurrentCount) + 10);

            while (oi < limitOrig && ci < limitCurr)
            {
                if (orig[oi] == current[ci])
                {
                    oi++;
                    ci++;
                }
                else
                {
                    int aheadOrig = -1;
                    for (int s = oi + 1; s < Math.Min(oi + scanLimit, limitOrig); s++)
                    {
                        if (orig[s] == current[ci])
                        {
                            aheadOrig = s;
                            break;
                        }
                    }

                    int aheadCurr = -1;
                    for (int s = ci + 1; s < Math.Min(ci + scanLimit, limitCurr); s++)
                    {
                        if (current[s] == orig[oi])
                        {
                            aheadCurr = s;
                            break;
                        }
                    }

                    if (aheadOrig >= 0 && (aheadCurr < 0 || (aheadOrig - oi) < (aheadCurr - ci)))
                    {
                        markers[ci + 1] = "del";
                        oi = aheadOrig;
                    }
                    else if (aheadCurr >= 0)
                    {
                        for (int a = ci; a < aheadCurr; a++)
                        {
                            markers[a + 1] = "add";
                        }

                        ci = aheadCurr;
                    }
                    else
                    {
                        markers[ci + 1] = "mod";
                        oi++;
                        ci++;
                    }
                }
            }

            if (oi < limitOrig && limitCurr >= 1)
            {
                markers[limitCurr] = "del";
            }

            while (ci < limitCurr)
            {
                markers[ci + 1] = "add";
                ci++;
            }

            return markers;
        }

        private static string? NormalizeTabPath(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch
            {
                return filePath;
            }
        }
    }
}
