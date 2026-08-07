using System;
using System.Globalization;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaMetadataUtilities;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class Mp3MediaMetadataParser : IMediaMetadataParser
    {
        public bool CanRead(byte[] header, int bytesRead)
        {
            return (bytesRead >= 3 && header[0] == 'I' && header[1] == 'D' && header[2] == '3') ||
                   (bytesRead >= 4 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0);
        }

        public void Read(Stream stream, MediaMetadataResult result)
        {
            TryReadMp3(stream, result);
        }

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
    }
}
