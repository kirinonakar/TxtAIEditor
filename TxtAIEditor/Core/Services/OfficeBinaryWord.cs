using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeBinaryData;
using static TxtAIEditor.Core.Services.OfficeLegacyPackage;

namespace TxtAIEditor.Core.Services
{
    // MS-DOC, Word 97–2003. Text is in logical CP order; FKP properties use physical FC offsets.
    internal sealed class OfficeBinaryWord
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private static readonly Encoding CompressedText = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        private static readonly string[] Colors = ["", "000000", "0000FF", "00FFFF", "00FF00", "FF00FF", "FF0000",
            "FFFF00", "FFFFFF", "000080", "008080", "008000", "800080", "800000", "808000", "808080", "C0C0C0"];

        private readonly ZipArchive _archive;
        private readonly OfficeCompoundFile _compound;
        private readonly CancellationToken _cancellationToken;
        private readonly XElement _body = new(W + "body");
        private readonly XElement _relationships = new(Relationships + "Relationships");
        private readonly List<bool> _fieldResults = new();
        private readonly Dictionary<int, Picture?> _pictures = new();
        private readonly Dictionary<int, Picture> _anchoredPictures = new();
        private readonly List<string> _fonts = new();
        private int _pictureCount;
        private int _hiddenFieldCount;
        private byte[]? _data;
        private XElement _paragraph = new(W + "p");
        private XElement? _table;
        private XElement? _row;
        private XElement? _cell;

        private OfficeBinaryWord(ZipArchive archive, OfficeCompoundFile compound, CancellationToken cancellationToken)
        {
            _archive = archive;
            _compound = compound;
            _cancellationToken = cancellationToken;
        }

        internal static void Write(ZipArchive archive, OfficeCompoundFile compound, CancellationToken cancellationToken) =>
            new OfficeBinaryWord(archive, compound, cancellationToken).Write();

        private void Write()
        {
            byte[] word = _compound.ReadStream("WordDocument");
            ushort flags = U16(word, 10);
            if ((flags & 0x8100) != 0) throw Encrypted();
            if (U16(word, 0) != 0xA5EC || U16(word, 2) < 0x00C1)
            {
                throw new InvalidDataException("This Word binary version is unsupported.");
            }

            byte[] table = _compound.ReadStream((flags & 0x0200) != 0 ? "1Table" : "0Table");
            int longWords = 34 + 2 * U16(word, 32);
            int mainLength = I32(word, longWords + 14);
            int offsets = longWords + 2 + 4 * U16(word, longWords);
            if (mainLength < 0 || mainLength > word.Length || U16(word, offsets) < 34)
            {
                throw new InvalidDataException("Invalid Word binary document header.");
            }

            offsets += 2;
            ReadFonts(TablePart(word, table, offsets, 15));
            if (U16(word, offsets - 2) > 50)
            {
                ReadAnchoredPictures(word, table, offsets);
            }
            List<PropertyRange> paragraphs = ReadFkp(word, TablePart(word, table, offsets, 13), paragraph: true);
            List<PropertyRange> characters = ReadFkp(word, TablePart(word, table, offsets, 12), paragraph: false);
            ReadOnlySpan<byte> clx = TablePart(word, table, offsets, 33);
            int cursor = 0;
            while (cursor < clx.Length && clx[cursor] == 1)
            {
                cursor += 3 + U16(clx, cursor + 1);
            }

            if (mainLength > 0)
            {
                if (cursor >= clx.Length || clx[cursor] != 2)
                {
                    throw new InvalidDataException("Missing Word text piece table.");
                }

                ReadPieces(word, Slice(clx, cursor + 5, I32(clx, cursor + 1)), mainLength, paragraphs, characters);
            }

            if (_paragraph.HasElements) FinishParagraph('\r', default);
            FinishTable();
            if (!_body.HasElements) _body.Add(new XElement(W + "p"));
            _body.Add(ReadSection(word, TablePart(word, table, offsets, 6)));
            WriteXml(_archive, "word/document.xml", new XElement(W + "document", _body));
            if (_relationships.HasElements) WriteXml(_archive, "word/_rels/document.xml.rels", _relationships);
        }

        private void ReadPieces(byte[] word, ReadOnlySpan<byte> pieces, int length,
            List<PropertyRange> paragraphs, List<PropertyRange> characters)
        {
            if (pieces.Length < 4 || (pieces.Length - 4) % 12 != 0 || I32(pieces, 0) != 0)
            {
                throw new InvalidDataException("Invalid Word text piece table.");
            }

            int count = (pieces.Length - 4) / 12;
            if (I32(pieces, count * 4) < length) throw new InvalidDataException("Incomplete Word text piece table.");
            var text = new StringBuilder();
            Dictionary<ushort, int>? style = null;
            for (int i = 0; i < count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int start = I32(pieces, i * 4);
                int end = Math.Min(length, I32(pieces, (i + 1) * 4));
                if (start >= length) break;
                if (start < 0 || end < start) throw new InvalidDataException("Unordered Word text pieces.");
                uint fc = U32(pieces, (count + 1) * 4 + i * 8 + 2);
                bool compressed = (fc & 0x40000000) != 0;
                int stride = compressed ? 1 : 2;
                int offset = (int)(fc & 0x3FFFFFFF) / (compressed ? 2 : 1);
                ReadOnlySpan<byte> data = Slice(word, offset, checked((end - start) * stride));
                string value = (compressed ? CompressedText : Encoding.Unicode).GetString(data);
                PropertyRange range = default;
                for (int index = 0; index < value.Length; index++)
                {
                    if ((index & 4095) == 0) _cancellationToken.ThrowIfCancellationRequested();
                    int position = offset + index * stride;
                    if (position < range.Start || position >= range.End)
                    {
                        range = FindRange(characters, position);
                        if (!ReferenceEquals(style, range.Properties))
                        {
                            FlushText(text, style);
                            style = range.Properties;
                        }
                    }

                    char ch = value[index];
                    if (ch is '\r' or '\a')
                    {
                        FlushText(text, style);
                        FinishParagraph(ch, FindRange(paragraphs, position));
                        continue;
                    }

                    // Keep the displayed result of a field, including nested fields.
                    if (ch == '\u0013')
                    {
                        _fieldResults.Add(false);
                        _hiddenFieldCount++;
                        continue;
                    }
                    if (ch is '\u0014' or '\u0015')
                    {
                        if (_fieldResults.Count > 0)
                        {
                            if (!_fieldResults[^1]) _hiddenFieldCount--;
                            if (ch == '\u0014') _fieldResults[^1] = true;
                            else _fieldResults.RemoveAt(_fieldResults.Count - 1);
                        }
                        continue;
                    }
                    if (_hiddenFieldCount > 0) continue;

                    switch (ch)
                    {
                        case '\t':
                        case '\n':
                        case '\v':
                        case '\f':
                            FlushText(text, style);
                            _paragraph.Add(new XElement(W + "r", ch == '\t' ? new XElement(W + "tab") :
                                new XElement(W + "br", ch == '\f' ? new XAttribute(W + "type", "page") : null)));
                            break;
                        case '\u0001':
                            FlushText(text, style);
                            if (style != null && style.TryGetValue(0x6A03, out int imageOffset) &&
                                ReadPicture(imageOffset) is Picture picture)
                            {
                                AppendPicture(picture);
                            }
                            break;
                        case '\b':
                            if (_anchoredPictures.TryGetValue(start + index, out Picture? anchored))
                            {
                                FlushText(text, style);
                                AppendPicture(anchored);
                            }
                            break;
                        case '\u001E': text.Append('\u2011'); break;
                        case '\u001F': text.Append('\u00AD'); break;
                        default:
                            if (ch >= ' ') text.Append(ch);
                            break;
                    }
                }
            }

            FlushText(text, style);
        }

        private void FlushText(StringBuilder text, Dictionary<ushort, int>? style)
        {
            if (text.Length == 0) return;
            XElement properties = new(W + "rPr");
            if (Get(style, 0x0835, 0x085C) is 1 or 129) properties.Add(new XElement(W + "b"));
            if (Get(style, 0x0836, 0x085D) is 1 or 129) properties.Add(new XElement(W + "i"));
            if (Get(style, 0x2A3E) > 0) properties.Add(new XElement(W + "u"));
            int size = Get(style, 0x4A43, 0x4A61);
            if (size > 0) properties.Add(Val("sz", size));
            if (style?.ContainsKey(0x4A4F) == true || style?.ContainsKey(0x4A50) == true)
            {
                int font = Get(style, 0x4A4F, 0x4A50);
                int eastAsian = Get(style, 0x4A50, 0x4A4F);
                var fonts = new XElement(W + "rFonts");
                if ((uint)font < _fonts.Count && _fonts[font].Length > 0) fonts.SetAttributeValue(W + "ascii", _fonts[font]);
                if ((uint)eastAsian < _fonts.Count && _fonts[eastAsian].Length > 0) fonts.SetAttributeValue(W + "eastAsia", _fonts[eastAsian]);
                if (fonts.HasAttributes) properties.Add(fonts);
            }
            int color = Get(style, 0x6870);
            int indexedColor = Get(style, 0x2A42);
            if (style?.ContainsKey(0x6870) == true) properties.Add(Val("color", ColorRef(color)));
            else if (indexedColor > 0 && indexedColor < Colors.Length) properties.Add(Val("color", Colors[indexedColor]));
            _paragraph.Add(new XElement(W + "r", properties.HasElements ? properties : null,
                new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), XmlText(text.ToString()))));
            text.Clear();
        }

        private void FinishParagraph(char mark, PropertyRange range)
        {
            Dictionary<ushort, int>? style = range.Properties;
            XElement properties = new(W + "pPr");
            string? alignment = Get(style, 0x2461, 0x2403) switch
            {
                1 => "center", 2 => "right", 3 => "both", 4 => "distribute", _ => null
            };
            if (alignment != null) properties.Add(Val("jc", alignment));
            if (Get(style, 0x2407) == 1) properties.Add(new XElement(W + "pageBreakBefore"));
            AddMeasurements(properties, "spacing", ("before", Get(style, 0xA413)), ("after", Get(style, 0xA414)));
            AddMeasurements(properties, "ind", ("left", (short)Get(style, 0x845E, 0x840F)),
                ("right", (short)Get(style, 0x845D, 0x840E)), ("firstLine", (short)Get(style, 0x8460, 0x8411)));
            if (properties.HasElements) _paragraph.AddFirst(properties);

            bool inTable = mark == '\a' || Get(style, 0x2416) == 1 || Get(style, 0x6649) > 0;
            if (inTable)
            {
                _table ??= new XElement(W + "tbl");
                if ((mark == '\a' && Get(style, 0x2417) == 1) || Get(style, 0x244C) == 1)
                {
                    FinishRow(range.RowProperties);
                }
                else
                {
                    _row ??= new XElement(W + "tr");
                    _cell ??= new XElement(W + "tc");
                    _cell.Add(_paragraph);
                    if (mark == '\a' || Get(style, 0x244B) == 1) FinishCell();
                }
            }
            else
            {
                FinishTable();
                _body.Add(_paragraph);
            }

            _paragraph = new XElement(W + "p");
        }

        private void FinishCell()
        {
            if (_cell == null) return;
            _row!.Add(_cell);
            _cell = null;
        }

        private void FinishRow(OfficeBinaryWordTable.RowProperties? properties = null)
        {
            FinishCell();
            if (_row == null) return;
            if (properties != null) _row.AddAnnotation(properties);
            _table!.Add(_row);
            _row = null;
        }

        private void FinishTable()
        {
            if (_table == null) return;
            FinishRow();
            if (_table.Element(W + "tr") != null)
            {
                OfficeBinaryWordTable.ApplyLayout(_table);
                _body.Add(_table);
            }
            _table = null;
        }

        private Picture? ReadPicture(int offset)
        {
            if (_pictures.TryGetValue(offset, out Picture? existing)) return existing;
            _pictures[offset] = null;
            if (!_compound.Contains("Data")) return null;
            _data ??= _compound.ReadStream("Data");
            if (offset < 0 || offset > _data.Length - 68 || U16(_data, offset + 4) != 68) return null;
            int length = I32(_data, offset);
            if (length < 68 || length > _data.Length - offset) return null;
            int drawing = offset + 68;
            int end = offset + length;
            if (U16(_data, offset + 6) == 0x0066 && drawing < end) drawing += 1 + _data[drawing];
            OfficeArtBinary.Image? image = null;
            foreach (OfficeArtBinary.Record record in OfficeArtBinary.Records(_data, drawing, end))
            {
                image = record.Type == 0xF007 ? OfficeArtBinary.ReadBse(_data, record, null) : OfficeArtBinary.ReadBlip(_data, record);
                if (image != null) break;
            }

            if (image == null) return null;
            return _pictures[offset] = WritePicture(image, ImageSize(_data, offset + 28, offset + 32),
                ImageSize(_data, offset + 30, offset + 34));
        }

        private void ReadAnchoredPictures(byte[] word, byte[] table, int offsets)
        {
            ReadOnlySpan<byte> anchors = TablePart(word, table, offsets, 40);
            int length = I32(word, offsets + 50 * 8 + 4);
            if (anchors.Length < 34 || (anchors.Length - 4) % 30 != 0 || length == 0) return;

            int start = I32(word, offsets + 50 * 8);
            int end = start + Slice(table, start, length).Length;
            OfficeArtBinary.Record group = OfficeArtBinary.ReadRecord(table, start, end);
            OfficeArtBinary.Record store = group.Children(table).FirstOrDefault(record => record.Type == 0xF001);
            OfficeArtBinary.Record[] images = store.Children(table).ToArray();
            var shapeImages = new Dictionary<uint, int>();
            for (int position = group.End; position < end;)
            {
                int label = table[position++];
                OfficeArtBinary.Record drawing = OfficeArtBinary.ReadRecord(table, position, end);
                position = drawing.End;
                if (label != 0 || drawing.Type != 0xF002) continue;
                foreach (OfficeArtBinary.Record shape in OfficeArtBinary.ShapeContainers(table, drawing))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    OfficeArtBinary.Record[] parts = shape.Children(table).ToArray();
                    OfficeArtBinary.Record id = parts.FirstOrDefault(record => record.Type == 0xF00A);
                    if (id.Length < 8) continue;
                    int image = (int)OfficeArtBinary.ReadShapeOptions(table, parts).GetValueOrDefault(260);
                    if (image > 0 && image <= images.Length) shapeImages.TryAdd(U32(id.Body(table), 0), image - 1);
                }
            }

            int count = (anchors.Length - 4) / 30;
            var paths = new Dictionary<int, Picture>();
            for (int index = 0; index < count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> anchor = Slice(anchors, 4 * (count + 1) + index * 26, 26);
                if (!shapeImages.TryGetValue(U32(anchor, 0), out int imageIndex)) continue;
                long width = Math.Max(1, I32(anchor, 12) - (long)I32(anchor, 4)) * 635;
                long height = Math.Max(1, I32(anchor, 16) - (long)I32(anchor, 8)) * 635;
                if (!paths.TryGetValue(imageIndex, out Picture? picture))
                {
                    OfficeArtBinary.Record record = images[imageIndex];
                    OfficeArtBinary.Image? image = record.Type == 0xF007
                        ? OfficeArtBinary.ReadBse(table, record, word) : OfficeArtBinary.ReadBlip(table, record);
                    if (image == null) continue;
                    paths.Add(imageIndex, picture = WritePicture(image, width, height));
                }
                _anchoredPictures[I32(anchors, index * 4)] = picture with { Width = width, Height = height };
            }
        }

        private Picture WritePicture(OfficeArtBinary.Image image, long width, long height)
        {
            string id = "rImage" + ++_pictureCount;
            string path = "media/" + id + "." + image.Extension;
            image.Write(_archive, "word/" + path);
            _relationships.Add(Relationship(id, path, "image"));
            return new Picture(id, width, height);
        }

        private void AppendPicture(Picture picture)
        {
            _paragraph.Add(new XElement(W + "r", new XElement(W + "drawing",
                new XElement(Wp + "inline", new XElement(Wp + "extent",
                    new XAttribute("cx", picture.Width), new XAttribute("cy", picture.Height)),
                    new XElement(A + "blip", new XAttribute(RelationshipId + "embed", picture.Id))))));
        }

        private void ReadFonts(ReadOnlySpan<byte> fonts)
        {
            if (fonts.IsEmpty) return;
            int count = U16(fonts, 0);
            for (int index = 0, position = 4; index < count && position < fonts.Length; index++)
            {
                int length = fonts[position] + 1;
                if (length < 40 || length > fonts.Length - position) break;
                int nameLength = (length - 40) & ~1;
                _fonts.Add(XmlText(Encoding.Unicode.GetString(fonts.Slice(position + 40, nameLength)).Split('\0', 2)[0]));
                position += length;
            }
        }

        private static long ImageSize(byte[] data, int size, int scale) =>
            Math.Max(1, (int)I16(data, size)) * 635L * Math.Max(1, (int)U16(data, scale)) / 1000;

        private XElement ReadSection(byte[] word, ReadOnlySpan<byte> sections)
        {
            Dictionary<ushort, int>? properties = null;
            if (sections.Length >= 20 && (sections.Length - 4) % 16 == 0)
            {
                int count = (sections.Length - 4) / 16;
                int offset = I32(sections, (count + 1) * 4 + 2);
                if (offset != -1) properties = ReadProperties(Slice(word, offset + 2, U16(word, offset)), false, out _);
            }

            return new XElement(W + "sectPr",
                new XElement(W + "pgSz", new XAttribute(W + "w", SectionValue(properties, 0xB01F, 12240)),
                    new XAttribute(W + "h", SectionValue(properties, 0xB020, 15840))),
                new XElement(W + "pgMar", new XAttribute(W + "left", SectionValue(properties, 0xB021, 1440)),
                    new XAttribute(W + "right", SectionValue(properties, 0xB022, 1440)),
                    new XAttribute(W + "top", Math.Abs((short)SectionValue(properties, 0x9023, 1440))),
                    new XAttribute(W + "bottom", Math.Abs((short)SectionValue(properties, 0x9024, 1440)))));
        }

        private static int SectionValue(Dictionary<ushort, int>? style, ushort key, int fallback) =>
            style != null && style.TryGetValue(key, out int value) ? value : fallback;

        private static ReadOnlySpan<byte> TablePart(byte[] word, byte[] table, int offsets, int index)
        {
            int pair = offsets + index * 8;
            int length = I32(word, pair + 4);
            return length == 0 ? [] : Slice(table, I32(word, pair), length);
        }

        private List<PropertyRange> ReadFkp(byte[] word, ReadOnlySpan<byte> plc, bool paragraph)
        {
            var ranges = new List<PropertyRange>();
            if (plc.IsEmpty) return ranges;
            if (plc.Length < 4 || (plc.Length - 4) % 8 != 0) throw new InvalidDataException("Invalid Word formatting PLC.");
            int pages = (plc.Length - 4) / 8;
            var seen = new HashSet<uint>();
            for (int index = 0; index < pages; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                uint pageNumber = U32(plc, (pages + 1) * 4 + index * 4) & 0x003FFFFF;
                if (!seen.Add(pageNumber)) continue;
                ReadOnlySpan<byte> page = Slice(word, checked((int)pageNumber * 512), 512);
                int runs = page[511];
                int entries = (runs + 1) * 4;
                int stride = paragraph ? 13 : 1;
                if (entries + runs * stride > 511) throw new InvalidDataException("Invalid Word formatting page.");
                for (int run = 0; run < runs; run++)
                {
                    int offset = page[entries + run * stride] * 2;
                    if (offset == 0) continue;
                    int length = page[offset++];
                    if (paragraph)
                    {
                        length = length == 0 ? 2 * page[offset++] : 2 * length - 1;
                        if (length < 2) continue;
                        offset += 2; // Paragraph style index precedes the direct properties.
                        length -= 2;
                    }

                    var properties = ReadProperties(Slice(page[..511], offset, length), paragraph, out var rowProperties);
                    if (properties.Count > 0 || rowProperties != null)
                    {
                        ranges.Add(new PropertyRange(I32(page, run * 4), I32(page, (run + 1) * 4), properties, rowProperties));
                    }
                }
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            return ranges;
        }

        private Dictionary<ushort, int> ReadProperties(ReadOnlySpan<byte> data, bool paragraph,
            out OfficeBinaryWordTable.RowProperties? rowProperties)
        {
            var properties = new Dictionary<ushort, int>();
            rowProperties = null;
            ReadProperties(data, properties, paragraph, ref rowProperties, depth: 0);
            return properties;
        }

        private void ReadProperties(ReadOnlySpan<byte> data, Dictionary<ushort, int> properties, bool paragraph,
            ref OfficeBinaryWordTable.RowProperties? rowProperties, int depth)
        {
            for (int offset = 0; offset + 2 <= data.Length;)
            {
                ushort code = U16(data, offset);
                offset += 2;
                int size = (code >> 13) switch
                {
                    0 or 1 => 1, 2 or 4 or 5 => 2, 3 => 4, 7 => 3,
                    _ => -1
                };
                if (size == -1)
                {
                    if (offset >= data.Length) break;
                    if (code == 0xD608 && offset + 2 <= data.Length)
                    {
                        size = U16(data, offset) - 1;
                        offset += 2;
                    }
                    else
                    {
                        size = data[offset++];
                    }

                }

                if (size < 0 || size > data.Length - offset) break;
                ReadOnlySpan<byte> operand = data.Slice(offset, size);
                if (paragraph && OfficeBinaryWordTable.IsRowProperty(code))
                {
                    (rowProperties ??= new()).Add(code, operand);
                }
                else if (paragraph && code is 0x6646 or 0x646B)
                {
                    if (depth < 8 && TryReadIndirectProperties(I32(operand, 0), out ReadOnlySpan<byte> indirect))
                    {
                        ReadProperties(indirect, properties, paragraph, ref rowProperties, depth + 1);
                    }
                    // A huge PAPX replaces the rest of this property block. For PTableProps,
                    // also read the backward-compatible TDefTable that can follow the pointer.
                    if (code == 0x6646) break;
                }
                else if ((code >> 13) != 6)
                {
                    if (size == 1) properties[code] = operand[0];
                    if (size == 2) properties[code] = U16(operand, 0);
                    if (size == 4) properties[code] = I32(operand, 0);
                }
                offset += size;
            }
        }

        private bool TryReadIndirectProperties(int offset, out ReadOnlySpan<byte> properties)
        {
            properties = default;
            if (!_compound.Contains("Data")) return false;
            _data ??= _compound.ReadStream("Data");
            if (offset < 0 || offset > _data.Length - 2) return false;
            int length = U16(_data, offset);
            if (length > _data.Length - offset - 2) return false;
            properties = _data.AsSpan(offset + 2, length);
            return true;
        }

        private static PropertyRange FindRange(List<PropertyRange> ranges, int position)
        {
            int low = 0;
            int high = ranges.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                PropertyRange range = ranges[middle];
                if (position < range.Start) high = middle - 1;
                else if (position >= range.End) low = middle + 1;
                else return range;
            }

            return new PropertyRange(position, low < ranges.Count ? ranges[low].Start : int.MaxValue, null, null);
        }

        private static int Get(Dictionary<ushort, int>? properties, ushort key, ushort fallback = 0) =>
            properties != null && (properties.TryGetValue(key, out int value) ||
                fallback != 0 && properties.TryGetValue(fallback, out value)) ? value : 0;

        private static XElement Val(string name, object value) => new(W + name, new XAttribute(W + "val", value));
        internal static string ColorRef(int color) => $"{color & 255:X2}{(color >> 8) & 255:X2}{(color >> 16) & 255:X2}";
        internal static string? IndexedColor(int index) => index > 0 && index < Colors.Length ? Colors[index] : null;

        private static void AddMeasurements(XElement properties, string name, params (string Name, int Value)[] values)
        {
            var item = new XElement(W + name);
            foreach ((string key, int value) in values)
            {
                if (value != 0) item.SetAttributeValue(W + key, value);
            }
            if (item.HasAttributes) properties.Add(item);
        }

        private readonly record struct PropertyRange(int Start, int End, Dictionary<ushort, int>? Properties,
            OfficeBinaryWordTable.RowProperties? RowProperties);
        private sealed record Picture(string Id, long Width, long Height);
    }
}
