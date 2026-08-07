using System;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Models
{
    /// <summary>
    /// Holds parsed metadata for an audio/video file.
    /// </summary>
    internal sealed class MediaMetadataResult
    {
        public string? Container { get; set; }

        public bool HasAudioTrack { get; set; }
        public bool HasVideoTrack { get; set; }

        public TimeSpan? Duration { get; set; }
        public long FileSizeBytes { get; set; }
        public string? AudioCodec { get; set; }
        public string? VideoCodec { get; set; }

        public uint? Bitrate { get; set; }
        public uint? SampleRate { get; set; }
        public uint? Channels { get; set; }
        public uint? BitsPerSample { get; set; }
        public uint? Width { get; set; }
        public uint? Height { get; set; }
        public double? FrameRate { get; set; }

        public string? AlbumArtDataUri { get; set; }

        public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasAny =>
            Duration.HasValue ||
            AudioCodec != null ||
            VideoCodec != null ||
            Bitrate.HasValue ||
            SampleRate.HasValue ||
            Channels.HasValue ||
            BitsPerSample.HasValue ||
            Width.HasValue ||
            Height.HasValue ||
            FrameRate.HasValue ||
            Tags.Count > 0;
    }
}
