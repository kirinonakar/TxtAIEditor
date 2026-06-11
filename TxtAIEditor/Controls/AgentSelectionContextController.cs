using System;
using System.IO;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSelectionSnapshot
    {
        public string Text { get; init; } = string.Empty;
        public string? SourcePath { get; init; }
        public string? SourceTitle { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }

        public bool HasLineRange =>
            !string.IsNullOrEmpty(Text) &&
            !string.IsNullOrEmpty(SourcePath) &&
            StartLine > 0 &&
            EndLine > 0;
    }

    internal sealed class AgentSelectionContextController
    {
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string> _workspaceRootProvider;

        private string _lastSelectionText = string.Empty;
        private string? _lastSelectionTabId;
        private string? _lastSelectionSourceTitle;
        private string? _lastSelectionSourcePath;
        private int _lastSelectionStartLine;
        private int _lastSelectionEndLine;
        private OpenedTab? _lastKnownActiveTab;
        private bool _lastKnownActiveTabFromTabSelection;
        private OpenedTab? _currentRunActiveTabSnapshot;
        private AgentSelectionSnapshot? _currentRunSelectionSnapshot;

        public AgentSelectionContextController(
            Func<OpenedTab?> activeTabProvider,
            Func<string> workspaceRootProvider)
        {
            _activeTabProvider = activeTabProvider;
            _workspaceRootProvider = workspaceRootProvider;
        }

        public void SetActiveTab(OpenedTab? activeTab)
        {
            _lastKnownActiveTab = activeTab;
            _lastKnownActiveTabFromTabSelection = true;
            ClearSelectionIfItBelongsToAnotherTab(activeTab);
        }

        public void SetSelectionText(string selectedText, OpenedTab? sourceTab = null, int startLine = 0, int endLine = 0)
        {
            _lastSelectionText = selectedText ?? string.Empty;
            if (string.IsNullOrEmpty(_lastSelectionText))
            {
                ClearSelectionState();
                return;
            }

            _lastSelectionTabId = sourceTab?.Id;
            _lastSelectionSourceTitle = sourceTab?.Title;
            _lastSelectionSourcePath = sourceTab?.FilePath;
            _lastSelectionStartLine = startLine;
            _lastSelectionEndLine = endLine;
        }

        public void ClearSelection()
        {
            _lastSelectionText = string.Empty;
            ClearSelectionState();
        }

        public void ClearRunSnapshots()
        {
            _currentRunActiveTabSnapshot = null;
            _currentRunSelectionSnapshot = null;
        }

        public OpenedTab? GetActiveTabForContext(bool isRunning)
        {
            if (isRunning && _currentRunActiveTabSnapshot != null)
            {
                return _currentRunActiveTabSnapshot;
            }

            var liveActiveTab = _activeTabProvider();
            if (!_lastKnownActiveTabFromTabSelection)
            {
                _lastKnownActiveTab = liveActiveTab;
                return liveActiveTab;
            }

            if (_lastKnownActiveTab == null)
            {
                return null;
            }

            if (liveActiveTab == null)
            {
                return _lastKnownActiveTab;
            }

            if (string.Equals(liveActiveTab.Id, _lastKnownActiveTab.Id, StringComparison.Ordinal))
            {
                _lastKnownActiveTab = liveActiveTab;
                return liveActiveTab;
            }

            return _lastKnownActiveTab;
        }

        public OpenedTab? CaptureActiveTabForRun(bool isRunning)
        {
            var activeTab = GetActiveTabForContext(isRunning);
            _lastKnownActiveTab = activeTab;
            _currentRunActiveTabSnapshot = activeTab;
            return activeTab;
        }

        public AgentSelectionSnapshot CaptureSelectionForRun(bool isRunning)
        {
            _currentRunSelectionSnapshot = CaptureLiveSelectionSnapshot(isRunning);
            return _currentRunSelectionSnapshot;
        }

        public string GetActiveSelectionText(bool isRunning)
        {
            var activeTab = GetActiveTabForContext(isRunning);
            if (string.IsNullOrEmpty(_lastSelectionText))
            {
                return string.Empty;
            }

            if (activeTab == null)
            {
                return string.Empty;
            }

            if (_lastSelectionTabId == null)
            {
                return _lastSelectionText;
            }

            return string.Equals(_lastSelectionTabId, activeTab.Id, StringComparison.Ordinal)
                ? _lastSelectionText
                : string.Empty;
        }

        public string BuildActiveSelectionContext(bool isRunning)
        {
            AgentSelectionSnapshot selection = CaptureActiveSelectionSnapshot(isRunning);
            if (string.IsNullOrEmpty(selection.Text))
            {
                return string.Empty;
            }

            string source = FormatSelectionSourceForPrompt(selection.SourcePath, selection.SourceTitle);
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            if (selection.StartLine > 0 && selection.EndLine > 0)
            {
                string linePart = selection.StartLine == selection.EndLine
                    ? $"line {selection.StartLine}"
                    : $"line {selection.StartLine}-{selection.EndLine}";
                return $"{source} - {linePart}";
            }

            return source;
        }

        public AgentSelectionSnapshot CaptureActiveSelectionSnapshot(bool isRunning)
        {
            if (isRunning &&
                _currentRunSelectionSnapshot != null &&
                !string.IsNullOrEmpty(_currentRunSelectionSnapshot.Text))
            {
                return _currentRunSelectionSnapshot;
            }

            return CaptureLiveSelectionSnapshot(isRunning);
        }

        public string FormatSelectionScope(AgentSelectionSnapshot selection)
        {
            string source = FormatSelectionSourceForPrompt(selection.SourcePath, selection.SourceTitle);
            if (string.IsNullOrWhiteSpace(source))
            {
                source = selection.SourcePath ?? "the selected file";
            }

            return $"{source} lines {selection.StartLine}-{selection.EndLine}";
        }

        private AgentSelectionSnapshot CaptureLiveSelectionSnapshot(bool isRunning)
        {
            string selectedText = GetActiveSelectionText(isRunning);
            if (string.IsNullOrEmpty(selectedText))
            {
                return new AgentSelectionSnapshot();
            }

            return new AgentSelectionSnapshot
            {
                Text = selectedText,
                SourcePath = _lastSelectionSourcePath,
                SourceTitle = _lastSelectionSourceTitle,
                StartLine = _lastSelectionStartLine,
                EndLine = _lastSelectionEndLine
            };
        }

        private string FormatSelectionSourceForPrompt(string? sourcePath, string? sourceTitle)
        {
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                try
                {
                    string root = _workspaceRootProvider();
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        string fullRoot = Path.GetFullPath(root);
                        string fullSource = Path.GetFullPath(sourcePath);
                        if (fullSource.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            return Path.GetRelativePath(fullRoot, fullSource).Replace('\\', '/');
                        }
                    }
                }
                catch { }

                return sourcePath.Replace('\\', '/');
            }

            return sourceTitle ?? string.Empty;
        }

        private void ClearSelectionIfItBelongsToAnotherTab(OpenedTab? activeTab)
        {
            if (string.IsNullOrEmpty(_lastSelectionText) || string.IsNullOrEmpty(_lastSelectionTabId))
            {
                return;
            }

            if (activeTab != null &&
                string.Equals(_lastSelectionTabId, activeTab.Id, StringComparison.Ordinal))
            {
                return;
            }

            _lastSelectionText = string.Empty;
            ClearSelectionState();
        }

        private void ClearSelectionState()
        {
            _lastSelectionTabId = null;
            _lastSelectionSourceTitle = null;
            _lastSelectionSourcePath = null;
            _lastSelectionStartLine = 0;
            _lastSelectionEndLine = 0;
        }
    }
}
