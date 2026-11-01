using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace TxtAIEditor.Core.Services
{
    // Preserve WIC's PNG filters, pixels and metadata; change only the IDAT deflate compression.
    internal static class PngCompression
    {
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static void Recompress(Stream input, Stream output, int strength)
        {
            if (strength < 1 || strength > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(strength));
            }
            Span<byte> signature = stackalloc byte[8];
            input.ReadExactly(signature);
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                throw new InvalidDataException("Invalid PNG signature.");
            }
            output.Write(signature);

            using var compressed = new MemoryStream();
            Span<byte> header = stackalloc byte[8];
            Span<byte> crc = stackalloc byte[4];
            bool wroteImageData = false;
            while (true)
            {
                input.ReadExactly(header);
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
                byte[] payload = new byte[length];
                input.ReadExactly(payload);
                input.ReadExactly(crc);
                ReadOnlySpan<byte> type = header[4..];
                if (type.SequenceEqual("IDAT"u8))
                {
                    if (wroteImageData)
                    {
                        throw new InvalidDataException("Nonconsecutive PNG image data.");
                    }
                    compressed.Write(payload);
                    continue;
                }

                if (!wroteImageData && compressed.Length > 0)
                {
                    compressed.Position = 0;
                    using (var source = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
                    using (var destination = new ZLibStream(new IdatStream(output),
                        new ZLibCompressionOptions { CompressionLevel = 1 + (strength - 1) * 8 / 99 }, leaveOpen: true))
                    {
                        source.CopyTo(destination);
                    }
                    wroteImageData = true;
                }

                output.Write(header);
                output.Write(payload);
                output.Write(crc);
                if (type.SequenceEqual("IEND"u8))
                {
                    if (!wroteImageData)
                    {
                        throw new InvalidDataException("Missing PNG image data.");
                    }
                    return;
                }
            }
        }

        private static void WriteIdat(Stream output, ReadOnlySpan<byte> payload)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            "IDAT"u8.CopyTo(header[4..]);
            output.Write(header);
            output.Write(payload);
            uint crc = uint.MaxValue;
            foreach (byte value in header[4..]) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
            foreach (byte value in payload) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
            BinaryPrimitives.WriteUInt32BigEndian(header, ~crc);
            output.Write(header[..4]);
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
                table[index] = value;
            }
            return table;
        }

        private sealed class IdatStream(Stream output) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => output.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                while (!buffer.IsEmpty)
                {
                    int count = Math.Min(buffer.Length, 65536);
                    WriteIdat(output, buffer[..count]);
                    buffer = buffer[count..];
                }
            }
        }
    }
}
