using System;
using System.Globalization;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaCodecCatalog;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaMetadataUtilities;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class Mp4MediaMetadataParser : IMediaMetadataParser
    {
        public bool CanRead(byte[] header, int bytesRead)
        {
            return bytesRead >= 8 &&
                   header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p';
        }

        public void Read(Stream stream, MediaMetadataResult result)
        {
            TryReadMp4(stream, result);
        }

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
    }
}
