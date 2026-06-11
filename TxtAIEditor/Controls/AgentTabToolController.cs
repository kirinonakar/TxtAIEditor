using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentTabToolController
    {
        private readonly Func<OpenedTab?> _activeTabForContextProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Func<OpenedTab, int, string> _getTabText;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Func<string?, string, OpenedTab> _openNewTabWithContent;
        private readonly Func<OpenedTab, string?, Task<bool>>? _saveTabAsync;
        private readonly Func<OpenedTab, string, Task<bool>>? _editTabAsync;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly Func<Action, Task> _runOnUIThreadAsync;
        private readonly Func<Func<OpenedTab>, Task<OpenedTab>> _runOnUIThreadForTabAsync;
        private readonly Func<Func<Task<bool>>, Task<bool>> _runOnUIThreadForBoolAsync;

        public AgentTabToolController(
            Func<OpenedTab?> activeTabForContextProvider,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Func<string?, string, OpenedTab> openNewTabWithContent,
            Func<OpenedTab, string?, Task<bool>>? saveTabAsync,
            Func<OpenedTab, string, Task<bool>>? editTabAsync,
            AgentFileToolService fileTools,
            AgentSessionEditController sessionEditController,
            Func<Action, Task> runOnUIThreadAsync,
            Func<Func<OpenedTab>, Task<OpenedTab>> runOnUIThreadForTabAsync,
            Func<Func<Task<bool>>, Task<bool>> runOnUIThreadForBoolAsync)
        {
            _activeTabForContextProvider = activeTabForContextProvider;
            _activeTabProvider = activeTabProvider;
            _openTabsProvider = openTabsProvider;
            _getTabText = getTabText;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _openNewTabWithContent = openNewTabWithContent;
            _saveTabAsync = saveTabAsync;
            _editTabAsync = editTabAsync;
            _fileTools = fileTools;
            _sessionEditController = sessionEditController;
            _runOnUIThreadAsync = runOnUIThreadAsync;
            _runOnUIThreadForTabAsync = runOnUIThreadForTabAsync;
            _runOnUIThreadForBoolAsync = runOnUIThreadForBoolAsync;
        }

        public async Task<string> InsertTextAsync(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "insert_text failed: content is empty.";
            }

            var activeTab = _activeTabForContextProvider();
            if (activeTab == null)
            {
                return "insert_text failed: no active tab.";
            }

            string oldContent = _getTabText(activeTab, int.MaxValue);

            bool inserted = await _runOnUIThreadForBoolAsync(async () => await _insertIntoActiveEditorAsync(content));
            if (!inserted)
            {
                return "insert_text failed: active editor did not accept the text.";
            }

            await Task.Delay(200);

            string newContent = _getTabText(activeTab, int.MaxValue);
            string relPath = string.IsNullOrEmpty(activeTab.FilePath)
                ? activeTab.Title
                : Path.GetRelativePath(_fileTools.WorkspaceRoot, activeTab.FilePath).Replace('\\', '/');
            string fullPath = string.IsNullOrEmpty(activeTab.FilePath)
                ? activeTab.Id
                : activeTab.FilePath;

            var preview = new AgentFileEditPreview
            {
                ActionName = "insert_text",
                RelativePath = relPath,
                FullPath = fullPath,
                OldContent = oldContent,
                NewContent = newContent,
                IsNewFile = false
            };

            await _runOnUIThreadAsync(() => _sessionEditController.Track(preview));

            return $"inserted into active editor: {content.Length:N0} chars";
        }

        public async Task<string> CreateTabAsync(JsonElement arguments)
        {
            try
            {
                string content = GetFirstStringArgument(arguments, "content", "text", "newText", "new_text");
                if (string.IsNullOrEmpty(content))
                {
                    return "create_tab failed: content is empty.";
                }

                string title = GetFirstStringArgument(arguments, "title", "name", "fileName", "file_name");
                OpenedTab tab = await _runOnUIThreadForTabAsync(() => _openNewTabWithContent(
                    string.IsNullOrWhiteSpace(title) ? null : title,
                    content));

                string displayTitle = string.IsNullOrWhiteSpace(tab.Title) ? "Untitled" : tab.Title;

                var preview = new AgentFileEditPreview
                {
                    ActionName = "create_tab",
                    RelativePath = displayTitle,
                    FullPath = tab.Id,
                    OldContent = string.Empty,
                    NewContent = content,
                    IsNewFile = true
                };

                await _runOnUIThreadAsync(() => _sessionEditController.Track(preview));

                return $"created new tab: {displayTitle} ({content.Length:N0} chars)";
            }
            catch (Exception ex)
            {
                return $"create_tab failed with exception: {ex}";
            }
        }

        public async Task<string> SaveTabAsync(JsonElement arguments)
        {
            try
            {
                if (_saveTabAsync == null)
                {
                    return "save_tab failed: save operation is not supported by the host.";
                }

                OpenedTab? tab = FindTab(arguments);
                if (tab == null)
                {
                    string titleOrId = GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id");
                    return string.IsNullOrEmpty(titleOrId)
                        ? "save_tab failed: no active tab to save."
                        : $"save_tab failed: tab not found for '{titleOrId}'.";
                }

                if (tab.IsReadOnlyViewer)
                {
                    return "save_tab failed: this is a read-only viewer tab and cannot be saved.";
                }

                string path = GetFirstStringArgument(arguments, "path", "filePath", "file_path");
                string? originalFilePath = tab.FilePath;

                string? resolvedPath = null;
                if (!string.IsNullOrEmpty(path))
                {
                    string root = _fileTools.WorkspaceRoot;
                    resolvedPath = Path.IsPathRooted(path)
                        ? Path.GetFullPath(path)
                        : Path.GetFullPath(Path.Combine(root, path));

                    if (!resolvedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"save_tab failed: path '{path}' escapes workspace root.";
                    }
                }
                else if (string.IsNullOrEmpty(tab.FilePath))
                {
                    return "save_tab failed: the tab is unsaved (has no file path) and no destination path was specified. Provide a 'path' argument.";
                }

                bool success = await _runOnUIThreadForBoolAsync(async () => await _saveTabAsync(tab, resolvedPath));
                if (!success)
                {
                    return "save_tab failed: save operation failed or was cancelled.";
                }

                string finalPath = tab.FilePath ?? string.Empty;
                string relativePath = GetRelativePathForDisplay(finalPath);

                var preview = new AgentFileEditPreview
                {
                    ActionName = "save_tab",
                    RelativePath = relativePath,
                    FullPath = finalPath,
                    OldContent = string.Empty,
                    NewContent = tab.Content,
                    IsNewFile = string.IsNullOrEmpty(originalFilePath)
                };
                await _runOnUIThreadAsync(() => _sessionEditController.Track(preview));

                return $"successfully saved tab to: {relativePath}";
            }
            catch (Exception ex)
            {
                return $"save_tab failed with exception: {ex.Message}";
            }
        }

        public async Task<string> EditTabAsync(JsonElement arguments)
        {
            try
            {
                if (_editTabAsync == null)
                {
                    return "edit_tab failed: edit operation is not supported by the host.";
                }

                OpenedTab? tab = FindTab(arguments);
                if (tab == null)
                {
                    string titleOrId = GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id");
                    return string.IsNullOrEmpty(titleOrId)
                        ? "edit_tab failed: no active tab to edit."
                        : $"edit_tab failed: tab not found for '{titleOrId}'.";
                }

                if (tab.IsReadOnlyViewer)
                {
                    return "edit_tab failed: this is a read-only viewer tab and cannot be edited.";
                }

                string content = GetFirstStringArgument(arguments, "content", "newText", "new_text", "text");
                if (content == null)
                {
                    return "edit_tab failed: content argument is missing.";
                }

                string oldContent = tab.Content;
                if (string.Equals(oldContent, content, StringComparison.Ordinal))
                {
                    return "edit_tab completed: content is already identical.";
                }

                bool success = await _runOnUIThreadForBoolAsync(async () => await _editTabAsync(tab, content));
                if (!success)
                {
                    return "edit_tab failed: edit operation failed or was cancelled.";
                }

                string finalPath = tab.FilePath ?? string.Empty;
                string relativePath = GetRelativePathForDisplay(finalPath);
                if (string.IsNullOrEmpty(relativePath))
                {
                    relativePath = tab.Title ?? "Untitled";
                }

                var preview = new AgentFileEditPreview
                {
                    ActionName = "edit_tab",
                    RelativePath = relativePath,
                    FullPath = finalPath,
                    OldContent = oldContent,
                    NewContent = content,
                    IsNewFile = false
                };
                await _runOnUIThreadAsync(() => _sessionEditController.Track(preview));

                return $"successfully edited tab: {relativePath}";
            }
            catch (Exception ex)
            {
                return $"edit_tab failed with exception: {ex.Message}";
            }
        }

        private OpenedTab? FindTab(JsonElement arguments)
        {
            string titleOrId = GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id");
            if (string.IsNullOrEmpty(titleOrId))
            {
                return _activeTabProvider();
            }

            var openTabs = _openTabsProvider();
            return openTabs.FirstOrDefault(t => string.Equals(t.Id, titleOrId, StringComparison.OrdinalIgnoreCase))
                  ?? openTabs.FirstOrDefault(t => string.Equals(t.Title, titleOrId, StringComparison.OrdinalIgnoreCase))
                  ?? openTabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.FilePath) && string.Equals(Path.GetFileName(t.FilePath), titleOrId, StringComparison.OrdinalIgnoreCase));
        }

        private string GetRelativePathForDisplay(string finalPath)
        {
            string relativePath = finalPath;
            try
            {
                if (finalPath.StartsWith(_fileTools.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = finalPath.Substring(_fileTools.WorkspaceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch { }

            return relativePath;
        }
    }
}
