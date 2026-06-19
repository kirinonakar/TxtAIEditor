import {
    findPanel,
    scrollContainer,
    viewport
} from './editor-dom.js';
import {
    post,
    queueRender,
    reportCursorAndSelection,
    selectedText,
    state,
    syncCustomSelectionClass
} from './editor-core.js';
import {
    hasCustomSelection,
    isPositionInsideSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    focusLine,
    lineElementFromEvent,
    lineTextFromElement,
    positionFromPointer,
    replaceSelectionWith,
    selectWordAtPointer,
    setCaret
} from './editor-commands.js';
import { showContextMenu } from './editor-context-menu.js';

export function bindPointerSelectionEvents({
    getPreciseLivePreviewPosition,
    renderer
}) {
    const {
        beginInlineLivePreviewEdit,
        keepEditablePreviewBlockFromElement,
        pendingInlineLivePreviewLine,
        updateHoveredLineFromCoordinates,
        updateHoveredLineFromPointer
    } = renderer;
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

        if (isInlinePreviewRow) {
            const precisePos = getPreciseLivePreviewPosition(position.element, event);
            state.alignCaretToY = event.clientY;
            state.selection = null;
            state.selectionAnchor = { line: precisePos.line, column: precisePos.column };
            syncCustomSelectionClass();
            state.isSelecting = false;
            state.isLineSelecting = false;
            state.isDragPotential = false;
            state.isDragMoving = false;
            state.dragSelectionData = null;
            state.dragDropPosition = null;
            state.dragStartPosition = null;
            state.currentLine = precisePos.line;
            state.currentColumn = precisePos.column + 1;
            beginInlineLivePreviewEdit(precisePos.line, precisePos.column);
        } else {
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
                }
            }
            if (event.shiftKey || (hadSelection && !clickInsideSelection)) {
                queueRender(true);
                setTimeout(() => focusLine(state.currentLine, Math.max(0, state.currentColumn - 1)), 0);
            }
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
}
