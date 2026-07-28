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
    }

    public interface IAutoSaveLifecycle
    {
        void Stop();
    }
}
