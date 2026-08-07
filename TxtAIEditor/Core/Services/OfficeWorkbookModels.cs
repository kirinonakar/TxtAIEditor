using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal sealed class ViewerWorkbookSheet
    {
        public string Name { get; init; } = string.Empty;
        public List<List<ViewerWorkbookCell>> Rows { get; } = new();
        public Dictionary<(int Row, int Column), ViewerWorkbookCell> Cells { get; } = new();
        public List<ViewerWorkbookObject> Objects { get; } = new();
    }

    internal sealed class ViewerWorkbookCell
    {
        public string Value { get; init; } = string.Empty;
        public string? BackgroundColor { get; init; }
        public string? TextColor { get; init; }
        public bool Bold { get; init; }
        public bool Italic { get; init; }
    }

    internal sealed class ViewerCellStyle
    {
        public string? BackgroundColor { get; init; }
        public string? TextColor { get; init; }
        public int NumberFormatId { get; init; }
        public string? NumberFormatCode { get; init; }
        public bool Bold { get; init; }
        public bool Italic { get; init; }
    }

    internal sealed class ViewerWorkbookObject
    {
        public string Kind { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Svg { get; init; }
        public string? ImageData { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int AnchorRow { get; init; }
        public int AnchorColumn { get; init; }
        public bool HasHeader { get; init; }
        public List<List<ViewerWorkbookCell>> Rows { get; } = new();
    }

    internal sealed class ViewerChartSeries
    {
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<double?> XValues { get; init; } = Array.Empty<double?>();
        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
        public IReadOnlyList<double?> Values { get; init; } = Array.Empty<double?>();
    }
}
