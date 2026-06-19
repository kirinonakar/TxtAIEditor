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

                string background = ReadSlideBackground(slide) ?? "#ffffff";
                slides.Append("<section class=\"slide\" style=\"--slide-ratio:")
                    .Append(FormatInvariant(slideWidth / (double)Math.Max(1, slideHeight)))
                    .Append(";background:")
                    .Append(Html(background))
                    .Append("\">");
                slides.Append("<div class=\"slide-number\">")
                    .Append(i + 1)
                    .Append(" / ")
                    .Append(slidePaths.Count)
                    .Append("</div>");

                foreach (string elementHtml in ReadSlideElements(archive, slide, relationships, slideWidth, slideHeight))
                {
                    slides.Append(elementHtml);
                }

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
    align-items: flex-start;
    color: #111827;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    padding: .45em .55em;
}
.ppt-shape p { margin: 0 0 .25em; }
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
</body>
</html>
""";
        }

        private static async Task<string> BuildWorkbookHtmlAsync(string filePath)
        {
            IReadOnlyList<ExtractedSpreadsheetSheet> sheets =
                await DocumentTextExtractionService.ExtractXlsxSheetsAsync(filePath, 50_000_000).ConfigureAwait(false);
            if (sheets.Count == 0)
            {
                return BuildErrorHtml("표시할 시트가 없습니다.");
            }

            var sheetPayload = sheets.Select(sheet => new
            {
                name = string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet {sheet.Index}" : sheet.Name,
                csv = sheet.CsvContent ?? string.Empty
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
.table-wrap { flex: 1; overflow: auto; padding: 14px; }
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

function parseCsv(text) {
    const rows = [];
    let row = [];
    let value = '';
    let quoted = false;
    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (quoted) {
            if (ch === '"') {
                if (text[i + 1] === '"') {
                    value += '"';
                    i++;
                } else {
                    quoted = false;
                }
            } else {
                value += ch;
            }
            continue;
        }
        if (ch === '"') {
            quoted = true;
        } else if (ch === ',') {
            row.push(value);
            value = '';
        } else if (ch === '\n') {
            row.push(value);
            rows.push(row);
            row = [];
            value = '';
        } else if (ch !== '\r') {
            value += ch;
        }
    }
    if (value.length || row.length) {
        row.push(value);
        rows.push(row);
    }
    return rows;
}

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

function renderSheet(index) {
    const sheet = sheets[index];
    const rows = parseCsv(sheet.csv || '');
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
            tr.appendChild(cell('td', row[c] ?? ''));
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

        private static IEnumerable<string> ReadSlideElements(
            ZipArchive archive,
            XDocument slide,
            IReadOnlyDictionary<string, string> relationships,
            long slideWidth,
            long slideHeight)
        {
            foreach (XElement picture in slide.Descendants().Where(e => e.Name.LocalName == "pic"))
            {
                if (!TryReadBounds(picture, slideWidth, slideHeight, out string bounds))
                {
                    continue;
                }

                string? relationshipId = picture.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip")
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
            }

            foreach (XElement tableFrame in slide.Descendants().Where(e => e.Name.LocalName == "graphicFrame" && e.Descendants().Any(d => d.Name.LocalName == "tbl")))
            {
                if (!TryReadBounds(tableFrame, slideWidth, slideHeight, out string bounds))
                {
                    continue;
                }

                XElement? table = tableFrame.Descendants().FirstOrDefault(e => e.Name.LocalName == "tbl");
                if (table == null)
                {
                    continue;
                }

                string tableHtml = BuildTableHtml(table);
                if (!string.IsNullOrWhiteSpace(tableHtml))
                {
                    yield return "<div class=\"ppt-table\" style=\"" + bounds + "\">" + tableHtml + "</div>";
                }
            }

            foreach (XElement shape in slide.Descendants().Where(e => e.Name.LocalName == "sp" && e.Descendants().Any(d => d.Name.LocalName == "txBody")))
            {
                string textHtml = BuildShapeTextHtml(shape);
                if (string.IsNullOrWhiteSpace(textHtml))
                {
                    continue;
                }

                string bounds = TryReadBounds(shape, slideWidth, slideHeight, out string readBounds)
                    ? readBounds
                    : "left:5%;top:5%;width:90%;height:auto;";
                string fill = ReadSolidFill(shape.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr"));
                string textStyle = ReadTextStyle(shape);
                yield return "<div class=\"ppt-shape\" style=\"" + bounds + fill + textStyle + "\">" + textHtml + "</div>";
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

        private static string ReadTextStyle(XElement shape)
        {
            XElement? runProperties = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "rPr");
            var style = new StringBuilder();
            if (runProperties?.Attribute("sz")?.Value is string sizeValue &&
                int.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) &&
                size > 0)
            {
                style.Append("font-size:clamp(9px,")
                    .Append(FormatInvariant(size / 100.0 * 1.333))
                    .Append("px,96px);");
            }
            else
            {
                style.Append("font-size:clamp(11px,1.8vw,28px);");
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

        private static bool TryReadBounds(XElement element, long slideWidth, long slideHeight, out string bounds)
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
                return false;
            }

            bounds = "left:" + Percent(x, slideWidth) +
                ";top:" + Percent(y, slideHeight) +
                ";width:" + Percent(cx, slideWidth) +
                ";height:" + Percent(cy, slideHeight) + ";";
            return true;
        }

        private static bool TryReadLong(XElement element, string attributeName, out long value)
        {
            value = 0;
            return long.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string Percent(long value, long total)
        {
            return FormatInvariant(value / (double)Math.Max(1, total) * 100) + "%";
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
