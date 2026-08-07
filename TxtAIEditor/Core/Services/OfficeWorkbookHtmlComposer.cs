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

using static TxtAIEditor.Core.Services.OfficeWorkbookPackageUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWorkbookHtmlComposer
    {
        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            IReadOnlyList<ViewerWorkbookSheet> sheets = await OfficeWorkbookPackageReader.ReadAsync(filePath).ConfigureAwait(false);
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
    }
}
