using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

            if (!result.HasAny)
            {
                TryReadContainerFallback(filePath, result);
            }

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

                byte[] magic = new byte[8];
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
                                    result.Duration = TimeSpan.FromSeconds((double)frames * samplesPerFrame / sampleRate);
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
                        result.AudioCodec = WaveFormatNames.TryGetValue(formatTag, out string? name)
                            ? name
                            : $"0x{formatTag:X4}";
                        if (formatTag == 0xFFFE && read >= 40)
                        {
                            ushort sub = BitConverter.ToUInt16(fmt, 24);
                            if (WaveFormatNames.TryGetValue(sub, out string? subName))
                            {
                                result.AudioCodec = subName;
                            }
                        }

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

            if (dataSize.HasValue && result.Bitrate.HasValue && result.Bitrate.Value > 0)
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
                        result.Duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);
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
                    result.Duration = TimeSpan.FromSeconds((double)duration / timescale);
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
                    result.Duration = TimeSpan.FromSeconds((double)duration / timescale);
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
                            result.Duration = TimeSpan.FromSeconds((double)granule / rate);
                        }

                        break;
                    }
                }
            }
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
    }
}
