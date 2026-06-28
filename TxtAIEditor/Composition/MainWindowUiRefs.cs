using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;

namespace TxtAIEditor.Composition
{
    public sealed record MainWindowUiRefs(
        Grid RootGrid,
        Grid AppTitleBar,
        RowDefinition TitleBarRow,
        TextBlock AppTitleTextBlock,
        TopCommandBarPane TopToolbar,
        Grid MarkdownToolbarHost,
        MarkdownToolbarControl MarkdownToolbar,
        Grid MainWorkGrid,
        ColumnDefinition ExplorerColumn,
        ColumnDefinition PreviewColumn,
        TxtAIEditor.CustomSplitter LeftSplitter,
        TxtAIEditor.CustomSplitter RightSplitter,
        LeftSidebarPane LeftSidebar,
        EditorWorkspacePane EditorWorkspace,
        RightSidebarPane PreviewGrid,
        StatusBarPane StatusBar,
        Grid DragOverlay,
        TabView EditorTabView,
        TabView EditorTabView2,
        TerminalPane TerminalPane,
        FrameworkElement RootElement);
}
