import {
    activateTextareaImeBypass,
    beginImeCommit,
    beginImeComposition,
    cancelImeComposition,
    completeImeCommit,
    ImePhase,
    updateImeComposition
} from './editor-ime-state.js';

export function createEditorCompositionHandlers({
    activeColumnSelection,
    activeEditableElement,
    caretRectForOffset,
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

function createRangeCompositionEditCommand(selection) {
    const originalSelection = cloneEditorSelection(selection);
    const { start, end } = originalSelection;
    const prefix = (state.cache.get(start.line) ?? '').slice(0, start.column);
    const suffix = (state.cache.get(end.line) ?? '').slice(end.column);
    const collapsedText = prefix + suffix;

    return {
        type: 'replaceRange',
        selection: originalSelection,
        start,
        end,
        collapsedText,
        caretColumn: Math.max(0, Math.min(start.column, collapsedText.length)),
        removedLineCount: Math.max(0, end.line - start.line)
    };
}

function applyLocalRangeCompositionEdit(command, preferredElement = null) {
    if (!command || command.type !== 'replaceRange') return null;

    const { start, end, collapsedText, caretColumn, removedLineCount } = command;
    state.selection = null;
    state.selectionAnchor = { line: start.line, column: caretColumn };
    state.currentLine = start.line;
    state.currentColumn = caretColumn + 1;
    state.editingLine = start.line;
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();

    state.cache.set(start.line, collapsedText);
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

    const incomingLine = Number(preferredElement?.dataset?.line || 0);
    const incomingElementIsInsideSelection = preferredElement &&
        preferredElement.getAttribute?.('contenteditable') === 'true' &&
        incomingLine >= start.line && incomingLine <= end.line;
    const startRow = viewport.querySelector(`.line-row[data-line="${start.line}"]`);
    const startTextElement = startRow?.querySelector('.line-text') || null;
    let targetElement = incomingElementIsInsideSelection ? preferredElement : startTextElement;

    if (targetElement?.getAttribute?.('contenteditable') === 'true') {
        makeEditablePlainText(targetElement, null, false);
        targetElement.textContent = collapsedText;
    }

    const collapsedElement = syncRenderedRowsAfterCompositionSelectionCollapse(
        start.line,
        end.line,
        collapsedText,
        caretColumn,
        targetElement
    );

    state.lastRangeKey = '';
    if (state.wordWrap) {
        measureRenderedRows(false);
    }
    return collapsedElement || targetElement || startTextElement || preferredElement;
}

function commitRangeCompositionEdit(command, insertedText) {
    if (!command || command.type !== 'replaceRange') return false;
    const { start, end } = command;
    post({
        type: 'rangeEdit',
        startLine: start.line,
        startColumn: start.column + 1,
        endLine: end.line,
        endColumn: end.column + 1,
        text: String(insertedText ?? '')
    });
    post({ type: 'contentChanged' });
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
            if (!state.textareaImeBypassActive) {
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
                        // This row is outside the IME host. Re-highlight it after its line number
                        // shifts instead of flattening its syntax-token spans to plain text.
                        textElement.innerHTML = renderLineContent(newLine, state.cache.get(newLine) || '');
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

    clearCustomSelectionVisuals();

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
    reportCursorAndSelection(targetElement);
    return targetElement;
}

function finishRangeComposition(element, lineNumber, compositionText = '') {
    const pending = state.rangeComposition;
    if (!pending || !pending.command) {
        state.rangeComposition = null;
        return false;
    }

    const targetLine = Number(lineNumber || element?.dataset?.line || pending.command.start.line || state.currentLine || 1);
    let targetElement = targetLine === pending.lineNumber && element?.getAttribute?.('contenteditable') === 'true'
        ? element
        : viewport.querySelector(`.line-text[data-line="${pending.lineNumber}"]`);

    if (!targetElement || targetElement.getAttribute?.('contenteditable') !== 'true') {
        targetElement = element && element.getAttribute?.('contenteditable') === 'true' ? element : null;
    }

    const fallbackCompositionText = String(compositionText || '');
    const finalText = targetElement
        ? lineTextFromElement(targetElement)
        : pending.command.collapsedText.slice(0, pending.command.caretColumn) +
            fallbackCompositionText +
            pending.command.collapsedText.slice(pending.command.caretColumn);
    const insertedText = finalText !== pending.command.collapsedText
        ? changedTextBetween(pending.command.collapsedText, finalText)
        : fallbackCompositionText;

    state.rangeComposition = null;
    state.cache.set(pending.lineNumber, finalText);
    state.cacheVersion++;
    state.selection = null;
    state.selectionAnchor = {
        line: pending.lineNumber,
        column: pending.command.caretColumn + insertedText.length
    };
    state.currentLine = pending.lineNumber;
    state.currentColumn = state.selectionAnchor.column + 1;
    if (!cleanDirtyMarker(pending.lineNumber)) {
        markDirty(pending.lineNumber, 'mod');
    }
    commitRangeCompositionEdit(pending.command, insertedText);
    syncCustomSelectionClass();
    queueRender(true);
    setTimeout(() => focusLine(state.currentLine, Math.max(0, state.currentColumn - 1)), 0);

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

    // DOM은 IME 호스트를 유지하기 위해 즉시 접지만, C# 문서 모델에는 아직
    // 아무 메시지도 보내지 않는다. compositionend에서 rangeEdit 한 건만 확정한다.
    const command = createRangeCompositionEditCommand(selection);
    const targetElement = applyLocalRangeCompositionEdit(command, element);
    state.rangeComposition = {
        command,
        lineNumber: command.start.line
    };

    if (targetElement && targetElement.getAttribute?.('contenteditable') === 'true') {
        targetElement.focus({ preventScroll: true });
        const textNode = targetElement.firstChild;
        const range = document.createRange();
        if (textNode && textNode.nodeType === Node.TEXT_NODE) {
            range.setStart(textNode, Math.max(0, Math.min(command.caretColumn, textNode.textContent.length)));
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
        beginPendingImeSelectionCollapse(targetElement, command.start.line, command.caretColumn);
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
    const previewText = String(insertedText ?? '');

    for (let line = bounds.startLine; line <= bounds.endLine; line++) {
        const baseText = pending.baseLines.get(line) ?? '';
        const nextText = buildColumnCompositionLine(baseText, bounds.startCol, bounds.endCol, previewText);
        state.cache.set(line, nextText);
        pending.lastPreviewLines.set(line, nextText);
        updateVisibleLineTextDuringComposition(line, nextText, element);
        changed = true;
    }

    if (changed) {
        state.cacheVersion++;
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        drawEditableSelectionOverlays();
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

function restoreColumnCompositionBase(pending, preserveElement = null) {
    const bounds = pending ? columnCompositionBounds(pending.selection) : null;
    if (!pending || !bounds) return;

    for (let line = bounds.startLine; line <= bounds.endLine; line++) {
        const baseText = pending.baseLines.get(line) ?? '';
        state.cache.set(line, baseText);
        updateVisibleLineTextDuringComposition(line, baseText, preserveElement);
    }

    state.cacheVersion++;
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
        restoreColumnCompositionBase(pending, element);
        state.columnComposition = null;
        return false;
    }

    const finalText = lineTextFromElement(element);
    const insertedText = changedTextBetween(pending.beforeText, finalText);
    const originalSelection = cloneEditorSelection(pending.selection);
    const changed = insertedText.length > 0 || finalText !== pending.beforeText;

    restoreColumnCompositionBase(pending, element);
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

    let textareaBypassNode = null;
    let bypassSelection = null;
    let bypassPrefix = '';
    let bypassSuffix = '';
    let bypassStartLine = 1;
    let bypassStartColumn = 0;
    let isBypassCompositionActive = false;
    let bypassEditCommand = null;
    let bypassUndoTransactionActive = false;

    function beginBypassUndoTransaction() {
        if (bypassUndoTransactionActive) return;
        post({ type: 'editTransactionStarted' });
        bypassUndoTransactionActive = true;
    }

    function endBypassUndoTransaction() {
        if (!bypassUndoTransactionActive) return;
        post({ type: 'editTransactionEnded' });
        bypassUndoTransactionActive = false;
    }

    function getOrCreateBypassTextarea() {
        if (!textareaBypassNode) {
            textareaBypassNode = document.createElement('textarea');
            textareaBypassNode.id = 'ime-bypass-textarea';
            textareaBypassNode.setAttribute('wrap', 'off');
            textareaBypassNode.autocomplete = 'off';
            textareaBypassNode.autocapitalize = 'off';
            textareaBypassNode.spellcheck = false;
            textareaBypassNode.style.cssText = `
                position: absolute;
                width: 1px;
                min-width: 1px;
                height: 1px;
                opacity: 0;
                pointer-events: none;
                border: none;
                padding: 0;
                margin: 0;
                outline: none;
                resize: none;
                overflow: hidden;
                background: transparent;
                color: transparent;
                caret-color: transparent;
                font-family: var(--font-family);
                font-size: var(--font-size);
                line-height: var(--line-height);
                white-space: pre;
                overflow-wrap: normal;
                word-break: normal;
                z-index: -1000;
            `;
            const host = document.getElementById('editor-host') || document.body;
            host.appendChild(textareaBypassNode);

            textareaBypassNode.addEventListener('compositionstart', onBypassCompositionStart);
            textareaBypassNode.addEventListener('compositionupdate', onBypassCompositionUpdate);
            textareaBypassNode.addEventListener('compositionend', onBypassCompositionEnd);
            textareaBypassNode.addEventListener('input', onBypassInput);
            textareaBypassNode.addEventListener('keydown', onBypassKeyDown);
            textareaBypassNode.addEventListener('blur', onBypassBlur);
        }
        return textareaBypassNode;
    }

    function setBypassCursor(column) {
        const numericColumn = Number(column);
        const safeColumn = Math.max(0, Number.isFinite(numericColumn) ? numericColumn : 0);
        state.bypassCursorLine = bypassStartLine;
        state.bypassCursorColumn = safeColumn;
        state.currentLine = bypassStartLine;
        state.currentColumn = safeColumn + 1;
        state.editingLine = bypassStartLine;
    }

    function clearBypassCursor() {
        state.bypassCursorLine = null;
        state.bypassCursorColumn = null;
    }

    function syncBypassTextareaPosition(element, anchorColumn = bypassStartColumn) {
        if (!textareaBypassNode || !element || element.getAttribute?.('contenteditable') !== 'true') return;

        const anchorRect = caretRectForOffset(element, anchorColumn);
        const elementRect = element.getBoundingClientRect();
        const host = document.getElementById('editor-host') || document.body;
        const hostRect = host.getBoundingClientRect();
        const scrollRect = scrollContainer.getBoundingClientRect();
        const styles = window.getComputedStyle(element);
        const parsedLineHeight = Number.parseFloat(styles.lineHeight);
        const height = Math.max(1, anchorRect?.height || (Number.isFinite(parsedLineHeight) ? parsedLineHeight : state.lineHeight));
        const left = anchorRect?.left ?? elementRect.left;
        const top = anchorRect?.top ?? elementRect.top;
        const rightLimit = Math.max(hostRect.right, scrollRect.right);
        const width = Math.max(32, rightLimit - left);

        textareaBypassNode.style.left = `${left - hostRect.left}px`;
        textareaBypassNode.style.top = `${top - hostRect.top}px`;
        textareaBypassNode.style.width = `${Math.ceil(width)}px`;
        textareaBypassNode.style.height = `${Math.ceil(height)}px`;
        textareaBypassNode.style.fontFamily = styles.fontFamily;
        textareaBypassNode.style.fontSize = styles.fontSize;
        textareaBypassNode.style.fontWeight = styles.fontWeight;
        textareaBypassNode.style.fontStyle = styles.fontStyle;
        textareaBypassNode.style.letterSpacing = styles.letterSpacing;
        textareaBypassNode.style.lineHeight = Number.isFinite(parsedLineHeight)
            ? `${parsedLineHeight}px`
            : `${height}px`;
        textareaBypassNode.style.tabSize = styles.tabSize || String(state.tabSize || 4);
    }

    function collapseEditorSelectionForBypass() {
        if (!bypassSelection) return;

        beginBypassUndoTransaction();
        bypassEditCommand = createRangeCompositionEditCommand(bypassSelection);
        const row = viewport.querySelector(`.line-row[data-line="${bypassEditCommand.start.line}"]`);
        const element = row ? row.querySelector('.line-text') : null;
        applyLocalRangeCompositionEdit(bypassEditCommand, element);
        bypassSelection = null;
    }

    function onBypassCompositionStart(e) {
        if (!beginImeComposition(state, ImePhase.TextareaBypassComposition, bypassStartLine)) return;
        state.editingLine = bypassStartLine;
        isBypassCompositionActive = true;
        collapseEditorSelectionForBypass();
    }

    function onBypassCompositionUpdate(e) {
        if (!updateImeComposition(state)) return;
        const val = e.data || '';
        updateEditorText(val, true);
    }

    function onBypassInput(e) {
        if (!isBypassCompositionActive && !bypassSelection) return;
        if (bypassSelection) {
            collapseEditorSelectionForBypass();
        }
        const val = textareaBypassNode.value;
        updateEditorText(val, isBypassCompositionActive);
    }

    function onBypassCompositionEnd(e) {
        beginImeCommit(state);
        isBypassCompositionActive = false;
        const val = e.data !== undefined ? (textareaBypassNode.value || e.data) : textareaBypassNode.value;
        updateEditorText(val, false);
        endBypassUndoTransaction();
        completeImeCommit(state, true);
    }

    function onBypassKeyDown(e) {
        if (!isBypassCompositionActive) {
            const val = textareaBypassNode.value;
            
            if (bypassSelection) {
                bypassSelection = null;
                cancelImeComposition(state);
                completeImeCommit(state);
                state.bypassStartLine = null;
                clearBypassCursor();
                const activeLine = viewport.querySelector(`.line-text[data-line="${state.currentLine}"]`);
                if (activeLine) {
                    activeLine.focus({ preventScroll: true });
                }
            } else {
                updateEditorText(val, false);
                endBypassUndoTransaction();
                const newCaretCol = bypassStartColumn + val.length;
                cancelImeComposition(state);
                completeImeCommit(state);
                state.bypassStartLine = null;
                clearBypassCursor();
                focusLine(bypassStartLine, newCaretCol);
            }
            
            const active = document.activeElement;
            if (active && active !== textareaBypassNode) {
                const initOpts = {
                    key: e.key,
                    code: e.code,
                    keyCode: e.keyCode,
                    which: e.which,
                    ctrlKey: e.ctrlKey,
                    shiftKey: e.shiftKey,
                    altKey: e.altKey,
                    metaKey: e.metaKey,
                    bubbles: true,
                    cancelable: true
                };
                active.dispatchEvent(new KeyboardEvent('keydown', initOpts));
            }
            e.preventDefault();
            e.stopPropagation();
        }
    }
 
    function onBypassBlur(e) {
        if (state.textareaImeBypassActive) {
            const val = textareaBypassNode.value;
            if (bypassSelection) {
                bypassSelection = null;
            } else {
                updateEditorText(val, false);
                endBypassUndoTransaction();
            }
            cancelImeComposition(state);
            completeImeCommit(state);
            state.bypassStartLine = null;
            clearBypassCursor();
            drawEditableSelectionOverlays();
        }
    }

    function cancelImeBypassTextarea() {
        if (isBypassCompositionActive || state.isComposing) {
            return false;
        }

        const wasActive = !!(state.textareaImeBypassActive || bypassSelection || document.activeElement === textareaBypassNode);
        if (textareaBypassNode) {
            textareaBypassNode.value = '';
        }

        bypassSelection = null;
        bypassEditCommand = null;
        bypassPrefix = '';
        bypassSuffix = '';
        cancelImeComposition(state);
        completeImeCommit(state);
        state.bypassStartLine = null;
        clearBypassCursor();
        endBypassUndoTransaction();
        return wasActive;
    }

    function updateEditorText(val, isComposing) {
        const nextText = bypassPrefix + val + bypassSuffix;
        const previousText = state.cache.get(bypassStartLine) ?? '';
        const cursorColumn = bypassStartColumn + String(val ?? '').length;
        setBypassCursor(cursorColumn);
        state.cache.set(bypassStartLine, nextText);
        state.cacheVersion++;

        if (!isComposing) {
            if (bypassEditCommand) {
                commitRangeCompositionEdit(bypassEditCommand, val);
                bypassEditCommand = null;
            } else if (previousText !== nextText) {
                post({ type: 'lineChanged', lineNumber: bypassStartLine, text: nextText });
                post({ type: 'contentChanged' });
            }
        }
        
        const element = viewport.querySelector(`.line-text[data-line="${bypassStartLine}"]`);
        if (element) {
            element.innerHTML = renderLineContent(bypassStartLine, nextText);
            syncBypassTextareaPosition(element, bypassStartColumn);
            drawEditableSelectionOverlays();
        }
        
        if (state.wordWrap) {
            measureRenderedRows(false);
        }

        reportCursorAndSelection();
    }

    function focusImeBypassTextarea() {
        const selection = normalizeSelection();
        if (selection && selection.start.line !== selection.end.line && !selection.isColumn && !state.isSelecting) {
            const textarea = getOrCreateBypassTextarea();
            const shouldRefreshBypass = state.isSplitView || document.activeElement !== textarea;
            if (shouldRefreshBypass) {
                bypassSelection = cloneEditorSelection(selection);
                bypassEditCommand = null;
                bypassStartLine = selection.start.line;
                bypassStartColumn = selection.start.column;
                
                const prefix = (state.cache.get(bypassStartLine) ?? '').slice(0, bypassStartColumn);
                const suffix = (state.cache.get(selection.end.line) ?? '').slice(selection.end.column);
                bypassPrefix = prefix;
                bypassSuffix = suffix;
                
                textarea.value = '';
                if (!activateTextareaImeBypass(state, bypassStartLine)) return false;
                state.bypassStartLine = bypassStartLine;
                setBypassCursor(bypassStartColumn);
                
                const row = viewport.querySelector(`.line-row[data-line="${bypassStartLine}"]`);
                if (row) {
                    const textElement = row.querySelector('.line-text');
                    if (textElement) {
                        syncBypassTextareaPosition(textElement, bypassStartColumn);
                    }
                }
                drawEditableSelectionOverlays();
                
                if (document.activeElement !== textarea) {
                    textarea.focus();
                }
                return true;
            }
        }

        return false;
    }

    return {
        beginColumnComposition,
        cancelImeBypassTextarea,
        focusImeBypassTextarea,
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
