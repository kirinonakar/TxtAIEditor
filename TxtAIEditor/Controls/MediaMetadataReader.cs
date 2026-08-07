using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.MediaMetadata;

namespace TxtAIEditor.Controls
{
    /// <summary>
    /// Compatibility facade for media metadata consumers in the Controls layer.
    /// Parsing and album-art responsibilities are composed by <see cref="MediaMetadataService"/>.
    /// </summary>
    internal static class MediaMetadataReader
    {
        private static readonly IMediaMetadataService Service = MediaMetadataService.CreateDefault();

        public static Task<MediaMetadataResult> ReadAsync(string? filePath)
        {
            return Service.ReadAsync(filePath);
        }
    }
}
