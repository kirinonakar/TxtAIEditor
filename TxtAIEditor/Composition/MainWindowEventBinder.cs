using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowEventBinder
    {
        public static void Bind(
            MainWindowUiRefs ui,
            SearchReplaceController searchReplace,
            TabMoveController tabMove,
            TabCloseController tabClose,
            MainWindowToolbarCommandController toolbarCommand,
            Action openNewTab,
            Func<Task> saveUiLayoutSettingsAsync)
        {
            ui.LeftSidebar.SearchQueryInputKeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    await searchReplace.HandleSearchQueryEnterAsync();
                }
            };
            ui.LeftSidebar.SearchQuery.TextChanged += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(ui.LeftSidebar.SearchQuery.Text))
                {
                    searchReplace.CancelActiveSearch();
                }
            };
            ui.LeftSidebar.SearchAllFilesClick += async (_, _) => await searchReplace.SearchAllFilesAsync();
            ui.LeftSidebar.ReplaceAllClick += async (_, _) => await searchReplace.ReplaceAllAsync();
            ui.LeftSidebar.ReplaceOneClick += async (sender, _) =>
            {
                if (sender is Button button && button.Tag is SearchResultItem item)
                {
                    await searchReplace.ReplaceOneAsync(item);
                }
            };
            ui.LeftSidebar.SearchResultItemClick += async (_, e) =>
            {
                if (e.ClickedItem is SearchResultItem item)
                {
                    await searchReplace.OpenSearchResultAsync(item);
                }
            };

            ui.EditorWorkspace.PrimaryAddTabButtonClick += (_, _) => openNewTab();
            ui.EditorWorkspace.PrimaryTabCloseRequested += (_, args) => tabClose.CloseRequested(args);
            ui.EditorWorkspace.MoveTabLeftClick += (_, _) => tabMove.MoveLeft();
            ui.EditorWorkspace.MoveTabRightClick += (_, _) => tabMove.MoveRight();
            ui.EditorWorkspace.TerminalPanelHeightChanged += async (_, _) => await saveUiLayoutSettingsAsync();

            ui.PreviewGrid.ModelNameClick += (_, _) => toolbarCommand.ShowModelSettings();
            ui.PreviewGrid.AgentPane.ModelNameClick += (_, _) => toolbarCommand.ShowModelSettings();
        }
    }
}
