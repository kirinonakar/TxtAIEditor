using System;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaCodecCatalog;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class MatroskaMediaMetadataParser : IMediaMetadataParser
    {
        public bool CanRead(byte[] header, int bytesRead)
        {
            return bytesRead >= 4 &&
                   header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;
        }

        public void Read(Stream stream, MediaMetadataResult result)
        {
            TryReadMkv(stream, result);
        }

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
    }
}
