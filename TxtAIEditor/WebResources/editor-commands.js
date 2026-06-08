import {
    activeEditableElement,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    comparePositions,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasTextAt,
    isStandaloneDelimiter,
    invalidateMeasuredLineHeightsAround,
    lineCommentSyntax,
    lineTop,
    markDirty,
    measureRenderedRows,
    orderedRange,
    post,
    queueRender,
    readClipboardText,
    reportCursorAndSelection,
    requestLines,
    selectedLineRange,
    selectedText,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    writeClipboardText
} from './editor-core.js';
import { scrollContainer, viewport } from './editor-dom.js';
import { renderLineContent } from './editor-highlighter.js';
import {
    activeColumnSelection,
    cloneEditorSelection,
    drawEditableSelectionOverlays,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import { createMarkdownCommandHandlers } from './editor-markdown-commands.js';

function lineTextFromElement(element) {
    return (element.textContent || '').replace(/\u00a0/g, ' ');
}

function makeEditablePlainText(element, caretColumn = null, restoreCaret = true) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return null;
    const text = lineTextFromElement(element);
    const column = caretColumn === null
        ? getCaretOffset(element)
        : Math.max(0, Math.min(Number(caretColumn || 0), text.length));
    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    state.editingLine = lineNumber;

    const needsFlatten = element.childNodes.length !== 1 ||
        element.firstChild?.nodeType !== Node.TEXT_NODE ||
        element.textContent !== text;
    if (needsFlatten) {
        element.textContent = text;
    }

    if (restoreCaret && (needsFlatten || caretColumn !== null)) {
        setCaret(element, column);
    }
    return { text, column };
}

function getCaretOffset(element) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return 0;
    const range = selection.getRangeAt(0);
    if (!element.contains(range.startContainer)) return 0;
    return offsetFromNodeInElement(element, range.startContainer, range.startOffset);
}

function offsetFromNodeInElement(element, node, offset) {
    if (!element || !node || !element.contains(node)) return 0;
    const before = document.createRange();
    before.selectNodeContents(element);
    try {
        before.setEnd(node, offset);
        return before.toString().length;
    } catch {
        return 0;
    }
}

function inputRangeInElement(event, element) {
    if (!element || typeof event.getTargetRanges !== 'function') return null;
    const ranges = event.getTargetRanges();
    if (!ranges || ranges.length === 0) return null;
    const range = ranges[0];
    if (!element.contains(range.startContainer) || !element.contains(range.endContainer)) {
        return null;
    }
    const start = offsetFromNodeInElement(element, range.startContainer, range.startOffset);
    const end = offsetFromNodeInElement(element, range.endContainer, range.endOffset);
    return { start: Math.min(start, end), end: Math.max(start, end) };
}

function lineElementFromDomNode(node) {
    if (!node) return null;
    if (node.nodeType === Node.ELEMENT_NODE && node.closest) {
        return node.closest('.line-text');
    }
    return node.parentElement?.closest?.('.line-text') || null;
}

function editorPositionFromDomPosition(node, offset) {
    const element = lineElementFromDomNode(node);
    if (!element || element.getAttribute('contenteditable') !== 'true') return null;

    const line = Number(element.dataset.line || 0);
    if (!line) return null;

    const text = lineTextFromElement(element);
    const column = Math.max(0, Math.min(offsetFromNodeInElement(element, node, offset), text.length));
    return { line, column };
}

function nativeEditorSelectionRange() {
    const domSelection = window.getSelection();
    if (!domSelection || domSelection.rangeCount === 0 || domSelection.isCollapsed) return null;

    const range = domSelection.getRangeAt(0);
    const start = editorPositionFromDomPosition(range.startContainer, range.startOffset);
    const end = editorPositionFromDomPosition(range.endContainer, range.endOffset);
    if (!start || !end) return null;

    const ordered = orderedRange({ start, end });
    if (ordered.start.line === ordered.end.line && ordered.start.column === ordered.end.column) {
        return null;
    }
    return ordered;
}

function compositionSelectionRange(includeNativeSelection = true) {
    const customSelection = normalizeSelection();
    if (customSelection && !customSelection.isColumn &&
        (customSelection.start.line !== customSelection.end.line ||
            customSelection.start.column !== customSelection.end.column)) {
        return customSelection;
    }

    if (!includeNativeSelection || state.isComposing) {
        return null;
    }

    return nativeEditorSelectionRange();
}

function getCaretOffsetFromPoint(element, clientX, clientY) {
    let range = null;
    if (document.caretRangeFromPoint) {
        range = document.caretRangeFromPoint(clientX, clientY);
    } else if (document.caretPositionFromPoint) {
        const position = document.caretPositionFromPoint(clientX, clientY);
        if (position) {
            range = document.createRange();
            range.setStart(position.offsetNode, position.offset);
        }
    }

    if (range && element.contains(range.startContainer)) {
        const before = range.cloneRange();
        before.selectNodeContents(element);
        before.setEnd(range.startContainer, range.startOffset);
        return before.toString().length;
    }

    const rect = element.getBoundingClientRect();
    const textLength = lineTextFromElement(element).length;
    if (clientX <= rect.left) return 0;
    if (clientX >= rect.right) return textLength;
    return Math.round(((clientX - rect.left) / Math.max(1, rect.width)) * textLength);
}

function positionFromPointer(event) {
    let element = lineElementFromEvent(event);
    if (!element) {
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const row = hit?.closest?.('.line-row');
        if (row) {
            element = row.querySelector('.line-text');
        }
        if (!element) {
            const rows = viewport.querySelectorAll('.line-row');
            if (rows.length === 0) return null;
            const y = event.clientY + scrollContainer.scrollTop - (viewport.getBoundingClientRect().top + scrollContainer.scrollTop - scrollContainer.scrollTop);
            let bestRow = rows[0];
            let bestDist = Infinity;
            for (const r of rows) {
                const rect = r.getBoundingClientRect();
                const mid = rect.top + rect.height / 2;
                const dist = Math.abs(event.clientY - mid);
                if (dist < bestDist) {
                    bestDist = dist;
                    bestRow = r;
                }
            }
            element = bestRow.querySelector('.line-text');
        }
    }
    if (!element) return null;
    const line = Number(element.dataset.line || 1);
    const textLength = (state.cache.get(line) ?? lineTextFromElement(element)).length;
    const column = Math.max(0, Math.min(getCaretOffsetFromPoint(element, event.clientX, event.clientY), textLength));
    return { line, column, element };
}

function isWordCharacter(char) {
    return /[\p{L}\p{N}_-]/u.test(char || '');
}

function wordRangeAtColumn(text, column) {
    const value = String(text ?? '');
    if (!value) return null;

    let index = Math.max(0, Math.min(column, value.length - 1));
    if (!isWordCharacter(value[index]) && index > 0 && isWordCharacter(value[index - 1])) {
        index--;
    }
    if (!isWordCharacter(value[index])) return null;

    let start = index;
    let end = index + 1;
    while (start > 0 && isWordCharacter(value[start - 1])) start--;
    while (end < value.length && isWordCharacter(value[end])) end++;
    return start < end ? { start, end } : null;
}

function selectWordAtPointer(event) {
    const position = positionFromPointer(event);
    if (!position) return false;

    const text = state.cache.get(position.line) ?? lineTextFromElement(position.element);
    const wordRange = wordRangeAtColumn(text, position.column);
    if (!wordRange) return false;

    state.selectionAnchor = { line: position.line, column: wordRange.start };
    state.selection = {
        start: { line: position.line, column: wordRange.start },
        end: { line: position.line, column: wordRange.end }
    };
    state.currentLine = position.line;
    state.currentColumn = wordRange.end + 1;
    queueRender(true);
    setTimeout(() => focusLine(position.line, wordRange.end), 0);
    reportCursorAndSelection(position.element);
    return true;
}

function setCaret(element, offset) {
    const oldActiveElement = document.activeElement?.closest?.('.line-text');
    const oldActiveLine = oldActiveElement ? Number(oldActiveElement.dataset.line || 0) : null;

    state.editingLine = Number(element.dataset.line || state.currentLine || 1);
    if (state.inlineLivePreviewEnabled && state.inlineLivePreviewSourceLine) {
        state.inlineLivePreviewSourceLine = state.editingLine;
    }
    element.focus({ preventScroll: true });
    const selection = window.getSelection();
    const range = document.createRange();
    let remaining = Math.max(0, offset);

    function walk(node) {
        if (node.nodeType === Node.TEXT_NODE) {
            if (remaining <= node.textContent.length) {
                range.setStart(node, remaining);
                range.collapse(true);
                return true;
            }
            remaining -= node.textContent.length;
            return false;
        }

        for (const child of node.childNodes) {
            if (walk(child)) return true;
        }
        return false;
    }

    if (!walk(element)) {
        range.selectNodeContents(element);
        range.collapse(false);
    }

    selection.removeAllRanges();
    selection.addRange(range);
    revealCaretHorizontally(element, offset);
    reportCursorAndSelection(element);

    if (oldActiveLine !== null && oldActiveLine !== state.editingLine) {
        queueRender(true);
    } else {
        drawEditableSelectionOverlays();
    }
}

function revealCaretHorizontally(element, offset) {
    if (!element || state.wordWrap) return;

    const caretRect = caretRectForOffset(element, offset);
    if (!caretRect) return;

    const containerRect = scrollContainer.getBoundingClientRect();
    const margin = 24;
    const leftLimit = containerRect.left + margin;
    const rightLimit = containerRect.right - margin;

    if (caretRect.right > rightLimit) {
        scrollContainer.scrollLeft += caretRect.right - rightLimit;
    } else if (caretRect.left < leftLimit) {
        scrollContainer.scrollLeft = Math.max(0, scrollContainer.scrollLeft - (leftLimit - caretRect.left));
    }
}

function textPositionForOffset(element, offset) {
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

    return walk(element) || {
        node: lastText || element,
        offset: lastText ? lastText.textContent.length : element.childNodes.length
    };
}

function caretRectForOffset(element, offset) {
    if (!element) return null;
    const textLength = lineTextFromElement(element).length;
    if (textLength === 0) {
        const rect = element.getBoundingClientRect();
        const styles = window.getComputedStyle(element);
        const paddingLeft = Number.parseFloat(styles.paddingLeft) || 0;
        const parsedLineHeight = Number.parseFloat(styles.lineHeight);
        const height = Math.max(1, Number.isFinite(parsedLineHeight) ? parsedLineHeight : state.lineHeight);
        const top = rect.top + Math.max(0, (rect.height - height) / 2);
        const left = rect.left + paddingLeft;
        return { left, right: left, top, bottom: top + height, height };
    }

    const position = textPositionForOffset(element, offset);
    if (!position?.node) return null;

    const range = document.createRange();
    try {
        range.setStart(position.node, position.offset);
        range.collapse(true);
        let rect = range.getBoundingClientRect();
        if (rect && (rect.width > 0 || rect.height > 0)) return rect;

        if (offset > 0) {
            const before = textPositionForOffset(element, offset - 1);
            range.setStart(before.node, before.offset);
            range.setEnd(position.node, position.offset);
            rect = range.getBoundingClientRect();
            if (rect && (rect.width > 0 || rect.height > 0)) {
                return { left: rect.right, right: rect.right, top: rect.top, bottom: rect.bottom, height: rect.height || state.lineHeight };
            }
        }
        if (offset < textLength) {
            const after = textPositionForOffset(element, offset + 1);
            range.setStart(position.node, position.offset);
            range.setEnd(after.node, after.offset);
            rect = range.getBoundingClientRect();
            if (rect && (rect.width > 0 || rect.height > 0)) {
                return { left: rect.left, right: rect.left, top: rect.top, bottom: rect.bottom, height: rect.height || state.lineHeight };
            }
        }
    } catch {
        return null;
    } finally {
        range.detach?.();
    }

    return null;
}

function nativeOffsetFromPointInElement(element, clientX, clientY) {
    let range = null;
    if (document.caretRangeFromPoint) {
        range = document.caretRangeFromPoint(clientX, clientY);
    } else if (document.caretPositionFromPoint) {
        const position = document.caretPositionFromPoint(clientX, clientY);
        if (position) {
            range = document.createRange();
            range.setStart(position.offsetNode, position.offset);
        }
    }

    if (!range || !element.contains(range.startContainer)) {
        return null;
    }

    const before = range.cloneRange();
    before.selectNodeContents(element);
    before.setEnd(range.startContainer, range.startOffset);
    return before.toString().length;
}

function isRectOnAdjacentVisualLine(referenceRect, candidateRect, direction, lineStep) {
    if (!referenceRect || !candidateRect || !direction) return true;

    const referenceCenter = referenceRect.top + (referenceRect.height || lineStep) / 2;
    const candidateCenter = candidateRect.top + (candidateRect.height || lineStep) / 2;
    const tolerance = Math.max(3, lineStep * 0.4);
    return direction < 0
        ? candidateCenter < referenceCenter - tolerance
        : candidateCenter > referenceCenter + tolerance;
}

function offsetFromPointInElement(element, clientX, clientY, referenceRect = null, direction = 0, lineStep = state.lineHeight) {
    const nativeOffset = nativeOffsetFromPointInElement(element, clientX, clientY);
    if (nativeOffset !== null) {
        const nativeRect = caretRectForOffset(element, nativeOffset);
        if (isRectOnAdjacentVisualLine(referenceRect, nativeRect, direction, lineStep)) {
            return nativeOffset;
        }
    }

    const textLength = lineTextFromElement(element).length;
    let bestOffset = null;
    let bestDistance = Infinity;
    for (let offset = 0; offset <= textLength; offset++) {
        const rect = caretRectForOffset(element, offset);
        if (!rect) continue;
        if (!isRectOnAdjacentVisualLine(referenceRect, rect, direction, lineStep)) continue;
        const x = rect.left;
        const y = rect.top + (rect.height || state.lineHeight) / 2;
        const dx = x - clientX;
        const dy = y - clientY;
        const distance = (dy * dy * 8) + (dx * dx);
        if (distance < bestDistance) {
            bestDistance = distance;
            bestOffset = offset;
        }
    }
    return bestOffset;
}

function moveCaretVertical(element, direction, extendSelection = false) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return false;

    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    const text = lineTextFromElement(element);
    const caret = Math.max(0, Math.min(getCaretOffset(element), text.length));

    const anchor = extendSelection
        ? (state.selectionAnchor || { line: lineNumber, column: caret })
        : null;

    let target = null;

    const caretRect = caretRectForOffset(element, caret);
    if (!caretRect) {
        if (direction < 0 && lineNumber > 1) {
            const prevText = state.cache.get(lineNumber - 1) || '';
            target = { line: lineNumber - 1, column: Math.min(caret, prevText.length) };
        } else if (direction > 0 && lineNumber < state.lineCount) {
            const nextText = state.cache.get(lineNumber + 1) || '';
            target = { line: lineNumber + 1, column: Math.min(caret, nextText.length) };
        }
    } else {
        const elementRect = element.getBoundingClientRect();
        const styles = window.getComputedStyle(element);
        const parsedLineHeight = Number.parseFloat(styles.lineHeight);
        const lineStep = Math.max(1, Number.isFinite(parsedLineHeight) ? parsedLineHeight : (caretRect.height || state.lineHeight));
        const targetX = Math.max(elementRect.left + 1, Math.min(caretRect.left, elementRect.right - 1));
        const targetY = direction < 0
            ? caretRect.top - lineStep / 2
            : caretRect.bottom + lineStep / 2;

        if (targetY >= elementRect.top && targetY <= elementRect.bottom) {
            const targetColumn = offsetFromPointInElement(element, targetX, targetY, caretRect, direction, lineStep);
            if (targetColumn !== null) {
                target = { line: lineNumber, column: targetColumn };
            }
        }

        if (!target) {
            if (direction < 0 && lineNumber > 1) {
                const prevText = state.cache.get(lineNumber - 1) || '';
                target = { line: lineNumber - 1, column: Math.min(caret, prevText.length) };
            } else if (direction > 0 && lineNumber < state.lineCount) {
                const nextText = state.cache.get(lineNumber + 1) || '';
                target = { line: lineNumber + 1, column: Math.min(caret, nextText.length) };
            }
        }
    }

    if (target) {
        if (extendSelection) {
            state.selectionAnchor = anchor;
            state.selection = (anchor.line === target.line && anchor.column === target.column)
                ? null
                : { start: anchor, end: target };
            state.currentLine = target.line;
            state.currentColumn = target.column + 1;
            syncCustomSelectionClass();
            queueRender(true);
            setTimeout(() => focusLine(target.line, target.column), 0);
        } else {
            state.selection = null;
            state.selectionAnchor = { line: target.line, column: target.column };
            state.currentLine = target.line;
            state.currentColumn = target.column + 1;
            syncCustomSelectionClass();
            if (target.line === lineNumber) {
                setCaret(element, target.column);
            } else {
                focusLine(target.line, target.column);
            }
        }
        return true;
    }

    return false;
}

function commitLine(element) {
    const lineNumber = Number(element.dataset.line || 1);
    const isComposing = state.isComposing &&
        (!state.compositionLine || state.compositionLine === lineNumber);

    const text = lineTextFromElement(element);

    const isInlineLivePreviewActiveLine = state.inlineLivePreviewEnabled &&
        state.inlineLivePreviewSourceLine === lineNumber;

    if (isComposing && state.columnComposition) {
        updateColumnCompositionPreview(element);
    } else {
        state.cache.set(lineNumber, text);
        if (!isInlineLivePreviewActiveLine) {
            state.cacheVersion++;
        }
    }

    state.currentLine = lineNumber;
    state.currentColumn = getCaretOffset(element) + 1;

    if (isComposing) {
        if (!state.columnComposition) {
            post({ type: 'lineChanged', lineNumber, text, isComposing: true });
            post({ type: 'contentChanged', isComposing: true });
        }
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return;
    }

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    post({ type: 'lineChanged', lineNumber, text });
    post({ type: 'contentChanged' });
    reportCursorAndSelection(element);

    if (state.wordWrap) {
        measureRenderedRows(false);
    }
}

function commitLineForSave(element) {
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        return false;
    }

    const lineNumber = Number(element.dataset.line || state.compositionLine || state.currentLine || 1);

    if (state.columnComposition && finishColumnComposition(element, lineNumber)) {
        state.isComposing = false;
        state.compositionLine = null;
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

    const text = lineTextFromElement(element);
    state.isComposing = false;
    state.compositionLine = null;
    state.cache.set(lineNumber, text);
    state.cacheVersion++;
    state.currentLine = lineNumber;
    state.currentColumn = Math.min(getCaretOffset(element) + 1, text.length + 1);

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    post({ type: 'lineChanged', lineNumber, text });
    post({ type: 'contentChanged' });
    reportCursorAndSelection(element);

    if (state.wordWrap) {
        measureRenderedRows(false);
    }

    return true;
}

function flushPendingEditForSave(requestId) {
    const requestedLine = Number(state.compositionLine || state.editingLine || state.currentLine || 1);
    const focusedElement = document.activeElement?.closest?.('.line-text');
    let element = (focusedElement && focusedElement.getAttribute('contenteditable') === 'true')
        ? focusedElement
        : viewport.querySelector(`.line-text[data-line="${requestedLine}"]`) || activeEditableElement();
    const wasFocused = !!(element && document.activeElement === element);
    const restoreColumn = element ? getCaretOffset(element) : Math.max(0, Number(state.currentColumn || 1) - 1);
    let finished = false;

    const finish = () => {
        if (finished) return;
        finished = true;

        const lineNumber = Number(state.compositionLine || requestedLine || state.currentLine || 1);
        element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) ||
            element ||
            activeEditableElement();

        if (element && element.getAttribute('contenteditable') === 'true') {
            commitLineForSave(element);
        } else {
            state.isComposing = false;
            state.compositionLine = null;
        }

        if (wasFocused && element && element.getAttribute('contenteditable') === 'true') {
            const textLength = lineTextFromElement(element).length;
            setTimeout(() => setCaret(element, Math.min(restoreColumn, textLength)), 0);
        }

        post({ type: 'editorFlushedForSave', requestId: Number(requestId || 0) });
    };

    if (state.isComposing && element && element.getAttribute('contenteditable') === 'true') {
        try {
            element.blur();
        } catch { }

        setTimeout(finish, 60);
        return;
    }

    setTimeout(finish, 0);
}

function splitCurrentLine(element) {
    const lineNumber = Number(element.dataset.line || 1);
    const text = lineTextFromElement(element);
    const caret = getCaretOffset(element);
    const before = text.slice(0, caret);
    const after = text.slice(caret);
    const indent = (text.match(/^[ \t]*/) || [''])[0];
    const indentedAfter = indent + after;
    state.cache.set(lineNumber, before);
    shiftCachedLines(lineNumber + 1, 1);
    state.cache.set(lineNumber + 1, indentedAfter);
    state.lineCount++;
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    state.dirtyLines.set(lineNumber + 1, 'add');
    setupVirtualHeight();
    post({ type: 'splitLine', lineNumber, before, after: indentedAfter });
    post({ type: 'contentChanged' });
    markLineBoundaryTransition(lineNumber + 1, indent.length);
    queueRender(true);
    setTimeout(() => focusLine(lineNumber + 1, indent.length), 0);
}

function mergeWithPrevious(element) {
    const lineNumber = Number(element.dataset.line || 1);
    if (lineNumber <= 1) return;
    const current = lineTextFromElement(element);
    const previous = state.cache.get(lineNumber - 1);
    if (previous === undefined) {
        queueLineAction({ kind: 'mergeBackward', lineNumber, currentText: current });
        return;
    }

    applyMergeLineBackward(lineNumber, previous, current);
}

function focusLine(lineNumber, columnZeroBased = 0) {
    state.editingLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    if (state.inlineLivePreviewEnabled && state.inlineLivePreviewSourceLine) {
        state.inlineLivePreviewSourceLine = state.editingLine;
        const targetLine = state.editingLine;
        const targetColumn = Math.max(0, Number(columnZeroBased || 0));
        queueRender(true);
        requestAnimationFrame(() => {
            const element = viewport.querySelector(`.line-text[data-line="${targetLine}"]`);
            if (element && element.getAttribute('contenteditable') === 'true') {
                setCaret(element, targetColumn);
                keepElementInView(element);
            }
        });
        return;
    }
    const wrappedTargetTop = lineTop(lineNumber);
    if (wrappedTargetTop < scrollContainer.scrollTop ||
        wrappedTargetTop > scrollContainer.scrollTop + scrollContainer.clientHeight - state.lineHeight) {
        scrollContainer.scrollTop = Math.max(0, wrappedTargetTop - Math.floor(scrollContainer.clientHeight / 2));
    }

    queueRender(true);
    setTimeout(() => {
        const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            setCaret(element, columnZeroBased);
        }
    }, 20);
}

function keepElementInView(element) {
    const containerRect = scrollContainer.getBoundingClientRect();
    const rect = element.closest('.line-row')?.getBoundingClientRect() || element.getBoundingClientRect();
    if (rect.top < containerRect.top) {
        scrollContainer.scrollTop -= containerRect.top - rect.top;
    } else if (rect.bottom > containerRect.bottom) {
        scrollContainer.scrollTop += rect.bottom - containerRect.bottom;
    }
}

function insertTextAtCaret(text) {
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            replaceSelectionWith(sel, text || '');
            return;
        }
    }
    let element = document.activeElement?.closest?.('.line-text');
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        element = activeEditableElement();
        if (element) {
            setCaret(element, Math.max(0, state.currentColumn - 1));
        }
    }
    if (!element || element.getAttribute('contenteditable') !== 'true') return;
    const normalized = String(text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    if (normalized.includes('\n')) {
        const lineNumber = Number(element.dataset.line || 1);
        const current = lineTextFromElement(element);
        const caret = getCaretOffset(element);
        const before = current.slice(0, caret);
        const after = current.slice(caret);
        const parts = normalized.split('\n');
        const insertedCount = parts.length - 1;
        const firstLine = before + parts[0];
        const lastLineNumber = lineNumber + insertedCount;

        state.cache.set(lineNumber, firstLine);
        shiftCachedLines(lineNumber + 1, insertedCount);
        if (!cleanDirtyMarker(lineNumber)) {
            markDirty(lineNumber, 'mod');
        }
        post({ type: 'lineChanged', lineNumber, text: firstLine });
        for (let i = 1; i < parts.length; i++) {
            const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
            const nextLineNumber = lineNumber + i;
            state.cache.set(nextLineNumber, nextText);
            state.dirtyLines.set(nextLineNumber, 'add');
            post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
        }

        state.lineCount += insertedCount;
        setupVirtualHeight();
        post({ type: 'contentChanged' });
        queueRender(true);
        setTimeout(() => focusLine(lastLineNumber, parts[parts.length - 1]?.length || 0), 0);
        return;
    }

    insertPlainTextByModel(element, normalized);
}

function isModelRepeatKey(event) {
    if (!event) return false;
    const isDelOrBack = event.key === 'Backspace' || event.key === 'Delete';
    if (!isDelOrBack) return false;
    if (event.ctrlKey || event.metaKey || event.altKey) {
        return hasCustomSelection();
    }
    return true;
}

function normalizedModelRepeatKey(event) {
    if (event.key === 'Backspace') return 'Backspace';
    if (event.key === 'Delete') return 'Delete';
    return event.key;
}

function isSpaceInputEvent(event) {
    if (!event) return false;
    const inputType = event.inputType || '';
    return (inputType === 'insertText' || inputType === 'insertSpace') && event.data === ' ';
}

function markNativeBeforeInputHandled(inputTypes, durationMs = 120) {
    state.repeatEdit.suppressBeforeInputUntil = performance.now() + durationMs;
    state.repeatEdit.suppressBeforeInputTypes = new Set(inputTypes);
}

function shouldSuppressNativeBeforeInput(event) {
    if (!event || performance.now() > state.repeatEdit.suppressBeforeInputUntil) return false;
    const inputType = event.inputType || '';
    const types = state.repeatEdit.suppressBeforeInputTypes;
    if (types.has(inputType)) return true;
    if (types.has('insertSpace') && inputType.startsWith('insert') && event.data === ' ') return true;
    return false;
}

function markLineBoundaryTransition(targetLine, targetColumn) {
    state.currentLine = Math.min(Math.max(1, Number(targetLine || 1)), state.lineCount);
    state.currentColumn = Math.max(1, Number(targetColumn || 0) + 1);
    state.repeatEdit.lineBoundaryUntil = Math.max(
        state.repeatEdit.lineBoundaryUntil,
        performance.now() + state.repeatEdit.lineBoundaryHoldMs
    );
}

function clearPendingRepeatEdit() {
    if (state.repeatEdit.timer) {
        clearTimeout(state.repeatEdit.timer);
        state.repeatEdit.timer = 0;
    }
    state.repeatEdit.pending = null;
}

function scheduleModelRepeatEdit(key, isRepeat) {
    if (state.readOnly || state.isComposing) return;

    const now = performance.now();
    if (!isRepeat) {
        clearPendingRepeatEdit();
        state.repeatEdit.lastRunAt = now;
        runModelRepeatEdit(key);
        return;
    }

    const boundaryWait = Math.max(0, state.repeatEdit.lineBoundaryUntil - now);
    const intervalWait = Math.max(0, state.repeatEdit.intervalMs - (now - state.repeatEdit.lastRunAt));
    const wait = Math.max(boundaryWait, intervalWait);
    state.repeatEdit.pending = key;

    if (wait <= 0) {
        clearPendingRepeatEdit();
        state.repeatEdit.lastRunAt = now;
        runModelRepeatEdit(key);
        return;
    }

    if (state.repeatEdit.timer) return;
    state.repeatEdit.timer = setTimeout(() => {
        const pending = state.repeatEdit.pending;
        state.repeatEdit.timer = 0;
        state.repeatEdit.pending = null;
        if (!pending || state.readOnly || state.isComposing) return;
        state.repeatEdit.lastRunAt = performance.now();
        runModelRepeatEdit(pending);
    }, wait);
}

function insertPlainTextByModel(element, text) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return;
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) replaceSelectionWith(sel, text || '');
        return;
    }

    makeEditablePlainText(element);
    const current = lineTextFromElement(element);
    const caret = getCaretOffset(element);
    const nextText = current.slice(0, caret) + text + current.slice(caret);
    updateSingleLine(element, nextText, caret + String(text || '').length);
}

// ----------------------------------------------------
// Core Korean IME and Selection Collapse protection helpers
// ----------------------------------------------------
function beginPendingImeSelectionCollapse(element, line, column) {
    state.pendingImeSelectionCollapse = {
        element,
        line,
        column,
        time: performance.now()
    };
}

function clearPendingImeSelectionCollapse() {
    state.pendingImeSelectionCollapse = null;
}

function isPendingImeSelectionCollapseFor(element, event = null) {
    const pending = state.pendingImeSelectionCollapse;
    if (!pending || pending.element !== element) return false;
    if (performance.now() - pending.time > 500) {
        state.pendingImeSelectionCollapse = null;
        return false;
    }
    return true;
}

function syncRenderedRowsAfterCompositionSelectionCollapse(startLine, endLine, nextText, caretColumn, preferredElement = null) {
    const removedLineCount = Math.max(0, Number(endLine || startLine) - Number(startLine || 1));
    const start = Number(startLine || 1);
    const end = Number(endLine || start);
    const preferredRow = preferredElement?.closest?.('.line-row') || null;
    const preferredLine = Number(preferredElement?.dataset?.line || preferredRow?.dataset?.line || 0);
    const rowInfos = [...viewport.querySelectorAll('.line-row')].map(row => ({
        row,
        line: Number(row.dataset.line || 0)
    }));

    let targetRow = null;
    if (preferredRow && preferredLine >= start && preferredLine <= end) {
        targetRow = preferredRow;
    } else {
        targetRow = viewport.querySelector(`.line-row[data-line="${start}"]`);
    }

    const oldTargetRect = targetRow ? targetRow.getBoundingClientRect() : null;

    const startRow = viewport.querySelector(`.line-row[data-line="${start}"]`);
    if (targetRow && startRow && targetRow !== startRow) {
        viewport.insertBefore(targetRow, startRow);
    }

    for (const info of rowInfos) {
        if (!info.line) continue;
        if (info.row === targetRow) continue;
        if (info.line >= start && info.line <= end) {
            info.row.remove();
        }
    }

    if (targetRow) {
        targetRow.dataset.line = String(start);
        targetRow.classList.remove('selected-row', 'selected-empty-row');
        const numberElement = targetRow.querySelector('.line-number');
        if (numberElement) numberElement.textContent = String(start);
        const textElement = targetRow.querySelector('.line-text');
        if (textElement) {
            textElement.dataset.line = String(start);
            if (textElement.childNodes.length === 1 && textElement.firstChild?.nodeType === Node.TEXT_NODE) {
                textElement.firstChild.nodeValue = String(nextText ?? '');
            } else {
                textElement.textContent = String(nextText ?? '');
            }
            const column = Math.max(0, Math.min(Number(caretColumn || 0), textElement.textContent.length));
            const textNode = textElement.firstChild;
            textElement.focus({ preventScroll: true });
            const range = document.createRange();
            if (textNode && textNode.nodeType === Node.TEXT_NODE) {
                range.setStart(textNode, column);
            } else {
                range.setStart(textElement, 0);
            }
            range.collapse(true);
            const selection = window.getSelection();
            if (selection) {
                selection.removeAllRanges();
                selection.addRange(range);
            }
        }
    }

    if (removedLineCount > 0) {
        for (const info of rowInfos) {
            if (!info.line || info.row === targetRow || !info.row.isConnected) continue;
            if (info.line > end) {
                const newLine = info.line - removedLineCount;
                info.row.dataset.line = String(newLine);
                info.row.classList.remove('selected-row', 'selected-empty-row');
                const numberElement = info.row.querySelector('.line-number');
                if (numberElement) numberElement.textContent = String(newLine);
                const textElement = info.row.querySelector('.line-text');
                if (textElement) {
                    textElement.dataset.line = String(newLine);
                    if (state.cache.has(newLine)) {
                        textElement.textContent = state.cache.get(newLine) || '';
                    }
                }
            }
        }
    }

    clearCustomSelectionVisuals();
    if (state.wordWrap) {
        measureRenderedRows(false);
    }

    if (targetRow && oldTargetRect) {
        const newTargetRect = targetRow.getBoundingClientRect();
        const diffY = newTargetRect.top - oldTargetRect.top;
        if (Math.abs(diffY) > 0.5) {
            scrollContainer.scrollTop = Math.max(0, scrollContainer.scrollTop + diffY);
        }
    }

    return targetRow?.querySelector?.('.line-text') || null;
}

function replaceSelectionForCompositionStart(element, markPendingImeStart = false) {
    const selection = compositionSelectionRange();
    if (!selection || selection.isColumn) {
        return element;
    }

    const { start, end } = selection;
    const prefix = (state.cache.get(start.line) ?? '').slice(0, start.column);
    const suffix = (state.cache.get(end.line) ?? '').slice(end.column);
    const nextText = prefix + suffix;
    const removedLineCount = Math.max(0, end.line - start.line);
    const caretColumn = Math.max(0, Math.min(start.column, nextText.length));

    state.selection = null;
    state.selectionAnchor = { line: start.line, column: caretColumn };
    state.currentLine = start.line;
    state.currentColumn = caretColumn + 1;
    state.editingLine = start.line;
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();

    state.cache.set(start.line, nextText);
    for (let line = start.line + 1; line <= end.line; line++) {
        state.cache.delete(line);
    }
    if (removedLineCount > 0) {
        shiftCachedLines(end.line + 1, -removedLineCount);
        state.lineCount = Math.max(1, state.lineCount - removedLineCount);
        setupVirtualHeight();
    }
    state.cacheVersion++;

    if (!cleanDirtyMarker(start.line)) {
        markDirty(start.line, 'mod');
    }

    post({ type: 'lineChanged', lineNumber: start.line, text: nextText });
    for (let line = end.line; line > start.line; line--) {
        post({ type: 'deleteLine', lineNumber: line });
    }
    post({ type: 'contentChanged' });

    const incomingLine = Number(element?.dataset?.line || 0);
    const incomingElementIsInsideSelection = element &&
        element.getAttribute?.('contenteditable') === 'true' &&
        incomingLine >= start.line && incomingLine <= end.line;
    const startRow = viewport.querySelector(`.line-row[data-line="${start.line}"]`);
    const startTextElement = startRow?.querySelector('.line-text') || null;
    let preferredElement = incomingElementIsInsideSelection ? element : startTextElement;

    if (preferredElement && preferredElement.getAttribute('contenteditable') === 'true') {
        makeEditablePlainText(preferredElement, null, false);
        preferredElement.textContent = nextText;
    } else if (startTextElement && startTextElement.getAttribute('contenteditable') === 'true') {
        preferredElement = startTextElement;
        makeEditablePlainText(preferredElement, null, false);
        preferredElement.textContent = nextText;
    }

    const collapsedElement = syncRenderedRowsAfterCompositionSelectionCollapse(
        start.line,
        end.line,
        nextText,
        caretColumn,
        preferredElement
    );
    const targetElement = collapsedElement || preferredElement || startTextElement || element;

    if (targetElement && targetElement.getAttribute?.('contenteditable') === 'true') {
        targetElement.focus({ preventScroll: true });
        const textNode = targetElement.firstChild;
        const range = document.createRange();
        if (textNode && textNode.nodeType === Node.TEXT_NODE) {
            range.setStart(textNode, Math.max(0, Math.min(caretColumn, textNode.textContent.length)));
        } else {
            range.setStart(targetElement, 0);
        }
        range.collapse(true);
        const domSelection = window.getSelection();
        if (domSelection) {
            domSelection.removeAllRanges();
            domSelection.addRange(range);
        }
    }

    if (markPendingImeStart && targetElement && targetElement.getAttribute?.('contenteditable') === 'true') {
        beginPendingImeSelectionCollapse(targetElement, start.line, caretColumn);
    }

    state.lastRangeKey = '';
    if (state.wordWrap) {
        measureRenderedRows(false);
    }
    return targetElement || element;
}

function moveCaretHorizontal(element, direction, extendSelection = false) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return false;

    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    const text = lineTextFromElement(element);
    const caret = Math.max(0, Math.min(getCaretOffset(element), text.length));
    let target = { line: lineNumber, column: caret };

    if (!extendSelection && hasCustomSelection()) {
        const selection = normalizeSelection();
        if (selection) {
            target = direction < 0
                ? { line: selection.start.line, column: selection.start.column }
                : { line: selection.end.line, column: selection.end.column };
        }
    } else if (direction < 0) {
        if (caret > 0) {
            target = { line: lineNumber, column: caret - 1 };
        } else if (lineNumber > 1) {
            const previousText = state.cache.get(lineNumber - 1) || '';
            target = { line: lineNumber - 1, column: previousText.length };
        }
    } else {
        if (caret < text.length) {
            target = { line: lineNumber, column: caret + 1 };
        } else if (lineNumber < state.lineCount) {
            target = { line: lineNumber + 1, column: 0 };
        }
    }

    if (extendSelection) {
        const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
        state.selectionAnchor = anchor;
        state.selection = (anchor.line === target.line && anchor.column === target.column)
            ? null
            : { start: anchor, end: target };
        state.currentLine = target.line;
        state.currentColumn = target.column + 1;
        syncCustomSelectionClass();
        queueRender(true);
        setTimeout(() => focusLine(target.line, target.column), 0);
    } else {
        state.selection = null;
        state.selectionAnchor = { line: target.line, column: target.column };
        syncCustomSelectionClass();
        focusLine(target.line, target.column);
    }

    return true;
}

function runModelRepeatEdit(key) {
    if (state.readOnly || state.isComposing) return;
    let element = activeEditableElement();
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
        return;
    }

    makeEditablePlainText(element);
    if (key === 'Backspace') {
        deleteBackwardAtCaret(element);
    } else if (key === 'Delete') {
        deleteForwardAtCaret(element);
    }
}

function updateSingleLine(element, text, caretColumn) {
    const lineNumber = Number(element.dataset.line || 1);
    const nextText = String(text ?? '');
    const nextColumn = Math.max(0, Math.min(Number(caretColumn || 0), nextText.length));

    state.cache.set(lineNumber, nextText);
    state.cacheVersion++;
    invalidateMeasuredLineHeightsAround(lineNumber);
    state.selection = null;
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();
    state.selectionAnchor = { line: lineNumber, column: nextColumn };
    state.currentLine = lineNumber;
    state.currentColumn = nextColumn + 1;

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    if (state.isComposing || (state.compositionLine && state.compositionLine === lineNumber)) {
        element.textContent = nextText;
    } else {
        element.innerHTML = renderLineContent(lineNumber, nextText);
    }

    setCaret(element, nextColumn);

    post({ type: 'lineChanged', lineNumber, text: nextText });
    post({ type: 'contentChanged' });

    if (state.wordWrap) {
        measureRenderedRows(false);
    }
}

function applyMergeLineForward(lineNumber, text, nextText) {
    if (state.cache.get(lineNumber) !== text) {
        post({ type: 'lineChanged', lineNumber, text });
    }

    shiftCachedLines(lineNumber + 1, -1);
    state.cache.set(lineNumber, text + nextText);
    state.lineCount = Math.max(1, state.lineCount - 1);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    state.dirtyLines.delete(lineNumber + 1);
    setupVirtualHeight();
    post({ type: 'mergeLineWithPrevious', lineNumber: lineNumber + 1 });
    post({ type: 'contentChanged' });
    state.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber, text.length);
    queueRender(true);
    setTimeout(() => focusLine(lineNumber, text.length), 0);
}

function applyMergeLineBackward(lineNumber, previous, current) {
    if (state.cache.get(lineNumber) !== current) {
        post({ type: 'lineChanged', lineNumber, text: current });
    }

    shiftCachedLines(lineNumber, -1);
    state.cache.set(lineNumber - 1, previous + current);
    state.lineCount = Math.max(1, state.lineCount - 1);
    if (!cleanDirtyMarker(lineNumber - 1)) {
        markDirty(lineNumber - 1, 'mod');
    }
    state.dirtyLines.delete(lineNumber);
    setupVirtualHeight();
    post({ type: 'mergeLineWithPrevious', lineNumber });
    post({ type: 'contentChanged' });
    state.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber - 1, previous.length);
    queueRender(true);
    setTimeout(() => focusLine(lineNumber - 1, previous.length), 0);
}

function queueLineAction(action) {
    state.pendingLineActions = state.pendingLineActions.filter(existing =>
        existing.kind !== action.kind || existing.lineNumber !== action.lineNumber);
    state.pendingLineActions.push(action);
    if (action.kind === 'mergeBackward') {
        requestLines(Math.max(1, action.lineNumber - 1), 2);
    } else if (action.kind === 'mergeForward') {
        requestLines(action.lineNumber + 1, 1);
    }
}

function runPendingLineActions() {
    if (state.pendingLineActions.length === 0) return;

    const remaining = [];
    for (const action of state.pendingLineActions) {
        if (action.kind === 'mergeBackward') {
            const previous = state.cache.get(action.lineNumber - 1);
            if (previous !== undefined) {
                applyMergeLineBackward(action.lineNumber, previous, action.currentText);
                continue;
            }
        } else if (action.kind === 'mergeForward') {
            const nextText = state.cache.get(action.lineNumber + 1);
            if (nextText !== undefined) {
                applyMergeLineForward(action.lineNumber, action.currentText, nextText);
                continue;
            }
        }

        remaining.push(action);
    }

    state.pendingLineActions = remaining;
}

function mergeLineForward(element) {
    const lineNumber = Number(element.dataset.line || 1);
    if (lineNumber >= state.lineCount) return;

    const text = lineTextFromElement(element);
    const nextText = state.cache.get(lineNumber + 1);
    if (nextText === undefined) {
        queueLineAction({ kind: 'mergeForward', lineNumber, currentText: text });
        return;
    }

    applyMergeLineForward(lineNumber, text, nextText);
}

function mergeLineBackward(element) {
    const lineNumber = Number(element.dataset.line || 1);
    if (lineNumber <= 1) return;

    const current = lineTextFromElement(element);
    const previous = state.cache.get(lineNumber - 1);
    if (previous === undefined) {
        queueLineAction({ kind: 'mergeBackward', lineNumber, currentText: current });
        return;
    }

    applyMergeLineBackward(lineNumber, previous, current);
}

function deleteForwardAtCaret(element = activeEditableElement()) {
    if (!element) return;
    if (document.activeElement !== element) {
        setCaret(element, Math.max(0, state.currentColumn - 1));
    }
    makeEditablePlainText(element);
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            if (sel.isColumn && sel.start.column === sel.end.column) {
                sel.end.column = sel.end.column + 1;
            }
            replaceSelectionWith(sel, '');
        }
        return;
    }

    const text = lineTextFromElement(element);
    const caret = getCaretOffset(element);
    if (caret < text.length) {
        const delEnd = graphemeDeleteEnd(text, caret);
        updateSingleLine(element, text.slice(0, caret) + text.slice(delEnd), caret);
        return;
    }

    mergeLineForward(element);
}

function deleteBackwardAtCaret(element = activeEditableElement()) {
    if (!element) return;
    if (document.activeElement !== element) {
        setCaret(element, Math.max(0, state.currentColumn - 1));
    }
    makeEditablePlainText(element);
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            if (sel.isColumn && sel.start.column === sel.end.column) {
                sel.start.column = Math.max(0, sel.start.column - 1);
            }
            replaceSelectionWith(sel, '');
        }
        return;
    }

    const text = lineTextFromElement(element);
    const caret = getCaretOffset(element);
    if (caret > 0) {
        const tabSize = state.tabSize || 4;
        const prefix = text.slice(0, caret);
        const onlySpacesBefore = prefix.length > 0 && /^ *$/.test(prefix);
        if (onlySpacesBefore && prefix.length % tabSize === 0) {
            const deleteStart = caret - Math.min(tabSize, caret);
            updateSingleLine(element, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
        } else {
            const deleteStart = graphemeDeleteStart(text, caret);
            updateSingleLine(element, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
        }
        return;
    }

    mergeLineBackward(element);
}

function replaceColumnSelectionWith(selection, text, skipRender = false) {
    const normalized = normalizeSelection(selection);
    if (!normalized || !normalized.isColumn) return;

    const { start, end } = normalized;
    const startLine = Math.min(start.line, end.line);
    const endLine = Math.max(start.line, end.line);
    const startCol = Math.min(start.column, end.column);
    const endCol = Math.max(start.column, end.column);
    const lineCount = endLine - startLine + 1;

    const replacementText = String(text ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const lines = replacementText.split('\n');
    const useLineByLinePaste = lines.length === lineCount;
    const insertedLengthForCaret = useLineByLinePaste ? lines[0].length : replacementText.length;

    for (let line = startLine; line <= endLine; line++) {
        const originalText = state.cache.get(line) || '';
        const replaceText = useLineByLinePaste ? lines[line - startLine] : replacementText;

        const sCol = Math.max(0, Math.min(startCol, originalText.length));
        const eCol = Math.max(0, Math.min(endCol, originalText.length));

        const nextText = originalText.slice(0, sCol) + replaceText + originalText.slice(eCol);
        state.cache.set(line, nextText);
    if (!cleanDirtyMarker(line)) {
        markDirty(line, 'mod');
    }
        post({ type: 'lineChanged', lineNumber: line, text: nextText });
    }

    state.cacheVersion++;
    post({ type: 'contentChanged' });

    const nextCol = startCol + insertedLengthForCaret;
    state.selection = {
        start: { line: startLine, column: nextCol },
        end: { line: endLine, column: nextCol },
        isColumn: true
    };
    state.selectionAnchor = state.selection.start;
    state.currentLine = endLine;
    state.currentColumn = nextCol + 1;
    syncCustomSelectionClass();

    if (skipRender) {
        drawEditableSelectionOverlays();
        reportCursorAndSelection(activeEditableElement());
    } else {
        queueRender(true);
        setTimeout(() => {
            focusLine(state.currentLine, nextCol);
            reportCursorAndSelection();
        }, 0);
    }
}

function changedTextBetween(beforeText, afterText) {
    const before = String(beforeText ?? '');
    const after = String(afterText ?? '');
    let prefix = 0;
    while (prefix < before.length &&
        prefix < after.length &&
        before[prefix] === after[prefix]) {
        prefix++;
    }

    let beforeEnd = before.length;
    let afterEnd = after.length;
    while (beforeEnd > prefix &&
        afterEnd > prefix &&
        before[beforeEnd - 1] === after[afterEnd - 1]) {
        beforeEnd--;
        afterEnd--;
    }

    return after.slice(prefix, afterEnd);
}

function columnCompositionBounds(selection) {
    const normalized = normalizeSelection(selection);
    if (!normalized || !normalized.isColumn) return null;
    const startLine = Math.min(normalized.start.line, normalized.end.line);
    const endLine = Math.max(normalized.start.line, normalized.end.line);
    const startCol = Math.min(normalized.start.column, normalized.end.column);
    const endCol = Math.max(normalized.start.column, normalized.end.column);
    return { startLine, endLine, startCol, endCol };
}

function isLineInColumnComposition(lineNumber) {
    const pending = state.columnComposition;
    const bounds = pending ? columnCompositionBounds(pending.selection) : null;
    if (!bounds) return false;
    return lineNumber >= bounds.startLine && lineNumber <= bounds.endLine;
}

function updateVisibleLineTextDuringComposition(lineNumber, text, preserveElement = null) {
    const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
    if (!element || element === preserveElement || element.getAttribute('contenteditable') !== 'true') return;
    element.innerHTML = renderLineContent(lineNumber, text);
}

function buildColumnCompositionLine(baseText, startCol, endCol, insertedText) {
    const originalText = String(baseText ?? '');
    const sCol = Math.max(0, Math.min(startCol, originalText.length));
    const eCol = Math.max(0, Math.min(endCol, originalText.length));
    return originalText.slice(0, sCol) + String(insertedText ?? '') + originalText.slice(eCol);
}

function applyColumnCompositionPreview(element, insertedText) {
    const pending = state.columnComposition;
    const bounds = pending ? columnCompositionBounds(pending.selection) : null;
    if (!pending || !bounds) return false;

    let changed = false;
    let posted = false;
    const previewText = String(insertedText ?? '');

    for (let line = bounds.startLine; line <= bounds.endLine; line++) {
        const baseText = pending.baseLines.get(line) ?? '';
        const nextText = buildColumnCompositionLine(baseText, bounds.startCol, bounds.endCol, previewText);
        const previousPreview = pending.lastPreviewLines.get(line);

        state.cache.set(line, nextText);
        pending.lastPreviewLines.set(line, nextText);
        updateVisibleLineTextDuringComposition(line, nextText, element);

        if (previousPreview !== nextText) {
            post({ type: 'lineChanged', lineNumber: line, text: nextText, isComposing: true, isColumnComposition: true });
            posted = true;
        }
        changed = true;
    }

    if (changed) {
        state.cacheVersion++;
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        drawEditableSelectionOverlays();
    }

    if (posted) {
        post({ type: 'contentChanged', isComposing: true, isColumnComposition: true });
    }

    return changed;
}

function updateColumnCompositionPreview(element) {
    const pending = state.columnComposition;
    if (!pending || !element || element.getAttribute('contenteditable') !== 'true') return false;

    const finalText = lineTextFromElement(element);
    const insertedText = changedTextBetween(pending.beforeText, finalText);
    return applyColumnCompositionPreview(element, insertedText);
}

function restoreColumnCompositionBase(pending, postChanges = false, preserveElement = null) {
    const bounds = pending ? columnCompositionBounds(pending.selection) : null;
    if (!pending || !bounds) return;

    let posted = false;
    for (let line = bounds.startLine; line <= bounds.endLine; line++) {
        const baseText = pending.baseLines.get(line) ?? '';
        const previousPreview = pending.lastPreviewLines.get(line);
        state.cache.set(line, baseText);
        updateVisibleLineTextDuringComposition(line, baseText, preserveElement);

        if (postChanges && previousPreview !== undefined && previousPreview !== baseText) {
            post({ type: 'lineChanged', lineNumber: line, text: baseText, isComposing: true, isColumnComposition: true, isCompositionCancel: true });
            posted = true;
        }
    }

    state.cacheVersion++;
    if (posted) {
        post({ type: 'contentChanged', isComposing: true, isColumnComposition: true, isCompositionCancel: true });
    }
}

function beginColumnComposition(element) {
    const selection = activeColumnSelection();
    const bounds = selection ? columnCompositionBounds(selection) : null;
    if (!bounds || !element || element.getAttribute('contenteditable') !== 'true') {
        state.columnComposition = null;
        return false;
    }

    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    const baseLines = new Map();
    for (let line = bounds.startLine; line <= bounds.endLine; line++) {
        if (line === lineNumber) {
            baseLines.set(line, lineTextFromElement(element));
        } else {
            baseLines.set(line, state.cache.get(line) ?? '');
        }
    }

    state.columnComposition = {
        selection: cloneEditorSelection(selection),
        lineNumber,
        beforeText: baseLines.get(lineNumber) ?? lineTextFromElement(element),
        caretColumn: getCaretOffset(element),
        baseLines,
        lastPreviewLines: new Map()
    };
    return true;
}

function finishColumnComposition(element, lineNumber) {
    const pending = state.columnComposition;
    if (!pending || !pending.selection || !element || element.getAttribute('contenteditable') !== 'true') {
        state.columnComposition = null;
        return false;
    }

    const targetLine = Number(lineNumber || element.dataset.line || pending.lineNumber || state.currentLine || 1);
    if (targetLine !== pending.lineNumber) {
        restoreColumnCompositionBase(pending, true, element);
        state.columnComposition = null;
        return false;
    }

    const finalText = lineTextFromElement(element);
    const insertedText = changedTextBetween(pending.beforeText, finalText);
    const originalSelection = cloneEditorSelection(pending.selection);
    const changed = insertedText.length > 0 || finalText !== pending.beforeText;

    restoreColumnCompositionBase(pending, !changed, element);
    state.columnComposition = null;

    if (changed) {
        replaceColumnSelectionWith(originalSelection, insertedText, true);
        queueRender(true);
    } else {
        state.selection = originalSelection;
        state.selectionAnchor = originalSelection.start;
        syncCustomSelectionClass();
        queueRender(true);
        setTimeout(() => focusLine(state.currentLine, Math.max(0, state.currentColumn - 1)), 0);
    }

    return true;
}

function replaceSelectionWith(selection, text, editSelection = null) {
    if (selection.isColumn) {
        replaceColumnSelectionWith(selection, text);
        return;
    }
    const { start, end } = selection;
    const prefix = (state.cache.get(start.line) || '').slice(0, start.column);
    const suffix = (state.cache.get(end.line) || '').slice(end.column);
    const linesToRemove = end.line - start.line;
    const replacementText = String(text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const parts = replacementText.split('\n');
    const newLines = [];

    if (parts.length === 1) {
        newLines.push(prefix + parts[0] + suffix);
    } else {
        newLines.push(prefix + parts[0]);
        for (let i = 1; i < parts.length - 1; i++) {
            newLines.push(parts[i]);
        }
        newLines.push(parts[parts.length - 1] + suffix);
    }

    const netLines = newLines.length - 1 - linesToRemove;
    const shiftAmount = newLines.length - (linesToRemove + 1);

    state.cache.set(start.line, newLines[0]);
    for (let i = start.line + 1; i <= end.line; i++) {
        state.cache.delete(i);
    }
    if (shiftAmount !== 0) {
        shiftCachedLines(end.line + 1, shiftAmount);
    }
    for (let i = 1; i < newLines.length; i++) {
        state.cache.set(start.line + i, newLines[i]);
    }

    if (!cleanDirtyMarker(start.line)) {
        markDirty(start.line, 'mod');
    }
    for (let i = 1; i < newLines.length; i++) {
        state.dirtyLines.set(start.line + i, 'add');
    }

    state.lineCount = Math.max(1, state.lineCount + netLines);
    setupVirtualHeight();
    post({ type: 'lineChanged', lineNumber: start.line, text: newLines[0] });
    for (let i = end.line; i > start.line; i--) {
        post({ type: 'deleteLine', lineNumber: i });
    }
    for (let i = 1; i < newLines.length; i++) {
        post({ type: 'insertLine', lineNumber: start.line + i, text: newLines[i] });
    }
    post({ type: 'contentChanged' });
    if (editSelection) {
        const positionFromOffset = offset => {
            const safeOffset = Math.max(0, Math.min(offset, replacementText.length));
            const before = replacementText.slice(0, safeOffset).split('\n');
            if (before.length === 1) {
                return { line: start.line, column: start.column + before[0].length };
            }
            return { line: start.line + before.length - 1, column: before[before.length - 1].length };
        };
        const selectionStart = positionFromOffset(editSelection.startOffset ?? 0);
        const selectionEnd = positionFromOffset(editSelection.endOffset ?? editSelection.startOffset ?? 0);
        state.selectionAnchor = selectionStart;
        state.selection = editSelection.startOffset === editSelection.endOffset
            ? null
            : { start: selectionStart, end: selectionEnd };
        state.currentLine = selectionEnd.line;
        state.currentColumn = selectionEnd.column + 1;
    } else {
        state.selection = null;
        syncCustomSelectionClass();
        clearCustomSelectionVisuals();
        const endLine = start.line + parts.length - 1;
        const endColumn = parts.length === 1 ? start.column + parts[0].length : parts[parts.length - 1].length;
        state.currentLine = endLine;
        state.currentColumn = endColumn + 1;
    }
    if (!editSelection) {
        const immediateLine = state.currentLine;
        const immediateColumn = Math.max(0, state.currentColumn - 1);
        const immediateElement = viewport.querySelector(`.line-text[data-line="${immediateLine}"]`);
        if (immediateElement && immediateElement.getAttribute('contenteditable') === 'true') {
            immediateElement.textContent = state.cache.get(immediateLine) || '';
            setCaret(immediateElement, immediateColumn);
        }
    }

    queueRender(true);
    setTimeout(() => {
        if (editSelection) {
            const targetLine = state.currentLine;
            const targetColumn = Math.max(0, state.currentColumn - 1);
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection();
        } else {
            focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
        }
    }, 0);
}

function deleteCurrentLine(element) {
    const lineNumber = Number(element.dataset.line || 1);
    if (state.lineCount <= 1) return;

    const nextText = state.cache.get(lineNumber + 1);
    if (nextText !== undefined) {
        const current = lineTextFromElement(element);
        if (state.cache.get(lineNumber) !== current) {
            post({ type: 'lineChanged', lineNumber, text: current });
        }
        shiftCachedLines(lineNumber + 1, -1);
        state.cache.set(lineNumber, current + nextText);
        state.lineCount = Math.max(1, state.lineCount - 1);
        if (!cleanDirtyMarker(lineNumber)) {
            markDirty(lineNumber, 'mod');
        }
        state.dirtyLines.delete(lineNumber + 1);
        setupVirtualHeight();
        post({ type: 'mergeLineWithPrevious', lineNumber: lineNumber + 1 });
        post({ type: 'contentChanged' });
        queueRender(true);
        setTimeout(() => focusLine(lineNumber, current.length), 0);
    } else if (lineNumber > 1) {
        mergeLineBackward(element);
    }
}

const { applyMarkdownCommand } = createMarkdownCommandHandlers({
    activeEditableElement,
    cleanDirtyMarker,
    comparePositions,
    focusLine,
    getCaretOffset,
    hasCustomSelection,
    hasTextAt,
    isStandaloneDelimiter,
    lineTextFromElement,
    markDirty,
    normalizeSelection,
    orderedRange,
    post,
    queueRender,
    replaceSelectionWith,
    reportCursorAndSelection,
    state,
    syncCustomSelectionClass,
    writeClipboardText
});
function toggleCommentForLine(lineNumber, syntax, shouldUncomment) {
    const original = state.cache.get(lineNumber) ?? '';
    const indent = original.match(/^\s*/)?.[0] || '';
    const body = original.slice(indent.length);
    let next = original;

    if (shouldUncomment && body.trim().length === 0) {
        return;
    }

    if (syntax.prefix) {
        next = shouldUncomment && body.startsWith(syntax.prefix)
            ? indent + body.slice(syntax.prefix.length)
            : indent + syntax.prefix + body;
    } else {
        const { blockStart, blockEnd } = syntax;
        const trimmed = body.trim();
        if (shouldUncomment && trimmed.startsWith(blockStart) && trimmed.endsWith(blockEnd)) {
            const leading = body.slice(0, body.indexOf(trimmed));
            const inner = trimmed.slice(blockStart.length, trimmed.length - blockEnd.length);
            next = indent + leading + inner;
        } else {
            next = indent + blockStart + body + blockEnd;
        }
    }

    state.cache.set(lineNumber, next);
    post({ type: 'lineChanged', lineNumber, text: next });
}

function toggleComment() {
    if (state.readOnly) return;
    const { startLine, endLine } = selectedLineRange();
    const syntax = lineCommentSyntax();
    const shouldUncomment = (() => {
        for (let line = startLine; line <= endLine; line++) {
            const text = state.cache.get(line) ?? '';
            const body = text.slice((text.match(/^\s*/)?.[0] || '').length);
            if (syntax.prefix && body.length > 0 && !body.startsWith(syntax.prefix)) return false;
            if (!syntax.prefix) {
                const trimmed = body.trim();
                if (trimmed.length > 0 && !(trimmed.startsWith(syntax.blockStart) && trimmed.endsWith(syntax.blockEnd))) {
                    return false;
                }
            }
        }
        return true;
    })();

    for (let line = startLine; line <= endLine; line++) {
        toggleCommentForLine(line, syntax, shouldUncomment);
    }

    post({ type: 'contentChanged' });
    queueRender(true);
    setTimeout(() => focusLine(startLine, 0), 0);
}

function changeLineIndent(direction) {
    if (state.readOnly) return;

    const { startLine, endLine } = selectedLineRange();
    const indentText = ' '.repeat(Math.max(1, state.tabSize || 4));
    let changed = false;

    for (let line = startLine; line <= endLine; line++) {
        const original = state.cache.get(line);
        if (original === undefined) continue;

        let next = original;
        if (direction > 0) {
            next = indentText + original;
        } else if (original.startsWith('\t')) {
            next = original.slice(1);
        } else {
            const leadingSpaces = original.match(/^ +/)?.[0].length || 0;
            const removeCount = Math.min(indentText.length, leadingSpaces);
            if (removeCount > 0) {
                next = original.slice(removeCount);
            }
        }

        if (next !== original) {
            state.cache.set(line, next);
            if (!cleanDirtyMarker(line)) {
                markDirty(line, 'mod');
            }
            post({ type: 'lineChanged', lineNumber: line, text: next });
            changed = true;
        }
    }

    if (!changed) return;

    post({ type: 'contentChanged' });
    queueRender(true);
    setTimeout(() => focusLine(startLine, 0), 0);
}

function handleLineSortingAndCleanup(action) {
    if (state.readOnly) return;

    let useWholeDocument = !hasCustomSelection();
    let startLine, endLine;

    if (useWholeDocument) {
        startLine = 1;
        endLine = state.lineCount;
    } else {
        const range = selectedLineRange();
        startLine = range.startLine;
        endLine = range.endLine;
    }

    const lineSelection = {
        start: { line: startLine, column: 0 },
        end: { line: endLine, column: (state.cache.get(endLine) || '').length }
    };

    const lines = [];
    for (let i = startLine; i <= endLine; i++) {
        lines.push(state.cache.get(i) || '');
    }

    let newLines = [...lines];

    switch (action) {
        case 'sortAsc':
            newLines.sort((a, b) => a.localeCompare(b));
            break;
        case 'sortDesc':
            newLines.sort((a, b) => b.localeCompare(a));
            break;
        case 'removeDuplicates':
            const seen = new Set();
            newLines = lines.filter(line => {
                if (seen.has(line)) return false;
                seen.add(line);
                return true;
            });
            break;
        case 'removeEmptyLines':
            newLines = lines.filter(line => line.trim() !== '');
            break;
        case 'collapseConsecutiveEmptyLines':
            newLines = [];
            let prevEmpty = false;
            for (let i = 0; i < lines.length; i++) {
                const isEmpty = lines[i].trim() === '';
                if (isEmpty) {
                    if (!prevEmpty) {
                        newLines.push(lines[i]);
                        prevEmpty = true;
                    }
                } else {
                    newLines.push(lines[i]);
                    prevEmpty = false;
                }
            }
            break;
        case 'trimSpaces':
            newLines = lines.map(line => line.trim());
            break;
    }

    const transformedText = newLines.join('\n');
    replaceSelectionWith(lineSelection, transformedText);
}

function handleTextConversion(action) {
    if (state.readOnly) return;

    if (action === 'insertDivider') {
        const divider = '\n---\n';
        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, divider);
        } else {
            insertTextAtCaret(divider);
        }
        return;
    }

    let sel;
    let text;

    if (hasCustomSelection()) {
        sel = normalizeSelection();
        text = selectedText();
    } else {
        sel = {
            start: { line: 1, column: 0 },
            end: { line: state.lineCount, column: (state.cache.get(state.lineCount) || '').length }
        };
        const parts = [];
        for (let i = 1; i <= state.lineCount; i++) {
            parts.push(state.cache.get(i) || '');
        }
        text = parts.join('\n');
    }

    if (!sel || !text) return;

    let transformed = text;

    switch (action) {
        case 'toUpperCase':
            transformed = text.toUpperCase();
            break;
        case 'toLowerCase':
            transformed = text.toLowerCase();
            break;
        case 'toSentenceCase':
            transformed = text.replace(/((?:^|[.!?]\s+)\s*)(\S)/g, (match, p1, p2) => p1 + p2.toUpperCase());
            break;
        case 'toTitleCase':
            transformed = text.toLowerCase().replace(/\b([a-z])/g, match => match.toUpperCase());
            break;
        case 'urlEncode':
            try {
                transformed = encodeURIComponent(text);
            } catch (e) {
                transformed = text;
            }
            break;
        case 'urlDecode':
            try {
                transformed = decodeURIComponent(text);
            } catch (e) {
                transformed = text;
            }
            break;
        case 'base64Encode':
            try {
                transformed = btoa(encodeURIComponent(text).replace(/%([0-9A-F]{2})/g, (match, p1) => {
                    return String.fromCharCode(parseInt(p1, 16));
                }));
            } catch (e) {
                transformed = text;
            }
            break;
        case 'base64Decode':
            try {
                transformed = decodeURIComponent(atob(text).split('').map(c => {
                    return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
                }).join(''));
            } catch (e) {
                transformed = text;
            }
            break;
        case 'hexToDec':
            try {
                const trimmed = text.trim();
                const cleaned = trimmed.replace(/^(0x|0X)/, '');
                transformed = BigInt('0x' + cleaned).toString();
            } catch (e) {
                transformed = text;
            }
            break;
        case 'decToHex':
            try {
                const trimmed = text.trim();
                if (/^-?\d+$/.test(trimmed)) {
                    const dec = BigInt(trimmed);
                    if (dec < 0n) {
                        transformed = '-' + (-dec).toString(16).toUpperCase();
                    } else {
                        transformed = '0x' + dec.toString(16).toUpperCase();
                    }
                }
            } catch (e) {
                transformed = text;
            }
            break;
    }

    replaceSelectionWith(sel, transformed);
}

function handleFormatText() {
    if (state.readOnly) return;

    let sel;
    let text;

    if (hasCustomSelection()) {
        sel = normalizeSelection();
        text = selectedText();
    } else {
        sel = {
            start: { line: 1, column: 0 },
            end: { line: state.lineCount, column: (state.cache.get(state.lineCount) || '').length }
        };
        const parts = [];
        for (let i = 1; i <= state.lineCount; i++) {
            parts.push(state.cache.get(i) || '');
        }
        text = parts.join('\n');
    }

    if (!sel || !text) return;

    let transformed = text;
    let lines = text.split('\n');

    lines = lines.map(line => line.trimEnd());

    if (state.language === 'markdown') {
        lines = lines.map(line => {
            const headerMatch = line.match(/^(#{1,6})([^\s#].*)$/);
            if (headerMatch) {
                return headerMatch[1] + ' ' + headerMatch[2].trim();
            }
            const listMatch = line.match(/^(\s*)([-*+]|\d+\.)([^\s].*)$/);
            if (listMatch) {
                return listMatch[1] + listMatch[2] + ' ' + listMatch[3].trim();
            }
            const quoteMatch = line.match(/^(\s*)(>+)([^\s>].*)$/);
            if (quoteMatch) {
                return quoteMatch[1] + quoteMatch[2] + ' ' + quoteMatch[3].trim();
            }
            return line;
        });
        transformed = lines.join('\n');
    } else if (state.language === 'json') {
        try {
            transformed = JSON.stringify(JSON.parse(text), null, state.tabSize || 4);
        } catch (e) {
            transformed = lines.join('\n');
        }
    } else {
        transformed = lines.join('\n');
    }

    replaceSelectionWith(sel, transformed);
}

function deleteSelectionOrForward() {
    if (state.readOnly) return;
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) replaceSelectionWith(sel, '');
        return;
    }

    const selection = window.getSelection();
    const element = activeEditableElement();
    if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
        const selected = selection.toString();
        if (selected) {
            document.execCommand('delete');
            commitLine(element);
            return;
        }
    }

    deleteForwardAtCaret(element);
}

async function cutSelectionToClipboard() {
    const text = selectedText();
    if (!text) return false;
    const copied = await writeClipboardText(text);
    if (!copied || state.readOnly) return copied;

    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) replaceSelectionWith(sel, '');
        return true;
    }

    const element = activeEditableElement();
    const selection = window.getSelection();
    if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
        document.execCommand('delete');
        commitLine(element);
    }

    return true;
}

async function copySelectionToClipboard() {
    const text = selectedText();
    if (!text) return false;
    return await writeClipboardText(text);
}

async function pasteFromClipboard() {
    if (state.readOnly) return;
    const text = await readClipboardText();
    if (text) insertTextAtCaret(text);
}

function lineElementFromEvent(event) {
    const target = event.target;
    if (target?.closest) {
        return target.closest('.line-text');
    }
    return target?.parentElement?.closest?.('.line-text') || null;
}

function selectAll() {
    const lastLine = state.lineCount;
    const lastText = state.cache.get(lastLine) || '';
    const endColumn = lastText.length;
    state.selectionAnchor = { line: 1, column: 0 };
    state.selection = { start: { line: 1, column: 0 }, end: { line: lastLine, column: endColumn } };
    syncCustomSelectionClass();
    state.currentLine = lastLine;
    state.currentColumn = endColumn + 1;
    queueRender(true);
    setTimeout(() => focusLine(1, 0), 0);
    reportCursorAndSelection();
}

export {
    applyMarkdownCommand,
    beginColumnComposition,
    clearPendingImeSelectionCollapse,
    clearPendingRepeatEdit,
    commitLine,
    compositionSelectionRange,
    copySelectionToClipboard,
    cutSelectionToClipboard,
    deleteSelectionOrForward,
    finishColumnComposition,
    flushPendingEditForSave,
    focusLine,
    getCaretOffset,
    handleFormatText,
    handleLineSortingAndCleanup,
    handleTextConversion,
    inputRangeInElement,
    insertPlainTextByModel,
    insertTextAtCaret,
    isLineInColumnComposition,
    isModelRepeatKey,
    isPendingImeSelectionCollapseFor,
    isSpaceInputEvent,
    lineElementFromEvent,
    lineTextFromElement,
    makeEditablePlainText,
    markNativeBeforeInputHandled,
    mergeLineBackward,
    mergeLineForward,
    moveCaretHorizontal,
    moveCaretVertical,
    normalizedModelRepeatKey,
    pasteFromClipboard,
    positionFromPointer,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    runPendingLineActions,
    scheduleModelRepeatEdit,
    selectAll,
    selectWordAtPointer,
    setCaret,
    shouldSuppressNativeBeforeInput,
    splitCurrentLine,
    toggleComment,
    changeLineIndent,
    updateSingleLine
};
