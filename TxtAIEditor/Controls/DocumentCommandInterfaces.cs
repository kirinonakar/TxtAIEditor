using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public interface ITabSaveCommands
    {
        Task<bool> SaveAsync(OpenedTab tab);
        Task<bool> SaveAsAsync(OpenedTab tab);
    }

    public interface ITabCloseCommands
    {
        void CloseAndCleanup(OpenedTab tab, TabViewItem tabItem);
        void CloseRightTabs(TabViewItem tabItem, TabView tabView);
        void CloseLeftTabs(TabViewItem tabItem, TabView tabView);
        void CloseOtherTabs(TabViewItem tabItem, TabView tabView);
    }

    public interface IAutoSaveLifecycle
    {
        void Stop();
    }
}
