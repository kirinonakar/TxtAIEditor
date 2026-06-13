using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace TxtAIEditor.Controls
{
    public sealed class TerminalPanelController
    {
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TopCommandBarPane _topToolbar;
        private readonly Func<TxtAIEditor.ExplorerItem?> _selectedExplorerItemProvider;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<string> _currentRepoProvider;
        private readonly Func<string, int, Task> _openFileAsync;
        private readonly Func<string, Task> _navigateFolderAsync;
        private readonly Window _owner;
        private TerminalPane? _terminalPane;

        public TerminalPanelController(
            Window owner,
            EditorWorkspacePane editorWorkspace,
            TopCommandBarPane topToolbar,
            Func<TxtAIEditor.ExplorerItem?> selectedExplorerItemProvider,
            Func<string> currentFolderProvider,
            Func<string> currentRepoProvider,
            Func<string, int, Task> openFileAsync,
            Func<string, Task> navigateFolderAsync)
        {
            _owner = owner;
            _editorWorkspace = editorWorkspace;
            _topToolbar = topToolbar;
            _selectedExplorerItemProvider = selectedExplorerItemProvider;
            _currentFolderProvider = currentFolderProvider;
            _currentRepoProvider = currentRepoProvider;
            _openFileAsync = openFileAsync;
            _navigateFolderAsync = navigateFolderAsync;
        }

        private TerminalPane EnsureTerminalPane()
        {
            if (_terminalPane != null)
            {
                return _terminalPane;
            }

            _terminalPane = _editorWorkspace.TerminalPaneControl;
            _terminalPane.AttachOwner(_owner);
            _terminalPane.WorkingDirectoryProvider = GetWorkingDirectory;
            _terminalPane.SessionsEmptied += OnSessionsEmptied;
            _terminalPane.CloseRequested += OnCloseRequested;
            _terminalPane.PathOpenRequested += OnPathOpenRequested;

            return _terminalPane;
        }

        public void Toggle()
        {
            EnsureTerminalPane();
            _topToolbar.TerminalIsChecked = _editorWorkspace.ToggleTerminal(GetWorkingDirectory);
        }

        private string GetWorkingDirectory()
        {
            if (_selectedExplorerItemProvider() is TxtAIEditor.ExplorerItem selectedItem)
            {
                if (selectedItem.IsFolder && Directory.Exists(selectedItem.Path))
                {
                    return selectedItem.Path;
                }

                string? selectedFileDirectory = Path.GetDirectoryName(selectedItem.Path);
                if (!string.IsNullOrWhiteSpace(selectedFileDirectory) && Directory.Exists(selectedFileDirectory))
                {
                    return selectedFileDirectory;
                }
            }

            string currentFolderPath = _currentFolderProvider();
            if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
            {
                return currentFolderPath;
            }

            string currentRepoPath = _currentRepoProvider();
            if (!string.IsNullOrWhiteSpace(currentRepoPath) && Directory.Exists(currentRepoPath))
            {
                return currentRepoPath;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void OnCloseRequested(object? sender, EventArgs e)
        {
            Toggle();
        }

        private void OnSessionsEmptied(object? sender, EventArgs e)
        {
            if (_editorWorkspace.HideTerminalPanelIfEmpty())
            {
                _topToolbar.TerminalIsChecked = false;
            }
        }

        private async void OnPathOpenRequested(object? sender, string path)
        {
            try
            {
                string filePath = path;
                int lineNumber = 1;
                var match = Regex.Match(path, @"^(?<path>.+?):(?<line>\d+)(?::\d+)?$");
                if (match.Success)
                {
                    filePath = match.Groups["path"].Value;
                    int.TryParse(match.Groups["line"].Value, out lineNumber);
                }

                if (File.Exists(filePath))
                {
                    await _openFileAsync(filePath, lineNumber);
                    return;
                }

                if (Directory.Exists(filePath))
                {
                    await _navigateFolderAsync(filePath);
                    return;
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open terminal path '{path}': {ex.Message}");
            }
        }
    }
}
