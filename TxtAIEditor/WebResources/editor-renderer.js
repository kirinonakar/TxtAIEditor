import { csvFormulaInput, scrollContainer, viewport } from './editor-dom.js';
import {
    MAX_RENDER_CHARS,
    escapeHtml,
    lineAt,
    lineTop,
    measureRenderedRows,
    queueRender,
    requestMissingLines,
    state,
    syncCustomSelectionClass,
    totalVirtualHeight,
    visibleRange
} from './editor-core.js';
import { renderLineContent } from './editor-highlighter.js';
import {
    drawEditableSelectionOverlays,
    hasCustomSelection,
    selectionBoundsForLine
} from './editor-selection.js';
import {
    renderCsvTableRows,
    restoreCsvFocusAfterRender
} from './editor-csv-table.js';

function createEditorRenderer({
    findEditablePreviewBlockContaining,
    getCaretOffset,
    renderMermaidBlocks,
    renderPreviewLineAt,
    setCaret
}) {
    let hoveredLineNumber = 0;
    let lastPointerClientX = null;
    let lastPointerClientY = null;
    let pendingInlineLivePreviewFocus = null;
    let inlineLivePreviewFocusSeq = 0;
    const livePreviewProcessedImages = new Set();

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

    function updateHoveredLineFromCoordinates(clientX, clientY) {
        lastPointerClientX = clientX;
        lastPointerClientY = clientY;
        setHoveredLineNumber(lineNumberFromPointerCoordinates(clientX, clientY));
    }

    function refreshHoveredLineFromLastPointer() {
        if (lastPointerClientX === null || lastPointerClientY === null) return;
        setHoveredLineNumber(lineNumberFromPointerCoordinates(lastPointerClientX, lastPointerClientY));
    }

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

    function clearPendingInlineLivePreviewFocus() {
        pendingInlineLivePreviewFocus = null;
    }

    function clearPendingInlineLivePreviewFocusForLine(lineNumber) {
        if (pendingInlineLivePreviewFocus?.line === lineNumber) {
            pendingInlineLivePreviewFocus = null;
        }
    }

    function pendingInlineLivePreviewLine() {
        return pendingInlineLivePreviewFocus?.line || 0;
    }

    function watchLivePreviewImages() {
        if (!state.inlineLivePreviewEnabled) return;
        viewport.querySelectorAll('.live-preview-row img').forEach(img => {
            if (img.dataset.livePreviewImageWatch === '1') return;
            img.dataset.livePreviewImageWatch = '1';
            const src = img.getAttribute('src');
            if (!src) return;
            if (livePreviewProcessedImages.has(src)) return;

            const update = () => {
                livePreviewProcessedImages.add(src);
                state.lastRangeKey = '';
                measureRenderedRows();
            };
            if (img.complete) {
                livePreviewProcessedImages.add(src);
            } else {
                img.addEventListener('load', update, { once: true });
                img.addEventListener('error', update, { once: true });
            }
        });
    }

    return {
        beginInlineLivePreviewEdit,
        clearPendingInlineLivePreviewFocus,
        clearPendingInlineLivePreviewFocusForLine,
        focusLineWithRetry,
        keepEditablePreviewBlockFromElement,
        lineNumberFromPointerCoordinates,
        pendingInlineLivePreviewLine,
        render,
        setHoveredLineNumber,
        updateHoveredLineFromCoordinates,
        updateHoveredLineFromPointer
    };
}

export { createEditorRenderer };
