using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeArtBinary;
using static TxtAIEditor.Core.Services.OfficeBinaryData;
using static TxtAIEditor.Core.Services.OfficeLegacyPackage;

namespace TxtAIEditor.Core.Services
{
    // MS-PPT and MS-ODRAW. Resolve the current persist directory before reading slides.
    internal sealed class OfficeBinaryPresentation
    {
        private const int SlideList = 0x0FF0;
        private const int SlidePersist = 0x03F3;
        private const int Drawing = 0x040C;
        private const int ShapeContainer = 0xF004;
        private const int GroupContainer = 0xF003;
        private const int UnitsToEmu = 15875;
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

        private readonly ZipArchive _archive;
        private readonly OfficeCompoundFile _compound;
        private readonly byte[] _data;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<int, int> _persist = new();
        private readonly Dictionary<uint, Record> _masters = new();
        private readonly Dictionary<int, string> _fonts = new();
        private readonly List<Record> _imageEntries = new();
        private readonly Dictionary<int, string?> _imagePaths = new();
        private byte[]? _pictures;
        private int _width;
        private int _height;

        private OfficeBinaryPresentation(ZipArchive archive, OfficeCompoundFile compound, CancellationToken cancellationToken)
        {
            _archive = archive;
            _compound = compound;
            _cancellationToken = cancellationToken;
            _data = compound.ReadStream("PowerPoint Document");
        }

        internal static void Write(ZipArchive archive, OfficeCompoundFile compound, CancellationToken cancellationToken) =>
            new OfficeBinaryPresentation(archive, compound, cancellationToken).Write();

        private void Write()
        {
            byte[] current = _compound.ReadStream("Current User");
            if (U16(current, 2) != 0x0FF6) throw new InvalidDataException("Missing PowerPoint current user record.");
            if (U32(current, 12) == 0xF3D1C4DF) throw Encrypted();
            Record document = ResolvePersist(ReadPersistDirectory(I32(current, 16)), 0x03E8);
            Record size = document.Children(_data).FirstOrDefault(record => record.Type == 0x03E9);
            _width = I32(size.Body(_data), 0);
            _height = I32(size.Body(_data), 4);
            if (_width <= 0 || _height <= 0) throw new InvalidDataException("Invalid PowerPoint slide size.");

            Record mainList = default;
            foreach (Record record in document.Children(_data))
            {
                if (record.Type == SlideList && record.Instance == 0) mainList = record;
                if (record.Type == SlideList && record.Instance == 1) ReadMasters(record);
                if (record.Type == 0x040B) ReadImageEntries(record);
                if (record.Type == 0x03F2) ReadFonts(record);
            }

            var ids = new XElement(P + "sldIdLst");
            var relationships = new XElement(Relationships + "Relationships");
            int count = 0;
            foreach ((Record slide, List<TextSource> text) in ReadSlides(mainList))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                string id = "rId" + ++count;
                string path = "slides/slide" + count + ".xml";
                ids.Add(new XElement(P + "sldId", new XAttribute("id", (long)count + 255), new XAttribute(RelationshipId + "id", id)));
                relationships.Add(Relationship(id, path, "slide"));
                WriteSlide(slide, text, path);
            }

            WriteXml(_archive, "ppt/presentation.xml", new XElement(P + "presentation", ids,
                new XElement(P + "sldSz", new XAttribute("cx", (long)_width * UnitsToEmu), new XAttribute("cy", (long)_height * UnitsToEmu))));
            WriteXml(_archive, "ppt/_rels/presentation.xml.rels", relationships);
        }

        private int ReadPersistDirectory(int editOffset)
        {
            int documentId = 0;
            while (editOffset != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Record edit = ReadRecord(_data, editOffset, _data.Length);
                if (edit.Type != 0x0FF5) throw new InvalidDataException("Invalid PowerPoint user edit.");
                if (edit.Length >= 32) throw Encrypted();
                ReadOnlySpan<byte> body = edit.Body(_data);
                if (documentId == 0) documentId = I32(body, 16);
                Record directory = ReadRecord(_data, I32(body, 12), _data.Length);
                if (directory.Type != 0x1772) throw new InvalidDataException("Invalid PowerPoint persist directory.");
                ReadOnlySpan<byte> entries = directory.Body(_data);
                for (int offset = 0; offset < entries.Length;)
                {
                    uint header = U32(entries, offset);
                    offset += 4;
                    int start = (int)(header & 0xFFFFF);
                    int count = (int)(header >> 20);
                    for (int i = 0; i < count; i++, offset += 4)
                    {
                        // The newest version of each persist object wins.
                        _persist.TryAdd(start + i, I32(entries, offset));
                    }
                }

                int previous = I32(body, 8);
                if (previous < 0 || previous >= editOffset) throw new InvalidDataException("Invalid PowerPoint edit chain.");
                editOffset = previous;
            }
            return documentId;
        }

        private Record ResolvePersist(int id, int? type = null)
        {
            if (!_persist.TryGetValue(id, out int offset)) throw new InvalidDataException("Missing PowerPoint persist object.");
            Record record = ReadRecord(_data, offset, _data.Length);
            if (type.HasValue && record.Type != type.Value) throw new InvalidDataException("Invalid PowerPoint persist object.");
            return record;
        }

        private void ReadMasters(Record container)
        {
            foreach (Record child in container.Children(_data))
            {
                if (child.Type != SlidePersist) continue;
                ReadOnlySpan<byte> body = child.Body(_data);
                _masters[U32(body, 12)] = ResolvePersist(I32(body, 0));
            }
        }

        private void ReadFonts(Record environment)
        {
            Record collection = environment.Children(_data).FirstOrDefault(record => record.Type == 0x07D5);
            foreach (Record font in collection.Children(_data))
            {
                if (font.Type != 0x0FB7 || font.Length < 64) continue;
                string name = Encoding.Unicode.GetString(font.Body(_data)[..64]).Split('\0', 2)[0];
                if (name.Length > 0) _fonts[font.Instance] = XmlText(name);
            }
        }

        private void ReadImageEntries(Record drawingGroup)
        {
            Record drawing = drawingGroup.Children(_data).FirstOrDefault(record => record.Type == 0xF000);
            Record store = drawing.Children(_data).FirstOrDefault(record => record.Type == 0xF001);
            _imageEntries.AddRange(store.Children(_data).Where(record => record.Type == 0xF007));
        }

        private IEnumerable<(Record Slide, List<TextSource> Text)> ReadSlides(Record list)
        {
            Record? slide = null;
            var records = new List<Record>();
            foreach (Record child in list.Children(_data))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (child.Type != SlidePersist)
                {
                    records.Add(child);
                    continue;
                }
                if (slide.HasValue) yield return (slide.Value, ReadTextSources(records));
                records.Clear();
                slide = ResolvePersist(I32(child.Body(_data), 0), 0x03EE);
            }
            if (slide.HasValue) yield return (slide.Value, ReadTextSources(records));
        }

        private List<TextSource> ReadTextSources(IEnumerable<Record> records)
        {
            var result = new List<TextSource>();
            TextSource? current = null;
            foreach (Record record in records)
            {
                switch (record.Type)
                {
                    case 0x0F9F:
                        current = new TextSource(I32(record.Body(_data), 0));
                        result.Add(current);
                        break;
                    case 0x0FA0 when current != null:
                        current.Value = Encoding.Unicode.GetString(record.Body(_data));
                        break;
                    case 0x0FA8 when current != null:
                        current.Value = Encoding.Latin1.GetString(record.Body(_data));
                        break;
                    case 0x0FA1 when current != null:
                        current.Styles = record;
                        break;
                }
            }
            return result;
        }

        private void WriteSlide(Record slide, List<TextSource> text, string path)
        {
            Record[] children = slide.Children(_data).ToArray();
            Record slideAtom = children.FirstOrDefault(record => record.Type == 0x03EF);
            ReadOnlySpan<byte> slideInfo = slideAtom.Body(_data);
            uint masterId = U32(slideInfo, 12);
            int flags = U16(slideInfo, 20);
            _masters.TryGetValue(masterId, out Record master);
            string[] colors = ReadColors((flags & 2) != 0 && master.Length > 0 ? master : slide);
            var masterText = ReadMasterStyles(master, colors);
            var slideText = new OfficeBinaryPresentationText(_data, _fonts, colors, masterText);
            var relationships = new XElement(Relationships + "Relationships");
            var tree = new XElement(P + "spTree");
            string? background = null;
            if (master.Length > 0 && (flags & 5) != 0)
            {
                Record masterDrawing = master.Children(_data).FirstOrDefault(record => record.Type == Drawing);
                var masterTree = new XElement(P + "spTree");
                var masterContext = new SlideContext(masterTree, relationships, slideText, [], colors, IsMaster: true);
                ReadShapes(masterDrawing, masterContext);
                if ((flags & 1) != 0) tree.Add(masterTree.Elements());
                if ((flags & 4) != 0) background = masterContext.Background;
            }

            var context = new SlideContext(tree, relationships, slideText, text, colors, IsMaster: false);
            ReadShapes(children.FirstOrDefault(record => record.Type == Drawing), context);
            if ((flags & 4) == 0) background = context.Background;
            background ??= colors[0];
            int index = 0;
            foreach (TextSource source in text)
            {
                if (source.Used || source.Value.Length == 0) continue;
                int top = _height / 12 + index++ * (_height / 9);
                var bounds = new Bounds(_width / 14, top, _width * 13L / 14, top + _height / 7);
                tree.Add(new XElement(P + "sp", new XElement(P + "spPr", Transform(bounds, null, null)),
                    slideText.Build(source, null)));
            }

            WriteXml(_archive, "ppt/" + path, new XElement(P + "sld", new XElement(P + "cSld",
                new XElement(P + "bg", new XElement(P + "bgPr", SolidFill(background))), tree)));
            if (relationships.HasElements)
            {
                WriteXml(_archive, "ppt/slides/_rels/" + Path.GetFileName(path) + ".rels", relationships);
            }
        }

        private Dictionary<int, OfficeBinaryPresentationText.Style> ReadMasterStyles(Record master, string[] colors)
        {
            var styles = new Dictionary<int, OfficeBinaryPresentationText.Style>();
            var reader = new OfficeBinaryPresentationText(_data, _fonts, colors, styles);
            foreach (Record record in master.Children(_data))
            {
                if (record.Type == 0x0FA3 && reader.ReadMasterStyle(record) is { } style) styles[record.Instance] = style;
            }
            return styles;
        }

        private string[] ReadColors(Record slide)
        {
            Record color = slide.Children(_data).FirstOrDefault(record => record.Type == 0x07F0 && record.Instance == 1);
            if (color.Length < 32) color = slide.Children(_data).FirstOrDefault(record => record.Type == 0x07F0);
            string[] colors = ["FFFFFF", "000000", "808080", "000000", "4472C4", "4472C4", "0563C1", "954F72"];
            if (color.Length >= 32)
            {
                for (int i = 0; i < colors.Length; i++) colors[i] = Rgb(U32(color.Body(_data), i * 4));
            }
            return colors;
        }

        private void ReadShapes(Record drawing, SlideContext context)
        {
            Record[] parts = drawing.Children(_data).Where(record => record.Type == 0xF002)
                .SelectMany(record => record.Children(_data)).ToArray();
            foreach (Record record in parts)
            {
                if (record.Type == GroupContainer) AppendShapes(record, context.Tree, context, depth: 0);
                if (record.Type == ShapeContainer) _ = ReadShape(record, context);
            }
        }

        private void AppendShapes(Record group, XElement parent, SlideContext context, int depth)
        {
            if (depth > 64) throw new InvalidDataException("PowerPoint drawing is nested too deeply.");
            bool first = true;
            foreach (Record record in group.Children(_data))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (record.Type == GroupContainer)
                {
                    XElement? element = ReadGroup(record);
                    AppendShapes(record, element ?? parent, context, depth + 1);
                    if (element != null) parent.Add(element);
                }
                else if (record.Type == ShapeContainer && !first)
                {
                    XElement? shape = ReadShape(record, context);
                    if (shape != null) parent.Add(shape);
                }
                first = false;
            }
        }

        private XElement? ReadGroup(Record group)
        {
            Record first = group.Children(_data).FirstOrDefault(record => record.Type == ShapeContainer);
            Record[] parts = first.Children(_data).ToArray();
            Bounds? bounds = ReadAnchor(parts);
            Record coordinates = parts.FirstOrDefault(record => record.Type == 0xF009);
            if (bounds == null || coordinates.Length < 16) return null;
            ReadOnlySpan<byte> data = coordinates.Body(_data);
            var children = new Bounds(I32(data, 0), I32(data, 4), I32(data, 8), I32(data, 12));
            return new XElement(P + "grpSp", new XElement(P + "grpSpPr", Transform(bounds.Value, children, null)));
        }

        private XElement? ReadShape(Record container, SlideContext context)
        {
            Record[] parts = container.Children(_data).ToArray();
            Record shape = parts.FirstOrDefault(record => record.Type == 0xF00A);
            if (shape.Length < 8) return null;
            uint flags = U32(shape.Body(_data), 4);
            if ((flags & 8) != 0) return null; // Deleted drawing object.
            Dictionary<int, uint> options = ReadShapeOptions(_data, parts);
            string? fill = DrawingColor(options, 385, context.Colors);
            if ((flags & 0x400) != 0)
            {
                context.Background = fill;
                return null;
            }

            Bounds? bounds = ReadAnchor(parts);
            if (bounds == null) return null;
            XElement transform = Transform(bounds.Value, null, options);
            if ((flags & 0x40) != 0) transform.SetAttributeValue("flipH", 1);
            if ((flags & 0x80) != 0) transform.SetAttributeValue("flipV", 1);
            string? path = ResolveImage((int)options.GetValueOrDefault(260));
            if (path != null)
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!context.Relationships.Elements().Any(item => (string?)item.Attribute("Id") == id))
                {
                    context.Relationships.Add(Relationship(id, "../media/" + Path.GetFileName(path), "image"));
                }
                return new XElement(P + "pic", new XElement(P + "blipFill",
                    new XElement(A + "blip", new XAttribute(RelationshipId + "embed", id))), new XElement(P + "spPr", transform));
            }

            Record clientData = parts.FirstOrDefault(record => record.Type == 0xF011);
            bool placeholder = clientData.Children(_data).Any(record => record.Type == 0x0BC3);
            if (context.IsMaster && placeholder) return null;
            Record textbox = parts.FirstOrDefault(record => record.Type == 0xF00D);
            List<TextSource> inlineText = ReadTextSources(textbox.Children(_data));
            Record outline = textbox.Children(_data).FirstOrDefault(record => record.Type == 0x0F9E);
            if (outline.Length >= 4)
            {
                int index = I32(outline.Body(_data), 0);
                if ((uint)index < context.Text.Count)
                {
                    TextSource source = context.Text[index];
                    inlineText.Add(source);
                    source.Used = true;
                }
            }

            string geometry = Geometry(shape.Instance);
            bool filled = (options.GetValueOrDefault(447, fill != null ? 0x00000010u : 0u) & 0x10) != 0;
            bool outlined = (options.GetValueOrDefault(511, options.ContainsKey(448) || geometry == "line" ? 8u : 0u) & 8) != 0;
            XElement properties = new(P + "spPr", transform, new XElement(A + "prstGeom", new XAttribute("prst", geometry)),
                filled ? SolidFill(fill ?? context.Colors[4]) : new XElement(A + "noFill"),
                new XElement(A + "ln", new XAttribute("w", options.GetValueOrDefault(459, 12700u)),
                    outlined ? SolidFill(DrawingColor(options, 448, context.Colors) ?? context.Colors[1]) : new XElement(A + "noFill")));
            return new XElement(P + "sp", properties,
                inlineText.FirstOrDefault(source => source.Value.Length != 0) is { } text ? context.TextRenderer.Build(text, options) : null);
        }

        private string? ResolveImage(int id)
        {
            if (id < 1 || id > _imageEntries.Count) return null;
            if (_imagePaths.TryGetValue(id, out string? path)) return path;
            if (_compound.Contains("Pictures")) _pictures ??= _compound.ReadStream("Pictures");
            Image? image = ReadBse(_data, _imageEntries[id - 1], _pictures);
            if (image != null)
            {
                path = "ppt/media/rPicture" + id + "." + image.Extension;
                image.Write(_archive, path);
            }
            return _imagePaths[id] = path;
        }

        private Bounds? ReadAnchor(Record[] parts)
        {
            foreach (Record part in parts)
            {
                ReadOnlySpan<byte> data = part.Body(_data);
                if (part.Type == 0xF010 && part.Length == 8) return new Bounds(I16(data, 2), I16(data, 0), I16(data, 4), I16(data, 6));
                if (part.Type is 0xF00F or 0xF010 && part.Length >= 16) return new Bounds(I32(data, 0), I32(data, 4), I32(data, 8), I32(data, 12));
            }
            return null;
        }

        private static XElement Transform(Bounds bounds, Bounds? children, Dictionary<int, uint>? options)
        {
            var transform = new XElement(A + "xfrm", Position("off", bounds), Extents("ext", bounds));
            if (children is Bounds childBounds) transform.Add(Position("chOff", childBounds), Extents("chExt", childBounds));
            if (options != null && options.TryGetValue(4, out uint rotation))
            {
                transform.SetAttributeValue("rot", (long)(int)rotation * 60000 / 65536);
            }
            return transform;
        }

        private static XElement Position(string name, Bounds bounds) => new(A + name,
            new XAttribute("x", bounds.Left * UnitsToEmu), new XAttribute("y", bounds.Top * UnitsToEmu));
        private static XElement Extents(string name, Bounds bounds) => new(A + name,
            new XAttribute("cx", Math.Max(1, bounds.Right - bounds.Left) * UnitsToEmu),
            new XAttribute("cy", Math.Max(1, bounds.Bottom - bounds.Top) * UnitsToEmu));
        internal static XElement SolidFill(string color) => new(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color)));
        internal static string Rgb(uint value) => $"{value & 255:X2}{(value >> 8) & 255:X2}{(value >> 16) & 255:X2}";

        private static string? DrawingColor(Dictionary<int, uint> options, int key, string[] colors)
        {
            if (!options.TryGetValue(key, out uint value)) return null;
            if ((value >> 24) == 8) return (value & 255) < colors.Length ? colors[value & 255] : null;
            return (value >> 24) == 0 ? Rgb(value) : null;
        }

        private static string Geometry(int type) => type switch
        {
            2 => "roundRect", 3 => "ellipse", 4 => "diamond", 5 => "triangle", 6 => "rtTriangle", 7 => "parallelogram",
            8 => "trapezoid", 9 => "hexagon", 10 => "octagon", 11 => "plus", 12 => "star5",
            13 => "rightArrow", 20 or 32 => "line", 66 => "leftArrow", 67 => "downArrow", 68 => "upArrow",
            69 => "leftRightArrow", 70 => "upDownArrow", _ => "rect"
        };

        private readonly record struct Bounds(long Left, long Top, long Right, long Bottom);

        internal sealed class TextSource(int type)
        {
            internal int Type { get; } = type;
            internal string Value { get; set; } = string.Empty;
            internal Record Styles { get; set; }
            internal bool Used { get; set; }
        }

        private sealed record SlideContext(XElement Tree, XElement Relationships, OfficeBinaryPresentationText TextRenderer,
            List<TextSource> Text, string[] Colors, bool IsMaster)
        {
            internal string? Background { get; set; }
        }
    }
}
