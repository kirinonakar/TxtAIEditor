using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionEditController
    {
        private readonly AgentPane _agentPane;
        private readonly Func<Func<Task>, Task> _runOnUIThreadAsync;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Func<string, string, bool, Task>? _revertTabOrFileAsync;
        private readonly Action<string>? _closeTabById;
        private readonly Action<string> _appendActivity;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly List<AgentFileEditPreview> _sessionEdits = new();

        public AgentSessionEditController(
            AgentPane agentPane,
            Func<Func<Task>, Task> runOnUIThreadAsync,
            Func<string, Task>? fileModifiedAsync,
            Func<string, string, bool, Task>? revertTabOrFileAsync,
            Action<string>? closeTabById,
            Action<string> appendActivity,
            Action<string, string> showError,
            Func<string, string, string> getString)
        {
            _agentPane = agentPane;
            _runOnUIThreadAsync = runOnUIThreadAsync;
            _fileModifiedAsync = fileModifiedAsync;
            _revertTabOrFileAsync = revertTabOrFileAsync;
            _closeTabById = closeTabById;
            _appendActivity = appendActivity;
            _showError = showError;
            _getString = getString;
        }

        public IReadOnlyList<AgentFileEditPreview> SessionEdits => _sessionEdits;

        public void Track(AgentFileEditPreview preview)
        {
            // Keep every accepted edit as a chronological undo stack.
            // Do not merge by file here: if A -> B -> C is collapsed into a single
            // entry and the original IsNewFile flag is kept, restore can jump to the
            // wrong state, especially for files created and then edited in the same
            // agent session.
            _sessionEdits.Add(Clone(preview));
            UpdateModifiedFilesList();
        }

        public void Clear()
        {
            _sessionEdits.Clear();
            UpdateModifiedFilesList();
        }

        public void Replace(IEnumerable<AgentFileEditPreview>? edits)
        {
            _sessionEdits.Clear();
            if (edits != null)
            {
                _sessionEdits.AddRange(edits.Select(Clone));
            }

            UpdateModifiedFilesList();
        }

        public async Task RevertAsync(AgentFileEditPreview preview)
        {
            try
            {
                int editIndex = FindLatestEditIndex(preview.FullPath);
                if (editIndex < 0)
                {
                    return;
                }

                AgentFileEditPreview editToRevert = _sessionEdits[editIndex];
                bool isFileBackedPath = IsFileBackedSessionPath(editToRevert.FullPath);

                // Make the durable source match the restored state before notifying
                // the editor/file refresh pipeline.
                if (editToRevert.IsNewFile)
                {
                    if (isFileBackedPath && File.Exists(editToRevert.FullPath))
                    {
                        File.Delete(editToRevert.FullPath);
                    }
                }
                else if (isFileBackedPath)
                {
                    await File.WriteAllTextAsync(editToRevert.FullPath, editToRevert.OldContent);
                }

                if (_fileModifiedAsync != null && isFileBackedPath)
                {
                    await _fileModifiedAsync(editToRevert.FullPath);
                }

                if (_revertTabOrFileAsync != null)
                {
                    await _runOnUIThreadAsync(async () =>
                    {
                        await _revertTabOrFileAsync(editToRevert.FullPath, editToRevert.OldContent, editToRevert.IsNewFile);
                    });
                }
                else if (editToRevert.IsNewFile && !isFileBackedPath && _closeTabById != null)
                {
                    await _runOnUIThreadAsync(() =>
                    {
                        _closeTabById(editToRevert.FullPath);
                        return Task.CompletedTask;
                    });
                }

                _sessionEdits.RemoveAt(editIndex);
                UpdateModifiedFilesList();

                _appendActivity(string.Format(
                    _getString("AgentActivityFileReverted", "파일 변경 취소 완료: {0}"),
                    editToRevert.RelativePath));
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentRevertErrorTitle", "변경 취소 오류"),
                    string.Format(_getString("AgentRevertErrorFormat", "파일을 되돌리는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public string BuildDiffLog()
        {
            if (_sessionEdits.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var edit in _sessionEdits)
            {
                builder.AppendLine($"--- File: {edit.RelativePath} (Action: {edit.ActionName}) ---");
                if (edit.IsNewFile)
                {
                    builder.AppendLine("[New File Content]");
                    builder.AppendLine(edit.NewContent);
                }
                else
                {
                    string diff = GenerateDiff(edit.OldContent, edit.NewContent);
                    builder.AppendLine(diff);
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private int FindLatestEditIndex(string fullPath)
        {
            string targetKey = GetSessionEditKey(fullPath, string.Empty);
            for (int i = _sessionEdits.Count - 1; i >= 0; i--)
            {
                AgentFileEditPreview edit = _sessionEdits[i];
                if (string.Equals(GetSessionEditKey(edit.FullPath, edit.RelativePath), targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private List<AgentFileEditPreview> GetLatestEditsForDisplay()
        {
            var latestByPath = new Dictionary<string, AgentFileEditPreview>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentFileEditPreview edit in _sessionEdits)
            {
                latestByPath[GetSessionEditKey(edit.FullPath, edit.RelativePath)] = edit;
            }

            return latestByPath.Values.ToList();
        }

        private void UpdateModifiedFilesList()
        {
            var displayEdits = GetLatestEditsForDisplay();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.UpdateModifiedFiles(displayEdits);
            });
        }

        private static AgentFileEditPreview Clone(AgentFileEditPreview preview)
        {
            return new AgentFileEditPreview
            {
                ActionName = preview.ActionName,
                RelativePath = preview.RelativePath,
                FullPath = preview.FullPath,
                OldContent = preview.OldContent,
                NewContent = preview.NewContent,
                IsNewFile = preview.IsNewFile
            };
        }

        private static string GetSessionEditKey(string fullPath, string relativePath)
        {
            return !string.IsNullOrWhiteSpace(fullPath)
                ? fullPath
                : relativePath ?? string.Empty;
        }

        private static bool IsFileBackedSessionPath(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    return true;
                }

                return Path.IsPathRooted(fullPath);
            }
            catch
            {
                return false;
            }
        }

        private static string GenerateDiff(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                return "+ " + newText.Replace("\r", "").Replace("\n", "\n+ ");
            }

            if (string.IsNullOrEmpty(newText))
            {
                return "- " + oldText.Replace("\r", "").Replace("\n", "\n- ");
            }

            var oldLines = oldText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var newLines = newText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int n = oldLines.Length;
            int m = newLines.Length;

            if ((long)n * m > 500_000)
            {
                return "[Diff too large to display line-by-line]";
            }

            int[,] lcs = new int[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    if (oldLines[i - 1] == newLines[j - 1])
                    {
                        lcs[i, j] = lcs[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        lcs[i, j] = Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
                    }
                }
            }

            var diffResult = new List<string>();
            int x = n, y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && oldLines[x - 1] == newLines[y - 1])
                {
                    diffResult.Add("  " + oldLines[x - 1]);
                    x--;
                    y--;
                }
                else if (y > 0 && (x == 0 || lcs[x, y - 1] >= lcs[x - 1, y]))
                {
                    diffResult.Add("+ " + newLines[y - 1]);
                    y--;
                }
                else if (x > 0 && (y == 0 || lcs[x, y - 1] < lcs[x - 1, y]))
                {
                    diffResult.Add("- " + oldLines[x - 1]);
                    x--;
                }
            }

            diffResult.Reverse();
            return string.Join("\n", diffResult);
        }
    }
}
