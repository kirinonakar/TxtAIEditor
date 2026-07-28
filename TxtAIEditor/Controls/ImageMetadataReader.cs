using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TxtAIEditor.Controls
{
    /// <summary>
    /// Holds parsed metadata from an image file including EXIF tags,
    /// Stable Diffusion / ComfyUI generation parameters, and color mode.
    /// </summary>
    internal sealed class ImageMetadataResult
    {
        // ── Color mode ──────────────────────────────────────────────
        /// <summary>e.g. "RGBA (8bit)", "RGB (8bit)", "Grayscale+Alpha (16bit)"</summary>
        public string? ColorMode { get; set; }

        // ── EXIF ────────────────────────────────────────────────────
        public Dictionary<string, string> ExifTags { get; } = new(StringComparer.OrdinalIgnoreCase);

        // ── Stable Diffusion (A1111 / Forge) ────────────────────────
        public string? SdPrompt { get; set; }
        public string? SdNegativePrompt { get; set; }
        public Dictionary<string, string> SdParameters { get; } = new(StringComparer.OrdinalIgnoreCase);

        // ── ComfyUI ─────────────────────────────────────────────────
        public Dictionary<string, string> ComfyParameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ComfyWorkflowJson { get; set; }

        // ── Raw PNG text chunks (for fallback) ──────────────────────
        public Dictionary<string, string> PngTextChunks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasExif => ExifTags.Count > 0;
        public bool HasStableDiffusion => !string.IsNullOrEmpty(SdPrompt) || SdParameters.Count > 0;
        public bool HasComfyUI => ComfyParameters.Count > 0 || !string.IsNullOrEmpty(ComfyWorkflowJson);
        public bool HasAny => HasExif || HasStableDiffusion || HasComfyUI || !string.IsNullOrEmpty(ColorMode);
    }

    /// <summary>
    /// Reads extended metadata from image files:
    /// EXIF, Stable Diffusion, ComfyUI generation params, and color mode.
    /// Operates independently from <see cref="ImageFileInfoReader"/>.
    /// </summary>
    internal static class ImageMetadataReader
    {
        // PNG color type constants
        private const byte PngColorGrayscale = 0;
        private const byte PngColorRgb = 2;
        private const byte PngColorIndexed = 3;
        private const byte PngColorGrayscaleAlpha = 4;
        private const byte PngColorRgba = 6;

        // Well-known EXIF IFD0 / SubIFD tag IDs
        private static readonly Dictionary<ushort, string> ExifTagNames = new()
        {
            [0x010F] = "Make",
            [0x0110] = "Model",
            [0x0112] = "Orientation",
            [0x011A] = "XResolution",
            [0x011B] = "YResolution",
            [0x0131] = "Software",
            [0x0132] = "DateTime",
            [0x8769] = "ExifIFDPointer",
            [0x8825] = "GPSInfoIFDPointer",
            [0x829A] = "ExposureTime",
            [0x829D] = "FNumber",
            [0x8827] = "ISOSpeedRatings",
            [0x9000] = "ExifVersion",
            [0x9003] = "DateTimeOriginal",
            [0x9004] = "DateTimeDigitized",
            [0x920A] = "FocalLength",
            [0xA001] = "ColorSpace",
            [0xA002] = "PixelXDimension",
            [0xA003] = "PixelYDimension",
            [0xA405] = "FocalLengthIn35mmFilm",
            [0xA430] = "CameraOwnerName",
            [0xA431] = "BodySerialNumber",
            [0xA432] = "LensInfo",
            [0xA433] = "LensMake",
            [0xA434] = "LensModel",
        };

        public static bool TryRead(string? filePath, out ImageMetadataResult result)
        {
            result = new ImageMetadataResult();
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

                if (stream.Length < 8)
                {
                    return false;
                }

                var header = new byte[8];
                if (stream.Read(header, 0, 8) < 8)
                {
                    return false;
                }

                stream.Position = 0;

                // PNG
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                {
                    ReadPngMetadata(stream, result);
                    return result.HasAny;
                }

                // JPEG
                if (header[0] == 0xFF && header[1] == 0xD8)
                {
                    ReadJpegMetadata(stream, result);
                    return result.HasAny;
                }

                // WebP
                if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                    stream.Length >= 12)
                {
                    var webpSig = new byte[4];
                    stream.Position = 8;
                    if (stream.Read(webpSig, 0, 4) == 4 &&
                        webpSig[0] == 0x57 && webpSig[1] == 0x45 && webpSig[2] == 0x42 && webpSig[3] == 0x50)
                    {
                        stream.Position = 0;
                        ReadWebPMetadata(stream, result);
                        return result.HasAny;
                    }
                }
            }
            catch
            {
                // Swallow parse errors – partial results are still valid
            }

            return result.HasAny;
        }

        // ═══════════════════════════════════════════════════════════
        // PNG
        // ═══════════════════════════════════════════════════════════

        private static void ReadPngMetadata(Stream stream, ImageMetadataResult result)
        {
            stream.Position = 8; // skip signature

            var chunkHeader = new byte[8];
            while (stream.Read(chunkHeader, 0, 8) == 8)
            {
                int length = ReadBE32(chunkHeader, 0);
                string type = Encoding.ASCII.GetString(chunkHeader, 4, 4);

                if (length < 0 || length > 100 * 1024 * 1024)
                {
                    break;
                }

                long dataStart = stream.Position;

                switch (type)
                {
                    case "IHDR":
                        ReadPngIhdr(stream, length, result);
                        break;
                    case "tEXt":
                        ReadPngText(stream, length, result);
                        break;
                    case "iTXt":
                        ReadPngItxt(stream, length, result);
                        break;
                    case "zTXt":
                        ReadPngZtxt(stream, length, result);
                        break;
                    case "eXIf":
                    case "exIf":
                        ReadPngExifChunk(stream, length, result);
                        break;
                    case "IEND":
                        goto done;
                }

                // Skip to next chunk (data + 4-byte CRC)
                stream.Position = dataStart + length + 4;
            }

        done:
            // Parse Stable Diffusion / ComfyUI from collected text chunks
            ParseSdFromPngChunks(result);
            ParseComfyFromPngChunks(result);
        }

        private static void ReadPngIhdr(Stream stream, int length, ImageMetadataResult result)
        {
            if (length < 13)
            {
                return;
            }

            var ihdr = new byte[13];
            if (stream.Read(ihdr, 0, 13) < 13)
            {
                return;
            }

            byte bitDepth = ihdr[8];
            byte colorType = ihdr[9];

            string colorName = colorType switch
            {
                PngColorGrayscale => "Grayscale",
                PngColorRgb => "RGB",
                PngColorIndexed => "Indexed",
                PngColorGrayscaleAlpha => "Grayscale+Alpha",
                PngColorRgba => "RGBA",
                _ => $"Type{colorType}"
            };

            result.ColorMode = $"{colorName} ({bitDepth}bit)";
        }

        private static void ReadPngText(Stream stream, int length, ImageMetadataResult result)
        {
            if (length <= 0 || length > 10 * 1024 * 1024)
            {
                return;
            }

            var data = new byte[length];
            if (stream.Read(data, 0, length) < length)
            {
                return;
            }

            int nullIndex = Array.IndexOf(data, (byte)0);
            if (nullIndex < 0)
            {
                return;
            }

            string keyword = Encoding.Latin1.GetString(data, 0, nullIndex);
            string value = Encoding.Latin1.GetString(data, nullIndex + 1, length - nullIndex - 1);
            result.PngTextChunks[keyword] = value;
        }

        private static void ReadPngItxt(Stream stream, int length, ImageMetadataResult result)
        {
            if (length <= 0 || length > 10 * 1024 * 1024)
            {
                return;
            }

            var data = new byte[length];
            if (stream.Read(data, 0, length) < length)
            {
                return;
            }

            int nullIndex = Array.IndexOf(data, (byte)0);
            if (nullIndex < 0 || nullIndex + 4 >= length)
            {
                return;
            }

            string keyword = Encoding.Latin1.GetString(data, 0, nullIndex);
            byte compressionFlag = data[nullIndex + 1];
            // byte compressionMethod = data[nullIndex + 2]; // always 0 (deflate)

            // Skip language tag (null-terminated) and translated keyword (null-terminated)
            int pos = nullIndex + 3;
            int langEnd = Array.IndexOf(data, (byte)0, pos);
            if (langEnd < 0 || langEnd + 1 >= length)
            {
                return;
            }

            int transEnd = Array.IndexOf(data, (byte)0, langEnd + 1);
            if (transEnd < 0 || transEnd + 1 > length)
            {
                return;
            }

            int textStart = transEnd + 1;
            int textLength = length - textStart;

            string value;
            if (compressionFlag == 1 && textLength > 0)
            {
                value = DeflateDecompress(data, textStart, textLength);
            }
            else
            {
                value = Encoding.UTF8.GetString(data, textStart, textLength);
            }

            result.PngTextChunks[keyword] = value;
        }

        private static void ReadPngZtxt(Stream stream, int length, ImageMetadataResult result)
        {
            if (length <= 0 || length > 10 * 1024 * 1024)
            {
                return;
            }

            var data = new byte[length];
            if (stream.Read(data, 0, length) < length)
            {
                return;
            }

            int nullIndex = Array.IndexOf(data, (byte)0);
            if (nullIndex < 0 || nullIndex + 2 >= length)
            {
                return;
            }

            string keyword = Encoding.Latin1.GetString(data, 0, nullIndex);
            // byte compressionMethod = data[nullIndex + 1]; // always 0
            int compressedStart = nullIndex + 2;
            int compressedLength = length - compressedStart;

            string value = DeflateDecompress(data, compressedStart, compressedLength);
            result.PngTextChunks[keyword] = value;
        }

        private static void ReadPngExifChunk(Stream stream, int length, ImageMetadataResult result)
        {
            if (length < 8 || length > 10 * 1024 * 1024)
            {
                return;
            }

            var data = new byte[length];
            if (stream.Read(data, 0, length) < length)
            {
                return;
            }

            ParseTiffExif(data, 0, length, result);
        }

        // ═══════════════════════════════════════════════════════════
        // JPEG
        // ═══════════════════════════════════════════════════════════

        private static void ReadJpegMetadata(Stream stream, ImageMetadataResult result)
        {
            stream.Position = 2; // skip SOI

            while (stream.Position < stream.Length)
            {
                int prefix;
                do
                {
                    prefix = stream.ReadByte();
                    if (prefix < 0)
                    {
                        return;
                    }
                }
                while (prefix != 0xFF);

                int marker;
                do
                {
                    marker = stream.ReadByte();
                    if (marker < 0)
                    {
                        return;
                    }
                }
                while (marker == 0xFF);

                if (marker == 0xD9 || marker == 0xDA)
                {
                    return;
                }

                if (IsStandaloneJpegMarker(marker))
                {
                    continue;
                }

                int hi = stream.ReadByte();
                int lo = stream.ReadByte();
                if (hi < 0 || lo < 0)
                {
                    return;
                }

                int segmentLength = (hi << 8) | lo;
                if (segmentLength < 2)
                {
                    return;
                }

                long segStart = stream.Position;
                int dataLength = segmentLength - 2;

                // APP1 (Exif or XMP)
                if (marker == 0xE1 && dataLength > 6)
                {
                    var segData = new byte[dataLength];
                    if (stream.Read(segData, 0, dataLength) == dataLength)
                    {
                        // "Exif\0\0"
                        if (segData[0] == 'E' && segData[1] == 'x' && segData[2] == 'i' &&
                            segData[3] == 'f' && segData[4] == 0 && segData[5] == 0)
                        {
                            ParseTiffExif(segData, 6, dataLength - 6, result);
                        }
                    }
                }

                // SOF markers – extract color mode
                if (IsJpegSofMarker(marker) && dataLength >= 6)
                {
                    var sofData = new byte[Math.Min(dataLength, 16)];
                    stream.Position = segStart;
                    if (stream.Read(sofData, 0, sofData.Length) == sofData.Length)
                    {
                        byte precision = sofData[0]; // bit depth per component
                        byte numComponents = sofData[5];

                        string colorName = numComponents switch
                        {
                            1 => "Grayscale",
                            3 => "YCbCr",
                            4 => "CMYK",
                            _ => $"{numComponents}ch"
                        };

                        result.ColorMode = $"{colorName} ({precision}bit)";
                    }
                }

                stream.Position = segStart + dataLength;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // WebP – minimal EXIF support via EXIF chunk
        // ═══════════════════════════════════════════════════════════

        private static void ReadWebPMetadata(Stream stream, ImageMetadataResult result)
        {
            stream.Position = 12;
            var buf = new byte[8];

            while (stream.Read(buf, 0, 8) == 8)
            {
                string fourCc = Encoding.ASCII.GetString(buf, 0, 4);
                int chunkSize = ReadLE32(buf, 4);
                if (chunkSize < 0 || chunkSize > 100 * 1024 * 1024)
                {
                    break;
                }

                long dataStart = stream.Position;

                if (fourCc == "VP8X" && chunkSize >= 10)
                {
                    var vp8x = new byte[10];
                    if (stream.Read(vp8x, 0, 10) == 10)
                    {
                        bool hasAlpha = (vp8x[0] & 0x10) != 0;
                        result.ColorMode = hasAlpha ? "RGBA (8bit)" : "RGB (8bit)";
                    }
                }
                else if (fourCc == "EXIF" && chunkSize > 6)
                {
                    var exifData = new byte[chunkSize];
                    if (stream.Read(exifData, 0, chunkSize) == chunkSize)
                    {
                        // Some WebP EXIF chunks start with "Exif\0\0"
                        if (chunkSize > 6 &&
                            exifData[0] == 'E' && exifData[1] == 'x' && exifData[2] == 'i' &&
                            exifData[3] == 'f' && exifData[4] == 0 && exifData[5] == 0)
                        {
                            ParseTiffExif(exifData, 6, chunkSize - 6, result);
                        }
                        else
                        {
                            ParseTiffExif(exifData, 0, chunkSize, result);
                        }
                    }
                }
                else if (fourCc == "VP8L" && chunkSize >= 5)
                {
                    // VP8L (lossless) always RGBA
                    result.ColorMode ??= "RGBA (8bit)";
                }
                else if (fourCc == "VP8 " && result.ColorMode == null)
                {
                    result.ColorMode = "YCbCr (8bit)";
                }

                stream.Position = dataStart + chunkSize + (chunkSize % 2);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // TIFF / EXIF parser
        // ═══════════════════════════════════════════════════════════

        private static void ParseTiffExif(byte[] data, int offset, int length, ImageMetadataResult result)
        {
            if (length < 8)
            {
                return;
            }

            bool le;
            if (data[offset] == 0x49 && data[offset + 1] == 0x49)
            {
                le = true;
            }
            else if (data[offset] == 0x4D && data[offset + 1] == 0x4D)
            {
                le = false;
            }
            else
            {
                return;
            }

            int ifdOffset = le ? ReadLE32(data, offset + 4) : ReadBE32(data, offset + 4);
            ParseIfd(data, offset, length, offset + ifdOffset, le, result, depth: 0);
        }

        private static void ParseIfd(byte[] data, int tiffBase, int tiffLength, int ifdPos, bool le,
            ImageMetadataResult result, int depth)
        {
            if (depth > 4 || ifdPos < tiffBase || ifdPos + 2 > tiffBase + tiffLength)
            {
                return;
            }

            int tagCount = le ? ReadLE16(data, ifdPos) : ReadBE16(data, ifdPos);

            for (int i = 0; i < tagCount; i++)
            {
                int tagOff = ifdPos + 2 + (i * 12);
                if (tagOff + 12 > tiffBase + tiffLength)
                {
                    break;
                }

                ushort tagId = (ushort)(le ? ReadLE16(data, tagOff) : ReadBE16(data, tagOff));
                ushort type = (ushort)(le ? ReadLE16(data, tagOff + 2) : ReadBE16(data, tagOff + 2));
                int count = le ? ReadLE32(data, tagOff + 4) : ReadBE32(data, tagOff + 4);

                // ExifIFDPointer → recurse
                if (tagId == 0x8769 || tagId == 0x8825)
                {
                    int subIfd = le ? ReadLE32(data, tagOff + 8) : ReadBE32(data, tagOff + 8);
                    ParseIfd(data, tiffBase, tiffLength, tiffBase + subIfd, le, result, depth + 1);
                    continue;
                }

                if (!ExifTagNames.TryGetValue(tagId, out string? name))
                {
                    continue;
                }

                string? value = ReadExifValue(data, tiffBase, tiffLength, tagOff + 8, type, count, le);
                if (!string.IsNullOrEmpty(value))
                {
                    result.ExifTags[name] = value;
                }
            }
        }

        private static string? ReadExifValue(byte[] data, int tiffBase, int tiffLength,
            int valueOffset, ushort type, int count, bool le)
        {
            int elementSize = type switch
            {
                1 => 1, // BYTE
                2 => 1, // ASCII
                3 => 2, // SHORT
                4 => 4, // LONG
                5 => 8, // RATIONAL
                7 => 1, // UNDEFINED
                10 => 8, // SRATIONAL
                _ => 0
            };

            if (elementSize == 0)
            {
                return null;
            }

            int totalBytes = count * elementSize;
            int dataOffset;
            if (totalBytes <= 4)
            {
                dataOffset = valueOffset;
            }
            else
            {
                int pointer = le ? ReadLE32(data, valueOffset) : ReadBE32(data, valueOffset);
                dataOffset = tiffBase + pointer;
            }

            if (dataOffset < 0 || dataOffset + totalBytes > data.Length || dataOffset + totalBytes > tiffBase + tiffLength)
            {
                return null;
            }

            switch (type)
            {
                case 2: // ASCII
                    int asciiLen = count;
                    if (asciiLen > 0 && data[dataOffset + asciiLen - 1] == 0)
                    {
                        asciiLen--;
                    }

                    return asciiLen > 0
                        ? Encoding.ASCII.GetString(data, dataOffset, asciiLen).Trim()
                        : null;

                case 3: // SHORT
                    if (count == 1)
                    {
                        return (le ? ReadLE16(data, dataOffset) : ReadBE16(data, dataOffset)).ToString();
                    }

                    break;

                case 4: // LONG
                    if (count == 1)
                    {
                        uint val = (uint)(le ? ReadLE32(data, dataOffset) : ReadBE32(data, dataOffset));
                        return val.ToString();
                    }

                    break;

                case 5: // RATIONAL
                    if (count == 1)
                    {
                        uint num = (uint)(le ? ReadLE32(data, dataOffset) : ReadBE32(data, dataOffset));
                        uint den = (uint)(le ? ReadLE32(data, dataOffset + 4) : ReadBE32(data, dataOffset + 4));
                        if (den == 0)
                        {
                            return "0";
                        }

                        if (num % den == 0)
                        {
                            return (num / den).ToString();
                        }

                        return $"{num}/{den}";
                    }

                    break;

                case 10: // SRATIONAL
                    if (count == 1)
                    {
                        int num = le ? ReadLE32(data, dataOffset) : ReadBE32(data, dataOffset);
                        int den = le ? ReadLE32(data, dataOffset + 4) : ReadBE32(data, dataOffset + 4);
                        if (den == 0)
                        {
                            return "0";
                        }

                        if (num % den == 0)
                        {
                            return (num / den).ToString();
                        }

                        return $"{num}/{den}";
                    }

                    break;

                case 7: // UNDEFINED
                    if (count <= 16)
                    {
                        return Encoding.ASCII.GetString(data, dataOffset, count).TrimEnd('\0');
                    }

                    break;
            }

            return null;
        }

        // ═══════════════════════════════════════════════════════════
        // Stable Diffusion parameter parsing (A1111 / Forge)
        // ═══════════════════════════════════════════════════════════

        private static void ParseSdFromPngChunks(ImageMetadataResult result)
        {
            // A1111 format: "parameters" key contains the full prompt + settings
            if (result.PngTextChunks.TryGetValue("parameters", out string? parameters) &&
                !string.IsNullOrWhiteSpace(parameters))
            {
                ParseA1111Parameters(parameters, result);
                return;
            }

            // Some tools use separate keys
            if (result.PngTextChunks.TryGetValue("prompt", out string? prompt))
            {
                // Check if it's JSON (ComfyUI) or plain text (SD)
                string trimmed = prompt.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    return; // ComfyUI JSON – handled separately
                }

                result.SdPrompt = prompt;
            }

            if (result.PngTextChunks.TryGetValue("negative_prompt", out string? neg))
            {
                result.SdNegativePrompt = neg;
            }

            // Additional SD metadata keys
            string[] sdKeys = { "steps", "sampler", "cfg_scale", "seed", "model", "model_hash",
                                "clip_skip", "size", "denoising_strength", "hires_upscaler",
                                "hires_steps", "hires_upscale", "vae", "scheduler" };
            foreach (string key in sdKeys)
            {
                if (result.PngTextChunks.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val))
                {
                    result.SdParameters[key] = val;
                }
            }
        }

        private static void ParseA1111Parameters(string raw, ImageMetadataResult result)
        {
            // A1111 format:
            // <prompt>
            // Negative prompt: <negative prompt>
            // Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 12345, Size: 512x768, Model: ...

            int negIndex = raw.IndexOf("\nNegative prompt:", StringComparison.OrdinalIgnoreCase);
            int stepsIndex = raw.LastIndexOf("\nSteps:", StringComparison.OrdinalIgnoreCase);

            if (stepsIndex < 0)
            {
                // Try without newline prefix (single line)
                stepsIndex = raw.LastIndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
                if (stepsIndex > 0)
                {
                    // Only accept if preceded by newline
                    int prevNewline = raw.LastIndexOf('\n', stepsIndex - 1);
                    if (prevNewline < 0)
                    {
                        stepsIndex = -1;
                    }
                    else
                    {
                        stepsIndex = prevNewline;
                    }
                }
            }

            if (negIndex >= 0)
            {
                result.SdPrompt = raw[..negIndex].Trim();
                int negStart = negIndex + "\nNegative prompt:".Length;
                int negEnd = stepsIndex >= 0 ? stepsIndex : raw.Length;
                result.SdNegativePrompt = raw[negStart..negEnd].Trim();
            }
            else if (stepsIndex >= 0)
            {
                result.SdPrompt = raw[..stepsIndex].Trim();
            }
            else
            {
                result.SdPrompt = raw.Trim();
                return;
            }

            if (stepsIndex >= 0)
            {
                string paramsLine = raw[(stepsIndex + 1)..].Trim();
                ParseSdKeyValuePairs(paramsLine, result.SdParameters);
            }
        }

        private static void ParseSdKeyValuePairs(string line, Dictionary<string, string> target)
        {
            // "Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 12345"
            var parts = line.Split(',');
            foreach (string part in parts)
            {
                int colonIndex = part.IndexOf(':');
                if (colonIndex > 0)
                {
                    string key = part[..colonIndex].Trim();
                    string value = part[(colonIndex + 1)..].Trim();
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        target[key] = value;
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ComfyUI parameter parsing
        // ═══════════════════════════════════════════════════════════

        private static void ParseComfyFromPngChunks(ImageMetadataResult result)
        {
            // ComfyUI stores "prompt" as a JSON object and optionally "workflow"
            if (result.PngTextChunks.TryGetValue("prompt", out string? promptJson))
            {
                string trimmed = promptJson.TrimStart();
                if (trimmed.StartsWith('{'))
                {
                    ParseComfyPromptJson(trimmed, result);
                }
            }

            if (result.PngTextChunks.TryGetValue("workflow", out string? workflowJson))
            {
                string trimmed = workflowJson.TrimStart();
                if (trimmed.StartsWith('{'))
                {
                    result.ComfyWorkflowJson = trimmed;
                    ParseComfyWorkflowTitle(trimmed, result);
                }
            }
        }

        private static void ParseComfyPromptJson(string json, ImageMetadataResult result)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var node in root.EnumerateObject())
                {
                    if (node.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!node.Value.TryGetProperty("class_type", out var classType))
                    {
                        continue;
                    }

                    string cls = classType.GetString() ?? string.Empty;

                    if (cls.Contains("KSampler", StringComparison.OrdinalIgnoreCase) &&
                        node.Value.TryGetProperty("inputs", out var ksInputs))
                    {
                        TryExtract(ksInputs, "seed", "Seed", result.ComfyParameters);
                        TryExtract(ksInputs, "steps", "Steps", result.ComfyParameters);
                        TryExtract(ksInputs, "cfg", "CFG", result.ComfyParameters);
                        TryExtract(ksInputs, "sampler_name", "Sampler", result.ComfyParameters);
                        TryExtract(ksInputs, "scheduler", "Scheduler", result.ComfyParameters);
                        TryExtract(ksInputs, "denoise", "Denoise", result.ComfyParameters);
                    }
                    else if (cls.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase) &&
                             node.Value.TryGetProperty("inputs", out var ckptInputs))
                    {
                        TryExtract(ckptInputs, "ckpt_name", "Model", result.ComfyParameters);
                    }
                    else if ((cls.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase) ||
                              cls.Contains("ConditioningCombine", StringComparison.OrdinalIgnoreCase)) &&
                             node.Value.TryGetProperty("inputs", out var clipInputs))
                    {
                        if (clipInputs.TryGetProperty("text", out var textProp) &&
                            textProp.ValueKind == JsonValueKind.String)
                        {
                            string text = textProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                // Try to determine if positive or negative
                                string nodeTitle = node.Name;
                                string label = result.ComfyParameters.ContainsKey("Positive Prompt")
                                    ? $"Prompt (Node {nodeTitle})"
                                    : "Positive Prompt";

                                // Heuristic: if the node name or class contains "negative"
                                if (cls.Contains("Negative", StringComparison.OrdinalIgnoreCase) ||
                                    nodeTitle.Contains("neg", StringComparison.OrdinalIgnoreCase))
                                {
                                    label = "Negative Prompt";
                                }

                                if (!result.ComfyParameters.ContainsKey(label))
                                {
                                    result.ComfyParameters[label] = text.Length > 300
                                        ? text[..300] + "..."
                                        : text;
                                }
                            }
                        }
                    }
                    else if ((cls == "EmptyLatentImage" || cls.Contains("LatentImage", StringComparison.OrdinalIgnoreCase)) &&
                             node.Value.TryGetProperty("inputs", out var latentInputs))
                    {
                        TryExtract(latentInputs, "width", "Width", result.ComfyParameters);
                        TryExtract(latentInputs, "height", "Height", result.ComfyParameters);
                        TryExtract(latentInputs, "batch_size", "Batch Size", result.ComfyParameters);
                    }
                    else if ((cls.Contains("VAELoader", StringComparison.OrdinalIgnoreCase) ||
                              cls.Contains("VAEDecode", StringComparison.OrdinalIgnoreCase)) &&
                             node.Value.TryGetProperty("inputs", out var vaeInputs))
                    {
                        TryExtract(vaeInputs, "vae_name", "VAE", result.ComfyParameters);
                    }
                    else if (cls.Contains("LoraLoader", StringComparison.OrdinalIgnoreCase) &&
                             node.Value.TryGetProperty("inputs", out var loraInputs))
                    {
                        TryExtract(loraInputs, "lora_name", "LoRA", result.ComfyParameters);
                        TryExtract(loraInputs, "strength_model", "LoRA Strength (Model)", result.ComfyParameters);
                        TryExtract(loraInputs, "strength_clip", "LoRA Strength (CLIP)", result.ComfyParameters);
                    }
                }
            }
            catch
            {
                // Ignore JSON parse errors
            }
        }

        private static void ParseComfyWorkflowTitle(string workflowJson, ImageMetadataResult result)
        {
            try
            {
                using var doc = JsonDocument.Parse(workflowJson);
                if (doc.RootElement.TryGetProperty("extra", out var extra) &&
                    extra.TryGetProperty("workspace_info", out var wsInfo) &&
                    wsInfo.TryGetProperty("name", out var wsName))
                {
                    string name = wsName.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.ComfyParameters["Workflow"] = name;
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryExtract(JsonElement parent, string prop, string label,
            Dictionary<string, string> target)
        {
            if (!parent.TryGetProperty(prop, out var val))
            {
                return;
            }

            string? text = val.ValueKind switch
            {
                JsonValueKind.String => val.GetString(),
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text) && !target.ContainsKey(label))
            {
                target[label] = text;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════

        private static string DeflateDecompress(byte[] data, int offset, int length)
        {
            try
            {
                using var compressed = new MemoryStream(data, offset, length);
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                using var reader = new StreamReader(deflate, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                // Try with zlib header (skip 2-byte zlib header)
                if (length > 2)
                {
                    try
                    {
                        using var compressed = new MemoryStream(data, offset + 2, length - 2);
                        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                        using var reader = new StreamReader(deflate, Encoding.UTF8);
                        return reader.ReadToEnd();
                    }
                    catch
                    {
                    }
                }

                return string.Empty;
            }
        }

        private static bool IsStandaloneJpegMarker(int marker)
        {
            return marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7);
        }

        private static bool IsJpegSofMarker(int marker)
        {
            return marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
        }

        private static int ReadBE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static int ReadBE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static int ReadLE16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static int ReadLE32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
