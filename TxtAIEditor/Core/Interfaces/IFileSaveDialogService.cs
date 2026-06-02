using Microsoft.UI.Xaml;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IFileSaveDialogService
    {
        string? ShowSaveDialog(Window owner, string suggestedName, string? initialDirectory);
    }
}
