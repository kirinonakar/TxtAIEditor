import { scrollContainer, viewport } from './editor-dom.js';
import {
    configureEditorCoreRuntime,
    lineAt,
    lineTop,
    post,
    queueRender,
    requestLines,
    setupVirtualHeight,
    state
} from './editor-core.js';
const {
    findEditablePreviewBlockContaining,
    initializeMermaid,
    inlineMarkdown,
    listItemInfo,
    renderMermaidBlocks,
    renderPreviewLineAt
} = await import(`./markdown-preview-renderer.js?v=${encodeURIComponent(window.__TxtAIEditorVersion || Date.now())}`);
import {
    drawEditableSelectionOverlays,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    focusImeBypassTextarea,
    getCaretOffset,
    isLineInColumnComposition,
    setCaret
} from './editor-commands.js';
import { createFindReplaceController } from './editor-find-replace.js';
import { createEditorRenderer } from './editor-renderer.js';
import { bindEditorEvents } from './editor-events.js';
import { createHostMessageHandler } from './editor-host-messages.js';
import { createLivePreviewPositionResolver } from './editor-live-preview-position.js';
import { bindPrintSupport } from './editor-print.js';

const renderer = createEditorRenderer({
    findEditablePreviewBlockContaining,
    getCaretOffset,
    renderMermaidBlocks,
    renderPreviewLineAt,
    setCaret
});

const {
    clearPendingInlineLivePreviewFocus,
    focusLineWithRetry,
    render
} = renderer;

configureEditorCoreRuntime({
    drawEditableSelectionOverlays,
    focusImeBypassTextarea,
    focusLine: focusLineWithRetry,
    getCaretOffset,
    hasCustomSelection,
    isLineInColumnComposition,
    normalizeSelection,
    render
});

initializeMermaid(new URLSearchParams(window.location.search).get('theme') || 'Dark');
bindPrintSupport();

const findReplaceController = createFindReplaceController({ revealLine });
const { openFindPanel } = findReplaceController;
const getPreciseLivePreviewPosition = createLivePreviewPositionResolver({
    findEditablePreviewBlockContaining,
    inlineMarkdown,
    listItemInfo
});

const editorEvents = bindEditorEvents({
    findReplaceController,
    openFindPanel,
    getPreciseLivePreviewPosition,
    renderer
});

const handleCsharpMessage = createHostMessageHandler({
    revealLine,
    revealHexOffset,
    openFindPanel,
    suppressNativePaste: editorEvents.suppressNativePaste,
    syncHostScroll: editorEvents.syncHostScroll,
    findEditablePreviewBlockContaining,
    clearPendingInlineLivePreviewFocus
});

const REVEAL_SCROLL_TOLERANCE = 1;
const REVEAL_SCROLL_RETRY_DELAY_MS = 40;
const REVEAL_SCROLL_MAX_RETRIES = 8;
const HEX_BYTES_PER_ROW = 16;
const HEX_WIDE_LINE_COUNT_THRESHOLD = 268435457;
let revealScrollToken = 0;

function maxScrollTop() {
    return Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
}

function clampScrollTop(value) {
    return Math.min(maxScrollTop(), Math.max(0, Number(value || 0)));
}

function centeredScrollTopForLine(lineNumber) {
    return clampScrollTop(lineTop(lineNumber) - Math.floor(scrollContainer.clientHeight / 2));
}

function postEditorScroll(scrollTop = scrollContainer.scrollTop) {
    if (!state.scrollSyncEnabled) return;

    const firstVisible = lineAt(scrollTop);
    post({
        type: 'editorScroll',
        firstLine: firstVisible,
        offset: scrollTop - lineTop(firstVisible)
    });
}

function alignRenderedLineInView(lineNumber) {
    const row = viewport.querySelector(`.line-row[data-line="${lineNumber}"]`);
    if (!row) {
        return { found: false, adjusted: false, loading: false };
    }

    const rowRect = row.getBoundingClientRect();
    const containerRect = scrollContainer.getBoundingClientRect();
    const rowTopInContent = scrollContainer.scrollTop + rowRect.top - containerRect.top;
    const targetScrollTop = rowRect.height >= scrollContainer.clientHeight
        ? rowTopInContent
        : rowTopInContent - Math.floor((scrollContainer.clientHeight - rowRect.height) / 2);
    const nextScrollTop = clampScrollTop(targetScrollTop);
    const adjusted = Math.abs(scrollContainer.scrollTop - nextScrollTop) > REVEAL_SCROLL_TOLERANCE;

    if (adjusted) {
        editorEvents.beginProgrammaticScroll(nextScrollTop);
        postEditorScroll(nextScrollTop);
    }

    return {
        found: true,
        adjusted,
        loading: !!row.querySelector('.line-text.loading')
    };
}

function scheduleRevealLineAlignment(lineNumber, token, retries = REVEAL_SCROLL_MAX_RETRIES) {
    requestAnimationFrame(() => {
        if (token !== revealScrollToken) return;

        const result = alignRenderedLineInView(lineNumber);
        if (retries > 0 && (!result.found || result.adjusted || result.loading)) {
            setTimeout(
                () => scheduleRevealLineAlignment(lineNumber, token, retries - 1),
                REVEAL_SCROLL_RETRY_DELAY_MS);
        }
    });
}

function revealLine(lineNumber, indexOfMatch = 0, matchLength = 0, query = '', preventFocus = false) {
    const safeLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    state.currentLine = safeLine;
    state.currentColumn = Math.max(1, Number(indexOfMatch || 0) + 1);
    state.activeSearch = query
        ? { lineNumber: safeLine, indexOfMatch, matchLength, query }
        : null;

    const targetScrollTop = centeredScrollTopForLine(safeLine);
    editorEvents.beginProgrammaticScroll(targetScrollTop);
    postEditorScroll();

    requestLines(Math.max(1, safeLine - state.overscan), state.overscan * 2 + 1);
    queueRender(true);
    scheduleRevealLineAlignment(safeLine, ++revealScrollToken);
    if (!preventFocus && state.language !== 'hex') {
        focusLineWithRetry(safeLine, Math.max(0, indexOfMatch || 0));
    }
}

function revealHexOffset(offset) {
    const safeOffset = Math.max(0, Math.floor(Number(offset || 0)));
    const lineNumber = Math.min(state.lineCount, Math.floor(safeOffset / HEX_BYTES_PER_ROW) + 2);
    const byteIndex = safeOffset % HEX_BYTES_PER_ROW;
    const column = hexColumnForByteIndex(byteIndex);

    state.hexSelection = null;
    state.hexSelectionAnchorOffset = safeOffset;
    state.hexCursorOffset = safeOffset;
    state.currentLine = lineNumber;
    state.currentColumn = column + 1;

    revealLine(lineNumber, column, 0, '', true);
    queueRender(true);
    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    post({ type: 'selectionResult', text: '', startLine: 0, endLine: 0, hexOffset: null, hexLength: 0 });
}

function hexColumnForByteIndex(byteIndex) {
    const offsetWidth = state.lineCount > HEX_WIDE_LINE_COUNT_THRESHOLD ? 16 : 8;
    const safeByteIndex = Math.max(0, Math.min(Number(byteIndex || 0), HEX_BYTES_PER_ROW - 1));
    return offsetWidth + 2 + (safeByteIndex * 3) + (safeByteIndex >= 8 ? 1 : 0);
}

if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', event => {
        const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        handleCsharpMessage(msg);
    });
}

setupVirtualHeight();
post({ type: 'ready', virtualized: true });
