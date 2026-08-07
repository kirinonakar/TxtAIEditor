using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal static class MediaMetadataUtilities
    {
        internal static string? GetContainerFromExtension(string filePath)
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

        internal static string? JoinStrings(IEnumerable<string>? values)
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

        internal static void AddTagIfPresent(MediaMetadataResult result, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || result.Tags.ContainsKey(key))
            {
                return;
            }

            result.Tags[key] = value.Trim();
        }

        internal static string TrimTag(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\0', ' ').Trim();
        }

        internal static int SyncSafeToInt(byte[] data, int offset)
        {
            return ((data[offset] & 0x7F) << 21) |
                   ((data[offset + 1] & 0x7F) << 14) |
                   ((data[offset + 2] & 0x7F) << 7) |
                   (data[offset + 3] & 0x7F);
        }

        internal static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) |
                   (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) |
                   data[offset + 3];
        }

        internal static string GetImageMimeType(byte[] imgBytes, string fallback = "image/jpeg")
        {
            if (imgBytes == null || imgBytes.Length < 4) return fallback;

            if (imgBytes[0] == 0xFF && imgBytes[1] == 0xD8) return "image/jpeg";
            if (imgBytes[0] == 0x89 && imgBytes[1] == 'P' && imgBytes[2] == 'N' && imgBytes[3] == 'G') return "image/png";
            if (imgBytes[0] == 'R' && imgBytes[1] == 'I' && imgBytes[2] == 'F' && imgBytes[3] == 'F') return "image/webp";
            if (imgBytes[0] == 'G' && imgBytes[1] == 'I' && imgBytes[2] == 'F') return "image/gif";
            if (imgBytes[0] == 'B' && imgBytes[1] == 'M') return "image/bmp";

            return fallback;
        }
    }
}
