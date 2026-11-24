using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeArtBinary;
using static TxtAIEditor.Core.Services.OfficeBinaryData;
using static TxtAIEditor.Core.Services.OfficeLegacyPackage;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeBinaryWorkbookDrawing(ZipArchive archive, byte[] group, CancellationToken cancellationToken)
    {
        private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        private readonly Dictionary<int, string?> _picturePaths = new();
        private Record[]? _pictures;

        internal bool WriteSheet(int sheetIndex, byte[] bytes, List<XElement> charts)
        {
            var drawing = new XElement(Xdr + "wsDr");
            var relationships = new XElement(Relationships + "Relationships");
            if (bytes.Length >= 8)
            {
                Record container = ReadRecord(bytes, 0, bytes.Length);
                if (container.Type == 0xF002) ReadPictures(bytes, container, drawing, relationships);
            }

            for (int i = 0; i < charts.Count; i++)
            {
                string id = $"rChart{i + 1}";
                string name = $"chart{sheetIndex}-{i + 1}.xml";
                WriteXml(archive, "xl/charts/" + name, charts[i]);
                relationships.Add(Relationship(id, "../charts/" + name, "chart"));
                drawing.Add(new XElement(Xdr + "absoluteAnchor",
                    new XElement(Xdr + "graphicFrame", new XElement(A + "graphic",
                        new XElement(A + "graphicData", new XElement(C + "chart", new XAttribute(RelationshipId + "id", id)))))));
            }
            if (!drawing.HasElements) return false;

            string path = "drawing" + sheetIndex + ".xml";
            WriteXml(archive, "xl/drawings/" + path, drawing);
            WriteXml(archive, "xl/drawings/_rels/" + path + ".rels", relationships);
            WriteXml(archive, $"xl/worksheets/_rels/sheet{sheetIndex}.xml.rels",
                new XElement(Relationships + "Relationships", Relationship("rDrawing", "../drawings/" + path, "drawing")));
            return true;
        }

        private void ReadPictures(byte[] bytes, Record container, XElement drawing, XElement relationships)
        {
            foreach (Record shape in ShapeContainers(bytes, container))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Record[] parts = shape.Children(bytes).ToArray();
                Record anchor = parts.FirstOrDefault(record => record.Type == 0xF010);
                if (anchor.Length < 18) continue;
                int id = (int)ReadShapeOptions(bytes, parts).GetValueOrDefault(260);
                if (id <= 0 || ResolvePicture(id) is not string path) continue;
                string relationshipId = "rPicture" + id;
                if (!relationships.Elements().Any(item => (string?)item.Attribute("Id") == relationshipId))
                {
                    relationships.Add(Relationship(relationshipId, "../media/" + path, "image"));
                }

                ReadOnlySpan<byte> position = anchor.Body(bytes);
                int col = U16(position, 2);
                int row = U16(position, 6);
                double columns = U16(position, 10) - col + (I16(position, 12) - I16(position, 4)) / 1024.0;
                double rows = U16(position, 14) - row + (I16(position, 16) - I16(position, 8)) / 256.0;
                // The existing sheet viewer uses a single grid style for all workbooks.
                long width = (long)(Math.Clamp(columns * 64, 1, 4096) * 9525);
                long height = (long)(Math.Clamp(rows * 20, 1, 4096) * 9525);
                drawing.Add(new XElement(Xdr + "oneCellAnchor",
                    new XElement(Xdr + "from", new XElement(Xdr + "col", col), new XElement(Xdr + "row", row)),
                    new XElement(Xdr + "ext", new XAttribute("cx", width), new XAttribute("cy", height)),
                    new XElement(Xdr + "pic", new XElement(Xdr + "blipFill",
                        new XElement(A + "blip", new XAttribute(RelationshipId + "embed", relationshipId))))));
            }
        }

        private string? ResolvePicture(int id)
        {
            if (_picturePaths.TryGetValue(id, out string? path)) return path;
            _pictures ??= ReadPictureStore();
            if (id > _pictures.Length) return null;

            Record record = _pictures[id - 1];
            Image? image = record.Type == 0xF007 ? ReadBse(group, record, null) : ReadBlip(group, record);
            if (image != null)
            {
                path = $"picture{id}.{image.Extension}";
                image.Write(archive, "xl/media/" + path);
            }
            return _picturePaths[id] = path;
        }

        private Record[] ReadPictureStore()
        {
            if (group.Length < 8) return [];
            Record container = ReadRecord(group, 0, group.Length);
            Record store = container.Children(group).FirstOrDefault(record => record.Type == 0xF001);
            return store.Children(group).ToArray();
        }
    }
}
