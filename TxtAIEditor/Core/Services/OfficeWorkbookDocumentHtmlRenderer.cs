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
            public Dictionary<(int Row, int Column), ViewerWorkbookCell> Cells { get; } = new();
            public List<ViewerWorkbookObject> Objects { get; } = new();
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

        private sealed class ViewerWorkbookObject
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

        private sealed class ViewerChartSeries
        {
            public string Name { get; init; } = string.Empty;
            public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
            public IReadOnlyList<double?> Values { get; init; } = Array.Empty<double?>();
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
                }).ToArray()).ToArray(),
                objects = sheet.Objects.Select(item => new
                {
                    type = item.Kind,
                    title = item.Title,
                    svg = item.Svg,
                    imageData = item.ImageData,
                    width = item.Width,
                    height = item.Height,
                    hasHeader = item.HasHeader,
                    rows = item.Rows.Select(row => row.Select(cell => new
                    {
                        value = cell.Value,
                        backgroundColor = cell.BackgroundColor,
                        textColor = cell.TextColor,
                        bold = cell.Bold,
                        italic = cell.Italic
                    }).ToArray()).ToArray()
                }).ToArray()
            }).ToArray();
            string sheetsJson = JsonSerializer.Serialize(sheetPayload);
            string emptySheetTextJson = JsonSerializer.Serialize(getString("OfficeViewerEmptySheet", "Empty sheet."));
            string rowsTextJson = JsonSerializer.Serialize(getString("OfficeViewerRowsLabel", "rows"));
            string columnsTextJson = JsonSerializer.Serialize(getString("OfficeViewerColumnsLabel", "columns"));
            string firstShownTextJson = JsonSerializer.Serialize(getString("OfficeViewerFirstRowsShownFormat", "first {0} shown"));
            string sheetAriaLabelJson = JsonSerializer.Serialize(getString("OfficeViewerSheetSelectorLabel", "Sheet"));
            string objectsLabelJson = JsonSerializer.Serialize(getString("OfficeViewerSheetObjectsLabel", "Sheet objects"));
            string chartsLabelJson = JsonSerializer.Serialize(getString("OfficeViewerChartsLabel", "Charts"));
            string tablesLabelJson = JsonSerializer.Serialize(getString("OfficeViewerTablesLabel", "Tables"));
            string imagesLabelJson = JsonSerializer.Serialize(getString("OfficeViewerImagesLabel", "Images"));

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
    user-select: none;
    -webkit-user-select: none;
}
/* Keep workbook cell text out of table sizing so it can flow over empty cells. */
.workbook-table td.xlsx-cell {
    position: relative;
    overflow: visible;
    white-space: nowrap;
    overflow-wrap: normal;
    word-break: normal;
}
.workbook-table td.xlsx-cell[data-xlsx-has-value="true"] {
    z-index: 1;
}
.workbook-table .xlsx-cell-text {
    position: absolute;
    top: 6px;
    left: 8px;
    display: block;
    width: max-content;
    min-width: 0;
    max-width: none;
    overflow: hidden;
    white-space: pre;
    overflow-wrap: normal;
    word-break: normal;
    text-overflow: clip;
    pointer-events: none;
    z-index: 2;
}
th {
    position: sticky;
    top: 0;
    z-index: 2;
    background: var(--header);
    color: var(--muted);
    font-weight: 600;
    cursor: cell;
}
th.row-header {
    left: 0;
    z-index: 3;
    min-width: 54px;
    width: 54px;
    text-align: right;
    cursor: default;
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
    cursor: cell;
}
td:not(.row-header) { cursor: cell; }
th.col-resize-hover { cursor: ew-resize !important; }
th.col-resize-hover::after {
    content: '';
    position: absolute;
    top: 0;
    right: 0;
    width: 2px;
    height: 100%;
    background: color-mix(in srgb, var(--accent) 60%, transparent);
    pointer-events: none;
}
td.row-resize-hover { cursor: ns-resize !important; }
td.row-resize-hover::after {
    content: '';
    position: absolute;
    left: 0;
    bottom: 0;
    width: 100%;
    height: 2px;
    background: color-mix(in srgb, var(--accent) 60%, transparent);
    pointer-events: none;
}
body.resizing { user-select: none; -webkit-user-select: none; }
body.resizing * { cursor: inherit !important; }
body.resizing-col { cursor: ew-resize !important; }
body.resizing-row { cursor: ns-resize !important; }
th.selected-column-heading,
td.selected-row-heading {
    color: var(--text);
    background: color-mix(in srgb, var(--accent) 22%, var(--header));
}
td.selected-cell {
    background: color-mix(in srgb, var(--accent) 16%, var(--panel));
    outline: 1px solid color-mix(in srgb, var(--accent) 70%, transparent);
    outline-offset: -1px;
}
td.selected-cell.active-cell {
    outline: 2px solid var(--accent);
    outline-offset: -2px;
}
.empty { padding: 28px; color: var(--muted); }
.truncated { color: var(--accent); }
.sheet-objects {
    display: flex;
    flex-direction: column;
    gap: 14px;
    padding: 18px 14px 28px;
}
.sheet-objects-heading {
    margin: 0;
    color: var(--text);
    font-size: 15px;
    font-weight: 650;
}
.sheet-object-card {
    width: min(100%, 980px);
    overflow: hidden;
    border: 1px solid var(--line);
    border-radius: 8px;
    background: var(--panel);
    box-shadow: 0 8px 22px rgba(15, 23, 42, .07);
}
.sheet-object-title {
    margin: 0;
    padding: 10px 12px;
    border-bottom: 1px solid var(--line);
    color: var(--text);
    font-size: 13px;
    font-weight: 650;
}
.sheet-object-body { padding: 12px; }
.sheet-object-chart {
    width: min(100%, 900px);
    min-height: 260px;
    margin: 0 auto;
    overflow: auto;
}
.sheet-object-chart svg {
    display: block;
    width: 100%;
    height: auto;
    min-height: 260px;
}
.sheet-object-image {
    display: block;
    width: auto;
    max-width: 100%;
    height: auto;
    max-height: 720px;
    margin: 0 auto;
    object-fit: contain;
}
.sheet-object-table-wrap { overflow: auto; }
.sheet-object-table {
    border-collapse: collapse;
    min-width: 100%;
    background: var(--panel);
}
.sheet-object-table th,
.sheet-object-table td {
    min-width: 96px;
    max-width: 360px;
    height: 30px;
    padding: 6px 8px;
    border: 1px solid var(--line);
    color: var(--text);
    text-align: left;
    vertical-align: top;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 13px;
    user-select: text;
    -webkit-user-select: text;
}
.sheet-object-table th {
    background: var(--header);
    color: var(--muted);
    font-weight: 650;
}
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
const objectsLabel = {{objectsLabelJson}};
const chartsLabel = {{chartsLabelJson}};
const tablesLabel = {{tablesLabelJson}};
const imagesLabel = {{imagesLabelJson}};
const maxRows = 5000;
const select = document.getElementById('sheetSelect');
const meta = document.getElementById('sheetMeta');
const wrap = document.getElementById('tableWrap');
let activeSheetIndex = 0;
let columnCount = 1;
let selectionState = null;
let dragState = null;
let resizeState = null;
let resizeHover = null;
let workbookTextOverflowFrame = 0;
const sheetLayouts = [];
const resizeStyle = document.createElement('style');
document.head.appendChild(resizeStyle);
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

function createWorkbookCell(sourceCell) {
    const value = String(valueOf(sourceCell) ?? '');
    const td = document.createElement('td');
    td.className = 'xlsx-cell';
    td.dataset.xlsxHasValue = value.length > 0 ? 'true' : 'false';

    const text = document.createElement('span');
    text.className = 'xlsx-cell-text';
    text.textContent = value;
    td.appendChild(text);
    applyCellStyle(td, sourceCell);
    return td;
}

function updateWorkbookTextOverflow() {
    const table = wrap.querySelector('table.workbook-table');
    if (!table) return;

    table.querySelectorAll('tbody tr').forEach(row => {
        const cells = Array.from(row.querySelectorAll('td.xlsx-cell[data-csv-column]'));
        let nextValueCell = null;

        for (let index = cells.length - 1; index >= 0; index--) {
            const currentCell = cells[index];
            const text = currentCell.querySelector('.xlsx-cell-text');
            if (!text) continue;

            text.style.width = 'max-content';
            if (currentCell.dataset.xlsxHasValue !== 'true') continue;

            if (nextValueCell) {
                const currentRect = currentCell.getBoundingClientRect();
                const nextRect = nextValueCell.getBoundingClientRect();
                const availableWidth = Math.max(0, Math.round(nextRect.left - currentRect.left - 8));
                text.style.width = `${availableWidth}px`;
            }

            nextValueCell = currentCell;
        }
    });
}

function scheduleWorkbookTextOverflowUpdate() {
    if (workbookTextOverflowFrame) return;

    workbookTextOverflowFrame = requestAnimationFrame(() => {
        workbookTextOverflowFrame = 0;
        updateWorkbookTextOverflow();
    });
}

function escapeCsvValue(value) {
    const text = String(value ?? '');
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function sheetRows() {
    return sheets[activeSheetIndex]?.rows || [];
}

function cellValue(rowIndex, columnIndex) {
    const row = sheetRows()[rowIndex] || [];
    return valueOf(row[columnIndex] || null);
}

function createCellSelection(startRow, startColumn, endRow = startRow, endColumn = startColumn) {
    return {
        mode: 'cells',
        startRow: Math.max(0, Number(startRow || 0)),
        startColumn: Math.max(0, Number(startColumn || 0)),
        endRow: Math.max(0, Number(endRow || 0)),
        endColumn: Math.max(0, Number(endColumn || 0))
    };
}

function selectedCellRect() {
    const sel = selectionState;
    if (!sel || sel.mode !== 'cells') return null;
    return {
        startRow: Math.min(sel.startRow, sel.endRow),
        endRow: Math.max(sel.startRow, sel.endRow),
        startColumn: Math.min(sel.startColumn, sel.endColumn),
        endColumn: Math.max(sel.startColumn, sel.endColumn)
    };
}

function selectedCellsAsCsv() {
    const rows = sheetRows();
    if (!selectionState || !rows.length) return '';

    let rowIndexes = [];
    let columnIndexes = [];
    if (selectionState.mode === 'rows') {
        rowIndexes = [...selectionState.rows].filter(row => row >= 0 && row < rows.length).sort((a, b) => a - b);
        columnIndexes = Array.from({ length: columnCount }, (_, index) => index);
    } else if (selectionState.mode === 'columns') {
        rowIndexes = Array.from({ length: rows.length }, (_, index) => index);
        columnIndexes = [...selectionState.columns].filter(column => column >= 0 && column < columnCount).sort((a, b) => a - b);
    } else {
        const rect = selectedCellRect();
        if (!rect) return '';
        rowIndexes = Array.from({ length: rect.endRow - rect.startRow + 1 }, (_, index) => rect.startRow + index)
            .filter(row => row >= 0 && row < rows.length);
        columnIndexes = Array.from({ length: rect.endColumn - rect.startColumn + 1 }, (_, index) => rect.startColumn + index)
            .filter(column => column >= 0 && column < columnCount);
    }

    if (!rowIndexes.length || !columnIndexes.length) return '';

    const lines = [];
    rowIndexes.forEach(rowIndex => {
        lines.push(columnIndexes.map(columnIndex => escapeCsvValue(cellValue(rowIndex, columnIndex))).join(','));
    });

    return lines.join('\r\n');
}

function isSelectedCell(rowIndex, columnIndex) {
    if (!selectionState) return false;
    if (selectionState.mode === 'rows') return selectionState.rows.has(rowIndex);
    if (selectionState.mode === 'columns') return selectionState.columns.has(columnIndex);

    const rect = selectedCellRect();
    return !!rect &&
        rowIndex >= rect.startRow &&
        rowIndex <= rect.endRow &&
        columnIndex >= rect.startColumn &&
        columnIndex <= rect.endColumn;
}

function isActiveCell(rowIndex, columnIndex) {
    return selectionState?.mode === 'cells' &&
        rowIndex === selectionState.endRow &&
        columnIndex === selectionState.endColumn;
}

function applySelectionClasses() {
    wrap.querySelectorAll('tbody td[data-csv-row][data-csv-column]').forEach(td => {
        const rowIndex = Number(td.dataset.csvRow);
        const columnIndex = Number(td.dataset.csvColumn);
        td.classList.toggle('selected-cell', isSelectedCell(rowIndex, columnIndex));
        td.classList.toggle('active-cell', isActiveCell(rowIndex, columnIndex));
    });
    wrap.querySelectorAll('thead th[data-csv-column]').forEach(th => {
        th.classList.toggle('selected-column-heading',
            selectionState?.mode === 'columns' && selectionState.columns.has(Number(th.dataset.csvColumn)));
    });
    wrap.querySelectorAll('tbody td.row-header[data-csv-row]').forEach(td => {
        td.classList.toggle('selected-row-heading',
            selectionState?.mode === 'rows' && selectionState.rows.has(Number(td.dataset.csvRow)));
    });
}

function clearNativeSelection() {
    window.getSelection()?.removeAllRanges();
}

function setCellSelection(startRow, startColumn, endRow = startRow, endColumn = startColumn) {
    selectionState = createCellSelection(startRow, startColumn, endRow, endColumn);
    clearNativeSelection();
    applySelectionClasses();
}

function setColumnSelection(columnIndex, event) {
    const column = Math.max(0, Math.min(columnCount - 1, Number(columnIndex || 0)));
    const ctrl = !!(event?.ctrlKey || event?.metaKey);
    const shift = !!event?.shiftKey;
    const current = selectionState?.mode === 'columns' ? new Set(selectionState.columns) : new Set();
    const anchor = Math.max(0, Math.min(columnCount - 1, Number(selectionState?.columnAnchor ?? column)));

    if (shift) {
        if (!ctrl) current.clear();
        const start = Math.min(anchor, column);
        const end = Math.max(anchor, column);
        for (let c = start; c <= end; c++) current.add(c);
    } else if (ctrl) {
        current.has(column) ? current.delete(column) : current.add(column);
    } else {
        current.clear();
        current.add(column);
    }

    if (!current.size) current.add(column);
    selectionState = {
        mode: 'columns',
        columns: current,
        columnAnchor: shift ? anchor : column
    };
    clearNativeSelection();
    applySelectionClasses();
}

function setRowSelection(rowIndex, event) {
    const rows = sheetRows();
    const row = Math.max(0, Math.min(rows.length - 1, Number(rowIndex || 0)));
    const ctrl = !!(event?.ctrlKey || event?.metaKey);
    const shift = !!event?.shiftKey;
    const current = selectionState?.mode === 'rows' ? new Set(selectionState.rows) : new Set();
    const anchor = Math.max(0, Math.min(rows.length - 1, Number(selectionState?.rowAnchor ?? row)));

    if (shift) {
        if (!ctrl) current.clear();
        const start = Math.min(anchor, row);
        const end = Math.max(anchor, row);
        for (let r = start; r <= end; r++) current.add(r);
    } else if (ctrl) {
        current.has(row) ? current.delete(row) : current.add(row);
    } else {
        current.clear();
        current.add(row);
    }

    if (!current.size) current.add(row);
    selectionState = {
        mode: 'rows',
        rows: current,
        rowAnchor: shift ? anchor : row
    };
    clearNativeSelection();
    applySelectionClasses();
}

function cellFromPoint(event) {
    return document.elementFromPoint(event.clientX, event.clientY)
        ?.closest?.('td[data-csv-row][data-csv-column]');
}

function handleCellPointerDown(event) {
    const td = event.target.closest?.('td[data-csv-row][data-csv-column]');
    if (!td || event.button !== 0) return;

    const row = Number(td.dataset.csvRow);
    const column = Number(td.dataset.csvColumn);
    const anchor = event.shiftKey && selectionState?.mode === 'cells'
        ? { row: selectionState.startRow, column: selectionState.startColumn }
        : { row, column };
    dragState = anchor;
    setCellSelection(anchor.row, anchor.column, row, column);
    event.preventDefault();
}

function handleCellPointerMove(event) {
    if (resizeState) {
        if ((event.buttons & 1) === 1) {
            const layout = sheetLayouts[activeSheetIndex];
            if (resizeState.type === 'column') {
                const px = Math.max(40, Math.round(resizeState.startWidth + event.clientX - resizeState.startX));
                layout.columns.set(resizeState.index, px);
            } else {
                const px = Math.max(20, Math.round(resizeState.startHeight + event.clientY - resizeState.startY));
                layout.rows.set(resizeState.index, px);
            }
            updateResizedStyles();
        }
        event.preventDefault();
        return;
    }

    if (!dragState || (event.buttons & 1) !== 1) return;

    const td = cellFromPoint(event);
    if (!td) return;

    setCellSelection(dragState.row, dragState.column, Number(td.dataset.csvRow), Number(td.dataset.csvColumn));
    event.preventDefault();
}

function handlePointerUp() {
    dragState = null;
    if (resizeState) {
        resizeState = null;
        document.body.classList.remove('resizing', 'resizing-col', 'resizing-row');
        scheduleWorkbookTextOverflowUpdate();
    }
}

function sheetLayout() {
    if (!sheetLayouts[activeSheetIndex]) {
        sheetLayouts[activeSheetIndex] = { columns: new Map(), rows: new Map() };
    }
    return sheetLayouts[activeSheetIndex];
}

function clearResizeHover() {
    resizeHover = null;
    wrap.querySelectorAll('.col-resize-hover, .row-resize-hover').forEach(el => {
        el.classList.remove('col-resize-hover', 'row-resize-hover');
    });
}

function updateResizeHover(event) {
    clearResizeHover();
    const th = event.target.closest?.('th[data-csv-column]');
    if (th) {
        const rect = th.getBoundingClientRect();
        const distanceToRightEdge = rect.right - event.clientX;
        if (distanceToRightEdge > 0 && distanceToRightEdge <= 8) {
            th.classList.add('col-resize-hover');
            resizeHover = { type: 'column', index: Number(th.dataset.csvColumn) };
            return;
        }
    }
    const rowHeader = event.target.closest?.('td.row-header[data-csv-row]');
    if (rowHeader) {
        const rect = rowHeader.getBoundingClientRect();
        const distanceToBottomEdge = rect.bottom - event.clientY;
        if (distanceToBottomEdge > 0 && distanceToBottomEdge <= 8) {
            rowHeader.classList.add('row-resize-hover');
            resizeHover = { type: 'row', index: Number(rowHeader.dataset.csvRow) };
        }
    }
}

function updateResizedStyles() {
    const layout = sheetLayouts[activeSheetIndex];
    if (!layout) return;
    let css = '';
    layout.columns.forEach((px, columnIndex) => {
        css += `table tr > :nth-child(${columnIndex + 2}) { width: ${px}px !important; min-width: ${px}px !important; max-width: none !important; }`;
    });
    layout.rows.forEach((px, rowIndex) => {
        css += `table tbody tr:nth-child(${rowIndex + 1}) > * { height: ${px}px !important; }`;
    });
    resizeStyle.textContent = css;
    scheduleWorkbookTextOverflowUpdate();
}

function startColumnResize(columnIndex, event) {
    const header = wrap.querySelector(`thead th[data-csv-column="${columnIndex}"]`);
    resizeState = {
        type: 'column',
        index: columnIndex,
        startX: event.clientX,
        startWidth: header ? header.getBoundingClientRect().width : 96
    };
    document.body.classList.add('resizing', 'resizing-col');
    event.preventDefault();
    event.stopPropagation();
}

function startRowResize(rowIndex, event) {
    const headerCell = wrap.querySelector(`tbody td.row-header[data-csv-row="${rowIndex}"]`);
    resizeState = {
        type: 'row',
        index: rowIndex,
        startY: event.clientY,
        startHeight: headerCell ? headerCell.getBoundingClientRect().height : 30
    };
    document.body.classList.add('resizing', 'resizing-row');
    event.preventDefault();
    event.stopPropagation();
}

function objectLabel(object) {
    switch (object?.type) {
        case 'chart': return chartsLabel;
        case 'table': return tablesLabel;
        case 'image': return imagesLabel;
        default: return objectsLabel;
    }
}

function renderSheetObjectTable(item) {
    const rows = Array.isArray(item?.rows) ? item.rows : [];
    if (!rows.length) return null;

    const table = document.createElement('table');
    table.className = 'sheet-object-table';
    const body = document.createElement('tbody');
    rows.forEach((row, rowIndex) => {
        const tr = document.createElement('tr');
        (Array.isArray(row) ? row : []).forEach(sourceCell => {
            const tag = item.hasHeader && rowIndex === 0 ? 'th' : 'td';
            const td = cell(tag, valueOf(sourceCell));
            applyCellStyle(td, sourceCell);
            tr.appendChild(td);
        });
        body.appendChild(tr);
    });
    table.appendChild(body);

    const tableWrap = document.createElement('div');
    tableWrap.className = 'sheet-object-table-wrap';
    tableWrap.appendChild(table);
    return tableWrap;
}

function renderSheetObjects(sheet) {
    const objects = Array.isArray(sheet?.objects) ? sheet.objects : [];
    if (!objects.length) return;

    const section = document.createElement('section');
    section.className = 'sheet-objects';
    section.appendChild(cell('h2', objectsLabel, 'sheet-objects-heading'));

    objects.forEach((item, objectIndex) => {
        const card = document.createElement('article');
        card.className = 'sheet-object-card';
        const label = objectLabel(item);
        const title = item.title || `${label} ${objectIndex + 1}`;
        card.appendChild(cell('h3', title, 'sheet-object-title'));

        const body = document.createElement('div');
        body.className = 'sheet-object-body';
        if (item.type === 'chart' && item.svg) {
            const chart = document.createElement('div');
            chart.className = 'sheet-object-chart';
            chart.innerHTML = item.svg;
            body.appendChild(chart);
        } else if (item.type === 'image' && item.imageData) {
            const image = document.createElement('img');
            image.className = 'sheet-object-image';
            image.src = item.imageData;
            image.alt = title;
            if (Number(item.width) > 0) image.width = Number(item.width);
            if (Number(item.height) > 0) image.height = Number(item.height);
            body.appendChild(image);
        } else if (item.type === 'table') {
            const table = renderSheetObjectTable(item);
            if (table) body.appendChild(table);
        }

        if (!body.childElementCount) {
            body.appendChild(cell('div', title, 'empty'));
        }
        card.appendChild(body);
        section.appendChild(card);
    });

    wrap.appendChild(section);
}

function renderSheet(index) {
    const sheet = sheets[index];
    const rows = sheet.rows || [];
    const visibleRows = rows.slice(0, maxRows);
    columnCount = Math.max(1, ...visibleRows.map(row => row.length));
    wrap.textContent = '';
    sheetLayout();

    if (!rows.length) {
        selectionState = null;
        wrap.appendChild(cell('div', emptySheetText, 'empty'));
        renderSheetObjects(sheet);
        meta.textContent = `0 ${rowsText}`;
        return;
    }

    const table = document.createElement('table');
    table.className = 'workbook-table';
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    headRow.appendChild(cell('th', '', 'row-header'));
    for (let c = 0; c < columnCount; c++) {
        const th = cell('th', colName(c));
        th.dataset.csvColumn = String(c);
        headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    visibleRows.forEach((row, r) => {
        const tr = document.createElement('tr');
        const rowHeader = cell('td', String(r + 1), 'row-header');
        rowHeader.dataset.csvRow = String(r);
        tr.appendChild(rowHeader);
        for (let c = 0; c < columnCount; c++) {
            const sourceCell = row[c] || null;
            const td = createWorkbookCell(sourceCell);
            td.dataset.csvRow = String(r);
            td.dataset.csvColumn = String(c);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    renderSheetObjects(sheet);

    const firstShown = firstShownText.replace('{0}', maxRows.toLocaleString());
    meta.innerHTML = `${rows.length.toLocaleString()} ${rowsText} x ${columnCount.toLocaleString()} ${columnsText}` +
        (rows.length > maxRows ? ` <span class="truncated">${firstShown}</span>` : '');

    if (!selectionState) {
        selectionState = createCellSelection(0, 0);
    }
    applySelectionClasses();
    updateResizedStyles();
}

sheets.forEach((sheet, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = sheet.name || `Sheet ${index + 1}`;
    select.appendChild(option);
});
select.addEventListener('change', () => {
    activeSheetIndex = Number(select.value || 0);
    selectionState = null;
    renderSheet(activeSheetIndex);
});
wrap.addEventListener('pointerdown', event => {
    updateResizeHover(event);
    if (resizeHover) {
        if (resizeHover.type === 'column') {
            startColumnResize(resizeHover.index, event);
        } else {
            startRowResize(resizeHover.index, event);
        }
        return;
    }

    const columnHeader = event.target.closest?.('th[data-csv-column]');
    if (columnHeader) {
        setColumnSelection(Number(columnHeader.dataset.csvColumn), event);
        event.preventDefault();
        return;
    }

    const rowHeader = event.target.closest?.('td.row-header[data-csv-row]');
    if (rowHeader) {
        setRowSelection(Number(rowHeader.dataset.csvRow), event);
        event.preventDefault();
        return;
    }

    handleCellPointerDown(event);
});
wrap.addEventListener('pointermove', event => {
    if ((event.buttons & 1) === 0) updateResizeHover(event);
});
wrap.addEventListener('pointerleave', clearResizeHover);
document.addEventListener('pointermove', handleCellPointerMove);
document.addEventListener('pointerup', handlePointerUp);
window.addEventListener('resize', scheduleWorkbookTextOverflowUpdate);
window.addEventListener('blur', () => {
    if (resizeState) {
        resizeState = null;
        document.body.classList.remove('resizing', 'resizing-col', 'resizing-row');
    }
});
document.addEventListener('copy', event => {
    const csv = selectedCellsAsCsv();
    if (!csv) return;

    event.clipboardData?.setData('text/plain', csv);
    event.preventDefault();
});
document.addEventListener('selectstart', event => {
    if (event.target?.closest?.('#tableWrap') && !event.target?.closest?.('.sheet-objects')) {
        event.preventDefault();
    }
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
                    themeColors).ConfigureAwait(false);

                sheets.Add(sheet);
            }

            return sheets;
        }

        private static async Task LoadWorkbookObjectsAsync(
            ZipArchive archive,
            ZipArchiveEntry sheetEntry,
            ViewerWorkbookSheet sheet,
            XDocument sheetDocument,
            IReadOnlyList<string> themeColors)
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
                    themeColors).ConfigureAwait(false);
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
            IReadOnlyList<string> themeColors)
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
                        themeColors).ConfigureAwait(false);
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

        private static async Task<string?> BuildWorkbookChartSvgAsync(
            ZipArchive archive,
            string chartPath,
            IReadOnlyList<string> themeColors)
        {
            try
            {
                string? svg = OfficePresentationChartSvgRenderer.TryBuild(
                    archive,
                    chartPath,
                    themeColors);
                if (!string.IsNullOrWhiteSpace(svg))
                {
                    return svg;
                }
            }
            catch
            {
                // Fall back to a simple chart for chart types not handled by the presentation renderer.
            }

            XDocument? chartDocument = await TryLoadXmlEntryAsync(archive, chartPath).ConfigureAwait(false);
            return BuildWorkbookFallbackChartSvg(chartDocument);
        }

        private static string? BuildWorkbookFallbackChartSvg(XDocument? chartDocument)
        {
            XElement? plotArea = chartDocument?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "plotArea");
            XElement? chart = plotArea?.Elements().FirstOrDefault(e =>
                e.Name.LocalName is
                    "areaChart" or "barChart" or "bubbleChart" or "doughnutChart" or
                    "lineChart" or "pie3DChart" or "pieChart" or "radarChart" or
                    "scatterChart" or "stockChart");
            if (chart == null)
            {
                return null;
            }

            List<ViewerChartSeries> series = chart.Elements()
                .Where(e => e.Name.LocalName == "ser")
                .Select(ReadWorkbookChartSeries)
                .Where(item => item.Values.Any(value => value.HasValue))
                .ToList();
            if (series.Count == 0)
            {
                return null;
            }

            int categoryCount = series.Max(item => Math.Max(item.Categories.Count, item.Values.Count));
            if (categoryCount <= 0)
            {
                return null;
            }

            List<string> categories = Enumerable.Range(0, categoryCount)
                .Select(index => series
                    .Select(item => index < item.Categories.Count ? item.Categories[index] : string.Empty)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                    (index + 1).ToString(CultureInfo.InvariantCulture))
                .ToList();
            List<double> values = series.SelectMany(item => item.Values)
                .Where(value => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                .Select(value => value!.Value)
                .ToList();
            if (values.Count == 0)
            {
                return null;
            }

            const double svgHeight = 520;
            const double plotLeft = 78;
            const double plotTop = 66;
            const double plotWidth = 832;
            const double plotHeight = 350;
            double minimum = Math.Min(0, values.Min());
            double maximum = Math.Max(0, values.Max());
            if (maximum <= minimum)
            {
                maximum = minimum + Math.Max(1, Math.Abs(minimum) * .1);
            }

            double zeroY = plotTop + ((maximum / (maximum - minimum)) * plotHeight);
            double categoryWidth = plotWidth / categoryCount;
            double groupWidth = categoryWidth * .72;
            double barWidth = groupWidth / Math.Max(1, series.Count);
            string title = ReadWorkbookChartTitle(chartDocument);
            string[] colors = { "#2864DC", "#16A46C", "#7656D6", "#D97706", "#DC3E42", "#0891B2" };
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 520\" role=\"img\" aria-label=\"")
                .Append(Html(title))
                .Append("\"><rect width=\"960\" height=\"520\" fill=\"#FFFFFF\"/>");
            if (!string.IsNullOrWhiteSpace(title))
            {
                svg.Append("<text x=\"480\" y=\"30\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"20\" font-weight=\"600\" fill=\"#111827\">")
                    .Append(Html(title))
                    .Append("</text>");
            }

            for (int gridIndex = 0; gridIndex <= 5; gridIndex++)
            {
                double ratio = gridIndex / 5.0;
                double y = plotTop + (ratio * plotHeight);
                double value = maximum - (ratio * (maximum - minimum));
                svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                    .Append(FormatInvariant(y)).Append("\" x2=\"")
                    .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                    .Append(FormatInvariant(y)).Append("\" stroke=\"#D9E0EA\" stroke-width=\"1\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(plotLeft - 10)).Append("\" y=\"")
                    .Append(FormatInvariant(y + 4)).Append("\" text-anchor=\"end\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                    .Append(Html(FormatInvariant(value))).Append("</text>");
            }

            svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(plotTop)).Append("\" x2=\"").Append(FormatInvariant(plotLeft))
                .Append("\" y2=\"").Append(FormatInvariant(plotTop + plotHeight))
                .Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>");
            svg.Append("<line x1=\"").Append(FormatInvariant(plotLeft)).Append("\" y1=\"")
                .Append(FormatInvariant(zeroY)).Append("\" x2=\"")
                .Append(FormatInvariant(plotLeft + plotWidth)).Append("\" y2=\"")
                .Append(FormatInvariant(zeroY)).Append("\" stroke=\"#667085\" stroke-width=\"1.2\"/>");

            for (int categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
            {
                double categoryLeft = plotLeft + (categoryIndex * categoryWidth) + ((categoryWidth - groupWidth) / 2);
                for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    double? value = categoryIndex < series[seriesIndex].Values.Count
                        ? series[seriesIndex].Values[categoryIndex]
                        : null;
                    if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                    {
                        continue;
                    }

                    double valueY = plotTop + ((maximum - value.Value) / (maximum - minimum) * plotHeight);
                    double y = Math.Min(valueY, zeroY);
                    double height = Math.Max(1, Math.Abs(zeroY - valueY));
                    svg.Append("<rect x=\"").Append(FormatInvariant(categoryLeft + (seriesIndex * barWidth)))
                        .Append("\" y=\"").Append(FormatInvariant(y)).Append("\" width=\"")
                        .Append(FormatInvariant(Math.Max(1, barWidth - 2))).Append("\" height=\"")
                        .Append(FormatInvariant(height)).Append("\" rx=\"2\" fill=\"")
                        .Append(colors[seriesIndex % colors.Length]).Append("\"/>");
                }

                if (categoryIndex % Math.Max(1, (int)Math.Ceiling(categoryCount / 16.0)) == 0)
                {
                    double labelX = plotLeft + (categoryIndex * categoryWidth) + (categoryWidth / 2);
                    svg.Append("<text x=\"").Append(FormatInvariant(labelX)).Append("\" y=\"")
                        .Append(FormatInvariant(plotTop + plotHeight + 22))
                        .Append("\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"11\" fill=\"#667085\">")
                        .Append(Html(TrimWorkbookChartLabel(categories[categoryIndex])))
                        .Append("</text>");
                }
            }

            double legendX = plotLeft;
            double legendY = svgHeight - 44;
            for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                string label = string.IsNullOrWhiteSpace(series[seriesIndex].Name)
                    ? (seriesIndex + 1).ToString(CultureInfo.InvariantCulture)
                    : series[seriesIndex].Name;
                svg.Append("<rect x=\"").Append(FormatInvariant(legendX)).Append("\" y=\"")
                    .Append(FormatInvariant(legendY - 10)).Append("\" width=\"12\" height=\"12\" rx=\"2\" fill=\"")
                    .Append(colors[seriesIndex % colors.Length]).Append("\"/>")
                    .Append("<text x=\"").Append(FormatInvariant(legendX + 18)).Append("\" y=\"")
                    .Append(FormatInvariant(legendY)).Append("\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"12\" fill=\"#334155\">")
                    .Append(Html(TrimWorkbookChartLabel(label, 26))).Append("</text>");
                legendX += 130;
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        private static ViewerChartSeries ReadWorkbookChartSeries(XElement series)
        {
            XElement? categorySource = series.Elements().FirstOrDefault(e => e.Name.LocalName is "cat" or "xVal");
            XElement? valueSource = series.Elements().FirstOrDefault(e => e.Name.LocalName is "val" or "yVal" or "bubbleSize");
            return new ViewerChartSeries
            {
                Name = series.Elements().FirstOrDefault(e => e.Name.LocalName == "tx")?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName is "v" or "t")?.Value ?? string.Empty,
                Categories = ReadWorkbookChartTextPoints(categorySource),
                Values = ReadWorkbookChartNumberPoints(valueSource)
            };
        }

        private static IReadOnlyList<string> ReadWorkbookChartTextPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<string>();
            }

            var points = new SortedDictionary<int, string>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(point.Attribute("idx")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int readIndex)
                    ? readIndex
                    : fallbackIndex;
                points[index] = point.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string value = source.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                return string.IsNullOrEmpty(value) ? Array.Empty<string>() : new[] { value };
            }

            int count = Math.Max(points.Keys.Max() + 1, TryReadInt(source.Descendants().FirstOrDefault(e => e.Name.LocalName == "ptCount") ?? source, "val"));
            return Enumerable.Range(0, count)
                .Select(index => points.TryGetValue(index, out string? value) ? value : string.Empty)
                .ToList();
        }

        private static IReadOnlyList<double?> ReadWorkbookChartNumberPoints(XElement? source)
        {
            if (source == null)
            {
                return Array.Empty<double?>();
            }

            var points = new SortedDictionary<int, double?>();
            int fallbackIndex = 0;
            foreach (XElement point in source.Descendants().Where(e => e.Name.LocalName == "pt"))
            {
                int index = int.TryParse(point.Attribute("idx")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int readIndex)
                    ? readIndex
                    : fallbackIndex;
                string? text = point.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                points[index] = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? value
                    : null;
                fallbackIndex++;
            }

            if (points.Count == 0)
            {
                string? text = source.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? new double?[] { value }
                    : Array.Empty<double?>();
            }

            int count = Math.Max(points.Keys.Max() + 1, TryReadInt(source.Descendants().FirstOrDefault(e => e.Name.LocalName == "ptCount") ?? source, "val"));
            return Enumerable.Range(0, count)
                .Select(index => points.TryGetValue(index, out double? value) ? value : null)
                .ToList();
        }

        private static string ReadWorkbookChartTitle(XDocument? chartDocument)
        {
            XElement? title = chartDocument?.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
            if (title == null)
            {
                return string.Empty;
            }

            string text = string.Concat(title.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }

            return title.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value?.Trim() ?? string.Empty;
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

        private static string TrimWorkbookChartLabel(string value, int maxLength = 18)
        {
            string text = value ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }

        private static string FormatInvariant(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
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
