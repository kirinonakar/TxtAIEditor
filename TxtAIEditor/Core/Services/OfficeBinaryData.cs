using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeBinaryData
    {
        internal static ushort U16(ReadOnlySpan<byte> data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));

        internal static short I16(ReadOnlySpan<byte> data, int offset) => unchecked((short)U16(data, offset));

        internal static uint U32(ReadOnlySpan<byte> data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));

        internal static int I32(ReadOnlySpan<byte> data, int offset) => unchecked((int)U32(data, offset));

        internal static ulong U64(ReadOnlySpan<byte> data, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(Slice(data, offset, 8));

        internal static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length)
            {
                throw new InvalidDataException("Office binary data contains an invalid offset or length.");
            }

            return data.Slice(offset, length);
        }

        internal static string XmlText(string value)
        {
            StringBuilder? result = null;
            int start = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }

                bool surrogate = char.IsSurrogate(character);
                if (!surrogate && (character is '\t' or '\n' or '\r' || character is >= ' ' and < '\uFFFE')) continue;
                result ??= new StringBuilder(value.Length);
                result.Append(value, start, index - start);
                if (surrogate) result.Append('\uFFFD');
                start = index + 1;
            }

            return result == null ? value : result.Append(value, start, value.Length - start).ToString();
        }
    }
}
