using Microsoft.UI.Xaml;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IStickyNoteService
    {
        void ShowOrActivate(Window ownerWindow);
        void ApplyTopMost(Window window, bool topMost);
    }
}
