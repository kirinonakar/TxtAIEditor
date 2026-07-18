import {
    MAX_RENDER_CHARS,
    activeEditableElement,
    captureScrollAnchor,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    comparePositions,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasTextAt,
    isPlainTextKey,
    isStandaloneDelimiter,
    invalidateMeasuredLineHeightsAround,
    lineCommentSyntax,
    lineTop,
    markDirty,
    measureRenderedRows,
    orderedRange,
    post,
    preserveScrollTop,
    queueRender,
    readClipboardText,
    reportCursorAndSelection,
    requestLines,
    restoreScrollAnchor,
    selectedLineRange,
    selectedText,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    visualScrollDeltaToScrollTopDelta,
    writeClipboardText
} from './editor-core.js';
import { scrollContainer, viewport } from './editor-dom.js';
import {
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
} from './editor-caret.js';
import { renderLineContent } from './editor-highlighter.js';
import {
    activeColumnSelection,
    cloneEditorSelection,
    drawEditableSelectionOverlays,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    cancelPostEditFocusFollowUps,
    clampScrollTop,
    queueFocusPreservingScroll,
    queuePostEditCaretRestore,
    queuePostEditFocus,
    restoreScrollTop
} from './editor-edit-focus.js';
import { createCaretNavigationCommands } from './editor-caret-navigation-commands.js';
import { createClipboardCommandHandlers } from './editor-clipboard-commands.js';
import { createEditorCompositionHandlers } from './editor-composition.js';
import {
    beginImeCommit,
    completeImeCommit,
    resetImeState
} from './editor-ime-state.js';
import { createHostStreamInsertCommands } from './editor-host-stream-insert.js';
import { createModelRepeatInputHandlers } from './editor-repeat-input.js';
import {
    copyCsvSelectionToClipboard,
    cutCsvSelectionToClipboard
} from './editor-csv-table.js';
import { createMarkdownCommandHandlers } from './editor-markdown-commands.js';
import { createTextCommandActions } from './editor-text-command-actions.js';

let splitScrollRestoreToken = 0;
const LONG_LINE_EDIT_THRESHOLD = 500;

function postLineUpdate(lineNumber, previousText, nextText, isComposing = false) {
    const before = String(previousText ?? '');
    const after = String(nextText ?? '');
    if (Math.max(before.length, after.length) < LONG_LINE_EDIT_THRESHOLD) {
        post({ type: 'lineChanged', lineNumber, text: after, isComposing });
        return;
    }

    let prefixLength = 0;
    const prefixLimit = Math.min(before.length, after.length);
    while (prefixLength < prefixLimit && before.charCodeAt(prefixLength) === after.charCodeAt(prefixLength)) {
        prefixLength++;
    }

    let beforeEnd = before.length;
    let afterEnd = after.length;
    while (beforeEnd > prefixLength &&
        afterEnd > prefixLength &&
        before.charCodeAt(beforeEnd - 1) === after.charCodeAt(afterEnd - 1)) {
        beforeEnd--;
        afterEnd--;
    }

    post({
        type: 'lineEdit',
        lineNumber,
        startColumn: prefixLength + 1,
        endColumn: beforeEnd + 1,
        text: after.slice(prefixLength, afterEnd),
        isComposing
    });
}

function cancelPendingRepeatFollowUps(key) {
    if (!key) return;
    cancelPostEditFocusFollowUps();
    if (key === 'Enter') {
        splitScrollRestoreToken++;
    }
    if (key === 'Backspace' || key === 'Delete') {
        state.pendingLineActions = [];
    }
}

function beginEditTransaction() {
    post({ type: 'editTransactionStarted' });
}

function endEditTransaction() {
    post({ type: 'editTransactionEnded' });
}

function commitLine(element) {
    const lineNumber = Number(element.dataset.line || 1);
    const isComposing = state.isComposing &&
        (!state.compositionLine || state.compositionLine === lineNumber);

    const previousText = state.cache.get(lineNumber) ?? '';
    const text = lineTextFromElement(element);
    const textChanged = previousText !== text;

    const isInlineLivePreviewActiveLine = state.inlineLivePreviewEnabled &&
        state.inlineLivePreviewSourceLine === lineNumber;

    if (isComposing && state.rangeComposition) {
        // 다중 줄 선택 IME 조합 중에는 모델/DOM 재렌더를 건드리지 않는다.
        // 최종 조합 문자열은 compositionend에서 rangeEdit 한 건으로 반영한다.
    } else if (isComposing && state.columnComposition) {
        updateColumnCompositionPreview(element);
    } else if (isComposing) {
        // 네이티브 IME가 소유한 DOM을 로컬 미리보기로만 유지한다.
        // cache 및 C# 문서 모델 delta는 compositionend에서 한 번만 확정한다.
    } else if (textChanged) {
        state.cache.set(lineNumber, text);
        if (!isInlineLivePreviewActiveLine) {
            state.cacheVersion++;
        }
    }

    const caretOffset = getCaretOffset(element);
    const inputSnapshot = { lineNumber, text, caretOffset };
    state.currentLine = lineNumber;
    state.currentColumn = caretOffset + 1;

    if (isComposing) {
        reportCursorAndSelection(element, caretOffset);
        if (state.wordWrap && !state.rangeComposition) {
            measureRenderedRows(false);
        }
        return inputSnapshot;
    }

    // Cursor-only navigation (notably PageUp/PageDown) commits the active row
    // before moving. Avoid rewriting an unchanged cache entry: cache.set()
    // invalidates all later syntax-context entries, which becomes an O(n)
    // main-thread stall after navigating through a large document.
    if (!textChanged) {
        reportCursorAndSelection(element, caretOffset);
        return inputSnapshot;
    }

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    postLineUpdate(lineNumber, previousText, text);
    post({ type: 'contentChanged' });
    reportCursorAndSelection(element, caretOffset);

    if (state.wordWrap) {
        measureRenderedRows(false);
    }
    return inputSnapshot;
}

function commitLineForSave(element) {
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        return false;
    }

    const lineNumber = Number(element.dataset.line || state.compositionLine || state.currentLine || 1);

    if (state.rangeComposition && finishRangeComposition(element, lineNumber)) {
        beginImeCommit(state);
        completeImeCommit(state);
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

    if (state.columnComposition && finishColumnComposition(element, lineNumber)) {
        beginImeCommit(state);
        completeImeCommit(state);
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

    const text = lineTextFromElement(element);
    const previousText = state.cache.get(lineNumber) ?? '';
    resetImeState(state);
    state.currentLine = lineNumber;
    state.currentColumn = Math.min(getCaretOffset(element) + 1, text.length + 1);

    if (text !== previousText) {
        state.cache.set(lineNumber, text);
        state.cacheVersion++;

        if (!cleanDirtyMarker(lineNumber)) {
            markDirty(lineNumber, 'mod');
        }

        post({ type: 'lineChanged', lineNumber, text });
        post({ type: 'contentChanged' });
    }
    reportCursorAndSelection(element);

    if (state.wordWrap) {
        measureRenderedRows(false);
    }

    return true;
}

function flushPendingEditForSave(requestId) {
    const focusedElement = document.activeElement?.closest?.('.line-text');
    const focusedLine = focusedElement?.getAttribute('contenteditable') === 'true'
        ? Number(focusedElement.dataset.line || 0)
        : 0;
    const requestedLine = Number(state.compositionLine || focusedLine || state.currentLine || state.editingLine || 1);
    let element = (focusedElement && focusedElement.getAttribute('contenteditable') === 'true')
        ? focusedElement
        : viewport.querySelector(`.line-text[data-line="${requestedLine}"]`) || activeEditableElement();
    const wasFocused = !!(element && document.activeElement === element);
    const domSelection = window.getSelection();
    const hasDomCaret = !!((!hasCustomSelection() || state.isComposing) && element && domSelection?.rangeCount &&
        element.contains(domSelection.getRangeAt(0).startContainer));
    const restoreLine = Math.max(1, Number(focusedLine || state.currentLine || requestedLine));
    const restoreColumn = hasDomCaret
        ? getCaretOffset(element)
        : Math.max(0, Number(state.currentColumn || 1) - 1);
    let finished = false;
    let compositionFallbackTimer = 0;

    const finish = () => {
        if (finished) return;
        finished = true;
        if (compositionFallbackTimer) {
            clearTimeout(compositionFallbackTimer);
            compositionFallbackTimer = 0;
        }

        const lineNumber = Number(state.compositionLine || requestedLine || state.currentLine || 1);
        element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) ||
            element ||
            activeEditableElement();

        if (element && element.getAttribute('contenteditable') === 'true') {
            commitLineForSave(element);
        } else {
            resetImeState(state);
        }

        const safeRestoreLine = Math.min(Math.max(1, restoreLine), state.lineCount);
        const restoreElement = viewport.querySelector(`.line-text[data-line="${safeRestoreLine}"]`);
        const restoreText = restoreElement?.getAttribute('contenteditable') === 'true'
            ? lineTextFromElement(restoreElement)
            : (state.cache.get(safeRestoreLine) ?? '');
        const safeRestoreColumn = Math.min(restoreColumn, restoreText.length);
        state.currentLine = safeRestoreLine;
        state.currentColumn = safeRestoreColumn + 1;

        if (wasFocused) {
            setTimeout(() => {
                const currentElement = viewport.querySelector(`.line-text[data-line="${safeRestoreLine}"]`);
                if (currentElement?.getAttribute('contenteditable') === 'true') {
                    setCaret(currentElement, safeRestoreColumn);
                } else {
                    reportCursorAndSelection();
                }
            }, 0);
        } else {
            reportCursorAndSelection();
        }

        post({ type: 'editorFlushedForSave', requestId: Number(requestId || 0) });
    };

    if (state.isComposing && element && element.getAttribute('contenteditable') === 'true') {
        const finishAfterCompositionEnd = () => queueMicrotask(finish);
        element.addEventListener('compositionend', finishAfterCompositionEnd, { once: true });
        try {
            element.blur();
        } catch { }

        if (!state.isComposing) {
            queueMicrotask(finish);
        } else {
            // WebView가 blur에 compositionend를 보내지 않는 비정상 상황에서만
            // 저장 요청이 영구 대기하지 않도록 하는 안전 fallback이다.
            compositionFallbackTimer = setTimeout(finish, 250);
        }
        return;
    }

    queueMicrotask(finish);
}


function visualLineHeightFromCaret(element, caretRect) {
    if (element) {
        const parsedLineHeight = Number.parseFloat(window.getComputedStyle(element).lineHeight);
        if (Number.isFinite(parsedLineHeight) && parsedLineHeight > 0) {
            return parsedLineHeight;
        }
    }
    if (caretRect && Number.isFinite(caretRect.height) && caretRect.height > 0) {
        return caretRect.height;
    }
    return Math.max(1, Number(state.lineHeight || 1));
}

function captureSplitScrollAnchor(element, caretOffset) {
    if (!element || !element.isConnected || scrollContainer.clientHeight <= 0) return null;

    const containerRect = scrollContainer.getBoundingClientRect();
    const caretRect = caretRectForOffset(element, caretOffset);
    if (!caretRect) return null;

    const rowHeight = visualLineHeightFromCaret(element, caretRect);
    const caretHeight = Math.max(1, Number(caretRect.height || rowHeight));
    const caretViewportTop = caretRect.top - containerRect.top;
    const caretViewportBottom = caretRect.bottom - containerRect.top;
    const bottomGuard = 3 * rowHeight;
    const guardBottom = Math.max(caretHeight, scrollContainer.clientHeight - bottomGuard);

    // Enter must behave like ArrowDown: while there is room below, the caret
    // moves down by one visual row without scrolling.  Only when that next row
    // would cross the three-line bottom guard do we keep the caret pinned there.
    // The previous implementation called focusLine() before the inserted row was
    // measured, so the virtual lineTop estimate could over-scroll and make the
    // caret jump 6-7 rows upward; repeated Enter then accumulated that error.
    const naturalNextBottom = caretViewportBottom + rowHeight;
    const desiredViewportBottom = Math.min(naturalNextBottom, guardBottom);

    return {
        scrollTop: scrollContainer.scrollTop,
        caretViewportTop,
        caretViewportBottom,
        desiredViewportBottom,
        desiredViewportTop: desiredViewportBottom - caretHeight,
        bottomGuard,
        rowHeight,
        caretHeight
    };
}

function alignSplitCaretAfterRender(targetLineNumber, targetColumn, anchor, token, attempt = 0) {
    if (!anchor || !Number.isFinite(anchor.desiredViewportBottom)) return;
    if (token !== splitScrollRestoreToken) return;

    const run = () => {
        if (token !== splitScrollRestoreToken) return;

        const element = viewport.querySelector(`.line-text[data-line="${targetLineNumber}"]`);
        if (!element || element.getAttribute('contenteditable') !== 'true') {
            if (attempt < 8) {
                alignSplitCaretAfterRender(targetLineNumber, targetColumn, anchor, token, attempt + 1);
            } else {
                focusLine(targetLineNumber, targetColumn, 3 * anchor.rowHeight);
            }
            return;
        }

        setCaret(element, targetColumn);
        const caretRect = caretRectForOffset(element, targetColumn);
        if (!caretRect) return;

        const containerRect = scrollContainer.getBoundingClientRect();
        const currentViewportBottom = caretRect.bottom - containerRect.top;
        const delta = currentViewportBottom - anchor.desiredViewportBottom;
        if (Math.abs(delta) > 0.5) {
            scrollContainer.scrollTop = clampScrollTop(
                scrollContainer.scrollTop + visualScrollDeltaToScrollTopDelta(delta));
        }

        // A scrollTop write can cause one more virtual render.  Re-check once or
        // twice so measured row heights cannot accumulate a vertical drift while
        // Enter is held down.
        if (attempt < 2) {
            alignSplitCaretAfterRender(targetLineNumber, targetColumn, anchor, token, attempt + 1);
        }
    };

    if (attempt === 0) {
        requestAnimationFrame(run);
    } else {
        setTimeout(run, 0);
    }
}

function splitCurrentLine(element, options = {}) {
    // A held Enter can start the next split before the previous render-pass
    // restoration has fired.  Invalidate older restorations so they cannot move
    // focus/scroll back to an earlier line and make the caret appear to climb.
    const splitRestoreToken = ++splitScrollRestoreToken;
    let preferStateCaret = options?.preferStateCaret === true;

    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            if (sel.isColumn) {
                // Column input leaves a zero-width selection across all edited
                // rows. A newline cannot stay rectangular, so collapse it to the
                // active endpoint and split that line with the normal Enter path.
                // Passing "\n" to replaceColumnSelectionWith would otherwise put
                // embedded newlines in cached rows (or delete a two-row selection).
                const caretLine = Math.min(Math.max(1, Number(state.currentLine || sel.end.line)), state.lineCount);
                const caretText = state.cache.get(caretLine) || '';
                const caretColumn = Math.max(0, Math.min(Number(state.currentColumn || 1) - 1, caretText.length));
                state.selection = null;
                state.selectionAnchor = { line: caretLine, column: caretColumn };
                state.currentLine = caretLine;
                state.currentColumn = caretColumn + 1;
                syncCustomSelectionClass();
                clearCustomSelectionVisuals();
                preferStateCaret = true;
            } else {
                replaceSelectionWith(sel, '\n');
                return;
            }
        }
    }

    const elementLineNumber = Number(element?.dataset?.line || 0);
    const stateLineNumber = Math.min(Math.max(1, Number(state.currentLine || elementLineNumber || 1)), state.lineCount);
    const useElementCaret = !preferStateCaret &&
        element &&
        element.getAttribute?.('contenteditable') === 'true' &&
        elementLineNumber > 0;

    const lineNumber = useElementCaret ? elementLineNumber : stateLineNumber;
    const text = useElementCaret
        ? lineTextFromElement(element)
        : (state.cache.get(lineNumber) ?? '');
    const caret = useElementCaret
        ? getCaretOffset(element)
        : Math.max(0, Math.min(Number(state.currentColumn || 1) - 1, text.length));

    if (useElementCaret) {
        // Keep the model in sync before splitting. This covers the last native
        // composition/input that may still only exist in the contenteditable DOM.
        state.cache.set(lineNumber, text);
    }

    const splitScrollAnchor = useElementCaret
        ? captureSplitScrollAnchor(element, caret)
        : null;
    const virtualScrollAnchor = captureScrollAnchor();

    const before = text.slice(0, caret);
    const after = text.slice(caret);
    const indent = (text.match(/^[ \t]*/) || [''])[0];
    const afterStartsWithSpace = /^[ \t]/.test(after);
    // If the caret is before/inside the line's existing leading spaces, `after`
    // already contains that indentation.  Prepending `indent` again makes each
    // repeated Enter before a space grow the line by one more space.
    const shouldReuseExistingIndent = caret < indent.length;
    const insertedIndent = shouldReuseExistingIndent ? '' : indent;
    // When Enter is pressed immediately before a space in an already-indented
    // line, the copied line indent and the boundary space both become leading
    // whitespace on the new line.  Consume just that boundary whitespace so
    // repeated Enter keeps the same indentation instead of adding one space
    // on every split.  Non-indented plain text keeps its original space.
    const shouldConsumeBoundarySpace = !shouldReuseExistingIndent && indent.length > 0 && afterStartsWithSpace;
    const normalizedAfter = shouldConsumeBoundarySpace ? after.slice(1) : after;
    const indentedAfter = insertedIndent + normalizedAfter;
    const nextCaretColumn = insertedIndent.length;
    const nextLineNumber = lineNumber + 1;

    state.cache.set(lineNumber, before);
    shiftCachedLines(nextLineNumber, 1);
    state.cache.set(nextLineNumber, indentedAfter);
    state.lineCount++;
    invalidateMeasuredLineHeightsAround(lineNumber);
    invalidateMeasuredLineHeightsAround(nextLineNumber);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    if (state.showDirtyLines) {
        state.dirtyLines.set(nextLineNumber, 'add');
    }
    state.selection = null;
    clearCustomSelectionVisuals();
    syncCustomSelectionClass();
    setupVirtualHeight();
    restoreScrollAnchor(virtualScrollAnchor);
    post({ type: 'splitLine', lineNumber, before, after: indentedAfter });
    post({ type: 'contentChanged' });
    markLineBoundaryTransition(nextLineNumber, nextCaretColumn);
    state.selectionAnchor = { line: nextLineNumber, column: nextCaretColumn };
    state.currentLine = nextLineNumber;
    state.currentColumn = nextCaretColumn + 1;
    state.editingLine = nextLineNumber;

    if (useElementCaret && element.isConnected) {
        element.innerHTML = renderLineContent(lineNumber, before);
    }

    const immediateTarget = viewport.querySelector(`.line-text[data-line="${nextLineNumber}"]`);
    if (immediateTarget && immediateTarget.getAttribute('contenteditable') === 'true') {
        immediateTarget.innerHTML = renderLineContent(nextLineNumber, indentedAfter);
        setCaret(immediateTarget, nextCaretColumn);
    }

    queueRender(true);
    if (splitScrollAnchor) {
        alignSplitCaretAfterRender(nextLineNumber, nextCaretColumn, splitScrollAnchor, splitRestoreToken);
    } else {
        focusLine(nextLineNumber, nextCaretColumn, 3 * state.lineHeight);
    }
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

function insertTextAtCaret(text, options = {}) {
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            replaceSelectionWith(sel, text || '');
            return;
        }
    }
    const preferStateCaret = options?.preferStateCaret === true;
    let element = preferStateCaret ? null : document.activeElement?.closest?.('.line-text');
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        element = preferStateCaret ? null : activeEditableElement();
        if (element) {
            setCaret(element, Math.max(0, state.currentColumn - 1));
        }
    }
    const normalized = String(text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        const targetLine = state.currentLine || 1;
        const currentText = state.cache.get(targetLine) ?? '';
        const caret = Math.max(0, Math.min(state.currentColumn - 1, currentText.length));

        if (normalized.includes('\n')) {
            const before = currentText.slice(0, caret);
            const after = currentText.slice(caret);
            const parts = normalized.split('\n');
            const insertedCount = parts.length - 1;
            const firstLine = before + parts[0];
            const lastLineNumber = targetLine + insertedCount;

            state.cache.set(targetLine, firstLine);
            shiftCachedLines(targetLine + 1, insertedCount);
            if (!cleanDirtyMarker(targetLine)) {
                markDirty(targetLine, 'mod');
            }
            beginEditTransaction();
            try {
                post({ type: 'lineChanged', lineNumber: targetLine, text: firstLine });
                for (let i = 1; i < parts.length; i++) {
                    const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
                    const nextLineNumber = targetLine + i;
                    state.cache.set(nextLineNumber, nextText);
                    if (state.showDirtyLines) {
                        state.dirtyLines.set(nextLineNumber, 'add');
                    }
                    post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
                }
                post({ type: 'contentChanged' });
            } finally {
                endEditTransaction();
            }
            state.lineCount += insertedCount;
            state.currentLine = lastLineNumber;
            state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
            state.selection = null;
            state.selectionAnchor = {
                line: lastLineNumber,
                column: state.currentColumn - 1
            };
            syncCustomSelectionClass();
            setupVirtualHeight();
            queueRender(true);
            queuePostEditCaretRestore(lastLineNumber, state.currentColumn - 1);
        } else {
            const nextText = currentText.slice(0, caret) + normalized + currentText.slice(caret);
            state.cache.set(targetLine, nextText);
            state.cacheVersion++;
            invalidateMeasuredLineHeightsAround(targetLine);
            state.currentColumn = caret + normalized.length + 1;
            if (!cleanDirtyMarker(targetLine)) {
                markDirty(targetLine, 'mod');
            }
            post({ type: 'lineChanged', lineNumber: targetLine, text: nextText });
            post({ type: 'contentChanged' });
            if (state.wordWrap) {
                measureRenderedRows(false);
            }
            focusLine(targetLine, state.currentColumn - 1);
        }
        return;
    }
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
        beginEditTransaction();
        try {
            post({ type: 'lineChanged', lineNumber, text: firstLine });
            for (let i = 1; i < parts.length; i++) {
                const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
                const nextLineNumber = lineNumber + i;
                state.cache.set(nextLineNumber, nextText);
                if (state.showDirtyLines) {
                    state.dirtyLines.set(nextLineNumber, 'add');
                }
                post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
            }

            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }
        state.lineCount += insertedCount;
        state.currentLine = lastLineNumber;
        state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
        state.selection = null;
        state.selectionAnchor = {
            line: lastLineNumber,
            column: state.currentColumn - 1
        };
        setupVirtualHeight();
        queueRender(true);
        queuePostEditCaretRestore(lastLineNumber, parts[parts.length - 1]?.length || 0);
        return;
    }

    insertPlainTextByModel(element, normalized);
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

function updateSingleLine(element, text, caretColumn) {
    const lineNumber = Number(element.dataset.line || 1);
    const previousText = state.cache.get(lineNumber) ?? '';
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

    if (state.isComposing ||
        (state.compositionLine && state.compositionLine === lineNumber) ||
        nextText.length > MAX_RENDER_CHARS) {
        element.textContent = nextText;
    } else {
        element.innerHTML = renderLineContent(lineNumber, nextText);
    }

    setCaret(element, nextColumn);

    postLineUpdate(lineNumber, previousText, nextText);
    post({ type: 'contentChanged' });

    if (state.wordWrap) {
        measureRenderedRows(false);
    }
}

function applyMergeLineForward(lineNumber, text, nextText) {
    const savedScrollTop = scrollContainer.scrollTop;
    preserveScrollTop(savedScrollTop);

    if (state.cache.get(lineNumber) !== text) {
        post({ type: 'lineChanged', lineNumber, text });
    }

    shiftCachedLines(lineNumber + 1, -1);
    state.cache.set(lineNumber, text + nextText);
    invalidateMeasuredLineHeightsAround(lineNumber);
    state.lineCount = Math.max(1, state.lineCount - 1);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    if (state.showDirtyLines) {
        state.dirtyLines.delete(lineNumber + 1);
    }
    setupVirtualHeight();
    restoreScrollTop(savedScrollTop);
    post({ type: 'mergeLineWithPrevious', lineNumber: lineNumber + 1 });
    post({ type: 'contentChanged' });
    state.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber, text.length);
    queueRender(true);
    queueFocusPreservingScroll(lineNumber, text.length, savedScrollTop);
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
    if (state.showDirtyLines) {
        state.dirtyLines.delete(lineNumber);
    }
    setupVirtualHeight();
    post({ type: 'mergeLineWithPrevious', lineNumber });
    post({ type: 'contentChanged' });
    state.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber - 1, previous.length);
    queueRender(true);
    queuePostEditFocus(() => focusLine(lineNumber - 1, previous.length, 3 * state.lineHeight));
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

    beginEditTransaction();
    try {
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

        post({ type: 'contentChanged' });
    } finally {
        endEditTransaction();
    }
    state.cacheVersion++;

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
        const targetLine = endLine;
        const targetColumn = nextCol;
        setTimeout(() => {
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection(null);
        }, 0);
    }
}

function replaceSelectionWith(selection, text, editSelection = null) {
    if (selection.isColumn) {
        replaceColumnSelectionWith(selection, text);
        return;
    }
    let start = selection.start;
    let end = selection.end;
    const replacementText = String(text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');

    // 여러 줄 또는 한 줄 전체를 선택하고 삭제할 때 줄바꿈도 함께 삭제되도록 처리
    const endLineLength = (state.cache.get(end.line) || '').length;
    const isFullLineSelection = start.column === 0 && end.column >= endLineLength;

    if (isFullLineSelection && replacementText === '' && state.lineCount > 1) {
        if (end.line < state.lineCount) {
            end = { line: end.line + 1, column: 0 };
        } else if (start.line > 1) {
            const prevLineLength = (state.cache.get(start.line - 1) || '').length;
            start = { line: start.line - 1, column: prevLineLength };
        }
    }

    const prefix = (state.cache.get(start.line) || '').slice(0, start.column);
    const suffix = (state.cache.get(end.line) || '').slice(end.column);
    const linesToRemove = end.line - start.line;
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
    state.cacheVersion++;

    if (!cleanDirtyMarker(start.line)) {
        markDirty(start.line, 'mod');
    }
    for (let i = 1; i < newLines.length; i++) {
        if (state.showDirtyLines) {
            state.dirtyLines.set(start.line + i, 'add');
        }
    }

    const savedScrollTop = scrollContainer.scrollTop;
    const editLineTop = lineTop(start.line);
    const shouldPreserveScrollTop = netLines < 0 && editLineTop >= savedScrollTop;
    if (shouldPreserveScrollTop) {
        preserveScrollTop(savedScrollTop);
    }

    state.lineCount = Math.max(1, state.lineCount + netLines);
    setupVirtualHeight();

    // 삭제된 줄이 뷰포트 위쪽에 있을 때 스크롤 위치를 유지합니다.
    if (netLines < 0 && editLineTop < savedScrollTop) {
        const scrollAdjust = -netLines * state.lineHeight;
        scrollContainer.scrollTop = Math.max(0, savedScrollTop - scrollAdjust);
    } else if (shouldPreserveScrollTop) {
        restoreScrollTop(savedScrollTop);
    }
    const isStructuralRangeEdit = start.line !== end.line || newLines.length > 1;
    if (isStructuralRangeEdit) {
        // Send one range edit instead of one WebView message per removed/inserted line.
        // Large multi-line selections otherwise flood the UI thread and repeatedly
        // shift the backing line array for every deleted line.
        post({
            type: 'rangeEdit',
            startLine: start.line,
            startColumn: start.column + 1,
            endLine: end.line,
            endColumn: end.column + 1,
            text: replacementText
        });
        post({ type: 'contentChanged' });
    } else {
        beginEditTransaction();
        try {
            post({ type: 'lineChanged', lineNumber: start.line, text: newLines[0] });
            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }
    }
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
        state.selectionAnchor = { line: endLine, column: endColumn };
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
    const targetLine = state.currentLine;
    const targetColumn = Math.max(0, state.currentColumn - 1);
    if (shouldPreserveScrollTop) {
        queueFocusPreservingScroll(targetLine, targetColumn, savedScrollTop);
        if (editSelection) {
            setTimeout(() => reportCursorAndSelection(), 0);
        }
        return;
    }

    const replaceEditVersion = state.cacheVersion;
    setTimeout(() => {
        if (state.cacheVersion !== replaceEditVersion) return;
        if (editSelection) {
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection(null);
        } else {
            queuePostEditCaretRestore(targetLine, targetColumn);
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

        const savedScrollTop = scrollContainer.scrollTop;
        const deletedLineTop = lineTop(lineNumber);

        state.lineCount = Math.max(1, state.lineCount - 1);
        if (!cleanDirtyMarker(lineNumber)) {
            markDirty(lineNumber, 'mod');
        }
        if (state.showDirtyLines) {
            state.dirtyLines.delete(lineNumber + 1);
        }
        setupVirtualHeight();

        // 삭제된 줄이 뷰포트 위쪽에 있을 때 스크롤 위치를 유지합니다.
        if (deletedLineTop < savedScrollTop) {
            scrollContainer.scrollTop = Math.max(0, savedScrollTop - state.lineHeight);
        }
        post({ type: 'mergeLineWithPrevious', lineNumber: lineNumber + 1 });
        post({ type: 'contentChanged' });
        queueRender(true);
        setTimeout(() => focusLine(lineNumber, current.length), 0);
    } else if (lineNumber > 1) {
        mergeLineBackward(element);
    }
}

const {
    beginHostStreamInsert,
    endHostStreamInsert,
    insertHostStreamText
} = createHostStreamInsertCommands({
    activeEditableElement,
    beginEditTransaction,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    endEditTransaction,
    focusLine,
    getCaretOffset,
    invalidateMeasuredLineHeightsAround,
    lineTextFromElement,
    markDirty,
    measureRenderedRows,
    post,
    queueRender,
    reportCursorAndSelection,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass
});

const {
    clearPendingRepeatEdit,
    isModelRepeatKey,
    isSpaceInputEvent,
    markLineBoundaryTransition,
    markNativeBeforeInputHandled,
    normalizedModelRepeatKey,
    scheduleModelRepeatEdit,
    shouldSuppressNativeBeforeInput
} = createModelRepeatInputHandlers({
    activeEditableElement,
    cancelPendingRepeatFollowUps,
    deleteBackwardAtCaret,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    insertPlainTextByModel,
    isPlainTextKey,
    makeEditablePlainText,
    splitCurrentLine,
    state
});

const {
    beginDeferredRangeComposition,
    beginColumnComposition,
    cancelImeBypassTextarea,
    focusImeBypassTextarea,
    changedTextBetween,
    clearPendingImeSelectionCollapse,
    finishColumnComposition,
    finishRangeComposition,
    isLineInColumnComposition,
    isPendingImeSelectionCollapseFor,
    prepareMultilineCompositionHost,
    replaceSelectionForCompositionStart,
    updateColumnCompositionPreview
} = createEditorCompositionHandlers({
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
});

const {
    moveCaretHorizontal,
    moveCaretVertical
} = createCaretNavigationCommands({
    caretRectForOffset,
    changedTextBetween,
    clearCustomSelectionVisuals,
    clearPendingImeSelectionCollapse,
    commitLine,
    commitLineForSave,
    finishColumnComposition,
    finishRangeComposition,
    focusLine,
    getCaretOffset,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasCustomSelection,
    lineTextFromElement,
    normalizeSelection,
    offsetFromPointInElement,
    queueRender,
    reportCursorAndSelection,
    scrollContainer,
    setCaret,
    state,
    syncCustomSelectionClass,
    viewport
});

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
const {
    changeLineIndent,
    handleFormatText,
    handleLineSortingAndCleanup,
    handleTextConversion,
    toggleComment
} = createTextCommandActions({
    beginEditTransaction,
    cleanDirtyMarker,
    endEditTransaction,
    focusLine,
    hasCustomSelection,
    insertTextAtCaret,
    lineCommentSyntax,
    markDirty,
    normalizeSelection,
    post,
    queueRender,
    replaceSelectionWith,
    selectedLineRange,
    selectedText,
    state
});

const {
    copySelectionToClipboard,
    cutSelectionToClipboard,
    deleteSelectionOrForward,
    pasteFromClipboard,
    selectAll,
    submitHexEdit
} = createClipboardCommandHandlers({
    activeEditableElement,
    copyCsvSelectionToClipboard,
    cutCsvSelectionToClipboard,
    commitLine,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    insertTextAtCaret,
    normalizeSelection,
    post,
    queueRender,
    readClipboardText,
    replaceSelectionWith,
    reportCursorAndSelection,
    selectedText,
    state,
    syncCustomSelectionClass,
    writeClipboardText
});

export {
    applyMarkdownCommand,
    beginDeferredRangeComposition,
    beginColumnComposition,
    beginHostStreamInsert,
    cancelImeBypassTextarea,
    focusImeBypassTextarea,
    clearPendingImeSelectionCollapse,
    clearPendingRepeatEdit,
    commitLine,
    compositionSelectionRange,
    copySelectionToClipboard,
    cutSelectionToClipboard,
    deleteSelectionOrForward,
    endHostStreamInsert,
    finishColumnComposition,
    finishRangeComposition,
    flushPendingEditForSave,
    focusLine,
    getCaretOffset,
    handleFormatText,
    handleLineSortingAndCleanup,
    handleTextConversion,
    inputRangeInElement,
    insertHostStreamText,
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
    prepareMultilineCompositionHost,
    submitHexEdit,
    positionFromPointer,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    runPendingLineActions,
    scheduleModelRepeatEdit,
    selectAll,
    selectWordAtPointer,
    setCaret,
    setNativeSelectionRangeInElement,
    shouldSuppressNativeBeforeInput,
    splitCurrentLine,
    toggleComment,
    changeLineIndent,
    updateSingleLine
};
