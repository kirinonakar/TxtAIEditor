using System;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaCodecCatalog;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaMetadataUtilities;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class WaveFlacOggMediaMetadataParser : IMediaMetadataParser
    {
        public bool CanRead(byte[] header, int bytesRead)
        {
            return IsWave(header, bytesRead) ||
                   (bytesRead >= 4 && header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C') ||
                   (bytesRead >= 4 && header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S');
        }

        public void Read(Stream stream, MediaMetadataResult result)
        {
            byte[] header = new byte[12];
            int bytesRead = stream.Read(header, 0, header.Length);
            stream.Position = 0;

            if (IsWave(header, bytesRead))
            {
                TryReadWav(stream, result);
            }
            else if (bytesRead >= 4 && header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
            {
                TryReadFlac(stream, result);
            }
            else
            {
                TryReadOgg(stream, result);
            }
        }

        private static bool IsWave(byte[] header, int bytesRead)
        {
            // AVI is claimed by LegacyVideoMediaMetadataParser first, matching
            // the original RIFF fallback order.
            return bytesRead >= 4 &&
                   header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F';
        }

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
    }
}
