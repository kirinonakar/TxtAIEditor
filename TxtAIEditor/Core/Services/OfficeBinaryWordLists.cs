using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeBinaryData;

namespace TxtAIEditor.Core.Services
{
    internal sealed partial class OfficeBinaryWord
    {
        // Marker text is resolved while reading the binary story and carried in the preview package.
        internal static readonly XNamespace Preview = "urn:txtaieditor:word-preview";
        private readonly Dictionary<int, WordList> _lists = new();

        private void ReadLists(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> levels, ReadOnlySpan<byte> overrides)
        {
            const int listHeaderSize = 28; // LSTF
            const int overrideHeaderSize = 16; // LFO
            if (headers.Length < 2 || overrides.Length < 4) return;
            int count = U16(headers, 0);
            int overrideCount = I32(overrides, 0);
            if (count > (headers.Length - 2) / listHeaderSize ||
                overrideCount < 0 || overrideCount > (overrides.Length - 4) / overrideHeaderSize) return;

            var definitions = new Dictionary<int, WordListLevel[]>();
            for (int i = 0; i < count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> entry = headers.Slice(2 + i * listHeaderSize, listHeaderSize);
                var list = new WordListLevel[(entry[26] & 1) == 1 ? 1 : 9];
                for (int level = 0; level < list.Length; level++)
                {
                    if (ReadListLevel(ref levels, level) is not WordListLevel data) return;
                    list[level] = data;
                }
                definitions[I32(entry, 0)] = list;
            }

            ReadOnlySpan<byte> dataRecords = overrides[(4 + overrideCount * overrideHeaderSize)..];
            for (int i = 0; i < overrideCount; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> entry = overrides.Slice(4 + i * overrideHeaderSize, overrideHeaderSize);
                if (dataRecords.Length < 4) return;
                dataRecords = dataRecords[4..]; // LFOData.cp
                WordListLevel[]? list = definitions.TryGetValue(I32(entry, 0), out WordListLevel[]? definition)
                    ? (WordListLevel[])definition.Clone()
                    : null;
                for (int j = 0; j < entry[12]; j++)
                {
                    if (dataRecords.Length < 8) return;
                    byte flags = dataRecords[4];
                    int level = flags & 0x0F;
                    int start = I32(dataRecords, 0);
                    dataRecords = dataRecords[8..];
                    WordListLevel? replacement = null;
                    if ((flags & 0x20) != 0)
                    {
                        replacement = ReadListLevel(ref dataRecords, level);
                        if (replacement == null) return;
                    }
                    if (list == null || level >= list.Length) continue;
                    list[level] = replacement ?? (((flags & 0x10) != 0 && start is >= 0 and <= 0x7FFF)
                        ? list[level] with { Start = start }
                        : list[level]);
                }
                if (list != null) _lists[i + 1] = new WordList(list);
            }
        }

        private WordListLevel? ReadListLevel(ref ReadOnlySpan<byte> data, int index)
        {
            const int headerSize = 28; // LVLF
            if (data.Length < headerSize + 2) return null;
            int characterLength = data[24];
            int paragraphLength = data[25];
            int textOffset = headerSize + paragraphLength + characterLength;
            if (textOffset > data.Length - 2) return null;
            int textLength = 2 * U16(data, textOffset);
            if (textLength > data.Length - textOffset - 2) return null;
            var paragraph = ReadProperties(data.Slice(headerSize, paragraphLength), false, out _);
            var characters = ReadProperties(data.Slice(headerSize + paragraphLength, characterLength), false, out _);
            var level = new WordListLevel(Math.Clamp(I32(data, 0), 0, 0x7FFF), data[4], data[5] & 3, data[15],
                (data[5] & 8) != 0 ? Math.Min(index, data[26]) : index, (data[5] & 4) != 0,
                Encoding.Unicode.GetString(data.Slice(textOffset + 2, textLength)), paragraph, characters);
            data = data[(textOffset + 2 + textLength)..];
            return level;
        }

        private WordListLevel? AddListMarker(XElement properties, Dictionary<ushort, int>? direct,
            Dictionary<ushort, int>? style, Dictionary<ushort, int>? mark)
        {
            int listId = (short)EffectiveProperty(direct, style, 0x460B, missing: 0);
            int levelIndex = EffectiveProperty(direct, style, 0x260A, missing: 0);
            if (listId is 0 or 0x7FF || !_lists.TryGetValue(Math.Abs(listId), out WordList? list) ||
                (uint)levelIndex >= list.Levels.Length) return null;
            WordListLevel level = list.Levels[levelIndex];
            if (level.Format == 0xFF) return listId > 0 ? level : null;

            string label = level.Text;
            if (level.Format != 0x17)
            {
                list.Counters[levelIndex] = (list.Counters[levelIndex] ?? level.Start - 1) + 1;
                var number = new StringBuilder();
                foreach (char character in label)
                {
                    if (character < list.Levels.Length)
                    {
                        WordListLevel reference = list.Levels[character];
                        number.Append(FormatListNumber(list.Counters[character] ?? reference.Start,
                            level.LegalNumbering && reference.Format != 0x16 ? 0 : reference.Format));
                    }
                    else number.Append(character);
                }
                label = number.ToString();
            }
            for (int lower = levelIndex + 1; lower < list.Levels.Length; lower++)
            {
                if (levelIndex < list.Levels[lower].RestartLimit) list.Counters[lower] = null;
            }

            if (label.Length > 0)
            {
                var run = new XElement(W + "rPr");
                AddRunFonts(run, level.Characters, mark);
                int size = Get(level.Characters, 0x4A43);
                if (size > 0) run.Add(Val("sz", size));
                if (EffectiveProperty(level.Characters, mark, 0x0835, 0x085C) is 1 or 129)
                    run.Add(new XElement(W + "b"));
                if (EffectiveProperty(level.Characters, mark, 0x0836, 0x085D) is 1 or 129)
                    run.Add(new XElement(W + "i"));
                properties.Add(new XElement(Preview + "listMarker",
                    new XAttribute("alignment", level.Alignment), new XAttribute("follow", level.Follow),
                    run, new XElement(W + "t", XmlText(label))));
            }
            return listId > 0 ? level : null;
        }

        private static string FormatListNumber(int number, int format)
        {
            if (number > 0 && format is 3 or 4)
            {
                // Word repeats the same letter after Z, alternating through AA, BB, and so on.
                return new string((char)((format == 3 ? 'A' : 'a') + (number - 1) % 26), (number - 1) / 26 + 1);
            }
            if (number is > 0 and <= 3999 && format is 1 or 2)
            {
                int[] values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
                string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
                var roman = new StringBuilder();
                for (int i = 0; i < values.Length; i++)
                {
                    while (number >= values[i])
                    {
                        roman.Append(symbols[i]);
                        number -= values[i];
                    }
                }
                return format == 2 ? roman.ToString().ToLowerInvariant() : roman.ToString();
            }
            return number.ToString(format == 0x16 ? "D2" : "D", CultureInfo.InvariantCulture);
        }

        private sealed record WordListLevel(int Start, int Format, int Alignment, int Follow, int RestartLimit,
            bool LegalNumbering, string Text, Dictionary<ushort, int> Paragraph, Dictionary<ushort, int> Characters);

        private sealed class WordList(WordListLevel[] levels)
        {
            internal WordListLevel[] Levels { get; } = levels;
            internal int?[] Counters { get; } = new int?[levels.Length];
        }
    }
}
