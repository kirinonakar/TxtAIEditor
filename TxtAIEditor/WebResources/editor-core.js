import { contextMenu, scrollContainer, viewport, virtualSpacer } from './editor-dom.js';
import { CsvInteractionController } from './editor-csv-interaction-state.js';
import { EditorDocumentCache } from './editor-document-cache.js';
import { DragDropController } from './editor-drag-drop-state.js';
import { HostRequestController } from './editor-host-request-state.js';
import { ImeController } from './editor-ime-state.js';
import { SearchController } from './editor-search-state.js';
import { SelectionController } from './editor-selection-state.js';
import { ViewportController } from './editor-viewport-state.js';
import {
    CsvTableMode,
    EditorModeCoordinator,
    HexEditorMode,
    TextEditorMode
} from './editor-modes.js';

const MAX_RENDER_CHARS = 20000;
const MIN_BATCH_SIZE = 100;
const PREFETCH_AHEAD = 200;
const HEX_PREFETCH_AHEAD = 80;
const HEX_RENDER_OVERSCAN = 48;
const BROWSER_SCROLL_HEIGHT_LIMIT = 12000000;
const HEX_CACHE_RETAIN_LINES = 512;
const HEX_SELECTION_CACHE_RETAIN_LIMIT = 2048;
// Keep this aligned with EditorInitialCachePolicy.FullDocumentLineLimit.
const FULL_DOCUMENT_RENDER_LINE_LIMIT = 1000;

const runtime = {
    drawEditableSelectionOverlays: () => { },
    focusImeBypassTextarea: () => { },
    focusLine: () => { },
    getCaretOffset: () => 0,
    hasCustomSelection: () => false,
    isLineInColumnComposition: () => false,
    normalizeSelection: () => null,
    render: () => { }
};

function configureEditorCoreRuntime(deps) {
    Object.assign(runtime, deps || {});
}

const documentCache = new EditorDocumentCache();
const csvInteractionController = new CsvInteractionController();
const dragDropController = new DragDropController();
const hostRequestController = new HostRequestController();
const imeController = new ImeController({
    onRangeCompositionCleared: () => document.body.classList.remove('range-composition-active')
});
const selectionController = new SelectionController();
const searchController = new SearchController();
const viewportController = new ViewportController();
const hexEditorMode = new HexEditorMode({
    renderOverscan: HEX_RENDER_OVERSCAN,
    prefetchAhead: HEX_PREFETCH_AHEAD
});
const csvTableMode = new CsvTableMode();
const modeCoordinator = new EditorModeCoordinator({
    textMode: new TextEditorMode(),
    hexMode: hexEditorMode,
    csvMode: csvTableMode
});

const state = {
    lineCount: 1,
    cache: documentCache,
    currentLine: 1,
    currentColumn: 1,
    readOnly: false,
    wordWrap: false,
    showDirtyLines: true,
    syntaxHighlighting: true,
    language: 'plaintext',
    tabSize: 4,
    initialized: false,
    cacheVersion: 0,
    documentVersion: 0,
    hostDocumentId: '',
    hostDocumentVersion: 0,
    viewId: '',
    messageSequence: 0,
    pendingLinePatchBatch: null,
    textOperationLocked: false,
    textOperationPreviousReadOnly: false,
    pendingLineActions: [],
    autocompleteOnEnter: true,
    autocompleteOnTab: true,
    snippets: [],
    scrollSyncEnabled: true,
    autocompleteWords: [],
    inlineLivePreviewEnabled: false,
    livePreviewBaseHref: '',
    livePreviewLocalResourceVersion: '0',
    inlineLivePreviewSourceLine: null,
    inlineLivePreviewEditableBlock: null,
    isSplitView: false,
    suppressNextBeforeInputType: null,
    lastManualDeleteAt: 0,
    editingLine: null,
    lastDeleteKeyDown: null,
    dirtyLines: new Map(),
    longLineProtectionFormat: '... too long ({0} characters total)'
};

function activeEditorMode() {
    return modeCoordinator.resolve({
        language: state.language
    });
}

function post(msg) {
    const baseVersion = state.hostDocumentVersion;
    const isMutation = ['edit', 'lineChanged', 'lineEdit', 'rangeEdit', 'insertLine', 'splitLine',
        'mergeLineWithPrevious', 'deleteLine', 'hexEdit', 'replaceAll'].includes(msg?.type);
    // Composition previews are intentionally ignored by the host model until
    // compositionend. Do not advance the optimistic host version for them;
    // otherwise the final committed edit is sent with a future baseVersion and
    // the host rejects it, triggering a full document resynchronization.
    const isOptimisticallyAppliedMutation = isMutation &&
        msg?.type !== 'replaceAll' &&
        msg?.isComposing !== true;
    if (msg?.type === 'contentChanged') {
        state.documentVersion++;
        searchController.invalidateDocument();
    }

    const sequence = state.hostDocumentId ? ++state.messageSequence : 0;
    const outgoing = state.hostDocumentId
        ? {
            protocolVersion: 1,
            documentId: state.hostDocumentId,
            viewId: state.viewId,
            documentVersion: state.hostDocumentVersion,
            sequence,
            ...(isMutation
                ? { editId: sequence, baseVersion }
                : {}),
            ...msg
        }
        : msg;

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(outgoing);
    }

    // Each mutation is applied optimistically in the DOM. Advance the expected
    // host version per command so a transaction containing several edits still
    // carries a contiguous base-version chain.
    if (isOptimisticallyAppliedMutation && state.hostDocumentId) {
        state.hostDocumentVersion = baseVersion + 1;
    }
}

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

const graphemeSegmenter = typeof Intl !== 'undefined' && Intl.Segmenter
    ? new Intl.Segmenter('en', { granularity: 'grapheme' })
    : null;

function graphemeDeleteStart(text, caret) {
    if (caret <= 0) return 0;
    if (graphemeSegmenter) {
        try {
            const segment = graphemeSegmenter.segment(text).containing(caret - 1);
            if (segment) return segment.index;
        } catch { }
    }
    let pos = caret - 1;
    if (pos >= 0) {
        const c = text.charCodeAt(pos);
        if (c >= 0xDC00 && c <= 0xDFFF && pos > 0) {
            const p = text.charCodeAt(pos - 1);
            if (p >= 0xD800 && p <= 0xDBFF) pos--;
        }
    }
    return pos;
}

function graphemeDeleteEnd(text, caret) {
    if (caret >= text.length) return text.length;
    if (graphemeSegmenter) {
        try {
            const segment = graphemeSegmenter.segment(text).containing(caret);
            if (segment) return segment.index + segment.segment.length;
        } catch { }
    }
    let pos = caret;
    if (pos < text.length) {
        const c = text.charCodeAt(pos);
        if (c >= 0xD800 && c <= 0xDBFF && pos + 1 < text.length) {
            const n = text.charCodeAt(pos + 1);
            if (n >= 0xDC00 && n <= 0xDFFF) pos += 2;
            else pos++;
        } else {
            pos++;
        }
    }
    return pos;
}

function parseHexColor(value) {
    if (!value || typeof value !== 'string') return null;
    const match = value.trim().match(/^#?([0-9a-f]{3}|[0-9a-f]{6})$/i);
    if (!match) return null;
    let hex = match[1];
    if (hex.length === 3) hex = hex.split('').map(ch => ch + ch).join('');
    return {
        r: parseInt(hex.slice(0, 2), 16),
        g: parseInt(hex.slice(2, 4), 16),
        b: parseInt(hex.slice(4, 6), 16)
    };
}

function colorToHex(color) {
    const part = value => value.toString(16).padStart(2, '0');
    return `#${part(color.r)}${part(color.g)}${part(color.b)}`;
}

function relativeLuminance(color) {
    const normalize = value => {
        const channel = value / 255;
        return channel <= 0.03928 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * normalize(color.r) + 0.7152 * normalize(color.g) + 0.0722 * normalize(color.b);
}

function contrastRatio(a, b) {
    const l1 = relativeLuminance(a);
    const l2 = relativeLuminance(b);
    return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

function readableForegroundFor(background) {
    const white = { r: 255, g: 255, b: 255 };
    const black = { r: 17, g: 17, b: 17 };
    return contrastRatio(background, white) >= contrastRatio(background, black) ? '#ffffff' : '#111111';
}

function resolveReadableColor(backgroundValue, foregroundValue, fallbackForeground) {
    const background = parseHexColor(backgroundValue);
    const foreground = parseHexColor(foregroundValue);
    const fallback = parseHexColor(fallbackForeground) || { r: 212, g: 212, b: 212 };
    if (!background) return foregroundValue || fallbackForeground;
    if (foreground && contrastRatio(background, foreground) >= 4.5) return colorToHex(foreground);
    if (contrastRatio(background, fallback) >= 4.5) return colorToHex(fallback);
    return readableForegroundFor(background);
}

function snapCssPixelsToDevicePixels(value) {
    const dpr = Number(window.devicePixelRatio || 1);
    if (!Number.isFinite(dpr) || dpr <= 0) return value;
    return Math.ceil(value * dpr) / dpr;
}

function roundCssPixelsToDevicePixels(value) {
    const dpr = Number(window.devicePixelRatio || 1);
    if (!Number.isFinite(dpr) || dpr <= 0) return value;
    return Math.round(value * dpr) / dpr;
}

function applyOptions(msg) {
    const theme = msg.theme || 'Dark';
    const bg = msg.customBackgroundColor || (theme === 'PastelDark' ? '#24273a' : (theme === 'Light' ? '#ffffff' : '#1e1e1e'));
    const preferredFg = msg.customForegroundColor || (theme === 'PastelDark' ? '#cad3f5' : (theme === 'Light' ? '#111111' : '#d4d4d4'));
    const fg = resolveReadableColor(bg, preferredFg, theme === 'PastelDark' ? '#cad3f5' : (theme === 'Light' ? '#111111' : '#d4d4d4'));
    const fontSize = Number(msg.fontSize || 14);
    const baseLineHeight = Math.max(18, Math.ceil(fontSize + 8));
    const previousLineHeight = viewportController.setLineHeight(
        snapCssPixelsToDevicePixels(baseLineHeight),
        state.lineCount);
    state.tabSize = Number(msg.tabSize || 4);
    state.readOnly = !!msg.readOnly;
    hexEditorMode.setEditable(msg.hexEditable);
    state.wordWrap = !!msg.wordWrap;
    state.syntaxHighlighting = msg.hasOwnProperty('syntaxHighlighting') ? !!msg.syntaxHighlighting : true;
    state.showDirtyLines = msg.hasOwnProperty('showDirtyLines') ? !!msg.showDirtyLines : true;
    if (!state.showDirtyLines) {
        state.dirtyLines.clear();
    }
    state.bracketPairColorization = msg.hasOwnProperty('bracketPairColorization') ? !!msg.bracketPairColorization : true;
    state.autocompleteOnEnter = msg.hasOwnProperty('autocompleteOnEnter') ? !!msg.autocompleteOnEnter : true;
    state.autocompleteOnTab = msg.hasOwnProperty('autocompleteOnTab') ? !!msg.autocompleteOnTab : true;

    document.documentElement.style.setProperty('--bg', bg);
    document.documentElement.style.setProperty('--fg', fg);
    document.documentElement.style.setProperty('--gutter-bg', theme === 'PastelDark' ? '#1e2030' : (theme === 'Light' ? '#f3f3f3' : '#252526'));
    document.documentElement.style.setProperty('--gutter-fg', theme === 'PastelDark' ? '#a5adcb' : (theme === 'Light' ? '#6b6b6b' : '#858585'));
    document.documentElement.style.setProperty('--selection', theme === 'PastelDark' ? 'rgba(198, 160, 246, 0.3)' : (theme === 'Light' ? 'rgba(0, 95, 184, 0.28)' : 'rgba(0, 120, 212, 0.38)'));
    document.documentElement.style.setProperty('--selection-foreground', fg);
    document.documentElement.style.setProperty('--preview-code-bg', theme === 'PastelDark' ? '#1e2030' : (theme === 'Light' ? '#f3f5f7' : '#2d2d2d'));
    document.documentElement.style.setProperty('--font-size', `${fontSize}px`);
    document.documentElement.style.setProperty('--font-family', msg.fontFamily || 'Consolas, "Courier New", monospace');
    document.documentElement.style.setProperty('--line-height', `${viewportController.lineHeight}px`);
    document.documentElement.style.setProperty('--wrap', state.wordWrap ? 'break-spaces' : 'pre');
    document.body.classList.toggle('wrap-enabled', state.wordWrap);
    document.body.classList.toggle('dirty-lines-hidden', !state.showDirtyLines);

    const replaceRow = document.getElementById('replace-row');
    if (replaceRow) {
        replaceRow.style.display = state.readOnly ? 'none' : 'flex';
    }
    const replaceActionsRow = document.getElementById('replace-actions-row');
    if (replaceActionsRow) {
        replaceActionsRow.style.display = state.readOnly ? 'none' : 'flex';
    }

    // Update syntax highlighting token variables dynamically based on Light / Dark theme
    if (theme === 'Light') {
        document.documentElement.style.setProperty('--token-comment', '#008000');
        document.documentElement.style.setProperty('--token-keyword', '#0000ff');
        document.documentElement.style.setProperty('--token-control', '#af00db');
        document.documentElement.style.setProperty('--token-string', '#a31515');
        document.documentElement.style.setProperty('--token-number', '#098658');
        document.documentElement.style.setProperty('--token-type', '#267f99');
        document.documentElement.style.setProperty('--token-function', '#795e26');
        document.documentElement.style.setProperty('--token-variable', '#001080');
        document.documentElement.style.setProperty('--token-operator', '#111111');
        document.documentElement.style.setProperty('--token-punctuation', '#3b3b3b');
        document.documentElement.style.setProperty('--token-tag', '#800000');
        document.documentElement.style.setProperty('--token-attr', '#ff0000');
        document.documentElement.style.setProperty('--bracket-depth-0', '#111111');
        document.documentElement.style.setProperty('--bracket-depth-1', '#0000ff');
        document.documentElement.style.setProperty('--bracket-depth-2', '#795e26');
        document.documentElement.style.setProperty('--bracket-depth-3', '#a31515');
        document.documentElement.style.setProperty('--bracket-depth-4', '#267f99');
        document.documentElement.style.setProperty('--bracket-depth-5', '#af00db');
        document.documentElement.style.setProperty('--hex-blue', '#0067c0');
        document.documentElement.style.setProperty('--hex-data-even', '#111111');
        document.documentElement.style.setProperty('--hex-data-odd', '#707070');
    } else if (theme === 'PastelDark') {
        document.documentElement.style.setProperty('--token-comment', '#939ab7');
        document.documentElement.style.setProperty('--token-keyword', '#c6a0f6');
        document.documentElement.style.setProperty('--token-control', '#f5bde6');
        document.documentElement.style.setProperty('--token-string', '#a6da95');
        document.documentElement.style.setProperty('--token-number', '#f5a97f');
        document.documentElement.style.setProperty('--token-type', '#eed49f');
        document.documentElement.style.setProperty('--token-function', '#8aadf4');
        document.documentElement.style.setProperty('--token-variable', '#cad3f5');
        document.documentElement.style.setProperty('--token-operator', '#91d7e3');
        document.documentElement.style.setProperty('--token-punctuation', '#8bd5ca');
        document.documentElement.style.setProperty('--token-tag', '#c6a0f6');
        document.documentElement.style.setProperty('--token-attr', '#eed49f');
        document.documentElement.style.setProperty('--bracket-depth-0', '#cad3f5');
        document.documentElement.style.setProperty('--bracket-depth-1', '#f5bde6');
        document.documentElement.style.setProperty('--bracket-depth-2', '#8aadf4');
        document.documentElement.style.setProperty('--bracket-depth-3', '#eed49f');
        document.documentElement.style.setProperty('--bracket-depth-4', '#a6da95');
        document.documentElement.style.setProperty('--bracket-depth-5', '#c6a0f6');
        document.documentElement.style.setProperty('--hex-blue', '#8aadf4');
        document.documentElement.style.setProperty('--hex-data-even', '#cad3f5');
        document.documentElement.style.setProperty('--hex-data-odd', '#939ab7');
    } else {
        document.documentElement.style.setProperty('--token-comment', '#6a9955');
        document.documentElement.style.setProperty('--token-keyword', '#569cd6');
        document.documentElement.style.setProperty('--token-control', '#c586c0');
        document.documentElement.style.setProperty('--token-string', '#ce9178');
        document.documentElement.style.setProperty('--token-number', '#b5cea8');
        document.documentElement.style.setProperty('--token-type', '#4ec9b0');
        document.documentElement.style.setProperty('--token-function', '#dcdcaa');
        document.documentElement.style.setProperty('--token-variable', '#9cdcfe');
        document.documentElement.style.setProperty('--token-operator', '#d4d4d4');
        document.documentElement.style.setProperty('--token-punctuation', '#808080');
        document.documentElement.style.setProperty('--token-tag', '#569cd6');
        document.documentElement.style.setProperty('--token-attr', '#9cdcfe');
        document.documentElement.style.setProperty('--bracket-depth-0', '#d4d4d4');
        document.documentElement.style.setProperty('--bracket-depth-1', '#569cd6');
        document.documentElement.style.setProperty('--bracket-depth-2', '#dcdcaa');
        document.documentElement.style.setProperty('--bracket-depth-3', '#ce9178');
        document.documentElement.style.setProperty('--bracket-depth-4', '#4ec9b0');
        document.documentElement.style.setProperty('--bracket-depth-5', '#c586c0');
        document.documentElement.style.setProperty('--hex-blue', '#4da3ff');
        document.documentElement.style.setProperty('--hex-data-even', '#f2f2f2');
        document.documentElement.style.setProperty('--hex-data-odd', '#8a8a8a');
    }

    if (usesMeasuredLineHeights() || previousLineHeight !== viewportController.lineHeight) {
        clearMeasuredLineHeights();
    }

    // Apply localized strings for Find & Replace panel if present
    if (msg.findPlaceholder !== undefined) {
        const el = document.getElementById('find-input');
        if (el) el.placeholder = msg.findPlaceholder;
    }
    if (msg.replacePlaceholder !== undefined) {
        const el = document.getElementById('replace-input');
        if (el) el.placeholder = msg.replacePlaceholder;
    }
    if (msg.replaceButton !== undefined) {
        const el = document.getElementById('replace-btn');
        if (el) {
            el.textContent = msg.replaceButton;
            el.title = msg.replaceButton;
        }
    }
    if (msg.replaceAllButton !== undefined) {
        const el = document.getElementById('replace-all-btn');
        if (el) {
            el.textContent = msg.replaceAllButton;
            el.title = msg.replaceAllButton;
        }
    }
    if (msg.findClearTooltip !== undefined) {
        const el = document.getElementById('find-clear');
        if (el) el.title = msg.findClearTooltip;
    }
    if (msg.findMatchCaseTooltip !== undefined) {
        const el = document.getElementById('find-match-case');
        if (el) el.title = msg.findMatchCaseTooltip;
    }
    if (msg.findRegexTooltip !== undefined) {
        const el = document.getElementById('find-regex');
        if (el) el.title = msg.findRegexTooltip;
    }
    if (msg.replaceClearTooltip !== undefined) {
        const el = document.getElementById('replace-clear');
        if (el) el.title = msg.replaceClearTooltip;
    }
    if (msg.findPrevTooltip !== undefined) {
        const el = document.getElementById('find-prev');
        if (el) el.title = msg.findPrevTooltip;
    }
    if (msg.findNextTooltip !== undefined) {
        const el = document.getElementById('find-next');
        if (el) el.title = msg.findNextTooltip;
    }
    if (msg.findCloseTooltip !== undefined) {
        const el = document.getElementById('find-close');
        if (el) el.title = msg.findCloseTooltip;
    }
    if (msg.editorLoadingText !== undefined) {
        const el = document.getElementById('loading-overlay');
        if (el) el.textContent = msg.editorLoadingText;
    }
    if (msg.longLineProtectionFormat !== undefined) {
        state.longLineProtectionFormat = msg.longLineProtectionFormat;
    }

    if (msg.autocompleteSnippet !== undefined) {
        state.autocompleteSnippet = msg.autocompleteSnippet;
    }
    if (msg.autocompleteSnippetPrefix !== undefined) {
        state.autocompleteSnippetPrefix = msg.autocompleteSnippetPrefix;
    }
    if (msg.menuScrollSync !== undefined) {
        state.menuScrollSync = msg.menuScrollSync;
    }

    // Apply localized context menu text
    const actions = [
        'cut', 'copy', 'paste', 'delete', 'selectAll', 'toggleComment',
        'sortAsc', 'sortDesc', 'removeDuplicates', 'removeEmptyLines', 'collapseConsecutiveEmptyLines', 'trimSpaces',
        'toUpperCase', 'toLowerCase', 'toSentenceCase', 'toTitleCase', 'urlEncode', 'urlDecode',
        'base64Encode', 'base64Decode', 'hexToDec', 'decToHex', 'formatText'
    ];
    actions.forEach(action => {
        const key = 'menu' + action.charAt(0).toUpperCase() + action.slice(1);
        if (msg[key] !== undefined) {
            const el = contextMenu.querySelector(`[data-action="${action}"]`);
            if (el) el.textContent = msg[key];
        }
    });
    if (msg.menuIndent !== undefined) {
        const el = contextMenu.querySelector('[data-action="indentLines"]');
        if (el) el.textContent = msg.menuIndent;
    }
    if (msg.menuOutdent !== undefined) {
        const el = contextMenu.querySelector('[data-action="outdentLines"]');
        if (el) el.textContent = msg.menuOutdent;
    }
    if (msg.menuLineCleanup !== undefined) {
        const el = contextMenu.querySelector('[data-label="lineCleanup"]');
        if (el) el.textContent = msg.menuLineCleanup;
    }
    if (msg.menuConvert !== undefined) {
        const el = contextMenu.querySelector('[data-label="convert"]');
        if (el) el.textContent = msg.menuConvert;
    }

    setupVirtualHeight();
    queueRender(true);
}

function setupModel(lineCount) {
    state.lineCount = Math.max(1, Number(lineCount || 1));
    csvTableMode.virtualLineCount = 0;
    state.cache.clear();
    state.cache.clearLineRequests();
    viewportController.resetDocument();
    clearMeasuredLineHeights();
    state.cacheVersion++;
    state.documentVersion++;
    searchController.invalidateDocument({ clearPendingNavigation: true });
    hexEditorMode.resetSelection();
    state.dirtyLines.clear();
    setupVirtualHeight();
    queueRender(true);
}

function receiveLineBlock(startLine, lines) {
    const start = Number(startLine || 1);
    const safeLines = Array.isArray(lines) ? lines : [];
    let changed = false;
    for (let i = 0; i < safeLines.length; i++) {
        const lineNumber = start + i;
        if ((imeController.isComposing && (!imeController.compositionLine || imeController.compositionLine === lineNumber)) ||
            (imeController.isComposing && runtime.isLineInColumnComposition(lineNumber)) ||
            (state.inlineLivePreviewEnabled && state.inlineLivePreviewSourceLine === lineNumber) ||
            (document.hasFocus() && state.editingLine === lineNumber &&
                document.activeElement?.closest?.('.line-text')?.dataset.line === String(lineNumber))) {
            continue;
        }
        state.cache.set(lineNumber, safeLines[i] ?? '');
        changed = true;
    }
    if (changed) {
        state.cacheVersion++;
    }
    state.cache.completeLineRequests(start, safeLines.length, state.lineCount);
    return safeLines.length;
}

function updateLineFromHost(lineNumber, text, isComposing = false) {
    const line = Number(lineNumber || 1);
    if (!line || line < 1) return false;

    if ((imeController.isComposing && (!imeController.compositionLine || imeController.compositionLine === line)) ||
        (imeController.textareaBypassActive && imeController.bypassStartLine === line) ||
        (imeController.isComposing && runtime.isLineInColumnComposition(line)) ||
        (state.inlineLivePreviewEnabled && state.inlineLivePreviewSourceLine === line)) {
        return false;
    }

    const activeLineElement = document.hasFocus()
        ? document.activeElement?.closest?.('.line-text')
        : null;
    if (state.editingLine === line && activeLineElement?.dataset.line === String(line)) {
        return false;
    }

    const nextText = String(text ?? '');
    state.cache.set(line, nextText);
    state.cacheVersion++;
    state.documentVersion++;
    searchController.invalidateDocument();
    invalidateMeasuredLineHeightsAround(line);

    if (!cleanDirtyMarker(line)) {
        markDirty(line, 'mod');
    }

    const element = viewport.querySelector(`.line-text[data-line="${line}"]`);
    if (element && element.getAttribute('contenteditable') === 'true') {
        element.textContent = nextText;
    }

    if (state.wordWrap) {
        measureRenderedRows(false);
    }

    if (!imeController.isComposing && !isComposing) {
        queueRender();
    } else {
        runtime.drawEditableSelectionOverlays();
    }

    return true;
}

function applyEditResultFromHost(startLine, oldLineCount, lines, documentLineCount, caret = null) {
    if (imeController.isComposing || imeController.textareaBypassActive) {
        return false;
    }

    const start = Math.max(1, Number(startLine || 1));
    const removeCount = Math.max(0, Number(oldLineCount || 0));
    const nextLines = Array.isArray(lines) ? lines.map(line => String(line ?? '')) : [];
    const nextDocumentLineCount = Math.max(1, Number(documentLineCount || (state.lineCount + nextLines.length - removeCount)));

    const hasExplicitCaret = caret && Number(caret.line || 0) > 0;
    // Save current caret position before clearing selection so we can restore it
    // when the host does not provide an explicit caret (e.g. undo/redo).
    const savedCaretLine = state.currentLine;
    const savedCaretColumn = state.currentColumn;

    selectionController.clear();
    hexEditorMode.resetSelection();
    try {
        if (hasExplicitCaret) {
            window.getSelection()?.removeAllRanges();
        }
    } catch (e) { }
    clearCustomSelectionVisuals();
    syncCustomSelectionClass();

    for (let line = start; line < start + removeCount; line++) {
        state.cache.delete(line);
        if (state.showDirtyLines) {
            state.dirtyLines.delete(line);
        }
        deleteMeasuredLineHeight(line);
    }

    const delta = nextLines.length - removeCount;
    if (delta !== 0) {
        shiftCachedLines(start + removeCount, delta);
    }

    state.lineCount = nextDocumentLineCount;
    for (let i = 0; i < nextLines.length; i++) {
        const line = start + i;
        state.cache.set(line, nextLines[i]);
        deleteMeasuredLineHeight(line);
        if (!cleanDirtyMarker(line)) {
            markDirty(line, removeCount === 0 ? 'add' : 'mod');
        }
    }

    state.cacheVersion++;
    state.documentVersion++;
    searchController.invalidateDocument();
    state.livePreviewLocalResourceVersion = String(Date.now());
    setupVirtualHeight();

    if (state.wordWrap) {
        measureRenderedRows(false);
    }

    queueRender(true);

    const canRestoreHostCaret = document.hasFocus();
    if (canRestoreHostCaret && caret && Number(caret.line || 0) > 0) {
        const caretLine = Math.min(state.lineCount, Math.max(1, Number(caret.line)));
        const caretColumn = Math.max(0, Number(caret.column || 1) - 1);
        setTimeout(() => {
            if (document.hasFocus() && !imeController.isComposing && !imeController.textareaBypassActive) {
                runtime.focusLine(caretLine, caretColumn);
            }
        }, 20);
    } else if (canRestoreHostCaret && savedCaretLine > 0) {
        const caretLine = Math.min(state.lineCount, Math.max(1, Number(savedCaretLine)));
        const caretColumn = Math.max(0, Number(savedCaretColumn || 1) - 1);
        setTimeout(() => {
            if (document.hasFocus() && !imeController.isComposing && !imeController.textareaBypassActive) {
                runtime.focusLine(caretLine, caretColumn);
            }
        }, 20);
    }

    return true;
}

function setupVirtualHeight() {
    const savedScroll = scrollContainer.scrollTop;

    // Small documents are rendered in normal flow. Let the browser derive the
    // document height instead of maintaining a second, measured height model.
    document.body.classList.toggle('full-document-render', usesFullDocumentRender());
    if (usesFullDocumentRender()) {
        syncFullDocumentTrailingSpace();
        const maximumScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
        if (savedScroll > maximumScrollTop) {
            scrollContainer.scrollTop = maximumScrollTop;
        }
        return;
    }

    viewport.style.removeProperty('--full-document-trailing-height');
    const maximumScrollTop = maximumVirtualScrollTop();
    viewportController.clampPreservedScrollTop(maximumScrollTop);
    const preservedHeight = viewportController.preservedContentHeight(scrollContainer.clientHeight);
    virtualSpacer.style.height = `${Math.max(totalVirtualHeight(), preservedHeight)}px`;
    const maxScroll = Math.min(
        maximumScrollTop,
        Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight));
    if (savedScroll > maxScroll) {
        scrollContainer.scrollTop = maxScroll;
    }
}

function syncFullDocumentTrailingSpace() {
    const lastRow = viewport.lastElementChild;
    const lastRowHeight = Math.max(1, lastRow?.offsetHeight || viewportController.lineHeight);
    const trailingHeight = Math.max(0, scrollContainer.clientHeight - lastRowHeight);
    viewport.style.setProperty('--full-document-trailing-height', `${trailingHeight}px`);
}

function preserveScrollTop(scrollTop) {
    viewportController.preserveScrollTop(scrollTop);
    setupVirtualHeight();
}

function clearPreservedScrollTop() {
    if (!viewportController.clearPreservedScrollTop()) return;
    setupVirtualHeight();
}

function usesMeasuredLineHeights() {
    return (state.wordWrap || state.inlineLivePreviewEnabled) && !usesFullDocumentRender();
}

function lineHeightFor(lineNumber) {
    return viewportController.lineHeightFor(lineNumber, {
        useMeasured: usesMeasuredLineHeights()
    });
}

function measuredLineHeightDeltaBefore(lineNumber) {
    if (!usesMeasuredLineHeights()) return 0;
    return viewportController.measuredLineHeightDeltaBefore(lineNumber, state.lineCount);
}

function setMeasuredLineHeight(lineNumber, height) {
    return viewportController.setMeasuredLineHeight(lineNumber, height, state.lineCount);
}

function deleteMeasuredLineHeight(lineNumber) {
    return viewportController.deleteMeasuredLineHeight(lineNumber, state.lineCount);
}

function clearMeasuredLineHeights() {
    viewportController.clearMeasuredLineHeights(state.lineCount);
}

function shiftMeasuredLineHeights(fromLine, delta) {
    viewportController.shiftMeasuredLineHeights(fromLine, delta, state.lineCount);
}

function totalVirtualHeight() {
    if (usesFullDocumentRender()) {
        return Math.max(1, scrollContainer.scrollHeight || (effectiveLineCount() * viewportController.lineHeight));
    }

    const total = rawTotalVirtualHeight();
    if (usesCompressedScroll()) {
        const viewHeight = Math.max(scrollContainer.clientHeight, viewportController.lineHeight);
        return Math.max(viewHeight + viewportController.lineHeight, Math.min(total, BROWSER_SCROLL_HEIGHT_LIMIT));
    }

    return total + trailingScrollHeight();
}

function rawTotalVirtualHeight() {
    if (usesFullDocumentRender()) {
        return Math.max(1, scrollContainer.scrollHeight || (effectiveLineCount() * viewportController.lineHeight));
    }

    let total = effectiveLineCount() * viewportController.lineHeight;
    if (usesMeasuredLineHeights()) {
        total += viewportController.totalMeasuredLineHeightDelta(state.lineCount);
    }
    return Math.max(1, total);
}

function trailingScrollHeight() {
    const viewHeight = Math.max(scrollContainer.clientHeight, viewportController.lineHeight);
    const lastLineHeight = Math.max(1, lineHeightFor(effectiveLineCount()));
    return Math.max(0, viewHeight - lastLineHeight);
}

function maximumVirtualScrollTop() {
    if (usesFullDocumentRender()) {
        return Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
    }

    return Math.max(0, totalVirtualHeight() - scrollContainer.clientHeight);
}

function lineTop(lineNumber) {
    if (usesFullDocumentRender()) {
        const line = Math.max(1, Math.min(effectiveLineCount(), Number(lineNumber || 1)));
        const row = viewport.children[line - 1];
        if (row?.dataset?.line === String(line)) {
            return Math.max(0, row.offsetTop);
        }
    }

    if (usesCompressedScroll()) {
        const metrics = compressedScrollMetrics();
        if (metrics.maxScrollTop <= 0 || metrics.maxFirstLine <= 1) return 0;
        const line = Math.min(metrics.maxFirstLine, Math.max(1, Math.floor(Number(lineNumber || 1))));
        return ((line - 1) / (metrics.maxFirstLine - 1)) * metrics.maxScrollTop;
    }

    let top = (Math.max(1, lineNumber) - 1) * viewportController.lineHeight;
    top += measuredLineHeightDeltaBefore(lineNumber);
    return Math.max(0, top);
}

function lineAt(scrollTop) {
    const lineCount = effectiveLineCount();

    if (usesFullDocumentRender() && viewport.children.length > 0) {
        const targetTop = Math.max(0, Number(scrollTop || 0));
        let low = 0;
        let high = viewport.children.length - 1;
        let result = 0;
        while (low <= high) {
            const mid = Math.floor((low + high) / 2);
            const row = viewport.children[mid];
            if (!row?.dataset?.line) {
                break;
            }

            if (row.offsetTop <= targetTop) {
                result = mid;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }

        return Math.min(lineCount, Math.max(1, Number(viewport.children[result]?.dataset?.line || 1)));
    }

    if (usesCompressedScroll()) {
        const metrics = compressedScrollMetrics();
        if (metrics.maxScrollTop <= 0 || metrics.maxFirstLine <= 1) return 1;
        const ratio = Math.max(0, Math.min(1, Number(scrollTop || 0) / metrics.maxScrollTop));
        return Math.min(metrics.maxFirstLine, Math.max(1, Math.floor(ratio * (metrics.maxFirstLine - 1)) + 1));
    }

    if (!usesMeasuredLineHeights() || !viewportController.hasMeasuredLineHeights) {
        return Math.min(lineCount, Math.max(1, Math.floor(scrollTop / viewportController.lineHeight) + 1));
    }

    const targetTop = Math.max(0, Number(scrollTop || 0));
    let low = 1;
    let high = lineCount;
    let result = 1;
    while (low <= high) {
        const mid = Math.floor((low + high) / 2);
        if (lineTop(mid) <= targetTop) {
            result = mid;
            low = mid + 1;
        } else {
            high = mid - 1;
        }
    }
    return Math.min(lineCount, Math.max(1, result));
}

function visibleRange() {
    const viewHeight = Math.max(scrollContainer.clientHeight, viewportController.lineHeight);
    const lineCount = effectiveLineCount();
    if (usesFullDocumentRender()) {
        return { start: 1, end: lineCount, count: lineCount };
    }

    const firstVisible = lineAt(scrollContainer.scrollTop);
    if (usesCompressedScroll()) {
        const visibleRows = Math.max(1, Math.ceil(viewHeight / viewportController.lineHeight) + 1);
        const overscan = activeEditorMode().renderOverscan({ defaultOverscan: viewportController.overscan });
        const start = Math.max(1, firstVisible - overscan);
        const end = Math.min(lineCount, firstVisible + visibleRows + overscan);
        return { start, end, count: Math.max(0, end - start + 1) };
    }

    const lastVisible = lineAt(scrollContainer.scrollTop + viewHeight);
    const overscan = activeEditorMode().renderOverscan({ defaultOverscan: viewportController.overscan });
    const windowStep = Math.max(1, Math.floor(overscan / 2));
    const windowAnchor = Math.floor((firstVisible - 1) / windowStep) * windowStep + 1;
    const visibleLineCount = Math.max(1, lastVisible - firstVisible + 1);
    const start = Math.max(1, windowAnchor - overscan);
    const end = Math.min(lineCount, start + visibleLineCount + (overscan * 2) + windowStep - 1);
    return { start, end, count: Math.max(0, end - start + 1) };
}

function usesFullDocumentRender() {
    return activeEditorMode().usesFullDocumentRender({
        inlineLivePreviewEnabled: state.inlineLivePreviewEnabled,
        lineCount: effectiveLineCount(),
        fullDocumentLineLimit: FULL_DOCUMENT_RENDER_LINE_LIMIT
    });
}

function usesCompressedScroll() {
    return activeEditorMode().allowsCompressedScroll() &&
        rawTotalVirtualHeight() > BROWSER_SCROLL_HEIGHT_LIMIT;
}

function compressedScrollMetrics() {
    const viewHeight = Math.max(scrollContainer.clientHeight, viewportController.lineHeight);
    const visibleRows = Math.max(1, Math.ceil(viewHeight / viewportController.lineHeight));
    const lineCount = effectiveLineCount();
    const maxFirstLine = lineCount;
    const virtualHeight = totalVirtualHeight();
    const maxScrollTop = Math.max(0, virtualHeight - viewHeight);
    return { lineCount, maxFirstLine, maxScrollTop, visibleRows, viewHeight };
}

function viewportTopForLine(startLine) {
    if (!usesCompressedScroll()) {
        return lineTop(startLine);
    }

    const metrics = compressedScrollMetrics();
    const firstVisible = lineAt(scrollContainer.scrollTop);
    const firstVisibleTop = lineTop(firstVisible);
    const nextVisibleTop = firstVisible < metrics.maxFirstLine
        ? lineTop(firstVisible + 1)
        : firstVisibleTop + viewportController.lineHeight;
    const virtualLineSpan = Math.max(0.0001, nextVisibleTop - firstVisibleTop);
    const scrollOffset = Math.max(0, scrollContainer.scrollTop - firstVisibleTop);
    const physicalOffset = Math.max(0, Math.min(1, scrollOffset / virtualLineSpan)) * viewportController.lineHeight;
    return scrollContainer.scrollTop -
        physicalOffset -
        ((firstVisible - Math.max(1, Number(startLine || 1))) * viewportController.lineHeight);
}

function effectiveLineCount() {
    return activeEditorMode().effectiveLineCount({
        sourceLineCount: state.lineCount
    });
}

function requestLines(start, count) {
    if (count <= 0) return;
    if (!state.cache.beginLineRequest(start, count)) return;
    post({
        type: 'requestLines',
        requestId: hostRequestController.nextRequestId(),
        startLine: start,
        count
    });
}

function requestMissingLines(start, end) {
    const sourceLineCount = Math.max(1, Number(state.lineCount || 1));
    if (Number(start || 1) > sourceLineCount) return;

    start = Math.max(1, Number(start || 1));
    end = Math.min(sourceLineCount, Math.max(start, Number(end || start)));

    const pendingRanges = state.cache.pendingLineRanges();
    const isPending = line => pendingRanges.some(range => line >= range.start && line <= range.end);
    const requestMissingBlock = (missingStart, missingCount) => {
        let requestCount = Math.max(missingCount, MIN_BATCH_SIZE);
        const nextPendingStart = pendingRanges
            .filter(range => range.start > missingStart)
            .reduce((nearest, range) => Math.min(nearest, range.start), Number.POSITIVE_INFINITY);
        if (Number.isFinite(nextPendingStart)) {
            requestCount = Math.min(requestCount, nextPendingStart - missingStart);
        }
        requestCount = Math.min(requestCount, sourceLineCount - missingStart + 1);
        requestLines(missingStart, requestCount);
    };

    let missingStart = 0;
    let missingCount = 0;
    for (let line = start; line <= end; line++) {
        if (!state.cache.has(line) && !isPending(line)) {
            if (missingStart === 0) {
                missingStart = line;
                missingCount = 1;
            } else {
                missingCount++;
            }
        } else if (missingStart !== 0) {
            requestMissingBlock(missingStart, missingCount);
            missingStart = 0;
            missingCount = 0;
        }
    }
    if (missingStart !== 0) {
        requestMissingBlock(missingStart, missingCount);
    }
}

function prefetchAround(scrollTop) {
    const viewHeight = Math.max(scrollContainer.clientHeight, viewportController.lineHeight);
    const firstVisible = lineAt(scrollTop);
    const prefetchAhead = activeEditorMode().prefetchAhead({ defaultPrefetchAhead: PREFETCH_AHEAD });
    if (usesCompressedScroll()) {
        const visibleRows = Math.max(1, Math.ceil(viewHeight / viewportController.lineHeight) + 1);
        const prefetchStart = Math.max(1, firstVisible - prefetchAhead);
        const prefetchEnd = Math.min(state.lineCount, firstVisible + visibleRows + prefetchAhead);
        requestMissingLines(prefetchStart, prefetchEnd);
        return;
    }

    const lastVisible = lineAt(scrollTop + viewHeight);
    const prefetchStart = Math.max(1, firstVisible - prefetchAhead);
    const prefetchEnd = Math.min(state.lineCount, lastVisible + prefetchAhead);
    const viewportRequestEnd = Math.min(
        prefetchEnd,
        Math.max(lastVisible, firstVisible + MIN_BATCH_SIZE - 1));

    // Request the viewport first so a fast scroll never waits for a large surrounding
    // prefetch block before the rows at the current position can be displayed.
    requestMissingLines(firstVisible, viewportRequestEnd);
    if (prefetchStart < firstVisible) {
        requestMissingLines(prefetchStart, firstVisible - 1);
    }
    if (viewportRequestEnd < prefetchEnd) {
        requestMissingLines(viewportRequestEnd + 1, prefetchEnd);
    }
}

function compressedScrollScale() {
    if (!usesCompressedScroll()) return 1;

    const metrics = compressedScrollMetrics();
    const lastLineHeight = Math.max(1, lineHeightFor(effectiveLineCount()));
    const rawMaxScrollTop = Math.max(0, rawTotalVirtualHeight() - lastLineHeight);
    if (metrics.maxScrollTop <= 0 || rawMaxScrollTop <= 0) return 1;
    return Math.max(1, rawMaxScrollTop / metrics.maxScrollTop);
}

function visualScrollDeltaToScrollTopDelta(delta) {
    return Number(delta || 0) / compressedScrollScale();
}

function captureScrollAnchor(scrollTop = scrollContainer.scrollTop) {
    const line = lineAt(scrollTop);
    const top = lineTop(line);
    const nextTop = line < effectiveLineCount()
        ? lineTop(line + 1)
        : top + viewportController.lineHeight;
    const span = Math.max(0.0001, nextTop - top);
    const ratio = Math.max(0, Math.min(1, (Number(scrollTop || 0) - top) / span));
    return { line, ratio };
}

function restoreScrollAnchor(anchor) {
    if (!anchor) return;

    const line = Math.min(effectiveLineCount(), Math.max(1, Number(anchor.line || 1)));
    const top = lineTop(line);
    const nextTop = line < effectiveLineCount()
        ? lineTop(line + 1)
        : top + viewportController.lineHeight;
    const ratio = Math.max(0, Math.min(1, Number(anchor.ratio || 0)));
    const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
    scrollContainer.scrollTop = Math.min(maxScrollTop, Math.max(0, top + ((nextTop - top) * ratio)));
}

function trimHexCacheToRange(startLine, endLine) {
    if (!activeEditorMode().shouldTrimDocumentCache() || state.cache.size <= HEX_CACHE_RETAIN_LINES) return;

    const keepRanges = [{
        start: Math.max(1, Number(startLine || 1) - HEX_PREFETCH_AHEAD),
        end: Math.min(state.lineCount, Number(endLine || startLine || 1) + HEX_PREFETCH_AHEAD)
    }];

    const hexSelection = normalizedHexSelection();
    if (hexSelection) {
        const selectionStartLine = Math.floor(hexSelection.startOffset / 16) + 2;
        const selectionEndLine = Math.floor((hexSelection.endOffset - 1) / 16) + 2;
        if (selectionEndLine - selectionStartLine + 1 <= HEX_SELECTION_CACHE_RETAIN_LIMIT) {
            keepRanges.push({ start: selectionStartLine, end: selectionEndLine });
        }
    }

    for (const lineNumber of [...state.cache.keys()]) {
        if (lineNumber === 1) continue;
        const keep = keepRanges.some(range => lineNumber >= range.start && lineNumber <= range.end);
        if (!keep) {
            state.cache.delete(lineNumber);
        }
    }
}

function queueRender(force = false) {
    if (!viewportController.beginQueuedRender({ force })) return;
    requestAnimationFrame(() => {
        viewportController.completeQueuedRender();
        runtime.render();
    });
}

function queueColumnTextInputFallback(text, callback, delayMs = 40) {
    return imeController.queueColumnTextInputFallback(text, callback, delayMs);
}

function consumePendingColumnTextInput(text = null) {
    return imeController.consumePendingColumnTextInput(text);
}

function cancelPendingColumnTextInputs() {
    imeController.cancelPendingColumnTextInputs();
}

function measureRenderedRows(renderOnChange = true, force = false) {
    if (!usesMeasuredLineHeights()) return;

    measureRenderedRowsSynchronously(renderOnChange, force);
}

function measureRenderedRowsSynchronously(renderOnChange = true, force = false) {
    if (!usesMeasuredLineHeights()) return;

    const anchorLine = lineAt(scrollContainer.scrollTop);
    const anchorOffset = scrollContainer.scrollTop - lineTop(anchorLine);
    const oldEditingLineTop = state.editingLine ? lineTop(state.editingLine) : null;
    const containerRect = scrollContainer.getBoundingClientRect();
    let changed = false;
    for (const row of viewport.querySelectorAll('.line-row')) {
        const lineNumber = Number(row.dataset.line || 0);
        if (!lineNumber) continue;
        const rowRect = row.getBoundingClientRect();
        // Live preview keeps a large lookbehind window in the DOM. Its first
        // line can move into the middle of a merged Markdown block as the
        // virtual window advances, so rows far above the viewport may have a
        // different temporary shape. Re-measuring those rows moves the current
        // scroll anchor by their accumulated delta and can make the viewport
        // oscillate at a render-window boundary.
        if (state.inlineLivePreviewEnabled && rowRect.bottom <= containerRect.top) continue;
        const isSkipped = row.classList.contains('live-preview-skipped');
        const minimum = isSkipped ? 0 : viewportController.lineHeight;
        const measuredHeight = state.inlineLivePreviewEnabled
            ? Math.max(rowRect.height || 0, row.scrollHeight || 0)
            : (rowRect.height || row.scrollHeight || 0);
        const measured = Math.max(minimum, roundCssPixelsToDevicePixels(measuredHeight));
        if (setMeasuredLineHeight(lineNumber, measured)) {
            changed = true;
        }
    }

    if (changed) {
        setupVirtualHeight();
        if (state.inlineLivePreviewEnabled) {
            const maxScrollTop = Math.max(0, totalVirtualHeight() - scrollContainer.clientHeight);
            let anchoredScrollTop;
            if (state.editingLine && oldEditingLineTop !== null) {
                const newEditingLineTop = lineTop(state.editingLine);
                anchoredScrollTop = Math.min(maxScrollTop, Math.max(0, scrollContainer.scrollTop + (newEditingLineTop - oldEditingLineTop)));
            } else {
                anchoredScrollTop = Math.min(maxScrollTop, Math.max(0, lineTop(anchorLine) + anchorOffset));
            }
            if (Math.abs(scrollContainer.scrollTop - anchoredScrollTop) > 1) {
                scrollContainer.scrollTop = anchoredScrollTop;
            }
        }
        if (renderOnChange) {
            viewportController.invalidateRenderRange();
            requestAnimationFrame(() => runtime.render());
        }
    }
}

function invalidateMeasuredLineHeightsAround(lineNumber, radius = 0) {
    if (!usesMeasuredLineHeights()) return;

    const center = Math.max(1, Number(lineNumber || 1));
    const start = Math.max(1, center - radius);
    const end = Math.min(state.lineCount, center + radius);
    let changed = false;
    for (let line = start; line <= end; line++) {
        changed = deleteMeasuredLineHeight(line) || changed;
    }

    if (changed) {
        viewportController.invalidateRenderRange();
        setupVirtualHeight();
    }
}

function shiftCachedLines(fromLine, delta) {
    shiftLineMap(state.cache, fromLine, delta);
    shiftMeasuredLineHeights(fromLine, delta);
    if (state.showDirtyLines) {
        shiftLineMap(state.dirtyLines, fromLine, delta);
    }
}

function shiftLineMap(map, fromLine, delta) {
    const entries = [...map.entries()]
        .filter(([line]) => line >= fromLine);

    // All source keys are removed before any destination key is inserted, so
    // ordering is unnecessary. Avoiding the sort keeps large range undo/redo
    // linear in the number of cached lines instead of O(n log n).
    for (const [line] of entries) {
        map.delete(line);
    }
    for (const [line, value] of entries) {
        const nextLine = line + delta;
        if (nextLine >= 1 && nextLine <= state.lineCount + Math.max(delta, 0)) {
            map.set(nextLine, value);
        }
    }
}

function markDirty(lineNumber, type) {
    if (!state.showDirtyLines) return;
    const existing = state.dirtyLines.get(lineNumber);
    if (existing === 'add') return;
    state.dirtyLines.set(lineNumber, type || 'mod');
}

function cleanDirtyMarker(lineNumber) {
    // The host owns the saved baseline and sends authoritative dirty markers.
    // Local edits stay optimistically dirty until the deferred host reconciliation.
    return !state.showDirtyLines;
}
function reportCursorAndSelection(
    element = document.activeElement,
    knownCaretOffset = null,
    includeSelection = true) {
    if (element && !document.body.contains(element)) {
        element = document.activeElement;
    }
    const editable = element && element.closest ? element.closest('.line-text') : null;
    if (editable && document.body.contains(editable)) {
        state.currentLine = Number(editable.dataset.line || state.currentLine);
        if (selectionController.selection?.end) {
            state.currentLine = selectionController.selection.end.line;
            state.currentColumn = selectionController.selection.end.column + 1;
        } else if (editable.getAttribute('contenteditable') === 'true') {
            const caretOffset = Number(knownCaretOffset);
            state.currentColumn = (knownCaretOffset !== null && Number.isFinite(caretOffset)
                ? Math.max(0, caretOffset)
                : runtime.getCaretOffset(editable)) + 1;
        }
    }

    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    if (!includeSelection) return;

    const selInfo = selectionInfo();
    post({
        type: 'selectionResult',
        text: selInfo.text,
        startLine: selInfo.startLine,
        endLine: selInfo.endLine,
        hexOffset: selInfo.hexOffset,
        hexLength: selInfo.hexLength
    });
}

function selectionInfo() {
    return activeEditorMode().selectionInfo({
        hexSelectionInfo,
        textSelectionInfo
    });
}

function textSelectionInfo() {
    const selection = runtime.normalizeSelection();
    if (selection && runtime.hasCustomSelection()) {
        return selectionTextFromModel(selection);
    }

    return nativeSelectionTextFromModel() ?? { text: window.getSelection()?.toString() || '', startLine: 0, endLine: 0 };
}

function selectedText() {
    return activeEditorMode().selectedText({
        hexSelectedText,
        textSelectedText
    });
}

function textSelectedText() {
    const selection = runtime.normalizeSelection();
    if (selection && runtime.hasCustomSelection()) {
        return selectionTextFromModel(selection).text;
    }

    return nativeSelectionTextFromModel()?.text ?? window.getSelection()?.toString() ?? '';
}

function hexSelectionInfo() {
    const selection = normalizedHexSelection();
    if (!selection) {
        return { text: '', startLine: 0, endLine: 0, hexOffset: null, hexLength: 0 };
    }

    return {
        text: hexSelectedText(selection),
        startLine: 0,
        endLine: 0,
        hexOffset: selection.startOffset,
        hexLength: selection.endOffset - selection.startOffset
    };
}

function normalizedHexSelection(selection = hexEditorMode.selection) {
    if (!selection) return null;
    const startOffset = Math.max(0, Math.min(Number(selection.startOffset || 0), Number(selection.endOffset || 0)));
    const endOffset = Math.max(startOffset, Math.max(Number(selection.startOffset || 0), Number(selection.endOffset || 0)));
    if (endOffset <= startOffset) return null;
    return { startOffset, endOffset };
}

function hexSelectedText(selection = normalizedHexSelection()) {
    if (!selection) return '';

    const parts = [];
    for (let offset = selection.startOffset; offset < selection.endOffset; offset++) {
        const line = Math.floor(offset / 16) + 2;
        const byteIndex = offset % 16;
        const text = state.cache.get(line);
        if (!text) continue;

        const pair = hexPairAtByteIndex(text, byteIndex);
        if (pair) {
            parts.push(pair);
        }
    }

    return parts.join(' ');
}

function hexPairAtByteIndex(text, byteIndex) {
    const layout = hexLayoutFromLine(text);
    const start = layout.hexStart + (byteIndex * 3) + (byteIndex >= 8 ? 1 : 0);
    const pair = text.slice(start, start + 2);
    return /^[0-9A-F]{2}$/i.test(pair) ? pair.toUpperCase() : '';
}

function hexLayoutFromLine(text) {
    const firstPipe = String(text ?? '').indexOf('|');
    const hexStart = Math.max(0, firstPipe > 0 ? firstPipe - 50 : 11);
    return { hexStart };
}

function selectionTextFromModel(selection) {
    const parts = [];
    for (let line = selection.start.line; line <= selection.end.line; line++) {
        const text = state.cache.get(line) ?? '';
        if (selection.isColumn) {
            const start = Math.min(selection.start.column, selection.end.column);
            const end = Math.max(selection.start.column, selection.end.column);
            parts.push(text.slice(Math.max(0, start), Math.max(0, end)));
        } else {
            const start = line === selection.start.line ? selection.start.column : 0;
            const end = line === selection.end.line ? selection.end.column : text.length;
            parts.push(text.slice(Math.max(0, start), Math.max(0, end)));
        }
    }
    return { text: parts.join('\n'), startLine: selection.start.line, endLine: selection.end.line };
}

function nativeSelectionTextFromModel() {
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

    return selectionTextFromModel(ordered);
}

function editorPositionFromDomPosition(node, offset) {
    const element = lineElementFromDomNode(node);
    if (!element) return null;

    const line = Number(element.dataset.line || 0);
    if (!line) return null;

    const text = state.cache.get(line) ?? element.textContent ?? '';
    const column = Math.max(0, Math.min(offsetFromNodeInElement(element, node, offset), text.length));
    return { line, column };
}

function lineElementFromDomNode(node) {
    if (!node) return null;
    if (node.nodeType === Node.ELEMENT_NODE && node.closest) {
        return node.closest('.line-text');
    }
    return node.parentElement?.closest?.('.line-text') || null;
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
    } finally {
        before.detach?.();
    }
}

function activeEditableElement() {
    const active = document.activeElement?.closest?.('.line-text');
    if (active && active.isConnected && active.getAttribute('contenteditable') === 'true') return active;
    const current = viewport.querySelector(`.line-text[data-line="${state.currentLine}"]`);
    if (current && current.getAttribute('contenteditable') === 'true') {
        if (document.activeElement !== current) {
            current.focus({ preventScroll: true });
        }
        return current;
    }
    return null;
}

function isPlainTextKey(event) {
    if (!event || event.ctrlKey || event.metaKey || event.altKey) return false;
    if (event.isComposing || imeController.isComposing || event.key === 'Process' || event.keyCode === 229) return false;
    if (containsHangulInputText(event.key)) return false;
    return typeof event.key === 'string' && event.key.length === 1;
}

function containsHangulInputText(value) {
    return /[\u1100-\u11FF\u3130-\u318F\uA960-\uA97F\uD7B0-\uD7FF\uAC00-\uD7A3]/.test(String(value ?? ''));
}

function isHangulImeKeyEvent(event) {
    if (!event || event.ctrlKey || event.metaKey || event.altKey) return false;
    return !!(event.isComposing || imeController.isComposing ||
        event.key === 'Process' || event.keyCode === 229 ||
        containsHangulInputText(event.key));
}

function syncCustomSelectionClass() {
    const hasSelection = runtime.hasCustomSelection();
    document.body.classList.toggle('custom-selection-active', hasSelection);
    if (!hasSelection && !imeController.rangeComposition?.deferred) {
        imeController.preparedRangeCompositionLine = null;
        document.body.classList.remove('range-composition-active');
    }
}

function clearCustomSelectionVisuals() {
    viewport.querySelectorAll('.editable-selection-overlay').forEach(el => el.remove());
    viewport.querySelectorAll('.line-row.selected-row, .line-row.selected-empty-row').forEach(row => {
        row.classList.remove('selected-row', 'selected-empty-row');
    });
    viewport.querySelectorAll('.selection-fragment').forEach(fragment => {
        const parent = fragment.parentNode;
        if (!parent) return;

        while (fragment.firstChild) {
            parent.insertBefore(fragment.firstChild, fragment);
        }
        fragment.remove();
    });
}

function comparePositions(a, b) {
    if (a.line !== b.line) return a.line - b.line;
    return a.column - b.column;
}

function orderedRange(range) {
    return comparePositions(range.start, range.end) <= 0
        ? range
        : { start: range.end, end: range.start };
}

function isStandaloneDelimiter(text, index, delimiter) {
    if (!hasTextAt(text, index, delimiter)) return false;
    if (delimiter.length === 1) {
        const marker = delimiter[0];
        if (index > 0 && text[index - 1] === marker) return false;
        if (index + 1 < text.length && text[index + 1] === marker) return false;
    }
    return true;
}

function hasTextAt(text, index, value) {
    return index >= 0 && index + value.length <= text.length && text.slice(index, index + value.length) === value;
}

async function writeClipboardText(text) {
    const value = String(text ?? '');
    if (window.chrome && window.chrome.webview) {
        post({ type: 'clipboardWrite', text: value });
        return true;
    }

    if (navigator.clipboard?.writeText) {
        try {
            await navigator.clipboard.writeText(value);
            return true;
        } catch { }
    }

    const textarea = document.createElement('textarea');
    textarea.value = value;
    textarea.style.position = 'fixed';
    textarea.style.left = '-9999px';
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    const ok = document.execCommand('copy');
    textarea.remove();
    if (ok) return true;

    post({ type: 'clipboardWrite', text: value });
    return true;
}

function requestClipboardTextFromHost() {
    return new Promise(resolve => {
        const requestId = hostRequestController.beginClipboardRequest(resolve);
        post({ type: 'clipboardRead', requestId });
    });
}

async function readClipboardText() {
    if (window.chrome && window.chrome.webview) {
        return await requestClipboardTextFromHost();
    }

    if (navigator.clipboard?.readText) {
        try {
            return (await navigator.clipboard.readText()).replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        } catch { }
    }

    return await requestClipboardTextFromHost();
}

function selectedLineRange() {
    const selection = runtime.normalizeSelection();
    if (!selection || !runtime.hasCustomSelection()) {
        return { startLine: state.currentLine, endLine: state.currentLine };
    }

    const endLine = selection.end.column === 0 && selection.end.line > selection.start.line
        ? selection.end.line - 1
        : selection.end.line;
    return {
        startLine: Math.max(1, selection.start.line),
        endLine: Math.max(selection.start.line, endLine)
    };
}

function lineCommentSyntax() {
    switch (state.language) {
        case 'python':
        case 'r':
        case 'ruby':
        case 'shell':
        case 'powershell':
        case 'yaml':
        case 'toml':
        case 'ini':
        case 'dockerfile':
        case 'makefile':
            return { prefix: '# ' };
        case 'sql':
        case 'lua':
            return { prefix: '-- ' };
        case 'latex':
            return { prefix: '% ' };
        case 'vb':
            return { prefix: "' " };
        case 'html':
        case 'svg':
        case 'xml':
        case 'markdown':
            return { blockStart: '<!-- ', blockEnd: ' -->' };
        case 'css':
        case 'scss':
        case 'less':
            return { blockStart: '/* ', blockEnd: ' */' };
        default:
            return { prefix: '// ' };
    }
}

export {
    MAX_RENDER_CHARS,
    applyOptions,
    applyEditResultFromHost,
    activeEditableElement,
    captureScrollAnchor,
    cancelPendingColumnTextInputs,
    cleanDirtyMarker,
    clearPreservedScrollTop,
    clearMeasuredLineHeights,
    clearCustomSelectionVisuals,
    comparePositions,
    consumePendingColumnTextInput,
    compressedScrollMetrics,
    configureEditorCoreRuntime,
    containsHangulInputText,
    csvInteractionController,
    csvTableMode,
    dragDropController,
    escapeHtml,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasTextAt,
    hexEditorMode,
    hostRequestController,
    imeController,
    isHangulImeKeyEvent,
    isPlainTextKey,
    isStandaloneDelimiter,
    lineAt,
    lineCommentSyntax,
    lineHeightFor,
    lineTop,
    invalidateMeasuredLineHeightsAround,
    markDirty,
    maximumVirtualScrollTop,
    measureRenderedRows,
    orderedRange,
    post,
    prefetchAround,
    preserveScrollTop,
    queueColumnTextInputFallback,
    queueRender,
    readClipboardText,
    receiveLineBlock,
    reportCursorAndSelection,
    requestLines,
    requestMissingLines,
    restoreScrollAnchor,
    searchController,
    selectionController,
    selectedLineRange,
    selectionInfo,
    selectedText,
    setupModel,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    trimHexCacheToRange,
    totalVirtualHeight,
    updateLineFromHost,
    usesCompressedScroll,
    usesFullDocumentRender,
    usesMeasuredLineHeights,
    visualScrollDeltaToScrollTopDelta,
    visibleRange,
    viewportController,
    viewportTopForLine,
    writeClipboardText
};
