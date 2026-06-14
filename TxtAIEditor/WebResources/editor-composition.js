export function createEditorCompositionHandlers({
    activeColumnSelection,
    activeEditableElement,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    cloneEditorSelection,
    compositionSelectionRange,
    drawEditableSelectionOverlays,
    focusLine,
    getCaretOffset,
    lineTextFromElement,
    makeEditablePlainText,
    markDirty,
    measureRenderedRows,
    normalizeSelection,
    post,
    queueRender,
    renderLineContent,
    replaceColumnSelectionWith,
    replaceSelectionWith,
    reportCursorAndSelection,
    scrollContainer,
    setCaret,
    setNativeSelectionRangeInElement,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    viewport
}) {
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

function prepareSingleLineSelectionForNativeComposition(selection) {
    if (!selection || selection.isColumn || selection.start.line !== selection.end.line) {
        return null;
    }

    const { start, end } = selection;
    if (start.column === end.column) return null;

    const targetElement = viewport.querySelector(`.line-text[data-line="${start.line}"]`);
    if (!targetElement || targetElement.getAttribute('contenteditable') !== 'true') {
        return null;
    }

    if (!setNativeSelectionRangeInElement(targetElement, start.column, end.column)) {
        return null;
    }

    // 한 줄 선택 영역은 직접 지우지 말고 브라우저/IME의 네이티브 replace-composition에 맡긴다.
    // compositionstart 도중 textContent를 바꾸고 캐럿을 다시 잡으면 WebView2/Chrome 한글 IME가
    // 첫 음절을 `ㅍㅗ`처럼 호환 자모 두 글자로 확정하는 경우가 있다.
    state.selection = null;
    state.selectionAnchor = { line: start.line, column: start.column };
    state.currentLine = start.line;
    state.currentColumn = start.column + 1;
    state.editingLine = start.line;
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();
    reportCursorAndSelection(targetElement);
    return targetElement;
}

function finishRangeComposition(element, lineNumber, compositionText = '') {
    const pending = state.rangeComposition;
    if (!pending || !pending.selection) {
        state.rangeComposition = null;
        return false;
    }

    const targetLine = Number(lineNumber || element?.dataset?.line || pending.lineNumber || state.currentLine || 1);
    let targetElement = targetLine === pending.lineNumber && element?.getAttribute?.('contenteditable') === 'true'
        ? element
        : viewport.querySelector(`.line-text[data-line="${pending.lineNumber}"]`);

    if (!targetElement || targetElement.getAttribute?.('contenteditable') !== 'true') {
        targetElement = element && element.getAttribute?.('contenteditable') === 'true' ? element : null;
    }

    const finalText = targetElement ? lineTextFromElement(targetElement) : pending.beforeText;
    let insertedText = String(compositionText || '');
    if (!insertedText && finalText !== pending.beforeText) {
        insertedText = changedTextBetween(pending.beforeText, finalText);
    }

    const originalSelection = cloneEditorSelection(pending.selection);
    state.rangeComposition = null;

    // 임시 조합 위치에 들어간 텍스트를 원래 줄 상태로 되돌린 뒤,
    // 원래 다중 줄 선택 영역을 최종 조합 문자열로 한 번에 교체한다.
    state.cache.set(pending.lineNumber, pending.beforeText);
    if (targetElement && targetElement.getAttribute?.('contenteditable') === 'true') {
        targetElement.textContent = pending.beforeText;
        const restoreColumn = Math.max(0, Math.min(pending.caretColumn, pending.beforeText.length));
        setCaret(targetElement, restoreColumn);
    }

    if (insertedText) {
        replaceSelectionWith(originalSelection, insertedText);
    } else {
        state.selection = originalSelection;
        state.selectionAnchor = originalSelection.start;
        syncCustomSelectionClass();
        queueRender(true);
        setTimeout(() => focusLine(originalSelection.start.line, originalSelection.start.column), 0);
    }

    return true;
}

function replaceSelectionForCompositionStart(element, markPendingImeStart = true) {
    const selection = compositionSelectionRange();
    if (!selection || selection.isColumn) {
        return element;
    }

    const nativeCompositionElement = prepareSingleLineSelectionForNativeComposition(selection);
    if (nativeCompositionElement) {
        return nativeCompositionElement;
    }

    // 여러 줄 선택 영역도 한글 조합이 끝날 때까지 삭제를 미루지 않는다.
    // 이전 방식은 IME 임시 조합 위치만 만들고 compositionend/화살표 이동 때
    // 원래 선택 영역을 교체했기 때문에, 마우스로 다른 곳을 클릭하면 선택 영역이
    // 그대로 남거나 다음 caret 이동 시점에야 뒤늦게 삭제되는 문제가 있었다.
    // 여기서 먼저 모델과 현재 렌더된 행을 선택 시작 위치로 접고, 브라우저 IME는
    // 접힌 caret에 조합 문자열을 네이티브로 입력하게 둔다.

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

    return {
        beginColumnComposition,
        changedTextBetween,
        clearPendingImeSelectionCollapse,
        finishColumnComposition,
        finishRangeComposition,
        isLineInColumnComposition,
        isPendingImeSelectionCollapseFor,
        replaceSelectionForCompositionStart,
        updateColumnCompositionPreview
    };
}