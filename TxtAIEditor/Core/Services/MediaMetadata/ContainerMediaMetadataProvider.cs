using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class ContainerMediaMetadataProvider : IMediaMetadataProvider
    {
        private readonly IReadOnlyList<IMediaMetadataParser> _parsers;

        public ContainerMediaMetadataProvider(IReadOnlyList<IMediaMetadataParser> parsers)
        {
            _parsers = parsers;
        }

        public Task EnrichAsync(string filePath, MediaMetadataResult result)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length <= 0)
                {
                    return Task.CompletedTask;
                }

                byte[] header = new byte[16];
                int bytesRead = stream.Read(header, 0, header.Length);
                foreach (IMediaMetadataParser parser in _parsers)
                {
                    if (!parser.CanRead(header, bytesRead))
                    {
                        continue;
                    }

                    stream.Position = 0;
                    parser.Read(stream, result);
                    break;
                }
            }
            catch
            {
            }

            return Task.CompletedTask;
        }
    }
}
