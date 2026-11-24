using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeBinaryData;
using static TxtAIEditor.Core.Services.OfficeBinaryPresentation;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeBinaryPresentationText(
        byte[] data, Dictionary<int, string> fonts, string[] colors,
        Dictionary<int, OfficeBinaryPresentationText.Style> masterStyles)
    {
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

        internal XElement Build(TextSource source, Dictionary<int, uint>? shape)
        {
            string value = source.Value.TrimEnd('\0');
            int size = source.Type is 0 or 6 ? 32 : 20;
            Style master = masterStyles.GetValueOrDefault(source.Type);
            var bodyProperties = new XElement(A + "bodyPr");
            if (shape != null)
            {
                SetAttribute(bodyProperties, "lIns", shape, 129);
                SetAttribute(bodyProperties, "tIns", shape, 130);
                SetAttribute(bodyProperties, "rIns", shape, 131);
                SetAttribute(bodyProperties, "bIns", shape, 132);
                if (shape.TryGetValue(135, out uint anchor))
                {
                    bodyProperties.SetAttributeValue("anchor", anchor switch { 1 or 4 => "ctr", 2 or 5 => "b", _ => "t" });
                }
            }

            ParseRuns(source, value.Length, out List<ParagraphRun> paragraphs, out List<CharacterRun> characters);
            XElement textBody = new(P + "txBody", bodyProperties);
            int start = 0;
            int paragraphIndex = 0;
            int characterIndex = 0;
            do
            {
                int end = value.IndexOf('\r', start);
                if (end < 0) end = value.Length;
                while (paragraphIndex + 1 < paragraphs.Count && paragraphs[paragraphIndex].End <= start) paragraphIndex++;
                ParagraphStyle paragraph = paragraphIndex < paragraphs.Count ? paragraphs[paragraphIndex].Style : default;
                XElement p = new(A + "p", ParagraphProperties(paragraph, master.Paragraph));
                for (int offset = start; offset < end;)
                {
                    while (characterIndex < characters.Count && characters[characterIndex].End <= offset) characterIndex++;
                    CharacterRun run = characterIndex < characters.Count ? characters[characterIndex] : new CharacterRun(end, default);
                    int runEnd = Math.Min(end, run.End);
                    ReadOnlySpan<char> text = value.AsSpan(offset, runEnd - offset);
                    int breakOffset = text.IndexOfAny('\v', '\n');
                    if (breakOffset >= 0)
                    {
                        if (breakOffset > 0) AppendRun(p, text[..breakOffset], run.Style, master.Character, size);
                        p.Add(new XElement(A + "br"));
                        offset += breakOffset + 1;
                    }
                    else
                    {
                        AppendRun(p, text, run.Style, master.Character, size);
                        offset = runEnd;
                    }
                }
                textBody.Add(p);
                start = end + 1;
            }
            while (start <= value.Length);
            return textBody;
        }

        internal Style? ReadMasterStyle(OfficeArtBinary.Record record)
        {
            try
            {
                ReadOnlySpan<byte> style = record.Body(data);
                if (U16(style, 0) == 0) return null;
                int offset = record.Instance >= 5 ? 4 : 2;
                ParagraphStyle paragraph = ReadParagraph(style, ref offset);
                return new Style(paragraph, ReadCharacter(style, ref offset));
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private void ParseRuns(TextSource source, int length, out List<ParagraphRun> paragraphs, out List<CharacterRun> characters)
        {
            paragraphs = new();
            characters = new();
            if (source.Styles.Length == 0 || length == 0) return;

            ReadOnlySpan<byte> styles = source.Styles.Body(data);
            // A style run can include the implicit paragraph mark after the stored text.
            // Read that mark before looking for character formatting, even when it has its own run.
            if (!TryParseRuns(styles, length + 1, length, paragraphs, characters))
            {
                TryParseRuns(styles, length, length, paragraphs, characters);
            }
        }

        private bool TryParseRuns(ReadOnlySpan<byte> styles, int paragraphLength, int textLength,
            List<ParagraphRun> paragraphs, List<CharacterRun> characters)
        {
            try
            {
                int offset = 0;
                int end = 0;
                while (end < paragraphLength)
                {
                    end += ReadCount(styles, offset, textLength, end);
                    int level = U16(styles, offset + 4);
                    offset += 6;
                    paragraphs.Add(new ParagraphRun(end, ReadParagraph(styles, ref offset) with { Level = level }));
                }
                end = 0;
                while (end < textLength)
                {
                    end += ReadCount(styles, offset, textLength, end);
                    offset += 4;
                    characters.Add(new CharacterRun(end, ReadCharacter(styles, ref offset)));
                }
                return true;
            }
            catch (InvalidDataException)
            {
                // A malformed optional format block must not hide the slide text.
                paragraphs.Clear();
                characters.Clear();
                return false;
            }
        }

        private static int ReadCount(ReadOnlySpan<byte> styles, int offset, int length, int end)
        {
            int count = I32(styles, offset);
            if (count <= 0 || count > length - end + 1) throw new InvalidDataException("Invalid PowerPoint text run.");
            return count;
        }

        private static ParagraphStyle ReadParagraph(ReadOnlySpan<byte> data, ref int offset)
        {
            uint mask = U32(data, offset);
            offset += 4;
            int bulletFlags = (mask & 0xF) != 0 ? ReadShort(data, ref offset) : 0;
            int? bulletChar = OptionalShort(data, ref offset, mask, 0x80);
            _ = OptionalShort(data, ref offset, mask, 0x10); // Bullet font.
            _ = OptionalShort(data, ref offset, mask, 0x40); // Bullet size.
            if ((mask & 0x20) != 0) offset += 4;
            int? alignment = OptionalShort(data, ref offset, mask, 0x800);
            _ = OptionalShort(data, ref offset, mask, 0x1000);
            _ = OptionalShort(data, ref offset, mask, 0x2000);
            _ = OptionalShort(data, ref offset, mask, 0x4000);
            int? leftMargin = OptionalShort(data, ref offset, mask, 0x100);
            int? indent = OptionalShort(data, ref offset, mask, 0x400);
            _ = OptionalShort(data, ref offset, mask, 0x8000);
            if ((mask & 0x100000) != 0)
            {
                int tabCount = U16(data, offset);
                offset += 2 + 4 * tabCount;
            }
            _ = OptionalShort(data, ref offset, mask, 0x10000);
            if ((mask & 0xE0000) != 0) _ = ReadShort(data, ref offset);
            _ = OptionalShort(data, ref offset, mask, 0x200000);
            return new ParagraphStyle(alignment, (mask & 1) != 0 ? (bulletFlags & 1) != 0 : null,
                bulletChar, leftMargin, indent, null);
        }

        private CharacterStyle ReadCharacter(ReadOnlySpan<byte> data, ref int offset)
        {
            uint mask = U32(data, offset);
            offset += 4;
            int flags = (mask & 0x3EB7) != 0 ? ReadShort(data, ref offset) : 0;
            int? font = OptionalShort(data, ref offset, mask, 0x10000);
            int? eastAsianFont = OptionalShort(data, ref offset, mask, 0x200000);
            _ = OptionalShort(data, ref offset, mask, 0x400000);
            _ = OptionalShort(data, ref offset, mask, 0x800000);
            int? size = OptionalShort(data, ref offset, mask, 0x20000);
            if (size is < 1 or > 4000) size = null;
            string? color = null;
            if ((mask & 0x40000) != 0)
            {
                uint rgb = U32(data, offset);
                offset += 4;
                uint index = rgb >> 24;
                color = index < colors.Length ? colors[index] : index == 0xFE ? Rgb(rgb) : null;
            }
            _ = OptionalShort(data, ref offset, mask, 0x80000);
            return new CharacterStyle(
                (mask & 1) != 0 ? (flags & 1) != 0 : null,
                (mask & 2) != 0 ? (flags & 2) != 0 : null,
                (mask & 4) != 0 ? (flags & 4) != 0 : null, font, eastAsianFont, size, color);
        }

        private static XElement ParagraphProperties(ParagraphStyle paragraph, ParagraphStyle master)
        {
            var properties = new XElement(A + "pPr");
            int? alignment = paragraph.Alignment ?? master.Alignment;
            if (alignment.HasValue)
            {
                properties.SetAttributeValue("algn", alignment switch { 1 => "ctr", 2 => "r", 3 => "just", _ => "l" });
            }
            if (paragraph.Level is >= 0 and <= 8) properties.SetAttributeValue("lvl", paragraph.Level);
            int? left = paragraph.LeftMargin ?? master.LeftMargin;
            int? indent = paragraph.Indent ?? master.Indent;
            if (left.HasValue) properties.SetAttributeValue("marL", MasterUnitsToEmu(left.Value));
            if (indent.HasValue) properties.SetAttributeValue("indent", MasterUnitsToEmu(indent.Value - left.GetValueOrDefault()));
            if (paragraph.Bullet ?? master.Bullet ?? false)
            {
                int code = paragraph.BulletChar ?? master.BulletChar ?? 0x2022;
                string bullet = code < 32 || code >= 0xE000 ? "•" : XmlText(((char)code).ToString());
                properties.Add(new XElement(A + "buChar", new XAttribute("char", bullet)));
            }
            return properties;
        }

        private void AppendRun(XElement paragraph, ReadOnlySpan<char> text, CharacterStyle direct, CharacterStyle master, int defaultSize)
        {
            int size = Math.Clamp(direct.Size ?? master.Size ?? defaultSize, 1, 4000);
            var properties = new XElement(A + "rPr", new XAttribute("sz", size * 100),
                new XAttribute("b", direct.Bold ?? master.Bold ?? false ? 1 : 0),
                new XAttribute("i", direct.Italic ?? master.Italic ?? false ? 1 : 0));
            if (direct.Underline ?? master.Underline ?? false) properties.SetAttributeValue("u", "sng");
            properties.Add(SolidFill(direct.Color ?? master.Color ?? colors[1]));
            if (fonts.TryGetValue(direct.Font ?? master.Font ?? -1, out string? font))
            {
                properties.Add(new XElement(A + "latin", new XAttribute("typeface", font)));
            }
            if (fonts.TryGetValue(direct.EastAsianFont ?? master.EastAsianFont ?? -1, out string? eastAsian))
            {
                properties.Add(new XElement(A + "ea", new XAttribute("typeface", eastAsian)));
            }
            paragraph.Add(new XElement(A + "r", properties, new XElement(A + "t", XmlText(text.ToString()))));
        }

        private static void SetAttribute(XElement element, string name, Dictionary<int, uint> shape, int property)
        {
            if (shape.TryGetValue(property, out uint value)) element.SetAttributeValue(name, (int)value);
        }

        private static int ReadShort(ReadOnlySpan<byte> data, ref int offset)
        {
            int value = I16(data, offset);
            offset += 2;
            return value;
        }

        private static int? OptionalShort(ReadOnlySpan<byte> data, ref int offset, uint mask, uint bit) =>
            (mask & bit) != 0 ? ReadShort(data, ref offset) : null;

        internal readonly record struct Style(ParagraphStyle Paragraph, CharacterStyle Character);
        internal readonly record struct ParagraphStyle(int? Alignment, bool? Bullet, int? BulletChar,
            int? LeftMargin, int? Indent, int? Level);
        internal readonly record struct CharacterStyle(bool? Bold, bool? Italic, bool? Underline,
            int? Font, int? EastAsianFont, int? Size, string? Color);
        private readonly record struct ParagraphRun(int End, ParagraphStyle Style);
        private readonly record struct CharacterRun(int End, CharacterStyle Style);
    }
}
