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
    internal sealed class OfficeWorkbookDocumentHtmlRenderer
    {
        private sealed class ViewerWorkbookSheet
        {
            public string Name { get; init; } = string.Empty;
            public List<List<ViewerWorkbookCell>> Rows { get; } = new();
        }

        private sealed class ViewerWorkbookCell
        {
            public string Value { get; init; } = string.Empty;
            public string? BackgroundColor { get; init; }
            public string? TextColor { get; init; }
            public bool Bold { get; init; }
            public bool Italic { get; init; }
        }

        private sealed class ViewerCellStyle
        {
            public string? BackgroundColor { get; init; }
            public string? TextColor { get; init; }
            public int NumberFormatId { get; init; }
            public string? NumberFormatCode { get; init; }
            public bool Bold { get; init; }
            public bool Italic { get; init; }
        }


        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            IReadOnlyList<ViewerWorkbookSheet> sheets = await ExtractWorkbookSheetsAsync(filePath).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoSheets", "No sheets to display."));
            }

            var sheetPayload = sheets.Select(sheet => new
            {
                name = sheet.Name,
                rows = sheet.Rows.Select(row => row.Select(cell => new
                {
                    value = cell.Value,
                    backgroundColor = cell.BackgroundColor,
                    textColor = cell.TextColor,
                    bold = cell.Bold,
                    italic = cell.Italic
                }).ToArray()).ToArray()
            }).ToArray();
            string sheetsJson = JsonSerializer.Serialize(sheetPayload);
            string emptySheetTextJson = JsonSerializer.Serialize(getString("OfficeViewerEmptySheet", "Empty sheet."));
            string rowsTextJson = JsonSerializer.Serialize(getString("OfficeViewerRowsLabel", "rows"));
            string columnsTextJson = JsonSerializer.Serialize(getString("OfficeViewerColumnsLabel", "columns"));
            string firstShownTextJson = JsonSerializer.Serialize(getString("OfficeViewerFirstRowsShownFormat", "first {0} shown"));
            string sheetAriaLabelJson = JsonSerializer.Serialize(getString("OfficeViewerSheetSelectorLabel", "Sheet"));

            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(Path.GetFileName(filePath))}}</title>
<style>
:root {
    color-scheme: light dark;
    --bg: #f6f7f9;
    --panel: #ffffff;
    --line: #d8dee8;
    --line-strong: #b8c2d2;
    --text: #111827;
    --muted: #667085;
    --header: #eef2f7;
    --accent: #2563eb;
}
@media (prefers-color-scheme: dark) {
    :root {
        --bg: #15171b;
        --panel: #1f232a;
        --line: #343b47;
        --line-strong: #4b5563;
        --text: #f4f6fb;
        --muted: #aab2c0;
        --header: #2a3039;
        --accent: #60a5fa;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; height: 100%; background: var(--bg); color: var(--text); font-family: "Segoe UI", Arial, sans-serif; }
body { display: flex; flex-direction: column; }
.toolbar {
    position: sticky;
    top: 0;
    z-index: 20;
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 52px;
    padding: 10px 14px;
    background: color-mix(in srgb, var(--panel) 94%, transparent);
    border-bottom: 1px solid var(--line);
    backdrop-filter: blur(12px);
}
select {
    max-width: min(460px, 70vw);
    min-width: 190px;
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 7px 32px 7px 10px;
    color: var(--text);
    background: var(--panel);
    font: 14px/1.2 "Segoe UI", Arial, sans-serif;
}
.meta { color: var(--muted); font-size: 13px; white-space: nowrap; }
.table-wrap { flex: 1; overflow: auto; padding: 0; }
table {
    border-collapse: separate;
    border-spacing: 0;
    background: var(--panel);
    color: var(--text);
    box-shadow: 0 10px 26px rgba(15, 23, 42, .08);
}
th, td {
    border-right: 1px solid var(--line);
    border-bottom: 1px solid var(--line);
    min-width: 96px;
    max-width: 360px;
    height: 30px;
    padding: 6px 8px;
    text-align: left;
    vertical-align: top;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 13px;
}
th {
    position: sticky;
    top: 0;
    z-index: 2;
    background: var(--header);
    color: var(--muted);
    font-weight: 600;
}
th.row-header {
    left: 0;
    z-index: 3;
    min-width: 54px;
    width: 54px;
    text-align: right;
    user-select: none;
    -webkit-user-select: none;
}
td.row-header {
    position: sticky;
    left: 0;
    z-index: 1;
    min-width: 54px;
    width: 54px;
    text-align: right;
    color: var(--muted);
    background: var(--header);
    font-weight: 600;
    user-select: none;
    -webkit-user-select: none;
}
.empty { padding: 28px; color: var(--muted); }
.truncated { color: var(--accent); }
</style>
</head>
<body>
<div class="toolbar">
    <select id="sheetSelect"></select>
    <span id="sheetMeta" class="meta"></span>
</div>
<div id="tableWrap" class="table-wrap"></div>
<script>
const sheets = {{sheetsJson}};
const emptySheetText = {{emptySheetTextJson}};
const rowsText = {{rowsTextJson}};
const columnsText = {{columnsTextJson}};
const firstShownText = {{firstShownTextJson}};
const sheetAriaLabel = {{sheetAriaLabelJson}};
const maxRows = 5000;
const select = document.getElementById('sheetSelect');
const meta = document.getElementById('sheetMeta');
const wrap = document.getElementById('tableWrap');
select.setAttribute('aria-label', sheetAriaLabel);

function colName(index) {
    let n = index + 1;
    let name = '';
    while (n > 0) {
        n--;
        name = String.fromCharCode(65 + (n % 26)) + name;
        n = Math.floor(n / 26);
    }
    return name;
}

function cell(tag, text, className) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    el.textContent = text;
    return el;
}

function valueOf(cell) {
    return cell && typeof cell === 'object' ? (cell.value ?? '') : (cell ?? '');
}

function applyCellStyle(td, cell) {
    if (!cell || typeof cell !== 'object') return;
    if (cell.backgroundColor) td.style.backgroundColor = cell.backgroundColor;
    if (cell.textColor) td.style.color = cell.textColor;
    if (cell.bold) td.style.fontWeight = '700';
    if (cell.italic) td.style.fontStyle = 'italic';
}

function escapeCsvValue(value) {
    const text = String(value ?? '');
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function selectedCellsAsCsv() {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return '';

    const selectedCells = [];
    const cells = wrap.querySelectorAll('tbody td[data-csv-row][data-csv-column]');
    cells.forEach(td => {
        for (let i = 0; i < selection.rangeCount; i++) {
            const range = selection.getRangeAt(i);
            if (range.intersectsNode(td)) {
                selectedCells.push(td);
                break;
            }
        }
    });

    if (!selectedCells.length) return '';

    const rowIndexes = selectedCells.map(td => Number(td.dataset.csvRow));
    const columnIndexes = selectedCells.map(td => Number(td.dataset.csvColumn));
    const minRow = Math.min(...rowIndexes);
    const maxRow = Math.max(...rowIndexes);
    const minColumn = Math.min(...columnIndexes);
    const maxColumn = Math.max(...columnIndexes);

    const values = new Map();
    selectedCells.forEach(td => {
        values.set(`${td.dataset.csvRow}:${td.dataset.csvColumn}`, td.textContent ?? '');
    });

    const lines = [];
    for (let r = minRow; r <= maxRow; r++) {
        const line = [];
        for (let c = minColumn; c <= maxColumn; c++) {
            line.push(escapeCsvValue(values.get(`${r}:${c}`) ?? ''));
        }
        lines.push(line.join(','));
    }

    return lines.join('\r\n');
}

function renderSheet(index) {
    const sheet = sheets[index];
    const rows = sheet.rows || [];
    const visibleRows = rows.slice(0, maxRows);
    const columnCount = Math.max(1, ...visibleRows.map(row => row.length));
    wrap.textContent = '';

    if (!rows.length) {
        wrap.appendChild(cell('div', emptySheetText, 'empty'));
        meta.textContent = `0 ${rowsText}`;
        return;
    }

    const table = document.createElement('table');
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    headRow.appendChild(cell('th', '', 'row-header'));
    for (let c = 0; c < columnCount; c++) {
        headRow.appendChild(cell('th', colName(c)));
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    visibleRows.forEach((row, r) => {
        const tr = document.createElement('tr');
        tr.appendChild(cell('td', String(r + 1), 'row-header'));
        for (let c = 0; c < columnCount; c++) {
            const sourceCell = row[c] || null;
            const td = cell('td', valueOf(sourceCell));
            td.dataset.csvRow = String(r);
            td.dataset.csvColumn = String(c);
            applyCellStyle(td, sourceCell);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);

    const firstShown = firstShownText.replace('{0}', maxRows.toLocaleString());
    meta.innerHTML = `${rows.length.toLocaleString()} ${rowsText} x ${columnCount.toLocaleString()} ${columnsText}` +
        (rows.length > maxRows ? ` <span class="truncated">${firstShown}</span>` : '');
}

sheets.forEach((sheet, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = sheet.name || `Sheet ${index + 1}`;
    select.appendChild(option);
});
select.addEventListener('change', () => renderSheet(Number(select.value || 0)));
document.addEventListener('copy', event => {
    const csv = selectedCellsAsCsv();
    if (!csv) return;

    event.clipboardData?.setData('text/plain', csv);
    event.preventDefault();
});
renderSheet(0);
</script>
</body>
</html>
""";
        }

        private static async Task<IReadOnlyList<ViewerWorkbookSheet>> ExtractWorkbookSheetsAsync(string filePath)
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
                foreach (XElement rowElement in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
                {
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
                        row.Add(new ViewerWorkbookCell
                        {
                            Value = GetWorkbookCellText(cellElement, sharedStrings, style, use1904Dates),
                            BackgroundColor = style.BackgroundColor,
                            TextColor = style.TextColor,
                            Bold = style.Bold,
                            Italic = style.Italic
                        });
                    }

                    if (row.Any(cell =>
                        !string.IsNullOrWhiteSpace(cell.Value) ||
                        !string.IsNullOrWhiteSpace(cell.BackgroundColor) ||
                        !string.IsNullOrWhiteSpace(cell.TextColor)))
                    {
                        sheet.Rows.Add(row);
                    }
                }

                sheets.Add(sheet);
            }

            return sheets;
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

        private static string FormatWorkbookCellValue(string rawValue, ViewerCellStyle style, bool use1904Dates)
        {
            string formatCode = style.NumberFormatCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(formatCode) ||
                formatCode.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericValue))
            {
                return rawValue;
            }

            if (IsWorkbookDateFormat(formatCode) &&
                TryConvertExcelSerialDate(numericValue, use1904Dates, out DateTime dateTime))
            {
                return FormatWorkbookDateValue(dateTime, numericValue, formatCode);
            }

            return FormatWorkbookNumberValue(numericValue, formatCode, rawValue);
        }

        private static bool IsWorkbookDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[ymdhHsS]", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(cleaned, @"[0#?](?:\.[0#?]+)?\s*%?");
        }

        private static bool TryConvertExcelSerialDate(double serial, bool use1904Dates, out DateTime dateTime)
        {
            dateTime = default;
            if (double.IsNaN(serial) || double.IsInfinity(serial))
            {
                return false;
            }

            try
            {
                dateTime = use1904Dates
                    ? new DateTime(1904, 1, 1).AddDays(serial)
                    : new DateTime(1899, 12, 30).AddDays(serial);
                return dateTime.Year >= 1 && dateTime.Year <= 9999;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatWorkbookDateValue(DateTime dateTime, double serial, string formatCode)
        {
            if (ShouldUseIsoDateFormat(formatCode))
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            string section = SelectWorkbookFormatSection(formatCode, serial);
            section = CleanWorkbookFormatSection(section);
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string token = match.Value.Trim('[', ']');
                return token.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? token
                    : string.Empty;
            });

            string dotNetFormat = ConvertExcelDateFormatToDotNet(section);
            if (string.IsNullOrWhiteSpace(dotNetFormat))
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }

            try
            {
                return dateTime.ToString(dotNetFormat, CultureInfo.CurrentCulture);
            }
            catch
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static bool ShouldUseIsoDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[yd]", RegexOptions.IgnoreCase);
        }

        private static string ConvertExcelDateFormatToDotNet(string format)
        {
            var builder = new StringBuilder();
            bool hasAmPm = format.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("A/P", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < format.Length;)
            {
                char ch = format[i];
                if (ch == '"')
                {
                    int end = format.IndexOf('"', i + 1);
                    string literal = end > i ? format.Substring(i + 1, end - i - 1) : string.Empty;
                    AppendDateLiteral(builder, literal);
                    i = end > i ? end + 1 : format.Length;
                    continue;
                }

                if (ch == '\\')
                {
                    if (i + 1 < format.Length)
                    {
                        AppendDateLiteral(builder, format.Substring(i + 1, 1));
                    }

                    i += 2;
                    continue;
                }

                if (ch == '_' || ch == '*')
                {
                    i += 2;
                    continue;
                }

                string remaining = format.Substring(i);
                if (remaining.StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 5;
                    continue;
                }

                if (remaining.StartsWith("A/P", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 3;
                    continue;
                }

                int runLength = CountRepeatedDateFormatChars(format, i);
                char lower = char.ToLowerInvariant(ch);
                switch (lower)
                {
                    case 'y':
                        builder.Append(runLength <= 2 ? "yy" : "yyyy");
                        i += runLength;
                        break;
                    case 'd':
                        builder.Append(runLength switch
                        {
                            1 => "d",
                            2 => "dd",
                            3 => "ddd",
                            _ => "dddd"
                        });
                        i += runLength;
                        break;
                    case 'h':
                        builder.Append(runLength <= 1 ? (hasAmPm ? "h" : "H") : (hasAmPm ? "hh" : "HH"));
                        i += runLength;
                        break;
                    case 's':
                        builder.Append(runLength <= 1 ? "s" : "ss");
                        i += runLength;
                        break;
                    case 'm':
                        bool minute = IsMinuteToken(format, i);
                        builder.Append(minute
                            ? (runLength <= 1 ? "m" : "mm")
                            : runLength switch
                            {
                                1 => "M",
                                2 => "MM",
                                3 => "MMM",
                                _ => "MMMM"
                            });
                        i += runLength;
                        break;
                    default:
                        AppendDateLiteral(builder, ch.ToString());
                        i++;
                        break;
                }
            }

            return builder.ToString();
        }

        private static int CountRepeatedDateFormatChars(string format, int start)
        {
            char ch = char.ToLowerInvariant(format[start]);
            int count = 0;
            while (start + count < format.Length &&
                char.ToLowerInvariant(format[start + count]) == ch)
            {
                count++;
            }

            return count;
        }

        private static bool IsMinuteToken(string format, int index)
        {
            int previous = FindPreviousDateFormatToken(format, index);
            int next = FindNextDateFormatToken(format, index);
            return (previous >= 0 && "hHsS".IndexOf(format[previous]) >= 0) ||
                (next >= 0 && "hHsS".IndexOf(format[next]) >= 0);
        }

        private static int FindPreviousDateFormatToken(string format, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static int FindNextDateFormatToken(string format, int index)
        {
            for (int i = index + 1; i < format.Length; i++)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static void AppendDateLiteral(StringBuilder builder, string literal)
        {
            foreach (char ch in literal)
            {
                if (ch == '\'')
                {
                    builder.Append("''");
                }
                else if (char.IsLetter(ch))
                {
                    builder.Append('\'').Append(ch).Append('\'');
                }
                else
                {
                    builder.Append(ch);
                }
            }
        }

        private static string FormatWorkbookNumberValue(double numericValue, string formatCode, string rawValue)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            bool usesNegativeSection = numericValue < 0 && sections.Count > 1;
            string section = CleanWorkbookFormatSection(SelectWorkbookFormatSection(formatCode, numericValue));
            if (string.IsNullOrWhiteSpace(section) ||
                section.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                section.Contains("/", StringComparison.Ordinal) && section.Contains("?", StringComparison.Ordinal))
            {
                return rawValue;
            }

            try
            {
                double valueToFormat = usesNegativeSection ? Math.Abs(numericValue) : numericValue;
                return valueToFormat.ToString(section, CultureInfo.CurrentCulture);
            }
            catch
            {
                return rawValue;
            }
        }

        private static string SelectWorkbookFormatSection(string formatCode, double value)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            if (sections.Count == 0)
            {
                return formatCode;
            }

            if (sections.Count == 1)
            {
                return sections[0];
            }

            if (value > 0)
            {
                return sections[0];
            }

            if (value < 0)
            {
                return sections.Count > 1 ? sections[1] : sections[0];
            }

            return sections.Count > 2 ? sections[2] : sections[0];
        }

        private static List<string> SplitWorkbookFormatSections(string formatCode)
        {
            var sections = new List<string>();
            var current = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    current.Append(ch);
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    current.Append(ch);
                    continue;
                }

                if (ch == ';' && !inQuote)
                {
                    sections.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            sections.Add(current.ToString());
            return sections;
        }

        private static string CleanWorkbookFormatSection(string section)
        {
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string value = match.Value.Trim('[', ']');
                return value.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? match.Value
                    : string.Empty;
            });
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = section.Replace("_-", string.Empty, StringComparison.Ordinal)
                .Replace("_)", string.Empty, StringComparison.Ordinal)
                .Replace("_(", string.Empty, StringComparison.Ordinal)
                .Replace("_ ", string.Empty, StringComparison.Ordinal);
            section = Regex.Replace(section, @"_.", string.Empty);
            section = Regex.Replace(section, @"\*.", string.Empty);
            return section.Trim();
        }

        private static string RemoveWorkbookFormatLiterals(string formatCode)
        {
            var builder = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (!inQuote)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static string? ReadWorkbookColor(XElement? colorElement, IReadOnlyList<string> themeColors)
        {
            if (colorElement == null)
            {
                return null;
            }

            string? rgb = colorElement.Attribute("rgb")?.Value;
            if (!string.IsNullOrWhiteSpace(rgb))
            {
                rgb = rgb.Trim();
                if (rgb.Length == 8)
                {
                    rgb = rgb.Substring(2);
                }

                if (Regex.IsMatch(rgb, "^[0-9A-Fa-f]{6}$"))
                {
                    return "#" + rgb;
                }
            }

            if (int.TryParse(colorElement.Attribute("indexed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexed))
            {
                return IndexedWorkbookColor(indexed);
            }

            if (int.TryParse(colorElement.Attribute("theme")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int themeIndex) &&
                themeIndex >= 0 &&
                themeIndex < themeColors.Count)
            {
                double tint = 0;
                _ = double.TryParse(colorElement.Attribute("tint")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tint);
                return ApplyTint(themeColors[themeIndex], tint);
            }

            return null;
        }

        private static string? IndexedWorkbookColor(int index)
        {
            string[] colors =
            {
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
                "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#808080",
                "#9999FF", "#993366", "#FFFFCC", "#CCFFFF", "#660066", "#FF8080", "#0066CC", "#CCCCFF",
                "#000080", "#FF00FF", "#FFFF00", "#00FFFF", "#800080", "#800000", "#008080", "#0000FF",
                "#00CCFF", "#CCFFFF", "#CCFFCC", "#FFFF99", "#99CCFF", "#FF99CC", "#CC99FF", "#FFCC99",
                "#3366FF", "#33CCCC", "#99CC00", "#FFCC00", "#FF9900", "#FF6600", "#666699", "#969696",
                "#003366", "#339966", "#003300", "#333300", "#993300", "#993366", "#333399", "#333333"
            };

            return index >= 0 && index < colors.Length ? colors[index] : null;
        }

        private static string ApplyTint(string hex, double tint)
        {
            if (string.IsNullOrEmpty(hex) || !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
            {
                return hex ?? "#000000";
            }

            string normalized = hex;
            int r = Convert.ToInt32(normalized.Substring(1, 2), 16);
            int g = Convert.ToInt32(normalized.Substring(3, 2), 16);
            int b = Convert.ToInt32(normalized.Substring(5, 2), 16);
            r = ApplyTintComponent(r, tint);
            g = ApplyTintComponent(g, tint);
            b = ApplyTintComponent(b, tint);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static int ApplyTintComponent(int value, double tint)
        {
            double adjusted = tint < 0
                ? value * (1 + tint)
                : value + (255 - value) * tint;
            return Math.Max(0, Math.Min(255, (int)Math.Round(adjusted)));
        }

        private static int GetCellColumnIndex(XElement cell)
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return 0;
            }

            int column = 0;
            foreach (char ch in reference)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    column = (column * 26) + (ch - 'A' + 1);
                    continue;
                }

                if (ch >= 'a' && ch <= 'z')
                {
                    column = (column * 26) + (ch - 'a' + 1);
                    continue;
                }

                break;
            }

            return column;
        }

        private static int TryReadInt(XElement element, string attributeName)
        {
            return int.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : -1;
        }

        private static int GetTrailingNumber(string value)
        {
            Match match = Regex.Match(value, @"(\d+)(?=\.[^.]+$)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        private static string NormalizeZipPath(string basePath, string target)
        {
            if (string.IsNullOrWhiteSpace(target) ||
                target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string combined = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : $"{basePath.TrimEnd('/')}/{target}";
            var parts = new List<string>();
            foreach (string part in combined.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    continue;
                }

                parts.Add(part);
            }

            return string.Join("/", parts);
        }


        private static async Task<ZipArchive> OpenArchiveAsync(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        private static async Task<XDocument?> TryLoadXmlEntryAsync(ZipArchive archive, string path)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path);
            return entry == null ? null : await LoadXmlEntryAsync(entry).ConfigureAwait(false);
        }

        private static async Task<XDocument> LoadXmlEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() => XDocument.Load(stream)).ConfigureAwait(false);
        }

        private static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

    }
}
