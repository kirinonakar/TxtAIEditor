using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    internal interface IMediaMetadataService
    {
        Task<MediaMetadataResult> ReadAsync(string? filePath);
    }
}
