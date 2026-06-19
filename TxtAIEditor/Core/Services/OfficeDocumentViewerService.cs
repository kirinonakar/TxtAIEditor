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
    public sealed class OfficeDocumentViewerService
    {
        private const long DefaultSlideWidthEmu = 9144000;
        private const long DefaultSlideHeightEmu = 5143500;
        private const double PresentationBaseWidthPx = 960;

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
            public bool Bold { get; init; }
            public bool Italic { get; init; }
        }

        private sealed class PresentationPlaceholderBounds
        {
            public string? Type { get; init; }
            public string? Index { get; init; }
            public string BoundsStyle { get; init; } = string.Empty;
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildPresentationHtmlAsync(filePath).ConfigureAwait(false);
            }

            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildWorkbookHtmlAsync(filePath).ConfigureAwait(false);
            }

            return BuildErrorHtml("지원하지 않는 Office 문서입니다.");
        }

        private static async Task<string> BuildPresentationHtmlAsync(string filePath)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            XDocument? presentation = await TryLoadXmlEntryAsync(archive, "ppt/presentation.xml").ConfigureAwait(false);
            if (presentation == null)
            {
                return BuildErrorHtml("PPTX 프레젠테이션 구조를 읽을 수 없습니다.");
            }

            (long slideWidth, long slideHeight) = ReadSlideSize(presentation);
            List<string> slidePaths = await ReadPresentationSlidePathsAsync(archive, presentation).ConfigureAwait(false);
            if (slidePaths.Count == 0)
            {
                return BuildErrorHtml("표시할 슬라이드가 없습니다.");
            }

            var slides = new StringBuilder();
            for (int i = 0; i < slidePaths.Count; i++)
            {
                ZipArchiveEntry? slideEntry = archive.GetEntry(slidePaths[i]);
                if (slideEntry == null)
                {
                    continue;
                }

                XDocument slide = await LoadXmlEntryAsync(slideEntry).ConfigureAwait(false);
                IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                    archive,
                    GetRelationshipsPath(slidePaths[i]),
                    Path.GetDirectoryName(slidePaths[i])?.Replace('\\', '/') ?? string.Empty).ConfigureAwait(false);
                IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds = await LoadSlidePlaceholderBoundsAsync(
                    archive,
                    relationships,
                    slideWidth,
                    slideHeight,
                    PresentationBaseWidthPx,
                    PresentationBaseWidthPx * slideHeight / Math.Max(1.0, slideWidth)).ConfigureAwait(false);

                double baseHeightPx = PresentationBaseWidthPx * slideHeight / Math.Max(1.0, slideWidth);
                string background = ReadSlideBackground(slide) ?? "#ffffff";
                slides.Append("<section class=\"slide\" style=\"--slide-ratio:")
                    .Append(FormatInvariant(slideWidth / (double)Math.Max(1, slideHeight)))
                    .Append(";--base-width:")
                    .Append(FormatInvariant(PresentationBaseWidthPx))
                    .Append(";--base-width-px:")
                    .Append(FormatInvariant(PresentationBaseWidthPx))
                    .Append("px;--base-height-px:")
                    .Append(FormatInvariant(baseHeightPx))
                    .Append("px")
                    .Append(";background:")
                    .Append(Html(background))
                    .Append("\">");
                slides.Append("<div class=\"slide-canvas\">");

                slides.Append("<div class=\"slide-number\">")
                    .Append(i + 1)
                    .Append(" / ")
                    .Append(slidePaths.Count)
                    .Append("</div>");

                foreach (string elementHtml in ReadSlideElements(archive, slide, relationships, slideWidth, slideHeight, PresentationBaseWidthPx, baseHeightPx, placeholderBounds))
                {
                    slides.Append(elementHtml);
                }

                slides.Append("</div>");
                slides.Append("</section>");
            }

            if (slides.Length == 0)
            {
                return BuildErrorHtml("표시할 슬라이드를 렌더링하지 못했습니다.");
            }

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
    --app-bg: #f3f4f6;
    --slide-shadow: 0 16px 44px rgba(15, 23, 42, .18);
    --text: #111827;
    --muted: #667085;
}
@media (prefers-color-scheme: dark) {
    :root {
        --app-bg: #17181c;
        --slide-shadow: 0 18px 44px rgba(0, 0, 0, .42);
        --text: #f3f4f6;
        --muted: #a6adbb;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: var(--app-bg); color: var(--text); font-family: "Segoe UI", Arial, sans-serif; }
body { padding: 28px 16px 40px; }
.deck { display: flex; flex-direction: column; align-items: center; gap: 26px; }
.slide {
    position: relative;
    width: min(1120px, calc(100vw - 32px));
    aspect-ratio: var(--slide-ratio);
    overflow: hidden;
    box-shadow: var(--slide-shadow);
    border: 1px solid rgba(148, 163, 184, .35);
}
.slide-canvas {
    position: absolute;
    inset: 0 auto auto 0;
    width: var(--base-width-px);
    height: var(--base-height-px);
    transform-origin: top left;
}
.slide-number {
    position: absolute;
    right: 12px;
    bottom: 9px;
    z-index: 10;
    color: var(--muted);
    font: 12px/1.2 "Segoe UI", Arial, sans-serif;
    background: rgba(255, 255, 255, .72);
    border-radius: 999px;
    padding: 4px 8px;
}
@media (prefers-color-scheme: dark) {
    .slide-number { background: rgba(17, 24, 39, .66); }
}
.ppt-shape, .ppt-image, .ppt-table {
    position: absolute;
    overflow: hidden;
}
.ppt-shape {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    color: #111827;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    padding: 4px 6px;
    line-height: 1.16;
}
.ppt-shape p { margin: 0 0 .24em; line-height: inherit; }
.ppt-shape p:last-child { margin-bottom: 0; }
.ppt-image img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    display: block;
}
.ppt-table table {
    width: 100%;
    height: 100%;
    border-collapse: collapse;
    table-layout: fixed;
    background: rgba(255, 255, 255, .88);
    color: #111827;
    font-size: clamp(9px, 1.35vw, 15px);
}
.ppt-table td {
    border: 1px solid rgba(31, 41, 55, .28);
    padding: .32em .45em;
    vertical-align: top;
    overflow-wrap: anywhere;
}
</style>
</head>
<body>
<main class="deck">
{{slides}}
</main>
<script>
function fitSlide(slide) {
    const canvas = slide.querySelector('.slide-canvas');
    if (!canvas) return;
    const baseWidth = parseFloat(getComputedStyle(slide).getPropertyValue('--base-width')) || 960;
    canvas.style.transform = `scale(${slide.clientWidth / baseWidth})`;
}
const observer = new ResizeObserver(entries => {
    for (const entry of entries) fitSlide(entry.target);
});
document.querySelectorAll('.slide').forEach(slide => {
    fitSlide(slide);
    observer.observe(slide);
});
</script>
</body>
</html>
""";
        }

        private static async Task<string> BuildWorkbookHtmlAsync(string filePath)
        {
            IReadOnlyList<ViewerWorkbookSheet> sheets = await ExtractWorkbookSheetsAsync(filePath).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return BuildErrorHtml("표시할 시트가 없습니다.");
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
}
.empty { padding: 28px; color: var(--muted); }
.truncated { color: var(--accent); }
</style>
</head>
<body>
<div class="toolbar">
    <select id="sheetSelect" aria-label="Sheet"></select>
    <span id="sheetMeta" class="meta"></span>
</div>
<div id="tableWrap" class="table-wrap"></div>
<script>
const sheets = {{sheetsJson}};
const maxRows = 5000;
const select = document.getElementById('sheetSelect');
const meta = document.getElementById('sheetMeta');
const wrap = document.getElementById('tableWrap');

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

function renderSheet(index) {
    const sheet = sheets[index];
    const rows = sheet.rows || [];
    const visibleRows = rows.slice(0, maxRows);
    const columnCount = Math.max(1, ...visibleRows.map(row => row.length));
    wrap.textContent = '';

    if (!rows.length) {
        wrap.appendChild(cell('div', '빈 시트입니다.', 'empty'));
        meta.textContent = '0 rows';
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
            applyCellStyle(td, sourceCell);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);

    meta.innerHTML = `${rows.length.toLocaleString()} rows x ${columnCount.toLocaleString()} columns` +
        (rows.length > maxRows ? ` <span class="truncated">first ${maxRows.toLocaleString()} shown</span>` : '');
}

sheets.forEach((sheet, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = sheet.name || `Sheet ${index + 1}`;
    select.appendChild(option);
});
select.addEventListener('change', () => renderSheet(Number(select.value || 0)));
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
                            Value = GetWorkbookCellText(cellElement, sharedStrings),
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
                ViewerCellStyle fontStyle = fontId >= 0 && fontId < fontStyles.Count
                    ? fontStyles[fontId]
                    : new ViewerCellStyle();

                result.Add(new ViewerCellStyle
                {
                    BackgroundColor = fillId >= 0 && fillId < fillColors.Count ? fillColors[fillId] : null,
                    TextColor = fontStyle.TextColor,
                    Bold = fontStyle.Bold,
                    Italic = fontStyle.Italic
                });
            }

            return result;
        }

        private static async Task<IReadOnlyList<string>> LoadWorkbookThemeColorsAsync(ZipArchive archive)
        {
            XDocument? theme = await TryLoadXmlEntryAsync(archive, "xl/theme/theme1.xml").ConfigureAwait(false);
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

        private static string GetWorkbookCellText(XElement cell, IReadOnlyList<string> sharedStrings)
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
                _ => rawValue
            };
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

        private static IEnumerable<string> ReadSlideElements(
            ZipArchive archive,
            XDocument slide,
            IReadOnlyDictionary<string, string> relationships,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds)
        {
            XElement? shapeTree = slide.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
            IEnumerable<XElement> elements = shapeTree?.Elements() ?? slide.Root?.Elements() ?? Enumerable.Empty<XElement>();

            foreach (XElement element in elements)
            {
                if (element.Name.LocalName == "pic")
                {
                    if (!TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, out string bounds))
                    {
                        continue;
                    }

                    string? relationshipId = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip")
                        ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
                    if (string.IsNullOrWhiteSpace(relationshipId) ||
                        !relationships.TryGetValue(relationshipId, out string? imagePath))
                    {
                        continue;
                    }

                    string? dataUri = TryReadImageDataUri(archive, imagePath);
                    if (string.IsNullOrEmpty(dataUri))
                    {
                        continue;
                    }

                    yield return "<div class=\"ppt-image\" style=\"" + bounds + "\"><img src=\"" + Html(dataUri) + "\"></div>";
                    continue;
                }

                if (element.Name.LocalName == "graphicFrame" && element.Descendants().Any(d => d.Name.LocalName == "tbl"))
                {
                    if (!TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, out string bounds))
                    {
                        continue;
                    }

                    XElement? table = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "tbl");
                    if (table == null)
                    {
                        continue;
                    }

                    string tableHtml = BuildTableHtml(table);
                    if (!string.IsNullOrWhiteSpace(tableHtml))
                    {
                        yield return "<div class=\"ppt-table\" style=\"" + bounds + "\">" + tableHtml + "</div>";
                    }

                    continue;
                }

                if (element.Name.LocalName == "sp" && element.Descendants().Any(d => d.Name.LocalName == "txBody"))
                {
                    string textHtml = BuildShapeTextHtml(element);
                    if (string.IsNullOrWhiteSpace(textHtml))
                    {
                        continue;
                    }

                    string bounds = TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, placeholderBounds, out string readBounds)
                        ? readBounds
                        : "left:48px;top:27px;width:864px;height:auto;";
                    string fill = ReadSolidFill(element.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr"));
                    string textStyle = ReadTextStyle(element, slideWidth, baseWidthPx);
                    yield return "<div class=\"ppt-shape\" style=\"" + bounds + fill + textStyle + "\">" + textHtml + "</div>";
                }
            }
        }

        private static string BuildShapeTextHtml(XElement shape)
        {
            var paragraphs = new StringBuilder();
            XElement? txBody = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (txBody == null)
            {
                return string.Empty;
            }

            foreach (XElement paragraph in txBody.Elements().Where(e => e.Name.LocalName == "p"))
            {
                string text = ReadParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                bool hasBullet = paragraph.Descendants().Any(e => e.Name.LocalName == "buChar" || e.Name.LocalName == "buAutoNum");
                paragraphs.Append("<p>");
                if (hasBullet)
                {
                    paragraphs.Append("• ");
                }

                paragraphs.Append(Html(text));
                paragraphs.Append("</p>");
            }

            return paragraphs.ToString();
        }

        private static string BuildTableHtml(XElement table)
        {
            var builder = new StringBuilder("<table><tbody>");
            foreach (XElement row in table.Descendants().Where(e => e.Name.LocalName == "tr"))
            {
                builder.Append("<tr>");
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                {
                    string text = string.Join("\n", cell.Descendants().Where(e => e.Name.LocalName == "p").Select(ReadParagraphText).Where(t => !string.IsNullOrWhiteSpace(t)));
                    builder.Append("<td>").Append(Html(text)).Append("</td>");
                }

                builder.Append("</tr>");
            }

            builder.Append("</tbody></table>");
            return builder.ToString();
        }

        private static string ReadParagraphText(XElement paragraph)
        {
            var builder = new StringBuilder();
            foreach (XElement element in paragraph.Descendants())
            {
                switch (element.Name.LocalName)
                {
                    case "t":
                        builder.Append(element.Value);
                        break;
                    case "tab":
                        builder.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        builder.Append('\n');
                        break;
                }
            }

            return builder.ToString();
        }

        private static string ReadTextStyle(XElement shape, long slideWidth, double baseWidthPx)
        {
            XElement? runProperties = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "rPr");
            var style = new StringBuilder();
            double pixelsPerInch = baseWidthPx / (slideWidth / 914400.0);
            if (runProperties?.Attribute("sz")?.Value is string sizeValue &&
                int.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) &&
                size > 0)
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(size / 100.0 / 72.0 * pixelsPerInch))
                    .Append("px;");
            }
            else
            {
                style.Append("font-size:")
                    .Append(FormatInvariant(18.0 / 72.0 * pixelsPerInch))
                    .Append("px;");
            }

            if (string.Equals(runProperties?.Attribute("b")?.Value, "1", StringComparison.Ordinal))
            {
                style.Append("font-weight:700;");
            }

            if (string.Equals(runProperties?.Attribute("i")?.Value, "1", StringComparison.Ordinal))
            {
                style.Append("font-style:italic;");
            }

            string? color = ReadSolidColor(runProperties);
            if (!string.IsNullOrWhiteSpace(color))
            {
                style.Append("color:").Append(color).Append(';');
            }

            return style.ToString();
        }

        private static string ReadSolidFill(XElement? parent)
        {
            string? fill = ReadSolidColor(parent);
            if (string.IsNullOrWhiteSpace(fill))
            {
                return string.Empty;
            }

            return "background:" + fill + ";";
        }

        private static string? ReadSlideBackground(XDocument slide)
        {
            XElement? bg = slide.Descendants().FirstOrDefault(e => e.Name.LocalName == "bg");
            return ReadSolidColor(bg);
        }

        private static async Task<IReadOnlyList<PresentationPlaceholderBounds>> LoadSlidePlaceholderBoundsAsync(
            ZipArchive archive,
            IReadOnlyDictionary<string, string> slideRelationships,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            string? layoutPath = slideRelationships.Values.FirstOrDefault(path =>
                path.Contains("/slideLayouts/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("slideLayouts/", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(layoutPath))
            {
                return Array.Empty<PresentationPlaceholderBounds>();
            }

            var result = new List<PresentationPlaceholderBounds>();
            XDocument? layout = await TryLoadXmlEntryAsync(archive, layoutPath).ConfigureAwait(false);
            if (layout != null)
            {
                result.AddRange(ReadPlaceholderBoundsFromPart(layout, slideWidth, slideHeight, baseWidthPx, baseHeightPx));
            }

            IReadOnlyDictionary<string, string> layoutRelationships = await LoadRelationshipsAsync(
                archive,
                GetRelationshipsPath(layoutPath),
                Path.GetDirectoryName(layoutPath)?.Replace('\\', '/') ?? string.Empty).ConfigureAwait(false);
            string? masterPath = layoutRelationships.Values.FirstOrDefault(path =>
                path.Contains("/slideMasters/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("slideMasters/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(masterPath))
            {
                XDocument? master = await TryLoadXmlEntryAsync(archive, masterPath).ConfigureAwait(false);
                if (master != null)
                {
                    result.AddRange(ReadPlaceholderBoundsFromPart(master, slideWidth, slideHeight, baseWidthPx, baseHeightPx));
                }
            }

            return result;
        }

        private static IEnumerable<PresentationPlaceholderBounds> ReadPlaceholderBoundsFromPart(
            XDocument part,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx)
        {
            XElement? shapeTree = part.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
            foreach (XElement element in shapeTree?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (!TryReadPlaceholderInfo(element, out string? type, out string? index) ||
                    !TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, out string bounds))
                {
                    continue;
                }

                yield return new PresentationPlaceholderBounds
                {
                    Type = type,
                    Index = index,
                    BoundsStyle = bounds
                };
            }
        }

        private static bool TryReadPlaceholderBounds(
            XElement element,
            IReadOnlyList<PresentationPlaceholderBounds> placeholderBounds,
            out string bounds)
        {
            bounds = string.Empty;
            if (!TryReadPlaceholderInfo(element, out string? type, out string? index))
            {
                return false;
            }

            PresentationPlaceholderBounds? match = null;
            if (!string.IsNullOrWhiteSpace(index))
            {
                match = placeholderBounds.FirstOrDefault(item =>
                    string.Equals(item.Index, index, StringComparison.OrdinalIgnoreCase));
            }

            match ??= placeholderBounds.FirstOrDefault(item =>
                PlaceholderTypesMatch(item.Type, type));
            if (match == null && string.IsNullOrWhiteSpace(type))
            {
                match = placeholderBounds.FirstOrDefault(item =>
                    PlaceholderTypesMatch(item.Type, "body"));
            }

            if (match == null)
            {
                return false;
            }

            bounds = match.BoundsStyle;
            return true;
        }

        private static bool TryReadPlaceholderInfo(XElement element, out string? type, out string? index)
        {
            XElement? placeholder = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
            type = placeholder?.Attribute("type")?.Value;
            index = placeholder?.Attribute("idx")?.Value;
            return placeholder != null;
        }

        private static bool PlaceholderTypesMatch(string? candidate, string? requested)
        {
            candidate = NormalizePlaceholderType(candidate);
            requested = NormalizePlaceholderType(requested);
            return !string.IsNullOrWhiteSpace(candidate) &&
                !string.IsNullOrWhiteSpace(requested) &&
                string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizePlaceholderType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "body";
            }

            return type switch
            {
                "ctrTitle" => "title",
                "subTitle" => "subtitle",
                "obj" => "body",
                _ => type
            };
        }

        private static string? ReadSolidColor(XElement? parent)
        {
            if (parent == null)
            {
                return null;
            }

            XElement? solidFill = parent.Descendants().FirstOrDefault(e => e.Name.LocalName == "solidFill");
            XElement? srgb = solidFill?.Descendants().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            string? value = srgb?.Attribute("val")?.Value;
            if (!string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$"))
            {
                return "#" + value;
            }

            return null;
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            out string bounds)
        {
            return TryReadBounds(element, slideWidth, slideHeight, baseWidthPx, baseHeightPx, null, out bounds);
        }

        private static bool TryReadBounds(
            XElement element,
            long slideWidth,
            long slideHeight,
            double baseWidthPx,
            double baseHeightPx,
            IReadOnlyList<PresentationPlaceholderBounds>? placeholderBounds,
            out string bounds)
        {
            bounds = string.Empty;
            XElement? xfrm = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "xfrm");
            XElement? off = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "off");
            XElement? ext = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
            if (off == null || ext == null ||
                !TryReadLong(off, "x", out long x) ||
                !TryReadLong(off, "y", out long y) ||
                !TryReadLong(ext, "cx", out long cx) ||
                !TryReadLong(ext, "cy", out long cy) ||
                cx <= 0 ||
                cy <= 0)
            {
                if (placeholderBounds != null && TryReadPlaceholderBounds(element, placeholderBounds, out string inheritedBounds))
                {
                    bounds = inheritedBounds;
                    return true;
                }

                return false;
            }

            bounds = "left:" + Pixels(x, slideWidth, baseWidthPx) +
                ";top:" + Pixels(y, slideHeight, baseHeightPx) +
                ";width:" + Pixels(cx, slideWidth, baseWidthPx) +
                ";height:" + Pixels(cy, slideHeight, baseHeightPx) + ";";
            return true;
        }

        private static bool TryReadLong(XElement element, string attributeName, out long value)
        {
            value = 0;
            return long.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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

        private static string Pixels(long value, long total, double baseSizePx)
        {
            return FormatInvariant(value / (double)Math.Max(1, total) * baseSizePx) + "px";
        }

        private static string? TryReadImageDataUri(ZipArchive archive, string imagePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(imagePath);
            if (entry == null || entry.Length <= 0 || entry.Length > 15_000_000)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };

            return "data:" + mime + ";base64," + Convert.ToBase64String(memory.ToArray());
        }

        private static (long Width, long Height) ReadSlideSize(XDocument presentation)
        {
            XElement? size = presentation.Descendants().FirstOrDefault(e => e.Name.LocalName == "sldSz");
            if (size != null &&
                TryReadLong(size, "cx", out long width) &&
                TryReadLong(size, "cy", out long height) &&
                width > 0 &&
                height > 0)
            {
                return (width, height);
            }

            return (DefaultSlideWidthEmu, DefaultSlideHeightEmu);
        }

        private static async Task<List<string>> ReadPresentationSlidePathsAsync(ZipArchive archive, XDocument presentation)
        {
            IReadOnlyDictionary<string, string> relationships = await LoadRelationshipsAsync(
                archive,
                "ppt/_rels/presentation.xml.rels",
                "ppt").ConfigureAwait(false);
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            return presentation.Descendants()
                .Where(e => e.Name.LocalName == "sldId")
                .Select(e => e.Attribute(relNs + "id")?.Value ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id) && relationships.ContainsKey(id))
                .Select(id => relationships[id])
                .ToList();
        }

        private static async Task<IReadOnlyDictionary<string, string>> LoadRelationshipsAsync(
            ZipArchive archive,
            string relationshipPath,
            string basePath)
        {
            ZipArchiveEntry? entry = archive.GetEntry(relationshipPath);
            if (entry == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument rels = await LoadXmlEntryAsync(entry).ConfigureAwait(false);
            return rels.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .Select(e => new
                {
                    Id = e.Attribute("Id")?.Value ?? string.Empty,
                    Target = NormalizeZipPath(basePath, e.Attribute("Target")?.Value ?? string.Empty)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Target))
                .ToDictionary(x => x.Id, x => x.Target, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetRelationshipsPath(string partPath)
        {
            string directory = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
            string fileName = Path.GetFileName(partPath);
            return string.IsNullOrEmpty(directory)
                ? "_rels/" + fileName + ".rels"
                : directory + "/_rels/" + fileName + ".rels";
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

        private static string FormatInvariant(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
