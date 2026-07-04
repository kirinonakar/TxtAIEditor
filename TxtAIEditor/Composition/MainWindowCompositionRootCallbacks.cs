using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowCompositionRootCallbacks(
        Func<ElementTheme> GetCurrentElementTheme,
        Func<string, string, string> GetLocalizedString,
        Action UpdateWindowTitle,
        Func<OpenedTab, string, Task> ReloadTabWithEncodingAsync,
        Action<OpenedTab> MarkTabDirtyFromStatusBar,
        Func<string, int, Task> PerformLineNavigationAsync,
        Action ToggleMaximize,
        Func<string, Task> LoadFileIntoTabAsync,
        Func<string, int, Task> LoadFileIntoTabAtLineAsync,
        Func<string, Task<AgentOpenFileResult>> LoadFileIntoTabForAgentAsync,
        Action<string, OpenedTab, int, int> UpdateRightPanelSelectionContext,
        Action<OpenedTab> UpdateLivePreview,
        Action<OpenedTab> UpdateLanguageUi,
        Action<OpenedTab> SchedulePreview,
        Action FocusSearchPanel,
        Action EnsureLeftPanelVisible,
        Action<int> ShowLeftSidebarPage,
        Func<FileTabOpenRequest, OpenedTab> OpenNewTabFromRequest,
        Func<OpenedTab> OpenNewTab,
        Func<string, OpenedTab> OpenGeneratedTab,
        Func<string, OpenedTab> OpenImageTab,
        Func<string, OpenedTab> OpenPdfTab,
        Func<string, OpenedTab> OpenOfficeDocumentTab,
        Func<OpenedTab, Task> OpenHexViewAsync,
        Action CloseActiveTab,
        Func<Task> SyncSnippetsToOpenEditorsAsync,
        Action<object> InitializePickerWindow,
        Func<IReadOnlyList<AgentFileEditPreview>> GetAgentSessionEdits,
        Action<OpenedTab, TabViewItem> CloseTabAndCleanup,
        Action<string> CloseReadOnlyViewer,
        Func<OpenedTab, Task<bool>> SaveTabAsync,
        Func<OpenedTab, string> GetPreviewBaseHref,
        Action RefreshActivePreview,
        Action LocalizeUi,
        Action SyncAgentSettingsAfterLoad,
        Action UpdateAutoSaveStatus,
        Func<ExplorerItem?> GetSelectedExplorerItem,
        Action<string> SetCurrentRepoPath,
        Action<string> SetCurrentFolderPath,
        Func<bool> IsStartupInitializationComplete,
        EventHandler<string> GitFileRestored);
}
