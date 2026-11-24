using System;
using System.Collections.Generic;
using System.Globalization;
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
    // MS-XLS. Read BIFF8 globals and worksheet substreams; BIFF5 strings use the workbook's code page.
    internal sealed class OfficeBinaryWorkbook
    {
        private const int Bof = 0x0809;
        private const int Eof = 0x000A;
        private const int Continue = 0x003C;
        private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly string[] DefaultPalette = [
            "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
            "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080",
            "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
            "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
            "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
            "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
            "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333"];

        private readonly byte[] _data;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _unicode;
        private Encoding _codePage = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        private string[] _palette = DefaultPalette;
        private readonly List<Font> _fonts = new();
        private readonly List<Format> _formats = new();
        private readonly Dictionary<int, string> _numberFormats = new();
        private readonly List<Sheet> _sheets = new();
        private readonly List<bool> _localBooks = new();
        private readonly List<SheetReference> _sheetReferences = new();
        private byte[] _drawingGroup = [];
        private string[] _sharedStrings = [];
        private int _sheetOrdinal;
        private bool _uses1904Dates;

        private OfficeBinaryWorkbook(byte[] data, CancellationToken cancellationToken)
        {
            _data = data;
            _cancellationToken = cancellationToken;
            int offset = 0;
            Record first = ReadRecord(ref offset);
            int version = U16(first.Body(data), 0);
            if (first.Id != Bof || version is not (0x0500 or 0x0600))
            {
                throw new InvalidDataException("This Excel binary version is unsupported.");
            }

            _unicode = version == 0x0600;
            switch (U16(first.Body(data), 2))
            {
                case 0x0005:
                    ReadGlobals(offset);
                    break;
                case 0x0010:
                    _sheets.Add(new Sheet("Sheet 1", 0, 0));
                    break;
                default:
                    throw new InvalidDataException("Missing Excel workbook globals.");
            }
        }

        internal static byte[] ReadRaw(Stream stream)
        {
            if (stream.Length > 256 * 1024 * 1024) throw new InvalidDataException("The Excel binary stream exceeds 256 MiB.");
            byte[] data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            return data;
        }

        internal static void Write(ZipArchive archive, byte[] data, CancellationToken cancellationToken) =>
            new OfficeBinaryWorkbook(data, cancellationToken).Write(archive);

        private void Write(ZipArchive archive)
        {
            var sheetList = new XElement(X + "sheets");
            var relationships = new XElement(Relationships + "Relationships");
            var drawings = new OfficeBinaryWorkbookDrawing(archive, _drawingGroup, _cancellationToken);
            for (int i = 0; i < _sheets.Count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Sheet sheet = _sheets[i];
                string id = "rId" + (i + 1).ToString(CultureInfo.InvariantCulture);
                string path = "worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
                sheetList.Add(new XElement(X + "sheet", new XAttribute("name", sheet.Name),
                    new XAttribute("sheetId", i + 1), new XAttribute(RelationshipId + "id", id)));
                relationships.Add(Relationship(id, path, "worksheet"));
                WriteXml(archive, "xl/" + path, ReadWorksheet(sheet, drawings, i + 1));
            }

            WriteXml(archive, "xl/workbook.xml", new XElement(X + "workbook",
                new XElement(X + "workbookPr", new XAttribute("date1904", _uses1904Dates ? 1 : 0)), sheetList));
            WriteXml(archive, "xl/_rels/workbook.xml.rels", relationships);
            if (_sharedStrings.Length > 0)
            {
                WriteXml(archive, "xl/sharedStrings.xml", new XElement(X + "sst",
                    _sharedStrings.Select(value => new XElement(X + "si", TextElement(value)))));
            }
            WriteStyles(archive);
        }

        private void ReadGlobals(int offset)
        {
            var sheetRecords = new List<Record>();
            var formatRecords = new List<Record>();
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Record record = ReadRecord(ref offset);
                ReadOnlySpan<byte> data = record.Body(_data);
                switch (record.Id)
                {
                    case Eof:
                        foreach (Record sheet in sheetRecords) ReadSheetName(sheet.Body(_data));
                        foreach (Record format in formatRecords) ReadNumberFormat(format.Body(_data));
                        return;
                    case 0x002F: throw Encrypted(); // FILEPASS
                    case 0x0042:
                        _codePage = CodePagesEncodingProvider.Instance.GetEncoding(U16(data, 0)) ?? _codePage;
                        break;
                    case 0x0022: _uses1904Dates = U16(data, 0) != 0; break;
                    case 0x0085: sheetRecords.Add(record); break;
                    case 0x00FC: _sharedStrings = ReadSharedStrings(record, ref offset); break;
                    case 0x00EB:
                        using (var drawing = new MemoryStream())
                        {
                            CopySegments(drawing, record, ref offset, 0x00EB);
                            _drawingGroup = drawing.ToArray();
                        }
                        break;
                    case 0x01AE when _unicode:
                        _localBooks.Add(data.Length == 4 && U16(data, 2) == 0x0401);
                        break;
                    case 0x0017 when _unicode:
                        int references = U16(data, 0);
                        for (int i = 0; i < references; i++)
                        {
                            int entry = 2 + i * 6;
                            _sheetReferences.Add(new SheetReference(U16(data, entry), U16(data, entry + 2), U16(data, entry + 4)));
                        }
                        break;
                    case 0x0031:
                        if (_fonts.Count == 4) _fonts.Add(default); // The BIFF font index 4 is reserved.
                        _fonts.Add(new Font(U16(data, 4), U16(data, 6) >= 700, (U16(data, 2) & 2) != 0));
                        break;
                    case 0x041E: formatRecords.Add(record); break;
                    case 0x00E0:
                        _formats.Add(new Format(U16(data, 0), U16(data, 2),
                            _unicode && (U32(data, 14) >> 26) != 0 ? U16(data, 18) & 0x7F : -1));
                        break;
                    case 0x0092:
                        int count = U16(data, 0);
                        if (count > 56) throw new InvalidDataException("Invalid Excel color palette.");
                        _palette = (string[])DefaultPalette.Clone();
                        for (int i = 0; i < count; i++) _palette[i] = Convert.ToHexString(Slice(data, 2 + i * 4, 3));
                        break;
                }
            }
        }

        private void ReadSheetName(ReadOnlySpan<byte> data)
        {
            int ordinal = _sheetOrdinal++;
            // Charts, macros and VBA modules use different substreams.
            if (Slice(data, 0, 7)[5] != 0) return;
            int count = data[6];
            string name = _unicode ? ReadUnicode(data, 7, count) : _codePage.GetString(Slice(data, 7, count));
            _sheets.Add(new Sheet(XmlText(name), I32(data, 0), ordinal));
        }

        private void ReadNumberFormat(ReadOnlySpan<byte> data)
        {
            int index = U16(data, 0);
            _numberFormats[index] = XmlText(_unicode ? ReadUnicode(data, 4, U16(data, 2)) :
                _codePage.GetString(Slice(data, 3, data[2])));
        }

        private XElement ReadWorksheet(Sheet sheet, OfficeBinaryWorkbookDrawing drawings, int sheetIndex)
        {
            int offset = sheet.Offset;
            Record first = ReadRecord(ref offset);
            if (first.Id != Bof || U16(first.Body(_data), 2) != 0x0010)
            {
                throw new InvalidDataException("Invalid Excel worksheet substream.");
            }

            var rows = new SortedDictionary<int, SortedDictionary<int, XElement>>();
            using var drawing = new MemoryStream();
            var chartRecords = new List<ReadOnlyMemory<byte>>();
            XElement? pendingFormula = null;
            int nested = 0;
            int chartStart = -1;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int start = offset;
                Record record = ReadRecord(ref offset);
                if (record.Id == Bof)
                {
                    if (nested == 0 && U16(record.Body(_data), 2) == 0x0020) chartStart = start;
                    nested++;
                    continue;
                }
                if (record.Id == Eof)
                {
                    if (nested == 0) break;
                    nested--;
                    if (nested == 0 && chartStart >= 0)
                    {
                        chartRecords.Add(_data.AsMemory(chartStart, offset - chartStart));
                        chartStart = -1;
                    }
                    continue;
                }
                if (nested != 0) continue;

                ReadOnlySpan<byte> data = record.Body(_data);
                switch (record.Id)
                {
                    case 0x002F: throw Encrypted();
                    case 0x00EC: CopySegments(drawing, record, ref offset); break;
                    case 0x0203: AddCell(rows, data, Number(BitConverter.Int64BitsToDouble((long)U64(data, 6)))); break;
                    case 0x027E: AddCell(rows, data, Number(DecodeRk(U32(data, 6)))); break;
                    case 0x00BD: ReadMultipleCells(rows, data, 6); break; // MULRK
                    case 0x00BE: ReadMultipleCells(rows, data, 2); break; // MULBLANK
                    case 0x0201: AddCell(rows, data, string.Empty); break;
                    case 0x00FD:
                        uint index = U32(data, 6);
                        if (index >= _sharedStrings.Length) throw new InvalidDataException("Invalid Excel shared string index.");
                        AddCell(rows, data, index.ToString(CultureInfo.InvariantCulture), "s");
                        break;
                    case 0x0204:
                    case 0x00D6:
                        string value = _unicode ? ReadUnicode(data, 8, U16(data, 6)) :
                            _codePage.GetString(Slice(data, 8, U16(data, 6)));
                        AddCell(rows, data, XmlText(value), "inlineStr");
                        break;
                    case 0x0205:
                        _ = Slice(data, 6, 2);
                        if (data[7] != 0) AddCell(rows, data, ErrorValue(data[6]), "e");
                        else AddCell(rows, data, data[6] == 0 ? "0" : "1", "b");
                        break;
                    case 0x0006: // FORMULA stores its last calculated result.
                        pendingFormula = null;
                        if (U16(data, 12) != 0xFFFF)
                        {
                            AddCell(rows, data, Number(BitConverter.Int64BitsToDouble((long)U64(data, 6))));
                        }
                        else
                        {
                            switch (data[6])
                            {
                                case 0: pendingFormula = AddCell(rows, data, string.Empty, "inlineStr"); break;
                                case 1: AddCell(rows, data, data[8] == 0 ? "0" : "1", "b"); break;
                                case 2: AddCell(rows, data, ErrorValue(data[8]), "e"); break;
                                case 3: AddCell(rows, data, string.Empty, "inlineStr"); break;
                            }
                        }
                        break;
                    case 0x0207 when pendingFormula != null:
                        string result = _unicode ? ReadContinuedUnicode(record, ref offset) :
                            _codePage.GetString(Slice(data, 1, data[0]));
                        pendingFormula.Element(X + "is")!.Element(X + "t")!.Value = XmlText(result);
                        pendingFormula = null;
                        break;
                }
            }

            var worksheet = new XElement(X + "worksheet", new XElement(X + "sheetData",
                rows.Select(row => new XElement(X + "row", new XAttribute("r", row.Key + 1), row.Value.Values))));
            if (drawing.Length != 0 || chartRecords.Count > 0)
            {
                var charts = new List<XElement>();
                foreach (ReadOnlyMemory<byte> data in chartRecords)
                {
                    XElement? chart = OfficeBinaryWorkbookChart.Read(data.Span,
                        index => ReferencesSheet(index, sheet.Ordinal),
                        (row, col) => ChartCellValue(rows, row, col), _cancellationToken);
                    if (chart != null) charts.Add(chart);
                }
                if (drawings.WriteSheet(sheetIndex, drawing.ToArray(), charts))
                {
                    worksheet.Add(new XElement(X + "drawing", new XAttribute(RelationshipId + "id", "rDrawing")));
                }
            }
            return worksheet;
        }

        private bool ReferencesSheet(int index, int ordinal)
        {
            if ((uint)index >= _sheetReferences.Count) return false;
            SheetReference reference = _sheetReferences[index];
            return (uint)reference.Book < _localBooks.Count && _localBooks[reference.Book] &&
                reference.First == ordinal && reference.Last == ordinal;
        }

        private string? ChartCellValue(SortedDictionary<int, SortedDictionary<int, XElement>> rows, int row, int column)
        {
            if (!rows.TryGetValue(row, out SortedDictionary<int, XElement>? cells) ||
                !cells.TryGetValue(column, out XElement? cell)) return null;
            string? value = cell.Element(X + "v")?.Value ?? cell.Element(X + "is")?.Element(X + "t")?.Value;
            return (string?)cell.Attribute("t") == "s" && uint.TryParse(value, out uint index) && index < _sharedStrings.Length
                ? _sharedStrings[index] : value;
        }

        private void CopySegments(Stream destination, Record first, ref int offset, int repeatId = -1)
        {
            destination.Write(first.Body(_data));
            while (offset <= _data.Length - 4 &&
                (U16(_data, offset) == Continue || U16(_data, offset) == repeatId))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                destination.Write(ReadRecord(ref offset).Body(_data));
            }
        }

        private string ReadContinuedUnicode(Record first, ref int offset)
        {
            var reader = new StringSegments(_data, ReadSegments(first, ref offset));
            int count = (int)reader.ReadInteger(2);
            return reader.ReadCharacters(count, (reader.ReadInteger(1) & 1) != 0);
        }

        private string[] ReadSharedStrings(Record first, ref int offset)
        {
            List<Record> segments = ReadSegments(first, ref offset);
            var reader = new StringSegments(_data, segments);
            _ = reader.ReadInteger(4); // Total occurrences, including duplicates.
            uint count = reader.ReadInteger(4);
            if (count > segments.Sum(record => (long)record.Length) / 3)
            {
                throw new InvalidDataException("Invalid Excel shared string table.");
            }

            var strings = new string[count];
            for (int i = 0; i < strings.Length; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int length = (int)reader.ReadInteger(2);
                uint flags = reader.ReadInteger(1);
                uint runs = (flags & 8) != 0 ? reader.ReadInteger(2) : 0;
                uint phoneticSize = (flags & 4) != 0 ? reader.ReadInteger(4) : 0;
                strings[i] = reader.ReadCharacters(length, (flags & 1) != 0);
                reader.Skip(runs * 4L + phoneticSize);
            }

            return strings;
        }

        private List<Record> ReadSegments(Record first, ref int offset)
        {
            var segments = new List<Record> { first };
            while (offset <= _data.Length - 4 && U16(_data, offset) == Continue)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                segments.Add(ReadRecord(ref offset));
            }
            return segments;
        }

        private static XElement AddCell(SortedDictionary<int, SortedDictionary<int, XElement>> rows,
            ReadOnlySpan<byte> data, string value, string? type = null) =>
            AddCell(rows, U16(data, 0), U16(data, 2), U16(data, 4), value, type);

        private static XElement AddCell(SortedDictionary<int, SortedDictionary<int, XElement>> rows,
            int row, int col, int style, string value, string? type = null)
        {
            if (col > 255) throw new InvalidDataException("Invalid Excel column.");
            if (!rows.TryGetValue(row, out SortedDictionary<int, XElement>? cells))
            {
                rows.Add(row, cells = new SortedDictionary<int, XElement>());
            }

            string column = col < 26 ? ((char)('A' + col)).ToString() : string.Concat((char)('A' + col / 26 - 1), (char)('A' + col % 26));
            var cell = new XElement(X + "c", new XAttribute("r", column + (row + 1).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("s", style), type == null ? null : new XAttribute("t", type));
            if (type == "inlineStr") cell.Add(new XElement(X + "is", TextElement(value)));
            else if (value.Length != 0) cell.Add(new XElement(X + "v", value));
            cells[col] = cell;
            return cell;
        }

        private static void ReadMultipleCells(SortedDictionary<int, SortedDictionary<int, XElement>> rows,
            ReadOnlySpan<byte> data, int stride)
        {
            int row = U16(data, 0);
            int first = U16(data, 2);
            int last = U16(data, data.Length - 2);
            if (last < first || data.Length != 6 + (last - first + 1) * stride)
            {
                throw new InvalidDataException("Invalid Excel multiple-cell record.");
            }

            for (int col = first; col <= last; col++)
            {
                int offset = 4 + (col - first) * stride;
                AddCell(rows, row, col, U16(data, offset), stride == 6 ? Number(DecodeRk(U32(data, offset + 2))) : string.Empty);
            }
        }

        private void WriteStyles(ZipArchive archive)
        {
            var fills = new XElement(X + "fills", new XElement(X + "fill", new XElement(X + "patternFill", new XAttribute("patternType", "none"))));
            var fillIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var cellFormats = new XElement(X + "cellXfs");
            foreach (Format format in _formats)
            {
                int fillId = 0;
                if (PaletteColor(format.Fill) is string color && !fillIds.TryGetValue(color, out fillId))
                {
                    fillId = fillIds.Count + 1;
                    fillIds.Add(color, fillId);
                    fills.Add(new XElement(X + "fill", new XElement(X + "patternFill", new XAttribute("patternType", "solid"),
                        new XElement(X + "fgColor", new XAttribute("rgb", "FF" + color)))));
                }

                cellFormats.Add(new XElement(X + "xf", new XAttribute("fontId", format.Font),
                    new XAttribute("numFmtId", format.NumberFormat), new XAttribute("fillId", fillId)));
            }

            WriteXml(archive, "xl/styles.xml", new XElement(X + "styleSheet",
                new XElement(X + "numFmts", _numberFormats.Select(format => new XElement(X + "numFmt",
                    new XAttribute("numFmtId", format.Key), new XAttribute("formatCode", format.Value)))),
                new XElement(X + "fonts", _fonts.Select(font => new XElement(X + "font",
                    PaletteColor(font.Color) is string color ? new XElement(X + "color", new XAttribute("rgb", "FF" + color)) : null,
                    font.Bold ? new XElement(X + "b") : null, font.Italic ? new XElement(X + "i") : null))),
                fills, cellFormats));
        }

        private string? PaletteColor(int color) => color switch
        {
            >= 0 and < 8 => DefaultPalette[color],
            >= 8 and < 64 => _palette[color - 8],
            _ => null
        };

        private Record ReadRecord(ref int offset)
        {
            ReadOnlySpan<byte> header = Slice(_data, offset, 4);
            var result = new Record(U16(header, 0), offset + 4, U16(header, 2));
            _ = result.Body(_data);
            offset = result.Offset + result.Length;
            return result;
        }

        private static string ReadUnicode(ReadOnlySpan<byte> data, int offset, int count)
        {
            bool wide = (Slice(data, offset, 1)[0] & 1) != 0;
            return XmlText((wide ? Encoding.Unicode : Encoding.Latin1).GetString(Slice(data, offset + 1, count * (wide ? 2 : 1))));
        }

        private static double DecodeRk(uint value)
        {
            double number = (value & 2) != 0 ? (int)value >> 2 : BitConverter.Int64BitsToDouble((long)(value & 0xFFFFFFFC) << 32);
            return (value & 1) != 0 ? number / 100 : number;
        }

        private static string Number(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

        private static XElement TextElement(string value) =>
            new(X + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value);

        private static string ErrorValue(byte value) => value switch
        {
            0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?",
            0x24 => "#NUM!", 0x2A => "#N/A", 0x2B => "#GETTING_DATA", _ => "#VALUE!"
        };

        private readonly record struct Record(int Id, int Offset, int Length)
        {
            internal ReadOnlySpan<byte> Body(byte[] data) => Slice(data, Offset, Length);
        }

        private readonly record struct Sheet(string Name, int Offset, int Ordinal);
        private readonly record struct SheetReference(int Book, int First, int Last);
        private readonly record struct Font(int Color, bool Bold, bool Italic);
        private readonly record struct Format(int Font, int NumberFormat, int Fill);

        private sealed class StringSegments(byte[] data, List<Record> records)
        {
            private int _segment;
            private int _position;

            internal uint ReadInteger(int length)
            {
                uint value = 0;
                for (int i = 0; i < length; i++)
                {
                    while (_position >= records[_segment].Length) Next();
                    value |= (uint)data[records[_segment].Offset + _position++] << (8 * i);
                }
                return value;
            }

            internal string ReadCharacters(int count, bool wide)
            {
                var text = new StringBuilder(count);
                for (int i = 0; i < count; i++)
                {
                    while (_position == records[_segment].Length)
                    {
                        Next();
                        if (records[_segment].Length != 0) wide = (ReadInteger(1) & 1) != 0;
                    }
                    int width = wide ? 2 : 1;
                    if (records[_segment].Length - _position < width)
                    {
                        throw new InvalidDataException("A UTF-16 character crosses an Excel CONTINUE boundary.");
                    }
                    text.Append((char)ReadInteger(width));
                }
                return XmlText(text.ToString());
            }

            internal void Skip(long count)
            {
                while (count > 0)
                {
                    int available = records[_segment].Length - _position;
                    if (available == 0) { Next(); continue; }
                    int length = (int)Math.Min(count, available);
                    _position += length;
                    count -= length;
                }
            }

            private void Next()
            {
                if (++_segment >= records.Count) throw new InvalidDataException("Truncated Excel string data.");
                _position = 0;
            }
        }
    }
}
