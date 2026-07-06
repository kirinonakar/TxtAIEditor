import { state } from './editor-core.js';
import { viewport } from './editor-dom.js';

function lineTextFromElement(element) {
    return (element.textContent || '').replace(/\u00a0/g, ' ');
}

function normalizeSelection(selection = state.selection) {
    if (!selection) return null;
    const a = selection.start;
    const b = selection.end;
    if (a.line < b.line || (a.line === b.line && a.column <= b.column)) {
        return { start: a, end: b, isColumn: !!selection.isColumn };
    }
    return { start: b, end: a, isColumn: !!selection.isColumn };
}

function hasCustomSelection() {
    const normalized = normalizeSelection();
    return !!normalized &&
        (normalized.start.line !== normalized.end.line ||
            normalized.start.column !== normalized.end.column);
}

function activeColumnSelection() {
    const selection = normalizeSelection();
    return selection && selection.isColumn && hasCustomSelection() ? selection : null;
}

function cloneEditorSelection(selection) {
    if (!selection) return null;
    return {
        start: { line: selection.start.line, column: selection.start.column },
        end: { line: selection.end.line, column: selection.end.column },
        isColumn: !!selection.isColumn
    };
}

function isPositionInsideSelection(position) {
    const selection = normalizeSelection();
    if (!selection || !position) return false;
    if (position.line < selection.start.line || position.line > selection.end.line) return false;
    if (position.line === selection.start.line && position.column < selection.start.column) return false;
    if (position.line === selection.end.line && position.column > selection.end.column) return false;
    return true;
}

function selectionBoundsForLine(lineNumber, textLength) {
    const selection = normalizeSelection();
    if (!selection || lineNumber < selection.start.line || lineNumber > selection.end.line) {
        return null;
    }

    if (selection.isColumn) {
        const start = Math.min(selection.start.column, selection.end.column);
        const end = Math.max(selection.start.column, selection.end.column);
        return {
            start: Math.max(0, Math.min(start, textLength)),
            end: Math.max(0, Math.min(end, textLength))
        };
    }

    const rawStart = lineNumber === selection.start.line ? selection.start.column : 0;
    const rawEnd = lineNumber === selection.end.line ? selection.end.column : textLength;
    const start = Math.max(0, Math.min(rawStart, textLength));
    const end = Math.max(0, Math.min(rawEnd, textLength));

    const spansMultipleLines = selection.start.line !== selection.end.line;
    const isEndBoundaryAtLineStart = spansMultipleLines &&
        lineNumber === selection.end.line &&
        selection.end.column <= 0;
    if (start === end && isEndBoundaryAtLineStart && textLength > 0) {
        return null;
    }

    return { start, end };
}

function drawEditableSelectionOverlays() {
    viewport.querySelectorAll('.editable-selection-overlay').forEach(el => el.remove());

    const selection = normalizeSelection();
    if (!selection || !hasCustomSelection()) {
        drawImeBypassCaretOverlay();
        return;
    }

    for (const element of viewport.querySelectorAll('.line-text')) {
        const lineNumber = Number(element.dataset.line || 0);
        if (!lineNumber) continue;

        const text = lineTextFromElement(element);
        const bounds = selectionBoundsForLine(lineNumber, text.length);
        if (!bounds) continue;

        const start = Math.max(0, Math.min(bounds.start, text.length));
        const end = Math.max(0, Math.min(bounds.end, text.length));

        if (start === end && selection.isColumn) {
            drawEditableColumnCursorOverlay(element, start);
            continue;
        }

        if (start === end && text.length === 0) {
            drawEditableEmptyLineSelectionOverlay(element);
            continue;
        }

        drawEditableSelectionRangeOverlay(element, start, end);
    }

    drawImeBypassCaretOverlay();
}

function drawEditableSelectionRangeOverlay(element, start, end) {
    const row = element.closest('.line-row');
    if (!row) return;

    const _ = row.offsetHeight;

    const length = lineTextFromElement(element).length;
    const safeStart = Math.max(0, Math.min(start, length));
    const safeEnd = Math.max(safeStart, Math.min(end, length));
    if (safeStart === safeEnd) return;

    const positions = textPositionsForOffsets(element, safeStart, safeEnd);
    if (!positions) return;

    const range = document.createRange();
    range.setStart(positions.start.node, positions.start.offset);
    range.setEnd(positions.end.node, positions.end.offset);

    const rowRect = row.getBoundingClientRect();
    const startBoundary = caretBoundaryRect(element, safeStart, false);
    const endBoundary = caretBoundaryRect(element, safeEnd, true);
    const sameVisualRow = (a, b) => a && b && Math.abs(a.top - b.top) < 2;
    const rects = [...range.getClientRects()].filter(rect => rect.width > 0 && rect.height > 0);

    for (const rect of rects) {
        let left = rect.left;
        let right = rect.right;

        if (sameVisualRow(rect, startBoundary)) {
            left = Math.max(left, startBoundary.left);
        }
        if (sameVisualRow(rect, endBoundary)) {
            right = Math.min(right, endBoundary.left);
        }

        if (right > left) {
            appendEditableSelectionOverlay(row, left - rowRect.left, rect.top - rowRect.top, right - left, rect.height);
        }
    }

    range.detach?.();
}

function textPositionsForOffsets(element, start, end) {
    const startPosition = textPositionForOffset(element, start);
    const endPosition = textPositionForOffset(element, end);
    if (!startPosition || !endPosition) return null;
    return { start: startPosition, end: endPosition };
}

function textPositionForOffset(element, offset) {
    if (!element) return null;

    let remaining = Math.max(0, Number(offset || 0));
    let lastText = null;

    function walk(node) {
        if (node.nodeType === Node.TEXT_NODE) {
            lastText = node;
            const length = node.textContent.length;
            if (remaining <= length) {
                return { node, offset: remaining };
            }
            remaining -= length;
            return null;
        }

        for (const child of node.childNodes) {
            const found = walk(child);
            if (found) return found;
        }
        return null;
    }

    return walk(element) || (lastText
        ? { node: lastText, offset: lastText.textContent.length }
        : null);
}

function caretBoundaryRect(element, offset, preferPrevious = false) {
    const length = lineTextFromElement(element).length;
    if (!element || length === 0) return null;

    const safeOffset = Math.max(0, Math.min(Number(offset || 0), length));
    const range = document.createRange();

    try {
        if (safeOffset < length && !preferPrevious) {
            const positions = textPositionsForOffsets(element, safeOffset, safeOffset + 1);
            if (!positions) return null;
            range.setStart(positions.start.node, positions.start.offset);
            range.setEnd(positions.end.node, positions.end.offset);
            const rect = firstUsableRect(range);
            if (rect) {
                return { left: rect.left, top: rect.top, bottom: rect.bottom, height: rect.height };
            }
        }

        if (safeOffset > 0) {
            const positions = textPositionsForOffsets(element, safeOffset - 1, safeOffset);
            if (!positions) return null;
            range.setStart(positions.start.node, positions.start.offset);
            range.setEnd(positions.end.node, positions.end.offset);
            const rect = lastUsableRect(range);
            if (rect) {
                return { left: rect.right, top: rect.top, bottom: rect.bottom, height: rect.height };
            }
        }

        if (safeOffset < length) {
            const positions = textPositionsForOffsets(element, safeOffset, safeOffset + 1);
            if (!positions) return null;
            range.setStart(positions.start.node, positions.start.offset);
            range.setEnd(positions.end.node, positions.end.offset);
            const rect = firstUsableRect(range);
            if (rect) {
                return { left: rect.left, top: rect.top, bottom: rect.bottom, height: rect.height };
            }
        }
    } finally {
        range.detach?.();
    }

    return null;
}

function firstUsableRect(range) {
    return [...range.getClientRects()].find(rect => rect.width > 0 && rect.height > 0) || null;
}

function lastUsableRect(range) {
    const rects = [...range.getClientRects()].filter(rect => rect.width > 0 && rect.height > 0);
    return rects.length ? rects[rects.length - 1] : null;
}

function drawEditableColumnCursorOverlay(element, column) {
    const selection = normalizeSelection();
    if (!selection?.isColumn) return;

    drawCaretOverlay(element, column, 2, 'column-cursor-overlay');
}

function drawImeBypassCaretOverlay() {
    if (!state.textareaImeBypassActive || !state.bypassCursorLine) return;

    const line = Number(state.bypassCursorLine || 0);
    const element = viewport.querySelector(`.line-text[data-line="${line}"]`);
    if (!element) return;

    drawCaretOverlay(element, state.bypassCursorColumn ?? 0, 1, 'ime-bypass-caret-overlay');
}

function drawCaretOverlay(element, column, width, extraClass) {
    const row = element.closest('.line-row');
    if (!row) return;

    const rowRect = row.getBoundingClientRect();
    const lineRect = element.getBoundingClientRect();
    const height = Math.max(1, Math.min(lineRect.height, state.lineHeight));

    const textLength = lineTextFromElement(element).length;
    if (textLength === 0) {
        appendEditableSelectionOverlay(row, lineRect.left - rowRect.left, lineRect.top - rowRect.top, width, height, extraClass);
        return;
    }

    const safeColumn = Math.max(0, Math.min(column, textLength));
    const rect = caretBoundaryRect(element, safeColumn, safeColumn > 0);
    if (rect && rect.height > 0) {
        appendEditableSelectionOverlay(row, rect.left - rowRect.left, rect.top - rowRect.top, width, rect.height, extraClass);
    }
}

function drawEditableEmptyLineSelectionOverlay(element) {
    const row = element.closest('.line-row');
    if (!row) return;

    const rowRect = row.getBoundingClientRect();
    const lineRect = element.getBoundingClientRect();
    const height = Math.max(1, Math.min(lineRect.height, state.lineHeight));
    appendEditableSelectionOverlay(
        row,
        lineRect.left - rowRect.left,
        lineRect.top - rowRect.top,
        4,
        height,
        'selected-empty-line-overlay');
}

function appendEditableSelectionOverlay(row, left, top, width, height, extraClass = '') {
    const overlay = document.createElement('div');
    overlay.className = `editable-selection-overlay${extraClass ? ' ' + extraClass : ''}`;
    overlay.style.left = `${Math.max(0, left)}px`;
    overlay.style.top = `${Math.max(0, top)}px`;
    overlay.style.width = `${Math.max(1, width)}px`;
    overlay.style.height = `${Math.max(1, height)}px`;
    row.appendChild(overlay);
}

export {
    activeColumnSelection,
    cloneEditorSelection,
    drawEditableSelectionOverlays,
    hasCustomSelection,
    isPositionInsideSelection,
    normalizeSelection,
    selectionBoundsForLine
};
