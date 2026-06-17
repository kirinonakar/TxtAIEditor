import {
    lineHeightFor,
    lineTop,
    orderedRange,
    queueRender,
    reportCursorAndSelection,
    state
} from './editor-core.js';
import { scrollContainer, viewport } from './editor-dom.js';
import {
    drawEditableSelectionOverlays,
    normalizeSelection
} from './editor-selection.js';

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

function lineElementFromEvent(event) {
    const target = event.target;
    if (target?.closest) {
        return target.closest('.line-text');
    }
    return target?.parentElement?.closest?.('.line-text') || null;
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

    if (state.alignCaretToY !== null && state.alignCaretToY !== undefined) {
        const clickY = state.alignCaretToY;
        state.alignCaretToY = null;
        const caretRect = caretRectForOffset(element, offset);
        if (caretRect) {
            const caretCenter = caretRect.top + caretRect.height / 2;
            const scrollDiff = caretCenter - clickY;
            const maxScroll = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
            scrollContainer.scrollTop = Math.min(maxScroll, Math.max(0, scrollContainer.scrollTop + scrollDiff));
        }
    }

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
        const maxCaretHeight = Math.max(state.lineHeight * 1.75, state.lineHeight + 8);
        let rect = range.getBoundingClientRect();
        if (rect && (rect.width > 0 || rect.height > 0) && rect.height <= maxCaretHeight) return rect;

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

let _focusRetryTimer = 0;

function focusLine(lineNumber, columnZeroBased = 0, scrollMargin = 0) {
    if (_focusRetryTimer) {
        clearTimeout(_focusRetryTimer);
        _focusRetryTimer = 0;
    }
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

    if (scrollMargin > 0) {
        const viewTop = scrollContainer.scrollTop;
        const viewBottom = viewTop + scrollContainer.clientHeight;
        const lineH = lineHeightFor(lineNumber);
        const targetBottom = wrappedTargetTop + lineH;

        if (targetBottom >= viewBottom - scrollMargin) {
            const maxScroll = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
            scrollContainer.scrollTop = Math.min(maxScroll, scrollContainer.scrollTop + (targetBottom - (viewBottom - scrollMargin)));
        } else if (wrappedTargetTop <= viewTop + scrollMargin) {
            scrollContainer.scrollTop = Math.max(0, scrollContainer.scrollTop - ((viewTop + scrollMargin) - wrappedTargetTop));
        }
    } else {
        if (wrappedTargetTop < scrollContainer.scrollTop ||
            wrappedTargetTop > scrollContainer.scrollTop + scrollContainer.clientHeight - state.lineHeight) {
            scrollContainer.scrollTop = Math.max(0, wrappedTargetTop - Math.floor(scrollContainer.clientHeight / 2));
        }
    }

    queueRender(true);
    let retries = 10;
    function tryFocus() {
        const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            setCaret(element, columnZeroBased);
            _focusRetryTimer = 0;
        } else if (retries > 0) {
            retries--;
            _focusRetryTimer = setTimeout(tryFocus, 20);
        }
    }
    _focusRetryTimer = setTimeout(tryFocus, 20);
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

function textPositionForOffsetInElement(element, offset) {
    if (!element) return null;

    let remaining = Math.max(0, Number(offset || 0));
    let lastTextNode = null;

    function walk(node) {
        if (node.nodeType === Node.TEXT_NODE) {
            lastTextNode = node;
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

    return walk(element) || (lastTextNode
        ? { node: lastTextNode, offset: lastTextNode.textContent.length }
        : null);
}

function setNativeSelectionRangeInElement(element, startOffset, endOffset) {
    if (!element || element.getAttribute?.('contenteditable') !== 'true') return false;

    const textLength = lineTextFromElement(element).length;
    const start = Math.max(0, Math.min(Number(startOffset || 0), textLength));
    const end = Math.max(start, Math.min(Number(endOffset || 0), textLength));
    const startPosition = textPositionForOffsetInElement(element, start);
    const endPosition = textPositionForOffsetInElement(element, end);
    if (!startPosition || !endPosition) return false;

    const range = document.createRange();
    range.setStart(startPosition.node, startPosition.offset);
    range.setEnd(endPosition.node, endPosition.offset);

    element.focus({ preventScroll: true });
    const domSelection = window.getSelection();
    if (!domSelection) return false;
    domSelection.removeAllRanges();
    domSelection.addRange(range);
    return true;
}

export {
    caretRectForOffset,
    compositionSelectionRange,
    focusLine,
    getCaretOffset,
    inputRangeInElement,
    lineElementFromEvent,
    lineTextFromElement,
    makeEditablePlainText,
    offsetFromPointInElement,
    positionFromPointer,
    selectWordAtPointer,
    setCaret,
    setNativeSelectionRangeInElement
};
