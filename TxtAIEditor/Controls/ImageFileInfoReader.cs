using System;
using System.IO;

namespace TxtAIEditor.Controls
{
    internal readonly struct ImageFileInfo
    {
        public ImageFileInfo(int width, int height, string format)
        {
            Width = width;
            Height = height;
            Format = format;
        }

        public int Width { get; }
        public int Height { get; }
        public string Format { get; }
    }

    internal static class ImageFileInfoReader
    {
        public static bool TryRead(string? filePath, out ImageFileInfo imageInfo)
        {
            imageInfo = default;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                int sampleLength = (int)Math.Min(stream.Length, 1024 * 1024);
                if (sampleLength <= 0)
                {
                    return false;
                }

                var bytes = new byte[sampleLength];
                int read = stream.Read(bytes, 0, bytes.Length);
                if (TryReadRasterHeader(bytes, read, out imageInfo))
                {
                    return true;
                }

                if (read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                {
                    stream.Position = 0;
                    return TryReadJpegHeader(stream, out imageInfo);
                }
            }
            catch
            {
            }

            return false;
        }

        public static string? GetFormatFromExtension(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "PNG",
                ".jpg" => "JPEG",
                ".jpeg" => "JPEG",
                ".gif" => "GIF",
                ".bmp" => "BMP",
                ".ico" => "ICO",
                ".webp" => "WEBP",
                _ => null
            };
        }

        private static bool TryReadRasterHeader(byte[] bytes, int length, out ImageFileInfo imageInfo)
        {
            imageInfo = default;

            if (length >= 24 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47 &&
                bytes[12] == 0x49 &&
                bytes[13] == 0x48 &&
                bytes[14] == 0x44 &&
                bytes[15] == 0x52)
            {
                imageInfo = new ImageFileInfo(ReadBigEndianInt32(bytes, 16), ReadBigEndianInt32(bytes, 20), "PNG");
                return IsValidImageInfo(imageInfo);
            }

            if (length >= 10 &&
                bytes[0] == 0x47 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46)
            {
                imageInfo = new ImageFileInfo(ReadLittleEndianUInt16(bytes, 6), ReadLittleEndianUInt16(bytes, 8), "GIF");
                return IsValidImageInfo(imageInfo);
            }

            if (length >= 26 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            {
                int dibHeaderSize = ReadLittleEndianInt32(bytes, 14);
                if (dibHeaderSize == 12)
                {
                    imageInfo = new ImageFileInfo(ReadLittleEndianUInt16(bytes, 18), ReadLittleEndianUInt16(bytes, 20), "BMP");
                }
                else
                {
                    int width = ReadLittleEndianInt32(bytes, 18);
                    int height = ReadLittleEndianInt32(bytes, 22);
                    imageInfo = new ImageFileInfo(Math.Abs(width), Math.Abs(height), "BMP");
                }

                return IsValidImageInfo(imageInfo);
            }

            if (TryReadIcoHeader(bytes, length, out imageInfo))
            {
                return true;
            }

            return TryReadWebPHeader(bytes, length, out imageInfo);
        }

        private static bool TryReadIcoHeader(byte[] bytes, int length, out ImageFileInfo imageInfo)
        {
            imageInfo = default;
            if (length < 22 ||
                ReadLittleEndianUInt16(bytes, 0) != 0 ||
                ReadLittleEndianUInt16(bytes, 2) != 1)
            {
                return false;
            }

            int entryCount = ReadLittleEndianUInt16(bytes, 4);
            if (entryCount <= 0 || entryCount > 256)
            {
                return false;
            }

            int directoryLength = 6 + (entryCount * 16);
            if (directoryLength > length)
            {
                return false;
            }

            int bestWidth = 0;
            int bestHeight = 0;
            for (int i = 0; i < entryCount; i++)
            {
                int entryOffset = 6 + (i * 16);
                int width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
                int height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
                int imageBytes = ReadLittleEndianInt32(bytes, entryOffset + 8);
                int imageOffset = ReadLittleEndianInt32(bytes, entryOffset + 12);
                if (width <= 0 || height <= 0 || imageBytes <= 0 || imageOffset < directoryLength)
                {
                    continue;
                }

                if ((long)width * height > (long)bestWidth * bestHeight)
                {
                    bestWidth = width;
                    bestHeight = height;
                }
            }

            imageInfo = new ImageFileInfo(bestWidth, bestHeight, "ICO");
            return IsValidImageInfo(imageInfo);
        }

        private static bool TryReadWebPHeader(byte[] bytes, int length, out ImageFileInfo imageInfo)
        {
            imageInfo = default;
            if (length < 30 ||
                bytes[0] != 0x52 ||
                bytes[1] != 0x49 ||
                bytes[2] != 0x46 ||
                bytes[3] != 0x46 ||
                bytes[8] != 0x57 ||
                bytes[9] != 0x45 ||
                bytes[10] != 0x42 ||
                bytes[11] != 0x50)
            {
                return false;
            }

            int offset = 12;
            while (offset + 8 <= length)
            {
                int chunkSize = ReadLittleEndianInt32(bytes, offset + 4);
                int dataOffset = offset + 8;
                if (chunkSize < 0 || chunkSize > length - dataOffset)
                {
                    return false;
                }

                if (HasFourCc(bytes, offset, "VP8X") && chunkSize >= 10)
                {
                    int width = ReadLittleEndianUInt24(bytes, dataOffset + 4) + 1;
                    int height = ReadLittleEndianUInt24(bytes, dataOffset + 7) + 1;
                    imageInfo = new ImageFileInfo(width, height, "WEBP");
                    return IsValidImageInfo(imageInfo);
                }

                if (HasFourCc(bytes, offset, "VP8L") && chunkSize >= 5 && bytes[dataOffset] == 0x2F)
                {
                    uint bits = (uint)(bytes[dataOffset + 1] |
                        (bytes[dataOffset + 2] << 8) |
                        (bytes[dataOffset + 3] << 16) |
                        (bytes[dataOffset + 4] << 24));
                    int width = (int)(bits & 0x3FFF) + 1;
                    int height = (int)((bits >> 14) & 0x3FFF) + 1;
                    imageInfo = new ImageFileInfo(width, height, "WEBP");
                    return IsValidImageInfo(imageInfo);
                }

                if (HasFourCc(bytes, offset, "VP8 ") &&
                    chunkSize >= 10 &&
                    bytes[dataOffset + 3] == 0x9D &&
                    bytes[dataOffset + 4] == 0x01 &&
                    bytes[dataOffset + 5] == 0x2A)
                {
                    int width = ReadLittleEndianUInt16(bytes, dataOffset + 6) & 0x3FFF;
                    int height = ReadLittleEndianUInt16(bytes, dataOffset + 8) & 0x3FFF;
                    imageInfo = new ImageFileInfo(width, height, "WEBP");
                    return IsValidImageInfo(imageInfo);
                }

                offset = dataOffset + chunkSize + (chunkSize % 2);
            }

            return false;
        }

        private static bool TryReadJpegHeader(FileStream stream, out ImageFileInfo imageInfo)
        {
            imageInfo = default;
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            {
                return false;
            }

            while (stream.Position < stream.Length)
            {
                int markerPrefix;
                do
                {
                    markerPrefix = stream.ReadByte();
                    if (markerPrefix < 0)
                    {
                        return false;
                    }
                }
                while (markerPrefix != 0xFF);

                int marker;
                do
                {
                    marker = stream.ReadByte();
                    if (marker < 0)
                    {
                        return false;
                    }
                }
                while (marker == 0xFF);

                if (marker == 0xD9 || marker == 0xDA)
                {
                    return false;
                }

                if (IsStandaloneJpegMarker(marker))
                {
                    continue;
                }

                int segmentLength = ReadBigEndianUInt16(stream);
                if (segmentLength < 2)
                {
                    return false;
                }

                if (IsJpegStartOfFrameMarker(marker))
                {
                    if (stream.ReadByte() < 0)
                    {
                        return false;
                    }

                    int height = ReadBigEndianUInt16(stream);
                    int width = ReadBigEndianUInt16(stream);
                    imageInfo = new ImageFileInfo(width, height, "JPEG");
                    return IsValidImageInfo(imageInfo);
                }

                stream.Seek(segmentLength - 2, SeekOrigin.Current);
            }

            return false;
        }

        private static bool IsValidImageInfo(ImageFileInfo imageInfo)
        {
            return imageInfo.Width > 0 && imageInfo.Height > 0 && !string.IsNullOrWhiteSpace(imageInfo.Format);
        }

        private static bool IsStandaloneJpegMarker(int marker)
        {
            return marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7);
        }

        private static bool IsJpegStartOfFrameMarker(int marker)
        {
            return marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
        }

        private static bool HasFourCc(byte[] bytes, int offset, string fourCc)
        {
            return offset + 4 <= bytes.Length &&
                   bytes[offset] == fourCc[0] &&
                   bytes[offset + 1] == fourCc[1] &&
                   bytes[offset + 2] == fourCc[2] &&
                   bytes[offset + 3] == fourCc[3];
        }

        private static int ReadBigEndianUInt16(FileStream stream)
        {
            int high = stream.ReadByte();
            int low = stream.ReadByte();
            return high < 0 || low < 0 ? 0 : (high << 8) | low;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) |
                   (bytes[offset + 1] << 16) |
                   (bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static int ReadLittleEndianUInt16(byte[] bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static int ReadLittleEndianUInt24(byte[] bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        }

        private static int ReadLittleEndianInt32(byte[] bytes, int offset)
        {
            return bytes[offset] |
                   (bytes[offset + 1] << 8) |
                   (bytes[offset + 2] << 16) |
                   (bytes[offset + 3] << 24);
        }
    }
}
