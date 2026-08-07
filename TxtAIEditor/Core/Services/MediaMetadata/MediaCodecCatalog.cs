using System;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal static class MediaCodecCatalog
    {
        internal static readonly Dictionary<string, string> VideoCodecNames = new(StringComparer.OrdinalIgnoreCase)
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

        internal static readonly Dictionary<string, string> AudioFourCcNames = new(StringComparer.OrdinalIgnoreCase)
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

        internal static readonly Dictionary<uint, string> WaveFormatNames = new()
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

        internal static string? ResolveVideoCodec(string code)
        {
            string trimmed = code.Trim();
            return VideoCodecNames.TryGetValue(trimmed, out string? name) ? name : trimmed;
        }

        internal static string? ResolveVideoFourCc(uint fourCc)
        {
            string? code = DecodeFourCc(fourCc);
            return code is null ? null : ResolveVideoCodec(code);
        }

        internal static string? ResolveAudioFourCc(uint fourCc)
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

        internal static string? ResolveAudioCodec(string code)
        {
            string trimmed = code.Trim();
            return AudioFourCcNames.TryGetValue(trimmed, out string? name) ? name : trimmed;
        }

        internal static string? DecodeFourCc(uint value)
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
    }
}
