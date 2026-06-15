import { contextMenu, scrollContainer, viewport, virtualSpacer } from './editor-dom.js';

const MAX_RENDER_CHARS = 20000;
const MIN_BATCH_SIZE = 100;
const PREFETCH_AHEAD = 200;

const runtime = {
    drawEditableSelectionOverlays: () => { },
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
    overscan: 200,
    cache: new Map(),
    pending: new Set(),
    lineHeights: new Map(),
    requestSeq: 1,
    currentLine: 1,
    currentColumn: 1,
    readOnly: false,
    wordWrap: false,
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
    isSelecting: false,
    isLineSelecting: false,
    initialized: false,
    lastRangeKey: '',
    cacheVersion: 0,
    renderQueued: false,
    clipboardRequests: new Map(),
    pendingLineActions: [],
    autocompleteOnEnter: true,
    autocompleteOnTab: true,
    snippets: [],
    scrollSyncEnabled: true,
    inlineLivePreviewEnabled: false,
    livePreviewBaseHref: '',
    inlineLivePreviewSourceLine: null,
    inlineLivePreviewEditableBlock: null,
    dragStartPosition: null,
    isDragPotential: false,
    isDragMoving: false,
    dragSelectionData: null,
    dragDropPosition: null,
    isDragCopy: false,
    isComposing: false,
    compositionLine: null,
    columnComposition: null,
    pendingImeSelectionCollapse: null,
    suppressNextBeforeInputType: null,
    lastManualDeleteAt: 0,
    editingLine: null,
    lastDeleteKeyDown: null,
    repeatEdit: {
        lastRunAt: 0,
        timer: 0,
        pending: null,
        intervalMs: 28,
        lineBoundaryHoldMs: 65,
        lineBoundaryUntil: 0,
        suppressBeforeInputUntil: 0,
        suppressBeforeInputTypes: new Set()
    },
    originalLines: [],
    dirtyLines: new Map()
};

state.lineEndStacks = new Map();

const originalSet = state.cache.set;
state.cache.set = function(key, value) {
    originalSet.call(state.cache, key, value);
    invalidateLineEndStacks(key);
    return this;
};
const originalDelete = state.cache.delete;
state.cache.delete = function(key) {
    const res = originalDelete.call(state.cache, key);
    invalidateLineEndStacks(key);
    return res;
};
const originalClear = state.cache.clear;
state.cache.clear = function() {
    originalClear.call(state.cache);
    if (state.lineEndStacks) state.lineEndStacks.clear();
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

function post(msg) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(msg);
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

function graphemeDeleteStart(text, caret) {
    if (caret <= 0) return 0;
    if (typeof Intl !== 'undefined' && Intl.Segmenter) {
        try {
            const s = new Intl.Segmenter('en', { granularity: 'grapheme' });
            const segs = [...s.segment(text.slice(0, caret))];
            return segs.length > 0 ? segs[segs.length - 1].index : 0;
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
    if (typeof Intl !== 'undefined' && Intl.Segmenter) {
        try {
            const s = new Intl.Segmenter('en', { granularity: 'grapheme' });
            const segs = [...s.segment(text.slice(caret))];
            if (segs.length > 0) return caret + segs[0].segment.length;
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
    const bg = msg.customBackgroundColor || (theme === 'Light' ? '#ffffff' : '#1e1e1e');
    const preferredFg = msg.customForegroundColor || (theme === 'Light' ? '#111111' : '#d4d4d4');
    const fg = resolveReadableColor(bg, preferredFg, theme === 'Light' ? '#111111' : '#d4d4d4');
    const fontSize = Number(msg.fontSize || 14);
    const baseLineHeight = Math.max(18, Math.ceil(fontSize + 8));
    state.lineHeight = snapCssPixelsToDevicePixels(baseLineHeight);
    state.tabSize = Number(msg.tabSize || 4);
    state.readOnly = !!msg.readOnly;
    state.wordWrap = !!msg.wordWrap;
    state.bracketPairColorization = msg.hasOwnProperty('bracketPairColorization') ? !!msg.bracketPairColorization : true;
    state.autocompleteOnEnter = msg.hasOwnProperty('autocompleteOnEnter') ? !!msg.autocompleteOnEnter : true;
    state.autocompleteOnTab = msg.hasOwnProperty('autocompleteOnTab') ? !!msg.autocompleteOnTab : true;

    document.documentElement.style.setProperty('--bg', bg);
    document.documentElement.style.setProperty('--fg', fg);
    document.documentElement.style.setProperty('--gutter-bg', theme === 'Light' ? '#f3f3f3' : '#252526');
    document.documentElement.style.setProperty('--gutter-fg', theme === 'Light' ? '#6b6b6b' : '#858585');
    document.documentElement.style.setProperty('--selection', theme === 'Light' ? 'rgba(0, 95, 184, 0.28)' : 'rgba(0, 120, 212, 0.38)');
    document.documentElement.style.setProperty('--preview-code-bg', theme === 'Light' ? '#f3f5f7' : '#2d2d2d');
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
    }

    if (!state.wordWrap) {
        state.lineHeights.clear();
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
    state.cache.clear();
    state.pending.clear();
    state.lineHeights.clear();
    state.cacheVersion++;
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
            (state.editingLine === lineNumber && document.activeElement?.closest?.('.line-text')?.dataset.line === String(lineNumber))) {
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
        (state.isComposing && runtime.isLineInColumnComposition(line)) ||
        (state.inlineLivePreviewEnabled && state.inlineLivePreviewSourceLine === line)) {
        return false;
    }

    const activeLineElement = document.activeElement?.closest?.('.line-text');
    if (state.editingLine === line && activeLineElement?.dataset.line === String(line)) {
        return false;
    }

    const nextText = String(text ?? '');
    state.cache.set(line, nextText);
    state.cacheVersion++;
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

function setupVirtualHeight() {
    virtualSpacer.style.height = `${totalVirtualHeight()}px`;
}

function usesMeasuredLineHeights() {
    return state.wordWrap || state.inlineLivePreviewEnabled;
}

function lineHeightFor(lineNumber) {
    return usesMeasuredLineHeights() ? (state.lineHeights.get(lineNumber) || state.lineHeight) : state.lineHeight;
}

function totalVirtualHeight() {
    let total = Math.max(1, state.lineCount) * state.lineHeight;
    if (usesMeasuredLineHeights()) {
        for (const height of state.lineHeights.values()) {
            total += Math.max(0, height) - state.lineHeight;
        }
    }
    return Math.max(1, total);
}

function lineTop(lineNumber) {
    let top = (Math.max(1, lineNumber) - 1) * state.lineHeight;
    if (usesMeasuredLineHeights()) {
        for (const [line, height] of state.lineHeights.entries()) {
            if (line < lineNumber) {
                top += Math.max(0, height) - state.lineHeight;
            }
        }
    }
    return Math.max(0, top);
}

function lineAt(scrollTop) {
    let line = Math.min(state.lineCount, Math.max(1, Math.floor(scrollTop / state.lineHeight) + 1));
    while (line < state.lineCount && lineTop(line + 1) <= scrollTop) {
        line++;
    }
    while (line > 1 && lineTop(line) > scrollTop) {
        line--;
    }
    return line;
}

function visibleRange() {
    const viewHeight = Math.max(scrollContainer.clientHeight, state.lineHeight);
    const firstVisible = lineAt(scrollContainer.scrollTop);
    const lastVisible = lineAt(scrollContainer.scrollTop + viewHeight);
    const start = Math.max(1, firstVisible - state.overscan);
    const end = Math.min(state.lineCount, lastVisible + state.overscan);
    return { start, end, count: Math.max(0, end - start + 1) };
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
    const lastVisible = lineAt(scrollTop + viewHeight);
    const prefetchStart = Math.max(1, firstVisible - PREFETCH_AHEAD);
    const prefetchEnd = Math.min(state.lineCount, lastVisible + PREFETCH_AHEAD);
    requestMissingLines(prefetchStart, prefetchEnd);
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
        if (state.lineHeights.get(lineNumber) !== measured) {
            state.lineHeights.set(lineNumber, measured);
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
        changed = state.lineHeights.delete(line) || changed;
    }

    if (changed) {
        state.lastRangeKey = '';
        setupVirtualHeight();
    }
}

function shiftCachedLines(fromLine, delta) {
    shiftLineMap(state.cache, fromLine, delta);
    shiftLineMap(state.lineHeights, fromLine, delta);
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
    const markers = new Map();
    const orig = state.originalLines || [];
    const current = [];
    for (let i = 1; i <= state.lineCount; i++) {
        current.push(state.cache.get(i) ?? '');
    }

    // Find common prefix
    let prefixMatchCount = 0;
    const maxPrefix = Math.min(orig.length, current.length);
    while (prefixMatchCount < maxPrefix && orig[prefixMatchCount] === current[prefixMatchCount]) {
        prefixMatchCount++;
    }

    // Find common suffix
    let suffixMatchCount = 0;
    const maxSuffix = Math.min(orig.length - prefixMatchCount, current.length - prefixMatchCount);
    while (suffixMatchCount < maxSuffix && 
           orig[orig.length - 1 - suffixMatchCount] === current[current.length - 1 - suffixMatchCount]) {
        suffixMatchCount++;
    }

    const unmatchedOrigCount = orig.length - prefixMatchCount - suffixMatchCount;
    const unmatchedCurrentCount = current.length - prefixMatchCount - suffixMatchCount;

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

    state.dirtyLines = markers;
}
function reportCursorAndSelection(element = document.activeElement) {
    if (element && !document.body.contains(element)) {
        element = document.activeElement;
    }
    const editable = element && element.closest ? element.closest('.line-text') : null;
    if (editable && document.body.contains(editable)) {
        state.currentLine = Number(editable.dataset.line || state.currentLine);
        state.currentColumn = runtime.getCaretOffset(editable) + 1;
    }

    post({ type: 'cursorChanged', line: state.currentLine, column: state.currentColumn });
    const selInfo = selectionInfo();
    post({ type: 'selectionResult', text: selInfo.text, startLine: selInfo.startLine, endLine: selInfo.endLine });
}

function selectionInfo() {
    const selection = runtime.normalizeSelection();
    if (selection && runtime.hasCustomSelection()) {
        return selectionTextFromModel(selection);
    }

    return nativeSelectionTextFromModel() ?? { text: window.getSelection()?.toString() || '', startLine: 0, endLine: 0 };
}

function selectedText() {
    const selection = runtime.normalizeSelection();
    if (selection && runtime.hasCustomSelection()) {
        return selectionTextFromModel(selection).text;
    }

    return nativeSelectionTextFromModel()?.text ?? window.getSelection()?.toString() ?? '';
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
    if (active && active.getAttribute('contenteditable') === 'true') return active;
    const current = viewport.querySelector(`.line-text[data-line="${state.currentLine}"]`);
    return current && current.getAttribute('contenteditable') === 'true' ? current : null;
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
    document.body.classList.toggle('custom-selection-active', runtime.hasCustomSelection());
}

function clearCustomSelectionVisuals() {
    viewport.querySelectorAll('.editable-selection-overlay').forEach(el => el.remove());
    viewport.querySelectorAll('.line-row.selected-row, .line-row.selected-empty-row').forEach(row => {
        row.classList.remove('selected-row', 'selected-empty-row');
    });
    viewport.querySelectorAll('.selection-fragment').forEach(fragment => {
        fragment.replaceWith(document.createTextNode(fragment.textContent || ''));
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
    activeEditableElement,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    comparePositions,
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
    queueRender,
    readClipboardText,
    receiveLineBlock,
    recomputeDirtyLines,
    reportCursorAndSelection,
    requestLines,
    requestMissingLines,
    selectedLineRange,
    selectionInfo,
    selectedText,
    setOriginalLines,
    setupModel,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass,
    totalVirtualHeight,
    updateLineFromHost,
    usesMeasuredLineHeights,
    visibleRange,
    writeClipboardText
};
