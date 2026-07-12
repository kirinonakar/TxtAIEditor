import { csvFormulaInput, scrollContainer, viewport } from './editor-dom.js';
import {
    MAX_RENDER_CHARS,
    escapeHtml,
    lineAt,
    measureRenderedRows,
    preserveScrollTop,
    queueRender,
    requestMissingLines,
    state,
    syncCustomSelectionClass,
    trimHexCacheToRange,
    totalVirtualHeight,
    visibleRange,
    viewportTopForLine
} from './editor-core.js';
import { renderLineContent } from './editor-highlighter.js';
import {
    drawEditableSelectionOverlays,
    hasCustomSelection,
    selectionBoundsForLine
} from './editor-selection.js';
import {
    isJsonCsvTableMode,
    prepareCsvTableRenderModel,
    renderCsvTableRows,
    restoreCsvFocusAfterRender
} from './editor-csv-table.js';
import { isPointOnScrollContainerScrollbar } from './editor-caret.js';

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
    let hexStickyHeader = null;
    let horizontalOverflowFrame = 0;
    const livePreviewProcessedImages = new Set();
    const livePreviewFailedImages = new Set();

    function ensureHexStickyHeader() {
        if (hexStickyHeader && hexStickyHeader.isConnected) {
            return hexStickyHeader;
        }

        hexStickyHeader = document.createElement('div');
        hexStickyHeader.id = 'hex-sticky-header';
        hexStickyHeader.className = 'line-row hex-sticky-header';
        hexStickyHeader.dataset.line = '1';
        hexStickyHeader.hidden = true;
        hexStickyHeader.innerHTML =
            '<div class="line-number">1</div>' +
            '<div class="line-text" contenteditable="false" spellcheck="false" data-line="1"></div>';
        scrollContainer.insertBefore(hexStickyHeader, scrollContainer.firstElementChild);
        return hexStickyHeader;
    }

    function updateHexStickyHeader() {
        const header = ensureHexStickyHeader();
        const isHexView = state.language === 'hex';
        header.hidden = !isHexView;
        if (!isHexView) {
            return;
        }

        const textElement = header.querySelector('.line-text');
        const headerText = state.cache.get(1);
        if (headerText === undefined) {
            textElement.textContent = '';
            requestMissingLines(1, 1);
            return;
        }

        textElement.innerHTML = renderLineContent(1, headerText);
    }

    function syncHorizontalOverflow() {
        if (horizontalOverflowFrame) {
            cancelAnimationFrame(horizontalOverflowFrame);
        }

        horizontalOverflowFrame = requestAnimationFrame(() => {
            horizontalOverflowFrame = 0;
            const contentWidth = Math.ceil(Math.max(
                viewport.scrollWidth || 0,
                viewport.getBoundingClientRect().width || 0,
                hexStickyHeader && !hexStickyHeader.hidden ? hexStickyHeader.scrollWidth || 0 : 0));
            const hasOverflow = contentWidth > Math.ceil(scrollContainer.clientWidth) + 1;
            document.body.classList.toggle('no-horizontal-overflow', !hasOverflow);
            if (!hasOverflow && scrollContainer.scrollLeft !== 0) {
                scrollContainer.scrollLeft = 0;
            }
        });
    }

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
        if (isPointOnScrollContainerScrollbar(clientX, clientY)) return 0;

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
        updateHexStickyHeader();

        const csvTableLineCount = state.csvTableEnabled ? prepareCsvTableRenderModel() : 0;
        const range = visibleRange();
        const livePreviewContextLines = state.inlineLivePreviewEnabled ? 120 : 0;
        let renderStart = Math.max(1, range.start - livePreviewContextLines);
        let renderEnd = range.end;
        if (state.csvTableEnabled) {
            const csvOverscan = 30;
            const firstVisibleCsvLine = lineAt(scrollContainer.scrollTop);
            const lastVisibleCsvLine = lineAt(scrollContainer.scrollTop + Math.max(scrollContainer.clientHeight, state.lineHeight));
            renderStart = Math.max(1, firstVisibleCsvLine - csvOverscan);
            renderEnd = Math.min(Math.max(1, csvTableLineCount || state.lineCount), lastVisibleCsvLine + csvOverscan);
        }
        const activeEl = document.activeElement;
        const isFocused = activeEl && activeEl.closest('.line-text') && activeEl.getAttribute('contenteditable') === 'true';
        const activeLine = isFocused ? Number(activeEl.dataset.line) : null;
        const activeCaret = isFocused ? getCaretOffset(activeEl) : 0;
        const getCachedLine = lineNumber => state.cache.has(lineNumber) ? state.cache.get(lineNumber) : undefined;
        const requestedSourceLine = state.inlineLivePreviewEnabled
            ? (state.inlineLivePreviewSourceLine || 0)
            : 0;
        const hasEditingLineInSourceBlock = containsInlineLivePreviewBlockLine(
            state.inlineLivePreviewEditableBlock,
            state.editingLine);
        const hasSourceFocus = requestedSourceLine > 0 &&
            ((isFocused &&
                (activeLine === requestedSourceLine ||
                    containsInlineLivePreviewBlockLine(state.inlineLivePreviewEditableBlock, activeLine))) ||
                state.editingLine === requestedSourceLine ||
                hasEditingLineInSourceBlock ||
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
        const csvModeKey = state.csvTableEnabled ? `${state.csvTableVersion || 0}:${state.csvTableColumnCount || 0}:${state.csvSelectedLine || 0}:${state.csvSelectedColumn || 0}:${state.csvVirtualLineCount || 0}` : '0';
        const horizontalRenderKey = state.csvTableEnabled ? scrollContainer.scrollLeft : 0;
        const rangeKey = `${range.start}:${range.end}:${renderStart}:${renderEnd}:${state.lineCount}:${scrollContainer.clientWidth}:${horizontalRenderKey}:${state.wordWrap}:${totalVirtualHeight()}:${state.cacheVersion}:${state.inlineLivePreviewEnabled}:${activeLine || 0}:${state.editingLine || 0}:${sourceLine}:${editablePreviewBlockKey}:${csvModeKey}`;
        if (!state.csvTableEnabled || !isJsonCsvTableMode()) {
            requestMissingLines(renderStart, renderEnd);
            trimHexCacheToRange(renderStart, renderEnd);
        }
        if (rangeKey === state.lastRangeKey) return;
        state.lastRangeKey = rangeKey;

        if (state.columnComposition) {
            return;
        }

        const composingRow = state.isComposing && state.compositionLine
            ? viewport.querySelector(`.line-row[data-line="${state.compositionLine}"]`)
            : null;

        const offsetY = viewportTopForLine(renderStart);
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
            syncHorizontalOverflow();
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
            const displayText = text;
            const contentEditable = !state.readOnly && hasLine ? 'true' : 'false';
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
            const isInlineLivePreviewSourceLine = state.inlineLivePreviewEnabled &&
                hasLine &&
                shouldShowSource &&
                !isLong &&
                !state.isComposing;
            // Very long lines stay fully editable, but syntax highlighting is skipped to avoid
            // creating thousands of token spans and blocking the UI thread.
            let lineContent = isLong
                ? escapeHtml(displayText)
                : renderLineContent(line, displayText, false, isInlineLivePreviewSourceLine);
            let livePreviewClass = '';
            let liveContentEditable = contentEditable;
            let livePreviewAttributes = '';
            if (isEditablePreviewBlockLine) {
                const blockEdgeClass = `${line === editablePreviewBlock.startLine ? ' live-preview-source-block-start' : ''}${line === editablePreviewBlock.endLine ? ' live-preview-source-block-end' : ''}`;
                const isMathFenceLine = editablePreviewBlock.kind === 'math' &&
                    (line < editablePreviewBlock.bodyStartLine || line > editablePreviewBlock.bodyEndLine);
                const blockMetaClass = editablePreviewBlock.kind === 'code'
                    ? `${line < editablePreviewBlock.bodyStartLine || line > editablePreviewBlock.bodyEndLine ? ' live-preview-source-code-fence' : ''}`
                    : editablePreviewBlock.kind === 'math'
                        ? `${isMathFenceLine ? ' live-preview-source-math-fence' : ''}`
                        : `${editablePreviewBlock.kind === 'table' && line === editablePreviewBlock.separatorLine ? ' live-preview-source-table-separator' : ''}`;
                livePreviewClass = ` live-preview-source-block live-preview-source-${editablePreviewBlock.kind}${blockEdgeClass}${blockMetaClass}`;
                const commonAttributes = ` data-live-preview-block-kind="${editablePreviewBlock.kind}" data-live-preview-block-start="${editablePreviewBlock.startLine}" data-live-preview-block-end="${editablePreviewBlock.endLine}"`;
                livePreviewAttributes = editablePreviewBlock.kind === 'code'
                    ? `${commonAttributes} data-live-preview-block-body-start="${editablePreviewBlock.bodyStartLine}" data-live-preview-block-body-end="${editablePreviewBlock.bodyEndLine}" data-live-preview-block-language="${escapeHtml(editablePreviewBlock.language || '')}"`
                    : editablePreviewBlock.kind === 'math'
                        ? `${commonAttributes} data-live-preview-block-body-start="${editablePreviewBlock.bodyStartLine}" data-live-preview-block-body-end="${editablePreviewBlock.bodyEndLine}" data-live-preview-block-opener="${escapeHtml(editablePreviewBlock.opener || '')}"`
                    : editablePreviewBlock.kind === 'table'
                        ? `${commonAttributes} data-live-preview-block-separator="${editablePreviewBlock.separatorLine}"`
                        : commonAttributes;
                if (isMathFenceLine) {
                    liveContentEditable = 'false';
                }
            } else if (isInlineLivePreviewSourceLine) {
                livePreviewClass = ' live-preview-source-line';
            }
            const livePreviewOptions = {
                mode: 'markdown',
                baseHref: state.livePreviewBaseHref || '',
                localResourceVersion: state.livePreviewLocalResourceVersion || '0',
                failedImageSources: livePreviewFailedImages,
                tabSize: state.tabSize || 4,
                rangeEnd: renderEnd,
                requireClosedFence: true,
                sourceLine: sourceLine
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
                    livePreviewClass = renderedLine.skipped
                        ? ' live-preview-skipped'
                        : renderedLine.empty
                            ? ' live-preview-row live-preview-empty'
                            : ' live-preview-row';
                }
            }
            const dirtyType = state.language === 'hex' ? null : state.dirtyLines.get(line);
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
        syncHorizontalOverflow();

        if (composingRow) {
            const placeholder = viewport.querySelector(`.line-row-placeholder[data-line="${state.compositionLine}"]`);
            if (placeholder) {
                placeholder.replaceWith(composingRow);
            }
        }
        refreshHoveredLineFromLastPointer();

        watchLivePreviewImages();
        measureRenderedRows();
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
                const pendingFocus = pendingInlineLivePreviewFocus;
                setCaret(element, columnZeroBased);
                restoreInlineLivePreviewScroll(pendingFocus);
                if (!focusToken || pendingInlineLivePreviewFocus?.token === focusToken) {
                    pendingInlineLivePreviewFocus = null;
                }
                scheduleInlineLivePreviewScrollRestore(pendingFocus);
            } else if (retries > 0) {
                focusLineWithRetry(lineNumber, columnZeroBased, retries - 1, focusToken);
            }
        });
    }

    function restoreInlineLivePreviewScroll(focus) {
        if (!focus?.preserveScroll) return;

        preserveScrollTop(focus.scrollTop);
        const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
        scrollContainer.scrollTop = Math.min(maxScrollTop, Math.max(0, Number(focus.scrollTop || 0)));
        scrollContainer.scrollLeft = Math.max(0, Number(focus.scrollLeft || 0));
    }

    function scheduleInlineLivePreviewScrollRestore(focus) {
        if (!focus?.preserveScroll) return;

        restoreInlineLivePreviewScroll(focus);
        requestAnimationFrame(() => {
            restoreInlineLivePreviewScroll(focus);
            requestAnimationFrame(() => restoreInlineLivePreviewScroll(focus));
        });
        setTimeout(() => restoreInlineLivePreviewScroll(focus), 80);
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
        } else if (kind === 'math') {
            block.bodyStartLine = Number(row.dataset.livePreviewBlockBodyStart || startLine + 1);
            block.bodyEndLine = Number(row.dataset.livePreviewBlockBodyEnd || endLine - 1);
            block.opener = row.dataset.livePreviewBlockOpener || '';
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

    function editableLineForMathBlock(lineNumber, block) {
        const bodyStartLine = Math.min(
            block.endLine,
            Math.max(block.startLine, Number(block.bodyStartLine || block.startLine + 1)));
        const bodyEndLine = Math.max(
            bodyStartLine,
            Math.min(block.endLine, Number(block.bodyEndLine || block.endLine - 1)));
        return Math.min(bodyEndLine, Math.max(bodyStartLine, Number(lineNumber || bodyStartLine)));
    }

    function beginInlineLivePreviewEdit(lineNumber, columnZeroBased = 0, options = {}) {
        let safeLine = Math.min(Math.max(1, Number(lineNumber || 1)), state.lineCount);
        const block = findEditablePreviewBlockContaining(
            safeLine,
            state.lineCount,
            line => state.cache.has(line) ? state.cache.get(line) : undefined,
            {
                tabSize: state.tabSize || 4
            });
        if (block?.kind === 'math') {
            safeLine = editableLineForMathBlock(safeLine, block);
        }

        const text = state.cache.get(safeLine) || '';
        const safeColumn = Math.max(0, Math.min(Number(columnZeroBased || 0), text.length));
        const token = ++inlineLivePreviewFocusSeq;
        const preserveScroll = !!options.preserveScroll;
        const savedScrollTop = preserveScroll
            ? Math.max(0, Number(options.scrollTop ?? scrollContainer.scrollTop))
            : 0;
        const savedScrollLeft = preserveScroll
            ? Math.max(0, Number(options.scrollLeft ?? scrollContainer.scrollLeft))
            : 0;
        if (preserveScroll) {
            state.alignCaretToY = null;
            preserveScrollTop(savedScrollTop);
        }
        pendingInlineLivePreviewFocus = {
            line: safeLine,
            column: safeColumn,
            token,
            preserveScroll,
            scrollTop: savedScrollTop,
            scrollLeft: savedScrollLeft
        };
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
        scheduleInlineLivePreviewScrollRestore(pendingInlineLivePreviewFocus);
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
            restoreInlineLivePreviewScroll(pending);
            if (pendingInlineLivePreviewFocus?.token === pending.token) {
                pendingInlineLivePreviewFocus = null;
            }
            scheduleInlineLivePreviewScrollRestore(pending);
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
            const imageKey = img.dataset.previewImageKey || src;
            if (livePreviewProcessedImages.has(imageKey)) return;

            let settled = false;
            const finish = loaded => {
                if (settled) return;
                settled = true;
                livePreviewProcessedImages.add(imageKey);
                if (!loaded) {
                    livePreviewFailedImages.add(imageKey);
                    img.replaceWith(document.createTextNode(img.getAttribute('alt') || ''));
                }
                state.lastRangeKey = '';
                measureRenderedRows();
            };
            if (img.complete) {
                finish(img.naturalWidth > 0);
            } else {
                img.addEventListener('load', () => finish(true), { once: true });
                img.addEventListener('error', () => finish(false), { once: true });
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
