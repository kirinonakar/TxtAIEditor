import { scrollContainer } from './editor-dom.js';
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
    openFindPanel,
    suppressNativePaste: editorEvents.suppressNativePaste,
    syncHostScroll: editorEvents.syncHostScroll,
    findEditablePreviewBlockContaining,
    clearPendingInlineLivePreviewFocus
});

function revealLine(lineNumber, indexOfMatch = 0, matchLength = 0, query = '', preventFocus = false) {
    const safeLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    state.currentLine = safeLine;
    state.currentColumn = Math.max(1, Number(indexOfMatch || 0) + 1);
    state.activeSearch = query
        ? { lineNumber: safeLine, indexOfMatch, matchLength, query }
        : null;

    const targetScrollTop = Math.max(0, lineTop(safeLine) - Math.floor(scrollContainer.clientHeight / 2));
    editorEvents.beginProgrammaticScroll(targetScrollTop);

    if (state.scrollSyncEnabled) {
        const firstVisible = lineAt(targetScrollTop);
        const offset = targetScrollTop - lineTop(firstVisible);
        post({
            type: 'editorScroll',
            firstLine: firstVisible,
            offset: offset
        });
    }

    requestLines(Math.max(1, safeLine - state.overscan), state.overscan * 2 + 1);
    queueRender(true);
    if (!preventFocus) {
        focusLineWithRetry(safeLine, Math.max(0, indexOfMatch || 0));
    }
}

if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', event => {
        const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        handleCsharpMessage(msg);
    });
}

setupVirtualHeight();
post({ type: 'ready', virtualized: true });
