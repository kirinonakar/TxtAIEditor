using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using static TxtAIEditor.Core.Services.OfficeBinaryData;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeArtBinary
    {
        internal static Record ReadRecord(byte[] bytes, int offset, int limit)
        {
            ReadOnlySpan<byte> header = Slice(bytes.AsSpan(0, limit), offset, 8);
            int length = I32(header, 4);
            if (length < 0 || length > limit - offset - 8)
            {
                throw new InvalidDataException("Invalid Office drawing record length.");
            }

            ushort options = U16(header, 0);
            return new Record(U16(header, 2), options & 15, options >> 4, offset + 8, length);
        }

        internal static IEnumerable<Record> Records(byte[] bytes, int start, int end)
        {
            for (int offset = start; offset < end;)
            {
                Record record = ReadRecord(bytes, offset, end);
                yield return record;
                offset = record.End;
            }
        }

        internal static Image? ReadBlip(byte[] bytes, Record record)
        {
            ReadOnlySpan<byte> data = record.Body(bytes);
            int uidLength = (record.Instance & 1) == 0 ? 16 : 32;
            if (record.Type is 0xF01A or 0xF01B)
            {
                return ReadMetafile(data, uidLength, record.Type == 0xF01A ? "emf" : "wmf");
            }

            string? extension = record.Type switch
            {
                0xF01D or 0xF02A => "jpg",
                0xF01E => "png",
                0xF01F => "dib",
                0xF029 => "tiff",
                _ => null
            };
            if (extension != null && data.Length > uidLength + 1 && data.Length <= 15_000_000 + uidLength + 1)
            {
                ReadOnlySpan<byte> image = data[(uidLength + 1)..];
                return extension == "dib" ? ReadBitmap(image) : new Image(extension, image.ToArray());
            }

            return null;
        }

        internal static Image? ReadBse(byte[] data, Record bse, byte[]? pictures)
        {
            ReadOnlySpan<byte> body = bse.Body(data);
            if (body.Length < 36) return null;
            int inline = 36 + body[33];
            if (inline + 8 <= body.Length)
            {
                return ReadBlip(data, ReadRecord(data, bse.Start + inline, bse.End));
            }

            int offset = I32(body, 28);
            if (pictures == null || offset < 0 || offset > pictures.Length - 8) return null;
            int type = U16(pictures, offset + 2);
            return type is >= 0xF01A and <= 0xF01F or 0xF029 or 0xF02A
                ? ReadBlip(pictures, ReadRecord(pictures, offset, pictures.Length)) : null;
        }

        internal static Dictionary<int, uint> ReadShapeOptions(byte[] bytes, IEnumerable<Record> parts)
        {
            var properties = new Dictionary<int, uint>();
            foreach (Record part in parts)
            {
                if (part.Type != 0xF00B) continue;
                ReadOnlySpan<byte> data = part.Body(bytes);
                for (int i = 0; i < part.Instance; i++)
                {
                    int offset = i * 6;
                    int op = U16(data, offset);
                    if ((op & 0x8000) == 0) properties[op & 0x3FFF] = U32(data, offset + 2);
                }
            }
            return properties;
        }

        internal static IEnumerable<Record> ShapeContainers(byte[] bytes, Record drawing, int depth = 0)
        {
            if (depth > 64) throw new InvalidDataException("Office drawings are nested too deeply.");
            foreach (Record record in drawing.Children(bytes))
            {
                if (record.Type == 0xF004) yield return record;
                else if (record.Type == 0xF003)
                {
                    foreach (Record child in ShapeContainers(bytes, record, depth + 1)) yield return child;
                }
            }
        }

        private static Image? ReadMetafile(ReadOnlySpan<byte> data, int headerOffset, string extension)
        {
            if (data.Length < headerOffset + 34) return null;
            ReadOnlySpan<byte> header = data.Slice(headerOffset, 34);
            uint length = U32(header, 0);
            uint storedLength = U32(header, 28);
            data = data[(headerOffset + 34)..];
            if (length == 0 || length > 15_000_000 || storedLength > data.Length) return null;
            if (header[32] == 0xFE && length == storedLength) return new Image(extension, data[..(int)length].ToArray());
            if (header[32] != 0) return null;

            using var input = new MemoryStream(data[..(int)storedLength].ToArray(), writable: false);
            try
            {
                using var deflate = new ZLibStream(input, CompressionMode.Decompress);
                var output = new byte[length];
                deflate.ReadExactly(output);
                return deflate.ReadByte() == -1 ? new Image(extension, output) : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return null;
            }
        }

        private static Image? ReadBitmap(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12) return null;
            uint headerSize = U32(data, 0);
            if (headerSize != 12 && (headerSize < 40 || headerSize > data.Length)) return null;
            int bitCount = U16(data, headerSize == 12 ? 10 : 14);
            uint colors = headerSize >= 40 ? U32(data, 32) : 0;
            if (colors == 0 && bitCount is > 0 and <= 8) colors = 1u << bitCount;
            uint masks = headerSize == 40 ? U32(data, 16) switch { 3 => 12u, 6 => 16u, _ => 0u } : 0;
            long pixels = headerSize + masks + colors * (headerSize == 12 ? 3L : 4L);
            if (pixels >= data.Length) return null;
            byte[] bitmap = new byte[14 + data.Length];
            bitmap[0] = (byte)'B';
            bitmap[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), bitmap.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), (int)pixels + 14);
            data.CopyTo(bitmap.AsSpan(14));
            return new Image("bmp", bitmap);
        }

        internal readonly record struct Record(int Type, int Version, int Instance, int Start, int Length)
        {
            internal int End => Start + Length;
            internal ReadOnlySpan<byte> Body(byte[] bytes) => Slice(bytes, Start, Length);
            internal IEnumerable<Record> Children(byte[] bytes) => Records(bytes, Start, End);
        }

        internal sealed record Image(string Extension, byte[] Bytes)
        {
            internal void Write(ZipArchive archive, string path)
            {
                using Stream output = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
                output.Write(Bytes);
            }
        }
    }
}
