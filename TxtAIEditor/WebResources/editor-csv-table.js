import {
    cleanDirtyMarker,
    escapeHtml,
    invalidateMeasuredLineHeightsAround,
    markDirty,
    post,
    queueRender,
    requestMissingLines,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    writeClipboardText
} from './editor-core.js';
import {
    csvColumnHeader,
    csvColumnHeaderInner,
    csvFormulaInput,
    csvNameBox,
    csvToolbar,
    scrollContainer,
    viewport
} from './editor-dom.js';

const DEFAULT_COLUMN_WIDTH = 128;
const MIN_COLUMN_WIDTH = 48;
const MIN_VISIBLE_COLUMNS = 8;
const DEFAULT_TABLE_COLUMNS = 64;
const MAX_TABLE_COLUMNS = 1000;
const COLUMN_OVERSCAN_PX = 320;

let resizeState = null;
let csvDragState = null;

function ensureCsvState() {
    state.csvTableEnabled = !!state.csvTableEnabled;
    state.csvTableColumnWidths ??= [];
    state.csvTableColumnCount = Math.max(MIN_VISIBLE_COLUMNS, Number(state.csvTableColumnCount || 0));
    state.csvSelectedLine = Math.max(1, Number(state.csvSelectedLine || state.currentLine || 1));
    state.csvSelectedColumn = Math.max(0, Number(state.csvSelectedColumn || 0));
    state.csvTableVersion = Number(state.csvTableVersion || 0);
    state.csvCellComposing = !!state.csvCellComposing;
    state.csvEditMode = state.csvEditMode === 'edit' ? 'edit' : 'select';
    state.csvSelectedRows = Array.isArray(state.csvSelectedRows) ? state.csvSelectedRows : [];
    state.csvSelectedColumns = Array.isArray(state.csvSelectedColumns) ? state.csvSelectedColumns : [];
}

function columnName(index) {
    let value = Number(index || 0) + 1;
    let name = '';
    while (value > 0) {
        const rem = (value - 1) % 26;
        name = String.fromCharCode(65 + rem) + name;
        value = Math.floor((value - 1) / 26);
    }
    return name;
}

function parseCsvLine(line) {
    const text = String(line ?? '');
    const cells = [];
    let cell = '';
    let inQuotes = false;

    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (inQuotes) {
            if (ch === '"') {
                if (text[i + 1] === '"') {
                    cell += '"';
                    i++;
                } else {
                    inQuotes = false;
                }
            } else {
                cell += ch;
            }
        } else if (ch === '"') {
            inQuotes = true;
        } else if (ch === ',') {
            cells.push(cell);
            cell = '';
        } else {
            cell += ch;
        }
    }

    cells.push(cell);
    return cells;
}

function serializeCsvCell(value) {
    const text = String(value ?? '');
    return /[",\r\n]/.test(text)
        ? `"${text.replace(/"/g, '""')}"`
        : text;
}

function serializeCsvRow(cells) {
    const next = Array.isArray(cells) ? cells.slice() : [];
    while (next.length > 1 && next[next.length - 1] === '') {
        next.pop();
    }
    return next.map(serializeCsvCell).join(',');
}

function isJsonCsvTableMode() {
    return state.csvTableEnabled && state.language === 'json';
}

function allSourceLinesCached() {
    for (let line = 1; line <= state.lineCount; line++) {
        if (!state.cache.has(line)) return false;
    }

    return true;
}

function sourceTextFromCache() {
    const lines = [];
    for (let line = 1; line <= state.lineCount; line++) {
        lines.push(state.cache.get(line) || '');
    }

    return lines.join('\n').replace(/^\uFEFF/, '');
}

function decodeUnicodeEscapes(value) {
    const text = String(value ?? '');
    if (!/\\u[0-9a-fA-F]{4}/.test(text)) return text;

    return text
        .replace(/\\u([dD][89aAbB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})/g, (_, high, low) => {
            const highCode = parseInt(high, 16);
            const lowCode = parseInt(low, 16);
            return String.fromCodePoint(((highCode - 0xD800) * 0x400) + (lowCode - 0xDC00) + 0x10000);
        })
        .replace(/\\u([0-9a-fA-F]{4})/g, (_, code) => String.fromCharCode(parseInt(code, 16)));
}

function normalizeJsonStrings(value) {
    if (typeof value === 'string') return decodeUnicodeEscapes(value);
    if (Array.isArray(value)) return value.map(normalizeJsonStrings);
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, normalizeJsonStrings(child)]));
    }

    return value;
}

function jsonCellValue(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'string') return decodeUnicodeEscapes(value);
    if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') {
        return String(value);
    }

    try {
        return JSON.stringify(normalizeJsonStrings(value)) ?? '';
    } catch {
        return String(value ?? '');
    }
}

function cellValueFromText(text, previousValue) {
    const value = decodeUnicodeEscapes(String(text ?? ''));
    if (typeof previousValue === 'string') return value;
    const trimmed = value.trim();

    if (trimmed === '') {
        return typeof previousValue === 'number' ? 0 : '';
    }

    if (typeof previousValue === 'number') {
        const number = Number(trimmed);
        return Number.isFinite(number) ? number : value;
    }

    if (typeof previousValue === 'boolean') {
        if (/^true$/i.test(trimmed)) return true;
        if (/^false$/i.test(trimmed)) return false;
        return value;
    }

    if (previousValue === null) {
        if (/^null$/i.test(trimmed)) return null;
        return value;
    }

    if (Array.isArray(previousValue) || isPlainJsonObject(previousValue)) {
        try {
            return JSON.parse(trimmed);
        } catch {
            return value;
        }
    }

    try {
        return JSON.parse(trimmed);
    } catch {
        return value;
    }
}

function jsonTableKeyHeader() {
    return state.csvJsonKeyHeader || 'key';
}

function jsonTableValueHeader() {
    return state.csvJsonValueHeader || 'value';
}

function isPlainJsonObject(value) {
    return !!value && typeof value === 'object' && !Array.isArray(value);
}

function flattenJsonRecord(value, prefix = '', output = {}) {
    if (isPlainJsonObject(value)) {
        const entries = Object.entries(value);
        if (entries.length === 0 && prefix) {
            output[prefix] = '{}';
            return output;
        }

        for (const [key, child] of entries) {
            const nextPrefix = prefix ? `${prefix}.${key}` : key;
            flattenJsonRecord(child, nextPrefix, output);
        }
        return output;
    }

    if (Array.isArray(value)) {
        if (!prefix) {
            value.forEach((child, index) => {
                output[String(index)] = jsonCellValue(child);
            });
        } else {
            output[prefix] = jsonCellValue(value);
        }
        return output;
    }

    output[prefix || jsonTableValueHeader()] = jsonCellValue(value);
    return output;
}

function flattenJsonPaths(value, prefix = '', output = {}) {
    if (isPlainJsonObject(value)) {
        const entries = Object.entries(value);
        if (entries.length === 0 && prefix) {
            output[prefix] = [];
            return output;
        }

        for (const [key, child] of entries) {
            const nextPrefix = prefix ? `${prefix}.${key}` : key;
            const childPath = prefix ? [key] : [key];
            if (isPlainJsonObject(child)) {
                const childOutput = {};
                flattenJsonPaths(child, nextPrefix, childOutput);
                for (const [header, path] of Object.entries(childOutput)) {
                    output[header] = childPath.concat(path);
                }
            } else {
                output[nextPrefix] = childPath;
            }
        }
        return output;
    }

    if (Array.isArray(value)) {
        if (!prefix) {
            value.forEach((_, index) => {
                output[String(index)] = [index];
            });
        } else {
            output[prefix] = [];
        }
        return output;
    }

    output[prefix || jsonTableValueHeader()] = [];
    return output;
}

function prefixedPathObject(pathObject, basePath) {
    return Object.fromEntries(Object.entries(pathObject || {}).map(([header, path]) => [
        header,
        path === null ? null : basePath.concat(path || [])
    ]));
}

function tableFromRecordObjects(recordObjects, preferredHeaders = [], pathObjects = []) {
    const headers = [];
    const addHeader = header => {
        const safeHeader = String(header || jsonTableValueHeader());
        if (!headers.includes(safeHeader)) headers.push(safeHeader);
    };

    preferredHeaders.forEach(addHeader);
    recordObjects.forEach(record => Object.keys(record).forEach(addHeader));

    if (headers.length === 0) {
        headers.push(jsonTableValueHeader());
    }

    return {
        rows: [
            headers,
            ...recordObjects.map(record => headers.map(header =>
                Object.prototype.hasOwnProperty.call(record, header) ? record[header] : ''))
        ],
        paths: [
            headers.map(() => null),
            ...recordObjects.map((_, rowIndex) => headers.map(header =>
                Object.prototype.hasOwnProperty.call(pathObjects[rowIndex] || {}, header)
                    ? pathObjects[rowIndex][header]
                    : null))
        ],
        columnCount: headers.length
    };
}

function tableFromJsonArray(items) {
    const recordObjects = items.map(item => flattenJsonRecord(item));
    const pathObjects = items.map((item, index) => prefixedPathObject(flattenJsonPaths(item), [index]));
    return tableFromRecordObjects(recordObjects, [], pathObjects);
}

function tableFromKeyedJsonObject(entries) {
    const keyHeader = jsonTableKeyHeader();
    const recordObjects = entries.map(([key, value]) => ({
        [keyHeader]: decodeUnicodeEscapes(key),
        ...flattenJsonRecord(value)
    }));
    const pathObjects = entries.map(([key, value]) => ({
        [keyHeader]: null,
        ...prefixedPathObject(flattenJsonPaths(value), [key])
    }));

    return tableFromRecordObjects(recordObjects, [keyHeader], pathObjects);
}

function tableFromJsonObject(value) {
    const entries = Object.entries(value);
    if (entries.length === 1 && Array.isArray(entries[0][1])) {
        return tableFromJsonArray(entries[0][1]);
    }

    if (entries.length > 0 && entries.every(([, child]) => isPlainJsonObject(child))) {
        return tableFromKeyedJsonObject(entries);
    }

    return {
        rows: [
            [jsonTableKeyHeader(), jsonTableValueHeader()],
            ...entries.map(([key, child]) => [decodeUnicodeEscapes(key), jsonCellValue(child)])
        ],
        paths: [
            [null, null],
            ...entries.map(([key]) => [null, [key]])
        ],
        columnCount: 2
    };
}

function tableFromJsonValue(value) {
    if (Array.isArray(value)) return tableFromJsonArray(value);
    if (isPlainJsonObject(value)) return tableFromJsonObject(value);

    return {
        rows: [[jsonTableValueHeader()], [jsonCellValue(value)]],
        paths: [[null], [[]]],
        columnCount: 1
    };
}

function currentJsonTableModel() {
    if (!isJsonCsvTableMode()) return null;

    const cacheKey = `${state.cacheVersion}:${state.lineCount}:${state.language}`;
    if (state.csvJsonTableModel?.cacheKey === cacheKey) {
        return state.csvJsonTableModel.model;
    }

    if (!allSourceLinesCached()) {
        requestMissingLines(1, state.lineCount);
        state.csvJsonTableModel = { cacheKey, model: null };
        return null;
    }

    try {
        const parsed = JSON.parse(sourceTextFromCache());
        const model = tableFromJsonValue(parsed);
        model.root = parsed;
        model.sourceText = sourceTextFromCache();
        state.csvJsonTableModel = { cacheKey, model };
        return model;
    } catch {
        state.csvJsonTableModel = { cacheKey, model: null };
        return null;
    }
}

function csvDisplayLineCount() {
    const model = currentJsonTableModel();
    return model ? Math.max(1, model.rows.length) : Math.max(1, state.lineCount);
}

function updateCsvVirtualLineCount(lineCount) {
    const next = isJsonCsvTableMode() && currentJsonTableModel()
        ? Math.max(1, Number(lineCount || 1))
        : 0;
    if (state.csvVirtualLineCount === next) return;

    state.csvVirtualLineCount = next;
    setupVirtualHeight();
}

function prepareCsvTableRenderModel() {
    const lineCount = csvDisplayLineCount();
    updateCsvVirtualLineCount(lineCount);
    return lineCount;
}

function csvCellsForLine(lineNumber) {
    const model = currentJsonTableModel();
    if (model) {
        return model.rows[Math.max(1, Number(lineNumber || 1)) - 1] || [];
    }

    return parseCsvLine(state.cache.get(lineNumber) || '');
}

function csvJsonCellPath(lineNumber, columnIndex) {
    const model = currentJsonTableModel();
    if (!model) return null;

    const row = model.paths?.[Math.max(1, Number(lineNumber || 1)) - 1];
    const path = row?.[Math.max(0, Number(columnIndex || 0))];
    return Array.isArray(path) ? path : null;
}

function canEditCsvCell(lineNumber, columnIndex) {
    if (!isJsonCsvTableMode()) return true;
    return !!csvJsonCellPath(lineNumber, columnIndex);
}

function csvClipboardCellValue(value) {
    const text = String(value ?? '');
    return isJsonCsvTableMode() ? decodeUnicodeEscapes(text) : text;
}

function getJsonPathValue(root, path) {
    let current = root;
    for (const segment of path || []) {
        if (current === null || current === undefined) return undefined;
        current = current[segment];
    }

    return current;
}

function setJsonPathValue(root, path, value) {
    if (!Array.isArray(path) || path.length === 0) {
        return value;
    }

    let current = root;
    for (let i = 0; i < path.length - 1; i++) {
        current = current[path[i]];
        if (current === null || current === undefined) return root;
    }
    current[path[path.length - 1]] = value;
    return root;
}

function detectJsonIndent(sourceText) {
    const match = String(sourceText || '').match(/\n([ \t]+)"/);
    if (!match) return 0;
    return match[1].includes('\t') ? '\t' : match[1].length;
}

function serializeJsonRoot(root, sourceText) {
    const indent = detectJsonIndent(sourceText);
    return JSON.stringify(root, null, indent || undefined);
}

function beginCsvEditTransaction() {
    post({ type: 'editTransactionStarted' });
}

function endCsvEditTransaction() {
    post({ type: 'editTransactionEnded' });
}

function replaceJsonDocumentText(nextText) {
    const nextLines = String(nextText ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
    const previousLineCount = Math.max(1, state.lineCount);

    state.cache.clear();
    nextLines.forEach((line, index) => state.cache.set(index + 1, line));
    state.lineCount = Math.max(1, nextLines.length);
    state.cacheVersion++;
    state.csvTableVersion++;
    state.csvJsonTableModel = null;
    state.searchDocumentVersion = -1;
    state.livePreviewLocalResourceVersion = String(Date.now());
    state.dirtyLines.clear();
    for (let line = 1; line <= state.lineCount; line++) {
        if (!cleanDirtyMarker(line)) {
            state.dirtyLines.set(line, line <= state.originalLines.length ? 'mod' : 'add');
        }
    }
    prepareCsvTableRenderModel();

    beginCsvEditTransaction();
    try {
        post({ type: 'lineChanged', lineNumber: 1, text: nextLines[0] || '' });
        if (previousLineCount === nextLines.length) {
            for (let line = 2; line <= nextLines.length; line++) {
                post({ type: 'lineChanged', lineNumber: line, text: nextLines[line - 1] || '' });
            }
        } else {
            for (let line = previousLineCount; line > 1; line--) {
                post({ type: 'deleteLine', lineNumber: line });
            }
            for (let line = 2; line <= nextLines.length; line++) {
                post({ type: 'insertLine', lineNumber: line, text: nextLines[line - 1] || '' });
            }
        }
        post({ type: 'contentChanged' });
    } finally {
        endCsvEditTransaction();
    }
}

function writeJsonCell(lineNumber, columnIndex, value, sourceElement = null, refreshFormula = true) {
    const model = currentJsonTableModel();
    const path = csvJsonCellPath(lineNumber, columnIndex);
    if (!model || !path) {
        updateCsvFormula();
        return;
    }

    const previousValue = getJsonPathValue(model.root, path);
    const nextValue = cellValueFromText(value, previousValue);
    const nextRoot = setJsonPathValue(model.root, path, nextValue);
    const nextText = serializeJsonRoot(nextRoot, model.sourceText);

    replaceJsonDocumentText(nextText);
    state.csvSelectedLine = Math.max(1, Number(lineNumber || 1));
    state.csvSelectedColumn = Math.max(0, Number(columnIndex || 0));
    state.csvEditMode = 'edit';
    state.csvPendingFocus = {
        line: state.csvSelectedLine,
        column: state.csvSelectedColumn,
        mode: 'edit',
        until: performance.now() + 1200
    };
    state.currentLine = state.csvSelectedLine;
    state.currentColumn = state.csvSelectedColumn + 1;

    if (sourceElement) {
        sourceElement.textContent = jsonCellValue(nextValue);
    }
    if (refreshFormula) {
        updateCsvFormula();
    }
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: jsonCellValue(nextValue) });
    queueRender(true);
}

function columnWidth(index) {
    ensureCsvState();
    const value = Number(state.csvTableColumnWidths[index] || DEFAULT_COLUMN_WIDTH);
    return Math.max(MIN_COLUMN_WIDTH, Math.min(800, value));
}

function visibleColumnCount(startLine, endLine) {
    ensureCsvState();
    const jsonModel = currentJsonTableModel();
    if (jsonModel) {
        return Math.min(
            MAX_TABLE_COLUMNS,
            Math.max(MIN_VISIBLE_COLUMNS, state.csvSelectedColumn + 1, jsonModel.columnCount));
    }

    let count = Math.max(DEFAULT_TABLE_COLUMNS, state.csvSelectedColumn + 1);
    for (let line = startLine; line <= endLine; line++) {
        if (!state.cache.has(line)) continue;
        count = Math.max(count, csvCellsForLine(line).length + 1);
    }
    return Math.min(MAX_TABLE_COLUMNS, count);
}

function csvGutterWidth() {
    const gutter = getComputedStyle(document.documentElement).getPropertyValue('--gutter-width').trim() || '56px';
    return Number(gutter.replace('px', '')) || 56;
}

function renderedColumnRange(columnCount) {
    const totalColumns = Math.max(MIN_VISIBLE_COLUMNS, Number(columnCount || 0));
    const scrollLeft = Math.max(0, Number(scrollContainer.scrollLeft || 0));
    const viewportWidth = Math.max(1, Number(scrollContainer.clientWidth || 1));
    const startX = Math.max(0, scrollLeft - csvGutterWidth() - COLUMN_OVERSCAN_PX);
    const endX = Math.max(startX, scrollLeft - csvGutterWidth() + viewportWidth + COLUMN_OVERSCAN_PX);

    let offset = 0;
    let start = 0;
    while (start < totalColumns - 1 && offset + columnWidth(start) < startX) {
        offset += columnWidth(start);
        start++;
    }

    let end = start;
    let visibleWidth = 0;
    while (end < totalColumns && offset + visibleWidth <= endX) {
        visibleWidth += columnWidth(end);
        end++;
    }
    end = Math.max(start + 1, end);

    let totalWidth = 0;
    for (let i = 0; i < totalColumns; i++) {
        totalWidth += columnWidth(i);
    }

    return {
        start,
        end: Math.min(totalColumns, end),
        leftWidth: offset,
        rightWidth: Math.max(0, totalWidth - offset - visibleWidth),
        totalContentWidth: totalWidth
    };
}

function applyCsvGridMetrics(columnCount, columnRange) {
    ensureCsvState();
    state.csvTableColumnCount = Math.max(MIN_VISIBLE_COLUMNS, Number(columnCount || 0));

    const widths = [];
    const range = columnRange || renderedColumnRange(state.csvTableColumnCount);
    if (range.leftWidth > 0) {
        widths.push(`${range.leftWidth}px`);
    }
    for (let i = range.start; i < range.end; i++) {
        widths.push(`${columnWidth(i)}px`);
    }
    if (range.rightWidth > 0) {
        widths.push(`${range.rightWidth}px`);
    }

    const gutterWidth = csvGutterWidth();
    const tableWidth = gutterWidth + range.totalContentWidth;
    document.documentElement.style.setProperty('--csv-grid-template', `${gutterWidth}px ${widths.join(' ')}`);
    document.documentElement.style.setProperty('--csv-table-width', `${tableWidth}px`);
}

function renderCsvHeader(columnCount, columnRange) {
    applyCsvGridMetrics(columnCount, columnRange);
    const cells = ['<div class="csv-corner"></div>'];

    if (columnRange.leftWidth > 0) {
        cells.push('<div class="csv-column-spacer" aria-hidden="true"></div>');
    }

    for (let i = columnRange.start; i < columnRange.end; i++) {
        const selectedClass = isCsvColumnSelected(i) ? ' selected-column' : '';
        cells.push(
            `<button class="csv-column-heading${selectedClass}" type="button" data-csv-column="${i}">` +
            `<span>${columnName(i)}</span>` +
            `<span class="csv-column-resizer" data-csv-column="${i}" aria-hidden="true"></span>` +
            `</button>`
        );
    }

    if (columnRange.rightWidth > 0) {
        cells.push('<div class="csv-column-spacer" aria-hidden="true"></div>');
    }

    csvColumnHeaderInner.innerHTML = `<div class="csv-header-row">${cells.join('')}</div>`;
    syncCsvHeaderScroll();
    updateCsvFormula();
}

function updateCsvFormula() {
    ensureCsvState();
    const line = Math.max(1, Math.min(csvDisplayLineCount(), Number(state.csvSelectedLine || state.currentLine || 1)));
    const column = Math.max(0, Number(state.csvSelectedColumn || 0));
    const cells = csvCellsForLine(line);
    csvNameBox.value = `${columnName(column)}${line}`;
    csvFormulaInput.value = cells[column] || '';
    csvFormulaInput.readOnly = state.readOnly || !canEditCsvCell(line, column);
}

function setSelectedCell(lineNumber, columnIndex, focusFormula = false) {
    ensureCsvState();
    state.csvSelectedLine = Math.max(1, Math.min(csvDisplayLineCount(), Number(lineNumber || 1)));
    state.csvSelectedColumn = Math.max(0, Number(columnIndex || 0));
    state.currentLine = state.csvSelectedLine;
    state.currentColumn = state.csvSelectedColumn + 1;
    state.selection = null;
    state.csvEditMode = 'select';
    state.csvSelection = {
        mode: 'cells',
        startLine: state.csvSelectedLine,
        startColumn: state.csvSelectedColumn,
        endLine: state.csvSelectedLine,
        endColumn: state.csvSelectedColumn
    };
    state.csvSelectedRows = [];
    state.csvSelectedColumns = [];
    syncCustomSelectionClass();

    viewport.querySelectorAll('.csv-cell.selected-cell').forEach(cell => cell.classList.remove('selected-cell'));
    viewport
        .querySelector(`.csv-cell[data-line="${state.csvSelectedLine}"][data-csv-column="${state.csvSelectedColumn}"]`)
        ?.classList.add('selected-cell');

    updateCsvFormula();
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: selectedCsvText() });

    if (focusFormula) {
        csvFormulaInput.focus();
        csvFormulaInput.select();
    }
}

function writeCsvCell(lineNumber, columnIndex, value, sourceElement = null, refreshFormula = true, isComposing = false) {
    ensureCsvState();
    if (isJsonCsvTableMode()) {
        writeJsonCell(lineNumber, columnIndex, value, sourceElement, refreshFormula);
        return;
    }

    const line = Math.max(1, Number(lineNumber || 1));
    const column = Math.max(0, Number(columnIndex || 0));
    const cells = parseCsvLine(state.cache.get(line) || '');
    while (cells.length <= column) {
        cells.push('');
    }

    cells[column] = String(value ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const nextText = serializeCsvRow(cells);

    state.cache.set(line, nextText);
    state.cacheVersion++;
    state.csvTableVersion++;
    state.csvSelectedLine = line;
    state.csvSelectedColumn = column;
    state.csvEditMode = 'edit';
    state.csvPendingFocus = {
        line,
        column,
        mode: 'edit',
        until: performance.now() + 1200
    };
    state.currentLine = line;
    state.currentColumn = column + 1;
    invalidateMeasuredLineHeightsAround(line, 2);

    if (!isComposing && !cleanDirtyMarker(line)) {
        markDirty(line, 'mod');
    }

    post({ type: 'lineChanged', lineNumber: line, text: nextText, isComposing: !!isComposing });
    post({ type: 'contentChanged', isComposing: !!isComposing });
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: String(value ?? '') });

    if (sourceElement) {
        sourceElement.textContent = String(value ?? '');
    }
    if (refreshFormula) {
        updateCsvFormula();
    }
}

function activeCellMatches(lineNumber, columnIndex) {
    return Number(lineNumber || 0) === Number(state.csvSelectedLine || 0) &&
        Number(columnIndex || 0) === Number(state.csvSelectedColumn || 0);
}

function normalizedCsvSelection() {
    ensureCsvState();
    const rowCount = csvDisplayLineCount();
    const sel = state.csvSelection;
    if (!sel) {
        return {
            mode: 'cells',
            startLine: state.csvSelectedLine,
            endLine: state.csvSelectedLine,
            startColumn: state.csvSelectedColumn,
            endColumn: state.csvSelectedColumn
        };
    }

    if (sel.mode === 'columns') {
        if (Array.isArray(sel.columns)) {
            const columns = sel.columns
                .map(column => Math.max(0, Number(column || 0)))
                .filter(column => column >= 0 && column < state.csvTableColumnCount)
                .sort((a, b) => a - b);
            return {
                mode: 'columns',
                columns,
                startLine: 1,
                endLine: rowCount,
                startColumn: columns[0] ?? 0,
                endColumn: columns[columns.length - 1] ?? 0
            };
        }

        const startColumn = Math.min(Number(sel.startColumn || 0), Number(sel.endColumn || 0));
        const endColumn = Math.max(Number(sel.startColumn || 0), Number(sel.endColumn || 0));
        return {
            mode: 'columns',
            startLine: 1,
            endLine: rowCount,
            startColumn,
            endColumn
        };
    }

    if (sel.mode === 'rows') {
        const rows = Array.isArray(sel.rows) ? sel.rows : [];
        return {
            mode: 'rows',
            rows: rows
                .map(row => Math.max(1, Number(row || 1)))
                .filter(row => row >= 1 && row <= rowCount)
                .sort((a, b) => a - b),
            startLine: 1,
            endLine: rowCount,
            startColumn: 0,
            endColumn: Math.max(0, state.csvTableColumnCount - 1)
        };
    }

    return {
        mode: 'cells',
        startLine: Math.max(1, Math.min(rowCount, Math.min(Number(sel.startLine || 1), Number(sel.endLine || 1)))),
        endLine: Math.max(1, Math.min(rowCount, Math.max(Number(sel.startLine || 1), Number(sel.endLine || 1)))),
        startColumn: Math.min(Number(sel.startColumn || 0), Number(sel.endColumn || 0)),
        endColumn: Math.max(Number(sel.startColumn || 0), Number(sel.endColumn || 0))
    };
}

function isCsvCellSelected(lineNumber, columnIndex) {
    const sel = normalizedCsvSelection();
    if (sel.mode === 'rows') {
        return sel.rows.includes(Number(lineNumber || 0));
    }
    if (sel.mode === 'columns' && Array.isArray(sel.columns)) {
        return sel.columns.includes(Number(columnIndex || 0));
    }

    return lineNumber >= sel.startLine &&
        lineNumber <= sel.endLine &&
        columnIndex >= sel.startColumn &&
        columnIndex <= sel.endColumn;
}

function isCsvColumnSelected(columnIndex) {
    const sel = normalizedCsvSelection();
    if (sel.mode === 'rows') return false;
    if (Array.isArray(sel.columns)) return sel.columns.includes(Number(columnIndex || 0));
    return columnIndex >= sel.startColumn && columnIndex <= sel.endColumn;
}

function isCsvRowSelected(lineNumber) {
    const sel = normalizedCsvSelection();
    return sel.mode === 'rows' && sel.rows.includes(Number(lineNumber || 0));
}

function selectedCsvText() {
    const sel = normalizedCsvSelection();
    if (sel.mode === 'rows') {
        const rows = sel.rows.length > 0 ? sel.rows : [state.csvSelectedLine];
        let width = 1;
        for (const line of rows) {
            width = Math.max(width, csvCellsForLine(line).length);
        }

        return rows.map(line => {
            const cells = csvCellsForLine(line);
            const row = [];
            for (let column = 0; column < width; column++) {
                row.push(csvClipboardCellValue(cells[column] || ''));
            }
            return row.join('\t');
        }).join('\n');
    }

    const selectedColumns = sel.mode === 'columns' && Array.isArray(sel.columns) && sel.columns.length > 0
        ? sel.columns
        : null;
    const lines = [];
    for (let line = sel.startLine; line <= sel.endLine; line++) {
        const cells = csvCellsForLine(line);
        const row = [];
        const columns = selectedColumns ?? Array.from(
            { length: sel.endColumn - sel.startColumn + 1 },
            (_, index) => sel.startColumn + index);
        for (const column of columns) {
            row.push(csvClipboardCellValue(cells[column] || ''));
        }
        lines.push(row.join('\t'));
    }
    return lines.join('\n');
}

async function copyCsvSelectionToClipboard() {
    const text = selectedCsvText();
    if (!text) return false;
    return await writeClipboardText(text);
}

async function cutCsvSelectionToClipboard() {
    const copied = await copyCsvSelectionToClipboard();
    if (copied && !state.readOnly && !isJsonCsvTableMode()) {
        clearCsvSelection();
    }

    return copied;
}

function clearCsvSelection() {
    ensureCsvState();
    if (isJsonCsvTableMode()) return;

    const sel = normalizedCsvSelection();
    
    let linesToModify = [];
    if (sel.mode === 'rows') {
        linesToModify = sel.rows.length > 0 ? sel.rows : [state.csvSelectedLine];
    } else {
        for (let line = sel.startLine; line <= sel.endLine; line++) {
            linesToModify.push(line);
        }
    }

    let anyChanged = false;
    for (const line of linesToModify) {
        const cells = parseCsvLine(state.cache.get(line) || '');
        let lineChanged = false;

        if (sel.mode === 'rows') {
            for (let col = 0; col < cells.length; col++) {
                if (cells[col] !== '') {
                    cells[col] = '';
                    lineChanged = true;
                }
            }
        } else if (sel.mode === 'columns') {
            const columns = sel.columns ?? Array.from(
                { length: sel.endColumn - sel.startColumn + 1 },
                (_, index) => sel.startColumn + index);
            for (const col of columns) {
                while (cells.length <= col) {
                    cells.push('');
                }
                if (cells[col] !== '') {
                    cells[col] = '';
                    lineChanged = true;
                }
            }
        } else {
            for (let col = sel.startColumn; col <= sel.endColumn; col++) {
                while (cells.length <= col) {
                    cells.push('');
                }
                if (cells[col] !== '') {
                    cells[col] = '';
                    lineChanged = true;
                }
            }
        }

        if (lineChanged) {
            const nextText = serializeCsvRow(cells);
            state.cache.set(line, nextText);
            state.cacheVersion++;
            state.csvTableVersion++;
            
            if (!cleanDirtyMarker(line)) {
                markDirty(line, 'mod');
            }
            invalidateMeasuredLineHeightsAround(line, 2);
            post({ type: 'lineChanged', lineNumber: line, text: nextText, isComposing: false });
            anyChanged = true;
        }
    }

    if (anyChanged) {
        post({ type: 'contentChanged', isComposing: false });
    }

    state.csvPendingFocus = {
        line: state.csvSelectedLine,
        column: state.csvSelectedColumn,
        mode: 'select',
        until: performance.now() + 900
    };
    updateCsvFormula();
    queueRender(true);
    requestAnimationFrame(() => restoreCsvFocusAfterRender());
}

function setCsvRangeSelection(startLine, startColumn, endLine, endColumn, mode = 'cells') {
    ensureCsvState();
    const rowCount = csvDisplayLineCount();
    state.csvSelection = {
        mode,
        startLine: Math.max(1, Math.min(rowCount, Number(startLine || 1))),
        startColumn: Math.max(0, Number(startColumn || 0)),
        endLine: Math.max(1, Math.min(rowCount, Number(endLine || 1))),
        endColumn: Math.max(0, Number(endColumn || 0))
    };
    state.csvSelectedRows = [];
    state.csvSelectedColumns = mode === 'columns'
        ? Array.from(
            { length: Math.abs(Math.max(0, Number(endColumn || 0)) - Math.max(0, Number(startColumn || 0))) + 1 },
            (_, index) => Math.min(Math.max(0, Number(startColumn || 0)), Math.max(0, Number(endColumn || 0))) + index)
        : [];
    const normalized = normalizedCsvSelection();
    state.csvSelectedLine = Math.max(1, Math.min(rowCount, mode === 'columns' ? state.csvSelectedLine : normalized.startLine));
    state.csvSelectedColumn = normalized.startColumn;
    state.currentLine = state.csvSelectedLine;
    state.currentColumn = state.csvSelectedColumn + 1;
    state.csvTableVersion++;
    updateCsvFormula();
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: selectedCsvText() });
    queueRender(true);
}

function setCsvColumnSelection(columnIndex, event) {
    ensureCsvState();
    const column = Math.max(0, Math.min(state.csvTableColumnCount - 1, Number(columnIndex || 0)));
    const ctrl = !!(event?.ctrlKey || event?.metaKey);
    const shift = !!event?.shiftKey;
    const currentColumns = new Set((state.csvSelectedColumns || []).map(col => Number(col || 0)).filter(col => col >= 0));
    const anchor = Math.max(0, Math.min(state.csvTableColumnCount - 1, Number(state.csvColumnSelectionAnchor ?? state.csvSelectedColumn ?? column)));

    if (shift) {
        const start = Math.min(anchor, column);
        const end = Math.max(anchor, column);
        if (!ctrl) {
            currentColumns.clear();
        }
        for (let col = start; col <= end; col++) {
            currentColumns.add(col);
        }
    } else if (ctrl) {
        if (currentColumns.has(column)) {
            currentColumns.delete(column);
        } else {
            currentColumns.add(column);
        }
        state.csvColumnSelectionAnchor = column;
    } else {
        currentColumns.clear();
        currentColumns.add(column);
        state.csvColumnSelectionAnchor = column;
    }

    if (currentColumns.size === 0) {
        currentColumns.add(column);
    }

    const columns = [...currentColumns].sort((a, b) => a - b);
    state.csvSelectedColumns = columns;
    state.csvSelectedRows = [];
    state.csvSelection = {
        mode: 'columns',
        columns
    };
    state.csvSelectedColumn = column;
    state.csvEditMode = 'select';
    state.csvTableVersion++;
    updateCsvFormula();
    post({ type: 'cursorChanged', line: state.csvSelectedLine, column: state.csvSelectedColumn + 1 });
    post({ type: 'selectionResult', text: selectedCsvText() });
    queueRender(true);
}

function setCsvRowSelection(lineNumber, event) {
    ensureCsvState();
    const rowCount = csvDisplayLineCount();
    const line = Math.max(1, Math.min(rowCount, Number(lineNumber || 1)));
    const ctrl = !!(event?.ctrlKey || event?.metaKey);
    const shift = !!event?.shiftKey;
    const currentRows = new Set((state.csvSelectedRows || []).map(row => Number(row || 0)).filter(row => row >= 1));
    const anchor = Math.max(1, Math.min(rowCount, Number(state.csvRowSelectionAnchor || state.csvSelectedLine || line)));

    if (shift) {
        const start = Math.min(anchor, line);
        const end = Math.max(anchor, line);
        if (!ctrl) {
            currentRows.clear();
        }
        for (let row = start; row <= end; row++) {
            currentRows.add(row);
        }
    } else if (ctrl) {
        if (currentRows.has(line)) {
            currentRows.delete(line);
        } else {
            currentRows.add(line);
        }
        state.csvRowSelectionAnchor = line;
    } else {
        currentRows.clear();
        currentRows.add(line);
        state.csvRowSelectionAnchor = line;
    }

    if (currentRows.size === 0) {
        currentRows.add(line);
    }

    const rows = [...currentRows].sort((a, b) => a - b);
    state.csvSelectedRows = rows;
    state.csvSelectedColumns = [];
    state.csvSelection = {
        mode: 'rows',
        rows
    };
    state.csvSelectedLine = line;
    state.csvSelectedColumn = 0;
    state.csvEditMode = 'select';
    state.csvTableVersion++;
    updateCsvFormula();
    post({ type: 'cursorChanged', line: state.csvSelectedLine, column: 1 });
    post({ type: 'selectionResult', text: selectedCsvText() });
    queueRender(true);
}

function insertCsvLine(lineNumber, text = '') {
    if (isJsonCsvTableMode()) return;

    const targetLine = Math.max(1, Math.min(Number(lineNumber || 1), state.lineCount + 1));
    shiftCachedLines(targetLine, 1);
    state.cache.set(targetLine, String(text ?? ''));
    state.lineCount++;
    state.cacheVersion++;
    state.csvTableVersion++;
    state.dirtyLines.set(targetLine, 'add');
    setupVirtualHeight();
    post({ type: 'insertLine', lineNumber: targetLine, text: String(text ?? '') });
    post({ type: 'contentChanged' });
}

function moveCsvFocus(lineNumber, columnIndex) {
    const line = Math.max(1, Math.min(csvDisplayLineCount(), Number(lineNumber || 1)));
    const column = Math.max(0, Number(columnIndex || 0));
    state.csvSelectedLine = line;
    state.csvSelectedColumn = column;
    state.currentLine = state.csvSelectedLine;
    state.currentColumn = state.csvSelectedColumn + 1;
    state.selection = null;
    state.csvEditMode = 'select';
    state.csvSelection = {
        mode: 'cells',
        startLine: state.csvSelectedLine,
        startColumn: state.csvSelectedColumn,
        endLine: state.csvSelectedLine,
        endColumn: state.csvSelectedColumn
    };
    state.csvSelectedRows = [];
    state.csvSelectedColumns = [];
    syncCustomSelectionClass();

    state.csvPendingFocus = {
        line,
        column,
        mode: 'select',
        until: performance.now() + 900
    };
    ensureColumnVisible(column);
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: selectedCsvText() });
    queueRender(true);
    requestAnimationFrame(() => restoreCsvFocusAfterRender());
}

function moveCsvFocusOrInsert(lineNumber, columnIndex) {
    let targetLine = Number(lineNumber || 1);
    if (isJsonCsvTableMode()) {
        moveCsvFocus(targetLine, columnIndex);
        return;
    }

    if (targetLine > state.lineCount) {
        insertCsvLine(state.lineCount + 1, '');
        targetLine = state.lineCount;
    }
    moveCsvFocus(targetLine, columnIndex);
}

function focusCsvCell(lineNumber, columnIndex, mode = null) {
    const cell = viewport.querySelector(`.csv-cell[data-line="${lineNumber}"][data-csv-column="${columnIndex}"]`);
    if (!cell || cell.getAttribute('contenteditable') !== 'true') return false;

    const focusMode = mode || state.csvEditMode || 'select';
    cell.focus({ preventScroll: true });
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(cell);
    if (focusMode === 'edit') {
        range.collapse(false);
    }
    selection?.removeAllRanges();
    selection?.addRange(range);
    updateCsvFormula();
    return true;
}

function beginCsvEdit(lineNumber, columnIndex, mode = 'edit') {
    ensureCsvState();
    state.csvSelectedLine = Math.max(1, Number(lineNumber || 1));
    state.csvSelectedColumn = Math.max(0, Number(columnIndex || 0));
    state.csvEditMode = mode === 'select' ? 'select' : 'edit';
    state.csvSelection = {
        mode: 'cells',
        startLine: state.csvSelectedLine,
        startColumn: state.csvSelectedColumn,
        endLine: state.csvSelectedLine,
        endColumn: state.csvSelectedColumn
    };
    state.csvPendingFocus = {
        line: state.csvSelectedLine,
        column: state.csvSelectedColumn,
        mode: state.csvEditMode,
        until: performance.now() + 900
    };
    state.csvTableVersion++;
    queueRender(true);
    requestAnimationFrame(() => restoreCsvFocusAfterRender());
}

function restoreCsvFocusAfterRender() {
    if (!state.csvTableEnabled || !state.csvPendingFocus) return;
    if (csvDragState) return; // Do not focus cells while dragging to prevent browser text selection from interfering.

    const pending = state.csvPendingFocus;
    if (performance.now() > Number(pending.until || 0)) {
        state.csvPendingFocus = null;
        return;
    }

    if (pending.mode) {
        state.csvEditMode = pending.mode;
    }

    if (focusCsvCell(pending.line, pending.column, pending.mode)) {
        state.csvSelectedLine = pending.line;
        state.csvSelectedColumn = pending.column;
    }
}

function renderCsvTableRows(startLine, endLine, hoveredLineNumber) {
    const jsonModel = currentJsonTableModel();
    const columnCount = visibleColumnCount(startLine, endLine);
    const columnRange = renderedColumnRange(columnCount);
    renderCsvHeader(columnCount, columnRange);

    const rows = [];
    for (let line = startLine; line <= endLine; line++) {
        const hasLine = jsonModel ? line <= jsonModel.rows.length : state.cache.has(line);
        const cells = hasLine ? csvCellsForLine(line) : [];
        const dirtyType = jsonModel ? '' : state.dirtyLines.get(line);
        const dirtyClass = dirtyType ? ` dirty-${dirtyType}` : '';
        const hoveredClass = line === hoveredLineNumber ? ' hovered-row' : '';
        const rowSelected = isCsvRowSelected(line);
        const selectedRowClass = rowSelected || line === state.csvSelectedLine ? ' selected-csv-row' : '';
        const rowHeadingClass = rowSelected ? ' selected-row-heading' : '';
        const lineCells = [`<div class="line-number csv-row-heading${rowHeadingClass}">${line}</div>`];

        if (columnRange.leftWidth > 0) {
            lineCells.push('<div class="csv-cell-spacer" aria-hidden="true"></div>');
        }

        for (let column = columnRange.start; column < columnRange.end; column++) {
            const activeClass = line === state.csvSelectedLine && column === state.csvSelectedColumn ? ' active-cell' : '';
            const selectedClass = isCsvCellSelected(line, column) ? ' selected-cell' : '';
            const loadingClass = hasLine ? '' : ' loading';
            const value = hasLine ? (cells[column] || '') : '';
            const editable = !state.readOnly && hasLine && activeCellMatches(line, column) && canEditCsvCell(line, column) ? 'true' : 'false';
            lineCells.push(
                `<div class="csv-cell${selectedClass}${activeClass}${loadingClass}" contenteditable="${editable}" spellcheck="false" ` +
                `data-line="${line}" data-csv-column="${column}">${escapeHtml(value)}</div>`
            );
        }

        if (columnRange.rightWidth > 0) {
            lineCells.push('<div class="csv-cell-spacer" aria-hidden="true"></div>');
        }

        rows.push(
            `<div class="line-row csv-row${hoveredClass}${selectedRowClass}${dirtyClass}" data-line="${line}">` +
            lineCells.join('') +
            `</div>`
        );
    }

    return rows;
}

function setCsvTableMode(enabled, options = {}) {
    ensureCsvState();
    state.csvTableEnabled = !!enabled;
    state.csvTableVersion++;
    updateCsvLocalization(options);
    if (state.csvTableEnabled) {
        prepareCsvTableRenderModel();
    } else {
        updateCsvVirtualLineCount(0);
        csvFormulaInput.readOnly = false;
    }

    document.body.classList.toggle('csv-table-mode', state.csvTableEnabled);
    csvToolbar.hidden = !state.csvTableEnabled;
    csvColumnHeader.hidden = !state.csvTableEnabled;
    state.lastRangeKey = '';
    queueRender(true);
}

function updateCsvLocalization(options = {}) {
    if (options.csvNameBoxPlaceholder !== undefined) {
        csvNameBox.placeholder = options.csvNameBoxPlaceholder;
    }
    if (options.csvFormulaPlaceholder !== undefined) {
        csvFormulaInput.placeholder = options.csvFormulaPlaceholder;
    }
    if (options.csvJsonKeyHeader !== undefined) {
        state.csvJsonKeyHeader = options.csvJsonKeyHeader;
        state.csvJsonTableModel = null;
    }
    if (options.csvJsonValueHeader !== undefined) {
        state.csvJsonValueHeader = options.csvJsonValueHeader;
        state.csvJsonTableModel = null;
    }
}

function syncCsvHeaderScroll() {
    if (!state.csvTableEnabled) return;
    csvColumnHeaderInner.style.transform = `translateX(${-scrollContainer.scrollLeft}px)`;
}

function columnOffset(columnIndex) {
    const target = Math.max(0, Number(columnIndex || 0));
    let offset = csvGutterWidth();
    for (let i = 0; i < target; i++) {
        offset += columnWidth(i);
    }
    return offset;
}

function ensureColumnVisible(columnIndex) {
    const left = columnOffset(columnIndex);
    const right = left + columnWidth(columnIndex);
    const currentLeft = scrollContainer.scrollLeft;
    const currentRight = currentLeft + Math.max(1, scrollContainer.clientWidth);
    if (left < currentLeft + csvGutterWidth()) {
        scrollContainer.scrollLeft = Math.max(0, left - csvGutterWidth());
    } else if (right > currentRight) {
        scrollContainer.scrollLeft = right - Math.max(1, scrollContainer.clientWidth) + 12;
    }
}

function bindCsvTable() {
    viewport.addEventListener('pointerdown', event => {
        const rowHeading = event.target.closest?.('.csv-row-heading');
        if (rowHeading && viewport.contains(rowHeading) && event.button === 0) {
            event.preventDefault();
            const row = rowHeading.closest('.csv-row');
            setCsvRowSelection(Number(row?.dataset.line || 1), event);
            return;
        }

        const cell = event.target.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell) || event.button !== 0) return;
        event.preventDefault();
        const line = Number(cell.dataset.line || 1);
        const column = Number(cell.dataset.csvColumn || 0);
        if (event.detail >= 2) {
            beginCsvEdit(line, column, 'edit');
            return;
        }
        csvDragState = { mode: 'cells', startLine: line, startColumn: column, pointerId: event.pointerId };
        try {
            viewport.setPointerCapture(event.pointerId);
        } catch (e) {}
        document.body.classList.add('csv-selecting');
        setSelectedCell(line, column);
        beginCsvEdit(line, column, 'select');
    });

    viewport.addEventListener('pointermove', event => {
        if (!csvDragState || csvDragState.mode !== 'cells') return;
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const cell = hit?.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell)) return;
        const line = Number(cell.dataset.line || 1);
        const column = Number(cell.dataset.csvColumn || 0);
        setCsvRangeSelection(csvDragState.startLine, csvDragState.startColumn, line, column, 'cells');
    });

    viewport.addEventListener('focusin', event => {
        const cell = event.target.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell)) return;
        if (!activeCellMatches(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0))) {
            setSelectedCell(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0));
        }
    });

    viewport.addEventListener('dblclick', event => {
        const cell = event.target.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell)) return;
        event.preventDefault();
        beginCsvEdit(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0), 'edit');
    });

    viewport.addEventListener('input', event => {
        const cell = event.target.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell) || cell.getAttribute('contenteditable') !== 'true') return;
        state.csvEditMode = 'edit';
        writeCsvCell(
            Number(cell.dataset.line || 1),
            Number(cell.dataset.csvColumn || 0),
            cell.textContent || '',
            null,
            true,
            state.csvCellComposing || event.isComposing);
    });

    document.addEventListener('compositionstart', event => {
        const cell = event.target.closest?.('.csv-cell');
        const isFormula = event.target === csvFormulaInput;
        if ((cell && viewport.contains(cell)) || isFormula) {
            state.csvCellComposing = true;
        }
    });

    document.addEventListener('compositionend', event => {
        const cell = event.target.closest?.('.csv-cell');
        const isFormula = event.target === csvFormulaInput;
        if ((cell && viewport.contains(cell)) || isFormula) {
            state.csvCellComposing = false;
            if (cell && viewport.contains(cell)) {
                writeCsvCell(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0), cell.textContent || '');
            }
        }
    });

    viewport.addEventListener('keydown', event => {
        const cell = event.target.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell)) return;

        const line = Number(cell.dataset.line || 1);
        const column = Number(cell.dataset.csvColumn || 0);
        if (event.key === 'Tab') {
            event.preventDefault();
            moveCsvFocus(line, column + (event.shiftKey ? -1 : 1));
        } else if (event.key === 'Enter') {
            event.preventDefault();
            if (event.shiftKey) {
                moveCsvFocus(line - 1, column);
            } else {
                moveCsvFocusOrInsert(line + 1, column);
            }
        } else if (event.key === 'ArrowUp') {
            event.preventDefault();
            moveCsvFocus(line - 1, column);
        } else if (event.key === 'ArrowDown') {
            event.preventDefault();
            moveCsvFocus(line + 1, column);
        }
    });

    document.addEventListener('paste', event => {
        const cell = event.target?.closest?.('.csv-cell');
        if (!cell || !viewport.contains(cell)) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const text = event.clipboardData?.getData('text/plain') || '';
        writeCsvCell(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0), text, cell);
    }, true);

    document.addEventListener('keydown', event => {
        if (!state.csvTableEnabled) return;
        if (event.key === 'Delete') {
            const tag = event.target?.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA') return;
            if (state.csvEditMode === 'select' && !state.readOnly) {
                event.preventDefault();
                clearCsvSelection();
            }
        }
    }, true);

    document.addEventListener('copy', event => {
        const cell = event.target?.closest?.('.csv-cell');
        if (!cell && (event.target?.tagName === 'INPUT' || event.target?.tagName === 'TEXTAREA')) return;
        const isCsvSelectionActive = state.csvTableEnabled && state.csvSelection;
        if ((!cell || !viewport.contains(cell)) && !isCsvSelectionActive) return;
        event.clipboardData?.setData('text/plain', selectedCsvText());
        event.preventDefault();
        event.stopImmediatePropagation();
    }, true);

    document.addEventListener('cut', event => {
        const cell = event.target?.closest?.('.csv-cell');
        if (!cell && (event.target?.tagName === 'INPUT' || event.target?.tagName === 'TEXTAREA')) return;
        const isCsvSelectionActive = state.csvTableEnabled && state.csvSelection;
        if ((!cell || !viewport.contains(cell)) && !isCsvSelectionActive) return;
        if (state.readOnly) return;

        if (state.csvEditMode === 'select') {
            event.clipboardData?.setData('text/plain', selectedCsvText());
            event.preventDefault();
            event.stopImmediatePropagation();
            clearCsvSelection();
        } else if (cell) {
            event.clipboardData?.setData('text/plain', csvClipboardCellValue(cell.textContent || ''));
            event.preventDefault();
            event.stopImmediatePropagation();
            writeCsvCell(Number(cell.dataset.line || 1), Number(cell.dataset.csvColumn || 0), '', cell);
        }
    }, true);

    csvFormulaInput.addEventListener('input', () => {
        ensureCsvState();
        writeCsvCell(state.csvSelectedLine, state.csvSelectedColumn, csvFormulaInput.value, null, false);
        const cell = viewport.querySelector(`.csv-cell[data-line="${state.csvSelectedLine}"][data-csv-column="${state.csvSelectedColumn}"]`);
        if (cell) cell.textContent = csvFormulaInput.value;
    });

    csvFormulaInput.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            moveCsvFocusOrInsert(state.csvSelectedLine + 1, state.csvSelectedColumn);
        }
    });

    csvColumnHeader.addEventListener('pointerdown', event => {
        const resizer = event.target.closest?.('.csv-column-resizer');
        if (resizer) {
            event.preventDefault();
            event.stopPropagation();
            const column = Number(resizer.dataset.csvColumn || 0);
            resizeState = {
                column,
                startX: event.clientX,
                startWidth: columnWidth(column)
            };
            resizer.setPointerCapture?.(event.pointerId);
            document.body.classList.add('csv-resizing');
            return;
        }

        const heading = event.target.closest?.('.csv-column-heading');
        if (!heading || event.button !== 0) return;
        event.preventDefault();
        const column = Number(heading.dataset.csvColumn || 0);
        if (event.ctrlKey || event.metaKey || event.shiftKey) {
            setCsvColumnSelection(column, event);
            return;
        }

        state.csvColumnSelectionAnchor = column;
        csvDragState = { mode: 'columns', startColumn: column, pointerId: event.pointerId };
        try {
            csvColumnHeader.setPointerCapture(event.pointerId);
        } catch (e) {}
        document.body.classList.add('csv-selecting');
        setCsvRangeSelection(1, column, csvDisplayLineCount(), column, 'columns');
    });

    csvColumnHeader.addEventListener('pointermove', event => {
        if (!csvDragState || csvDragState.mode !== 'columns') return;
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const heading = hit?.closest?.('.csv-column-heading');
        if (!heading || !csvColumnHeader.contains(heading)) return;
        const column = Number(heading.dataset.csvColumn || 0);
        setCsvRangeSelection(1, csvDragState.startColumn, csvDisplayLineCount(), column, 'columns');
    });

    window.addEventListener('pointermove', event => {
        if (!resizeState) return;
        const width = Math.max(MIN_COLUMN_WIDTH, resizeState.startWidth + event.clientX - resizeState.startX);
        state.csvTableColumnWidths[resizeState.column] = width;
        state.csvTableVersion++;
        applyCsvGridMetrics(state.csvTableColumnCount, renderedColumnRange(state.csvTableColumnCount));
    });

    window.addEventListener('pointerup', () => {
        if (!resizeState) return;
        resizeState = null;
        document.body.classList.remove('csv-resizing');
        queueRender(true);
    });

    window.addEventListener('pointerup', event => {
        if (!csvDragState) return;
        const finalDragState = csvDragState;
        if (csvDragState.pointerId !== undefined) {
            try {
                if (csvDragState.mode === 'cells') {
                    viewport.releasePointerCapture(csvDragState.pointerId);
                } else if (csvDragState.mode === 'columns') {
                    csvColumnHeader.releasePointerCapture(csvDragState.pointerId);
                }
            } catch (e) {}
        }
        csvDragState = null;
        document.body.classList.remove('csv-selecting');

        if (finalDragState.mode === 'cells') {
            state.csvPendingFocus = {
                line: state.csvSelectedLine,
                column: state.csvSelectedColumn,
                mode: 'select',
                until: performance.now() + 900
            };
            restoreCsvFocusAfterRender();
        }
    });
}

export {
    bindCsvTable,
    copyCsvSelectionToClipboard,
    cutCsvSelectionToClipboard,
    isJsonCsvTableMode,
    prepareCsvTableRenderModel,
    renderCsvTableRows,
    restoreCsvFocusAfterRender,
    selectedCsvText,
    setCsvTableMode,
    syncCsvHeaderScroll,
    updateCsvLocalization
};
