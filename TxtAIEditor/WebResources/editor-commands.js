import {
    MAX_RENDER_CHARS,
    activeEditableElement,
    captureScrollAnchor,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    comparePositions,
    csvTableMode,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasTextAt,
    hexEditorMode,
    imeController,
    isPlainTextKey,
    isStandaloneDelimiter,
    invalidateMeasuredLineHeightsAround,
    lineCommentSyntax,
    lineTop,
    markDirty,
    measureRenderedRows,
    orderedRange,
    post,
    preserveCompressedScrollMapping,
    preserveScrollTop,
    queueRender,
    renderImmediately,
    readClipboardText,
    reportCursorAndSelection,
    requestLines,
    restoreScrollAnchor,
    selectionController,
    selectedLineRange,
    selectedText,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    usesCompressedScroll,
    visualScrollDeltaToScrollTopDelta,
    viewportController,
    writeClipboardText
} from './editor-core.js';
import { scrollContainer, viewport } from './editor-dom.js';
import {
    capturePreviewInputScrollAnchor,
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
    requestInputCaretReveal,
    selectWordAtPointer,
    setCaret,
    setNativeSelectionRangeInElement
} from './editor-caret.js';
import { highlightPreviewSearchInElement, renderLineContent } from './editor-highlighter.js';
import { markdownPreviewDeleteRange, markdownPreviewLineBreakOffset, markdownPreviewSpaceInsertionOffset, markdownPreviewTextPosition, needsMarkdownPreviewBlockRefresh, updateGroupedMarkdownElement, usesMarkdownPreviewEditing } from './editor-markdown-preview-edit.js';
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
import { createHostStreamInsertCommands } from './editor-host-stream-insert.js';
import { createKeyboardRepeatController } from './editor-repeat-input.js';
import {
    copyCsvSelectionToClipboard,
    cutCsvSelectionToClipboard
} from './editor-csv-table.js';
import { createMarkdownCommandHandlers } from './editor-markdown-commands.js';
import { createTextCommandActions } from './editor-text-command-actions.js';

let splitScrollRestoreToken = 0;
for (const eventName of ['pointerdown', 'wheel']) {
    // Explicit scrolling/selection supersedes Enter's delayed caret alignment.
    document.addEventListener(eventName, () => { splitScrollRestoreToken++; }, { capture: true, passive: true });
}
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
    const isComposing = imeController.isComposing &&
        (!imeController.compositionLine || imeController.compositionLine === lineNumber);

    const previousText = state.cache.get(lineNumber) ?? '';
    const text = lineTextFromElement(element);
    const textChanged = previousText !== text;

    if (!isComposing && element.dataset.markdownPreviewLine === 'code-empty') {
        if (textChanged) {
            const inserted = text.endsWith(previousText) ? text.slice(0, text.length - previousText.length) : text;
            insertIntoEmptyPreviewCode(element, inserted);
        }
        return null;
    }

    const isInlineLivePreviewActiveLine = state.inlineLivePreviewEnabled &&
        state.inlineLivePreviewSourceLine === lineNumber;

    if (isComposing && imeController.rangeComposition) {
        // 다중 줄 선택 IME 조합 중에는 모델/DOM 재렌더를 건드리지 않는다.
        // 최종 조합 문자열은 compositionend에서 rangeEdit 한 건으로 반영한다.
    } else if (isComposing && imeController.columnComposition) {
        updateColumnCompositionPreview(element);
    } else if (isComposing) {
        // 네이티브 IME가 소유한 DOM을 로컬 미리보기로만 유지한다.
        // cache 및 C# 문서 모델 delta는 compositionend에서 한 번만 확정한다.
    } else if (textChanged) {
        state.cache.set(lineNumber, text);
        if (!isInlineLivePreviewActiveLine || needsMarkdownPreviewBlockRefresh(state, lineNumber)) {
            state.cacheVersion++;
        }
    }

    const caretOffset = getCaretOffset(element);
    const inputSnapshot = { lineNumber, text, caretOffset };
    state.currentLine = lineNumber;
    state.currentColumn = caretOffset + 1;

    if (isComposing) {
        reportCursorAndSelection(element, caretOffset);
        requestInputCaretReveal(element, caretOffset);
        if (state.wordWrap && !imeController.rangeComposition) {
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

    const refreshPreview = needsMarkdownPreviewBlockRefresh(state, lineNumber);
    if (refreshPreview && text.trim()) element.closest('.line-row')?.classList.remove('live-preview-empty');

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    postLineUpdate(lineNumber, previousText, text);
    post({ type: 'contentChanged' });
    if (refreshPreview) queueRender(true);
    reportCursorAndSelection(element, caretOffset);

    requestInputCaretReveal(element, caretOffset);
    if (state.wordWrap && !refreshPreview) {
        measureRenderedRows(false);
    }
    return inputSnapshot;
}

function commitLineForSave(element) {
    if (!element || element.getAttribute('contenteditable') !== 'true') {
        return false;
    }

    const lineNumber = Number(element.dataset.line || imeController.compositionLine || state.currentLine || 1);

    if (imeController.rangeComposition && finishRangeComposition(element, lineNumber)) {
        imeController.beginCommit();
        imeController.completeCommit();
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

    if (imeController.columnComposition && finishColumnComposition(element, lineNumber)) {
        imeController.beginCommit();
        imeController.completeCommit();
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

    const text = lineTextFromElement(element);
    const previousText = state.cache.get(lineNumber) ?? '';
    imeController.reset();
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
    const requestedLine = Number(imeController.compositionLine || focusedLine || state.currentLine || state.editingLine || 1);
    let element = (focusedElement && focusedElement.getAttribute('contenteditable') === 'true')
        ? focusedElement
        : viewport.querySelector(`.line-text[data-line="${requestedLine}"]`) || activeEditableElement();
    const wasFocused = !!(element && document.activeElement === element);
    const domSelection = window.getSelection();
    const hasDomCaret = !!((!hasCustomSelection() || imeController.isComposing) && element && domSelection?.rangeCount &&
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

        const lineNumber = Number(imeController.compositionLine || requestedLine || state.currentLine || 1);
        element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) ||
            element ||
            activeEditableElement();

        if (element && element.getAttribute('contenteditable') === 'true') {
            commitLineForSave(element);
        } else {
            imeController.reset();
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

        post({
            type: 'editorFlushedForSave',
            requestId: Number(requestId || 0),
            documentVersion: state.hostDocumentVersion
        });
    };

    if (imeController.isComposing && element && element.getAttribute('contenteditable') === 'true') {
        const finishAfterCompositionEnd = () => queueMicrotask(finish);
        element.addEventListener('compositionend', finishAfterCompositionEnd, { once: true });
        try {
            element.blur();
        } catch { }

        if (!imeController.isComposing) {
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
    return Math.max(1, Number(viewportController.lineHeight || 1));
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
    const compressedScroll = usesCompressedScroll();
    // In compressed mode a visual row is represented by only a few scrollTop
    // pixels. Applying the normal three-row guard therefore makes every Enter
    // nudge the viewport even while the new caret still fits below. Wait for
    // the caret to reach the real viewport edge in that mode.
    const bottomGuard = compressedScroll ? 0 : 3 * rowHeight;
    const guardBottom = Math.max(caretHeight, scrollContainer.clientHeight - bottomGuard);

    // Enter must behave like ArrowDown: while there is room below, the caret
    // moves down by one visual row without scrolling. Normal scrolling retains
    // its three-line guard; compressed scrolling uses the actual viewport edge.
    // The previous implementation called focusLine() before the inserted row was
    // measured, so the virtual lineTop estimate could over-scroll and make the
    // caret jump 6-7 rows upward; repeated Enter then accumulated that error.
    const naturalNextBottom = caretViewportBottom + rowHeight;
    const shouldAdjustScroll = naturalNextBottom > guardBottom;
    const desiredViewportBottom = Math.min(naturalNextBottom, guardBottom);

    return {
        preview: usesMarkdownPreviewEditing(state),
        scrollTop: scrollContainer.scrollTop,
        caretViewportTop,
        caretViewportBottom,
        shouldAdjustScroll,
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
        // Input or navigation after Enter owns the caret now. In particular,
        // never rewind a new Korean composition to the inserted list marker.
        if (imeController.isComposing || state.currentLine !== targetLineNumber) return;

        const element = viewport.querySelector(`.line-text[data-line="${targetLineNumber}"]`);
        if (!element || element.getAttribute('contenteditable') !== 'true') {
            if (attempt < 8) {
                alignSplitCaretAfterRender(targetLineNumber, targetColumn, anchor, token, attempt + 1);
            } else {
                focusLine(targetLineNumber, targetColumn, 3 * anchor.rowHeight);
            }
            return;
        }

        // At a list/heading start the visible caret is after hidden syntax,
        // even when the split requested source column zero.
        const caretColumn = markdownPreviewTextPosition(element, targetColumn)?.sourceOffset ?? targetColumn;
        if (state.currentColumn !== caretColumn + 1) return;
        setCaret(element, caretColumn);
        if (anchor.preview) {
            const caretRect = caretRectForOffset(element, caretColumn);
            if (!caretRect) return;
            const containerRect = scrollContainer.getBoundingClientRect();
            const bottomMargin = Math.min(3 * viewportController.lineHeight,
                Math.max(0, scrollContainer.clientHeight - caretRect.height));
            // Headings, lists and the following blank row need not have the
            // same height. Decide from the newly painted caret, not an estimate
            // based on the old row's font or its grouped block height.
            const delta = caretRect.top < containerRect.top
                ? caretRect.top - containerRect.top
                : Math.max(0, caretRect.bottom - (containerRect.bottom - bottomMargin));
            if (Math.abs(delta) > 0.5) {
                scrollContainer.scrollTop = clampScrollTop(
                    scrollContainer.scrollTop + visualScrollDeltaToScrollTopDelta(delta));
            }
            if (attempt < 2) {
                alignSplitCaretAfterRender(targetLineNumber, targetColumn, anchor, token, attempt + 1);
            }
            return;
        }
        if (!anchor.shouldAdjustScroll) {
            // When the next visual row still fits, Enter should leave the
            // viewport exactly where it was. Re-rendering a wrapped text row can
            // differ by a fraction of a CSS pixel from the pre-split estimate;
            // feeding that difference back into scrollTop causes a small upward
            // drift that does not happen when splitting an empty line.
            const preservedScrollTop = clampScrollTop(anchor.scrollTop);
            if (scrollContainer.scrollTop !== preservedScrollTop) {
                scrollContainer.scrollTop = preservedScrollTop;
            }
        }
        const caretRect = caretRectForOffset(element, caretColumn);
        if (!caretRect) return;

        const containerRect = scrollContainer.getBoundingClientRect();
        const currentViewportBottom = caretRect.bottom - containerRect.top;
        if (anchor.shouldAdjustScroll) {
            const delta = currentViewportBottom - anchor.desiredViewportBottom;
            if (Math.abs(delta) > 0.5) {
                scrollContainer.scrollTop = clampScrollTop(
                    scrollContainer.scrollTop + visualScrollDeltaToScrollTopDelta(delta));
            }
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
    if (element?.dataset.markdownPreviewLine === 'code-empty' && !hasCustomSelection()) {
        insertIntoEmptyPreviewCode(element, '');
        return;
    }
    if (element?.dataset.markdownPreviewLine === 'table' && !hasCustomSelection()) {
        insertPlainTextByModel(element, '<br>');
        return;
    }
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
                selectionController.selection = null;
                selectionController.anchor = { line: caretLine, column: caretColumn };
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
    const previewSplit = usesMarkdownPreviewEditing(state) && element?.isConnected &&
        elementLineNumber === lineNumber;
    const text = useElementCaret
        ? lineTextFromElement(element)
        : (state.cache.get(lineNumber) ?? '');
    let caret = useElementCaret
        ? getCaretOffset(element)
        : Math.max(0, Math.min(Number(state.currentColumn || 1) - 1, text.length));

    if (useElementCaret && usesMarkdownPreviewEditing(state) && element.dataset.markdownPreviewLine !== 'code') {
        // The first visible caret is after hidden heading/list delimiters.
        // Enter at a visual line's start must move those delimiters too.
        const visibleStart = markdownPreviewTextPosition(element, 0)?.sourceOffset || 0;
        if (caret <= visibleStart) caret = 0;
        else caret = markdownPreviewLineBreakOffset(element, caret);
    }

    if (useElementCaret) {
        // Keep the model in sync before splitting. This covers the last native
        // composition/input that may still only exist in the contenteditable DOM.
        state.cache.set(lineNumber, text);
    }

    const splitScrollAnchor = useElementCaret || previewSplit
        ? captureSplitScrollAnchor(element, caret)
        : null;
    const shouldPreserveSplitScrollTop = !!splitScrollAnchor && (previewSplit || !splitScrollAnchor.shouldAdjustScroll);
    const virtualScrollAnchor = captureScrollAnchor();
    if (previewSplit && shouldPreserveSplitScrollTop) {
        // Keep the physical viewport throughout regrouping/measurement, rather
        // than allowing an upstream height correction and undoing it afterward.
        state.editScrollRestore = { top: splitScrollAnchor.scrollTop, left: scrollContainer.scrollLeft };
    }

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
    let nextPrefix = insertedIndent;
    const listItem = usesMarkdownPreviewEditing(state) &&
        elementLineNumber === lineNumber && element?.dataset.markdownPreviewLine === 'list' &&
        caret === text.length
        ? /^([ \t]*)([-*+]|\d+\.)([ \t]+)(?:\[([ xX])\]([ \t]+))?(.*)$/.exec(text)
        : null;
    const continueList = !!listItem && listItem[6].trim().length > 0;
    if (continueList) {
        const marker = /^\d+\.$/.test(listItem[2])
            ? `${Number.parseInt(listItem[2], 10) + 1}.`
            : listItem[2];
        nextPrefix = listItem[1] + marker + listItem[3] +
            (listItem[4] !== undefined ? `[ ]${listItem[5]}` : '');
    }
    const indentedAfter = nextPrefix + normalizedAfter;
    const nextCaretColumn = nextPrefix.length;
    const nextLineNumber = lineNumber + 1;

    state.cache.set(lineNumber, before);
    shiftCachedLines(nextLineNumber, 1);
    state.cache.set(nextLineNumber, indentedAfter);
    state.lineCount++;
    if (!previewSplit) {
        invalidateMeasuredLineHeightsAround(lineNumber);
        invalidateMeasuredLineHeightsAround(nextLineNumber);
    }
    // A preview row can own a whole wrapped list/paragraph. Keep its last
    // measured height until the new DOM is measured below: replacing it with
    // one estimated line temporarily shrinks the scroll range and clamps it.
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    if (state.showDirtyLines) {
        state.dirtyLines.set(nextLineNumber, 'add');
    }
    selectionController.selection = null;
    clearCustomSelectionVisuals();
    syncCustomSelectionClass();
    if (shouldPreserveSplitScrollTop && usesCompressedScroll()) {
        preserveCompressedScrollMapping(virtualScrollAnchor, splitScrollAnchor.scrollTop);
    }
    setupVirtualHeight();
    if (shouldPreserveSplitScrollTop) {
        // Adding a line changes the logical-line ratio used by compressed
        // scrolling. Restoring that ratio moves scrollTop upward; keeping the
        // physical value is the intended behavior while the next row fits.
        scrollContainer.scrollTop = clampScrollTop(splitScrollAnchor.scrollTop);
    } else {
        restoreScrollAnchor(virtualScrollAnchor);
    }
    post({ type: 'splitLine', lineNumber, before, after: indentedAfter });
    post({ type: 'contentChanged' });
    markLineBoundaryTransition(nextLineNumber, nextCaretColumn);
    selectionController.anchor = { line: nextLineNumber, column: nextCaretColumn };
    state.currentLine = nextLineNumber;
    state.currentColumn = nextCaretColumn + 1;
    state.editingLine = nextLineNumber;
    if (state.inlineLivePreviewEnabled) {
        state.inlineLivePreviewSourceLine = nextLineNumber;
        state.inlineLivePreviewEditableBlock = null;
    }

    if (useElementCaret && element.isConnected) {
        if (!updateGroupedMarkdownElement(element, before, state)) element.innerHTML = renderLineContent(lineNumber, before);
    }

    const immediateTarget = viewport.querySelector(`.line-text[data-line="${nextLineNumber}"]`);
    if (immediateTarget && immediateTarget.getAttribute('contenteditable') === 'true') {
        if (!updateGroupedMarkdownElement(immediateTarget, indentedAfter, state)) {
            immediateTarget.innerHTML = renderLineContent(nextLineNumber, indentedAfter);
        }
        setCaret(immediateTarget, nextCaretColumn);
    }

    if (shouldPreserveSplitScrollTop) {
        scrollContainer.scrollTop = clampScrollTop(splitScrollAnchor.scrollTop);
    }

    queueRender(true);
    let splitTargetReady = false;
    if (previewSplit && renderImmediately()) {
        // Complete blank-row/list regrouping and measurement before placing
        // the caret, rather than focusing the old next item's temporary DOM.
        const nextElement = viewport.querySelector(`.line-text[data-line="${nextLineNumber}"]`);
        if (nextElement?.getAttribute('contenteditable') === 'true') {
            setCaret(nextElement, nextCaretColumn);
            splitTargetReady = true;
        }
        if (shouldPreserveSplitScrollTop) {
            scrollContainer.scrollTop = clampScrollTop(splitScrollAnchor.scrollTop);
        }
    }
    if (splitScrollAnchor) {
        alignSplitCaretAfterRender(nextLineNumber, nextCaretColumn, splitScrollAnchor, splitRestoreToken);
    } else if (!splitTargetReady) {
        focusLine(nextLineNumber, nextCaretColumn, 3 * viewportController.lineHeight);
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
    if (element?.dataset.markdownPreviewLine === 'table') {
        insertPlainTextByModel(element, normalized);
        return;
    }
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
                for (let i = 1; i < parts.length; i++) {
                    const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
                    const nextLineNumber = targetLine + i;
                    state.cache.set(nextLineNumber, nextText);
                    if (state.showDirtyLines) {
                        state.dirtyLines.set(nextLineNumber, 'add');
                    }
                }
                // Keep large pastes atomic instead of flooding the host with one edit per line.
                post({ type: 'rangeEdit', startLine: targetLine, startColumn: caret + 1,
                    endLine: targetLine, endColumn: caret + 1, text: normalized });
                post({ type: 'contentChanged' });
            } finally {
                endEditTransaction();
            }
            state.lineCount += insertedCount;
            state.currentLine = lastLineNumber;
            state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
            selectionController.selection = null;
            selectionController.anchor = {
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
            for (let i = 1; i < parts.length; i++) {
                const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
                const nextLineNumber = lineNumber + i;
                state.cache.set(nextLineNumber, nextText);
                if (state.showDirtyLines) {
                    state.dirtyLines.set(nextLineNumber, 'add');
                }
            }

            // Use the same bulk edit protocol as multi-line selection replacement.
            post({ type: 'rangeEdit', startLine: lineNumber, startColumn: caret + 1,
                endLine: lineNumber, endColumn: caret + 1, text: normalized });
            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }
        state.lineCount += insertedCount;
        state.currentLine = lastLineNumber;
        state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
        selectionController.selection = null;
        selectionController.anchor = {
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

function insertIntoEmptyPreviewCode(element, text) {
    const position = { line: Number(element.dataset.line), column: 0 };
    const inserted = String(text || '');
    replaceSelectionWith({ start: position, end: position }, inserted + '\n',
        { startOffset: inserted.length, endOffset: inserted.length });
    const editVersion = state.cacheVersion;
    // Finish the placeholder after the IME event returns, before another key
    // can arrive ahead of the queued viewport paint and insert a second row.
    queueMicrotask(() => {
        if (!element.isConnected || imeController.isComposing || state.cacheVersion !== editVersion) return;
        element.dataset.markdownPreviewLine = 'code';
        updateGroupedMarkdownElement(element, state.cache.get(position.line) || '', state);
        highlightPreviewSearchInElement(element, position.line);
        if (state.currentLine === position.line) setCaret(element, Math.max(0, state.currentColumn - 1));
    });
}

function insertPlainTextByModel(element, text) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return;
    if (element.dataset.markdownPreviewLine === 'code-empty' && !hasCustomSelection()) {
        insertIntoEmptyPreviewCode(element, text);
        return;
    }
    if (element.dataset.markdownPreviewLine === 'table') {
        text = String(text || '').replace(/\r\n?|\n/g, '<br>').replace(/\|/g, '\\|');
    }
    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) replaceSelectionWith(sel, text || '');
        return;
    }

    makeEditablePlainText(element);
    const current = lineTextFromElement(element);
    const offset = getCaretOffset(element);
    const caret = text === ' ' ? markdownPreviewSpaceInsertionOffset(element, offset)
        : text === '<br>' && element.dataset.markdownPreviewLine === 'table' ? markdownPreviewLineBreakOffset(element, offset)
            : offset;
    const nextText = current.slice(0, caret) + text + current.slice(caret);
    updateSingleLine(element, nextText, caret + String(text || '').length);
}

function updateSingleLine(element, text, caretColumn) {
    // Model-handled Space/Delete/Backspace prevent native beforeinput. Capture
    // before changing either the DOM or the virtual height, just as native input does.
    capturePreviewInputScrollAnchor(element);
    const lineNumber = Number(element.dataset.line || 1);
    const previousText = state.cache.get(lineNumber) ?? '';
    const nextText = String(text ?? '');
    const nextColumn = Math.max(0, Math.min(Number(caretColumn || 0), nextText.length));

    state.cache.set(lineNumber, nextText);
    state.cacheVersion++;
    if (!usesMarkdownPreviewEditing(state) && element.dataset.markdownPreviewLine !== 'table') {
        invalidateMeasuredLineHeightsAround(lineNumber);
    }
    selectionController.selection = null;
    syncCustomSelectionClass();
    clearCustomSelectionVisuals();
    selectionController.anchor = { line: lineNumber, column: nextColumn };
    state.currentLine = lineNumber;
    state.currentColumn = nextColumn + 1;
    const refreshPreview = needsMarkdownPreviewBlockRefresh(state, lineNumber);
    if (refreshPreview && nextText.trim()) element.closest('.line-row')?.classList.remove('live-preview-empty');

    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }

    if (imeController.isComposing ||
        (imeController.compositionLine && imeController.compositionLine === lineNumber) ||
        nextText.length > MAX_RENDER_CHARS) {
        element.textContent = nextText;
    } else {
        if (!updateGroupedMarkdownElement(element, nextText, state)) {
            element.innerHTML = renderLineContent(lineNumber, nextText);
        }
        highlightPreviewSearchInElement(element, lineNumber);
    }

    setCaret(element, nextColumn);

    postLineUpdate(lineNumber, previousText, nextText);
    post({ type: 'contentChanged' });
    if (refreshPreview) queueRender(true);

    requestInputCaretReveal(element, nextColumn);
    if (state.wordWrap && !refreshPreview) {
        measureRenderedRows(false);
    }
}

function applyMergeLineForward(lineNumber, text, nextText) {
    const savedScrollTop = scrollContainer.scrollTop;
    const savedScrollLeft = scrollContainer.scrollLeft;
    const previewMerge = usesMarkdownPreviewEditing(state);
    const previousHeight = previewMerge
        ? viewportController.lineHeightFor(lineNumber, { useMeasured: true })
        : 0;
    if (previewMerge) {
        state.editScrollRestore = { top: savedScrollTop, left: savedScrollLeft };
    }
    preserveScrollTop(savedScrollTop);

    if (state.cache.get(lineNumber) !== text) {
        post({ type: 'lineChanged', lineNumber, text });
    }

    shiftCachedLines(lineNumber + 1, -1);
    state.cache.set(lineNumber, text + nextText);
    // A source row may own a whole wrapped paragraph/list. Retain its height
    // until the merged DOM is measured, rather than collapsing it to one line.
    if (!previewMerge) invalidateMeasuredLineHeightsAround(lineNumber);
    state.lineCount = Math.max(1, state.lineCount - 1);
    if (previewMerge) {
        // Shifting the removed row's cached height backward can overwrite the
        // surviving block owner with a covered row's zero height.
        viewportController.setMeasuredLineHeight(lineNumber, previousHeight, state.lineCount);
    }
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
    selectionController.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber, text.length);
    queueRender(true);
    if (previewMerge) {
        state.editingLine = lineNumber;
        state.inlineLivePreviewSourceLine = lineNumber;
        state.inlineLivePreviewEditableBlock = null;
        renderImmediately();
        const target = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        if (target?.getAttribute('contenteditable') === 'true') {
            setCaret(target, text.length, 0, true, false);
        } else {
            queueFocusPreservingScroll(lineNumber, text.length, savedScrollTop, savedScrollLeft);
        }
        restoreScrollTop(savedScrollTop);
        scrollContainer.scrollLeft = savedScrollLeft;
        return;
    }
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
    selectionController.selection = null;
    syncCustomSelectionClass();
    markLineBoundaryTransition(lineNumber - 1, previous.length);
    queueRender(true);
    queuePostEditFocus(() => focusLine(lineNumber - 1, previous.length, 3 * viewportController.lineHeight));
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
    if (element.dataset.markdownPreviewLine === 'code' && /^[ \t]*(?:`{3,}|~{3,})/.test(nextText)) return;

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
    if (element.dataset.markdownPreviewLine === 'code' && /^[ \t]*(?:`{3,}|~{3,})/.test(previous)) return;

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
    const previewRange = markdownPreviewDeleteRange(element, caret, 1, graphemeDeleteStart, graphemeDeleteEnd);
    if (previewRange) {
        if (previewRange.merge) {
            if (element.dataset.markdownPreviewLine !== 'table') mergeLineForward(element);
        }
        else updateSingleLine(element, text.slice(0, previewRange.start) + text.slice(previewRange.end), caret);
        return;
    }
    if (caret < text.length) {
        const delEnd = graphemeDeleteEnd(text, caret);
        updateSingleLine(element, text.slice(0, caret) + text.slice(delEnd), caret);
        return;
    }

    mergeLineForward(element);
}

function outdentMarkdownListAtStart(element, caret = element ? getCaretOffset(element) : 0) {
    if (state.readOnly || imeController.isComposing || hasCustomSelection() ||
        !usesMarkdownPreviewEditing(state) || element?.getAttribute('contenteditable') !== 'true' ||
        element.dataset.markdownPreviewLine !== 'list' ||
        window.getSelection()?.isCollapsed === false) return false;
    const text = lineTextFromElement(element);
    const indent = /^[ \t]+(?=(?:[-*+]|\d+\.)[ \t]+)/.exec(text)?.[0];
    const visibleStart = markdownPreviewTextPosition(element, 0)?.sourceOffset;
    if (!indent || visibleStart === undefined || caret > visibleStart) return false;
    const lineNumber = Number(element.dataset.line);

    const removed = indent.startsWith('\t') ? 1 : Math.min(state.tabSize || 4, /^ +/.exec(indent)[0].length);
    const nextText = text.slice(removed);
    const nextColumn = Math.max(0, caret - removed);
    const scroll = { top: scrollContainer.scrollTop, left: scrollContainer.scrollLeft };
    const previousText = state.cache.get(lineNumber) ?? '';
    state.cache.set(lineNumber, nextText);
    state.cacheVersion++;
    state.currentLine = lineNumber;
    state.currentColumn = nextColumn + 1;
    state.editingLine = lineNumber;
    state.inlineLivePreviewSourceLine = lineNumber;
    state.inlineLivePreviewEditableBlock = null;
    selectionController.anchor = { line: lineNumber, column: nextColumn };
    state.editScrollRestore = scroll;
    if (!cleanDirtyMarker(lineNumber)) markDirty(lineNumber, 'mod');
    postLineUpdate(lineNumber, previousText, nextText);
    post({ type: 'contentChanged' });
    // Outdenting changes the surrounding ul/ol hierarchy. Rebuild it before
    // the next input, keeping the previous measured height until it is ready.
    queueRender(true);
    renderImmediately();
    const target = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
    if (target?.getAttribute('contenteditable') === 'true') setCaret(target, nextColumn, 0, true, false);
    scrollContainer.scrollTop = scroll.top;
    scrollContainer.scrollLeft = scroll.left;
    return true;
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
    if (outdentMarkdownListAtStart(element, caret)) return;
    const previewRange = markdownPreviewDeleteRange(element, caret, -1, graphemeDeleteStart, graphemeDeleteEnd);
    if (previewRange) {
        updateSingleLine(element, text.slice(0, previewRange.start) + text.slice(previewRange.end), previewRange.start);
        return;
    }
    if (element.dataset.markdownPreviewLine === 'table') return;
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
    selectionController.selection = {
        start: { line: startLine, column: nextCol },
        end: { line: endLine, column: nextCol },
        isColumn: true
    };
    selectionController.anchor = selectionController.selection.start;
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

function moveSelectedLines(direction) {
    if (direction !== -1 && direction !== 1) return false;

    const element = activeEditableElement();
    if (!element || element.getAttribute('contenteditable') !== 'true' || imeController.isComposing) {
        return false;
    }

    const normalizedSelection = hasCustomSelection() ? normalizeSelection() : null;
    if (normalizedSelection?.isColumn) return false;

    commitLine(element);

    const currentLine = Math.min(
        state.lineCount,
        Math.max(1, Number(element.dataset.line || state.currentLine || 1)));
    const lineRange = selectedLineRange();
    const startLine = Math.min(
        state.lineCount,
        Math.max(1, Number(lineRange.startLine || currentLine)));
    const endLine = Math.min(
        state.lineCount,
        Math.max(startLine, Number(lineRange.endLine || startLine)));

    if ((direction < 0 && startLine <= 1) ||
        (direction > 0 && endLine >= state.lineCount)) {
        return false;
    }

    const getLineText = line => {
        if (state.cache.has(line)) return state.cache.get(line) ?? '';
        const lineElement = viewport.querySelector(`.line-text[data-line="${line}"]`);
        return lineElement ? lineTextFromElement(lineElement) : null;
    };

    const affectedStartLine = direction < 0 ? startLine - 1 : startLine;
    const affectedEndLine = direction < 0 ? endLine : endLine + 1;
    const affectedLines = new Map();
    for (let line = affectedStartLine; line <= affectedEndLine; line++) {
        const text = getLineText(line);
        if (text === null) {
            requestLines(affectedStartLine, affectedEndLine - affectedStartLine + 1);
            return false;
        }
        affectedLines.set(line, text);
        state.cache.set(line, text);
    }

    const movedBlock = [];
    for (let line = startLine; line <= endLine; line++) {
        movedBlock.push(affectedLines.get(line) ?? '');
    }

    const adjacentLine = direction < 0 ? startLine - 1 : endLine + 1;
    const adjacentText = affectedLines.get(adjacentLine) ?? '';
    const replacementLines = direction < 0
        ? [...movedBlock, adjacentText]
        : [adjacentText, ...movedBlock];
    const replacementText = replacementLines.join('\n');
    const editStartLine = affectedStartLine;
    const editEndLine = affectedEndLine;
    const range = {
        start: { line: editStartLine, column: 0 },
        end: { line: editEndLine, column: affectedLines.get(editEndLine)?.length ?? 0 }
    };

    const caretColumn = Math.max(
        0,
        Math.min(getCaretOffset(element), affectedLines.get(currentLine)?.length ?? 0));
    const caretPosition = { line: currentLine, column: caretColumn };
    const hasSelection = !!normalizedSelection && !normalizedSelection.isColumn;
    const selectionStart = hasSelection
        ? { ...normalizedSelection.start }
        : caretPosition;
    const selectionEnd = hasSelection
        ? { ...normalizedSelection.end }
        : caretPosition;
    const selectionAnchor = hasSelection && selectionController.anchor
        ? { ...selectionController.anchor }
        : selectionStart;
    const selectionFocus = hasSelection
        ? (selectionAnchor.line === selectionStart.line && selectionAnchor.column === selectionStart.column
            ? selectionEnd
            : selectionAnchor.line === selectionEnd.line && selectionAnchor.column === selectionEnd.column
                ? selectionStart
                : selectionEnd)
        : caretPosition;

    const mapPosition = position => {
        const line = Number(position?.line || currentLine);
        const column = Math.max(0, Number(position?.column || 0));
        if (line >= startLine && line <= endLine) {
            return { line: line + direction, column };
        }
        if (line === endLine + 1 && column === 0) {
            return { line: line + direction, column: 0 };
        }
        return { line, column };
    };

    const offsetForPosition = position => {
        const mapped = mapPosition(position);
        const lineIndex = mapped.line - editStartLine;
        if (lineIndex >= replacementLines.length) {
            return replacementText.length + (mapped.column === 0 ? 1 : 0);
        }

        const safeLineIndex = Math.max(0, lineIndex);
        let offset = 0;
        for (let index = 0; index < safeLineIndex; index++) {
            offset += replacementLines[index].length + 1;
        }

        const lineText = replacementLines[safeLineIndex] || '';
        return offset + Math.min(Math.max(0, mapped.column), lineText.length);
    };

    replaceSelectionWith(range, replacementText, {
        startOffset: offsetForPosition(selectionStart),
        endOffset: offsetForPosition(selectionEnd),
        anchorOffset: offsetForPosition(selectionAnchor),
        focusOffset: offsetForPosition(selectionFocus)
    });
    return true;
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
    const preserveEditScroll = editSelection?.preserveScroll === true;
    const savedScrollLeft = scrollContainer.scrollLeft;
    const shouldPreserveScrollTop = preserveEditScroll || netLines < 0 && editLineTop >= savedScrollTop;
    if (shouldPreserveScrollTop) {
        preserveScrollTop(savedScrollTop);
    }
    if (preserveEditScroll) state.editScrollRestore = { top: savedScrollTop, left: savedScrollLeft };

    state.lineCount = Math.max(1, state.lineCount + netLines);
    setupVirtualHeight();

    // 삭제된 줄이 뷰포트 위쪽에 있을 때 스크롤 위치를 유지합니다.
    if (!preserveEditScroll && netLines < 0 && editLineTop < savedScrollTop) {
        const scrollAdjust = -netLines * viewportController.lineHeight;
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
            const safeOffset = Math.max(0, Math.min(offset, replacementText.length + 1));
            if (safeOffset > replacementText.length) {
                const afterReplacementLine = start.line + parts.length;
                if (afterReplacementLine <= state.lineCount) {
                    return { line: afterReplacementLine, column: 0 };
                }
                const lastLineText = state.cache.get(state.lineCount) || '';
                return { line: state.lineCount, column: lastLineText.length };
            }
            const before = replacementText.slice(0, safeOffset).split('\n');
            if (before.length === 1) {
                return { line: start.line, column: start.column + before[0].length };
            }
            return { line: start.line + before.length - 1, column: before[before.length - 1].length };
        };
        const startOffset = editSelection.startOffset ?? 0;
        const endOffset = editSelection.endOffset ?? startOffset;
        const selectionStart = positionFromOffset(startOffset);
        const selectionEnd = positionFromOffset(endOffset);
        const selectionAnchor = positionFromOffset(editSelection.anchorOffset ?? startOffset);
        const selectionFocus = positionFromOffset(editSelection.focusOffset ?? endOffset);
        selectionController.anchor = selectionAnchor;
        selectionController.selection = startOffset === endOffset
            ? null
            : { start: selectionStart, end: selectionEnd };
        state.currentLine = selectionFocus.line;
        state.currentColumn = selectionFocus.column + 1;
        if (startOffset === endOffset) {
            clearCustomSelectionVisuals();
        }
        syncCustomSelectionClass();
    } else {
        selectionController.selection = null;
        syncCustomSelectionClass();
        clearCustomSelectionVisuals();
        const endLine = start.line + parts.length - 1;
        const endColumn = parts.length === 1 ? start.column + parts[0].length : parts[parts.length - 1].length;
        state.currentLine = endLine;
        state.currentColumn = endColumn + 1;
        selectionController.anchor = { line: endLine, column: endColumn };
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
        queueFocusPreservingScroll(targetLine, targetColumn, savedScrollTop, preserveEditScroll ? savedScrollLeft : null);
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
            scrollContainer.scrollTop = Math.max(0, savedScrollTop - viewportController.lineHeight);
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
    selectionController,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass
});

const {
    isModelRepeatKey,
    isSpaceInputEvent,
    markLineBoundaryTransition,
    markNativeBeforeInputHandled,
    normalizedModelRepeatKey,
    releaseModelRepeatKey,
    resetModelRepeatState,
    scheduleModelRepeatEdit,
    shouldSuppressNativeBeforeInput
} = createKeyboardRepeatController({
    activeEditableElement,
    cancelPendingRepeatFollowUps,
    deleteBackwardAtCaret,
    deleteForwardAtCaret,
    focusLine,
    getCursor: () => ({ line: state.currentLine, column: state.currentColumn }),
    getLineCount: () => state.lineCount,
    hasCustomSelection,
    insertPlainTextByModel,
    isImeComposing: () => imeController.isComposing,
    isPlainTextKey,
    isReadOnly: () => state.readOnly,
    makeEditablePlainText,
    setCursor: ({ line, column }) => {
        state.currentLine = line;
        state.currentColumn = column;
    },
    splitCurrentLine,
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
    imeController,
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
    selectionController,
    setCaret,
    setNativeSelectionRangeInElement,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    viewportController,
    viewport
});

const {
    moveCaretHorizontal,
    moveCaretVertical,
    moveCaretWord
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
    imeController,
    lineTextFromElement,
    normalizeSelection,
    offsetFromPointInElement,
    queueRender,
    reportCursorAndSelection,
    scrollContainer,
    selectionController,
    setCaret,
    state,
    syncCustomSelectionClass,
    viewportController,
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
    imeController,
    isStandaloneDelimiter,
    lineTextFromElement,
    markDirty,
    normalizeSelection,
    orderedRange,
    post,
    preserveScrollTop,
    queueFocusPreservingScroll,
    queueRender,
    renderImmediately,
    replaceSelectionWith,
    reportCursorAndSelection,
    selectionController,
    setCaret,
    scrollContainer,
    state,
    syncCustomSelectionClass,
    viewport,
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
    csvTableMode,
    cutCsvSelectionToClipboard,
    commitLine,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    hexEditorMode,
    insertTextAtCaret,
    normalizeSelection,
    post,
    queueRender,
    readClipboardText,
    replaceSelectionWith,
    reportCursorAndSelection,
    selectionController,
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
    moveSelectedLines,
    moveCaretHorizontal,
    moveCaretVertical,
    moveCaretWord,
    normalizedModelRepeatKey,
    outdentMarkdownListAtStart,
    pasteFromClipboard,
    prepareMultilineCompositionHost,
    releaseModelRepeatKey,
    submitHexEdit,
    positionFromPointer,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    runPendingLineActions,
    resetModelRepeatState,
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
