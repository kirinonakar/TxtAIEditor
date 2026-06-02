using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public interface ISettingsService
    {
        EditorSettings CurrentSettings { get; }
        Task LoadSettingsAsync();
        Task SaveSettingsAsync(EditorSettings settings);
    }
}
