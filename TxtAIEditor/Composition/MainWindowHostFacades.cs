using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Composition
{
    internal interface IMainWindowShellFacade
    {
        ElementTheme GetCurrentElementTheme();
        string GetLocalizedString(string key, string fallback);
        void UpdateWindowTitle();
        void ToggleMaximize();
        void FocusSearchPanel();
        void EnsureLeftPanelVisible();
        void ShowLeftSidebarPage(int index);
        void InitializePickerWindow(object picker);
    }

    internal interface IMainWindowEditorFacade
    {
        Task ReloadTabWithEncodingAsync(OpenedTab tab, string encodingName);
        void MarkTabDirtyFromStatusBar(OpenedTab tab);
        Task PerformLineNavigationAsync(string tabId, int targetLine);
        void UpdateLivePreview(OpenedTab tab);
        void UpdateLanguageUi(OpenedTab tab);
        void SchedulePreview(OpenedTab tab);
        void UpdateRightPanelSelectionContext(
            string selectedText,
            OpenedTab tab,
            int startLine,
            int endLine);
        Task SetHexViewModeAsync(OpenedTab tab, bool enabled);
        Task SyncSnippetsToOpenEditorsAsync();
    }

    internal interface IMainWindowDocumentFacade
    {
        Task LoadFileIntoTabAsync(string filePath);
        Task LoadFileIntoTabAsync(string filePath, int lineNumber);
        OpenedTab OpenEmptyTab();
        OpenedTab OpenNewTab(FileTabOpenRequest request);
        OpenedTab OpenGeneratedTab(string content);
        void CloseActiveTab();
        void MoveActiveTabLeft();
        void MoveActiveTabRight();
        void CloseTabAndCleanup(OpenedTab tab, TabViewItem tabItem);
        Task<bool> SaveTabAsync(OpenedTab tab);
    }

    internal interface IMainWindowPreviewFacade
    {
        OpenedTab OpenImageTab(string filePath);
        OpenedTab OpenMediaTab(string filePath);
        OpenedTab OpenPdfTab(string filePath);
        OpenedTab OpenOfficeDocumentTab(string filePath);
        OpenedTab OpenHexTab(string filePath);
        OpenedTab OpenNotebookTab(string filePath);
        Task OpenNotebookSourceTabAsync(string filePath);
        Task OpenNotebookViewerTabAsync(string filePath);
        void CloseReadOnlyViewer(string tabId);
        string GetPreviewBaseHref(OpenedTab tab);
        void RefreshActivePreview();
    }

    internal interface IMainWindowAgentFacade
    {
        Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath);
        IReadOnlyList<AgentFileEditPreview> GetAgentSessionEdits();
        void SyncAgentSettingsAfterLoad();
    }

    internal interface IMainWindowWorkspaceFacade
    {
        ExplorerItem? GetSelectedExplorerItem();
        void SetCurrentRepoPath(string repoPath);
        void SetCurrentFolderPath(string folderPath);
        void HandleGitFileRestored(object? sender, string filePath);
    }

    internal interface IMainWindowLifecycleFacade
    {
        bool IsStartupInitializationComplete { get; }
        void LocalizeUi();
        void UpdateAutoSaveStatus();
    }

    internal sealed class MainWindowHostFacades
    {
        public MainWindowHostFacades(
            IMainWindowShellFacade shell,
            IMainWindowEditorFacade editor,
            IMainWindowDocumentFacade documents,
            IMainWindowPreviewFacade preview,
            IMainWindowAgentFacade agents,
            IMainWindowWorkspaceFacade workspace,
            IMainWindowLifecycleFacade lifecycle)
        {
            Shell = shell;
            Editor = editor;
            Documents = documents;
            Preview = preview;
            Agents = agents;
            Workspace = workspace;
            Lifecycle = lifecycle;
        }

        public IMainWindowShellFacade Shell { get; }
        public IMainWindowEditorFacade Editor { get; }
        public IMainWindowDocumentFacade Documents { get; }
        public IMainWindowPreviewFacade Preview { get; }
        public IMainWindowAgentFacade Agents { get; }
        public IMainWindowWorkspaceFacade Workspace { get; }
        public IMainWindowLifecycleFacade Lifecycle { get; }
    }
}
