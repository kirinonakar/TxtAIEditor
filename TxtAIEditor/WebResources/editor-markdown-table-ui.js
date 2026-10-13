import { scrollContainer, viewport } from './editor-dom.js';
import { clearCustomSelectionVisuals, csvTableMode, imeController, post, selectionController, state, syncCustomSelectionClass } from './editor-core.js';
import { commitLine, replaceSelectionWith, setCaret } from './editor-commands.js';
import { changeMarkdownTable, readMarkdownTable, tableSourceLine } from './editor-markdown-table-model.js';

let selected = null;
let toolbar = null;
let cellMode = false;
let dragPointer = null;
let bound = false;
let documentId = null;
let currentRendered = null;

const actions = {
    row: ['row-before', 'row-after', 'row-delete', 'row-up', 'row-down', 'select-row'],
    column: ['column-before', 'column-after', 'column-delete', 'column-left', 'column-right', 'select-column']
};

function enabled() {
    return state.inlineLivePreviewEnabled && ['markdown', 'md'].includes(state.language) && !csvTableMode.isEnabled;
}

function sourceTable() {
    return selected && readMarkdownTable(selected.startLine, state.lineCount, line => state.cache.get(line));
}

function renderedTable(startLine) {
    return [...viewport.querySelectorAll('.line-row table')].find(table => Number(table.closest('.line-row')?.dataset.line) === startLine);
}

function hitCell(target) {
    const cell = target?.closest?.('td, th');
    const table = cell?.closest('table');
    const startLine = Number(table?.closest('.line-row')?.dataset.line || 0);
    const source = enabled() && startLine && readMarkdownTable(startLine, state.lineCount, line => state.cache.get(line));
    if (!cell || !source) return null;
    const row = [...table.rows].indexOf(cell.parentElement);
    const column = [...cell.parentElement.cells].indexOf(cell);
    return row >= 0 && column >= 0 ? { startLine, row, column, cell, table, source } : null;
}

function bounds() {
    if (!selected) return null;
    return { top: Math.min(selected.anchor.row, selected.focus.row), bottom: Math.max(selected.anchor.row, selected.focus.row),
        left: Math.min(selected.anchor.column, selected.focus.column), right: Math.max(selected.anchor.column, selected.focus.column) };
}

function clearTextSelection() {
    selectionController.clear();
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();
}

export function resetMarkdownTableUi() {
    selected = null;
    dragPointer = null;
    cellMode = false;
    currentRendered = null;
    if (toolbar) toolbar.hidden = true;
    viewport.querySelectorAll('.md-table-cell-selected, .md-table-cell-active').forEach(cell => {
        cell.classList.remove('md-table-cell-selected', 'md-table-cell-active');
        cell.removeAttribute('aria-selected');
    });
}

export function markdownTableSelection() {
    return selected ? { startLine: selected.startLine, ...bounds(), row: selected.focus.row, column: selected.focus.column } : null;
}

function label(key) { return state.markdownTableText?.[key] || ''; }

function ensureToolbar() {
    if (toolbar) return;
    toolbar = document.createElement('div');
    toolbar.id = 'markdown-table-toolbar';
    toolbar.className = 'markdown-table-toolbar';
    toolbar.hidden = true;
    toolbar.setAttribute('role', 'toolbar');
    for (const group of ['row', 'column']) {
        const container = document.createElement('div');
        container.className = 'md-table-menu';
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.tableMenu = group;
        button.setAttribute('aria-haspopup', 'menu');
        button.setAttribute('aria-expanded', 'false');
        container.appendChild(button);
        const menu = document.createElement('div');
        menu.className = 'md-table-menu-items';
        menu.dataset.tableMenuItems = group;
        menu.setAttribute('role', 'menu');
        menu.hidden = true;
        for (const action of actions[group]) {
            const item = document.createElement('button');
            item.type = 'button';
            item.dataset.tableAction = action;
            item.setAttribute('role', 'menuitem');
            menu.appendChild(item);
        }
        container.appendChild(menu);
        toolbar.appendChild(container);
    }
    for (const action of ['cell-mode', 'clear-cells', 'copy-cells']) {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.tableAction = action;
        toolbar.appendChild(button);
    }
    document.getElementById('editor-host').appendChild(toolbar);
}

function closeMenus() {
    toolbar?.querySelectorAll('[data-table-menu-items]').forEach(menu => { menu.hidden = true; });
    toolbar?.querySelectorAll('[data-table-menu]').forEach(button => button.setAttribute('aria-expanded', 'false'));
}

function copyCells() {
    const table = sourceTable();
    const area = bounds();
    if (!table || !area) return;
    const text = table.rows.slice(area.top, area.bottom + 1).map(row =>
        Array.from({ length: area.right - area.left + 1 }, (_, offset) =>
            (row.cells[area.left + offset]?.text || '').trim().replace(/\\\|/g, '|')).join('\t')).join('\n');
    post({ type: 'clipboardWrite', text });
}

function canAct(action, table) {
    if (action.startsWith('select-') || action === 'cell-mode' || action === 'copy-cells') return !imeController.isComposing;
    if (state.readOnly || imeController.isComposing) return false;
    const { row, column } = selected.focus;
    if (action === 'row-delete' || action === 'row-down') return row > 0 && (action !== 'row-down' || row < table.rows.length - 1);
    if (action === 'row-up') return row > 1;
    if (action === 'column-delete') return table.columns > 1;
    if (action === 'column-left') return column > 0;
    if (action === 'column-right') return column < table.columns - 1;
    return true;
}

function focusCell(table, row, column) {
    const line = tableSourceLine(table, row);
    const field = viewport.querySelector(`.line-text[data-line="${line}"]`);
    const cell = renderedTable(table.startLine)?.rows[row]?.cells[column];
    const source = table.rows[row].cells[column];
    if (!source) return;
    const offset = source.start + Math.min(1, source.text.length - source.text.trimStart().length);
    if (field?.getAttribute('contenteditable') === 'true' && field.dataset.markdownPreviewLine === 'table') setCaret(field, offset, 0, true, false);
    else if (cell) { cell.tabIndex = -1; cell.focus({ preventScroll: true }); }
    syncMarkdownTableUi();
}

function selectCell(hit, extend = false, rectangular = false) {
    if (!selected || selected.startLine !== hit.startLine || !extend) {
        selected = { startLine: hit.startLine, anchor: { row: hit.row, column: hit.column },
            focus: { row: hit.row, column: hit.column }, rectangular };
    } else { selected.focus = { row: hit.row, column: hit.column }; selected.rectangular = true; }
    if (rectangular || extend) clearTextSelection();
    syncMarkdownTableUi();
}

function moveCell(delta, extend = false, rectangular = false) {
    const table = sourceTable();
    if (!table || imeController.isComposing) return false;
    const node = window.getSelection()?.focusNode;
    const hit = hitCell(node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement);
    const origin = !cellMode && !selected.rectangular && hit?.startLine === table.startLine ? hit : selected.focus;
    const row = Math.max(0, Math.min(table.rows.length - 1, origin.row + delta[0]));
    const column = Math.max(0, Math.min(table.columns - 1, origin.column + delta[1]));
    selectCell({ startLine: table.startLine, row, column }, extend, rectangular);
    clearTextSelection();
    focusCell(table, row, column);
    return true;
}

export function performMarkdownTableAction(action) {
    let table = sourceTable();
    if (!table || !canAct(action, table)) return false;
    if (action === 'copy-cells') { copyCells(); return true; }
    if (action === 'cell-mode') { cellMode = !cellMode; selected.rectangular = cellMode; clearTextSelection(); syncMarkdownTableUi(); return true; }
    if (action === 'select-row' || action === 'select-column') {
        selected.anchor = action === 'select-row' ? { row: selected.focus.row, column: 0 } : { row: 0, column: selected.focus.column };
        selected.focus = action === 'select-row' ? { row: selected.focus.row, column: table.columns - 1 } : { row: table.rows.length - 1, column: selected.focus.column };
        selected.rectangular = true;
        clearTextSelection();
        syncMarkdownTableUi();
        return true;
    }
    const active = document.activeElement?.closest?.('.line-text');
    if (active?.dataset.markdownPreviewLine === 'table') commitLine(active);
    table = sourceTable();
    const change = table && changeMarkdownTable(table, action, selected.focus.row, selected.focus.column, bounds());
    if (!change) return false;
    const lines = change.text.split('\n');
    const relativeLine = tableSourceLine(change.table, change.row) - table.startLine;
    const targetCell = change.table.rows[change.row].cells[change.column];
    const offset = lines.slice(0, relativeLine).reduce((length, line) => length + line.length + 1, 0) +
        targetCell.start + Math.min(1, targetCell.text.length - targetCell.text.trimStart().length);
    clearTextSelection();
    selected.anchor = selected.focus = { row: change.row, column: change.column };
    selected.rectangular = cellMode;
    closeMenus();
    replaceSelectionWith({ start: { line: table.startLine, column: 0 },
        end: { line: table.endLine, column: (state.cache.get(table.endLine) || '').length } }, change.text,
        { startOffset: offset, endOffset: offset, preserveScroll: true });
    return true;
}

export function syncMarkdownTableUi() {
    if (!bound) return;
    if (documentId !== state.hostDocumentId) { resetMarkdownTableUi(); documentId = state.hostDocumentId; }
    if (!enabled() || !selected) { if (toolbar) toolbar.hidden = true; return; }
    const table = sourceTable();
    if (!table) { resetMarkdownTableUi(); return; }
    const rendered = renderedTable(table.startLine);
    if (!rendered) { if (toolbar) toolbar.hidden = true; return; }
    currentRendered = rendered;
    for (const point of [selected.anchor, selected.focus]) {
        point.row = Math.max(0, Math.min(point.row, table.rows.length - 1));
        point.column = Math.max(0, Math.min(point.column, table.columns - 1));
    }
    viewport.querySelectorAll('.md-table-cell-selected, .md-table-cell-active').forEach(cell => {
        cell.classList.remove('md-table-cell-selected', 'md-table-cell-active');
        cell.removeAttribute('aria-selected');
    });
    const area = bounds();
    rendered.setAttribute('role', 'grid');
    [...rendered.rows].forEach((row, r) => [...row.cells].forEach((cell, c) => {
        const active = r === selected.focus.row && c === selected.focus.column;
        const inside = r >= area.top && r <= area.bottom && c >= area.left && c <= area.right;
        cell.classList.toggle('md-table-cell-active', active);
        cell.classList.toggle('md-table-cell-selected', inside && (selected.rectangular || cellMode));
        cell.setAttribute('aria-selected', String(inside));
    }));
    ensureToolbar();
    toolbar.hidden = false;
    for (const button of toolbar.querySelectorAll('button')) {
        const key = button.dataset.tableAction || button.dataset.tableMenu;
        button.textContent = label(key);
        button.title = label(key);
        button.disabled = button.dataset.tableAction ? !canAct(key, table) : imeController.isComposing;
        if (key === 'cell-mode') button.setAttribute('aria-pressed', String(cellMode));
    }
    positionToolbar();
}

function positionToolbar() {
    if (!toolbar || toolbar.hidden) return;
    if (!currentRendered?.isConnected) { toolbar.hidden = true; return; }
    const tableRect = currentRendered.getBoundingClientRect();
    const container = scrollContainer.getBoundingClientRect();
    const host = toolbar.parentElement.getBoundingClientRect();
    const height = toolbar.offsetHeight || 30;
    const top = tableRect.top - container.top >= height ? tableRect.top - height
        : Math.min(tableRect.bottom + 4, container.bottom - height);
    toolbar.style.left = `${Math.max(0, Math.min(tableRect.left - host.left, host.width - toolbar.offsetWidth))}px`;
    toolbar.style.top = `${Math.max(container.top, top) - host.top}px`;
    toolbar.classList.toggle('open-up', container.bottom - top - height < 210);
}

export function bindMarkdownTableEvents() {
    if (bound) return;
    bound = true;
    documentId = state.hostDocumentId;
    document.addEventListener('pointerdown', event => {
        if (toolbar?.contains(event.target)) {
            event.preventDefault();
            event.stopPropagation();
            return;
        }
        const hit = hitCell(event.target);
        if (!hit) { if (scrollContainer.contains(event.target)) resetMarkdownTableUi(); closeMenus(); return; }
        if (event.button !== 0 || imeController.isComposing) return;
        closeMenus();
        selectCell(hit, event.shiftKey, cellMode);
        if (state.readOnly || cellMode || event.shiftKey) {
            event.preventDefault(); event.stopPropagation();
            dragPointer = event.pointerId;
            scrollContainer.setPointerCapture?.(event.pointerId);
            focusCell(hit.source, hit.row, hit.column);
        }
    }, true);
    document.addEventListener('click', event => {
        const action = event.target.closest?.('[data-table-action]');
        const menu = event.target.closest?.('[data-table-menu]');
        if (!toolbar?.contains(event.target)) return;
        event.preventDefault(); event.stopPropagation();
        if (action) performMarkdownTableAction(action.dataset.tableAction);
        else if (menu) {
            const panel = toolbar.querySelector(`[data-table-menu-items="${menu.dataset.tableMenu}"]`);
            const open = panel.hidden;
            closeMenus(); panel.hidden = !open; menu.setAttribute('aria-expanded', String(open));
        }
    }, true);
    document.addEventListener('pointermove', event => {
        if (dragPointer === null || event.pointerId !== dragPointer || imeController.isComposing) return;
        const hit = hitCell(document.elementFromPoint(event.clientX, event.clientY));
        if (hit && hit.startLine === selected?.startLine) { selectCell(hit, true, true); event.preventDefault(); event.stopPropagation(); }
    }, true);
    window.addEventListener('pointerup', event => {
        if (dragPointer !== event.pointerId) return;
        dragPointer = null;
        if (scrollContainer.hasPointerCapture?.(event.pointerId)) scrollContainer.releasePointerCapture(event.pointerId);
        event.stopImmediatePropagation();
    }, true);
    window.addEventListener('pointercancel', () => { dragPointer = null; });
    document.addEventListener('keydown', event => {
        if (!enabled() || !selected || imeController.isComposing || event.isComposing || event.keyCode === 229) return;
        if (!scrollContainer.contains(document.activeElement) && !toolbar?.contains(document.activeElement)) return;
        const table = sourceTable();
        if (!table) return;
        const consume = () => { event.preventDefault(); event.stopImmediatePropagation(); };
        const control = event.ctrlKey || event.metaKey;
        if (control && event.key.toLowerCase() === 'c' && (selected.rectangular || cellMode)) { consume(); copyCells(); return; }
        if (['Delete', 'Backspace'].includes(event.key) && (selected.rectangular || cellMode)) { consume(); performMarkdownTableAction('clear-cells'); return; }
        if (event.key === 'Escape') { consume(); resetMarkdownTableUi(); return; }
        const move = { ArrowLeft: [0, -1], ArrowRight: [0, 1], ArrowUp: [-1, 0], ArrowDown: [1, 0] }[event.key];
        const verticalEdit = move?.[0] && !control && !event.altKey && scrollContainer.contains(document.activeElement);
        if (move && (cellMode || event.shiftKey && selected.rectangular || verticalEdit)) {
            consume();
            moveCell(move, event.shiftKey, cellMode || event.shiftKey);
            return;
        }
        if (event.key === 'Tab' && !control && !event.altKey) {
            consume();
            const position = selected.focus.row * table.columns + selected.focus.column + (event.shiftKey ? -1 : 1);
            if (position >= table.rows.length * table.columns) { selected.focus.column = 0; performMarkdownTableAction('row-after'); return; }
            const target = Math.max(0, position);
            const row = Math.floor(target / table.columns), column = target % table.columns;
            selectCell({ startLine: table.startLine, row, column });
            clearTextSelection(); focusCell(table, row, column);
        }
    }, true);
    viewport.addEventListener('focusin', event => {
        if (imeController.isComposing) return;
        const hit = hitCell(event.target);
        if (hit && !(selected?.startLine === hit.startLine && selected.focus.row === hit.row && selected.focus.column === hit.column)) selectCell(hit);
    });
    viewport.addEventListener('compositionstart', () => { closeMenus(); queueMicrotask(syncMarkdownTableUi); });
    viewport.addEventListener('compositionend', event => {
        const navigation = imeController.pendingVerticalNavigation;
        const field = event.target.closest?.('.line-text');
        const moveAfterCommit = navigation && field?.dataset.markdownPreviewLine === 'table' &&
            !imeController.rangeComposition && !imeController.columnComposition;
        if (moveAfterCommit) imeController.pendingVerticalNavigation = null;
        // The input listener commits the original IME-owned cell synchronously.
        // Only then may navigation change the selection and editing host.
        queueMicrotask(() => {
            if (moveAfterCommit) moveCell([navigation.direction, 0], navigation.extendSelection, navigation.extendSelection);
            syncMarkdownTableUi();
        });
    });
    // Scrolling only repositions the overlay; do not rescan/repaint every cell
    // or rewrite toolbar labels while a large table is moving.
    scrollContainer.addEventListener('scroll', positionToolbar, { passive: true });
    window.addEventListener('resize', positionToolbar);
}
