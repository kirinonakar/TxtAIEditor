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

using static TxtAIEditor.Core.Services.OfficeWorkbookCellFormatter;
using static TxtAIEditor.Core.Services.OfficeWorkbookChartSvgRenderer;
using static TxtAIEditor.Core.Services.OfficeWorkbookPackageUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWorkbookPackageReader
    {
        internal static async Task<IReadOnlyList<ViewerWorkbookSheet>> ReadAsync(string filePath)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyList<string> sharedStrings = await LoadWorkbookSharedStringsAsync(archive).ConfigureAwait(false);
            IReadOnlyList<string> themeColors = await LoadWorkbookThemeColorsAsync(archive).ConfigureAwait(false);
            IReadOnlyList<ViewerCellStyle> styles = await LoadWorkbookStylesAsync(archive, themeColors).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sheetNamesByPath = await LoadWorkbookSheetNamesByPathAsync(archive).ConfigureAwait(false);
            bool use1904Dates = await LoadWorkbookUses1904DatesAsync(archive).ConfigureAwait(false);

            var sheetEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            var sheets = new List<ViewerWorkbookSheet>();
            for (int sheetIndex = 0; sheetIndex < sheetEntries.Count; sheetIndex++)
            {
                ZipArchiveEntry sheetEntry = sheetEntries[sheetIndex];
                string sheetName = sheetNamesByPath.TryGetValue(sheetEntry.FullName, out string? mappedName)
                    ? mappedName
                    : $"Sheet {sheetIndex + 1}";

                var sheet = new ViewerWorkbookSheet { Name = sheetName };
                XDocument sheetDoc = await LoadXmlEntryAsync(sheetEntry).ConfigureAwait(false);
                int rowSequence = 0;
                foreach (XElement rowElement in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    int rowNumber = GetWorkbookRowIndex(rowElement, rowSequence + 1);
                    rowSequence = Math.Max(rowSequence + 1, rowNumber);
                    var row = new List<ViewerWorkbookCell>();
                    foreach (XElement cellElement in rowElement.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        int columnIndex = GetCellColumnIndex(cellElement);
                        if (columnIndex > 0)
                        {
                            while (row.Count < columnIndex - 1)
                            {
                                row.Add(new ViewerWorkbookCell());
                            }
                        }

                        ViewerCellStyle style = ReadWorkbookCellStyle(cellElement, styles);
                        var viewerCell = new ViewerWorkbookCell
                        {
                            Value = GetWorkbookCellText(cellElement, sharedStrings, style, use1904Dates),
                            BackgroundColor = style.BackgroundColor,
                            TextColor = style.TextColor,
                            Bold = style.Bold,
                            Italic = style.Italic
                        };
                        row.Add(viewerCell);
                        if (columnIndex > 0)
                        {
                            sheet.Cells[(rowNumber, columnIndex)] = viewerCell;
                        }
                    }

                    if (row.Any(cell =>
                        !string.IsNullOrWhiteSpace(cell.Value) ||
                        !string.IsNullOrWhiteSpace(cell.BackgroundColor) ||
                        !string.IsNullOrWhiteSpace(cell.TextColor)))
                    {
                        sheet.Rows.Add(row);
                    }
                }

                await LoadWorkbookObjectsAsync(
                    archive,
                    sheetEntry,
                    sheet,
                    sheetDoc,
                    themeColors,
                    use1904Dates).ConfigureAwait(false);

                sheets.Add(sheet);
            }

            return sheets;
        }

        private static async Task LoadWorkbookObjectsAsync(
            ZipArchive archive,
            ZipArchiveEntry sheetEntry,
            ViewerWorkbookSheet sheet,
            XDocument sheetDocument,
            IReadOnlyList<string> themeColors,
            bool use1904Dates)
        {
            string relationshipPath = OfficePresentationPackageReader.GetRelationshipsPath(sheetEntry.FullName);
            string basePath = Path.GetDirectoryName(sheetEntry.FullName)?.Replace('\\', '/') ?? string.Empty;
            IReadOnlyDictionary<string, string> relationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    relationshipPath,
                    basePath).ConfigureAwait(false);

            foreach (XElement tablePart in sheetDocument.Descendants().Where(e => e.Name.LocalName == "tablePart"))
            {
                string relationshipId = ReadWorkbookRelationshipId(tablePart);
                if (string.IsNullOrWhiteSpace(relationshipId) ||
                    !relationships.TryGetValue(relationshipId, out string? tablePath))
                {
                    continue;
                }

                ViewerWorkbookObject? table = await LoadWorkbookTableAsync(
                    archive,
                    tablePath,
                    sheet).ConfigureAwait(false);
                if (table != null)
                {
                    sheet.Objects.Add(table);
                }
            }

            XElement? drawingElement = sheetDocument.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "drawing");
            string drawingRelationshipId = drawingElement == null
                ? string.Empty
                : ReadWorkbookRelationshipId(drawingElement);
            if (!string.IsNullOrWhiteSpace(drawingRelationshipId) &&
                relationships.TryGetValue(drawingRelationshipId, out string? drawingPath))
            {
                await LoadWorkbookDrawingObjectsAsync(
                    archive,
                    drawingPath,
                    sheet,
                    themeColors,
                    use1904Dates).ConfigureAwait(false);
            }

            sheet.Objects.Sort((left, right) =>
            {
                int rowOrder = left.AnchorRow.CompareTo(right.AnchorRow);
                return rowOrder != 0
                    ? rowOrder
                    : left.AnchorColumn.CompareTo(right.AnchorColumn);
            });
        }

        private static async Task<ViewerWorkbookObject?> LoadWorkbookTableAsync(
            ZipArchive archive,
            string tablePath,
            ViewerWorkbookSheet sheet)
        {
            XDocument? tableDocument = await TryLoadXmlEntryAsync(archive, tablePath).ConfigureAwait(false);
            XElement? tableElement = tableDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "table");
            string reference = tableElement?.Attribute("ref")?.Value ?? string.Empty;
            if (tableElement == null ||
                !TryParseWorkbookRange(reference, out int startRow, out int startColumn, out int endRow, out int endColumn))
            {
                return null;
            }

            const int maxTableRows = 5000;
            const int maxTableColumns = 200;
            endRow = Math.Min(endRow, startRow + maxTableRows - 1);
            endColumn = Math.Min(endColumn, startColumn + maxTableColumns - 1);

            bool hasHeader = !IsWorkbookBooleanFalse(tableElement.Attribute("headerRowCount")?.Value);
            var result = new ViewerWorkbookObject
            {
                Kind = "table",
                Title = tableElement.Attribute("displayName")?.Value ??
                    tableElement.Attribute("name")?.Value ??
                    Path.GetFileNameWithoutExtension(tablePath),
                AnchorRow = startRow,
                AnchorColumn = startColumn,
                HasHeader = hasHeader
            };

            for (int rowIndex = startRow; rowIndex <= endRow; rowIndex++)
            {
                var row = new List<ViewerWorkbookCell>();
                for (int columnIndex = startColumn; columnIndex <= endColumn; columnIndex++)
                {
                    row.Add(sheet.Cells.TryGetValue((rowIndex, columnIndex), out ViewerWorkbookCell? cell)
                        ? cell
                        : new ViewerWorkbookCell());
                }

                result.Rows.Add(row);
            }

            return result;
        }

        private static async Task LoadWorkbookDrawingObjectsAsync(
            ZipArchive archive,
            string drawingPath,
            ViewerWorkbookSheet sheet,
            IReadOnlyList<string> themeColors,
            bool use1904Dates)
        {
            XDocument? drawingDocument = await TryLoadXmlEntryAsync(archive, drawingPath).ConfigureAwait(false);
            if (drawingDocument == null)
            {
                return;
            }

            IReadOnlyDictionary<string, string> relationships =
                await OfficePresentationPackageReader.LoadRelationshipsAsync(
                    archive,
                    OfficePresentationPackageReader.GetRelationshipsPath(drawingPath),
                    Path.GetDirectoryName(drawingPath)?.Replace('\\', '/') ?? string.Empty)
                    .ConfigureAwait(false);

            foreach (XElement anchor in drawingDocument.Descendants().Where(IsWorkbookDrawingAnchor))
            {
                (int row, int column) = ReadWorkbookAnchorPosition(anchor);
                (int width, int height) = ReadWorkbookAnchorSize(anchor);

                foreach (XElement chartElement in anchor.Descendants().Where(e => e.Name.LocalName == "chart"))
                {
                    string relationshipId = ReadWorkbookRelationshipId(chartElement);
                    if (string.IsNullOrWhiteSpace(relationshipId) ||
                        !relationships.TryGetValue(relationshipId, out string? chartPath))
                    {
                        continue;
                    }

                    string? svg = await BuildWorkbookChartSvgAsync(
                        archive,
                        chartPath,
                        themeColors,
                        use1904Dates).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(svg))
                    {
                        continue;
                    }

                    XDocument? chartDocument = await TryLoadXmlEntryAsync(archive, chartPath).ConfigureAwait(false);
                    sheet.Objects.Add(new ViewerWorkbookObject
                    {
                        Kind = "chart",
                        Title = ReadWorkbookChartTitle(chartDocument),
                        Svg = svg,
                        Width = width,
                        Height = height,
                        AnchorRow = row,
                        AnchorColumn = column
                    });
                }

                foreach (XElement picture in anchor.Descendants().Where(e => e.Name.LocalName == "pic"))
                {
                    string relationshipId = picture.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "blip")?
                        .Attributes()
                        .FirstOrDefault(attribute => attribute.Name.LocalName is "embed" or "link")
                        ?.Value ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(relationshipId) ||
                        !relationships.TryGetValue(relationshipId, out string? imagePath))
                    {
                        continue;
                    }

                    string? imageData;
                    try
                    {
                        imageData = OfficePresentationPackageReader.TryReadImageDataUri(archive, imagePath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(imageData))
                    {
                        continue;
                    }

                    sheet.Objects.Add(new ViewerWorkbookObject
                    {
                        Kind = "image",
                        Title = ReadWorkbookDrawingObjectTitle(picture),
                        ImageData = imageData,
                        Width = width,
                        Height = height,
                        AnchorRow = row,
                        AnchorColumn = column
                    });
                }
            }
        }

        private static string ReadWorkbookDrawingObjectTitle(XElement drawingObject)
        {
            XElement? nonVisualProperties = drawingObject.Descendants().FirstOrDefault(e => e.Name.LocalName == "cNvPr");
            return nonVisualProperties?.Attribute("descr")?.Value ??
                nonVisualProperties?.Attribute("name")?.Value ??
                string.Empty;
        }

        private static bool IsWorkbookDrawingAnchor(XElement element)
        {
            return element.Name.LocalName is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor";
        }

        private static (int Row, int Column) ReadWorkbookAnchorPosition(XElement anchor)
        {
            XElement? from = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "from");
            return (
                ReadWorkbookChildInt(from, "row"),
                ReadWorkbookChildInt(from, "col"));
        }

        private static (int Width, int Height) ReadWorkbookAnchorSize(XElement anchor)
        {
            XElement? extent = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
            if (extent == null)
            {
                extent = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "to");
            }

            long width = ReadWorkbookLongAttribute(extent, "cx");
            long height = ReadWorkbookLongAttribute(extent, "cy");
            return (EmuToPixels(width), EmuToPixels(height));
        }

        private static int ReadWorkbookChildInt(XElement? parent, string name)
        {
            return int.TryParse(
                parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                    ? Math.Max(0, value)
                    : 0;
        }

        private static long ReadWorkbookLongAttribute(XElement? element, string name)
        {
            return long.TryParse(
                element?.Attribute(name)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value)
                    ? Math.Max(0, value)
                    : 0;
        }

        private static int EmuToPixels(long emu)
        {
            return emu <= 0
                ? 0
                : Math.Clamp((int)Math.Round(emu / 9525.0), 1, 4000);
        }

        private static string ReadWorkbookRelationshipId(XElement element)
        {
            return element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "id")
                ?.Value ?? string.Empty;
        }

        private static bool IsWorkbookBooleanFalse(string? value)
        {
            return value is "0" or "false" or "off";
        }

        private static int GetWorkbookRowIndex(XElement row, int fallback)
        {
            return int.TryParse(
                row.Attribute("r")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value) && value > 0
                    ? value
                    : fallback;
        }

        private static bool TryParseWorkbookRange(
            string reference,
            out int startRow,
            out int startColumn,
            out int endRow,
            out int endColumn)
        {
            startRow = 0;
            startColumn = 0;
            endRow = 0;
            endColumn = 0;
            string[] parts = reference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Length > 2 ||
                !TryParseWorkbookCellReference(parts[0], out startRow, out startColumn))
            {
                return false;
            }

            if (parts.Length == 1)
            {
                endRow = startRow;
                endColumn = startColumn;
                return true;
            }

            if (!TryParseWorkbookCellReference(parts[1], out endRow, out endColumn))
            {
                return false;
            }

            if (endRow < startRow)
            {
                (startRow, endRow) = (endRow, startRow);
            }

            if (endColumn < startColumn)
            {
                (startColumn, endColumn) = (endColumn, startColumn);
            }

            return true;
        }

        private static bool TryParseWorkbookCellReference(string reference, out int row, out int column)
        {
            row = 0;
            column = 0;
            Match match = Regex.Match(reference.Trim(), @"^\$?([A-Za-z]+)\$?(\d+)$");
            if (!match.Success ||
                !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out row) ||
                row <= 0)
            {
                return false;
            }

            foreach (char character in match.Groups[1].Value)
            {
                column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
            }

            return column > 0;
        }

        private static async Task<IReadOnlyList<string>> LoadWorkbookSharedStringsAsync(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            XDocument doc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "si")
                .Select(item => string.Concat(item.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)))
                .ToList();
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadWorkbookSheetNamesByPathAsync(ZipArchive archive)
        {
            ZipArchiveEntry? workbookEntry = archive.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry == null || relsEntry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument workbook = await LoadXmlEntryAsync(workbookEntry).ConfigureAwait(false);
            XDocument rels = await LoadXmlEntryAsync(relsEntry).ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            var targetsById = rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath("xl", e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);

            var namesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
            {
                string name = sheet.Attribute("name")?.Value ?? string.Empty;
                string id = sheet.Attribute(relNs + "id")?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) &&
                    targetsById.TryGetValue(id, out string? targetPath))
                {
                    namesByPath[targetPath] = name;
                }
            }

            return namesByPath;
        }

        private static async Task<IReadOnlyList<ViewerCellStyle>> LoadWorkbookStylesAsync(
            ZipArchive archive,
            IReadOnlyList<string> themeColors)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/styles.xml");
            if (entry == null)
            {
                return Array.Empty<ViewerCellStyle>();
            }

            XDocument stylesDoc = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            IReadOnlyDictionary<int, string> numberFormats = LoadWorkbookNumberFormats(stylesDoc);
            var fontStyles = stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "fonts")
                ?.Elements().Where(e => e.Name.LocalName == "font")
                .Select(font => new ViewerCellStyle
                {
                    TextColor = ReadWorkbookColor(font.Elements().FirstOrDefault(e => e.Name.LocalName == "color"), themeColors),
                    Bold = font.Elements().Any(e => e.Name.LocalName == "b"),
                    Italic = font.Elements().Any(e => e.Name.LocalName == "i")
                })
                .ToList() ?? new List<ViewerCellStyle>();

            var fillColors = stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "fills")
                ?.Elements().Where(e => e.Name.LocalName == "fill")
                .Select(fill => ReadWorkbookFillColor(fill, themeColors))
                .ToList() ?? new List<string?>();

            var result = new List<ViewerCellStyle>();
            foreach (XElement xf in stylesDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs")?.Elements().Where(e => e.Name.LocalName == "xf") ?? Enumerable.Empty<XElement>())
            {
                int fillId = TryReadInt(xf, "fillId");
                int fontId = TryReadInt(xf, "fontId");
                int numberFormatId = TryReadInt(xf, "numFmtId");
                ViewerCellStyle fontStyle = fontId >= 0 && fontId < fontStyles.Count
                    ? fontStyles[fontId]
                    : new ViewerCellStyle();

                result.Add(new ViewerCellStyle
                {
                    BackgroundColor = fillId >= 0 && fillId < fillColors.Count ? fillColors[fillId] : null,
                    TextColor = fontStyle.TextColor,
                    NumberFormatId = numberFormatId,
                    NumberFormatCode = numberFormats.TryGetValue(numberFormatId, out string? numberFormatCode) ? numberFormatCode : null,
                    Bold = fontStyle.Bold,
                    Italic = fontStyle.Italic
                });
            }

            return result;
        }

        private static IReadOnlyDictionary<int, string> LoadWorkbookNumberFormats(XDocument stylesDoc)
        {
            var formats = new Dictionary<int, string>
            {
                [0] = "General",
                [1] = "0",
                [2] = "0.00",
                [3] = "#,##0",
                [4] = "#,##0.00",
                [9] = "0%",
                [10] = "0.00%",
                [11] = "0.00E+00",
                [12] = "# ?/?",
                [13] = "# ??/??",
                [14] = "m/d/yy",
                [15] = "d-mmm-yy",
                [16] = "d-mmm",
                [17] = "mmm-yy",
                [18] = "h:mm AM/PM",
                [19] = "h:mm:ss AM/PM",
                [20] = "h:mm",
                [21] = "h:mm:ss",
                [22] = "m/d/yy h:mm",
                [37] = "#,##0;(#,##0)",
                [38] = "#,##0;[Red](#,##0)",
                [39] = "#,##0.00;(#,##0.00)",
                [40] = "#,##0.00;[Red](#,##0.00)",
                [45] = "mm:ss",
                [46] = "[h]:mm:ss",
                [47] = "mm:ss.0",
                [48] = "##0.0E+0",
                [49] = "@"
            };

            foreach (XElement numFmt in stylesDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "numFmts")
                ?.Elements().Where(e => e.Name.LocalName == "numFmt") ?? Enumerable.Empty<XElement>())
            {
                int id = TryReadInt(numFmt, "numFmtId");
                string code = numFmt.Attribute("formatCode")?.Value ?? string.Empty;
                if (id >= 0 && !string.IsNullOrWhiteSpace(code))
                {
                    formats[id] = code;
                }
            }

            return formats;
        }

        private static async Task<bool> LoadWorkbookUses1904DatesAsync(ZipArchive archive)
        {
            XDocument? workbook = await TryLoadXmlEntryAsync(archive, "xl/workbook.xml").ConfigureAwait(false);
            XElement? workbookProperties = workbook?.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
            string value = workbookProperties?.Attribute("date1904")?.Value ?? string.Empty;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<IReadOnlyList<string>> LoadWorkbookThemeColorsAsync(ZipArchive archive)
        {
            return await LoadThemeColorsAsync(archive, "xl/theme/theme1.xml").ConfigureAwait(false);
        }


        private static async Task<IReadOnlyList<string>> LoadThemeColorsAsync(ZipArchive archive, string themePath)
        {
            XDocument? theme = await TryLoadXmlEntryAsync(archive, themePath).ConfigureAwait(false);
            XElement? clrScheme = theme?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
            if (clrScheme == null)
            {
                return Array.Empty<string>();
            }

            var order = new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
            var colors = new List<string>();
            foreach (string name in order)
            {
                XElement? item = clrScheme.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                string? color = item == null ? null : ReadThemeColor(item);
                colors.Add(color ?? "#000000");
            }

            return colors;
        }

        private static string? ReadThemeColor(XElement element)
        {
            XElement? srgb = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            if (!string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            XElement? sys = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "sysClr");
            value = sys?.Attribute("lastClr")?.Value;
            return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$")
                ? "#" + value
                : null;
        }

        private static string? ReadWorkbookFillColor(XElement fill, IReadOnlyList<string> themeColors)
        {
            XElement? pattern = fill.Descendants().FirstOrDefault(e => e.Name.LocalName == "patternFill");
            string? patternType = pattern?.Attribute("patternType")?.Value;
            if (pattern == null ||
                string.Equals(patternType, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ReadWorkbookColor(pattern.Elements().FirstOrDefault(e => e.Name.LocalName == "fgColor"), themeColors) ??
                ReadWorkbookColor(pattern.Elements().FirstOrDefault(e => e.Name.LocalName == "bgColor"), themeColors);
        }

        private static ViewerCellStyle ReadWorkbookCellStyle(XElement cell, IReadOnlyList<ViewerCellStyle> styles)
        {
            int styleIndex = TryReadInt(cell, "s");
            return styleIndex >= 0 && styleIndex < styles.Count
                ? styles[styleIndex]
                : new ViewerCellStyle();
        }

        private static string GetWorkbookCellText(
            XElement cell,
            IReadOnlyList<string> sharedStrings,
            ViewerCellStyle style,
            bool use1904Dates)
        {
            string type = cell.Attribute("t")?.Value ?? string.Empty;
            if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            }

            string rawValue = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(rawValue))
            {
                return string.Empty;
            }

            return type switch
            {
                "s" when int.TryParse(rawValue, out int index) && index >= 0 && index < sharedStrings.Count => sharedStrings[index],
                "b" => rawValue == "1" ? "TRUE" : "FALSE",
                "str" => rawValue,
                _ => FormatWorkbookCellValue(rawValue, style, use1904Dates)
            };
        }
    }
}
