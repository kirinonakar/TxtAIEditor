using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal interface IMediaMetadataProvider
    {
        Task EnrichAsync(string filePath, MediaMetadataResult result);
    }

    internal interface IMediaMetadataParser
    {
        bool CanRead(byte[] header, int bytesRead);

        void Read(Stream stream, MediaMetadataResult result);
    }

    internal interface IMediaAlbumArtService
    {
        Task<string?> ReadAsync(string filePath);
    }
}
