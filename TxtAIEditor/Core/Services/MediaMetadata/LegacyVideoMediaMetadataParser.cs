using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaCodecCatalog;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class LegacyVideoMediaMetadataParser : IMediaMetadataParser
    {
        public bool CanRead(byte[] header, int bytesRead)
        {
            return IsAvi(header, bytesRead) ||
                   (bytesRead >= 4 && header[0] == 'F' && header[1] == 'L' && header[2] == 'V') ||
                   (bytesRead >= 4 && header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75) ||
                   IsMpegProgram(header, bytesRead) ||
                   (bytesRead >= 4 && header[0] == 0x47);
        }

        public void Read(Stream stream, MediaMetadataResult result)
        {
            byte[] header = new byte[16];
            int bytesRead = stream.Read(header, 0, header.Length);
            stream.Position = 0;

            if (IsAvi(header, bytesRead))
            {
                TryReadAvi(stream, result);
            }
            else if (bytesRead >= 4 && header[0] == 'F' && header[1] == 'L' && header[2] == 'V')
            {
                TryReadFlv(stream, result);
            }
            else if (bytesRead >= 4 && header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75)
            {
                TryReadAsf(stream, result);
            }
            else if (IsMpegProgram(header, bytesRead))
            {
                TryReadMpegProgram(stream, result);
            }
            else
            {
                TryReadMpegTs(stream, result);
            }
        }

        private static bool IsAvi(byte[] header, int bytesRead)
        {
            return bytesRead >= 12 &&
                   header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                   header[8] == 'A' && header[9] == 'V' && header[10] == 'I' && header[11] == ' ';
        }

        private static bool IsMpegProgram(byte[] header, int bytesRead)
        {
            return bytesRead >= 4 &&
                   header[0] == 0 && header[1] == 0 && header[2] == 1 &&
                   (header[3] == 0xBA || header[3] == 0xB3);
        }

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
    }
}
