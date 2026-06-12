import {
    findInput,
    findPanel,
    contextMenu,
    scrollContainer,
    viewport
} from './editor-dom.js';
import {
    applyOptions,
    activeEditableElement,
    configureEditorCoreRuntime,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    isHangulImeKeyEvent,
    isPlainTextKey,
    lineAt,
    lineTop,
    measureRenderedRows,
    post,
    prefetchAround,
    queueRender,
    receiveLineBlock,
    recomputeDirtyLines,
    reportCursorAndSelection,
    requestLines,
    selectedText,
    selectionInfo,
    setOriginalLines,
    setupModel,
    setupVirtualHeight,
    state,
    syncCustomSelectionClass,
    updateLineFromHost
} from './editor-core.js';
const {
    findEditablePreviewBlockContaining,
    initializeMermaid,
    renderMermaidBlocks,
    renderPreviewLineAt
} = await import(`./markdown-preview-renderer.js?v=${encodeURIComponent(window.__TxtAIEditorVersion || Date.now())}`);
import {
    activeColumnSelection,
    drawEditableSelectionOverlays,
    hasCustomSelection,
    isPositionInsideSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    applyMarkdownCommand,
    beginColumnComposition,
    clearPendingImeSelectionCollapse,
    clearPendingRepeatEdit,
    commitLine,
    compositionSelectionRange,
    finishColumnComposition,
    flushPendingEditForSave,
    focusLine,
    getCaretOffset,
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
    changeLineIndent,
    updateSingleLine
} from './editor-commands.js';
import {
    autocompleteState,
    hideAutocomplete,
    insertSelectedCandidate,
    renderAutocomplete,
    scrollAutocompleteActiveIntoView,
    triggerAutocomplete
} from './editor-autocomplete.js';
import { bindContextMenu, hideContextMenu, showContextMenu } from './editor-context-menu.js';
import { createFindReplaceController } from './editor-find-replace.js';
import {
    bindCsvTable,
    selectedCsvText,
    setCsvTableMode,
    syncCsvHeaderScroll,
    updateCsvLocalization
} from './editor-csv-table.js';
import { createEditorRenderer } from './editor-renderer.js';

const {
    beginInlineLivePreviewEdit,
    clearPendingInlineLivePreviewFocus,
    clearPendingInlineLivePreviewFocusForLine,
    focusLineWithRetry,
    keepEditablePreviewBlockFromElement,
    pendingInlineLivePreviewLine,
    render,
    updateHoveredLineFromCoordinates,
    updateHoveredLineFromPointer
} = createEditorRenderer({
    findEditablePreviewBlockContaining,
    getCaretOffset,
    renderMermaidBlocks,
    renderPreviewLineAt,
    setCaret
});

configureEditorCoreRuntime({
    drawEditableSelectionOverlays,
    getCaretOffset,
    hasCustomSelection,
    isLineInColumnComposition,
    normalizeSelection,
    render
});

initializeMermaid(new URLSearchParams(window.location.search).get('theme') || 'Dark');

let suppressNativePasteUntil = 0;

const findReplaceController = createFindReplaceController({ revealLine });
const { openFindPanel } = findReplaceController;
// C# Host Message Handling
function handleCsharpMessage(msg) {
    switch (msg.action) {
        case 'initModel':
            state.initialized = true;
            state.language = msg.language || 'plaintext';
            applyOptions(msg);
            updateCsvLocalization(msg);
            setupModel(msg.lineCount || 1);
            {
                const initialLines = Array.isArray(msg.initialLines) ? msg.initialLines : [];
                setOriginalLines(initialLines);
                if (receiveLineBlock(msg.initialStartLine || 1, initialLines) > 0) {
                    queueRender(true);
                }
            }
            document.getElementById('loading-overlay')?.classList.add('hidden');
            requestAnimationFrame(() => {
                requestAnimationFrame(() => post({ type: 'initialRenderComplete' }));
            });
            break;
        case 'resetOriginalLines':
            {
                const lines = Array.isArray(msg.lines) ? msg.lines : [];
                setOriginalLines(lines);
                recomputeDirtyLines();
                queueRender(true);
            }
            break;
        case 'updateDirtyLines':
            {
                const markers = new Map();
                if (msg.dirtyLines) {
                    for (const [key, value] of Object.entries(msg.dirtyLines)) {
                        markers.set(Number(key), value);
                    }
                }
                state.dirtyLines = markers;
                queueRender(true);
            }
            break;
        case 'setText':
            {
                if (state.isComposing) {
                    break;
                }
                const text = msg.text || '';
                const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
                state.selection = null;
                try {
                    window.getSelection()?.removeAllRanges();
                } catch (e) { }
                syncCustomSelectionClass();
                const targetLine = Math.min(state.currentLine, lines.length);
                const targetCol = Math.min(Math.max(0, state.currentColumn - 1), (lines[targetLine - 1] || '').length);
                setupModel(Math.max(1, lines.length));
                lines.forEach((line, index) => state.cache.set(index + 1, line));
                recomputeDirtyLines();
                queueRender(true);
                if (msg.shouldFocus !== false) {
                    setTimeout(() => focusLine(targetLine, targetCol), 20);
                }
            }
            break;
        case 'updateLine':
            {
                updateLineFromHost(msg.lineNumber || 1, msg.text || '', !!msg.isComposing);
            }
            break;
        case 'receiveLines':
            {
                receiveLineBlock(msg.startLine || 1, msg.lines || []);
                runPendingLineActions();
                if (!state.isComposing) {
                    queueRender(true);
                }
            }
            break;
        case 'lineCountChanged':
            state.lineCount = Math.max(1, Number(msg.lineCount || 1));
            state.cacheVersion++;
            setupVirtualHeight();
            queueRender(true);
            break;
        case 'setLanguage':
            {
                const nextLanguage = msg.language || 'plaintext';
                if (state.language !== nextLanguage) {
                    state.language = nextLanguage;
                    state.lineEndStacks.clear();
                    queueRender(true);
                }
            }
            break;
        case 'updateOptions':
            applyOptions(msg);
            updateCsvLocalization(msg);
            break;
        case 'setCsvTableMode':
            setCsvTableMode(!!msg.enabled, msg);
            break;
        case 'updateSnippets':
            state.snippets = Array.isArray(msg.snippets) ? msg.snippets : [];
            if (autocompleteState.isOpen) {
                const element = autocompleteState.element;
                if (element) triggerAutocomplete(element);
            }
            break;
        case 'triggerFind':
            openFindPanel();
            break;
        case 'getSelection':
            if (state.csvTableEnabled) {
                post({ type: 'selectionResult', text: selectedCsvText(), startLine: 0, endLine: 0 });
            } else {
                const selInfo = selectionInfo();
                post({ type: 'selectionResult', text: selInfo.text, startLine: selInfo.startLine, endLine: selInfo.endLine });
            }
            break;
        case 'flushForSave':
            hideAutocomplete();
            flushPendingEditForSave(msg.requestId || 0);
            break;
        case 'insertText':
            suppressNativePasteUntil = performance.now() + 250;
            insertTextAtCaret(msg.text || '');
            break;
        case 'markdownCommand':
            applyMarkdownCommand(msg.command, msg.color);
            break;
        case 'revealLine':
            revealLine(msg.lineNumber || 1, msg.indexOfMatch || 0, msg.matchLength || 0, msg.query || '');
            break;
        case 'findAllResult':
            state.searchQuery = msg.query || '';
            state.searchMatches = msg.matches || [];
            state.searchIndex = state.searchMatches.length > 0 ? 0 : -1;
            state.activeSearch = null;
            if (state.searchIndex >= 0) {
                const match = state.searchMatches[0];
                state.activeSearch = {
                    lineNumber: match.lineNumber,
                    indexOfMatch: match.indexOfMatch,
                    matchLength: match.matchLength,
                    query: state.searchQuery
                };
                revealLine(match.lineNumber, match.indexOfMatch, match.matchLength, state.searchQuery, true);
            }
            queueRender(true);
            break;
        case 'findResult':
            if (msg.found) {
                revealLine(msg.lineNumber, msg.indexOfMatch || 0, msg.matchLength || 0, msg.query || findInput.value, true);
            }
            break;
        case 'focus':
            focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
            break;
        case 'clipboardReadResult':
            {
                const requestId = Number(msg.requestId || 0);
                const pending = state.clipboardRequests.get(requestId);
                if (pending) {
                    clearTimeout(pending.timer);
                    state.clipboardRequests.delete(requestId);
                    pending.resolve(String(msg.text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n'));
                }
            }
            break;
        case 'scrollSyncChanged':
            state.scrollSyncEnabled = !!msg.enabled;
            break;
        case 'setInlineLivePreview':
            state.inlineLivePreviewEnabled = !!msg.enabled;
            state.livePreviewBaseHref = msg.baseHref || '';
            clearPendingInlineLivePreviewFocus();
            state.inlineLivePreviewSourceLine = null;
            state.inlineLivePreviewEditableBlock = null;
            state.lineHeights.clear();
            document.body.classList.toggle('inline-live-preview-enabled', state.inlineLivePreviewEnabled);
            setupVirtualHeight();
            queueRender(true);
            break;
        case 'syncScroll':
            if (state.scrollSyncEnabled && msg.firstLine) {
                isSyncingScroll = true;
                const targetScrollTop = lineTop(msg.firstLine) + (msg.offset || 0);
                scrollContainer.scrollTop = targetScrollTop;
                requestAnimationFrame(() => {
                    isSyncingScroll = false;
                });
            }
            break;
    }
}

function revealLine(lineNumber, indexOfMatch = 0, matchLength = 0, query = '', preventFocus = false) {
    const safeLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    state.currentLine = safeLine;
    state.currentColumn = Math.max(1, Number(indexOfMatch || 0) + 1);
    state.activeSearch = query
        ? { lineNumber: safeLine, indexOfMatch, matchLength, query }
        : null;
    scrollContainer.scrollTop = Math.max(0, lineTop(safeLine) - Math.floor(scrollContainer.clientHeight / 2));
    requestLines(Math.max(1, safeLine - state.overscan), state.overscan * 2 + 1);
    queueRender(true);
    if (!preventFocus) {
        focusLineWithRetry(safeLine, Math.max(0, indexOfMatch || 0));
    }
}

// Printing Support
function printDocument(fullText) {
    var printContainer = document.getElementById('print-container');
    if (!printContainer) {
        printContainer = document.createElement('div');
        printContainer.id = 'print-container';
        document.body.appendChild(printContainer);
    }
    printContainer.textContent = fullText;

    var editorHost = document.getElementById('editor-host');
    var currentBg = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim() || '#fff';
    var currentFg = getComputedStyle(document.documentElement).getPropertyValue('--fg').trim() || '#000';

    var editorFontSize = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--font-size').trim()) || 13;
    var printFontSize = Math.max(16, editorFontSize * 1.2);

    printContainer.style.cssText = 'display:block; font-family: ' + getComputedStyle(document.documentElement).getPropertyValue('--font-family').trim() + '; font-size: ' + printFontSize + 'px; white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-all; padding: 20px; color: ' + currentFg + '; background: ' + currentBg + '; margin: 0; position: absolute; inset: 0; z-index: 1000; overflow: auto;';
    editorHost.style.display = 'none';

    var baseFontSize = printFontSize;
    var currentZoom = 1.0;
    printContainer.style.fontSize = baseFontSize + 'px';

    printContainer.onwheel = function (e) {
        if (e.ctrlKey) {
            e.preventDefault();
            if (e.deltaY < 0) {
                currentZoom = Math.min(3.0, currentZoom + 0.1);
            } else {
                currentZoom = Math.max(0.5, currentZoom - 0.1);
            }
            printContainer.style.fontSize = (baseFontSize * currentZoom) + 'px';
        }
    };

    window.onafterprint = function () {
        editorHost.style.display = '';
        printContainer.style.cssText = 'display:none;';
        printContainer.onwheel = null;
        window.onafterprint = null;
        queueRender(true);
    };

    setTimeout(function () {
        window.print();
    }, 100);
}

window.printDocument = printDocument;

// ----------------------------------------------------
// Core DOM Event Listeners bindings
// ----------------------------------------------------
viewport.addEventListener('input', event => {
    if (shouldSuppressNativeBeforeInput(event)) {
        return;
    }
    const element = lineElementFromEvent(event);
    if (element) {
        if (!state.isComposing && isPendingImeSelectionCollapseFor(element, event)) {
            return;
        }
        if (!state.columnComposition) {
            state.selection = null;
            syncCustomSelectionClass();
        }
        commitLine(element);
        triggerAutocomplete(element);
    }
});

viewport.addEventListener('focusin', event => {
    if (state.csvTableEnabled) return;
    const element = lineElementFromEvent(event);
    if (element && element.getAttribute('contenteditable') === 'true') {
        state.editingLine = Number(element.dataset.line || state.currentLine || 1);
        clearPendingInlineLivePreviewFocusForLine(state.editingLine);
        queueRender();
    }
});

viewport.addEventListener('focusout', () => {
    if (state.csvTableEnabled) return;
    setTimeout(() => {
        if (state.inlineLivePreviewEnabled && !document.activeElement?.closest?.('.line-text')) {
            state.inlineLivePreviewSourceLine = null;
            state.inlineLivePreviewEditableBlock = null;
            state.editingLine = null;
            queueRender(true);
            return;
        }
        if (!document.activeElement?.closest?.('.line-text')) {
            state.editingLine = null;
            queueRender(true);
        }
    }, 80);
});

viewport.addEventListener('compositionstart', event => {
    let element = lineElementFromEvent(event) || activeEditableElement();
    const pendingCompositionSelection = compositionSelectionRange();
    let collapsedSelectionForComposition = false;

    if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
        element = replaceSelectionForCompositionStart(element) || element;
        collapsedSelectionForComposition = true;
    }

    if (isPendingImeSelectionCollapseFor(element)) {
        clearPendingImeSelectionCollapse();
    }

    state.isComposing = true;
    state.compositionLine = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;

    if (element && element.getAttribute('contenteditable') === 'true') {
        state.editingLine = state.compositionLine;

        if (collapsedSelectionForComposition) {
            state.columnComposition = null;
            return;
        }

        beginColumnComposition(element);
    } else {
        state.columnComposition = null;
    }
});

viewport.addEventListener('compositionupdate', event => {
    state.isComposing = true;
});

viewport.addEventListener('compositionend', event => {
    const element = lineElementFromEvent(event) || activeEditableElement();
    const lineNumber = element ? Number(element.dataset.line || state.compositionLine || state.currentLine) : state.compositionLine;

    state.isComposing = false;
    clearPendingImeSelectionCollapse();
    state.compositionLine = null;

    if (finishColumnComposition(element, lineNumber)) {
        return;
    }

    if (element && element.getAttribute('contenteditable') === 'true') {
        state.selection = null;
        syncCustomSelectionClass();
        state.editingLine = lineNumber;
        setTimeout(() => {
            const current = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
            if (current && current.getAttribute('contenteditable') === 'true') {
                commitLine(current);
                triggerAutocomplete(current);
            }
        }, 0);
    }
});

function getUrlOrPathAtColumn(text, column) {
    const value = String(text ?? '');
    if (!value || column < 0) return null;

    const separators = /[\s\"\'\(\)\[\]\{\}\<\>\`]/;
    
    let col = Math.min(column, value.length - 1);
    if (col >= 0 && separators.test(value[col])) {
        if (col > 0 && !separators.test(value[col - 1])) {
            col--;
        } else {
            return null;
        }
    }

    let start = col;
    let end = col;

    while (start > 0 && !separators.test(value[start - 1])) {
        start--;
    }
    while (end < value.length && !separators.test(value[end])) {
        end++;
    }

    if (start >= end) return null;
    return value.slice(start, end).trim();
}

let lastPointerDownTime = 0;
let lastPointerDownPosition = null;

scrollContainer.addEventListener('pointerdown', event => {
    if (state.csvTableEnabled) return;
    if (event.button !== 0 || findPanel.contains(event.target)) return;

    if (event.ctrlKey) {
        const position = positionFromPointer(event);
        if (position) {
            const text = state.cache.get(position.line) ?? lineTextFromElement(position.element);
            const token = getUrlOrPathAtColumn(text, position.column);
            if (token) {
                const isUrl = /^https?:\/\/[^\s\)\(\]\[\}\{\>\<\"\']+/i.test(token);
                const isPath = /^[a-zA-Z]:[\/\\]/.test(token) || 
                               /^[\/\\]/.test(token) || 
                               /^\.\.?[\/\\]/.test(token) ||
                               ((token.includes('/') || token.includes('\\')) && !isUrl);

                if (isUrl || isPath) {
                    event.preventDefault();
                    event.stopPropagation();
                    post({
                        type: 'ctrlClick',
                        text: token,
                        isUrl: isUrl,
                        isPath: isPath
                    });
                    return;
                }
            }
        }
    }

    const lineNumEl = event.target.closest('.line-number');
    if (lineNumEl) {
        const row = lineNumEl.closest('.line-row');
        if (row) {
            const line = Number(row.dataset.line || 1);
            const text = state.cache.get(line) || '';
            const lineLength = text.length;

            event.preventDefault();
            scrollContainer.setPointerCapture?.(event.pointerId);

            state.selectionAnchor = { line: line, column: 0 };
            state.selection = { start: { line: line, column: 0 }, end: { line: line, column: lineLength || 1 } };
            syncCustomSelectionClass();
            state.isSelecting = true;
            state.isLineSelecting = true;
            document.body.classList.add('selecting');
            state.currentLine = line;
            state.currentColumn = lineLength + 1;

            queueRender(true);
            setTimeout(() => {
                const element = viewport.querySelector(`.line-text[data-line="${line}"]`);
                if (element && element.getAttribute('contenteditable') === 'true') {
                    setCaret(element, lineLength);
                }
            }, 0);
            reportCursorAndSelection(row.querySelector('.line-text'));
            return;
        }
    }

    const position = positionFromPointer(event);
    if (!position) return;

    const now = Date.now();
    const isDoubleClick = (event.detail >= 2) ||
        ((now - lastPointerDownTime < 350) &&
            lastPointerDownPosition &&
            (lastPointerDownPosition.line === position.line) &&
            (Math.abs(lastPointerDownPosition.column - position.column) < 5));

    lastPointerDownTime = now;
    lastPointerDownPosition = position;

    if (isDoubleClick && !event.shiftKey) {
        if (selectWordAtPointer(event)) {
            event.preventDefault();
            return;
        }
    }

    const isEditable = position.element.getAttribute('contenteditable') === 'true';
    const isInlinePreviewRow = state.inlineLivePreviewEnabled && !isEditable;
    event.preventDefault();
    scrollContainer.setPointerCapture?.(event.pointerId);

    const hadSelection = hasCustomSelection();
    const positionText = state.cache.get(position.line) ?? lineTextFromElement(position.element);
    state.dragStartPosition = {
        line: position.line,
        column: position.column,
        isEmptyLine: positionText.length === 0,
        clientX: event.clientX,
        clientY: event.clientY
    };
    const isColumnSelect = event.altKey;
    const clickInsideSelection = hadSelection && !event.shiftKey && !isColumnSelect && isPositionInsideSelection(position);

    if (clickInsideSelection) {
        const sel = normalizeSelection();
        state.dragSelectionData = {
            start: { line: sel.start.line, column: sel.start.column },
            end: { line: sel.end.line, column: sel.end.column },
            isColumn: !!sel.isColumn,
            text: ''
        };
        state.isDragPotential = true;
        state.isDragMoving = false;
        state.dragDropPosition = null;
    } else {
        state.isDragPotential = false;
        state.isDragMoving = false;
        state.dragSelectionData = null;
        state.dragDropPosition = null;
        const anchor = event.shiftKey && state.selectionAnchor
            ? state.selectionAnchor
            : { line: position.line, column: position.column };
        state.selectionAnchor = anchor;
        state.selection = event.shiftKey
            ? { start: anchor, end: { line: position.line, column: position.column }, isColumn: isColumnSelect }
            : null;
    }
    syncCustomSelectionClass();
    state.isSelecting = true;
    state.isLineSelecting = false;
    document.body.classList.add('selecting');
    if (!clickInsideSelection) {
        state.currentLine = position.line;
        state.currentColumn = position.column + 1;
        if (isEditable) {
            keepEditablePreviewBlockFromElement(position.element);
            setCaret(position.element, position.column);
        } else if (isInlinePreviewRow) {
            beginInlineLivePreviewEdit(position.line, position.column);
        }
    }
    if (event.shiftKey || (hadSelection && !clickInsideSelection)) {
        queueRender(true);
        setTimeout(() => focusLine(state.currentLine, Math.max(0, state.currentColumn - 1)), 0);
    }
    reportCursorAndSelection(position.element);
});

function computeDropPositionAfterCut(dropPos, selStart, selEnd) {
    if (dropPos.line < selStart.line ||
        (dropPos.line === selStart.line && dropPos.column <= selStart.column)) {
        return { line: dropPos.line, column: dropPos.column };
    }
    if (dropPos.line > selEnd.line ||
        (dropPos.line === selEnd.line && dropPos.column >= selEnd.column)) {
        const lineDiff = selEnd.line - selStart.line;
        if (dropPos.line === selEnd.line) {
            const colDiff = selEnd.column - selStart.column;
            return { line: selStart.line, column: selStart.column + (dropPos.column - selEnd.column) };
        }
        return { line: dropPos.line - lineDiff, column: dropPos.column };
    }
    return null;
}

function endSelection(event) {
    stopSelectionAutoScroll();

    if (state.isDragMoving && state.dragSelectionData) {
        const dragData = state.dragSelectionData;
        const dropPos = state.dragDropPosition;
        const isCopy = state.isDragCopy;
        state.isSelecting = false;
        state.isLineSelecting = false;
        document.body.classList.remove('selecting');
        if (event && event.pointerId !== undefined) {
            try {
                scrollContainer.releasePointerCapture?.(event.pointerId);
            } catch (e) { }
        }
        state.dragStartPosition = null;

        if (dropPos && hasCustomSelection() && dragData.text) {
            const sel = normalizeSelection();
            const adj = computeDropPositionAfterCut(dropPos, sel.start, sel.end);
            if (adj) {
                if (isCopy) {
                    const insertSel = { start: adj, end: adj, isColumn: false };
                    replaceSelectionWith(insertSel, dragData.text);
                } else {
                    replaceSelectionWith(sel, '');
                    const insertSel = { start: adj, end: adj, isColumn: false };
                    replaceSelectionWith(insertSel, dragData.text);
                }
            }
        }

        cleanupDragState();
        queueRender(true);
        reportCursorAndSelection(document.activeElement);
        return;
    }

    if (state.isDragPotential) {
        state.isDragPotential = false;
        state.dragSelectionData = null;
        state.dragDropPosition = null;
        const clickPos = event ? positionFromPointer(event) : null;
        if (clickPos) {
            state.selection = null;
            state.selectionAnchor = { line: clickPos.line, column: clickPos.column };
            state.currentLine = clickPos.line;
            state.currentColumn = clickPos.column + 1;
            keepEditablePreviewBlockFromElement(clickPos.element);
            syncCustomSelectionClass();
            queueRender(true);
            reportCursorAndSelection(clickPos.element || document.activeElement);
        } else if (hasCustomSelection()) {
            state.selection = null;
            syncCustomSelectionClass();
            queueRender(true);
        }
        state.isSelecting = true;
    }

    if (!state.isSelecting) return;
    const savedScrollTop = scrollContainer.scrollTop;
    state.isSelecting = false;
    state.isLineSelecting = false;
    document.body.classList.remove('selecting');
    syncCustomSelectionClass();
    if (event && event.pointerId !== undefined) {
        try {
            scrollContainer.releasePointerCapture?.(event.pointerId);
        } catch (e) { }
    }
    state.dragStartPosition = null;
    const hadSelection = hasCustomSelection();
    const selection = normalizeSelection();
    if (selection && !hasCustomSelection()) {
        state.selection = null;
        syncCustomSelectionClass();
    } else if (selection) {
        const releaseLine = state.currentLine;
        const releaseColumn = Math.max(0, state.currentColumn - 1);
        setTimeout(() => {
            focusLine(releaseLine, releaseColumn);
            scrollContainer.scrollTop = savedScrollTop;
        }, 0);
    }
    if (hadSelection || hasCustomSelection()) {
        queueRender(true);
    }
    reportCursorAndSelection(document.activeElement);
}

function updateDragDropIndicator(position, event) {
    const container = scrollContainer;
    container.querySelectorAll('.drag-drop-indicator').forEach(el => el.remove());

    if (!position) return;

    const row = viewport.querySelector(`.line-row[data-line="${position.line}"]`);
    if (!row) return;

    const element = row.querySelector('.line-text');
    if (!element) return;

    const containerRect = container.getBoundingClientRect();
    const rowRect = row.getBoundingClientRect();

    let x;
    const mx = event?.clientX;
    const my = event?.clientY;
    if (mx !== undefined && my !== undefined && document.caretRangeFromPoint) {
        const range = document.caretRangeFromPoint(mx, my);
        if (range && element.contains(range.startContainer)) {
            const r = range.getBoundingClientRect();
            x = r?.left;
        }
    }

    if (!x || isNaN(x)) {
        const elRect = element.getBoundingClientRect();
        const text = lineTextFromElement(element);
        const col = Math.max(0, Math.min(position.column, text.length));
        x = col === 0 ? elRect.left : elRect.right;
    }

    const indicator = document.createElement('div');
    indicator.className = 'drag-drop-indicator';
    indicator.style.left = (x - containerRect.left + container.scrollLeft) + 'px';
    indicator.style.top = (rowRect.top - containerRect.top + container.scrollTop) + 'px';
    indicator.style.height = rowRect.height + 'px';
    container.appendChild(indicator);

    if (state.isDragCopy) {
        const plus = document.createElement('span');
        plus.className = 'drag-drop-plus';
        plus.textContent = '+';
        indicator.appendChild(plus);
    }
}

function cleanupDragState() {
    scrollContainer.querySelectorAll('.drag-drop-indicator').forEach(el => el.remove());
    state.isDragPotential = false;
    state.isDragMoving = false;
    state.isDragCopy = false;
    state.dragSelectionData = null;
    state.dragDropPosition = null;
    document.body.classList.remove('dragging-selection');
}

let selectionAutoScrollFrame = 0;
let selectionAutoScrollPointer = null;

function updateSelectionFromPointer(event) {
    const position = positionFromPointer(event);
    if (!position) return false;

    let newSelection;
    if (state.isLineSelecting) {
        const startLine = state.selectionAnchor.line;
        const endLine = position.line;
        if (startLine <= endLine) {
            const endText = state.cache.get(endLine) || '';
            newSelection = {
                start: { line: startLine, column: 0 },
                end: { line: endLine, column: endText.length }
            };
        } else {
            const startText = state.cache.get(startLine) || '';
            newSelection = {
                start: { line: startLine, column: startText.length },
                end: { line: endLine, column: 0 }
            };
        }
    } else {
        newSelection = {
            start: state.selectionAnchor || { line: position.line, column: position.column },
            end: { line: position.line, column: position.column },
            isColumn: !!event.altKey
        };
    }

    const selectionChanged = !state.selection ||
        state.selection.start.line !== newSelection.start.line ||
        state.selection.start.column !== newSelection.start.column ||
        state.selection.end.line !== newSelection.end.line ||
        state.selection.end.column !== newSelection.end.column;

    const isEmpty = newSelection.start.line === newSelection.end.line &&
        newSelection.start.column === newSelection.end.column;

    if (isEmpty && !state.selection) {
        const emptyLineDragDistance = state.dragStartPosition?.isEmptyLine &&
            state.dragStartPosition.line === position.line
            ? Math.hypot(event.clientX - state.dragStartPosition.clientX, event.clientY - state.dragStartPosition.clientY)
            : 0;
        if (emptyLineDragDistance > 4) {
            newSelection = {
                start: { line: position.line, column: 0 },
                end: { line: position.line, column: 1 }
            };
        } else {
            return false;
        }
    }

    const finalSelectionChanged = selectionChanged ||
        newSelection.start.column !== newSelection.end.column;

    if (finalSelectionChanged) {
        const finalSelectionIsEmpty = newSelection.start.line === newSelection.end.line &&
            newSelection.start.column === newSelection.end.column;
        state.selection = finalSelectionIsEmpty ? null : newSelection;
        syncCustomSelectionClass();
        state.currentLine = position.line;
        state.currentColumn = position.column + 1;
        queueRender(true);
        reportCursorAndSelection(position.element);
    }

    return true;
}

function updateSelectionAutoScrollPointer(event) {
    if (state.wordWrap) {
        stopSelectionAutoScroll();
        return;
    }

    selectionAutoScrollPointer = {
        clientX: event.clientX,
        clientY: event.clientY,
        altKey: event.altKey,
        target: event.target
    };

    if (!selectionAutoScrollFrame) {
        selectionAutoScrollFrame = requestAnimationFrame(runSelectionAutoScroll);
    }
}

function runSelectionAutoScroll() {
    selectionAutoScrollFrame = 0;

    if (!state.isSelecting || state.isDragMoving || state.isDragPotential || !selectionAutoScrollPointer) {
        stopSelectionAutoScroll();
        return;
    }

    const rect = scrollContainer.getBoundingClientRect();
    const edge = 44;
    const maxStep = 36;
    const x = selectionAutoScrollPointer.clientX;
    let dx = 0;

    if (x > rect.right - edge) {
        dx = Math.ceil(Math.min(1, (x - (rect.right - edge)) / edge) * maxStep);
    } else if (x < rect.left + edge) {
        dx = -Math.ceil(Math.min(1, ((rect.left + edge) - x) / edge) * maxStep);
    }

    if (dx !== 0) {
        const before = scrollContainer.scrollLeft;
        scrollContainer.scrollLeft = Math.max(0, before + dx);
        if (scrollContainer.scrollLeft !== before) {
            updateSelectionFromPointer(selectionAutoScrollPointer);
        }
    }

    selectionAutoScrollFrame = requestAnimationFrame(runSelectionAutoScroll);
}

function stopSelectionAutoScroll() {
    selectionAutoScrollPointer = null;
    if (selectionAutoScrollFrame) {
        cancelAnimationFrame(selectionAutoScrollFrame);
        selectionAutoScrollFrame = 0;
    }
}

scrollContainer.addEventListener('pointermove', event => {
    if (state.csvTableEnabled) return;
    updateHoveredLineFromPointer(event);

    if (state.isDragPotential || state.isDragMoving) {
        if ((event.buttons & 1) === 0) {
            endSelection(event);
            return;
        }
        event.preventDefault();
        const dx = event.clientX - (state.dragStartPosition?.clientX ?? event.clientX);
        const dy = event.clientY - (state.dragStartPosition?.clientY ?? event.clientY);
        const distance = Math.hypot(dx, dy);

        if (state.isDragPotential && distance > 4) {
            state.isDragMoving = true;
            state.isDragPotential = false;
            state.isDragCopy = event.ctrlKey || event.metaKey;
            document.body.classList.remove('selecting');
            document.body.classList.add('dragging-selection');
            state.dragSelectionData.text = selectedText();
        }

        if (state.isDragMoving) {
            state.isDragCopy = event.ctrlKey || event.metaKey;
            const pos = positionFromPointer(event);
            if (pos) {
                state.dragDropPosition = { line: pos.line, column: pos.column };
                updateDragDropIndicator(pos, event);
            }
            return;
        }
        return;
    }

    if (!state.isSelecting) return;
    if ((event.buttons & 1) === 0) {
        endSelection(event);
        return;
    }

    event.preventDefault();
    updateSelectionAutoScrollPointer(event);
    updateSelectionFromPointer(event);
});

scrollContainer.addEventListener('pointerleave', event => {
    updateHoveredLineFromCoordinates(event.clientX, event.clientY);
});

window.addEventListener('pointerup', event => {
    endSelection(event);
});

window.addEventListener('pointercancel', event => {
    if (!state.isSelecting) return;
    stopSelectionAutoScroll();
    state.isSelecting = false;
    state.isLineSelecting = false;
    document.body.classList.remove('selecting');
    syncCustomSelectionClass();
    queueRender(true);
    reportCursorAndSelection(document.activeElement);
});

function focusOrSelectHome(extendSelection) {
    const targetLine = 1;
    const targetColumn = 0;

    const element = activeEditableElement();
    const lineNumber = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;
    const caret = element ? getCaretOffset(element) : (state.currentColumn - 1);

    if (extendSelection) {
        const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
        state.selectionAnchor = anchor;
        state.selection = (anchor.line === targetLine && anchor.column === targetColumn)
            ? null
            : { start: anchor, end: { line: targetLine, column: targetColumn } };
        state.currentLine = targetLine;
        state.currentColumn = targetColumn + 1;
        syncCustomSelectionClass();
        queueRender(true);
        setTimeout(() => focusLine(targetLine, targetColumn), 0);
        reportCursorAndSelection();
    } else {
        state.selection = null;
        state.selectionAnchor = { line: targetLine, column: targetColumn };
        state.currentLine = targetLine;
        state.currentColumn = targetColumn + 1;
        syncCustomSelectionClass();
        focusLine(targetLine, targetColumn);
        reportCursorAndSelection();
    }
}

function focusOrSelectEnd(extendSelection) {
    const targetLine = state.lineCount;

    function proceed() {
        const targetText = state.cache.get(targetLine) || '';
        const targetColumn = targetText.length;

        const element = activeEditableElement();
        const lineNumber = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;
        const caret = element ? getCaretOffset(element) : (state.currentColumn - 1);

        if (extendSelection) {
            const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
            state.selectionAnchor = anchor;
            state.selection = (anchor.line === targetLine && anchor.column === targetColumn)
                ? null
                : { start: anchor, end: { line: targetLine, column: targetColumn } };
            state.currentLine = targetLine;
            state.currentColumn = targetColumn + 1;
            syncCustomSelectionClass();
            queueRender(true);
            setTimeout(() => focusLine(targetLine, targetColumn), 0);
            reportCursorAndSelection();
        } else {
            state.selection = null;
            state.selectionAnchor = { line: targetLine, column: targetColumn };
            state.currentLine = targetLine;
            state.currentColumn = targetColumn + 1;
            syncCustomSelectionClass();
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection();
        }
    }

    if (state.cache.has(targetLine)) {
        proceed();
    } else {
        const wrappedTargetTop = lineTop(targetLine);
        scrollContainer.scrollTop = Math.max(0, wrappedTargetTop - Math.floor(scrollContainer.clientHeight / 2));
        requestLines(targetLine, 1);

        let attempts = 0;
        const interval = setInterval(() => {
            attempts++;
            if (state.cache.has(targetLine) || attempts > 50) {
                clearInterval(interval);
                proceed();
            }
        }, 20);
    }
}

document.addEventListener('keydown', event => {
    const earlyCtrl = event.ctrlKey || event.metaKey;
    const earlyKey = event.key ? event.key.toLowerCase() : '';
    if (earlyCtrl && earlyKey === 's') {
        event.preventDefault();
        hideAutocomplete();
        post({ type: 'shortcut', name: 'save' });
        return;
    }

    if (event.key === 'Escape' && (state.isDragPotential || state.isDragMoving)) {
        event.preventDefault();
        state.isSelecting = false;
        cleanupDragState();
        queueRender(true);
        return;
    }

    if (autocompleteState.isOpen) {
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' ||
            event.key === 'Backspace' || event.key === 'Delete') {
            hideAutocomplete();
        }
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            autocompleteState.activeIndex = (autocompleteState.activeIndex + 1) % autocompleteState.candidates.length;
            renderAutocomplete();
            scrollAutocompleteActiveIntoView();
            return;
        }
        if (event.key === 'ArrowUp') {
            event.preventDefault();
            autocompleteState.activeIndex = (autocompleteState.activeIndex - 1 + autocompleteState.candidates.length) % autocompleteState.candidates.length;
            renderAutocomplete();
            scrollAutocompleteActiveIntoView();
            return;
        }
        if (event.key === 'Enter') {
            if (state.autocompleteOnEnter) {
                event.preventDefault();
                insertSelectedCandidate();
                return;
            } else {
                hideAutocomplete(300);
            }
        }
        if (event.key === 'Tab' && state.autocompleteOnTab) {
            event.preventDefault();
            insertSelectedCandidate();
            return;
        }
        // event.code는 IME 상태와 무관하게 물리 키를 반환하므로, 한글 조합 중에도 ESC 감지 가능
        // suppressMs=300: compositionend 후 triggerAutocomplete 재호출로 팝업이 다시 열리는 것 방지
        if (event.key === 'Escape' || event.code === 'Escape') {
            event.preventDefault();
            hideAutocomplete(300);
            return;
        }
    }

    if (isHangulImeKeyEvent(event)) {
        const active = document.activeElement;
        const isFindOrInput = active && (
            active.closest?.('#find-panel') ||
            active.tagName === 'INPUT' ||
            active.tagName === 'TEXTAREA'
        );
        if (!isFindOrInput && !state.isComposing && !event.ctrlKey && !event.metaKey && !event.altKey) {
            const imeElement = lineElementFromEvent(event) || activeEditableElement();
            const pendingSelection = compositionSelectionRange();
            if (imeElement && pendingSelection && !pendingSelection.isColumn) {
                const replacedElement = replaceSelectionForCompositionStart(imeElement, true) || imeElement;
                state.compositionLine = Number(replacedElement.dataset.line || state.currentLine || 1);
                state.editingLine = state.compositionLine;
            }
        }
        return;
    }

    if (event.key === 'F4') {
        event.preventDefault();
        post({ type: 'shortcut', name: 'f4' });
        return;
    }
    if (event.key === 'F9') {
        event.preventDefault();
        post({ type: 'shortcut', name: 'f9' });
        return;
    }
    if (event.key === 'F10') {
        event.preventDefault();
        post({ type: 'shortcut', name: 'f10' });
        return;
    }
    if (event.key === 'F11') {
        event.preventDefault();
        post({ type: 'shortcut', name: 'f11' });
        return;
    }
    if (event.key === 'F12') {
        event.preventDefault();
        post({ type: 'shortcut', name: 'f12' });
        return;
    }

    const ctrl = event.ctrlKey || event.metaKey;
    const key = event.key ? event.key.toLowerCase() : '';

    if (ctrl) {
        if (key === '1') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'toggleLeftPanel' });
            return;
        }
        if (key === '2') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'toggleRightPanel' });
            return;
        }
        if (key === '3') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'expandRightPanel' });
            return;
        }
        if (key === 'n') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'newTab' });
            return;
        }
        if (key === 's') {
            event.preventDefault();
            hideAutocomplete();
            post({ type: 'shortcut', name: 'save' });
            return;
        }
        if (key === 'o') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'open' });
            return;
        }
        if (key === 'w') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'closeTab' });
            return;
        }
        if (key === 'p') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'print' });
            return;
        }
        if (key === 'f') {
            event.preventDefault();
            if (event.shiftKey) post({ type: 'shortcut', name: 'searchAll' });
            else openFindPanel();
            return;
        }
        if (event.code === 'Backquote' || event.key === '`' || event.key === '~' || event.key === 'Dead') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'terminal' });
            return;
        }
        if (key === 'a') {
            event.preventDefault();
            selectAll();
            return;
        }
        if (key === 'z') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'undo' });
            return;
        }
        if (key === 'y') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'redo' });
            return;
        }
    }

    if (document.activeElement && (document.activeElement.closest('#find-panel') || document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'TEXTAREA')) {
        return;
    }

    const element = activeEditableElement();
    if (state.csvTableEnabled || !element || element.getAttribute('contenteditable') !== 'true') return;

    const columnSelection = activeColumnSelection();
    if (columnSelection && isPlainTextKey(event)) {
        event.preventDefault();
        replaceSelectionWith(columnSelection, event.key);
        return;
    }

    if ((event.key === 'Home' || event.key === 'End') && event.ctrlKey) {
        event.preventDefault();
        if (event.key === 'Home') {
            focusOrSelectHome(event.shiftKey);
        } else {
            focusOrSelectEnd(event.shiftKey);
        }
        return;
    }

    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        event.preventDefault();
        moveCaretHorizontal(element, event.key === 'ArrowLeft' ? -1 : 1, event.shiftKey);
        return;
    }

    if (event.key === 'ArrowUp') {
        event.preventDefault();
        moveCaretVertical(element, -1, event.shiftKey);
        return;
    }

    if (event.key === 'ArrowDown') {
        event.preventDefault();
        moveCaretVertical(element, 1, event.shiftKey);
        return;
    }

    if ((event.key === ' ' || event.code === 'Space') && !event.ctrlKey && !event.metaKey && !event.altKey) {
        event.preventDefault();
        markNativeBeforeInputHandled(['insertSpace'], 80);
        insertPlainTextByModel(element, ' ');
        return;
    }

    if (event.key === 'Tab') {
        event.preventDefault();
        if (event.shiftKey || hasCustomSelection()) {
            changeLineIndent(event.shiftKey ? -1 : 1);
            return;
        }
        insertPlainTextByModel(element, ' '.repeat(state.tabSize));
        return;
    }

    if (isPlainTextKey(event)) {
        event.preventDefault();
        markNativeBeforeInputHandled(['insertText'], 80);
        insertPlainTextByModel(element, event.key);
        triggerAutocomplete(activeEditableElement() || element);
        return;
    }

    if (isModelRepeatKey(event)) {
        event.preventDefault();
        const keyName = normalizedModelRepeatKey(event);
        state.lastDeleteKeyDown = {
            key: keyName,
            line: Number(element.dataset.line || state.currentLine || 1),
            column: getCaretOffset(element),
            time: performance.now()
        };
        markNativeBeforeInputHandled(keyName === 'Backspace'
            ? ['deleteContentBackward']
            : ['deleteContentForward']);
        scheduleModelRepeatEdit(keyName, event.repeat);
        return;
    }

    if (event.key === 'Enter') {
        event.preventDefault();
        splitCurrentLine(element);
        return;
    }
});

viewport.addEventListener('beforeinput', event => {
    if (state.csvTableEnabled) return;
    let element = lineElementFromEvent(event);

    if (event.isComposing || state.isComposing ||
        event.inputType === 'insertCompositionText' ||
        event.inputType === 'deleteCompositionText') {
        const pendingCompositionSelection = compositionSelectionRange(!state.isComposing);
        if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
            const replacedElement = replaceSelectionForCompositionStart(element || activeEditableElement());
            if (replacedElement) {
                element = replacedElement;
                state.compositionLine = Number(replacedElement.dataset.line || state.currentLine || 1);
                state.editingLine = state.compositionLine;
            }
        }
        return;
    }

    if (isPendingImeSelectionCollapseFor(element, event)) {
        return;
    }

    if (shouldSuppressNativeBeforeInput(event)) {
        event.preventDefault();
        return;
    }

    const columnSelection = activeColumnSelection();
    if (columnSelection && event.inputType?.startsWith('insert') &&
        event.inputType !== 'insertCompositionText' &&
        event.inputType !== 'insertFromPaste' &&
        event.inputType !== 'insertFromDrop') {
        event.preventDefault();
        replaceSelectionWith(columnSelection, event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph' ? '\n' : (event.data || ''));
        return;
    }

    if (isSpaceInputEvent(event)) {
        event.preventDefault();
        const target = element || activeEditableElement();
        if (!target || target.getAttribute('contenteditable') !== 'true') return;

        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, ' ');
            return;
        }

        const text = lineTextFromElement(target);
        const range = inputRangeInElement(event, target);
        const start = range ? range.start : getCaretOffset(target);
        const end = range ? range.end : start;
        makeEditablePlainText(target, start);
        updateSingleLine(target, text.slice(0, start) + ' ' + text.slice(end), start + 1);
        return;
    }

    if (event.inputType === 'deleteContentBackward' || event.inputType === 'deleteContentForward') {
        event.preventDefault();
        const target = element || activeEditableElement();
        if (!target || target.getAttribute('contenteditable') !== 'true') return;

        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, '');
            return;
        }

        const text = lineTextFromElement(target);
        const range = inputRangeInElement(event, target);
        if (range && range.start !== range.end) {
            makeEditablePlainText(target, range.start);
            updateSingleLine(target, text.slice(0, range.start) + text.slice(range.end), range.start);
            return;
        }

        const caret = range ? range.start : getCaretOffset(target);
        makeEditablePlainText(target, caret);
        if (event.inputType === 'deleteContentBackward') {
            if (caret > 0) {
                const tabSize = state.tabSize || 4;
                const prefix = text.slice(0, caret);
                const onlySpacesBefore = prefix.length > 0 && /^ *$/.test(prefix);
                if (onlySpacesBefore && prefix.length % tabSize === 0) {
                    const deleteStart = caret - Math.min(tabSize, caret);
                    updateSingleLine(target, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
                } else {
                    const deleteStart = graphemeDeleteStart(text, caret);
                    updateSingleLine(target, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
                }
            } else {
                mergeLineBackward(target);
            }
        } else {
            if (caret < text.length) {
                const delEnd = graphemeDeleteEnd(text, caret);
                updateSingleLine(target, text.slice(0, caret) + text.slice(delEnd), caret);
            } else {
                mergeLineForward(target);
            }
        }
        return;
    }

    if (!hasCustomSelection()) {
        if (element && element.getAttribute('contenteditable') === 'true' && event.inputType?.startsWith('insert')) {
            makeEditablePlainText(element);
        }
        return;
    }

    const sel = normalizeSelection();
    if (!sel) return;

    if (event.inputType === 'insertText') {
        event.preventDefault();
        replaceSelectionWith(sel, event.data || '');
    } else if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') {
        event.preventDefault();
        replaceSelectionWith(sel, '\n');
    } else if (event.inputType === 'insertFromPaste' || event.inputType === 'insertFromDrop') {
        event.preventDefault();
    } else if (event.inputType && event.inputType.startsWith('insert')) {
        event.preventDefault();
        replaceSelectionWith(sel, event.data || '');
    }
});

viewport.addEventListener('keyup', event => {
    if (isModelRepeatKey(event)) {
        clearPendingRepeatEdit();
    }

    const element = lineElementFromEvent(event);
    reportCursorAndSelection(element || document.activeElement);

    if (event.key === 'Shift' && hasCustomSelection() && !state.isComposing) {
        const sel = normalizeSelection();
        if (sel && !sel.isColumn) {
            const startTextElement = viewport.querySelector(`.line-row[data-line="${sel.start.line}"] .line-text`);
            if (startTextElement && document.activeElement !== startTextElement) {
                focusLine(sel.start.line, sel.start.column);
            }
        }
    }

    if ((state.autocompleteOnEnter || state.autocompleteOnTab) && element && element.getAttribute('contenteditable') === 'true') {
        const ignoredKeys = [
            'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
            'Enter', 'Escape', 'Tab', 'Shift', 'Control', 'Alt', 'Meta',
            'CapsLock', 'Home', 'End', 'PageUp', 'PageDown', 'Backspace', 'Delete',
            'Process'
        ];
        if (!ignoredKeys.includes(event.key) && event.keyCode !== 229 && !event.ctrlKey && !event.metaKey) {
            triggerAutocomplete(element);
        }
    }
});

viewport.addEventListener('click', event => {
    const element = lineElementFromEvent(event);
    if (state.inlineLivePreviewEnabled && element && element.getAttribute('contenteditable') !== 'true') {
        const lineNumber = Number(element.dataset.line || state.currentLine || 1);
        event.preventDefault();
        event.stopPropagation();
        if (pendingInlineLivePreviewLine() !== lineNumber) {
            beginInlineLivePreviewEdit(lineNumber, 0);
        }
        return;
    }
    if (element && element.getAttribute('contenteditable') === 'true') {
        state.editingLine = Number(element.dataset.line || state.currentLine || 1);
    }
    reportCursorAndSelection(element || document.activeElement);
});

viewport.addEventListener('dblclick', event => {
    if (findPanel.contains(event.target)) return;
    if (event.target.closest?.('.line-number')) return;
    if (selectWordAtPointer(event)) {
        event.preventDefault();
        event.stopPropagation();
    }
});

viewport.addEventListener('contextmenu', event => {
    if (findPanel.contains(event.target)) return;
    event.preventDefault();

    const position = positionFromPointer(event);
    const keepSelection = hasCustomSelection() && isPositionInsideSelection(position);
    if (position && !keepSelection) {
        const hadSelection = hasCustomSelection();
        state.selection = null;
        syncCustomSelectionClass();
        state.selectionAnchor = { line: position.line, column: position.column };
        state.currentLine = position.line;
        state.currentColumn = position.column + 1;
        if (position.element.getAttribute('contenteditable') === 'true') {
            setCaret(position.element, position.column);
        }
        if (hadSelection) queueRender(true);
    }

    showContextMenu(event.clientX, event.clientY);
});

document.addEventListener('copy', event => {
    if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
        return;
    }
    const text = selectedText();
    if (text) {
        event.clipboardData?.setData('text/plain', text);
        event.preventDefault();
    }
});

document.addEventListener('cut', event => {
    if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
        return;
    }
    const text = selectedText();
    if (!text) return;

    event.clipboardData?.setData('text/plain', text);
    event.preventDefault();
    if (state.readOnly) return;

    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) replaceSelectionWith(sel, '');
        return;
    }

    const element = activeEditableElement();
    const selection = window.getSelection();
    if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
        document.execCommand('delete');
        commitLine(element);
    }
});

document.addEventListener('paste', event => {
    if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
        return;
    }
    if (performance.now() < suppressNativePasteUntil) {
        event.preventDefault();
        return;
    }
    event.preventDefault();
    const clipboardText = (event.clipboardData?.getData('text/plain') || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const element = document.activeElement?.closest?.('.line-text');
    if (!element || element.getAttribute('contenteditable') !== 'true') return;

    if (hasCustomSelection()) {
        const sel = normalizeSelection();
        if (sel) {
            replaceSelectionWith(sel, clipboardText);
            return;
        }
    }

    insertTextAtCaret(clipboardText);
});

bindContextMenu();
bindCsvTable();

document.addEventListener('pointerdown', event => {
    if (!contextMenu.hidden && !contextMenu.contains(event.target)) {
        hideContextMenu();
    }
    const popup = document.getElementById('autocomplete-popup');
    if (autocompleteState.isOpen && popup && !popup.contains(event.target)) {
        hideAutocomplete();
    }
});

const autocompletePopup = document.getElementById('autocomplete-popup');
if (autocompletePopup) {
    autocompletePopup.addEventListener('pointerdown', event => {
        event.preventDefault();
        const button = event.target.closest('.autocomplete-item');
        if (button) {
            const index = Number(button.dataset.index);
            autocompleteState.activeIndex = index;
            insertSelectedCandidate();
        }
    });
}

document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
        hideContextMenu();
        hideAutocomplete();
    }
});

let nativeSelectionReportTimer = 0;
document.addEventListener('selectionchange', () => {
    if (state.isSelecting) return;
    clearTimeout(nativeSelectionReportTimer);
    nativeSelectionReportTimer = setTimeout(() => {
        reportCursorAndSelection(document.activeElement);
    }, 30);
});

let isSyncingScroll = false;
scrollContainer.addEventListener('scroll', () => {
    hideContextMenu();
    syncCsvHeaderScroll();
    prefetchAround(scrollContainer.scrollTop);
    queueRender();
    if (state.scrollSyncEnabled && !isSyncingScroll) {
        const firstVisible = lineAt(scrollContainer.scrollTop);
        const offset = scrollContainer.scrollTop - lineTop(firstVisible);
        post({
            type: 'editorScroll',
            firstLine: firstVisible,
            offset: offset
        });
    }
});

window.addEventListener('resize', () => queueRender(true));
window.addEventListener('dragstart', event => event.preventDefault(), false);
window.addEventListener('dragover', event => event.preventDefault(), false);
window.addEventListener('drop', event => event.preventDefault(), false);

findReplaceController.bind();

if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', event => {
        const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        handleCsharpMessage(msg);
    });
}

setupVirtualHeight();
post({ type: 'ready', virtualized: true });
