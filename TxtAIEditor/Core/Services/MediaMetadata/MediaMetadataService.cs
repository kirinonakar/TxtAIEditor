using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class MediaMetadataService : IMediaMetadataService
    {
        private readonly IReadOnlyList<IMediaMetadataProvider> _providers;
        private readonly IMediaAlbumArtService _albumArtService;

        private MediaMetadataService(
            IReadOnlyList<IMediaMetadataProvider> providers,
            IMediaAlbumArtService albumArtService)
        {
            _providers = providers;
            _albumArtService = albumArtService;
        }

        public static IMediaMetadataService CreateDefault()
        {
            IMediaMetadataParser[] parsers =
            {
                new Mp3MediaMetadataParser(),
                new LegacyVideoMediaMetadataParser(),
                new WaveFlacOggMediaMetadataParser(),
                new Mp4MediaMetadataParser(),
                new MatroskaMediaMetadataParser()
            };

            IMediaMetadataProvider[] providers =
            {
                new WindowsStorageMediaMetadataProvider(),
                new ContainerMediaMetadataProvider(parsers)
            };

            return new MediaMetadataService(providers, new MediaAlbumArtService());
        }

        public async Task<MediaMetadataResult> ReadAsync(string? filePath)
        {
            var result = new MediaMetadataResult();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return result;
            }

            try
            {
                result.FileSizeBytes = new FileInfo(filePath).Length;
            }
            catch
            {
            }

            result.Container = MediaMetadataUtilities.GetContainerFromExtension(filePath);

            foreach (IMediaMetadataProvider provider in _providers)
            {
                try
                {
                    await provider.EnrichAsync(filePath, result);
                }
                catch
                {
                    // A provider failure must not discard metadata from other providers.
                }
            }

            try
            {
                result.AlbumArtDataUri = await _albumArtService.ReadAsync(filePath);
            }
            catch
            {
            }

            return result;
        }
    }
}
