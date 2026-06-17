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
    lineHeightFor,
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
    totalVirtualHeight,
    state,
    syncCustomSelectionClass,
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
import { createEditorCompositionHandlers } from './editor-composition.js';
import { createMarkdownCommandHandlers } from './editor-markdown-commands.js';
import { createTextCommandActions } from './editor-text-command-actions.js';

let verticalCaretVisualAnchor = null;
let splitScrollRestoreToken = 0;

function beginEditTransaction() {
    post({ type: 'editTransactionStarted' });
}

function endEditTransaction() {
    post({ type: 'editTransactionEnded' });
}

function commitDomLineBeforeCaretNavigation(element) {
    if (!element || element.getAttribute?.('contenteditable') !== 'true') return false;

    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    const domText = lineTextFromElement(element);
    if (state.cache.get(lineNumber) === domText) return false;

    // 화살표키 이동은 queueRender/focusLine을 거치므로, 이동 전에 contenteditable DOM에만
    // 남아 있는 최종 IME 문자열을 모델 캐시에 먼저 저장해야 한다. 그렇지 않으면 렌더링이
    // 이전 캐시 값으로 돌아가면서 방금 입력한 한글이 사라질 수 있다.
    commitLine(element);
    return true;
}

function finishPendingImeBeforeCaretNavigation(element) {
    if (!element || element.getAttribute?.('contenteditable') !== 'true') return false;

    if (state.rangeComposition) {
        const pending = state.rangeComposition;
        const lineNumber = Number(pending.lineNumber || element.dataset.line || state.currentLine || 1);
        const targetElement = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
        const finalText = targetElement?.getAttribute?.('contenteditable') === 'true'
            ? lineTextFromElement(targetElement)
            : pending.beforeText;
        const insertedText = changedTextBetween(pending.beforeText, finalText);

        if (finishRangeComposition(targetElement, lineNumber, insertedText)) {
            state.isComposing = false;
            state.compositionLine = null;
            clearPendingImeSelectionCollapse();
            reportCursorAndSelection(targetElement);
            return true;
        }
    }

    if (state.columnComposition) {
        const lineNumber = Number(element.dataset.line || state.compositionLine || state.currentLine || 1);
        if (finishColumnComposition(element, lineNumber)) {
            state.isComposing = false;
            state.compositionLine = null;
            clearPendingImeSelectionCollapse();
            reportCursorAndSelection(element);
            return true;
        }
    }

    if (state.isComposing) {
        commitLineForSave(element);
        clearPendingImeSelectionCollapse();
        return true;
    }

    commitDomLineBeforeCaretNavigation(element);
    return false;
}

function anchoredCaretRectForVerticalMove(element, lineNumber, caret, fallbackRect) {
    const anchor = verticalCaretVisualAnchor;
    if (!anchor ||
        anchor.line !== lineNumber ||
        anchor.column !== caret ||
        performance.now() - anchor.time > 2000 ||
        document.activeElement?.closest?.('.line-text') !== element) {
        return fallbackRect;
    }

    // Wrapped 줄 내부에서 ↑/↓를 계속 누르면 setCaret/focusLine이 scrollTop을 바꿀 수 있다.
    // 저장된 visual anchor는 viewport 좌표이므로, 스크롤 변화량만큼 보정하지 않으면
    // 다음 반복 이동이 현재 caret 위치보다 몇 visual line 아래/위를 기준으로 계산되어 건너뛰게 된다.
    const scrollDelta = scrollContainer.scrollTop - Number(anchor.scrollTop || 0);
    const top = anchor.top - scrollDelta;
    const left = anchor.left;

    return {
        left,
        right: left,
        top,
        bottom: top + anchor.height,
        height: anchor.height
    };
}

function rememberVerticalCaretVisualAnchor(line, column, left, top, height) {
    const safeHeight = Math.max(1, Number(height || state.lineHeight));
    verticalCaretVisualAnchor = {
        line,
        column,
        left,
        top,
        bottom: top + safeHeight,
        height: safeHeight,
        scrollTop: scrollContainer.scrollTop,
        time: performance.now()
    };
}

function clearVerticalCaretVisualAnchor() {
    verticalCaretVisualAnchor = null;
}

function visualLineBoundsForElement(element) {
    const textRect = element?.getBoundingClientRect?.();
    const rowRect = element?.closest?.('.line-row')?.getBoundingClientRect?.();

    if (!textRect && !rowRect) return null;
    if (!rowRect || rowRect.height <= 0) return textRect;
    if (!textRect || textRect.height <= 0) return rowRect;

    // word-wrap 상태에서는 실제 visual line 높이가 .line-text가 아니라
    // gutter를 포함한 .line-row 높이로 잡히는 경우가 있다.
    // ↑/↓ 이동의 세로 범위를 .line-text rect만으로 판단하면
    // 같은 원본 줄 안의 다음 wrap 줄을 건너뛰고 인접 원본 줄로 이동한다.
    return {
        left: textRect.left,
        right: textRect.right,
        top: Math.min(textRect.top, rowRect.top),
        bottom: Math.max(textRect.bottom, rowRect.bottom),
        width: textRect.width,
        height: Math.max(textRect.height, rowRect.height)
    };
}

function adjacentLogicalLineTarget(lineNumber, direction, preferredX, lineStep, fallbackColumn) {
    const targetLine = lineNumber + direction;
    if (targetLine < 1 || targetLine > state.lineCount) return null;

    const targetText = state.cache.get(targetLine) || '';
    const fallback = {
        line: targetLine,
        column: Math.min(Math.max(0, Number(fallbackColumn || 0)), targetText.length)
    };

    if (!state.wordWrap) {
        return fallback;
    }

    const targetElement = viewport.querySelector(`.line-text[data-line="${targetLine}"]`);
    if (!targetElement || targetElement.getAttribute('contenteditable') !== 'true') {
        return fallback;
    }

    const targetTextRect = targetElement.getBoundingClientRect();
    const targetBounds = visualLineBoundsForElement(targetElement);
    if (!targetTextRect || !targetBounds || targetBounds.height <= 0) {
        return fallback;
    }

    const x = Math.max(targetTextRect.left + 1, Math.min(preferredX, targetTextRect.right - 1));
    const y = direction < 0
        ? targetBounds.bottom - lineStep / 2
        : targetBounds.top + lineStep / 2;
    const column = offsetFromPointInElement(targetElement, x, y);
    const targetColumn = column === null ? fallback.column : column;
    const visualTop = direction < 0
        ? Math.max(targetBounds.top, targetBounds.bottom - lineStep)
        : targetBounds.top;
    return {
        line: targetLine,
        column: targetColumn,
        visualLeft: x,
        visualTop,
        visualHeight: lineStep
    };
}

function moveCaretVertical(element, direction, extendSelection = false) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return false;
    if (finishPendingImeBeforeCaretNavigation(element)) return true;

    const lineNumber = Number(element.dataset.line || state.currentLine || 1);
    const text = lineTextFromElement(element);
    const caret = Math.max(0, Math.min(getCaretOffset(element), text.length));

    const anchor = extendSelection
        ? (state.selectionAnchor || { line: lineNumber, column: caret })
        : null;

    let target = null;

    let caretRect = caretRectForOffset(element, caret);
    let preferredX = null;
    let lineStep = state.lineHeight;
    if (!caretRect) {
        target = adjacentLogicalLineTarget(lineNumber, direction, caret, lineStep, caret);
    } else {
        caretRect = anchoredCaretRectForVerticalMove(element, lineNumber, caret, caretRect);
        const elementRect = element.getBoundingClientRect();
        const visualBounds = visualLineBoundsForElement(element) || elementRect;
        const styles = window.getComputedStyle(element);
        const parsedLineHeight = Number.parseFloat(styles.lineHeight);
        lineStep = Math.max(1, Number.isFinite(parsedLineHeight) ? parsedLineHeight : (caretRect.height || state.lineHeight));
        preferredX = Math.max(elementRect.left + 1, Math.min(caretRect.left, elementRect.right - 1));
        const targetY = direction < 0
            ? caretRect.top - lineStep / 2
            : caretRect.bottom + lineStep / 2;

        if (targetY >= visualBounds.top - 1 && targetY <= visualBounds.bottom + 1) {
            const targetColumn = offsetFromPointInElement(element, preferredX, targetY, caretRect, direction, lineStep);
            if (targetColumn !== null) {
                target = {
                    line: lineNumber,
                    column: targetColumn,
                    visualLeft: preferredX,
                    visualTop: targetY - lineStep / 2,
                    visualHeight: lineStep
                };
            }
        }

        if (!target) {
            target = adjacentLogicalLineTarget(lineNumber, direction, preferredX ?? caret, lineStep, caret);
        }
    }

    if (target) {
        if (Number.isFinite(target.visualTop) && Number.isFinite(target.visualLeft)) {
            rememberVerticalCaretVisualAnchor(
                target.line,
                target.column,
                target.visualLeft,
                target.visualTop,
                target.visualHeight ?? lineStep);
        } else {
            clearVerticalCaretVisualAnchor();
        }

        if (extendSelection) {
            state.selectionAnchor = anchor;
            state.selection = (anchor.line === target.line && anchor.column === target.column)
                ? null
                : { start: anchor, end: target };
            state.currentLine = target.line;
            state.currentColumn = target.column + 1;
            syncCustomSelectionClass();
            queueRender(true);
            setTimeout(() => focusLine(target.line, target.column, 3 * state.lineHeight), 0);
        } else {
            state.selection = null;
            state.selectionAnchor = { line: target.line, column: target.column };
            state.currentLine = target.line;
            state.currentColumn = target.column + 1;
            syncCustomSelectionClass();
            if (target.line === lineNumber) {
                setCaret(element, target.column, 3 * state.lineHeight);
            } else {
                focusLine(target.line, target.column, 3 * state.lineHeight);
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

    if (isComposing && state.rangeComposition) {
        // 다중 줄 선택 IME 조합 중에는 모델/DOM 재렌더를 건드리지 않는다.
        // 최종 조합 문자열은 compositionend에서 원래 선택 영역에 한 번에 반영한다.
    } else if (isComposing && state.columnComposition) {
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
        if (!state.columnComposition && !state.rangeComposition) {
            post({ type: 'lineChanged', lineNumber, text, isComposing: true });
            post({ type: 'contentChanged', isComposing: true });
        }
        reportCursorAndSelection(element);
        if (state.wordWrap && !state.rangeComposition) {
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

    if (state.rangeComposition && finishRangeComposition(element, lineNumber)) {
        state.isComposing = false;
        state.compositionLine = null;
        reportCursorAndSelection(element);
        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        return true;
    }

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

function clampScrollTop(scrollTop) {
    const maxScrollTop = Math.max(0, totalVirtualHeight() - scrollContainer.clientHeight);
    return Math.min(maxScrollTop, Math.max(0, Number(scrollTop || 0)));
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
            scrollContainer.scrollTop = clampScrollTop(scrollContainer.scrollTop + delta);
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

    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            replaceSelectionWith(sel, '\n');
            return;
        }
    }

    const preferStateCaret = options?.preferStateCaret === true;
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
    state.dirtyLines.set(nextLineNumber, 'add');
    state.selection = null;
    clearCustomSelectionVisuals();
    syncCustomSelectionClass();
    setupVirtualHeight();
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
                    state.dirtyLines.set(nextLineNumber, 'add');
                    post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
                }
                post({ type: 'contentChanged' });
            } finally {
                endEditTransaction();
            }
            state.lineCount += insertedCount;
            state.currentLine = lastLineNumber;
            state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
            setupVirtualHeight();
            focusLine(lastLineNumber, state.currentColumn - 1);
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
                state.dirtyLines.set(nextLineNumber, 'add');
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
        setTimeout(() => focusLine(lastLineNumber, parts[parts.length - 1]?.length || 0), 0);
        return;
    }

    insertPlainTextByModel(element, normalized);
}

function isModelRepeatKey(event) {
    if (!event) return false;
    if (event.key === 'Enter') {
        return !event.ctrlKey && !event.metaKey && !event.altKey;
    }

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
    if (event.key === 'Enter') return 'Enter';
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
    if (state.repeatEdit.continuousTimer) {
        clearTimeout(state.repeatEdit.continuousTimer);
        state.repeatEdit.continuousTimer = 0;
    }
    state.repeatEdit.pending = null;
    state.repeatEdit.continuousKey = null;
}

function repeatEditDelayFromNow() {
    const now = performance.now();
    const boundaryWait = Math.max(0, state.repeatEdit.lineBoundaryUntil - now);
    const intervalWait = Math.max(0, state.repeatEdit.intervalMs - (now - state.repeatEdit.lastRunAt));
    return Math.max(boundaryWait, intervalWait);
}

function scheduleContinuousModelRepeatEdit(key, delayMs) {
    if (state.repeatEdit.continuousTimer) {
        clearTimeout(state.repeatEdit.continuousTimer);
        state.repeatEdit.continuousTimer = 0;
    }

    state.repeatEdit.continuousTimer = setTimeout(() => {
        state.repeatEdit.continuousTimer = 0;
        if (state.repeatEdit.continuousKey !== key || state.readOnly || state.isComposing) {
            return;
        }

        const wait = repeatEditDelayFromNow();
        if (wait > 0) {
            scheduleContinuousModelRepeatEdit(key, wait);
            return;
        }

        state.repeatEdit.lastRunAt = performance.now();
        runModelRepeatEdit(key);
        scheduleContinuousModelRepeatEdit(key, state.repeatEdit.intervalMs);
    }, Math.max(0, Number(delayMs || 0)));
}

function scheduleModelRepeatEdit(key, isRepeat) {
    if (state.readOnly || state.isComposing) return;

    // Backspace/Delete/Enter are handled from one cancellable timer instead of
    // browser key-repeat events.  This prevents queued keydown events from
    // continuing to delete or split lines after the physical key is released.
    if (state.repeatEdit.continuousKey === key) {
        return;
    }

    clearPendingRepeatEdit();
    state.repeatEdit.continuousKey = key;
    state.repeatEdit.pending = null;
    state.repeatEdit.lastRunAt = performance.now();
    runModelRepeatEdit(key);
    scheduleContinuousModelRepeatEdit(key, state.repeatEdit.continuousInitialDelayMs);
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
function moveCaretHorizontal(element, direction, extendSelection = false) {
    if (!element || element.getAttribute('contenteditable') !== 'true') return false;
    if (finishPendingImeBeforeCaretNavigation(element)) return true;
    clearVerticalCaretVisualAnchor();

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
            target = { line: lineNumber, column: graphemeDeleteStart(text, caret) };
        } else if (lineNumber > 1) {
            const previousText = state.cache.get(lineNumber - 1) || '';
            target = { line: lineNumber - 1, column: previousText.length };
        }
    } else {
        if (caret < text.length) {
            target = { line: lineNumber, column: graphemeDeleteEnd(text, caret) };
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
        setTimeout(() => focusLine(target.line, target.column, 3 * state.lineHeight), 0);
    } else {
        state.selection = null;
        state.selectionAnchor = { line: target.line, column: target.column };
        syncCustomSelectionClass();
        focusLine(target.line, target.column, 3 * state.lineHeight);
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

    if (key === 'Enter') {
        const elementLineNumber = Number(element.dataset.line || 0);
        splitCurrentLine(element, { preferStateCaret: elementLineNumber !== state.currentLine });
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
    setTimeout(() => focusLine(lineNumber, text.length, 3 * state.lineHeight), 0);
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
    setTimeout(() => focusLine(lineNumber - 1, previous.length, 3 * state.lineHeight), 0);
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
        setTimeout(() => {
            focusLine(state.currentLine, nextCol);
            reportCursorAndSelection();
        }, 0);
    }
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
    beginEditTransaction();
    try {
        post({ type: 'lineChanged', lineNumber: start.line, text: newLines[0] });
        for (let i = end.line; i > start.line; i--) {
            post({ type: 'deleteLine', lineNumber: i });
        }
        for (let i = 1; i < newLines.length; i++) {
            post({ type: 'insertLine', lineNumber: start.line + i, text: newLines[i] });
        }
        post({ type: 'contentChanged' });
    } finally {
        endEditTransaction();
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

const {
    beginColumnComposition,
    changedTextBetween,
    clearPendingImeSelectionCollapse,
    finishColumnComposition,
    finishRangeComposition,
    isLineInColumnComposition,
    isPendingImeSelectionCollapseFor,
    replaceSelectionForCompositionStart,
    updateColumnCompositionPreview
} = createEditorCompositionHandlers({
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
    finishRangeComposition,
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
