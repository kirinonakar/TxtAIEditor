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
        private readonly MainWindowViewModel _viewModel;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Action _updateWindowTitle;

        public TabDirtyStateController(
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            Action updateWindowTitle)
        {
            _viewModel = viewModel;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _updateWindowTitle = updateWindowTitle;
        }

        public void MarkTabDirty(OpenedTab tab, TabViewItem? tabItem = null)
        {
            CheckAndUpdateDirtyState(tab);
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
                    savedContent = session.GetText();
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
                    tab.OriginalLineEnding = hasSession ? session!.Model.LineEnding : LineArrayTextModel.FromText(savedContent).LineEnding;
                    tab.OriginalEncodingName = tab.EncodingName;

                    if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        if (hasSession)
                        {
                            var lines = session!.GetLines(1, session.Model.LineCount);
                            _ = bridgeGroup.Bridge.ResetOriginalLinesAsync(lines);
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
                var dirtyLines = ComputeDirtyLines(tab, session);
                bool isDirty = (dirtyLines.Count > 0) ||
                               (tab.OriginalLineEnding != null && session.Model.LineEnding != tab.OriginalLineEnding) ||
                               (tab.OriginalEncodingName != null && tab.EncodingName != tab.OriginalEncodingName);
                SetDirtyStateForFileGroup(tab, isDirty);

                foreach (var t in GetTabsForSameFile(tab))
                {
                    if (_tabBridges.TryGetValue(t.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
                    {
                        _ = bridgeGroup.Bridge.UpdateDirtyLinesAsync(dirtyLines);
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
            var markers = new Dictionary<int, string>();
            if (tab.OriginalContent == null) return markers;

            var orig = tab.OriginalLines;
            var current = session.Model.GetLines(1, session.Model.LineCount);

            int prefixMatchCount = 0;
            int maxPrefix = Math.Min(orig.Length, current.Count);
            while (prefixMatchCount < maxPrefix && orig[prefixMatchCount] == current[prefixMatchCount])
            {
                prefixMatchCount++;
            }

            int suffixMatchCount = 0;
            int maxSuffix = Math.Min(orig.Length - prefixMatchCount, current.Count - prefixMatchCount);
            while (suffixMatchCount < maxSuffix &&
                   orig[orig.Length - 1 - suffixMatchCount] == current[current.Count - 1 - suffixMatchCount])
            {
                suffixMatchCount++;
            }

            int unmatchedOrigCount = orig.Length - prefixMatchCount - suffixMatchCount;
            int unmatchedCurrentCount = current.Count - prefixMatchCount - suffixMatchCount;

            int oi = prefixMatchCount;
            int ci = prefixMatchCount;
            int limitOrig = orig.Length - suffixMatchCount;
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
