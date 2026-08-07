using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Storage;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaCodecCatalog;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaMetadataUtilities;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class WindowsStorageMediaMetadataProvider : IMediaMetadataProvider
    {
        public async Task EnrichAsync(string filePath, MediaMetadataResult result)
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var props = file.Properties;

            try
            {
                var music = await props.GetMusicPropertiesAsync();
                if (music.Duration > TimeSpan.Zero)
                {
                    result.Duration = music.Duration;
                }

                if (music.Bitrate > 0)
                {
                    result.Bitrate = music.Bitrate;
                }

                AddTagIfPresent(result, "Title", music.Title);
                AddTagIfPresent(result, "Album", music.Album);
                AddTagIfPresent(result, "AlbumArtist", music.AlbumArtist);
                AddTagIfPresent(result, "Publisher", music.Publisher);
                AddTagIfPresent(result, "Subtitle", music.Subtitle);
                AddTagIfPresent(result, "Artist", music.Artist);
                AddTagIfPresent(result, "Genre", JoinStrings(music.Genre));
                AddTagIfPresent(result, "Composer", JoinStrings(music.Composers));
                AddTagIfPresent(result, "Conductor", JoinStrings(music.Conductors));
                AddTagIfPresent(result, "Writers", JoinStrings(music.Writers));
                AddTagIfPresent(result, "Producers", JoinStrings(music.Producers));
                if (music.TrackNumber > 0)
                {
                    AddTagIfPresent(result, "Track", music.TrackNumber.ToString(CultureInfo.InvariantCulture));
                }

                if (music.Year > 0)
                {
                    AddTagIfPresent(result, "Year", music.Year.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }

            try
            {
                var video = await props.GetVideoPropertiesAsync();
                if (video.Duration > TimeSpan.Zero)
                {
                    result.Duration = video.Duration;
                }

                if (video.Bitrate > 0 && !result.Bitrate.HasValue)
                {
                    result.Bitrate = video.Bitrate;
                }

                if (video.Width > 0 && video.Height > 0)
                {
                    result.Width = video.Width;
                    result.Height = video.Height;
                }

                AddTagIfPresent(result, "Title", video.Title);
                AddTagIfPresent(result, "Publisher", video.Publisher);
                AddTagIfPresent(result, "Subtitle", video.Subtitle);
                AddTagIfPresent(result, "Directors", JoinStrings(video.Directors));
                AddTagIfPresent(result, "Writers", JoinStrings(video.Writers));
                AddTagIfPresent(result, "Producers", JoinStrings(video.Producers));
                AddTagIfPresent(result, "Keywords", JoinStrings(video.Keywords));
                if (video.Year > 0)
                {
                    AddTagIfPresent(result, "Year", video.Year.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }

            string[] extraKeys =
            {
                "System.Video.EncodingFormat",
                "System.Audio.EncodingFormat",
                "System.Video.Compression",
                "System.Video.FrameRate",
                "System.Audio.SampleRate",
                "System.Audio.ChannelCount",
                "System.Media.Duration",
                "System.Video.FrameWidth",
                "System.Video.FrameHeight",
                "System.Video.TotalBitrate",
                "System.Audio.BitRate",
                "System.Music.AlbumArtist",
                "System.Music.Composer",
                "System.Music.Conductor",
                "System.Music.Lyrics",
                "System.Music.Mood",
                "System.Music.BeatsPerMinute",
                "System.Music.InitialKey",
                "System.Music.PartOfSet",
                "System.Media.SubTitle",
                "System.Media.Year",
                "System.Media.Comment",
                "System.Music.Publisher",
                "System.Music.TrackNumber",
                "System.Music.Genre"
            };

            IDictionary<string, object> extra;
            try
            {
                extra = await props.RetrievePropertiesAsync(extraKeys);
            }
            catch
            {
                return;
            }

            if (!result.Duration.HasValue &&
                TryGetUInt64(extra, "System.Media.Duration", out ulong duration100Ns) &&
                duration100Ns > 0)
            {
                result.Duration = TimeSpan.FromTicks((long)(duration100Ns / 100));
            }

            if (result.VideoCodec is null)
            {
                if (TryGetString(extra, "System.Video.Compression", out string? compression))
                {
                    result.VideoCodec = ResolveVideoCodec(compression!);
                }
                else if (TryGetUInt32(extra, "System.Video.EncodingFormat", out uint videoFourCc))
                {
                    result.VideoCodec = ResolveVideoFourCc(videoFourCc);
                }
            }

            if (result.AudioCodec is null &&
                TryGetUInt32(extra, "System.Audio.EncodingFormat", out uint audioFourCc))
            {
                result.AudioCodec = ResolveAudioFourCc(audioFourCc);
            }

            if (!result.FrameRate.HasValue &&
                TryGetDouble(extra, "System.Video.FrameRate", out double frameRate) &&
                frameRate > 0)
            {
                result.FrameRate = frameRate;
            }

            if (!result.SampleRate.HasValue &&
                TryGetUInt32(extra, "System.Audio.SampleRate", out uint sampleRate) &&
                sampleRate > 0)
            {
                result.SampleRate = sampleRate;
            }

            if (!result.Channels.HasValue &&
                TryGetUInt32(extra, "System.Audio.ChannelCount", out uint channels) &&
                channels > 0)
            {
                result.Channels = channels;
            }

            if (!result.Width.HasValue &&
                TryGetUInt32(extra, "System.Video.FrameWidth", out uint width) &&
                width > 0)
            {
                result.Width = width;
            }

            if (!result.Height.HasValue &&
                TryGetUInt32(extra, "System.Video.FrameHeight", out uint height) &&
                height > 0)
            {
                result.Height = height;
            }

            if (TryGetUInt32(extra, "System.Video.TotalBitrate", out uint totalBitrate) && totalBitrate > 0)
            {
                result.Bitrate = totalBitrate;
            }
            else if (!result.Bitrate.HasValue &&
                     TryGetUInt32(extra, "System.Audio.BitRate", out uint audioBitrate) &&
                     audioBitrate > 0)
            {
                result.Bitrate = audioBitrate;
            }

            AddTagIfPresent(result, "AlbumArtist", GetString(extra, "System.Music.AlbumArtist"));
            AddTagIfPresent(result, "Composer", GetString(extra, "System.Music.Composer"));
            AddTagIfPresent(result, "Conductor", GetString(extra, "System.Music.Conductor"));
            AddTagIfPresent(result, "Lyrics", GetString(extra, "System.Music.Lyrics"));
            AddTagIfPresent(result, "Mood", GetString(extra, "System.Music.Mood"));
            AddTagIfPresent(result, "Publisher", GetString(extra, "System.Music.Publisher"));
            AddTagIfPresent(result, "Subtitle", GetString(extra, "System.Media.SubTitle"));
            AddTagIfPresent(result, "Comment", GetString(extra, "System.Media.Comment"));
            if (!result.Tags.ContainsKey("Year") && TryGetString(extra, "System.Media.Year", out string? year))
            {
                AddTagIfPresent(result, "Year", year);
            }

            if (!result.Tags.ContainsKey("Track") && TryGetString(extra, "System.Music.TrackNumber", out string? track))
            {
                AddTagIfPresent(result, "Track", track);
            }

            if (!result.Tags.ContainsKey("Genre") && TryGetString(extra, "System.Music.Genre", out string? genre))
            {
                AddTagIfPresent(result, "Genre", genre);
            }

            if (TryGetUInt32(extra, "System.Music.BeatsPerMinute", out uint bpm) && bpm > 0)
            {
                AddTagIfPresent(result, "BPM", bpm.ToString(CultureInfo.InvariantCulture));
            }

            if (TryGetString(extra, "System.Music.InitialKey", out string? key))
            {
                AddTagIfPresent(result, "Key", key);
            }

            if (TryGetString(extra, "System.Music.PartOfSet", out string? disc))
            {
                AddTagIfPresent(result, "Disc", disc);
            }
        }

        // ── Extended property value helpers ──────────────────────────────────

        private static string? GetString(IDictionary<string, object> extra, string key)
        {
            return TryGetString(extra, key, out string? value) ? value : null;
        }

        private static bool TryGetString(IDictionary<string, object> extra, string key, out string? value)
        {
            value = null;
            if (!extra.TryGetValue(key, out object? raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    value = s.Trim();
                    return true;
                case IEnumerable<string> list:
                    value = JoinStrings(list);
                    return !string.IsNullOrEmpty(value);
                default:
                    if (raw is uint or ulong or int or long or double or float)
                    {
                        value = Convert.ToString(raw, CultureInfo.InvariantCulture);
                        return !string.IsNullOrWhiteSpace(value);
                    }

                    return false;
            }
        }

        private static bool TryGetUInt32(IDictionary<string, object> extra, string key, out uint value)
        {
            value = 0;
            if (!extra.TryGetValue(key, out object? raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case uint u:
                    value = u;
                    return true;
                case ulong ul when ul <= uint.MaxValue:
                    value = (uint)ul;
                    return true;
                case int i when i > 0:
                    value = (uint)i;
                    return true;
                case long l when l > 0 && l <= uint.MaxValue:
                    value = (uint)l;
                    return true;
                case double d when d > 0 && d <= uint.MaxValue:
                    value = (uint)d;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetUInt64(IDictionary<string, object> extra, string key, out ulong value)
        {
            value = 0;
            if (!extra.TryGetValue(key, out object? raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case ulong ul:
                    value = ul;
                    return true;
                case uint u:
                    value = u;
                    return true;
                case int i when i >= 0:
                    value = (ulong)i;
                    return true;
                case long l when l >= 0:
                    value = (ulong)l;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetDouble(IDictionary<string, object> extra, string key, out double value)
        {
            value = 0;
            if (!extra.TryGetValue(key, out object? raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case double d:
                    value = d;
                    return true;
                case float f:
                    value = f;
                    return true;
                case uint u:
                    value = u;
                    return true;
                case ulong ul:
                    value = ul;
                    return true;
                case int i:
                    value = i;
                    return true;
                case long l:
                    value = l;
                    return true;
                default:
                    return false;
            }
        }
    }
}
