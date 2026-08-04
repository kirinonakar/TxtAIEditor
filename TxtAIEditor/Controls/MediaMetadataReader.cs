using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace TxtAIEditor.Controls
{
    /// <summary>
    /// Holds parsed metadata for an audio/video file: duration, size, codecs,
    /// stream parameters and tags (ID3, Vorbis comments, iTunes ilst, ...).
    /// </summary>
    internal sealed class MediaMetadataResult
    {
        /// <summary>Container label, e.g. "MP3", "MP4", "FLAC".</summary>
        public string? Container { get; set; }

        public bool HasAudioTrack { get; set; }
        public bool HasVideoTrack { get; set; }

        public TimeSpan? Duration { get; set; }
        public long FileSizeBytes { get; set; }
        public string? AudioCodec { get; set; }
        public string? VideoCodec { get; set; }

        /// <summary>Overall bitrate in bits per second.</summary>
        public uint? Bitrate { get; set; }

        public uint? SampleRate { get; set; }
        public uint? Channels { get; set; }
        public uint? BitsPerSample { get; set; }
        public uint? Width { get; set; }
        public uint? Height { get; set; }
        public double? FrameRate { get; set; }

        public string? AlbumArtDataUri { get; set; }

        /// <summary>Canonical tag keys: Title, Artist, Album, Year, Genre, Track, ...</summary>
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

    internal static class MediaMetadataReader
    {
        public static async Task<MediaMetadataResult> ReadAsync(string? filePath)
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

            result.Container = GetContainerFromExtension(filePath);

            try
            {
                await ReadWithStoragePropertiesAsync(filePath, result);
            }
            catch
            {
                // Windows.Storage unavailable for this path -> fall back below.
            }

            // Fill any gaps the property system left behind (codecs, resolution,
            // tags). Fallback parsers only set values that are still missing.
            TryReadContainerFallback(filePath, result);

            result.AlbumArtDataUri = await GetAlbumArtAsync(filePath);

            return result;
        }

        // ── Primary path: Windows property system ────────────────────────────

        private static async Task ReadWithStoragePropertiesAsync(string filePath, MediaMetadataResult result)
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

        // ── Codec name resolution ────────────────────────────────────────────

        private static readonly Dictionary<string, string> VideoCodecNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["avc1"] = "H.264 / AVC",
            ["h264"] = "H.264 / AVC",
            ["x264"] = "H.264 / AVC",
            ["avc3"] = "H.264 / AVC",
            ["hev1"] = "H.265 / HEVC",
            ["hvc1"] = "H.265 / HEVC",
            ["h265"] = "H.265 / HEVC",
            ["vp09"] = "VP9",
            ["vp80"] = "VP8",
            ["av01"] = "AV1",
            ["mp4v"] = "MPEG-4 Part 2",
            ["xvid"] = "Xvid (MPEG-4 Part 2)",
            ["divx"] = "DivX (MPEG-4 Part 2)",
            ["wmv1"] = "WMV 7",
            ["wmv2"] = "WMV 8",
            ["wmv3"] = "WMV 9",
            ["wvc1"] = "VC-1",
            ["vc-1"] = "VC-1",
            ["mjpg"] = "MJPEG",
            ["mjpa"] = "MJPEG",
            ["theo"] = "Theora",
            ["mpg1"] = "MPEG-1 Video",
            ["mpg2"] = "MPEG-2 Video",
            ["mpeg"] = "MPEG Video",
            ["h263"] = "H.263",
            ["flv1"] = "FLV1 (Sorenson H.263)",
            ["fmp4"] = "MPEG-4 (AVC)"
        };

        private static readonly Dictionary<string, string> AudioFourCcNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4a"] = "AAC",
            ["alac"] = "ALAC",
            ["ac-3"] = "AC-3",
            ["ec-3"] = "E-AC-3",
            ["dtsc"] = "DTS",
            ["dtsh"] = "DTS-HD",
            ["opus"] = "Opus",
            ["vorb"] = "Vorbis",
            ["flac"] = "FLAC",
            ["mp3 "] = "MP3",
            ["pcm "] = "PCM",
            ["twos"] = "PCM (Big Endian)",
            ["sowt"] = "PCM (Little Endian)",
            ["lpcm"] = "LPCM",
            ["in24"] = "PCM 24-bit",
            ["in32"] = "PCM 32-bit",
            ["fl32"] = "Float 32",
            ["fl64"] = "Float 64",
            ["wma1"] = "WMA v1",
            ["wma2"] = "WMA v2",
            ["wmav2"] = "WMA v2",
            ["wmav3"] = "WMA Pro",
            ["samr"] = "AMR-NB",
            ["sawb"] = "AMR-WB",
            ["celt"] = "CELT"
        };

        private static readonly Dictionary<uint, string> WaveFormatNames = new()
        {
            [1] = "PCM",
            [2] = "MS ADPCM",
            [3] = "IEEE Float",
            [6] = "A-law",
            [7] = "\u00B5-law",
            [0x11] = "IMA ADPCM",
            [0x55] = "MP3",
            [0x1500] = "AAC (MPEG-2)",
            [0x1600] = "AAC (MPEG-4)",
            [0x1610] = "AAC HE",
            [0x161] = "WMA v1",
            [0x162] = "WMA v2",
            [0x163] = "WMA Pro",
            [0x2000] = "AC-3",
            [0x2001] = "DTS",
            [0x674F] = "Vorbis",
            [0x6771] = "Vorbis",
            [0xF1AC] = "FLAC"
        };

        private static string? ResolveVideoCodec(string code)
        {
            string trimmed = code.Trim();
            return VideoCodecNames.TryGetValue(trimmed, out string? name) ? name : trimmed;
        }

        private static string? ResolveVideoFourCc(uint fourCc)
        {
            string? code = DecodeFourCc(fourCc);
            return code is null ? null : ResolveVideoCodec(code);
        }

        private static string? ResolveAudioFourCc(uint fourCc)
        {
            string? code = DecodeFourCc(fourCc);
            if (code != null)
            {
                if (AudioFourCcNames.TryGetValue(code, out string? name))
                {
                    return name;
                }

                return code.Trim();
            }

            return WaveFormatNames.TryGetValue(fourCc, out string? waveName) ? waveName : null;
        }

        private static string? ResolveAudioCodec(string code)
        {
            string trimmed = code.Trim();
            return AudioFourCcNames.TryGetValue(trimmed, out string? name) ? name : trimmed;
        }

        private static string? DecodeFourCc(uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value); // little-endian on Windows
            char[] le = new char[4];
            for (int i = 0; i < 4; i++)
            {
                le[i] = (char)bytes[i];
            }

            if (IsPrintable(le))
            {
                return new string(le);
            }

            char[] be = { le[3], le[2], le[1], le[0] };
            return IsPrintable(be) ? new string(be) : null;
        }

        private static bool IsPrintable(char[] chars)
        {
            foreach (char c in chars)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        // ── Fallback: manual container parsing ───────────────────────────────

        private static void TryReadContainerFallback(string filePath, MediaMetadataResult result)
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
                    return;
                }

                byte[] magic = new byte[16];
                int read = stream.Read(magic, 0, magic.Length);
                stream.Position = 0;

                if (read >= 3 && magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
                {
                    TryReadMp3(stream, result);
                }
                else if (read >= 4 && magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0)
                {
                    TryReadMp3(stream, result);
                }
                else if (read >= 12 && magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' &&
                         magic[3] == 'F' && magic[8] == 'A' && magic[9] == 'V' &&
                         magic[10] == 'I' && magic[11] == ' ')
                {
                    TryReadAvi(stream, result);
                }
                else if (read >= 4 && magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
                {
                    TryReadWav(stream, result);
                }
                else if (read >= 4 && magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
                {
                    TryReadFlac(stream, result);
                }
                else if (read >= 8 && magic[4] == 'f' && magic[5] == 't' && magic[6] == 'y' && magic[7] == 'p')
                {
                    TryReadMp4(stream, result);
                }
                else if (read >= 4 && magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
                {
                    TryReadOgg(stream, result);
                }
                else if (read >= 4 && magic[0] == 0x1A && magic[1] == 0x45 && magic[2] == 0xDF && magic[3] == 0xA3)
                {
                    TryReadMkv(stream, result);
                }
                else if (read >= 4 && magic[0] == 'F' && magic[1] == 'L' && magic[2] == 'V')
                {
                    TryReadFlv(stream, result);
                }
                else if (read >= 16 && magic[0] == 0x30 && magic[1] == 0x26 && magic[2] == 0xB2 && magic[3] == 0x75)
                {
                    TryReadAsf(stream, result);
                }
                else if (read >= 4 && magic[0] == 0 && magic[1] == 0 && magic[2] == 1 &&
                         (magic[3] == 0xBA || magic[3] == 0xB3))
                {
                    TryReadMpegProgram(stream, result);
                }
                else if (read >= 4 && magic[0] == 0x47)
                {
                    TryReadMpegTs(stream, result);
                }
            }
            catch
            {
            }
        }

        // ── MP3 / ID3 ────────────────────────────────────────────────────────

        private static void TryReadMp3(Stream stream, MediaMetadataResult result)
        {
            int id3Size = 0;
            byte[] header = new byte[10];
            int headerRead = stream.Read(header, 0, header.Length);
            if (headerRead >= 10 && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                id3Size = 10 + SyncSafeToInt(header, 6);
                if (id3Size < 10 || id3Size > stream.Length)
                {
                    return;
                }

                byte[] tagBytes = new byte[id3Size - 10];
                if (stream.Read(tagBytes, 0, tagBytes.Length) < tagBytes.Length)
                {
                    return;
                }

                if (!ReadId3v2Tags(tagBytes, header[3], result) && stream.Length >= 128)
                {
                    TryReadId3v1(stream, result);
                }
            }

            // Scan for the first MPEG audio frame sync inside the first 64 KB.
            long scanStart = stream.Position;
            long scanEnd = Math.Min(stream.Length, scanStart + 64 * 1024);
            byte[] sync = new byte[4];
            long pos = scanStart;
            while (pos + 4 <= scanEnd)
            {
                stream.Position = pos;
                if (stream.Read(sync, 0, 4) < 4)
                {
                    break;
                }

                if (sync[0] == 0xFF && (sync[1] & 0xE0) == 0xE0)
                {
                    TryParseMpegFrameHeader(sync, stream, pos, result);
                    break;
                }

                pos++;
            }

            if (!result.Duration.HasValue &&
                result.Bitrate.HasValue &&
                result.Bitrate.Value > 0 &&
                result.FileSizeBytes > 0)
            {
                long audioBytes = Math.Max(0, result.FileSizeBytes - id3Size - 128); // ignore trailing ID3v1
                result.Duration = TimeSpan.FromSeconds(audioBytes * 8d / result.Bitrate.Value);
            }
        }

        private static bool ReadId3v2Tags(byte[] data, byte majorVersion, MediaMetadataResult result)
        {
            bool foundAny = false;
            int pos = 0;
            while (pos + 6 <= data.Length)
            {
                if (data[pos] == 0)
                {
                    break; // padding
                }

                string frameId;
                int frameSize;
                if (majorVersion == 2)
                {
                    if (pos + 6 > data.Length)
                    {
                        break;
                    }

                    frameId = Encoding.ASCII.GetString(data, pos, 3);
                    frameSize = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
                    pos += 6;
                }
                else
                {
                    if (pos + 10 > data.Length)
                    {
                        break;
                    }

                    frameId = Encoding.ASCII.GetString(data, pos, 4);
                    frameSize = majorVersion == 4 ? SyncSafeToInt(data, pos + 4) : ReadBigEndianInt32(data, pos + 4);
                    pos += 10;
                }

                if (frameSize <= 0 || pos + frameSize > data.Length)
                {
                    break;
                }

                if (frameId == "COMM" || frameId == "COM")
                {
                    string? value = DecodeId3Comment(data, pos, frameSize);
                    if (value != null)
                    {
                        AddTagIfPresent(result, "Comment", value);
                        foundAny = true;
                    }
                }
                else if (frameId == "USLT" || frameId == "ULT")
                {
                    string? value = DecodeId3Comment(data, pos, frameSize);
                    if (value != null)
                    {
                        AddTagIfPresent(result, "Lyrics", value);
                        foundAny = true;
                    }
                }
                else if (frameId.Length > 0 && frameId[0] == 'T')
                {
                    string? canonical = MapId3Frame(frameId);
                    if (canonical != null)
                    {
                        string value = DecodeId3Text(data, pos, frameSize);
                        if (value.Length > 0)
                        {
                            AddTagIfPresent(result, canonical, canonical == "Genre" ? NormalizeId3Genre(value) : value);
                            foundAny = true;
                        }
                    }
                }

                pos += frameSize;
            }

            return foundAny;
        }

        private static string? MapId3Frame(string frameId) => frameId switch
        {
            "TIT2" => "Title",
            "TPE1" => "Artist",
            "TPE2" => "AlbumArtist",
            "TALB" => "Album",
            "TYER" or "TDRC" or "TDRL" => "Year",
            "TCON" => "Genre",
            "TRCK" => "Track",
            "TPOS" => "Disc",
            "TCOM" => "Composer",
            "TPUB" => "Publisher",
            "TBPM" => "BPM",
            "TKEY" => "Key",
            "TIT3" => "Subtitle",
            "TPE3" => "Conductor",
            "TEXT" => "Writers",
            _ => null
        };

        private static void TryReadId3v1(Stream stream, MediaMetadataResult result)
        {
            stream.Position = stream.Length - 128;
            byte[] tag = new byte[128];
            if (stream.Read(tag, 0, 128) < 128 || tag[0] != 'T' || tag[1] != 'A' || tag[2] != 'G')
            {
                return;
            }

            AddTagIfPresent(result, "Title", DecodeId3v1(tag, 3, 30));
            AddTagIfPresent(result, "Artist", DecodeId3v1(tag, 33, 30));
            AddTagIfPresent(result, "Album", DecodeId3v1(tag, 63, 30));
            AddTagIfPresent(result, "Year", DecodeId3v1(tag, 93, 4));
            if (tag[125] == 0 && tag[126] != 0)
            {
                AddTagIfPresent(result, "Track", tag[126].ToString(CultureInfo.InvariantCulture));
                AddTagIfPresent(result, "Comment", DecodeId3v1(tag, 97, 28));
            }
            else
            {
                AddTagIfPresent(result, "Comment", DecodeId3v1(tag, 97, 30));
            }

            byte genreIndex = tag[127];
            if (genreIndex < Id3GenreNames.Length && Id3GenreNames[genreIndex].Length > 0)
            {
                AddTagIfPresent(result, "Genre", Id3GenreNames[genreIndex]);
            }
        }

        private static string DecodeId3v1(byte[] tag, int offset, int length)
        {
            return TrimTag(Encoding.Latin1.GetString(tag, offset, length));
        }

        private static string DecodeId3Text(byte[] data, int start, int length)
        {
            if (length <= 1)
            {
                return string.Empty;
            }

            return DecodeId3RawText(data, start + 1, length - 1, data[start]);
        }

        private static string? DecodeId3Comment(byte[] data, int start, int length)
        {
            if (length < 4)
            {
                return null;
            }

            byte encoding = data[start];
            int end = start + length;
            int pos = start + 4; // skip encoding byte + 3 language bytes

            if (encoding == 1 || encoding == 2)
            {
                while (pos + 1 < end)
                {
                    if (data[pos] == 0 && data[pos + 1] == 0)
                    {
                        pos += 2;
                        break;
                    }

                    pos++;
                }
            }
            else
            {
                while (pos < end && data[pos] != 0)
                {
                    pos++;
                }

                if (pos < end)
                {
                    pos++;
                }
            }

            if (pos >= end)
            {
                return null;
            }

            string text = DecodeId3RawText(data, pos, end - pos, encoding);
            return text.Length > 0 ? text : null;
        }

        private static string DecodeId3RawText(byte[] data, int start, int length, byte encoding)
        {
            if (length <= 0)
            {
                return string.Empty;
            }

            switch (encoding)
            {
                case 1:
                    if (length >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
                    {
                        return TrimTag(Encoding.Unicode.GetString(data, start + 2, length - 2));
                    }

                    if (length >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
                    {
                        return TrimTag(Encoding.BigEndianUnicode.GetString(data, start + 2, length - 2));
                    }

                    return TrimTag(Encoding.Unicode.GetString(data, start, length));
                case 2:
                    return TrimTag(Encoding.BigEndianUnicode.GetString(data, start, length));
                case 3:
                    return TrimTag(Encoding.UTF8.GetString(data, start, length));
                default:
                    return TrimTag(Encoding.Latin1.GetString(data, start, length));
            }
        }

        private static string NormalizeId3Genre(string genre)
        {
            string g = genre.Trim();
            if (g.Length > 3 && g[0] == '(' && char.IsDigit(g[1]))
            {
                int close = g.IndexOf(')');
                if (close > 1 && int.TryParse(g.Substring(1, close - 1), out int idx) &&
                    idx >= 0 && idx < Id3GenreNames.Length)
                {
                    string rest = g.Substring(close + 1).Trim();
                    return rest.Length > 0 ? rest : Id3GenreNames[idx];
                }
            }

            if (g.Length > 0 && int.TryParse(g, out int idx2) && idx2 >= 0 && idx2 < Id3GenreNames.Length)
            {
                return Id3GenreNames[idx2];
            }

            return g;
        }

        private static readonly string[] Id3GenreNames =
        {
            "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
            "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae",
            "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks",
            "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion",
            "Trance", "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip",
            "Gospel", "Noise", "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative",
            "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic", "Darkwave",
            "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream", "Southern Rock",
            "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
            "Native American", "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes",
            "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical",
            "Rock & Roll", "Hard Rock"
        };

        private static void TryParseMpegFrameHeader(byte[] h, Stream stream, long frameStart, MediaMetadataResult result)
        {
            if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0)
            {
                return;
            }

            int versionBits = (h[1] >> 3) & 0x03; // 0 = MPEG 2.5, 2 = MPEG 2, 3 = MPEG 1
            int layerBits = (h[1] >> 1) & 0x03;   // 1 = Layer I, 2 = Layer II, 3 = Layer III
            int bitrateIndex = (h[2] >> 4) & 0x0F;
            int sampleRateIndex = (h[2] >> 2) & 0x03;
            int channelMode = (h[3] >> 6) & 0x03;
            if (versionBits == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3)
            {
                return;
            }

            int[] sampleRatesV1 = { 44100, 48000, 32000 };
            int[] sampleRatesV2 = { 22050, 24000, 16000 };
            int[] sampleRatesV25 = { 11025, 12000, 8000 };
            int sampleRate = versionBits switch
            {
                3 => sampleRatesV1[sampleRateIndex],
                2 => sampleRatesV2[sampleRateIndex],
                _ => sampleRatesV25[sampleRateIndex]
            };

            int bitrateKbps;
            if (versionBits == 3)
            {
                bitrateKbps = layerBits switch
                {
                    1 => new[] { 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }[bitrateIndex - 1],
                    2 => new[] { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 }[bitrateIndex - 1],
                    _ => new[] { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[bitrateIndex - 1]
                };
            }
            else
            {
                bitrateKbps = new[] { 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 }[bitrateIndex - 1];
            }

            result.SampleRate ??= (uint)sampleRate;
            result.Channels ??= (uint)(channelMode == 3 ? 1 : 2);
            result.Bitrate ??= (uint)(bitrateKbps * 1000);
            result.AudioCodec ??= layerBits switch
            {
                1 => "MPEG Audio Layer I",
                2 => "MPEG Audio Layer II",
                _ => "MP3"
            };

            try
            {
                // Xing/Info header (VBR stream info) right after the side info.
                bool mpeg1 = versionBits == 3;
                int sideInfoSize = mpeg1 ? (channelMode == 3 ? 17 : 32) : (channelMode == 3 ? 9 : 17);
                long xingPos = frameStart + 4 + sideInfoSize;
                if (xingPos + 8 <= stream.Length)
                {
                    stream.Position = xingPos;
                    byte[] xing = new byte[8];
                    if (stream.Read(xing, 0, 8) == 8 &&
                        ((xing[0] == 'X' && xing[1] == 'i' && xing[2] == 'n' && xing[3] == 'g') ||
                         (xing[0] == 'I' && xing[1] == 'n' && xing[2] == 'f' && xing[3] == 'o')))
                    {
                        uint flags = BitConverter.ToUInt32(xing, 4);
                        if ((flags & 0x01) != 0 && xingPos + 12 <= stream.Length)
                        {
                            stream.Position = xingPos + 8;
                            byte[] frameBytes = new byte[4];
                            if (stream.Read(frameBytes, 0, 4) == 4)
                            {
                                uint frames = BitConverter.ToUInt32(frameBytes, 0);
                                int samplesPerFrame = layerBits switch
                                {
                                    1 => 384,
                                    2 => 1152,
                                    _ => mpeg1 ? 1152 : 576
                                };
                                if (frames > 0 && sampleRate > 0)
                                {
                                    result.Duration ??= TimeSpan.FromSeconds((double)frames * samplesPerFrame / sampleRate);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        // ── WAV ──────────────────────────────────────────────────────────────

        private static void TryReadWav(Stream stream, MediaMetadataResult result)
        {
            if (stream.Length < 12)
            {
                return;
            }

            stream.Position = 12;
            byte[] chunkHeader = new byte[8];
            uint? dataSize = null;

            while (stream.Read(chunkHeader, 0, 8) == 8)
            {
                string id = Encoding.ASCII.GetString(chunkHeader, 0, 4);
                uint size = BitConverter.ToUInt32(chunkHeader, 4);

                if (id == "fmt ")
                {
                    byte[] fmt = new byte[Math.Min(size, 64)];
                    int read = stream.Read(fmt, 0, fmt.Length);
                    if (read >= 16)
                    {
                        ushort formatTag = BitConverter.ToUInt16(fmt, 0);
                        result.Channels ??= BitConverter.ToUInt16(fmt, 2);
                        result.SampleRate ??= BitConverter.ToUInt32(fmt, 4);
                        uint byteRate = BitConverter.ToUInt32(fmt, 8);
                        result.BitsPerSample ??= BitConverter.ToUInt16(fmt, 14);
                        string? codecName = WaveFormatNames.TryGetValue(formatTag, out string? name)
                            ? name
                            : $"0x{formatTag:X4}";
                        if (formatTag == 0xFFFE && read >= 40)
                        {
                            ushort sub = BitConverter.ToUInt16(fmt, 24);
                            if (WaveFormatNames.TryGetValue(sub, out string? subName))
                            {
                                codecName = subName;
                            }
                        }

                        result.AudioCodec ??= codecName;
                        if (!result.Bitrate.HasValue && byteRate > 0)
                        {
                            result.Bitrate = byteRate * 8;
                        }
                    }
                    else
                    {
                        stream.Position += size - read;
                    }
                }
                else if (id == "data")
                {
                    dataSize = size;
                    if (dataSize > stream.Length - stream.Position)
                    {
                        dataSize = (uint)Math.Max(0, stream.Length - stream.Position);
                    }

                    break;
                }
                else
                {
                    stream.Position += size + (size % 2);
                }
            }

            if (!result.Duration.HasValue &&
                dataSize.HasValue &&
                result.Bitrate.HasValue &&
                result.Bitrate.Value > 0)
            {
                result.Duration = TimeSpan.FromSeconds((double)dataSize.Value * 8 / result.Bitrate.Value);
            }
        }

        // ── FLAC ─────────────────────────────────────────────────────────────

        private static void TryReadFlac(Stream stream, MediaMetadataResult result)
        {
            stream.Position = 4;
            byte[] blockHeader = new byte[4];

            while (stream.Read(blockHeader, 0, 4) == 4)
            {
                bool last = (blockHeader[0] & 0x80) != 0;
                int type = blockHeader[0] & 0x7F;
                int size = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
                if (size < 0 || stream.Position + size > stream.Length)
                {
                    break;
                }

                if (type == 0 && size >= 34)
                {
                    byte[] si = new byte[34];
                    if (stream.Read(si, 0, 34) < 34)
                    {
                        return;
                    }

                    ulong sampleRate = ((ulong)si[10] << 12) | ((ulong)si[11] << 4) | ((ulong)si[12] >> 4);
                    uint channels = (uint)(((si[12] >> 1) & 0x07) + 1);
                    uint bitsPerSample = (uint)((((si[12] & 0x01) << 4) | (si[13] >> 4)) + 1);
                    ulong totalSamples =
                        ((ulong)(si[13] & 0x0F) << 32) |
                        ((ulong)si[14] << 24) |
                        ((ulong)si[15] << 16) |
                        ((ulong)si[16] << 8) |
                        si[17];

                    result.SampleRate ??= (uint)sampleRate;
                    result.Channels ??= channels;
                    result.BitsPerSample ??= bitsPerSample;
                    result.AudioCodec ??= "FLAC";
                    if (totalSamples > 0 && sampleRate > 0)
                    {
                        result.Duration ??= TimeSpan.FromSeconds((double)totalSamples / sampleRate);
                    }
                }
                else if (type == 4)
                {
                    byte[] vc = new byte[size];
                    if (stream.Read(vc, 0, size) < size)
                    {
                        return;
                    }

                    ReadVorbisComments(vc, result);
                }
                else
                {
                    stream.Position += size;
                }

                if (last)
                {
                    break;
                }
            }
        }

        private static void ReadVorbisComments(byte[] data, MediaMetadataResult result)
        {
            int pos = 0;
            if (pos + 4 > data.Length)
            {
                return;
            }

            uint vendorLen = BitConverter.ToUInt32(data, pos);
            pos += 4 + (int)vendorLen;
            if (pos + 4 > data.Length)
            {
                return;
            }

            uint count = BitConverter.ToUInt32(data, pos);
            pos += 4;
            for (uint i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                uint len = BitConverter.ToUInt32(data, pos);
                pos += 4;
                if (pos + len > data.Length)
                {
                    break;
                }

                string entry = Encoding.UTF8.GetString(data, pos, (int)len);
                pos += (int)len;
                int eq = entry.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = entry[..eq].Trim().ToUpperInvariant();
                string value = TrimTag(entry[(eq + 1)..]);
                if (value.Length == 0)
                {
                    continue;
                }

                string? canonical = key switch
                {
                    "TITLE" => "Title",
                    "ARTIST" => "Artist",
                    "ALBUM" or "ALBUMTITLE" => "Album",
                    "ALBUMARTIST" or "ALBUM ARTIST" => "AlbumArtist",
                    "DATE" or "YEAR" => "Year",
                    "GENRE" => "Genre",
                    "TRACKNUMBER" or "TRACK" => "Track",
                    "DISCNUMBER" or "DISC" => "Disc",
                    "COMMENT" or "DESCRIPTION" => "Comment",
                    "COMPOSER" => "Composer",
                    "ORGANIZATION" or "LABEL" => "Publisher",
                    "BPM" => "BPM",
                    "LYRICIST" => "Writers",
                    "KEY" or "INITIALKEY" => "Key",
                    _ => null
                };

                if (canonical != null)
                {
                    if (canonical == "Year" && value.Length > 4 && value.Length <= 10)
                    {
                        value = value[..4];
                    }

                    AddTagIfPresent(result, canonical, value);
                }
            }
        }

        // ── MP4 / M4A / MOV ──────────────────────────────────────────────────

        private static void TryReadMp4(Stream stream, MediaMetadataResult result)
        {
            stream.Position = 0;
            byte[] boxHeader = new byte[8];

            while (stream.Position + 8 <= stream.Length)
            {
                long boxStart = stream.Position;
                if (stream.Read(boxHeader, 0, 8) < 8)
                {
                    break;
                }

                uint size32 = BitConverter.ToUInt32(boxHeader, 0);
                string type = Encoding.ASCII.GetString(boxHeader, 4, 4);
                ulong boxSize;
                int headerSize;
                if (size32 == 1)
                {
                    byte[] size64 = new byte[8];
                    if (stream.Read(size64, 0, 8) < 8)
                    {
                        break;
                    }

                    boxSize = BitConverter.ToUInt64(size64, 0);
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    boxSize = (ulong)(stream.Length - boxStart);
                    headerSize = 8;
                }
                else
                {
                    boxSize = size32;
                    headerSize = 8;
                }

                if (boxSize < (ulong)headerSize || boxStart + (long)boxSize > stream.Length)
                {
                    break;
                }

                if (type == "ftyp" && boxSize >= 12)
                {
                    byte[] brandBytes = new byte[4];
                    if (stream.Read(brandBytes, 0, 4) == 4)
                    {
                        string brand = Encoding.ASCII.GetString(brandBytes, 0, 4);
                        result.Container ??= brand switch
                        {
                            "M4A " => "M4A",
                            "M4B " => "M4B",
                            "M4V " => "M4V",
                            "qt  " => "MOV",
                            _ => "MP4"
                        };
                    }
                }
                else if (type == "moov")
                {
                    ParseMoovBox(stream, boxSize - (ulong)headerSize, result);
                    break;
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseMoovBox(Stream stream, ulong size, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                switch (type)
                {
                    case "mvhd":
                        ParseMvhd(stream, (int)Math.Min(boxSize - (ulong)headerSize, 4096), result);
                        break;
                    case "trak":
                        ParseTrak(stream, boxSize - (ulong)headerSize, result);
                        break;
                    case "udta":
                        ParseUdta(stream, boxSize - (ulong)headerSize, result);
                        break;
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseTrak(Stream stream, ulong size, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "mdia")
                {
                    ParseMdia(stream, boxSize - (ulong)headerSize, result);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseMdia(Stream stream, ulong size, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            string? handlerType = null;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "hdlr")
                {
                    handlerType = ParseHdlr(stream, (int)Math.Min(boxSize - (ulong)headerSize, 64), result);
                }
                else if (type == "minf")
                {
                    ParseMinf(stream, boxSize - (ulong)headerSize, result, handlerType);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseMinf(Stream stream, ulong size, MediaMetadataResult result, string? handlerType)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "stbl")
                {
                    ParseStbl(stream, boxSize - (ulong)headerSize, result, handlerType);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseStbl(Stream stream, ulong size, MediaMetadataResult result, string? handlerType)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "stsd")
                {
                    ParseStsd(stream, (int)Math.Min(boxSize - (ulong)headerSize, 8192), result, handlerType);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseStsd(Stream stream, int size, MediaMetadataResult result, string? handlerType)
        {
            if (size < 16)
            {
                return;
            }

            byte[] data = new byte[size];
            int read = stream.Read(data, 0, size);
            if (read < 16)
            {
                return;
            }

            bool isVideo = string.Equals(handlerType, "vide", StringComparison.Ordinal);
            int entryCount = BitConverter.ToInt32(data, 4);
            if (entryCount <= 0)
            {
                return;
            }

            int entrySize = BitConverter.ToInt32(data, 8);
            if (entrySize < 8 || 8 + entrySize > read)
            {
                return;
            }

            string fourcc = Encoding.ASCII.GetString(data, 12, 4);
            if (isVideo)
            {
                result.HasVideoTrack = true;
                result.VideoCodec ??= ResolveVideoCodec(fourcc);
                // VisualSampleEntry: width(16) height(16) at entry offsets 24..28.
                if (entrySize >= 30)
                {
                    uint frameWidth = BitConverter.ToUInt16(data, 8 + 24);
                    uint frameHeight = BitConverter.ToUInt16(data, 8 + 26);
                    if (frameWidth > 0 && frameHeight > 0)
                    {
                        result.Width ??= frameWidth;
                        result.Height ??= frameHeight;
                    }
                }
            }
            else
            {
                result.HasAudioTrack = true;
                result.AudioCodec ??= ResolveAudioCodec(fourcc);
                // AudioSampleEntry: channelcount(16) samplesize(16) pre_defined(16)
                // reserved(16) samplerate(16.16) at entry offsets 16..28.
                if (entrySize >= 36)
                {
                    result.Channels ??= BitConverter.ToUInt16(data, 8 + 16);
                    result.BitsPerSample ??= BitConverter.ToUInt16(data, 8 + 18);
                    uint sampleRateFixed = BitConverter.ToUInt32(data, 8 + 24);
                    uint sampleRate = sampleRateFixed >> 16;
                    if (sampleRate > 0)
                    {
                        result.SampleRate ??= sampleRate;
                    }
                }
            }
        }

        private static string? ParseHdlr(Stream stream, int size, MediaMetadataResult result)
        {
            if (size < 12)
            {
                return null;
            }

            byte[] data = new byte[size];
            if (stream.Read(data, 0, size) < 12)
            {
                return null;
            }

            string handler = Encoding.ASCII.GetString(data, 8, 4); // fullbox(4) + pre_defined(4)
            if (string.Equals(handler, "vide", StringComparison.Ordinal))
            {
                result.HasVideoTrack = true;
                return "vide";
            }
            else if (string.Equals(handler, "soun", StringComparison.Ordinal))
            {
                result.HasAudioTrack = true;
                return "soun";
            }

            return null;
        }

        private static void ParseMvhd(Stream stream, int size, MediaMetadataResult result)
        {
            if (size < 20)
            {
                return;
            }

            byte[] data = new byte[size];
            if (stream.Read(data, 0, size) < size)
            {
                return;
            }

            if (data[0] == 1)
            {
                if (data.Length < 32)
                {
                    return;
                }

                uint timescale = BitConverter.ToUInt32(data, 20);
                ulong duration = BitConverter.ToUInt64(data, 24);
                if (timescale > 0 && duration > 0)
                {
                    result.Duration ??= TimeSpan.FromSeconds((double)duration / timescale);
                }
            }
            else
            {
                if (data.Length < 20)
                {
                    return;
                }

                uint timescale = BitConverter.ToUInt32(data, 12);
                uint duration = BitConverter.ToUInt32(data, 16);
                if (timescale > 0 && duration > 0)
                {
                    result.Duration ??= TimeSpan.FromSeconds((double)duration / timescale);
                }
            }
        }

        private static void ParseUdta(Stream stream, ulong size, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "meta")
                {
                    ParseMeta(stream, boxSize - (ulong)headerSize, result);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseMeta(Stream stream, ulong size, MediaMetadataResult result)
        {
            if (size < 4)
            {
                return;
            }

            stream.Position += 4; // version + flags
            ulong remaining = size - 4;
            long end = stream.Position + (long)remaining;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "ilst")
                {
                    ParseIlst(stream, boxSize - (ulong)headerSize, result);
                }

                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseIlst(Stream stream, ulong size, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 8 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string key, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                ParseIlstItem(stream, boxSize - (ulong)headerSize, key, result);
                stream.Position = boxStart + (long)boxSize;
            }
        }

        private static void ParseIlstItem(Stream stream, ulong size, string key, MediaMetadataResult result)
        {
            long end = stream.Position + (long)size;
            while (stream.Position + 16 <= end)
            {
                long boxStart = stream.Position;
                if (!ReadBoxHeader(stream, end, out string type, out ulong boxSize, out int headerSize))
                {
                    break;
                }

                if (type == "data")
                {
                    int payloadSize = (int)Math.Min(boxSize - (ulong)headerSize, 64 * 1024);
                    byte[] payload = new byte[payloadSize];
                    int read = stream.Read(payload, 0, payloadSize);
                    if (read >= 8)
                    {
                        uint dataType = BitConverter.ToUInt32(payload, 0);
                        string? canonical = MapMp4TagKey(key);
                        string? value = null;
                        if (canonical != null)
                        {
                            value = key switch
                            {
                                "trkn" or "disk" when dataType == 0 && read >= 12 =>
                                    BitConverter.ToUInt16(payload, 10).ToString(CultureInfo.InvariantCulture),
                                "tmpo" when dataType == 21 && read >= 10 =>
                                    BitConverter.ToUInt16(payload, 8).ToString(CultureInfo.InvariantCulture),
                                "covr" => null, // album art – skip
                                _ when dataType == 1 => TrimTag(Encoding.UTF8.GetString(payload, 8, read - 8)),
                                _ when dataType == 2 => TrimTag(Encoding.BigEndianUnicode.GetString(payload, 8, read - 8)),
                                _ => null
                            };
                        }

                        if (!string.IsNullOrEmpty(value) && canonical != null)
                        {
                            AddTagIfPresent(result, canonical, value);
                        }
                    }

                    break;
                }

                stream.Position = boxStart + (long)boxSize;
            }

            stream.Position = end;
        }

        private static string? MapMp4TagKey(string key) => key switch
        {
            "\u00A9nam" => "Title",
            "\u00A9ART" => "Artist",
            "\u00A9alb" => "Album",
            "\u00A9day" => "Year",
            "\u00A9gen" => "Genre",
            "\u00A9cmt" => "Comment",
            "\u00A9wrt" => "Composer",
            "\u00A9lyr" => "Lyrics",
            "\u00A9grp" => "Grouping",
            "aART" => "AlbumArtist",
            "trkn" => "Track",
            "disk" => "Disc",
            "tmpo" => "BPM",
            _ => null
        };

        private static bool ReadBoxHeader(Stream stream, long end, out string type, out ulong boxSize, out int headerSize)
        {
            type = string.Empty;
            boxSize = 0;
            headerSize = 8;
            if (stream.Position + 8 > end)
            {
                return false;
            }

            byte[] header = new byte[8];
            if (stream.Read(header, 0, 8) < 8)
            {
                return false;
            }

            long boxStart = stream.Position - 8;
            uint size32 = BitConverter.ToUInt32(header, 0);
            type = Encoding.ASCII.GetString(header, 4, 4);
            if (size32 == 1)
            {
                byte[] size64 = new byte[8];
                if (stream.Read(size64, 0, 8) < 8)
                {
                    return false;
                }

                boxSize = BitConverter.ToUInt64(size64, 0);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = (ulong)(end - boxStart);
            }
            else
            {
                boxSize = size32;
            }

            return boxSize >= (ulong)headerSize && boxStart + (long)boxSize <= end;
        }

        // ── OGG / Opus ───────────────────────────────────────────────────────

        private static void TryReadOgg(Stream stream, MediaMetadataResult result)
        {
            byte[] firstPage = new byte[64 * 1024];
            int read = stream.Read(firstPage, 0, firstPage.Length);
            if (read >= 36)
            {
                int segCount = firstPage[26];
                int payloadStart = 27 + segCount;
                if (payloadStart + 8 <= read)
                {
                    string codecId = Encoding.ASCII.GetString(firstPage, payloadStart, 8);
                    if (codecId == "OpusHead" && payloadStart + 19 <= read)
                    {
                        result.AudioCodec ??= "Opus";
                        result.Channels ??= firstPage[payloadStart + 9];
                        result.SampleRate ??= 48000;
                    }
                    else if (codecId.StartsWith("vorbis", StringComparison.Ordinal) && payloadStart + 30 <= read)
                    {
                        result.AudioCodec ??= "Vorbis";
                        result.Channels ??= firstPage[payloadStart + 11];
                        uint sampleRate = BitConverter.ToUInt32(firstPage, payloadStart + 12);
                        if (sampleRate > 0)
                        {
                            result.SampleRate ??= sampleRate;
                        }
                    }
                }
            }

            // Duration from the granule position of the last page.
            long end = stream.Length;
            int tailLen = (int)Math.Min(end, 64 * 1024);
            byte[] tail = new byte[tailLen];
            stream.Position = end - tailLen;
            if (stream.Read(tail, 0, tailLen) >= 30)
            {
                for (int i = tailLen - 4; i >= 0; i--)
                {
                    if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
                    {
                        ulong granule = BitConverter.ToUInt64(tail, i + 6);
                        uint rate = result.SampleRate ?? 48000;
                        if (granule > 0 && rate > 0)
                        {
                            result.Duration ??= TimeSpan.FromSeconds((double)granule / rate);
                        }

                        break;
                    }
                }
            }
        }

        // ── MKV / WebM (EBML) ────────────────────────────────────────────────

        private const ulong EbmlUnknownSize = 0x01FFFFFFFFFFFFFF;

        private static void TryReadMkv(Stream stream, MediaMetadataResult result)
        {
            stream.Position = 0;
            if (!TryReadEbmlId(stream, out ulong rootId) || rootId != 0x1A45DFA3)
            {
                return;
            }

            if (!TryReadEbmlVint(stream, out ulong rootSize, out _))
            {
                return;
            }

            // The EBML header element is followed by the Segment element (a sibling).
            long headerEnd = rootSize >= EbmlUnknownSize
                ? stream.Length
                : Math.Min(stream.Length, stream.Position + (long)rootSize);
            if (headerEnd >= stream.Length)
            {
                return;
            }

            stream.Position = headerEnd;
            WalkMkvElements(stream, stream.Length, result);
        }

        private static void WalkMkvElements(Stream stream, long end, MediaMetadataResult result)
        {
            bool sawInfo = false;
            bool sawTracks = false;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = size >= (ulong)(end - contentStart) ? end : contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                if (id == 0x18538067) // Segment: recurse into its children
                {
                    WalkMkvElements(stream, contentEnd, result);
                }
                else if (id == 0x1549A966) // Info
                {
                    ParseMkvInfo(stream, contentStart, contentEnd, result);
                    sawInfo = true;
                }
                else if (id == 0x1654AE6B) // Tracks
                {
                    ParseMkvTracks(stream, contentStart, contentEnd, result);
                    sawTracks = true;
                }

                stream.Position = contentEnd;
                if (sawInfo && sawTracks)
                {
                    break;
                }
            }
        }

        private static void ParseMkvInfo(Stream stream, long start, long end, MediaMetadataResult result)
        {
            ulong timestampScale = 1_000_000;
            double? duration = null;
            stream.Position = start;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                if (id == 0x2AD7B1 && size <= 8) // TimestampScale (ns per tick, default 1,000,000)
                {
                    timestampScale = ReadMkvUInt(stream, (int)size);
                }
                else if (id == 0x4489 && (size == 4 || size == 8)) // Duration
                {
                    duration = ReadMkvFloat(stream, (int)size);
                }

                stream.Position = contentEnd;
            }

            if (duration is { } d && d > 0 && !result.Duration.HasValue)
            {
                result.Duration = TimeSpan.FromSeconds(d * timestampScale / 1_000_000_000d);
            }
        }

        private static void ParseMkvTracks(Stream stream, long start, long end, MediaMetadataResult result)
        {
            stream.Position = start;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                if (id == 0xAE) // TrackEntry
                {
                    ParseMkvTrackEntry(stream, contentStart, contentEnd, result);
                }

                stream.Position = contentEnd;
            }
        }

        private static void ParseMkvTrackEntry(Stream stream, long start, long end, MediaMetadataResult result)
        {
            ulong trackType = 0;
            string? codecId = null;
            byte[]? codecPrivate = null;
            ulong pixelWidth = 0;
            ulong pixelHeight = 0;
            double? sampleRate = null;
            ulong channels = 0;
            ulong bitDepth = 0;

            stream.Position = start;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                switch (id)
                {
                    case 0x83 when size <= 8: // TrackType (1 = video, 2 = audio)
                        trackType = ReadMkvUInt(stream, (int)size);
                        break;
                    case 0x86 when size > 0 && size <= 256: // CodecID
                        codecId = ReadMkvAscii(stream, (int)size);
                        break;
                    case 0x63A2 when size > 0 && size <= 1024: // CodecPrivate
                        codecPrivate = ReadMkvBytes(stream, (int)size);
                        break;
                    case 0xE0: // Video
                        ParseMkvVideo(stream, contentStart, contentEnd, out pixelWidth, out pixelHeight);
                        break;
                    case 0xE1: // Audio
                        ParseMkvAudio(stream, contentStart, contentEnd, out sampleRate, out channels);
                        break;
                    case 0x6264 when size <= 8: // BitDepth (audio)
                        bitDepth = ReadMkvUInt(stream, (int)size);
                        break;
                }

                stream.Position = contentEnd;
            }

            if (trackType == 1)
            {
                result.HasVideoTrack = true;
                result.VideoCodec ??= ResolveMkvVideoCodec(codecId, codecPrivate);
                if (pixelWidth > 0 && pixelHeight > 0)
                {
                    result.Width ??= (uint)pixelWidth;
                    result.Height ??= (uint)pixelHeight;
                }
            }
            else if (trackType == 2)
            {
                result.HasAudioTrack = true;
                result.AudioCodec ??= ResolveMkvAudioCodec(codecId, codecPrivate);
                if (sampleRate is { } sr && sr > 0)
                {
                    result.SampleRate ??= (uint)Math.Round(sr);
                }

                if (channels > 0)
                {
                    result.Channels ??= (uint)channels;
                }

                if (bitDepth > 0)
                {
                    result.BitsPerSample ??= (uint)bitDepth;
                }
            }
        }

        private static void ParseMkvVideo(Stream stream, long start, long end, out ulong pixelWidth, out ulong pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;
            stream.Position = start;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                if (id == 0xB0 && size <= 8) // PixelWidth
                {
                    pixelWidth = ReadMkvUInt(stream, (int)size);
                }
                else if (id == 0xBA && size <= 8) // PixelHeight
                {
                    pixelHeight = ReadMkvUInt(stream, (int)size);
                }

                stream.Position = contentEnd;
            }
        }

        private static void ParseMkvAudio(Stream stream, long start, long end, out double? sampleRate, out ulong channels)
        {
            sampleRate = null;
            channels = 0;
            stream.Position = start;
            while (stream.Position + 1 < end)
            {
                if (!TryReadEbmlId(stream, out ulong id) || !TryReadEbmlVint(stream, out ulong size, out _))
                {
                    break;
                }

                long contentStart = stream.Position;
                long contentEnd = contentStart + (long)size;
                if (contentEnd > end || contentEnd < contentStart)
                {
                    break;
                }

                if (id == 0xB5 && (size == 4 || size == 8)) // SamplingFrequency
                {
                    sampleRate = ReadMkvFloat(stream, (int)size);
                }
                else if (id == 0x9F && size <= 8) // Channels
                {
                    channels = ReadMkvUInt(stream, (int)size);
                }

                stream.Position = contentEnd;
            }
        }

        private static string? ResolveMkvVideoCodec(string? codecId, byte[]? codecPrivate)
        {
            if (string.IsNullOrEmpty(codecId))
            {
                return null;
            }

            return codecId switch
            {
                "V_MPEG4/ISO/AVC" => "H.264 / AVC",
                "V_MPEGH/ISO/HEVC" => "H.265 / HEVC",
                "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/AP" => "MPEG-4 Part 2",
                "V_VP8" => "VP8",
                "V_VP9" => "VP9",
                "V_AV1" => "AV1",
                "V_THEORA" => "Theora",
                "V_MPEG1" => "MPEG-1 Video",
                "V_MPEG2" => "MPEG-2 Video",
                "V_MS/VFW/FOURCC" when codecPrivate is { Length: >= 20 } =>
                    ResolveVideoCodec(Encoding.ASCII.GetString(codecPrivate, 16, 4)),
                "V_QUICKTIME" when codecPrivate is { Length: >= 4 } =>
                    ResolveVideoCodec(Encoding.ASCII.GetString(codecPrivate, 0, 4)),
                _ => codecId
            };
        }

        private static string? ResolveMkvAudioCodec(string? codecId, byte[]? codecPrivate)
        {
            if (string.IsNullOrEmpty(codecId))
            {
                return null;
            }

            return codecId switch
            {
                "A_AAC" => "AAC",
                "A_AC3" => "AC-3",
                "A_EAC3" => "E-AC-3",
                "A_DTS" => "DTS",
                "A_FLAC" => "FLAC",
                "A_MPEG/L1" => "MPEG Audio Layer I",
                "A_MPEG/L2" => "MPEG Audio Layer II",
                "A_MPEG/L3" => "MP3",
                "A_OPUS" => "Opus",
                "A_VORBIS" => "Vorbis",
                "A_PCM/INT/LIT" or "A_PCM/INT/BIG" => "PCM",
                "A_PCM/FLOAT/IEEE" => "IEEE Float",
                "A_TRUEHD" => "TrueHD",
                "A_MLP" => "MLP",
                "A_WAVPACK4" => "WavPack",
                "A_MS/ACM" when codecPrivate is { Length: >= 2 } => ResolveAcmCodec(codecPrivate),
                _ => codecId
            };
        }

        private static string? ResolveAcmCodec(byte[] codecPrivate)
        {
            ushort formatTag = (ushort)(codecPrivate[0] | (codecPrivate[1] << 8));
            return WaveFormatNames.TryGetValue(formatTag, out string? name) ? name : $"0x{formatTag:X4}";
        }

        private static bool TryReadEbmlId(Stream stream, out ulong id)
        {
            id = 0;
            int b = stream.ReadByte();
            if (b <= 0)
            {
                return false;
            }

            int length = 1;
            int mask = 0x80;
            while ((b & mask) == 0 && length < 4)
            {
                mask >>= 1;
                length++;
            }

            if ((b & mask) == 0)
            {
                return false;
            }

            id = (ulong)b;
            for (int i = 1; i < length; i++)
            {
                int next = stream.ReadByte();
                if (next < 0)
                {
                    return false;
                }

                id = (id << 8) | (ulong)(byte)next;
            }

            return true;
        }

        private static bool TryReadEbmlVint(Stream stream, out ulong value, out int length)
        {
            value = 0;
            length = 0;
            int b = stream.ReadByte();
            if (b <= 0)
            {
                return false;
            }

            length = 1;
            int mask = 0x80;
            while ((b & mask) == 0 && length < 8)
            {
                mask >>= 1;
                length++;
            }

            if ((b & mask) == 0)
            {
                return false;
            }

            value = (ulong)(b & (mask - 1));
            for (int i = 1; i < length; i++)
            {
                int next = stream.ReadByte();
                if (next < 0)
                {
                    return false;
                }

                value = (value << 8) | (ulong)(byte)next;
            }

            return true;
        }

        private static ulong ReadMkvUInt(Stream stream, int length)
        {
            ulong value = 0;
            for (int i = 0; i < length; i++)
            {
                int b = stream.ReadByte();
                value = b < 0 ? value : (value << 8) | (ulong)(byte)b;
            }

            return value;
        }

        private static double ReadMkvFloat(Stream stream, int length)
        {
            byte[] bytes = new byte[length];
            int read = stream.Read(bytes, 0, length);
            if (length == 4 && read == 4)
            {
                uint bits = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                return BitConverter.UInt32BitsToSingle(bits);
            }

            if (length == 8 && read == 8)
            {
                ulong bits = 0;
                for (int i = 0; i < 8; i++)
                {
                    bits = (bits << 8) | bytes[i];
                }

                return BitConverter.UInt64BitsToDouble(bits);
            }

            return 0;
        }

        private static string? ReadMkvAscii(Stream stream, int length)
        {
            byte[] bytes = new byte[length];
            if (stream.Read(bytes, 0, length) < length)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes).Trim();
        }

        private static byte[]? ReadMkvBytes(Stream stream, int length)
        {
            byte[] bytes = new byte[length];
            if (stream.Read(bytes, 0, length) < length)
            {
                return null;
            }

            return bytes;
        }

        // ── AVI ──────────────────────────────────────────────────────────────

        private static void TryReadAvi(Stream stream, MediaMetadataResult result)
        {
            stream.Position = 12; // "RIFF" size "AVI "
            uint? microSecPerFrame = null;
            uint totalFrames = 0;
            uint width = 0;
            uint height = 0;
            string? videoHandler = null;
            bool haveVideoStream = false;
            bool haveAudioStream = false;
            bool haveVideoFormat = false;
            bool haveAudioFormat = false;
            uint audioByteRate = 0;

            void WalkChunks(long end)
            {
                while (stream.Position + 8 <= end)
                {
                    long chunkStart = stream.Position;
                    byte[] header = new byte[8];
                    if (stream.Read(header, 0, 8) < 8)
                    {
                        break;
                    }

                    string fourcc = Encoding.ASCII.GetString(header, 0, 4);
                    uint size = BitConverter.ToUInt32(header, 4);
                    if (chunkStart + 8 + size > end)
                    {
                        break;
                    }

                    if (fourcc == "LIST")
                    {
                        byte[] listTypeBytes = new byte[4];
                        if (stream.Read(listTypeBytes, 0, 4) < 4)
                        {
                            break;
                        }

                        string listType = Encoding.ASCII.GetString(listTypeBytes, 0, 4);
                        if (listType == "hdrl" || listType == "strl")
                        {
                            WalkChunks(stream.Position + size - 4);
                        }
                    }
                    else if (fourcc == "avih" && size >= 40)
                    {
                        byte[] avih = new byte[40];
                        if (stream.Read(avih, 0, 40) == 40)
                        {
                            microSecPerFrame = BitConverter.ToUInt32(avih, 0);
                            totalFrames = BitConverter.ToUInt32(avih, 16);
                            width = BitConverter.ToUInt32(avih, 32);
                            height = BitConverter.ToUInt32(avih, 36);
                        }
                    }
                    else if (fourcc == "strh" && size >= 56)
                    {
                        byte[] strh = new byte[56];
                        if (stream.Read(strh, 0, 56) == 56)
                        {
                            string fccType = Encoding.ASCII.GetString(strh, 0, 4);
                            if (fccType == "vids")
                            {
                                result.HasVideoTrack = true;
                                haveVideoStream = true;
                                videoHandler = Encoding.ASCII.GetString(strh, 4, 4);
                            }
                            else if (fccType == "auds")
                            {
                                result.HasAudioTrack = true;
                                haveAudioStream = true;
                            }
                        }
                    }
                    else if (fourcc == "strf")
                    {
                        byte[] fmt = new byte[Math.Min(size, 64)];
                        int read = stream.Read(fmt, 0, fmt.Length);
                        if (read >= 40 && haveVideoStream && !haveVideoFormat)
                        {
                            uint biWidth = BitConverter.ToUInt32(fmt, 4);
                            int biHeight = BitConverter.ToInt32(fmt, 8);
                            string biCompression = Encoding.ASCII.GetString(fmt, 16, 4);
                            if (biWidth > 0 && biHeight != 0)
                            {
                                width = biWidth;
                                height = (uint)Math.Abs(biHeight);
                            }

                            videoHandler = string.IsNullOrWhiteSpace(biCompression) ? videoHandler : biCompression;
                            haveVideoFormat = true;
                        }
                        else if (read >= 16 && haveAudioStream && !haveAudioFormat)
                        {
                            ushort formatTag = BitConverter.ToUInt16(fmt, 0);
                            uint channels = BitConverter.ToUInt16(fmt, 2);
                            uint sampleRate = BitConverter.ToUInt32(fmt, 4);
                            audioByteRate = BitConverter.ToUInt32(fmt, 8);
                            uint bits = BitConverter.ToUInt16(fmt, 14);
                            result.AudioCodec ??= WaveFormatNames.TryGetValue(formatTag, out string? name)
                                ? name
                                : $"0x{formatTag:X4}";
                            result.Channels ??= channels;
                            result.SampleRate ??= sampleRate;
                            result.BitsPerSample ??= bits;
                            haveAudioFormat = true;
                        }
                    }

                    stream.Position = chunkStart + 8 + size + (size % 2);
                }
            }

            WalkChunks(stream.Length);

            if (width > 0 && height > 0)
            {
                result.Width ??= width;
                result.Height ??= height;
            }

            if (videoHandler != null)
            {
                result.VideoCodec ??= ResolveVideoCodec(videoHandler);
            }

            if (audioByteRate > 0 && !result.Bitrate.HasValue)
            {
                result.Bitrate = audioByteRate * 8;
            }

            if (microSecPerFrame is { } uspf && uspf > 0)
            {
                if (!result.FrameRate.HasValue)
                {
                    result.FrameRate = 1_000_000d / uspf;
                }

                if (!result.Duration.HasValue && totalFrames > 0)
                {
                    result.Duration = TimeSpan.FromSeconds((double)totalFrames * uspf / 1_000_000d);
                }
            }
        }

        // ── WMV / ASF ────────────────────────────────────────────────────────

        private static readonly byte[] AsfHeaderGuid =
            { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

        private static readonly byte[] AsfFilePropertiesGuid =
            { 0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65 };

        private static readonly byte[] AsfStreamPropertiesGuid =
            { 0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65 };

        private static readonly byte[] AsfVideoMediaGuid =
            { 0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B };

        private static readonly byte[] AsfAudioMediaGuid =
            { 0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B };

        private static void TryReadAsf(Stream stream, MediaMetadataResult result)
        {
            if (stream.Length < 52)
            {
                return;
            }

            stream.Position = 0;
            byte[] headerGuid = new byte[16];
            if (stream.Read(headerGuid, 0, 16) < 16 || !BytesEqual(headerGuid, AsfHeaderGuid))
            {
                return;
            }

            byte[] sizeBytes = new byte[8];
            if (stream.Read(sizeBytes, 0, 8) < 8)
            {
                return;
            }

            ulong headerSize = BitConverter.ToUInt64(sizeBytes, 0);
            long headerEnd = Math.Min(stream.Length, 24 + (long)headerSize);
            stream.Position = 52; // header object: fileId(16) + headerSize(8) + objectCount(4)

            while (stream.Position + 24 <= headerEnd)
            {
                byte[] objGuid = new byte[16];
                if (stream.Read(objGuid, 0, 16) < 16)
                {
                    break;
                }

                byte[] objSizeBytes = new byte[8];
                if (stream.Read(objSizeBytes, 0, 8) < 8)
                {
                    break;
                }

                ulong objSize = BitConverter.ToUInt64(objSizeBytes, 0);
                long objStart = stream.Position - 24;
                if (objSize < 24 || objStart + (long)objSize > headerEnd)
                {
                    break;
                }

                if (BytesEqual(objGuid, AsfFilePropertiesGuid) && objSize >= 24 + 80)
                {
                    stream.Position = objStart + 24 + 40;
                    byte[] play = new byte[8];
                    if (stream.Read(play, 0, 8) == 8 && !result.Duration.HasValue)
                    {
                        ulong playDuration = BitConverter.ToUInt64(play, 0);
                        byte[] preroll = new byte[8];
                        stream.Position = objStart + 24 + 56;
                        ulong prerollMs = stream.Read(preroll, 0, 8) == 8 ? BitConverter.ToUInt64(preroll, 0) : 0;
                        if (playDuration > prerollMs * 10_000)
                        {
                            result.Duration = TimeSpan.FromSeconds((playDuration - prerollMs * 10_000) / 10_000_000d);
                        }
                        else if (playDuration > 0)
                        {
                            result.Duration = TimeSpan.FromSeconds(playDuration / 10_000_000d);
                        }
                    }
                }
                else if (BytesEqual(objGuid, AsfStreamPropertiesGuid))
                {
                    ParseAsfStreamProperties(stream, objStart, objSize, result);
                }

                stream.Position = objStart + (long)objSize;
            }
        }

        private static void ParseAsfStreamProperties(Stream stream, long objStart, ulong objSize, MediaMetadataResult result)
        {
            if (objSize < 24 + 54)
            {
                return;
            }

            stream.Position = objStart + 24;
            byte[] typeGuid = new byte[16];
            if (stream.Read(typeGuid, 0, 16) < 16)
            {
                return;
            }

            byte[] typeLenBytes = new byte[4];
            stream.Position = objStart + 24 + 40;
            if (stream.Read(typeLenBytes, 0, 4) < 4)
            {
                return;
            }

            uint typeLen = BitConverter.ToUInt32(typeLenBytes, 0);
            if (typeLen == 0 || typeLen > objSize - 24 - 54)
            {
                return;
            }

            if (BytesEqual(typeGuid, AsfVideoMediaGuid) && typeLen >= 68)
            {
                result.HasVideoTrack = true;
                // WMVIDEOINFOHEADER: rcSource(16) rcTarget(16) bitrate(4) buffer(4)
                // flags(4) + BITMAPINFOHEADER: biWidth(48) biHeight(52) biCompression(60)
                stream.Position = objStart + 24 + 54 + 48;
                byte[] wh = new byte[8];
                if (stream.Read(wh, 0, 8) == 8)
                {
                    uint width = BitConverter.ToUInt32(wh, 0);
                    uint height = BitConverter.ToUInt32(wh, 4);
                    if (width > 0 && height > 0)
                    {
                        result.Width ??= width;
                        result.Height ??= height;
                    }
                }

                stream.Position = objStart + 24 + 54 + 60;
                byte[] compression = new byte[4];
                if (stream.Read(compression, 0, 4) == 4)
                {
                    string fourcc = Encoding.ASCII.GetString(compression, 0, 4);
                    result.VideoCodec ??= ResolveVideoCodec(fourcc);
                }
            }
            else if (BytesEqual(typeGuid, AsfAudioMediaGuid) && typeLen >= 16)
            {
                result.HasAudioTrack = true;
                stream.Position = objStart + 24 + 54;
                byte[] fmt = new byte[16];
                if (stream.Read(fmt, 0, 16) == 16)
                {
                    ushort formatTag = BitConverter.ToUInt16(fmt, 0);
                    uint channels = BitConverter.ToUInt16(fmt, 2);
                    uint sampleRate = BitConverter.ToUInt32(fmt, 4);
                    uint avgBytesPerSec = BitConverter.ToUInt32(fmt, 8);
                    uint bits = BitConverter.ToUInt16(fmt, 14);
                    result.AudioCodec ??= WaveFormatNames.TryGetValue(formatTag, out string? name)
                        ? name
                        : $"0x{formatTag:X4}";
                    result.Channels ??= channels;
                    result.SampleRate ??= sampleRate;
                    result.BitsPerSample ??= bits;
                    if (!result.Bitrate.HasValue && avgBytesPerSec > 0)
                    {
                        result.Bitrate = avgBytesPerSec * 8;
                    }
                }
            }
        }

        // ── FLV ──────────────────────────────────────────────────────────────

        private static void TryReadFlv(Stream stream, MediaMetadataResult result)
        {
            stream.Position = 13; // "FLV"(3) + version(1) + flags(1) + headerSize(4) + previousTagSize(4)
            bool foundVideo = false;
            bool foundAudio = false;
            while (stream.Position + 15 <= stream.Length)
            {
                byte[] tagHeader = new byte[11];
                if (stream.Read(tagHeader, 0, 11) < 11)
                {
                    break;
                }

                int tagType = tagHeader[0] & 0x1F;
                uint dataSize = (uint)((tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3]);
                long dataStart = stream.Position;
                if (dataSize == 0 || dataStart + dataSize > stream.Length)
                {
                    break;
                }

                if (tagType == 9 && !foundVideo && dataSize >= 1) // video tag
                {
                    stream.Position = dataStart;
                    int first = stream.ReadByte();
                    if (first >= 0)
                    {
                        int codecId = (first >> 4) & 0x0F;
                        result.HasVideoTrack = true;
                        result.VideoCodec ??= codecId switch
                        {
                            1 => "JPEG",
                            2 => "Sorenson H.263",
                            3 => "Screen Video",
                            4 => "VP6",
                            5 => "VP6 (Alpha)",
                            6 => "H.264 / AVC",
                            7 => "H.265 / HEVC",
                            _ => $"FLV Codec {codecId}"
                        };
                        foundVideo = true;
                    }
                }
                else if (tagType == 8 && !foundAudio && dataSize >= 1) // audio tag
                {
                    stream.Position = dataStart;
                    int first = stream.ReadByte();
                    if (first >= 0)
                    {
                        int soundFormat = (first >> 4) & 0x0F;
                        result.HasAudioTrack = true;
                        result.AudioCodec ??= soundFormat switch
                        {
                            0 => "PCM (Big Endian)",
                            1 => "ADPCM",
                            2 => "MP3",
                            3 => "PCM (Little Endian)",
                            10 => "AAC",
                            11 => "Speex",
                            14 => "Opus",
                            _ => $"FLV Sound {soundFormat}"
                        };
                        foundAudio = true;
                    }
                }

                stream.Position = dataStart + dataSize + 4; // + PreviousTagSize
                if (foundVideo && foundAudio)
                {
                    break;
                }
            }
        }

        // ── MPEG program stream ──────────────────────────────────────────────

        private static void TryReadMpegProgram(Stream stream, MediaMetadataResult result)
        {
            // Scan the first 256 KB for a video sequence header (00 00 01 B3).
            long scanLength = Math.Min(stream.Length, 256 * 1024);
            byte[] buffer = new byte[scanLength];
            stream.Position = 0;
            int read = stream.Read(buffer, 0, buffer.Length);
            for (int i = 0; i + 9 < read; i++)
            {
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 1 || buffer[i + 3] != 0xB3)
                {
                    continue;
                }

                uint width = (uint)((buffer[i + 4] << 4) | (buffer[i + 5] >> 4));
                uint height = (uint)(((buffer[i + 5] & 0x0F) << 8) | buffer[i + 6]);
                if (width > 0 && height > 0)
                {
                    result.Width ??= width;
                    result.Height ??= height;
                }

                result.HasVideoTrack = true;
                result.VideoCodec ??= "MPEG Video";
                break;
            }
        }

        // ── MPEG transport stream ────────────────────────────────────────────

        private static void TryReadMpegTs(Stream stream, MediaMetadataResult result)
        {
            if (stream.Length < 564)
            {
                return;
            }

            // Verify sync bytes on the first three packets (188 bytes each).
            byte[] packet = new byte[188];
            for (int i = 0; i < 3; i++)
            {
                stream.Position = i * 188L;
                if (stream.Read(packet, 0, 188) < 188 || packet[0] != 0x47)
                {
                    return;
                }
            }

            int pmtPid = -1;
            var streamTypes = new List<int>();
            int scanned = 0;
            long pos = 0;
            while (pos + 188 <= stream.Length && scanned < 512)
            {
                stream.Position = pos;
                if (stream.Read(packet, 0, 188) < 188 || packet[0] != 0x47)
                {
                    break;
                }

                int pid = ((packet[1] & 0x1F) << 8) | packet[2];
                if ((packet[3] & 0x10) == 0) // no payload
                {
                    pos += 188;
                    scanned++;
                    continue;
                }

                int payloadStart = 4;
                if ((packet[1] & 0x40) != 0) // adaptation field present
                {
                    payloadStart += 1 + packet[4];
                }

                if (payloadStart >= 188)
                {
                    pos += 188;
                    scanned++;
                    continue;
                }

                int pointer = packet[payloadStart];
                int sectionStart = payloadStart + 1 + pointer;
                if (sectionStart + 5 > 188)
                {
                    pos += 188;
                    scanned++;
                    continue;
                }

                if (pid == 0) // PAT
                {
                    int sectionLength = ((packet[sectionStart + 1] & 0x0F) << 8) | packet[sectionStart + 2];
                    int cursor = sectionStart + 8; // 8-byte table header
                    int sectionEnd = Math.Min(188, sectionStart + 3 + sectionLength - 4);
                    while (cursor + 4 <= sectionEnd)
                    {
                        int programNumber = (packet[cursor] << 8) | packet[cursor + 1];
                        int programPid = ((packet[cursor + 2] & 0x1F) << 8) | packet[cursor + 3];
                        if (programNumber != 0 && pmtPid < 0)
                        {
                            pmtPid = programPid;
                        }

                        cursor += 4;
                    }
                }
                else if (pid == pmtPid && packet[sectionStart] == 0x02) // PMT
                {
                    int sectionLength = ((packet[sectionStart + 1] & 0x0F) << 8) | packet[sectionStart + 2];
                    int programInfoLength = ((packet[sectionStart + 11] & 0x0F) << 8) | packet[sectionStart + 12];
                    int cursor = sectionStart + 13 + programInfoLength;
                    int sectionEnd = Math.Min(188, sectionStart + 3 + sectionLength - 4);
                    while (cursor + 5 <= sectionEnd)
                    {
                        streamTypes.Add(packet[cursor]);
                        int esInfoLength = ((packet[cursor + 3] & 0x0F) << 8) | packet[cursor + 4];
                        cursor += 5 + esInfoLength;
                    }

                    if (streamTypes.Count > 0)
                    {
                        break;
                    }
                }

                pos += 188;
                scanned++;
            }

            foreach (int streamType in streamTypes)
            {
                switch (streamType)
                {
                    case 0x01:
                        result.HasVideoTrack = true;
                        result.VideoCodec ??= "MPEG-1 Video";
                        break;
                    case 0x02:
                        result.HasVideoTrack = true;
                        result.VideoCodec ??= "MPEG-2 Video";
                        break;
                    case 0x1B:
                        result.HasVideoTrack = true;
                        result.VideoCodec ??= "H.264 / AVC";
                        break;
                    case 0x24:
                        result.HasVideoTrack = true;
                        result.VideoCodec ??= "H.265 / HEVC";
                        break;
                    case 0x03:
                    case 0x04:
                        result.HasAudioTrack = true;
                        result.AudioCodec ??= "MPEG Audio";
                        break;
                    case 0x0F:
                    case 0x11:
                        result.HasAudioTrack = true;
                        result.AudioCodec ??= "AAC";
                        break;
                    case 0x81:
                        result.HasAudioTrack = true;
                        result.AudioCodec ??= "AC-3";
                        break;
                    case 0x87:
                        result.HasAudioTrack = true;
                        result.AudioCodec ??= "E-AC-3";
                        break;
                }
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        private static string? GetContainerFromExtension(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".mp3" => "MP3",
                ".wav" => "WAV",
                ".flac" => "FLAC",
                ".m4a" => "M4A",
                ".m4b" => "M4B",
                ".aac" => "AAC",
                ".ogg" or ".oga" => "OGG",
                ".opus" => "OPUS",
                ".wma" => "WMA",
                ".aif" or ".aiff" => "AIFF",
                ".amr" => "AMR",
                ".mid" or ".midi" => "MIDI",
                ".ape" => "APE",
                ".mp4" or ".m4v" or ".mp4v" => "MP4",
                ".mov" => "MOV",
                ".mkv" => "MKV",
                ".avi" => "AVI",
                ".webm" => "WEBM",
                ".wmv" => "WMV",
                ".flv" => "FLV",
                ".mpg" or ".mpeg" => "MPEG",
                ".ts" => "MPEG-TS",
                ".m2ts" or ".mts" => "M2TS",
                ".3gp" or ".3g2" => "3GP",
                ".ogv" => "OGG",
                ".asf" => "ASF",
                ".rm" or ".rmvb" => "RM",
                ".vob" => "VOB",
                ".divx" => "DIVX",
                ".mxf" => "MXF",
                _ => null
            };
        }

        private static string? JoinStrings(IEnumerable<string>? values)
        {
            if (values is null)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(value.Trim());
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static void AddTagIfPresent(MediaMetadataResult result, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || result.Tags.ContainsKey(key))
            {
                return;
            }

            result.Tags[key] = value.Trim();
        }

        private static string TrimTag(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\0', ' ').Trim();
        }

        private static int SyncSafeToInt(byte[] data, int offset)
        {
            return ((data[offset] & 0x7F) << 21) |
                   ((data[offset + 1] & 0x7F) << 14) |
                   ((data[offset + 2] & 0x7F) << 7) |
                   (data[offset + 3] & 0x7F);
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) |
                   (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) |
                   data[offset + 3];
        }

        private static string GetImageMimeType(byte[] imgBytes, string fallback = "image/jpeg")
        {
            if (imgBytes == null || imgBytes.Length < 4) return fallback;

            if (imgBytes[0] == 0xFF && imgBytes[1] == 0xD8) return "image/jpeg";
            if (imgBytes[0] == 0x89 && imgBytes[1] == 'P' && imgBytes[2] == 'N' && imgBytes[3] == 'G') return "image/png";
            if (imgBytes[0] == 'R' && imgBytes[1] == 'I' && imgBytes[2] == 'F' && imgBytes[3] == 'F') return "image/webp";
            if (imgBytes[0] == 'G' && imgBytes[1] == 'I' && imgBytes[2] == 'F') return "image/gif";
            if (imgBytes[0] == 'B' && imgBytes[1] == 'M') return "image/bmp";

            return fallback;
        }

        /// <summary>
        /// Attempts to extract album art cover image from the specified audio/media file
        /// returning a base64 data URI (data:image/...;base64,...), or null if no artwork is available.
        /// </summary>
        public static async Task<string?> GetAlbumArtAsync(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            // 1. Try Windows Storage API Thumbnail (MusicView mode) with 800ms timeout guard
            try
            {
                var fileTask = StorageFile.GetFileFromPathAsync(filePath).AsTask();
                if (await Task.WhenAny(fileTask, Task.Delay(800)) == fileTask)
                {
                    var file = await fileTask;
                    var thumbTask = file.GetThumbnailAsync(ThumbnailMode.MusicView, 600, ThumbnailOptions.UseCurrentScale).AsTask();
                    if (await Task.WhenAny(thumbTask, Task.Delay(800)) == thumbTask)
                    {
                        using var thumbnail = await thumbTask;
                        if (thumbnail != null && thumbnail.Size > 0)
                        {
                            using var stream = thumbnail.AsStreamForRead();
                            using var ms = new MemoryStream();
                            await stream.CopyToAsync(ms);
                            byte[] bytes = ms.ToArray();
                            if (bytes.Length > 100)
                            {
                                string rawContentType = thumbnail.ContentType;
                                string fallbackMime = string.IsNullOrEmpty(rawContentType) || rawContentType.Contains("win-bitmap") ? "image/jpeg" : rawContentType;
                                string mime = GetImageMimeType(bytes, fallbackMime);
                                return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // 2. Try Embedded Tag Picture Parsing (MP3 ID3v2 APIC/PIC, FLAC picture block, M4A/MP4 covr atom)
            try
            {
                string? embedded = ExtractEmbeddedPicture(filePath);
                if (!string.IsNullOrEmpty(embedded))
                {
                    return embedded;
                }
            }
            catch
            {
            }

            // 3. Try Same-Folder Artwork Image Files
            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
                    string[] candidateNames = { fileNameNoExt, "cover", "folder", "album", "front", "art", "artwork", "albumart", "albumartsmall" };
                    string[] candidateExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

                    foreach (var cand in candidateNames)
                    {
                        foreach (var ext in candidateExtensions)
                        {
                            string candidatePath = Path.Combine(directory, cand + ext);
                            if (File.Exists(candidatePath))
                            {
                                byte[] imgBytes = File.ReadAllBytes(candidatePath);
                                if (imgBytes.Length > 0)
                                {
                                    string mime = GetImageMimeType(imgBytes, ext == ".png" ? "image/png" : "image/jpeg");
                                    return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                                }
                            }
                        }
                    }

                    // Fallback: If folder has 1..4 image files, pick the first one as cover art
                    var imageFiles = Directory.GetFiles(directory)
                        .Where(f => {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp";
                        }).ToArray();

                    if (imageFiles.Length > 0 && imageFiles.Length <= 4)
                    {
                        byte[] imgBytes = File.ReadAllBytes(imageFiles[0]);
                        if (imgBytes.Length > 0)
                        {
                            string mime = GetImageMimeType(imgBytes, "image/jpeg");
                            return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string? ExtractEmbeddedPicture(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractId3v2Picture(filePath);
            }
            else if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractFlacPicture(filePath);
            }
            else if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractM4aPicture(filePath);
            }
            return null;
        }

        private static string? ExtractM4aPicture(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int maxRead = (int)Math.Min(fs.Length, 12 * 1024 * 1024);
                byte[] buffer = new byte[maxRead];
                int read = fs.Read(buffer, 0, maxRead);
                if (read < 16) return null;

                byte[] covrTag = Encoding.ASCII.GetBytes("covr");
                int covrIndex = IndexOfBytes(buffer, covrTag, 0, read);
                if (covrIndex < 0) return null;

                byte[] dataTag = Encoding.ASCII.GetBytes("data");
                int dataIndex = IndexOfBytes(buffer, dataTag, covrIndex, read - covrIndex);
                if (dataIndex < 4) return null;

                int boxLen = ReadBigEndianInt32(buffer, dataIndex - 4);
                int typeFlags = dataIndex + 8 < read ? ReadBigEndianInt32(buffer, dataIndex + 4) : 0;
                int dataOffset = dataIndex + 12;

                int imgLen = boxLen - 16;
                if (imgLen > 0 && dataOffset + imgLen <= read)
                {
                    byte[] imgBytes = new byte[imgLen];
                    Buffer.BlockCopy(buffer, dataOffset, imgBytes, 0, imgLen);
                    string fallbackMime = typeFlags == 14 ? "image/png" : "image/jpeg";
                    string mime = GetImageMimeType(imgBytes, fallbackMime);
                    return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                }
            }
            catch
            {
            }
            return null;
        }

        private static int IndexOfBytes(byte[] array, byte[] pattern, int startIndex, int count)
        {
            int endIndex = Math.Min(array.Length, startIndex + count) - pattern.Length;
            for (int i = startIndex; i <= endIndex; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (array[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string? ExtractId3v2Picture(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 10) return null;

            byte[] header = new byte[10];
            if (fs.Read(header, 0, 10) < 10) return null;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

            byte majorVersion = header[3];
            int tagSize = SyncSafeToInt(header, 6);
            if (tagSize <= 0 || tagSize > fs.Length - 10) return null;

            byte[] tagData = new byte[tagSize];
            if (fs.Read(tagData, 0, tagSize) < tagSize) return null;

            int offset = 0;
            while (offset < tagSize - 10)
            {
                if (majorVersion == 2)
                {
                    string frameId = Encoding.ASCII.GetString(tagData, offset, 3);
                    int frameSize = (tagData[offset + 3] << 16) | (tagData[offset + 4] << 8) | tagData[offset + 5];
                    offset += 6;
                    if (frameSize <= 0 || offset + frameSize > tagSize) break;

                    if (frameId == "PIC")
                    {
                        string format = Encoding.ASCII.GetString(tagData, offset + 1, 3).ToLowerInvariant();
                        string fallbackMime = format switch { "png" => "image/png", "jpg" => "image/jpeg", "jpeg" => "image/jpeg", _ => "image/jpeg" };
                        int imgStart = offset + 5;
                        while (imgStart < offset + frameSize && tagData[imgStart] != 0) imgStart++;
                        imgStart++;
                        int imgLen = (offset + frameSize) - imgStart;
                        if (imgLen > 0 && imgStart + imgLen <= tagData.Length)
                        {
                            byte[] imgBytes = new byte[imgLen];
                            Buffer.BlockCopy(tagData, imgStart, imgBytes, 0, imgLen);
                            string mime = GetImageMimeType(imgBytes, fallbackMime);
                            return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                        }
                    }
                    offset += frameSize;
                }
                else
                {
                    if (tagData[offset] == 0) break;
                    string frameId = Encoding.ASCII.GetString(tagData, offset, 4);
                    int frameSize = majorVersion == 4
                        ? SyncSafeToInt(tagData, offset + 4)
                        : ReadBigEndianInt32(tagData, offset + 4);
                    offset += 10;
                    if (frameSize <= 0 || offset + frameSize > tagSize) break;

                    if (frameId == "APIC")
                    {
                        byte encoding = tagData[offset];
                        int mimeEnd = offset + 1;
                        while (mimeEnd < offset + frameSize && tagData[mimeEnd] != 0) mimeEnd++;
                        string mimeStr = Encoding.ASCII.GetString(tagData, offset + 1, mimeEnd - (offset + 1));
                        if (string.IsNullOrEmpty(mimeStr)) mimeStr = "image/jpeg";

                        int picTypeOffset = mimeEnd + 1;
                        if (picTypeOffset < offset + frameSize)
                        {
                            int descStart = picTypeOffset + 1;
                            int descEnd = descStart;
                            if (encoding == 1 || encoding == 2)
                            {
                                while (descEnd + 1 < offset + frameSize && (tagData[descEnd] != 0 || tagData[descEnd + 1] != 0)) descEnd += 2;
                                descEnd += 2;
                            }
                            else
                            {
                                while (descEnd < offset + frameSize && tagData[descEnd] != 0) descEnd++;
                                descEnd += 1;
                            }

                            int imgStart = descEnd;
                            int imgLen = (offset + frameSize) - imgStart;
                            if (imgLen > 0 && imgStart + imgLen <= tagData.Length)
                            {
                                byte[] imgBytes = new byte[imgLen];
                                Buffer.BlockCopy(tagData, imgStart, imgBytes, 0, imgLen);
                                string mime = GetImageMimeType(imgBytes, mimeStr);
                                return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                            }
                        }
                    }
                    offset += frameSize;
                }
            }
            return null;
        }

        private static string? ExtractFlacPicture(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return null;

            byte[] marker = new byte[4];
            if (fs.Read(marker, 0, 4) < 4) return null;
            if (marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C') return null;

            while (fs.Position + 4 <= fs.Length)
            {
                byte[] blockHeader = new byte[4];
                if (fs.Read(blockHeader, 0, 4) < 4) break;

                bool isLast = (blockHeader[0] & 0x80) != 0;
                int blockType = blockHeader[0] & 0x7F;
                int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

                if (blockType == 6)
                {
                    if (blockLength <= 0 || fs.Position + blockLength > fs.Length) break;
                    byte[] pictureData = new byte[blockLength];
                    if (fs.Read(pictureData, 0, blockLength) < blockLength) break;

                    int pos = 4;
                    if (pos + 4 > blockLength) break;
                    int mimeLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;
                    if (pos + mimeLen > blockLength) break;
                    string mimeStr = Encoding.ASCII.GetString(pictureData, pos, mimeLen);
                    pos += mimeLen;

                    if (pos + 4 > blockLength) break;
                    int descLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;
                    pos += descLen;

                    pos += 16;

                    if (pos + 4 > blockLength) break;
                    int dataLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;

                    if (dataLen > 0 && pos + dataLen <= blockLength)
                    {
                        byte[] imgBytes = new byte[dataLen];
                        Buffer.BlockCopy(pictureData, pos, imgBytes, 0, dataLen);
                        string mime = GetImageMimeType(imgBytes, string.IsNullOrEmpty(mimeStr) ? "image/jpeg" : mimeStr);
                        return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                    }
                }
                else
                {
                    fs.Seek(blockLength, SeekOrigin.Current);
                }

                if (isLast) break;
            }
            return null;
        }
    }
}
