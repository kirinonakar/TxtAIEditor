import { contextMenu, scrollContainer, viewport, virtualSpacer } from './editor-dom.js';
import { ImePhase } from './editor-ime-state.js';

const MAX_RENDER_CHARS = 20000;
const MIN_BATCH_SIZE = 100;
const PREFETCH_AHEAD = 200;
const HEX_PREFETCH_AHEAD = 80;
const HEX_RENDER_OVERSCAN = 48;
const BROWSER_SCROLL_HEIGHT_LIMIT = 12000000;
const HEX_CACHE_RETAIN_LINES = 512;
const HEX_SELECTION_CACHE_RETAIN_LIMIT = 2048;
const MAX_DIRTY_DIFF_CELLS = 4000000;

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

const state = {
    lineCount: 1,
    lineHeight: 22,
    overscan: 80,
    cache: new Map(),
    pending: new Set(),
    lineHeights: new Map(),
    lineHeightIndex: null,
    requestSeq: 1,
    currentLine: 1,
    currentColumn: 1,
    readOnly: false,
    hexEditable: false,
    wordWrap: false,
    syntaxHighlighting: true,
    language: 'plaintext',
    tabSize: 4,
    searchQuery: '',
    searchMatches: [],
    searchIndex: -1,
    activeSearch: null,
    findMatchCase: false,
    findRegex: false,
    selection: null,
    selectionAnchor: null,
    hexSelection: null,
    hexSelectionAnchorOffset: null,
    hexSelectionPane: 'hex',
    hexCursorOffset: 0,
    hexPendingHighNibble: null,
    isSelecting: false,
    isLineSelecting: false,
    initialized: false,
    lastRangeKey: '',
    cacheVersion: 0,
    documentVersion: 0,
    hostDocumentId: '',
    hostDocumentVersion: 0,
    viewId: '',
    messageSequence: 0,
    searchDocumentVersion: -1,
    pendingSearchNavigation: null,
    renderQueued: false,
    clipboardRequests: new Map(),
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
    dragStartPosition: null,
    isDragPotential: false,
    isDragMoving: false,
    dragSelectionData: null,
    dragDropPosition: null,
    isDragCopy: false,
    imePhase: ImePhase.Idle,
    isComposing: false,
    compositionLine: null,
    rangeComposition: null,
    preparedRangeCompositionLine: null,
    columnComposition: null,
    pendingImeVerticalNavigation: null,
    pendingImeSelectionCollapse: null,
    textareaImeBypassActive: false,
    bypassStartLine: null,
    bypassCursorLine: null,
    bypassCursorColumn: null,
    isSplitView: false,
    suppressNextBeforeInputType: null,
    lastManualDeleteAt: 0,
    editingLine: null,
    lastDeleteKeyDown: null,
    preservedScrollTop: null,
    repeatEdit: {
        lastRunAt: 0,
        timer: 0,
        pending: null,
        continuousTimer: 0,
        continuousKey: null,
        hasContinuousRun: false,
        hasPhysicalRepeatSignal: false,
        lastKeyDownAt: 0,
        keyDownSilenceMs: 350,
        continuousInitialDelayMs: 140,
        intervalMs: 32,
        lineBoundaryHoldMs: 65,
        lineBoundaryUntil: 0,
        releaseGuardMs: 250,
        releasedKeys: new Map(),
        suppressBeforeInputUntil: 0,
        suppressBeforeInputTypes: new Set()
    },
    originalLines: [],
    dirtyLines: new Map(),
    csvVirtualLineCount: 0,
    longLineProtectionFormat: '... too long ({0} characters total)'
};

state.lineEndStacks = new Map();
state.htmlLineEndContexts = new Map();

const originalSet = state.cache.set;
state.cache.set = function(key, value) {
    originalSet.call(state.cache, key, value);
    invalidateLineEndStacks(key);
    invalidateHtmlLineEndContexts(key);
    return this;
};
const originalDelete = state.cache.delete;
state.cache.delete = function(key) {
    const res = originalDelete.call(state.cache, key);
    invalidateLineEndStacks(key);
    invalidateHtmlLineEndContexts(key);
    return res;
};
const originalClear = state.cache.clear;
state.cache.clear = function() {
    originalClear.call(state.cache);
    if (state.lineEndStacks) state.lineEndStacks.clear();
    if (state.htmlLineEndContexts) state.htmlLineEndContexts.clear();
};

function invalidateLineEndStacks(startLine) {
    if (!state.lineEndStacks || state.lineEndStacks.size === 0) return;
    if (startLine <= 1) {
        state.lineEndStacks.clear();
        return;
    }
    for (const key of state.lineEndStacks.keys()) {
        if (key >= startLine) {
            state.lineEndStacks.delete(key);
        }
    }
}

function invalidateHtmlLineEndContexts(startLine) {
    if (!state.htmlLineEndContexts || state.htmlLineEndContexts.size === 0) return;
    if (startLine <= 1) {
        state.htmlLineEndContexts.clear();
        return;
    }
    for (const key of state.htmlLineEndContexts.keys()) {
        if (key >= startLine) {
            state.htmlLineEndContexts.delete(key);
        }
    }
}

function post(msg) {
    if (msg?.type === 'contentChanged') {
        state.documentVersion++;
        if (!msg.isComposing && state.hostDocumentId) {
            state.hostDocumentVersion++;
        }
        state.searchDocumentVersion = -1;
    }

    const outgoing = state.hostDocumentId
        ? {
            protocolVersion: 1,
            documentId: state.hostDocumentId,
            viewId: state.viewId,
            documentVersion: state.hostDocumentVersion,
            sequence: ++state.messageSequence,
            ...msg
        }
        : msg;

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(outgoing);
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

function applyOptions(msg) {
    const theme = msg.theme || 'Dark';
    const bg = msg.customBackgroundColor || (theme === 'PastelDark' ? '#24273a' : (theme === 'Light' ? '#ffffff' : '#1e1e1e'));
    const preferredFg = msg.customForegroundColor || (theme === 'PastelDark' ? '#cad3f5' : (theme === 'Light' ? '#111111' : '#d4d4d4'));
    const fg = resolveReadableColor(bg, preferredFg, theme === 'PastelDark' ? '#cad3f5' : (theme === 'Light' ? '#111111' : '#d4d4d4'));
    const fontSize = Number(msg.fontSize || 14);
    const baseLineHeight = Math.max(18, Math.ceil(fontSize + 8));
    const previousLineHeight = state.lineHeight;
    state.lineHeight = snapCssPixelsToDevicePixels(baseLineHeight);
    state.tabSize = Number(msg.tabSize || 4);
    state.readOnly = !!msg.readOnly;
    state.hexEditable = !!msg.hexEditable;
    state.wordWrap = !!msg.wordWrap;
    state.syntaxHighlighting = msg.hasOwnProperty('syntaxHighlighting') ? !!msg.syntaxHighlighting : true;
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
    document.documentElement.style.setProperty('--line-height', `${state.lineHeight}px`);
    document.documentElement.style.setProperty('--wrap', state.wordWrap ? 'break-spaces' : 'pre');
    document.body.classList.toggle('wrap-enabled', state.wordWrap);

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

    if (!state.wordWrap || previousLineHeight !== state.lineHeight) {
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
    state.csvVirtualLineCount = 0;
    state.cache.clear();
    state.pending.clear();
    state.preservedScrollTop = null;
    clearMeasuredLineHeights();
    state.cacheVersion++;
    state.documentVersion++;
    state.searchDocumentVersion = -1;
    state.pendingSearchNavigation = null;
    state.hexSelection = null;
    state.hexSelectionAnchorOffset = null;
    state.hexCursorOffset = 0;
    state.lastRangeKey = '';
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
        if ((state.isComposing && (!state.compositionLine || state.compositionLine === lineNumber)) ||
            (state.isComposing && runtime.isLineInColumnComposition(lineNumber)) ||
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
    for (const key of [...state.pending]) {
        const [pendingStart, pendingCount] = key.split(':').map(Number);
        const pendingEnd = pendingStart + pendingCount - 1;
        const receivedEnd = start + safeLines.length - 1;
        if (start <= pendingStart && (safeLines.length === 0 || receivedEnd >= pendingEnd || receivedEnd >= state.lineCount)) {
            state.pending.delete(key);
        }
    }
    return safeLines.length;
}

function updateLineFromHost(lineNumber, text, isComposing = false) {
    const line = Number(lineNumber || 1);
    if (!line || line < 1) return false;

    if ((state.isComposing && (!state.compositionLine || state.compositionLine === line)) ||
        (state.textareaImeBypassActive && state.bypassStartLine === line) ||
        (state.isComposing && runtime.isLineInColumnComposition(line)) ||
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
    state.searchDocumentVersion = -1;
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

    if (!state.isComposing && !isComposing) {
        queueRender();
    } else {
        runtime.drawEditableSelectionOverlays();
    }

    return true;
}

function applyEditResultFromHost(startLine, oldLineCount, lines, documentLineCount, caret = null) {
    if (state.isComposing || state.textareaImeBypassActive) {
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

    state.selection = null;
    state.hexSelection = null;
    state.hexSelectionAnchorOffset = null;
    state.hexCursorOffset = 0;
    try {
        if (hasExplicitCaret) {
            window.getSelection()?.removeAllRanges();
        }
    } catch (e) { }
    clearCustomSelectionVisuals();
    syncCustomSelectionClass();

    for (let line = start; line < start + removeCount; line++) {
        state.cache.delete(line);
        state.dirtyLines.delete(line);
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
    state.searchDocumentVersion = -1;
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
            if (document.hasFocus() && !state.isComposing && !state.textareaImeBypassActive) {
                runtime.focusLine(caretLine, caretColumn);
            }
        }, 20);
    } else if (canRestoreHostCaret && savedCaretLine > 0) {
        const caretLine = Math.min(state.lineCount, Math.max(1, Number(savedCaretLine)));
        const caretColumn = Math.max(0, Number(savedCaretColumn || 1) - 1);
        setTimeout(() => {
            if (document.hasFocus() && !state.isComposing && !state.textareaImeBypassActive) {
                runtime.focusLine(caretLine, caretColumn);
            }
        }, 20);
    }

    return true;
}

function setupVirtualHeight() {
    const savedScroll = scrollContainer.scrollTop;
    const preservedHeight = state.preservedScrollTop !== null
        ? Math.max(0, Number(state.preservedScrollTop || 0)) + scrollContainer.clientHeight
        : 0;
    virtualSpacer.style.height = `${Math.max(totalVirtualHeight(), preservedHeight)}px`;
    const maxScroll = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
    if (savedScroll > maxScroll) {
        scrollContainer.scrollTop = maxScroll;
    }
}

function preserveScrollTop(scrollTop) {
    const nextScrollTop = Math.max(0, Number(scrollTop || 0));
    state.preservedScrollTop = Math.max(Number(state.preservedScrollTop ?? 0), nextScrollTop);
    setupVirtualHeight();
}

function clearPreservedScrollTop() {
    if (state.preservedScrollTop === null) return;
    state.preservedScrollTop = null;
    setupVirtualHeight();
}

function usesMeasuredLineHeights() {
    return state.wordWrap || state.inlineLivePreviewEnabled;
}

function lineHeightFor(lineNumber) {
    return usesMeasuredLineHeights() ? (state.lineHeights.get(lineNumber) || state.lineHeight) : state.lineHeight;
}

function createLineHeightIndex() {
    const size = Math.max(2, state.lineCount + 2);
    return {
        size,
        tree: new Map(),
        totalDelta: 0
    };
}

function measuredLineHeightDelta(height) {
    return Math.max(0, Number(height || 0)) - state.lineHeight;
}

function resetLineHeightIndex() {
    state.lineHeightIndex = createLineHeightIndex();
}

function rebuildLineHeightIndex() {
    const index = createLineHeightIndex();
    state.lineHeightIndex = index;
    for (const [line, height] of state.lineHeights.entries()) {
        addMeasuredLineHeightDelta(line, measuredLineHeightDelta(height));
    }
}

function ensureLineHeightIndex() {
    const requiredSize = Math.max(2, state.lineCount + 2);
    if (!state.lineHeightIndex || state.lineHeightIndex.size < requiredSize) {
        rebuildLineHeightIndex();
    }
}

function addMeasuredLineHeightDelta(lineNumber, delta) {
    if (!delta) return;
    ensureLineHeightIndex();
    const index = state.lineHeightIndex;
    const line = Math.max(1, Math.min(Number(lineNumber || 1), index.size));
    for (let i = line; i <= index.size; i += i & -i) {
        const next = (index.tree.get(i) || 0) + delta;
        if (Math.abs(next) < 0.0001) {
            index.tree.delete(i);
        } else {
            index.tree.set(i, next);
        }
    }
    index.totalDelta += delta;
}

function measuredLineHeightDeltaBefore(lineNumber) {
    if (!usesMeasuredLineHeights() || state.lineHeights.size === 0) return 0;
    ensureLineHeightIndex();
    const index = state.lineHeightIndex;
    let i = Math.max(0, Math.min(Number(lineNumber || 1) - 1, index.size));
    let sum = 0;
    while (i > 0) {
        sum += index.tree.get(i) || 0;
        i -= i & -i;
    }
    return sum;
}

function setMeasuredLineHeight(lineNumber, height) {
    const line = Math.max(1, Number(lineNumber || 1));
    const measured = Math.max(0, Number(height || 0));
    const previous = state.lineHeights.get(line);
    if (previous === measured) return false;

    state.lineHeights.set(line, measured);
    const previousDelta = previous === undefined ? 0 : measuredLineHeightDelta(previous);
    const nextDelta = measuredLineHeightDelta(measured);
    addMeasuredLineHeightDelta(line, nextDelta - previousDelta);
    return true;
}

function deleteMeasuredLineHeight(lineNumber) {
    const line = Math.max(1, Number(lineNumber || 1));
    const previous = state.lineHeights.get(line);
    if (previous === undefined) return false;

    state.lineHeights.delete(line);
    addMeasuredLineHeightDelta(line, -measuredLineHeightDelta(previous));
    return true;
}

function clearMeasuredLineHeights() {
    state.lineHeights.clear();
    resetLineHeightIndex();
}

function shiftMeasuredLineHeights(fromLine, delta) {
    const entries = [...state.lineHeights.entries()]
        .filter(([line]) => line >= fromLine)
        .sort((a, b) => delta > 0 ? b[0] - a[0] : a[0] - b[0]);
    if (entries.length === 0) return;

    for (const [line] of entries) {
        state.lineHeights.delete(line);
    }
    for (const [line, value] of entries) {
        const nextLine = line + delta;
        if (nextLine >= 1 && nextLine <= state.lineCount + Math.max(delta, 0)) {
            state.lineHeights.set(nextLine, value);
        }
    }
    rebuildLineHeightIndex();
}

function totalVirtualHeight() {
    const total = rawTotalVirtualHeight();
    if (usesCompressedScroll()) {
        const viewHeight = Math.max(scrollContainer.clientHeight, state.lineHeight);
        return Math.max(viewHeight + state.lineHeight, Math.min(total, BROWSER_SCROLL_HEIGHT_LIMIT));
    }

    return total;
}

function rawTotalVirtualHeight() {
    let total = effectiveLineCount() * state.lineHeight;
    if (usesMeasuredLineHeights()) {
        ensureLineHeightIndex();
        total += state.lineHeightIndex.totalDelta;
    }
    return Math.max(1, total);
}

function lineTop(lineNumber) {
    if (usesCompressedScroll()) {
        const metrics = compressedScrollMetrics();
        if (metrics.maxScrollTop <= 0 || metrics.maxFirstLine <= 1) return 0;
        const line = Math.min(metrics.maxFirstLine, Math.max(1, Math.floor(Number(lineNumber || 1))));
        return ((line - 1) / (metrics.maxFirstLine - 1)) * metrics.maxScrollTop;
    }

    let top = (Math.max(1, lineNumber) - 1) * state.lineHeight;
    top += measuredLineHeightDeltaBefore(lineNumber);
    return Math.max(0, top);
}

function lineAt(scrollTop) {
    const lineCount = effectiveLineCount();
    if (usesCompressedScroll()) {
        const metrics = compressedScrollMetrics();
        if (metrics.maxScrollTop <= 0 || metrics.maxFirstLine <= 1) return 1;
        const ratio = Math.max(0, Math.min(1, Number(scrollTop || 0) / metrics.maxScrollTop));
        return Math.min(metrics.maxFirstLine, Math.max(1, Math.floor(ratio * (metrics.maxFirstLine - 1)) + 1));
    }

    if (!usesMeasuredLineHeights() || state.lineHeights.size === 0) {
        return Math.min(lineCount, Math.max(1, Math.floor(scrollTop / state.lineHeight) + 1));
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
    const viewHeight = Math.max(scrollContainer.clientHeight, state.lineHeight);
    const lineCount = effectiveLineCount();
    const firstVisible = lineAt(scrollContainer.scrollTop);
    if (usesCompressedScroll()) {
        const visibleRows = Math.max(1, Math.ceil(viewHeight / state.lineHeight) + 1);
        const overscan = state.language === 'hex' ? HEX_RENDER_OVERSCAN : state.overscan;
        const start = Math.max(1, firstVisible - overscan);
        const end = Math.min(lineCount, firstVisible + visibleRows + overscan);
        return { start, end, count: Math.max(0, end - start + 1) };
    }

    const lastVisible = lineAt(scrollContainer.scrollTop + viewHeight);
    const overscan = state.language === 'hex' ? HEX_RENDER_OVERSCAN : state.overscan;
    const windowStep = Math.max(1, Math.floor(overscan / 2));
    const windowAnchor = Math.floor((firstVisible - 1) / windowStep) * windowStep + 1;
    const visibleLineCount = Math.max(1, lastVisible - firstVisible + 1);
    const start = Math.max(1, windowAnchor - overscan);
    const end = Math.min(lineCount, start + visibleLineCount + (overscan * 2) + windowStep - 1);
    return { start, end, count: Math.max(0, end - start + 1) };
}

function usesCompressedScroll() {
    return !state.csvTableEnabled &&
        rawTotalVirtualHeight() > BROWSER_SCROLL_HEIGHT_LIMIT;
}

function compressedScrollMetrics() {
    const viewHeight = Math.max(scrollContainer.clientHeight, state.lineHeight);
    const visibleRows = Math.max(1, Math.ceil(viewHeight / state.lineHeight));
    const lineCount = effectiveLineCount();
    const maxFirstLine = Math.max(1, lineCount - visibleRows + 1);
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
        : firstVisibleTop + state.lineHeight;
    const virtualLineSpan = Math.max(0.0001, nextVisibleTop - firstVisibleTop);
    const scrollOffset = Math.max(0, scrollContainer.scrollTop - firstVisibleTop);
    const physicalOffset = Math.max(0, Math.min(1, scrollOffset / virtualLineSpan)) * state.lineHeight;
    return scrollContainer.scrollTop -
        physicalOffset -
        ((firstVisible - Math.max(1, Number(startLine || 1))) * state.lineHeight);
}

function effectiveLineCount() {
    if (state.csvTableEnabled && Number(state.csvVirtualLineCount || 0) > 0) {
        return Math.max(1, Number(state.csvVirtualLineCount || 1));
    }

    return Math.max(1, state.lineCount);
}

function requestLines(start, count) {
    if (count <= 0) return;
    const key = `${start}:${count}`;
    if (state.pending.has(key)) return;
    state.pending.add(key);
    post({
        type: 'requestLines',
        requestId: state.requestSeq++,
        startLine: start,
        count
    });
}

function requestMissingLines(start, end) {
    const sourceLineCount = Math.max(1, Number(state.lineCount || 1));
    if (Number(start || 1) > sourceLineCount) return;

    start = Math.max(1, Number(start || 1));
    end = Math.min(sourceLineCount, Math.max(start, Number(end || start)));

    let missingStart = 0;
    let missingCount = 0;
    for (let line = start; line <= end; line++) {
        if (!state.cache.has(line)) {
            if (missingStart === 0) {
                missingStart = line;
                missingCount = 1;
            } else {
                missingCount++;
            }
        } else if (missingStart !== 0) {
            requestLines(missingStart, Math.max(missingCount, MIN_BATCH_SIZE));
            missingStart = 0;
            missingCount = 0;
        }
    }
    if (missingStart !== 0) {
        requestLines(missingStart, Math.max(missingCount, MIN_BATCH_SIZE));
    }
}

function prefetchAround(scrollTop) {
    const viewHeight = Math.max(scrollContainer.clientHeight, state.lineHeight);
    const firstVisible = lineAt(scrollTop);
    const prefetchAhead = state.language === 'hex' ? HEX_PREFETCH_AHEAD : PREFETCH_AHEAD;
    if (usesCompressedScroll()) {
        const visibleRows = Math.max(1, Math.ceil(viewHeight / state.lineHeight) + 1);
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
    const rawMaxScrollTop = Math.max(0, rawTotalVirtualHeight() - metrics.viewHeight);
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
        : top + state.lineHeight;
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
        : top + state.lineHeight;
    const ratio = Math.max(0, Math.min(1, Number(anchor.ratio || 0)));
    const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
    scrollContainer.scrollTop = Math.min(maxScrollTop, Math.max(0, top + ((nextTop - top) * ratio)));
}

function trimHexCacheToRange(startLine, endLine) {
    if (state.language !== 'hex' || state.cache.size <= HEX_CACHE_RETAIN_LINES) return;

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
    if (force) {
        state.lastRangeKey = '';
    }
    if (state.renderQueued) return;
    state.renderQueued = true;
    requestAnimationFrame(() => {
        state.renderQueued = false;
        runtime.render();
    });
}

function measureRenderedRows(renderOnChange = true) {
    if (!usesMeasuredLineHeights()) return;

    const anchorLine = lineAt(scrollContainer.scrollTop);
    const anchorOffset = scrollContainer.scrollTop - lineTop(anchorLine);
    const oldEditingLineTop = state.editingLine ? lineTop(state.editingLine) : null;
    let changed = false;
    for (const row of viewport.querySelectorAll('.line-row')) {
        const lineNumber = Number(row.dataset.line || 0);
        if (!lineNumber) continue;
        const isSkipped = row.classList.contains('live-preview-skipped');
        const minimum = isSkipped ? 0 : state.lineHeight;
        const measuredHeight = Math.max(row.getBoundingClientRect().height || 0, row.scrollHeight || 0);
        const measured = Math.max(minimum, Math.ceil(measuredHeight));
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
            state.lastRangeKey = '';
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
        state.lastRangeKey = '';
        setupVirtualHeight();
    }
}

function shiftCachedLines(fromLine, delta) {
    shiftLineMap(state.cache, fromLine, delta);
    shiftMeasuredLineHeights(fromLine, delta);
    shiftLineMap(state.dirtyLines, fromLine, delta);
}

function shiftLineMap(map, fromLine, delta) {
    const entries = [...map.entries()]
        .filter(([line]) => line >= fromLine)
        .sort((a, b) => delta > 0 ? b[0] - a[0] : a[0] - b[0]);
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

function setOriginalLines(lines) {
    state.originalLines = Array.isArray(lines) ? lines.slice() : [];
    state.dirtyLines.clear();
}

function markDirty(lineNumber, type) {
    const existing = state.dirtyLines.get(lineNumber);
    if (existing === 'add') return;
    state.dirtyLines.set(lineNumber, type || 'mod');
}

function cleanDirtyMarker(lineNumber) {
    const origIdx = lineNumber - 1;
    if (origIdx >= 0 && origIdx < state.originalLines.length) {
        const currentText = state.cache.get(lineNumber);
        if (currentText !== undefined && currentText === state.originalLines[origIdx]) {
            state.dirtyLines.delete(lineNumber);
            return true;
        }
    }
    return false;
}
function recomputeDirtyLines() {
    const orig = state.originalLines || [];
    const current = [];
    for (let i = 1; i <= state.lineCount; i++) {
        current.push(state.cache.get(i) ?? '');
    }

    state.dirtyLines = computeDirtyLineMarkers(orig, current);
}

function computeDirtyLineMarkers(orig, current) {
    const markers = new Map();

    let prefixMatchCount = 0;
    const maxPrefix = Math.min(orig.length, current.length);
    while (prefixMatchCount < maxPrefix && orig[prefixMatchCount] === current[prefixMatchCount]) {
        prefixMatchCount++;
    }

    let suffixMatchCount = 0;
    const maxSuffix = Math.min(orig.length - prefixMatchCount, current.length - prefixMatchCount);
    while (suffixMatchCount < maxSuffix &&
           orig[orig.length - 1 - suffixMatchCount] === current[current.length - 1 - suffixMatchCount]) {
        suffixMatchCount++;
    }

    const unmatchedOrigCount = orig.length - prefixMatchCount - suffixMatchCount;
    const unmatchedCurrentCount = current.length - prefixMatchCount - suffixMatchCount;
    const limitOrig = orig.length - suffixMatchCount;
    const limitCurr = current.length - suffixMatchCount;

    if (unmatchedOrigCount === 0) {
        for (let line = prefixMatchCount; line < limitCurr; line++) {
            markers.set(line + 1, 'add');
        }
        return markers;
    }

    if (unmatchedCurrentCount === 0) {
        markDeletionMarker(markers, prefixMatchCount, 0, current.length);
        return markers;
    }

    const cellCount = unmatchedOrigCount * unmatchedCurrentCount;
    if (cellCount <= MAX_DIRTY_DIFF_CELLS) {
        const matches = computeLcsMatches(orig, prefixMatchCount, limitOrig, current, prefixMatchCount, limitCurr);
        let previousOrig = prefixMatchCount;
        let previousCurrent = prefixMatchCount;

        for (const match of matches) {
            addDirtyMarkersForGap(
                markers,
                previousOrig,
                match.origIndex,
                previousCurrent,
                match.currentIndex,
                current.length);
            previousOrig = match.origIndex + 1;
            previousCurrent = match.currentIndex + 1;
        }

        addDirtyMarkersForGap(
            markers,
            previousOrig,
            limitOrig,
            previousCurrent,
            limitCurr,
            current.length);

        return markers;
    }

    return computeDirtyLineMarkersGreedy(
        orig,
        current,
        prefixMatchCount,
        suffixMatchCount,
        unmatchedOrigCount,
        unmatchedCurrentCount);
}

function computeLcsMatches(orig, origStart, origEnd, current, currentStart, currentEnd) {
    const origCount = origEnd - origStart;
    const currentCount = currentEnd - currentStart;
    const columns = currentCount + 1;
    const table = new Uint32Array((origCount + 1) * columns);

    for (let oi = origCount - 1; oi >= 0; oi--) {
        for (let ci = currentCount - 1; ci >= 0; ci--) {
            const index = oi * columns + ci;
            table[index] = orig[origStart + oi] === current[currentStart + ci]
                ? table[(oi + 1) * columns + ci + 1] + 1
                : Math.max(table[(oi + 1) * columns + ci], table[oi * columns + ci + 1]);
        }
    }

    const matches = [];
    let oi = 0;
    let ci = 0;
    while (oi < origCount && ci < currentCount) {
        if (orig[origStart + oi] === current[currentStart + ci]) {
            matches.push({ origIndex: origStart + oi, currentIndex: currentStart + ci });
            oi++;
            ci++;
        } else if (table[(oi + 1) * columns + ci] >= table[oi * columns + ci + 1]) {
            oi++;
        } else {
            ci++;
        }
    }

    return matches;
}

function addDirtyMarkersForGap(markers, origStart, origEnd, currentStart, currentEnd, currentLineCount) {
    const deletedCount = origEnd - origStart;
    const insertedCount = currentEnd - currentStart;
    if (deletedCount <= 0 && insertedCount <= 0) return;

    const modifiedCount = Math.min(deletedCount, insertedCount);
    for (let i = 0; i < modifiedCount; i++) {
        markers.set(currentStart + i + 1, 'mod');
    }

    for (let i = modifiedCount; i < insertedCount; i++) {
        markers.set(currentStart + i + 1, 'add');
    }

    if (deletedCount > insertedCount) {
        markDeletionMarker(markers, currentStart, insertedCount, currentLineCount);
    }
}

function markDeletionMarker(markers, currentStart, insertedCount, currentLineCount) {
    if (currentLineCount <= 0) return;

    let markerLine;
    if (insertedCount === 0) {
        markerLine = Math.max(1, Math.min(currentStart, currentLineCount));
    } else if (currentStart + insertedCount < currentLineCount) {
        markerLine = currentStart + insertedCount + 1;
    } else {
        markerLine = Math.max(1, Math.min(currentStart + insertedCount, currentLineCount));
    }

    if (!markers.has(markerLine)) {
        markers.set(markerLine, 'del');
    }
}

function computeDirtyLineMarkersGreedy(
    orig,
    current,
    prefixMatchCount,
    suffixMatchCount,
    unmatchedOrigCount,
    unmatchedCurrentCount) {
    const markers = new Map();

    let oi = prefixMatchCount;
    let ci = prefixMatchCount;
    const limitOrig = orig.length - suffixMatchCount;
    const limitCurr = current.length - suffixMatchCount;

    const scanLimit = Math.max(100, Math.abs(unmatchedOrigCount - unmatchedCurrentCount) + 10);

    while (oi < limitOrig && ci < limitCurr) {
        if (orig[oi] === current[ci]) {
            oi++; ci++;
        } else {
            let aheadOrig = -1;
            for (let s = oi + 1; s < Math.min(oi + scanLimit, limitOrig); s++) {
                if (orig[s] === current[ci]) { aheadOrig = s; break; }
            }
            let aheadCurr = -1;
            for (let s = ci + 1; s < Math.min(ci + scanLimit, limitCurr); s++) {
                if (current[s] === orig[oi]) { aheadCurr = s; break; }
            }

            if (aheadOrig >= 0 && (aheadCurr < 0 || (aheadOrig - oi) < (aheadCurr - ci))) {
                markers.set(ci + 1, 'del');
                oi = aheadOrig;
            } else if (aheadCurr >= 0) {
                for (let a = ci; a < aheadCurr; a++) {
                    markers.set(a + 1, 'add');
                }
                ci = aheadCurr;
            } else {
                markers.set(ci + 1, 'mod');
                oi++; ci++;
            }
        }
    }

    if (oi < limitOrig && state.lineCount >= 1) {
        markers.set(limitCurr, 'del');
    }

    while (ci < limitCurr) {
        markers.set(ci + 1, 'add');
        ci++;
    }

    return markers;
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
        if (state.selection && state.selection.end) {
            state.currentLine = state.selection.end.line;
            state.currentColumn = state.selection.end.column + 1;
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
    if (state.language === 'hex') {
        return hexSelectionInfo();
    }

    const selection = runtime.normalizeSelection();
    if (selection && runtime.hasCustomSelection()) {
        return selectionTextFromModel(selection);
    }

    return nativeSelectionTextFromModel() ?? { text: window.getSelection()?.toString() || '', startLine: 0, endLine: 0 };
}

function selectedText() {
    if (state.language === 'hex') {
        return hexSelectedText();
    }

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

function normalizedHexSelection(selection = state.hexSelection) {
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
    if (event.isComposing || state.isComposing || event.key === 'Process' || event.keyCode === 229) return false;
    if (containsHangulInputText(event.key)) return false;
    return typeof event.key === 'string' && event.key.length === 1;
}

function containsHangulInputText(value) {
    return /[\u1100-\u11FF\u3130-\u318F\uA960-\uA97F\uD7B0-\uD7FF\uAC00-\uD7A3]/.test(String(value ?? ''));
}

function isHangulImeKeyEvent(event) {
    if (!event || event.ctrlKey || event.metaKey || event.altKey) return false;
    return !!(event.isComposing || state.isComposing ||
        event.key === 'Process' || event.keyCode === 229 ||
        containsHangulInputText(event.key));
}

function syncCustomSelectionClass() {
    const hasSelection = runtime.hasCustomSelection();
    document.body.classList.toggle('custom-selection-active', hasSelection);
    if (!hasSelection && !state.rangeComposition?.deferred) {
        state.preparedRangeCompositionLine = null;
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

async function readClipboardText() {
    if (window.chrome && window.chrome.webview) {
        return await new Promise(resolve => {
            const requestId = state.requestSeq++;
            const timer = setTimeout(() => {
                state.clipboardRequests.delete(requestId);
                resolve('');
            }, 1200);
            state.clipboardRequests.set(requestId, { resolve, timer });
            post({ type: 'clipboardRead', requestId });
        });
    }

    if (navigator.clipboard?.readText) {
        try {
            return (await navigator.clipboard.readText()).replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        } catch { }
    }

    return await new Promise(resolve => {
        const requestId = state.requestSeq++;
        const timer = setTimeout(() => {
            state.clipboardRequests.delete(requestId);
            resolve('');
        }, 1200);
        state.clipboardRequests.set(requestId, { resolve, timer });
        post({ type: 'clipboardRead', requestId });
    });
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
    cleanDirtyMarker,
    clearPreservedScrollTop,
    clearMeasuredLineHeights,
    clearCustomSelectionVisuals,
    comparePositions,
    compressedScrollMetrics,
    configureEditorCoreRuntime,
    containsHangulInputText,
    escapeHtml,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasTextAt,
    isHangulImeKeyEvent,
    isPlainTextKey,
    isStandaloneDelimiter,
    lineAt,
    lineCommentSyntax,
    lineHeightFor,
    lineTop,
    invalidateMeasuredLineHeightsAround,
    markDirty,
    measureRenderedRows,
    orderedRange,
    post,
    prefetchAround,
    preserveScrollTop,
    queueRender,
    readClipboardText,
    receiveLineBlock,
    recomputeDirtyLines,
    reportCursorAndSelection,
    requestLines,
    requestMissingLines,
    restoreScrollAnchor,
    selectedLineRange,
    selectionInfo,
    selectedText,
    setOriginalLines,
    setupModel,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    trimHexCacheToRange,
    totalVirtualHeight,
    updateLineFromHost,
    usesCompressedScroll,
    usesMeasuredLineHeights,
    visualScrollDeltaToScrollTopDelta,
    visibleRange,
    viewportTopForLine,
    writeClipboardText
};
