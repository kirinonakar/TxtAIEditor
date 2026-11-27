using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeBinaryData;

namespace TxtAIEditor.Core.Services
{
    // MS-DOC row properties describe cells with absolute edges. Project each table onto a
    // common grid so a wide cell and the subdivided cells in adjacent rows stay aligned.
    internal static class OfficeBinaryWordTable
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly string[] Sides = ["top", "left", "bottom", "right"];

        internal static bool IsRowProperty(ushort code) => code is
            0xD608 or 0x9407 or 0x3403 or 0x3466 or 0xD605 or 0xD613 or >= 0xD61A and <= 0xD61D or
            0x5624 or 0x5625 or 0xD62B or 0xD62C or 0xD620 or 0xD62F or 0xD632 or 0xD634 or
            0xD612 or 0xD616 or 0xD60C or 0xD670 or 0xD671 or 0xD672;

        internal static void ApplyLayout(XElement table)
        {
            Row[] rows = table.Elements(W + "tr").Select(row => new Row(row)).ToArray();
            int[] grid = BuildGrid(rows);
            if (grid.Length > 1)
            {
                table.AddFirst(
                    new XElement(W + "tblPr", Measure("tblW", grid[^1] - grid[0]),
                        Measure("tblInd", grid[0]), new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed"))),
                    new XElement(W + "tblGrid", Enumerable.Range(1, grid.Length - 1)
                        .Select(i => new XElement(W + "gridCol", new XAttribute(W + "w", grid[i] - grid[i - 1])))));
            }

            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                rows[rowIndex].Apply(grid, rowIndex == 0, rowIndex == rows.Length - 1);
            }
        }

        private static int[] BuildGrid(Row[] rows)
        {
            if (rows.Length == 0 || rows[0].Edges.Length < 2) return [];
            var boundaries = new SortedSet<int>();
            foreach (Row row in rows)
            {
                if (row.Edges.Length < 2) return [];
                for (int i = 0; i < row.Edges.Length; i++)
                {
                    if (i > 0 && row.Edges[i] <= row.Edges[i - 1]) return [];
                    boundaries.Add(row.Edges[i]);
                }
                if (boundaries.Count > 512) return [];
            }
            return boundaries.ToArray();
        }

        private static XElement Measure(string name, int value) =>
            new(W + name, new XAttribute(W + "type", "dxa"), new XAttribute(W + "w", value));

        private static XElement Value(string name, object value) => new(W + name, new XAttribute(W + "val", value));

        internal sealed class RowProperties
        {
            internal List<(ushort Code, byte[] Value)> Operands { get; } = new();

            internal void Add(ushort code, ReadOnlySpan<byte> value) => Operands.Add((code, value.ToArray()));
        }

        private sealed class Row
        {
            private readonly XElement _row;
            private readonly Cell[] _cells;
            private readonly Border?[] _rowBorders = new Border?[6];
            private int _height;
            private bool _cantSplitLegacy;
            private bool? _cantSplit;

            internal int[] Edges { get; private set; } = [];

            internal Row(XElement row)
            {
                _row = row;
                _cells = row.Elements(W + "tc").Select(cell => new Cell(cell)).ToArray();
                if (row.Annotation<RowProperties>() is not { } properties) return;

                foreach ((ushort code, byte[] value) in properties.Operands)
                {
                    if (code == 0xD608) ReadCellDefinitions(value);
                    if (code == 0xD605) ReadRowBorders(value, 4);
                }
                foreach ((ushort code, byte[] value) in properties.Operands)
                {
                    if (code is 0xD612 or 0xD616 or 0xD60C) ReadShading(code, value);
                }
                foreach ((ushort code, byte[] value) in properties.Operands)
                {
                    ApplyProperty(code, value);
                }
            }

            private void ReadCellDefinitions(ReadOnlySpan<byte> data)
            {
                const int cellSize = 20; // TC80
                if (data.Length == 0 || data[0] is 0 or > 63 || data[0] != _cells.Length) return;
                int headerLength = 1 + 2 * (_cells.Length + 1);
                if (headerLength > data.Length) return;
                Edges = new int[_cells.Length + 1];
                for (int i = 0; i < Edges.Length; i++) Edges[i] = I16(data, 1 + 2 * i);

                int count = Math.Min(_cells.Length, (data.Length - headerLength) / cellSize);
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> definition = data.Slice(headerLength + i * cellSize, cellSize);
                    int flags = U16(definition, 0);
                    Cell cell = _cells[i];
                    cell.HorizontalMerge = (byte)(flags & 3);
                    cell.VerticalMerge = (byte)((flags >> 5) & 3);
                    cell.Alignment = (byte)((flags >> 7) & 3);
                    for (int side = 0; side < Sides.Length; side++)
                    {
                        ReadOnlySpan<byte> border = definition.Slice(4 + 4 * side, 4);
                        if (U32(border, 0) != 0) cell.Borders[side] = ReadBorder(border);
                    }
                }
            }

            private void ReadRowBorders(ReadOnlySpan<byte> data, int size)
            {
                if (data.Length < _rowBorders.Length * size) return;
                for (int i = 0; i < _rowBorders.Length; i++) _rowBorders[i] = ReadBorder(data.Slice(i * size, size));
            }

            private void ApplyProperty(ushort code, ReadOnlySpan<byte> data)
            {
                switch (code)
                {
                    case 0x9407: _height = I16(data, 0); break;
                    case 0x3403: _cantSplitLegacy = data[0] == 1; break;
                    case 0x3466: _cantSplit = data[0] == 1; break;
                    case 0xD613: ReadRowBorders(data, 8); break;
                    case >= 0xD61A and <= 0xD61D:
                        int side = code - 0xD61A;
                        for (int i = 0; i < _cells.Length && i < data.Length / 4; i++)
                        {
                            _cells[i].BorderColors[side] = ReadColor(I32(data, i * 4));
                        }
                        break;
                    case 0x5624 or 0x5625 when data.Length >= 2:
                        for (int i = data[0]; i < data[1] && i < _cells.Length; i++)
                        {
                            _cells[i].HorizontalMerge = code == 0x5625 ? (byte)0 : (byte)(i == data[0] ? 2 : 1);
                        }
                        break;
                    case 0xD62B when data.Length >= 2 && data[0] < _cells.Length:
                        _cells[data[0]].VerticalMerge = data[1];
                        break;
                    case 0xD62C when data.Length >= 3:
                        for (int i = data[0]; i < data[1] && i < _cells.Length; i++) _cells[i].Alignment = data[2];
                        break;
                    case 0xD620 or 0xD62F when data.Length >= (code == 0xD620 ? 7 : 11):
                        Border border = ReadBorder(data.Slice(3, code == 0xD620 ? 4 : 8));
                        for (int i = data[0]; i < data[1] && i < _cells.Length; i++)
                        {
                            for (int edge = 0; edge < Sides.Length; edge++)
                            {
                                if ((data[2] & (1 << edge)) != 0) _cells[i].Borders[edge] = border;
                            }
                        }
                        break;
                    case 0xD632 or 0xD634 when data.Length >= 6 && data[3] is 0 or 3:
                        int first = code == 0xD634 ? 0 : data[0];
                        int end = code == 0xD634 ? _cells.Length : Math.Min(data[1], _cells.Length);
                        int padding = data[3] == 0 ? 0 : U16(data, 4);
                        for (int i = first; i < end; i++)
                        {
                            for (int edge = 0; edge < Sides.Length; edge++)
                            {
                                if ((data[2] & (1 << edge)) != 0) _cells[i].Padding[edge] = padding;
                            }
                        }
                        break;
                    case 0xD670 or 0xD671 or 0xD672: ReadShading(code, data); break;
                }
            }

            private void ReadShading(ushort code, ReadOnlySpan<byte> data)
            {
                const int size = 10; // SHD
                int start = code switch { 0xD616 or 0xD671 => 22, 0xD60C or 0xD672 => 44, _ => 0 };
                for (int i = 0; i < data.Length / size && i + start < _cells.Length; i++)
                {
                    ReadOnlySpan<byte> shade = data.Slice(i * size, size);
                    int pattern = U16(shade, 8);
                    _cells[start + i].Fill = pattern == 0xFFFF ? null : ReadColor(I32(shade, pattern == 1 ? 0 : 4));
                }
            }

            internal void Apply(int[] grid, bool firstRow, bool lastRow)
            {
                var rowProperties = new XElement(W + "trPr");
                if (_cantSplit ?? _cantSplitLegacy) rowProperties.Add(new XElement(W + "cantSplit"));
                if (_height != 0)
                {
                    rowProperties.Add(new XElement(W + "trHeight",
                        new XAttribute(W + "val", Math.Abs(_height)), new XAttribute(W + "hRule", _height < 0 ? "exact" : "atLeast")));
                }
                if (grid.Length > 1)
                {
                    int before = Array.BinarySearch(grid, Edges[0]);
                    int after = grid.Length - 1 - Array.BinarySearch(grid, Edges[^1]);
                    if (before > 0) rowProperties.Add(Value("gridBefore", before));
                    if (after > 0) rowProperties.Add(Value("gridAfter", after));
                }
                if (rowProperties.HasElements) _row.AddFirst(rowProperties);

                for (int i = 0; i < _cells.Length; i++)
                {
                    Cell cell = _cells[i];
                    var properties = new XElement(W + "tcPr");
                    if (grid.Length > 1)
                    {
                        int span = Array.BinarySearch(grid, Edges[i + 1]) - Array.BinarySearch(grid, Edges[i]);
                        if (span > 1) properties.Add(Value("gridSpan", span));
                    }
                    if (cell.HorizontalMerge != 0) properties.Add(Value("hMerge", cell.HorizontalMerge == 1 ? "continue" : "restart"));
                    if (cell.VerticalMerge is 1 or 3) properties.Add(Value("vMerge", cell.VerticalMerge == 1 ? "continue" : "restart"));
                    if (cell.Alignment is 1 or 2) properties.Add(Value("vAlign", cell.Alignment == 1 ? "center" : "bottom"));
                    if (cell.Fill != null)
                    {
                        properties.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "fill", cell.Fill)));
                    }

                    int[] rowEdges = [firstRow ? 0 : 4, i == 0 ? 1 : 5, lastRow ? 2 : 4, i + 1 == _cells.Length ? 3 : 5];
                    var borders = new XElement(W + "tcBorders");
                    for (int edge = 0; edge < Sides.Length; edge++)
                    {
                        Border? border = cell.Borders[edge] ?? _rowBorders[rowEdges[edge]];
                        if (border != null) borders.Add(border.Value.ToElement(Sides[edge], cell.BorderColors[edge]));
                    }
                    if (borders.HasElements) properties.Add(borders);
                    if (Edges.Length > 0)
                    {
                        properties.Add(new XElement(W + "tcMar", Sides.Select((side, edge) => Measure(side, cell.Padding[edge]))));
                    }
                    if (properties.HasElements) cell.Element.AddFirst(properties);
                }
            }
        }

        private static Border ReadBorder(ReadOnlySpan<byte> bytes)
        {
            int styleOffset = bytes.Length == 8 ? 4 : 0;
            byte style = bytes[styleOffset + 1];
            string? color = bytes.Length == 8 ? ReadColor(I32(bytes, 0)) : OfficeBinaryWord.IndexedColor(bytes[2]);
            return new Border(style, Math.Max(2, (int)bytes[styleOffset]), color);
        }

        private static string? ReadColor(int value) => (value >> 24) == 0 ? OfficeBinaryWord.ColorRef(value) : null;

        private readonly record struct Border(byte Style, int Width, string? Color)
        {
            internal XElement ToElement(string name, string? colorOverride)
            {
                string style = Style switch
                {
                    0 or 255 => "nil", 3 => "double", 6 => "dotted", 7 => "dashed",
                    8 => "dotDash", 9 => "dotDotDash", _ => "single"
                };
                return new XElement(W + name, new XAttribute(W + "val", style), new XAttribute(W + "sz", Width),
                    new XAttribute(W + "color", colorOverride ?? Color ?? "auto"));
            }
        }

        private sealed class Cell(XElement element)
        {
            internal XElement Element { get; } = element;
            internal byte HorizontalMerge { get; set; }
            internal byte VerticalMerge { get; set; }
            internal byte Alignment { get; set; }
            internal string? Fill { get; set; }
            internal Border?[] Borders { get; } = new Border?[4];
            internal string?[] BorderColors { get; } = new string?[4];
            internal int[] Padding { get; } = [0, 108, 0, 108];
        }
    }
}
