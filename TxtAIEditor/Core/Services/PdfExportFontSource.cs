using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TxtAIEditor.Core.Services
{
    // Reads installed font programs without changing the user's font installation.
    // GDI can return a whole TTC; reconstruct just the selected face in that case.
    internal static class PdfExportFontSource
    {
        private const uint GdiError = uint.MaxValue;
        private const int MaximumFontBytes = 192 * 1024 * 1024;

        public static async Task<Dictionary<int, HashSet<string>>> GetUsedFontFamiliesAsync(Func<string, string, Task<string>> execute)
        {
            await execute("DOM.enable", "{}");
            await execute("CSS.enable", "{}");
            using var document = JsonDocument.Parse(await execute("DOM.getFlattenedDocument", "{\"depth\":-1}"));
            var nodes = new List<(int Node, int Group)>();
            foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (!node.TryGetProperty("attributes", out var attributes)) continue;
                for (int i = 0; i + 1 < attributes.GetArrayLength(); i += 2)
                    if (attributes[i].GetString() == "data-txtaieditor-pdf-font" && int.TryParse(attributes[i + 1].GetString(), out int group))
                        nodes.Add((node.GetProperty("nodeId").GetInt32(), group));
            }
            var result = new Dictionary<int, HashSet<string>>();
            foreach (var (node, group) in nodes)
            {
                using var fonts = JsonDocument.Parse(await execute("CSS.getPlatformFontsForNode", JsonSerializer.Serialize(new { nodeId = node })));
                foreach (var font in fonts.RootElement.GetProperty("fonts").EnumerateArray())
                {
                    if (font.GetProperty("glyphCount").GetInt32() == 0 || font.GetProperty("isCustomFont").GetBoolean()) continue;
                    if (!result.TryGetValue(group, out var families)) result[group] = families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    families.Add(font.GetProperty("familyName").GetString()!);
                }
            }
            return result;
        }

        public static byte[]? Read(string family, int weight, bool italic)
        {
            if (string.IsNullOrWhiteSpace(family) || family.Length > 128) return null;
            // Prefer an explicitly registered variable face over a legacy CFF
            // collection of the same family, as DirectWrite does in WebView2.
            byte[]? variable = ReadRegisteredVariableFont(family);
            if (variable != null) return variable;

            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr font = CreateFontW(-32, 0, 0, 0, weight, italic ? 1u : 0u,
                0, 0, 1, 0, 0, 0, 0, family);
            if (dc == IntPtr.Zero || font == IntPtr.Zero)
            {
                if (font != IntPtr.Zero) DeleteObject(font);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                return null;
            }
            IntPtr previous = SelectObject(dc, font);
            try
            {
                var actualFamily = new StringBuilder(256);
                GetTextFaceW(dc, actualFamily.Capacity, actualFamily);
                if (!actualFamily.ToString().Equals(family, StringComparison.OrdinalIgnoreCase)) return null;
                uint size = GetFontData(dc, 0, 0, null, 0);
                if (size == GdiError || size < 12 || size > MaximumFontBytes) return null;
                byte[] header = ReadData(dc, 0, 0, 12);
                if (Encoding.ASCII.GetString(header, 0, 4) != "ttcf")
                {
                    int tableCount = U16(header, 4);
                    if (tableCount < 1 || tableCount > 256) return null;
                    return ExtractFace(dc, header, ReadData(dc, 0, 12, tableCount * 16));
                }

                int count = checked((int)U32(header, 8));
                if (count < 1 || count > 256) return null;
                byte[] offsets = ReadData(dc, 0, 12, count * 4);
                uint nameTag = BinaryPrimitives.ReverseEndianness(0x6e616d65u);
                uint nameSize = GetFontData(dc, nameTag, 0, null, 0);
                if (nameSize == GdiError || nameSize > 1024 * 1024) return null;
                byte[] selectedName = ReadData(dc, nameTag, 0, checked((int)nameSize));
                for (int face = 0; face < count; face++)
                {
                    uint offset = U32(offsets, face * 4);
                    byte[] faceHeader = ReadData(dc, 0, offset, 12);
                    int tableCount = U16(faceHeader, 4);
                    if (tableCount < 1 || tableCount > 256) continue;
                    byte[] directory = ReadData(dc, 0, offset + 12, tableCount * 16);
                    for (int i = 0; i < tableCount; i++)
                    {
                        int entry = i * 16;
                        if (Encoding.ASCII.GetString(directory, entry, 4) != "name") continue;
                        uint length = U32(directory, entry + 12);
                        if (length != selectedName.Length) break;
                        byte[] name = ReadData(dc, 0, U32(directory, entry + 8), checked((int)length));
                        if (name.AsSpan().SequenceEqual(selectedName)) return ExtractFace(dc, faceHeader, directory);
                    }
                }
                return null;
            }
            finally
            {
                SelectObject(dc, previous);
                DeleteObject(font);
                DeleteDC(dc);
            }
        }

        private static byte[]? ReadRegisteredVariableFont(string family)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key?.GetValue(family + " (TrueType)") is not string value) continue;
                string path = Path.IsPathRooted(value) ? value : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), value);
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length < 12 || info.Length > MaximumFontBytes) continue;
                byte[] data = File.ReadAllBytes(path);
                if (Encoding.ASCII.GetString(data, 0, 4) == "ttcf") continue;
                int count = U16(data, 4);
                if (12L + count * 16L > data.Length) continue;
                for (int i = 0; i < count; i++)
                    if (Encoding.ASCII.GetString(data, 12 + i * 16, 4) == "fvar") return data;
            }
            return null;
        }

        private static byte[] ExtractFace(IntPtr dc, byte[] header, byte[] directory)
        {
            using var result = new MemoryStream();
            result.Write(header);
            result.Write(directory);
            int headOffset = 0;
            for (int i = 0; i < directory.Length; i += 16)
            {
                uint length = U32(directory, i + 12);
                if (length > MaximumFontBytes || result.Length + length > MaximumFontBytes) throw new InvalidDataException("Font face is too large.");
                // GDI's TTC stream offsets are not uniformly file-relative for
                // shared tables. Read each selected-face table by tag instead.
                uint tableTag = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i, 4));
                byte[] table = ReadData(dc, tableTag, 0, checked((int)length));
                if (Encoding.ASCII.GetString(directory, i, 4) == "GSUB")
                {
                    NormalizeLegacyGsubVersion(table);
                }
                int tableOffset = checked((int)result.Position);
                if (Encoding.ASCII.GetString(directory, i, 4) == "head")
                {
                    headOffset = tableOffset;
                    table.AsSpan(8, 4).Clear();
                }
                BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(i + 8), (uint)tableOffset);
                uint tableSum = 0;
                for (int at = 0; at < table.Length; at += 4)
                {
                    uint word = 0;
                    for (int j = 0; j < 4; j++) word = (word << 8) | (at + j < table.Length ? table[at + j] : 0u);
                    tableSum = unchecked(tableSum + word);
                }
                BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(i + 4), tableSum);
                result.Write(table);
                while (result.Position % 4 != 0) result.WriteByte(0);
            }
            result.Position = 12;
            result.Write(directory);
            byte[] bytes = result.ToArray();
            if (headOffset > 0)
            {
                uint sum = 0;
                for (int i = 0; i < bytes.Length; i += 4) sum = unchecked(sum + U32(bytes, i));
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(headOffset + 8), unchecked(0xb1b0afbAu - sum));
            }
            return bytes;
        }

        private static void NormalizeLegacyGsubVersion(byte[] table)
        {
            // Older Windows CJK fonts (including Gulim/GulimChe) use 0x00001000
            // for an otherwise v1.0 GSUB table. HarfBuzz rejects that version.
            // Repair only this known legacy spelling and only a v1.0-shaped
            // header; retain all substitution rules, glyphs and font names.
            if (table.Length < 10 || U32(table, 0) != 0x00001000) return;
            for (int at = 4; at <= 8; at += 2)
            {
                int offset = U16(table, at);
                if (offset < 10 || offset > table.Length - 2) return;
                int count = U16(table, offset);
                int recordSize = at == 8 ? 2 : 6;
                if (offset + 2L + count * (long)recordSize > table.Length) return;
            }
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0, 4), 0x00010000);
        }

        private static byte[] ReadData(IntPtr dc, uint table, uint offset, int count)
        {
            var bytes = new byte[count];
            if (GetFontData(dc, table, offset, bytes, (uint)count) != count) throw new InvalidDataException("Could not read the installed font program.");
            return bytes;
        }

        private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetFontData(IntPtr dc, uint table, uint offset, [Out] byte[]? data, uint length);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder face);
    }
}
