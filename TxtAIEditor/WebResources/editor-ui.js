import {
    findInput,
    findPanel,
    contextMenu,
    scrollContainer,
    viewport,
    csvFormulaInput
} from './editor-dom.js';
import {
    MAX_RENDER_CHARS,
    applyOptions,
    activeEditableElement,
    configureEditorCoreRuntime,
    escapeHtml,
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
    requestMissingLines,
    selectedText,
    selectionInfo,
    setOriginalLines,
    setupModel,
    setupVirtualHeight,
    state,
    syncCustomSelectionClass,
    totalVirtualHeight,
    updateLineFromHost,
    visibleRange
} from './editor-core.js';
import { renderLineContent } from './editor-highlighter.js';
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
    normalizeSelection,
    selectionBoundsForLine
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
    renderCsvTableRows,
    restoreCsvFocusAfterRender,
    selectedCsvText,
    setCsvTableMode,
    syncCsvHeaderScroll,
    updateCsvLocalization
} from './editor-csv-table.js';

configureEditorCoreRuntime({
    drawEditableSelectionOverlays,
    getCaretOffset,
    hasCustomSelection,
    isLineInColumnComposition,
    normalizeSelection,
    render
});

initializeMermaid(new URLSearchParams(window.location.search).get('theme') || 'Dark');

let lastCacheVersion = -1;
let hoveredLineNumber = 0;
let lastPointerClientX = null;
let lastPointerClientY = null;
let suppressNativePasteUntil = 0;

function setHoveredLineNumber(lineNumber) {
    const nextLineNumber = Math.max(0, Number(lineNumber || 0));
    if (hoveredLineNumber === nextLineNumber) return;

    if (hoveredLineNumber > 0) {
        viewport
            .querySelector(`.line-row.hovered-row[data-line="${hoveredLineNumber}"]`)
            ?.classList.remove('hovered-row');
    }

    hoveredLineNumber = nextLineNumber;
    if (hoveredLineNumber > 0) {
        viewport
            .querySelector(`.line-row[data-line="${hoveredLineNumber}"]`)
            ?.classList.add('hovered-row');
    }
}

function lineNumberFromPointerCoordinates(clientX, clientY) {
    if (!Number.isFinite(clientX) || !Number.isFinite(clientY)) return 0;

    const containerRect = scrollContainer.getBoundingClientRect();
    const isInsideContainer =
        clientX >= containerRect.left &&
        clientX <= containerRect.right &&
        clientY >= containerRect.top &&
        clientY <= containerRect.bottom;
    if (!isInsideContainer) return 0;

    const hit = document.elementFromPoint(clientX, clientY);
    const row = hit?.closest?.('.line-row');
    if (row && viewport.contains(row)) {
        return Number(row.dataset.line || 0);
    }

    return lineAt(scrollContainer.scrollTop + clientY - containerRect.top);
}

function updateHoveredLineFromPointer(event) {
    lastPointerClientX = event.clientX;
    lastPointerClientY = event.clientY;
    setHoveredLineNumber(lineNumberFromPointerCoordinates(event.clientX, event.clientY));
}

function refreshHoveredLineFromLastPointer() {
    if (lastPointerClientX === null || lastPointerClientY === null) return;
    setHoveredLineNumber(lineNumberFromPointerCoordinates(lastPointerClientX, lastPointerClientY));
}

// Main Render Loop
function render() {
    if (!state.initialized) return;

    const printContainer = document.getElementById('print-container');
    if (printContainer && printContainer.style.display === 'block') {
        return;
    }

    syncCustomSelectionClass();

    const range = visibleRange();
    const livePreviewContextLines = state.inlineLivePreviewEnabled ? 120 : 0;
    let renderStart = Math.max(1, range.start - livePreviewContextLines);
    let renderEnd = range.end;
    if (state.csvTableEnabled) {
        const csvOverscan = 30;
        const firstVisibleCsvLine = lineAt(scrollContainer.scrollTop);
        const lastVisibleCsvLine = lineAt(scrollContainer.scrollTop + Math.max(scrollContainer.clientHeight, state.lineHeight));
        renderStart = Math.max(1, firstVisibleCsvLine - csvOverscan);
        renderEnd = Math.min(state.lineCount, lastVisibleCsvLine + csvOverscan);
    }
    const activeEl = document.activeElement;
    const isFocused = activeEl && activeEl.closest('.line-text') && activeEl.getAttribute('contenteditable') === 'true';
    const activeLine = isFocused ? Number(activeEl.dataset.line) : null;
    const activeCaret = isFocused ? getCaretOffset(activeEl) : 0;
    const getCachedLine = lineNumber => state.cache.has(lineNumber) ? state.cache.get(lineNumber) : undefined;
    const requestedSourceLine = state.inlineLivePreviewEnabled
        ? (state.inlineLivePreviewSourceLine || 0)
        : 0;
    const hasSourceFocus = requestedSourceLine > 0 &&
        ((isFocused &&
            (activeLine === requestedSourceLine ||
                containsInlineLivePreviewBlockLine(state.inlineLivePreviewEditableBlock, activeLine))) ||
            state.editingLine === requestedSourceLine ||
            pendingInlineLivePreviewFocus?.line === requestedSourceLine);
    const sourceLine = hasSourceFocus ? requestedSourceLine : 0;
    if (requestedSourceLine && !sourceLine) {
        state.inlineLivePreviewSourceLine = null;
        state.inlineLivePreviewEditableBlock = null;
    }
    const editablePreviewBlock = state.inlineLivePreviewEnabled && sourceLine
        ? editablePreviewBlockForLine(sourceLine, getCachedLine)
        : null;
    const editablePreviewBlockKey = editablePreviewBlock
        ? `${editablePreviewBlock.kind}:${editablePreviewBlock.startLine}:${editablePreviewBlock.endLine}`
        : '';
    const csvModeKey = state.csvTableEnabled ? `${state.csvTableVersion || 0}:${state.csvTableColumnCount || 0}:${state.csvSelectedLine || 0}:${state.csvSelectedColumn || 0}` : '0';
    const rangeKey = `${range.start}:${range.end}:${renderStart}:${renderEnd}:${state.lineCount}:${scrollContainer.clientWidth}:${scrollContainer.scrollLeft}:${state.wordWrap}:${totalVirtualHeight()}:${state.cacheVersion}:${state.inlineLivePreviewEnabled}:${activeLine || 0}:${state.editingLine || 0}:${sourceLine}:${editablePreviewBlockKey}:${csvModeKey}`;
    requestMissingLines(renderStart, renderEnd);
    if (rangeKey === state.lastRangeKey) return;
    state.lastRangeKey = rangeKey;

    if (state.columnComposition) {
        return;
    }

    const composingRow = state.isComposing && state.compositionLine
        ? viewport.querySelector(`.line-row[data-line="${state.compositionLine}"]`)
        : null;

    const offsetY = lineTop(renderStart);
    viewport.style.transform = `translateY(${offsetY}px)`;

    if (state.csvTableEnabled) {
        const activeCell = document.activeElement?.closest?.('.csv-cell');
        const isEditingFormula = document.activeElement === csvFormulaInput;
        const isEditingActiveCell = state.csvEditMode === 'edit' && ((activeCell &&
            activeCell.getAttribute('contenteditable') === 'true' &&
            Number(activeCell.dataset.line || 0) === state.csvSelectedLine &&
            Number(activeCell.dataset.csvColumn || 0) === state.csvSelectedColumn) || isEditingFormula);

        if (isEditingActiveCell || state.csvCellComposing) {
            return;
        }

        viewport.innerHTML = renderCsvTableRows(renderStart, renderEnd, hoveredLineNumber).join('');
        refreshHoveredLineFromLastPointer();
        restoreCsvFocusAfterRender();
        return;
    }

    const rows = [];
    const livePreviewSkipRef = { val: 0 };
    for (let line = renderStart; line <= renderEnd; line++) {
        if (composingRow && line === state.compositionLine) {
            rows.push(`<div class="line-row-placeholder" data-line="${line}"></div>`);
            continue;
        }

        const hasLine = state.cache.has(line);
        const text = hasLine ? state.cache.get(line) : '';
        const isLong = hasLine && text.length > MAX_RENDER_CHARS;
        const displayText = isLong
            ? `${text.slice(0, MAX_RENDER_CHARS)} ... [긴 줄: ${text.length.toLocaleString()}자, 렌더링 보호]`
            : text;
        const contentEditable = !state.readOnly && hasLine && !isLong ? 'true' : 'false';
        const selectionBounds = selectionBoundsForLine(line, displayText.length);
        const isInSelection = !!selectionBounds;
        const isSelectedEmptyLine = isInSelection && displayText.length === 0 && hasCustomSelection();
        const textClass = `line-text${hasLine ? '' : ' loading'}${isLong ? ' long-line' : ''}`;
        const shouldShowSelectionSource = state.inlineLivePreviewEnabled &&
            hasLine &&
            !isLong &&
            !!selectionBounds &&
            (state.isSelecting || hasCustomSelection());
        const isEditablePreviewBlockLine = !!editablePreviewBlock &&
            line >= editablePreviewBlock.startLine &&
            line <= editablePreviewBlock.endLine;
        const shouldShowSource = line === sourceLine ||
            isEditablePreviewBlockLine ||
            shouldShowSelectionSource ||
            isLong ||
            !state.inlineLivePreviewEnabled ||
            state.isComposing ||
            !hasLine;
        let lineContent = renderLineContent(line, displayText);
        let livePreviewClass = '';
        let liveContentEditable = contentEditable;
        let livePreviewAttributes = '';
        if (isEditablePreviewBlockLine) {
            const blockEdgeClass = `${line === editablePreviewBlock.startLine ? ' live-preview-source-block-start' : ''}${line === editablePreviewBlock.endLine ? ' live-preview-source-block-end' : ''}`;
            const blockMetaClass = editablePreviewBlock.kind === 'code'
                ? `${line < editablePreviewBlock.bodyStartLine || line > editablePreviewBlock.bodyEndLine ? ' live-preview-source-code-fence' : ''}`
                : `${editablePreviewBlock.kind === 'table' && line === editablePreviewBlock.separatorLine ? ' live-preview-source-table-separator' : ''}`;
            livePreviewClass = ` live-preview-source-block live-preview-source-${editablePreviewBlock.kind}${blockEdgeClass}${blockMetaClass}`;
            const commonAttributes = ` data-live-preview-block-kind="${editablePreviewBlock.kind}" data-live-preview-block-start="${editablePreviewBlock.startLine}" data-live-preview-block-end="${editablePreviewBlock.endLine}"`;
            livePreviewAttributes = editablePreviewBlock.kind === 'code'
                ? `${commonAttributes} data-live-preview-block-body-start="${editablePreviewBlock.bodyStartLine}" data-live-preview-block-body-end="${editablePreviewBlock.bodyEndLine}" data-live-preview-block-language="${escapeHtml(editablePreviewBlock.language || '')}"`
                : editablePreviewBlock.kind === 'table'
                    ? `${commonAttributes} data-live-preview-block-separator="${editablePreviewBlock.separatorLine}"`
                    : commonAttributes;
        }
        const livePreviewOptions = {
            mode: 'markdown',
            baseHref: state.livePreviewBaseHref || '',
            tabSize: state.tabSize || 4,
            rangeEnd: renderEnd,
            requireClosedFence: true
        };

        if (state.inlineLivePreviewEnabled && hasLine && shouldShowSource && !isLong && !state.isComposing) {
            if (!isEditablePreviewBlockLine && line !== sourceLine) {
                const renderedLine = renderPreviewLineAt(
                    line,
                    state.lineCount,
                    getCachedLine,
                    livePreviewOptions,
                    livePreviewSkipRef);
                if (renderedLine.extendRangeEnd > renderEnd) {
                    const nextEnd = Math.min(state.lineCount, renderedLine.extendRangeEnd);
                    requestMissingLines(renderEnd + 1, nextEnd);
                    renderEnd = nextEnd;
                }
            }
        } else if (state.inlineLivePreviewEnabled && hasLine && !shouldShowSource) {
            const renderedLine = renderPreviewLineAt(
                line,
                state.lineCount,
                getCachedLine,
                livePreviewOptions,
                livePreviewSkipRef);
            if (renderedLine.extendRangeEnd > renderEnd) {
                const nextEnd = Math.min(state.lineCount, renderedLine.extendRangeEnd);
                requestMissingLines(renderEnd + 1, nextEnd);
                renderEnd = nextEnd;
            }
            if (!renderedLine.pending && !renderedLine.source) {
                lineContent = renderedLine.html;
                liveContentEditable = 'false';
                livePreviewClass = renderedLine.html ? ' live-preview-row' : ' live-preview-skipped';
            }
        }
        const dirtyType = state.dirtyLines.get(line);
        const dirtyClass = dirtyType ? ` dirty-${dirtyType}` : '';
        const editingClass = line === activeLine || line === state.editingLine || line === state.compositionLine
            ? ' editing-row'
            : '';
        const hoveredClass = line === hoveredLineNumber ? ' hovered-row' : '';
        rows.push(
            `<div class="line-row${livePreviewClass}${editingClass}${hoveredClass}${isInSelection ? ' selected-row' : ''}${isSelectedEmptyLine ? ' selected-empty-row' : ''}${dirtyClass}" data-line="${line}"${livePreviewAttributes}>` +
            `<div class="line-number">${line}</div>` +
            `<div class="${textClass}" contenteditable="${liveContentEditable}" spellcheck="false" data-line="${line}">${lineContent}</div>` +
            `</div>`
        );
    }

    viewport.innerHTML = rows.join('');

    if (composingRow) {
        const placeholder = viewport.querySelector(`.line-row-placeholder[data-line="${state.compositionLine}"]`);
        if (placeholder) {
            placeholder.replaceWith(composingRow);
        }
    }
    refreshHoveredLineFromLastPointer();

    measureRenderedRows();
    watchLivePreviewImages();
    renderMermaidBlocks(viewport, () => measureRenderedRows());

    if (pendingInlineLivePreviewFocus) {
        focusPendingInlineLivePreviewLine();
    } else if (isFocused && activeLine !== null) {
        const element = viewport.querySelector(`.line-text[data-line="${activeLine}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            setCaret(element, activeCaret);
        }
    }

    drawEditableSelectionOverlays();
}

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
            pendingInlineLivePreviewFocus = null;
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

function focusLineWithRetry(lineNumber, columnZeroBased, retries = 20, focusToken = 0) {
    const schedule = retries === 20 ? requestAnimationFrame : callback => setTimeout(callback, 35);
    schedule(() => {
        if (focusToken && pendingInlineLivePreviewFocus?.token !== focusToken) {
            return;
        }
        const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        if (element && element.getAttribute('contenteditable') === 'true') {
            setCaret(element, columnZeroBased);
            if (!focusToken || pendingInlineLivePreviewFocus?.token === focusToken) {
                pendingInlineLivePreviewFocus = null;
            }
        } else if (retries > 0) {
            focusLineWithRetry(lineNumber, columnZeroBased, retries - 1, focusToken);
        }
    });
}

let pendingInlineLivePreviewFocus = null;
let inlineLivePreviewFocusSeq = 0;

function containsInlineLivePreviewBlockLine(block, lineNumber) {
    return !!block &&
        lineNumber >= block.startLine &&
        lineNumber <= block.endLine;
}

function isInlinePreviewBlockInside(outerBlock, innerBlock) {
    return !!outerBlock && !!innerBlock &&
        innerBlock.startLine >= outerBlock.startLine &&
        innerBlock.endLine <= outerBlock.endLine;
}

function editablePreviewBlockForLine(lineNumber, getLine) {
    if (!state.inlineLivePreviewEnabled || !lineNumber) {
        state.inlineLivePreviewEditableBlock = null;
        return null;
    }

    const existing = state.inlineLivePreviewEditableBlock;
    const detected = findEditablePreviewBlockContaining(lineNumber, state.lineCount, getLine, {
        tabSize: state.tabSize || 4
    });
    if (containsInlineLivePreviewBlockLine(existing, lineNumber) &&
        (!detected || isInlinePreviewBlockInside(existing, detected))) {
        return existing;
    }

    if (detected) {
        state.inlineLivePreviewEditableBlock = detected;
        return detected;
    }

    if (containsInlineLivePreviewBlockLine(existing, lineNumber)) {
        return existing;
    }

    state.inlineLivePreviewEditableBlock = null;
    return null;
}

function editablePreviewBlockFromRow(row) {
    if (!row?.classList?.contains('live-preview-source-block')) return null;
    const startLine = Number(row.dataset.livePreviewBlockStart || 0);
    const endLine = Number(row.dataset.livePreviewBlockEnd || 0);
    const kind = row.dataset.livePreviewBlockKind || '';
    if (!kind || !startLine || !endLine) return null;

    const block = { kind, startLine, endLine };
    if (kind === 'code') {
        block.bodyStartLine = Number(row.dataset.livePreviewBlockBodyStart || startLine + 1);
        block.bodyEndLine = Number(row.dataset.livePreviewBlockBodyEnd || endLine - 1);
        block.language = row.dataset.livePreviewBlockLanguage || '';
    } else if (kind === 'table') {
        block.separatorLine = Number(row.dataset.livePreviewBlockSeparator || startLine + 1);
    }
    return block;
}

function keepEditablePreviewBlockFromElement(element) {
    if (!state.inlineLivePreviewEnabled || !element) return;
    const block = editablePreviewBlockFromRow(element.closest('.line-row'));
    if (block) {
        state.inlineLivePreviewEditableBlock = block;
    }
}

function beginInlineLivePreviewEdit(lineNumber, columnZeroBased = 0) {
    const safeLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
    const block = findEditablePreviewBlockContaining(
        safeLine,
        state.lineCount,
        line => state.cache.has(line) ? state.cache.get(line) : undefined,
        {
            tabSize: state.tabSize || 4
        });

    const text = state.cache.get(safeLine) || '';
    const safeColumn = Math.max(0, Math.min(Number(columnZeroBased || 0), text.length));
    const token = ++inlineLivePreviewFocusSeq;
    pendingInlineLivePreviewFocus = { line: safeLine, column: safeColumn, token };
    const activeElement = document.activeElement?.closest?.('.line-text');
    if (activeElement && Number(activeElement.dataset.line || 0) !== safeLine) {
        activeElement.blur();
    }
    state.inlineLivePreviewSourceLine = safeLine;
    state.inlineLivePreviewEditableBlock = block;
    state.editingLine = safeLine;
    state.currentLine = safeLine;
    state.currentColumn = safeColumn + 1;
    queueRender(true);
    focusLineWithRetry(safeLine, safeColumn, 20, token);

    setTimeout(() => {
        if (pendingInlineLivePreviewFocus?.token !== token) return;
        pendingInlineLivePreviewFocus = null;
        if (!document.activeElement?.closest?.(`.line-text[data-line="${safeLine}"]`)) {
            state.lastRangeKey = '';
            queueRender(true);
        }
    }, 1600);
}

function focusPendingInlineLivePreviewLine() {
    const pending = pendingInlineLivePreviewFocus;
    if (!pending) return;
    const element = viewport.querySelector(`.line-text[data-line="${pending.line}"]`);
    if (element && element.getAttribute('contenteditable') === 'true') {
        setCaret(element, pending.column);
        if (pendingInlineLivePreviewFocus?.token === pending.token) {
            pendingInlineLivePreviewFocus = null;
        }
        return;
    }
    focusLineWithRetry(pending.line, pending.column, 20, pending.token);
}

function watchLivePreviewImages() {
    if (!state.inlineLivePreviewEnabled) return;
    viewport.querySelectorAll('.live-preview-row img').forEach(img => {
        if (img.dataset.livePreviewImageWatch === '1') return;
        img.dataset.livePreviewImageWatch = '1';
        const update = () => {
            state.lastRangeKey = '';
            measureRenderedRows();
        };
        if (!img.complete) {
            img.addEventListener('load', update, { once: true });
            img.addEventListener('error', update, { once: true });
        }
    });
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
        if (pendingInlineLivePreviewFocus?.line === state.editingLine) {
            pendingInlineLivePreviewFocus = null;
        }
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
    lastPointerClientX = event.clientX;
    lastPointerClientY = event.clientY;
    setHoveredLineNumber(lineNumberFromPointerCoordinates(event.clientX, event.clientY));
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

    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        event.preventDefault();
        moveCaretHorizontal(element, event.key === 'ArrowLeft' ? -1 : 1, event.shiftKey);
        return;
    }

    if (event.key === 'ArrowUp') {
        event.preventDefault();
        moveCaretVertical(element, -1);
        return;
    }

    if (event.key === 'ArrowDown') {
        event.preventDefault();
        moveCaretVertical(element, 1);
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
        if (pendingInlineLivePreviewFocus?.line !== lineNumber) {
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
