import {
    reportCursorAndSelection,
    selectionController,
    state,
    totalVirtualHeight,
    viewportController
} from './editor-core.js';
import { scrollContainer, viewport } from './editor-dom.js';
import {
    focusLine,
    lineTextFromElement,
    setCaret
} from './editor-caret.js';

let postEditFocusToken = 0;
let scrollPreservingFocusToken = 0;
let postEditCaretRestoreToken = 0;

export function queuePostEditFocus(callback) {
    const token = ++postEditFocusToken;
    setTimeout(() => {
        if (token === postEditFocusToken) {
            callback();
        }
    }, 0);
}

export function queuePostEditCaretRestore(lineNumber, columnZeroBased, scrollMargin = 0) {
    const targetLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    const targetColumn = Math.max(0, Number(columnZeroBased || 0));
    const token = ++postEditCaretRestoreToken;

    function run(attempt = 0) {
        if (token !== postEditCaretRestoreToken) return;

        state.currentLine = targetLine;
        state.currentColumn = targetColumn + 1;
        selectionController.anchor = { line: targetLine, column: targetColumn };

        const element = viewport.querySelector(`.line-text[data-line="${targetLine}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            const safeColumn = Math.min(targetColumn, lineTextFromElement(element).length);
            setCaret(element, safeColumn, scrollMargin);
            reportCursorAndSelection(null);
            if (attempt < 2) {
                requestAnimationFrame(() => run(attempt + 1));
            }
            return;
        }

        if (attempt === 0) {
            focusLine(targetLine, targetColumn, scrollMargin);
        }

        if (attempt < 8) {
            requestAnimationFrame(() => run(attempt + 1));
            return;
        }

        focusLine(targetLine, targetColumn, scrollMargin);
        reportCursorAndSelection(null);
    }

    queuePostEditFocus(() => run());
}

export function clampScrollTop(scrollTop) {
    const preservedHeight = viewportController.preservedContentHeight(scrollContainer.clientHeight);
    const maxScrollTop = Math.max(0, Math.max(totalVirtualHeight(), preservedHeight) - scrollContainer.clientHeight);
    return Math.min(maxScrollTop, Math.max(0, Number(scrollTop || 0)));
}

export function restoreScrollTop(scrollTop) {
    scrollContainer.scrollTop = clampScrollTop(scrollTop);
}

export function queueFocusPreservingScroll(lineNumber, columnZeroBased, scrollTop, scrollLeft = null) {
    const token = ++scrollPreservingFocusToken;
    const restore = () => {
        restoreScrollTop(scrollTop);
        if (scrollLeft !== null) scrollContainer.scrollLeft = scrollLeft;
    };

    function run(attempt = 0) {
        if (token !== scrollPreservingFocusToken) return;

        restore();
        const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            setCaret(element, columnZeroBased, 0, true, scrollLeft === null);
            restore();
            if (scrollLeft === null && attempt < 2) {
                requestAnimationFrame(() => run(attempt + 1));
            }
            return;
        }

        if (attempt < 8) {
            requestAnimationFrame(() => run(attempt + 1));
            return;
        }

        // A preserved viewport must not reveal an off-screen editing target.
        if (scrollLeft === null) focusLine(lineNumber, columnZeroBased);
        restore();
    }

    queuePostEditFocus(() => run());
}

export function cancelPostEditFocusFollowUps() {
    postEditFocusToken++;
    scrollPreservingFocusToken++;
    postEditCaretRestoreToken++;
}
