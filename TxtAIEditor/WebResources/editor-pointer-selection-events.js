import {
    contextMenu,
    findPanel,
    scrollContainer,
    viewport
} from './editor-dom.js';
import {
    clearCustomSelectionVisuals,
    post,
    preserveScrollTop,
    queueRender,
    reportCursorAndSelection,
    selectedText,
    state,
    syncCustomSelectionClass
} from './editor-core.js';
import {
    drawEditableSelectionOverlays,
    hasCustomSelection,
    isPositionInsideSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    focusLine,
    lineElementFromEvent,
    lineTextFromElement,
    positionFromPointer,
    prepareMultilineCompositionHost,
    replaceSelectionWith,
    selectWordAtPointer,
    setCaret,
    setNativeSelectionRangeInElement
} from './editor-commands.js';
import { isPointOnScrollContainerScrollbar } from './editor-caret.js';
import { showContextMenu, hideContextMenu } from './editor-context-menu.js';
import { autocompleteState, hideAutocomplete } from './editor-autocomplete.js';
import { cancelPostEditFocusFollowUps } from './editor-edit-focus.js';

const HEX_BYTES_PER_ROW = 16;
const LIVE_PREVIEW_DRAG_THRESHOLD_PX = 4;

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
    function getOpenableTokenAtColumn(text, column) {
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

        let tokenStart = start;
        let tokenEnd = end;
        while (tokenStart < tokenEnd && /\s/.test(value[tokenStart])) tokenStart++;
        while (tokenEnd > tokenStart && /\s/.test(value[tokenEnd - 1])) tokenEnd--;

        const token = value.slice(tokenStart, tokenEnd);
        if (!token) return null;

        const isUrl = /^https?:\/\/[^\s\)\(\]\[\}\{\>\<\"\']+/i.test(token);
        const isPath = /^[a-zA-Z]:[\/\\]/.test(token) ||
                       /^[\/\\]/.test(token) ||
                       /^\.\.?[\/\\]/.test(token) ||
                       ((token.includes('/') || token.includes('\\')) && !isUrl);

        if (!isUrl && !isPath) return null;

        return {
            text: token,
            start: tokenStart,
            end: tokenEnd,
            isUrl,
            isPath
        };
    }

    let openableHoverElement = null;
    let openableHoverRequestSeq = 0;
    let pendingOpenableHover = null;
    let validatedOpenableHover = null;

    function clearOpenableHoverVisual() {
        viewport.querySelectorAll('.openable-hover-underline-overlay').forEach(el => el.remove());
        openableHoverElement?.classList?.remove('openable-hover-target');
        openableHoverElement = null;
    }

    function cancelOpenableHoverValidation() {
        pendingOpenableHover = null;
        validatedOpenableHover = null;
        clearOpenableHoverVisual();
    }

    function setOpenableHoverElement(element) {
        if (openableHoverElement === element) return;
        openableHoverElement?.classList?.remove('openable-hover-target');
        openableHoverElement = element;
        openableHoverElement?.classList?.add('openable-hover-target');
    }

    function textPositionForOffset(element, offset) {
        if (!element) return null;

        let remaining = Math.max(0, Number(offset || 0));
        let lastText = null;

        function walk(node) {
            if (node.nodeType === Node.TEXT_NODE) {
                lastText = node;
                const length = node.textContent.length;
                if (remaining <= length) {
                    return { node, offset: remaining };
                }
                remaining -= length;
                return null;
            }

            for (const child of node.childNodes) {
                const found = walk(child);
                if (found) return found;
            }
            return null;
        }

        return walk(element) || (lastText
            ? { node: lastText, offset: lastText.textContent.length }
            : null);
    }

    function snapToDevicePixel(value) {
        const dpr = Number(window.devicePixelRatio || 1);
        if (!Number.isFinite(dpr) || dpr <= 0) return value;
        return Math.round(value * dpr) / dpr;
    }

    function drawOpenableHoverUnderline(element, token) {
        clearOpenableHoverVisual();

        const row = element?.closest?.('.line-row');
        if (!row || !token || token.start >= token.end) return;

        const startPosition = textPositionForOffset(element, token.start);
        const endPosition = textPositionForOffset(element, token.end);
        if (!startPosition || !endPosition) return;

        const range = document.createRange();
        try {
            range.setStart(startPosition.node, startPosition.offset);
            range.setEnd(endPosition.node, endPosition.offset);

            const rowRect = row.getBoundingClientRect();
            const elementRect = element.getBoundingClientRect();
            const computedStyle = window.getComputedStyle(element);
            const parsedLineHeight = Number.parseFloat(computedStyle.lineHeight);
            const lineHeight = Math.max(1, Number.isFinite(parsedLineHeight) ? parsedLineHeight : state.lineHeight);
            const underlineInset = Math.max(2, Math.round(lineHeight * 0.12));
            const rects = [...range.getClientRects()].filter(rect => rect.width > 0 && rect.height > 0);
            for (const rect of rects) {
                const visualLineIndex = Math.max(0, Math.round((rect.top - elementRect.top) / lineHeight));
                const underlineTop = elementRect.top + ((visualLineIndex + 1) * lineHeight) - underlineInset;
                const underline = document.createElement('div');
                underline.className = 'openable-hover-underline-overlay';
                underline.style.left = `${Math.max(0, snapToDevicePixel(rect.left - rowRect.left))}px`;
                underline.style.top = `${Math.max(0, snapToDevicePixel(underlineTop - rowRect.top))}px`;
                underline.style.width = `${Math.max(1, snapToDevicePixel(rect.width))}px`;
                row.appendChild(underline);
            }
            setOpenableHoverElement(element);
        } finally {
            range.detach?.();
        }
    }

    function openableHoverKey(position, token) {
        return `${position.line}:${token.start}:${token.end}:${token.text}`;
    }

    function requestOpenableHoverValidation(position, token) {
        const key = openableHoverKey(position, token);

        if (validatedOpenableHover?.key === key) {
            if (validatedOpenableHover.isOpenable) {
                drawOpenableHoverUnderline(position.element, token);
            } else {
                clearOpenableHoverVisual();
            }
            return;
        }

        if (pendingOpenableHover?.key === key) {
            return;
        }

        clearOpenableHoverVisual();
        const requestId = ++openableHoverRequestSeq;
        pendingOpenableHover = {
            requestId,
            key,
            line: position.line,
            token: { ...token }
        };
        post({
            type: 'openableHoverRequest',
            requestId,
            text: token.text,
            isUrl: token.isUrl,
            isPath: token.isPath
        });
    }

    function handleOpenableHoverResult(requestId, isOpenable) {
        const pending = pendingOpenableHover;
        if (!pending || pending.requestId !== Number(requestId || 0)) {
            return;
        }

        pendingOpenableHover = null;
        validatedOpenableHover = {
            key: pending.key,
            isOpenable: !!isOpenable
        };

        if (!isOpenable) {
            clearOpenableHoverVisual();
            return;
        }

        const element = viewport.querySelector(`.line-text[data-line="${pending.line}"]`);
        if (!element) return;

        const text = state.cache.get(pending.line) ?? lineTextFromElement(element);
        const token = getOpenableTokenAtColumn(text, pending.token.start);
        if (!token || openableHoverKey({ line: pending.line }, token) !== pending.key) {
            clearOpenableHoverVisual();
            return;
        }

        drawOpenableHoverUnderline(element, token);
    }

    function updateOpenableHoverUnderline(event) {
        if (state.csvTableEnabled ||
            state.isSelecting ||
            state.isDragPotential ||
            state.isDragMoving ||
            hasCustomSelection() ||
            isHexView()) {
            cancelOpenableHoverValidation();
            return;
        }

        const position = positionFromPointer(event);
        if (!position) {
            cancelOpenableHoverValidation();
            return;
        }

        const text = state.cache.get(position.line) ?? lineTextFromElement(position.element);
        const token = getOpenableTokenAtColumn(text, position.column);
        if (!token) {
            cancelOpenableHoverValidation();
            return;
        }

        if (token.isUrl) {
            validatedOpenableHover = null;
            pendingOpenableHover = null;
            drawOpenableHoverUnderline(position.element, token);
            return;
        }

        requestOpenableHoverValidation(position, token);
    }

    function isHexView() {
        return state.language === 'hex';
    }

    function hexLineInfo(line, element = null) {
        if (line < 2) return null;

        const text = state.cache.get(line) ?? (element ? lineTextFromElement(element) : '');
        const offsetMatch = /^(\s*[0-9A-F]{8,16})(\s{2})/.exec(text);
        if (!offsetMatch) return null;

        const hexStart = offsetMatch[0].length;
        const firstPipe = text.indexOf('|', hexStart);
        const lastPipe = text.lastIndexOf('|');
        if (firstPipe < 0 || lastPipe <= firstPipe) return null;

        let byteCount = 0;
        for (let i = 0; i < HEX_BYTES_PER_ROW; i++) {
            const byteStart = hexStart + (i * 3) + (i >= 8 ? 1 : 0);
            const pair = text.slice(byteStart, byteStart + 2);
            if (/^[0-9A-F]{2}$/i.test(pair)) {
                byteCount++;
            }
        }
        if (byteCount <= 0) return null;

        return {
            text,
            rowOffset: (line - 2) * HEX_BYTES_PER_ROW,
            hexStart,
            asciiStart: firstPipe + 1,
            byteCount
        };
    }

    function hexPaneFromColumn(info, column) {
        return column >= info.asciiStart ? 'ascii' : 'hex';
    }

    function hexColumnForByte(info, byteIndex, pane) {
        const safeByteIndex = Math.max(0, Math.min(byteIndex, info.byteCount - 1));
        return pane === 'ascii'
            ? info.asciiStart + safeByteIndex
            : info.hexStart + (safeByteIndex * 3) + (safeByteIndex >= 8 ? 1 : 0);
    }

    function hexByteIndexFromColumn(info, column, pane) {
        if (pane === 'ascii') {
            return Math.max(0, Math.min(column - info.asciiStart, info.byteCount - 1));
        }

        if (column <= info.hexStart) {
            return 0;
        }

        let previousByteIndex = 0;
        for (let i = 0; i < info.byteCount; i++) {
            const byteStart = hexColumnForByte(info, i, 'hex');
            if (column < byteStart) {
                return previousByteIndex;
            }

            if (column <= byteStart + 2) {
                return i;
            }

            previousByteIndex = i;
        }

        return previousByteIndex;
    }

    function hexPositionFromPointer(event) {
        const position = positionFromPointer(event);
        if (!position) return null;

        const info = hexLineInfo(position.line, position.element);
        if (!info) return null;

        const pane = hexPaneFromColumn(info, position.column);
        const byteIndex = hexByteIndexFromColumn(info, position.column, pane);
        const column = hexColumnForByte(info, byteIndex, pane);

        return {
            line: position.line,
            column,
            offset: info.rowOffset + byteIndex,
            pane,
            element: position.element
        };
    }

    function clearNativeSelection() {
        try {
            window.getSelection()?.removeAllRanges();
        } catch (e) { }
    }

    function setHexCursor(position) {
        state.hexCursorOffset = position.offset;
        state.hexSelectionPane = position.pane;
        state.currentLine = position.line;
        state.currentColumn = position.column + 1;
    }

    function setHexSelectionFromOffsets(anchorOffset, cursorOffset) {
        const startOffset = Math.min(anchorOffset, cursorOffset);
        const endOffset = Math.max(anchorOffset, cursorOffset) + 1;
        state.hexSelection = { startOffset, endOffset };
    }

    function beginHexSelection(event) {
        const position = hexPositionFromPointer(event);
        if (!position) return false;

        event.preventDefault();
        captureSelectionPointer(event);
        clearNativeSelection();

        const anchorOffset = event.shiftKey && state.hexSelectionAnchorOffset !== null
            ? state.hexSelectionAnchorOffset
            : position.offset;
        state.hexSelectionAnchorOffset = anchorOffset;
        state.selection = null;
        setHexCursor(position);
        setHexSelectionFromOffsets(anchorOffset, position.offset);
        syncCustomSelectionClass();
        state.isSelecting = true;
        state.isLineSelecting = false;
        state.isDragPotential = false;
        state.isDragMoving = false;
        state.dragSelectionData = null;
        state.dragDropPosition = null;
        state.dragStartPosition = null;
        document.body.classList.add('selecting');

        queueRender(true);
        reportCursorAndSelection(position.element);
        return true;
    }

    function updateHexSelectionFromPointer(event) {
        const position = hexPositionFromPointer(event);
        if (!position) return false;

        clearNativeSelection();
        const anchorOffset = state.hexSelectionAnchorOffset ?? position.offset;
        setHexCursor(position);
        setHexSelectionFromOffsets(anchorOffset, position.offset);
        queueRender(true);
        reportCursorAndSelection(position.element);
        return true;
    }

    function endHexSelection(event) {
        stopSelectionAutoScroll();
        releaseSelectionPointer(event);
        state.isSelecting = false;
        state.isLineSelecting = false;
        state.isDragPotential = false;
        state.isDragMoving = false;
        state.dragStartPosition = null;
        cleanupDragState();
        clearNativeSelection();
        document.body.classList.remove('selecting');
        syncCustomSelectionClass();
        queueRender(true);
        reportCursorAndSelection(document.activeElement);
    }

    let lastPointerDownTime = 0;
    let lastPointerDownPosition = null;
    let activeSelectionPointerId = null;
    let livePreviewPointer = null;
    let suppressNextLivePreviewClick = false;
    let suppressNativeLivePreviewInputUntil = 0;

    function captureSelectionPointer(event) {
        activeSelectionPointerId = event.pointerId;
        try {
            scrollContainer.setPointerCapture?.(event.pointerId);
        } catch (e) { }
    }

    function releaseSelectionPointer(event = null) {
        const pointerId = event?.pointerId ?? activeSelectionPointerId;
        activeSelectionPointerId = null;
        if (pointerId === null || pointerId === undefined) return;

        try {
            if (!scrollContainer.hasPointerCapture || scrollContainer.hasPointerCapture(pointerId)) {
                scrollContainer.releasePointerCapture?.(pointerId);
            }
        } catch (e) { }
    }

    function cancelActiveSelectionInteraction({ render = true } = {}) {
        const wasActive = state.isSelecting || state.isLineSelecting || state.isDragPotential || state.isDragMoving;
        if (!wasActive) return;

        stopSelectionAutoScroll();
        releaseSelectionPointer();
        state.isSelecting = false;
        state.isLineSelecting = false;
        state.dragStartPosition = null;
        cleanupDragState();
        document.body.classList.remove('selecting');
        syncCustomSelectionClass();
        if (render) {
            queueRender(true);
            reportCursorAndSelection(document.activeElement);
        }
    }

    function isEditablePreviewBlockElement(element) {
        return state.inlineLivePreviewEnabled &&
            element?.getAttribute?.('contenteditable') === 'true' &&
            element.closest?.('.line-row')?.classList?.contains('live-preview-source-block');
    }

    function livePreviewPositionFromPointer(event) {
        const position = positionFromPointer(event);
        if (!position) return null;

        if (state.inlineLivePreviewEnabled &&
            position.element?.getAttribute?.('contenteditable') !== 'true') {
            return getPreciseLivePreviewPosition(position.element, event);
        }

        return position;
    }

    function restoreScrollPosition(scrollTop, scrollLeft) {
        const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
        scrollContainer.scrollTop = Math.min(maxScrollTop, Math.max(0, Number(scrollTop || 0)));
        scrollContainer.scrollLeft = Math.max(0, Number(scrollLeft || 0));
    }

    function preserveAndRestoreScroll(scrollTop, scrollLeft) {
        preserveScrollTop(scrollTop);
        restoreScrollPosition(scrollTop, scrollLeft);
        requestAnimationFrame(() => {
            restoreScrollPosition(scrollTop, scrollLeft);
            requestAnimationFrame(() => restoreScrollPosition(scrollTop, scrollLeft));
        });
        setTimeout(() => restoreScrollPosition(scrollTop, scrollLeft), 80);
    }

    function suppressNextClickEvent() {
        suppressNextLivePreviewClick = true;
        setTimeout(() => {
            suppressNextLivePreviewClick = false;
        }, 250);
    }

    function beginLivePreviewPointer(event, startPosition) {
        const sourceBlockSelection = isEditablePreviewBlockElement(startPosition.element);
        livePreviewPointer = {
            pointerId: event.pointerId,
            start: {
                line: startPosition.line,
                column: startPosition.column,
                element: startPosition.element
            },
            clientX: event.clientX,
            clientY: event.clientY,
            scrollTop: scrollContainer.scrollTop,
            scrollLeft: scrollContainer.scrollLeft,
            extendSelection: !!event.shiftKey,
            sourceBlockSelection,
            selecting: false
        };

        event.preventDefault();
        event.stopPropagation();
        captureSelectionPointer(event);
        cancelOpenableHoverValidation();
        clearNativeSelection();

        if (livePreviewPointer.extendSelection) {
            startLivePreviewSelection(event);
            updateLivePreviewSelectionFromPosition(startPosition, event);
            preserveAndRestoreScroll(livePreviewPointer.scrollTop, livePreviewPointer.scrollLeft);
        }
    }

    function startLivePreviewSelection(event) {
        if (!livePreviewPointer || livePreviewPointer.selecting) return;

        livePreviewPointer.selecting = true;
        const anchor = livePreviewPointer.extendSelection && state.selectionAnchor
            ? state.selectionAnchor
            : { line: livePreviewPointer.start.line, column: livePreviewPointer.start.column };
        state.selectionAnchor = anchor;
        state.selection = null;
        state.isSelecting = true;
        state.isLineSelecting = false;
        state.isDragPotential = false;
        state.isDragMoving = false;
        state.dragSelectionData = null;
        state.dragDropPosition = null;
        state.dragStartPosition = null;
        document.body.classList.add('selecting');
        syncCustomSelectionClass();
        clearNativeSelection();
    }

    function updateLivePreviewSelectionFromPosition(position, event) {
        const quietSourceSelection = !!livePreviewPointer?.sourceBlockSelection;
        return updateSelectionFromPosition(position, event, {
            render: !quietSourceSelection,
            drawOverlays: quietSourceSelection
        });
    }

    function syncLivePreviewSourceSelectionFocus(selection = normalizeSelection()) {
        if (!selection || selection.isColumn || !hasCustomSelection()) return false;

        const focusLine = Math.max(1, Number(state.currentLine || selection.end.line || 1));
        const focusColumn = Math.max(0, Number(state.currentColumn || 1) - 1);
        const focusElement = viewport.querySelector(`.line-text[data-line="${focusLine}"]`);
        if (!isEditablePreviewBlockElement(focusElement)) return false;

        keepEditablePreviewBlockFromElement(focusElement);

        if (selection.start.line === selection.end.line) {
            const selectionElement = viewport.querySelector(`.line-text[data-line="${selection.start.line}"]`);
            if (isEditablePreviewBlockElement(selectionElement) &&
                setNativeSelectionRangeInElement(selectionElement, selection.start.column, selection.end.column)) {
                state.editingLine = selection.start.line;
                return true;
            }
        }

        setCaret(focusElement, focusColumn);
        return true;
    }

    function handleLivePreviewPointerMove(event) {
        if (!livePreviewPointer || livePreviewPointer.pointerId !== event.pointerId) {
            return false;
        }

        if ((event.buttons & 1) === 0) {
            finishLivePreviewPointer(event);
            return true;
        }

        event.preventDefault();
        event.stopPropagation();

        const distance = Math.hypot(event.clientX - livePreviewPointer.clientX, event.clientY - livePreviewPointer.clientY);
        if (!livePreviewPointer.selecting && distance <= LIVE_PREVIEW_DRAG_THRESHOLD_PX) {
            preserveAndRestoreScroll(livePreviewPointer.scrollTop, livePreviewPointer.scrollLeft);
            return true;
        }

        startLivePreviewSelection(event);
        const position = livePreviewPositionFromPointer(event);
        if (position) {
            updateLivePreviewSelectionFromPosition(position, event);
            preserveAndRestoreScroll(livePreviewPointer.scrollTop, livePreviewPointer.scrollLeft);
        }
        return true;
    }

    function finishLivePreviewPointer(event = null) {
        const pending = livePreviewPointer;
        if (!pending) return false;

        const endPosition = event ? livePreviewPositionFromPointer(event) : null;
        livePreviewPointer = null;
        stopSelectionAutoScroll();
        releaseSelectionPointer(event);
        suppressNextClickEvent();
        if (pending.sourceBlockSelection) {
            suppressNativeLivePreviewInputUntil = Date.now() + 500;
        }

        if (pending.selecting) {
            if (endPosition) {
                const quietSourceSelection = !!pending.sourceBlockSelection;
                updateSelectionFromPosition(endPosition, event, {
                    render: !quietSourceSelection,
                    drawOverlays: quietSourceSelection
                });
            }

            const hadSelection = hasCustomSelection();
            state.isSelecting = false;
            state.isLineSelecting = false;
            state.dragStartPosition = null;
            document.body.classList.remove('selecting');
            syncCustomSelectionClass();
            if (!hasCustomSelection()) {
                state.selection = null;
                syncCustomSelectionClass();
            }
            if (!pending.sourceBlockSelection && (hadSelection || hasCustomSelection())) {
                queueRender(true);
            } else if (pending.sourceBlockSelection) {
                drawEditableSelectionOverlays();
                syncLivePreviewSourceSelectionFocus();
            }
            preserveAndRestoreScroll(pending.scrollTop, pending.scrollLeft);
            reportCursorAndSelection(endPosition?.element || document.activeElement);
            return true;
        }

        const clickPosition = endPosition || pending.start;
        const clickColumn = Math.max(0, clickPosition.column);
        state.selection = null;
        state.selectionAnchor = { line: clickPosition.line, column: clickColumn };
        state.currentLine = clickPosition.line;
        state.currentColumn = clickColumn + 1;
        state.isSelecting = false;
        state.isLineSelecting = false;
        state.dragStartPosition = null;
        syncCustomSelectionClass();
        preserveAndRestoreScroll(pending.scrollTop, pending.scrollLeft);

        if (clickPosition.element?.getAttribute?.('contenteditable') === 'true') {
            keepEditablePreviewBlockFromElement(clickPosition.element);
            setCaret(clickPosition.element, clickColumn);
            preserveAndRestoreScroll(pending.scrollTop, pending.scrollLeft);
        } else {
            beginInlineLivePreviewEdit(clickPosition.line, clickColumn, {
                preserveScroll: true,
                scrollTop: pending.scrollTop,
                scrollLeft: pending.scrollLeft
            });
        }

        reportCursorAndSelection(clickPosition.element || document.activeElement);
        return true;
    }

    function cancelLivePreviewPointer({ render = true } = {}) {
        if (!livePreviewPointer) return;

        const pending = livePreviewPointer;
        livePreviewPointer = null;
        if (pending.sourceBlockSelection) {
            suppressNativeLivePreviewInputUntil = Date.now() + 500;
        }
        stopSelectionAutoScroll();
        releaseSelectionPointer();
        state.isSelecting = false;
        state.isLineSelecting = false;
        state.dragStartPosition = null;
        document.body.classList.remove('selecting');
        syncCustomSelectionClass();
        preserveAndRestoreScroll(pending.scrollTop, pending.scrollLeft);
        if (render) {
            queueRender(true);
            reportCursorAndSelection(document.activeElement);
        }
    }

    function isNativeDragEdit(event) {
        const inputType = event?.inputType || '';
        return inputType === 'insertFromDrop' ||
            inputType === 'deleteByDrag' ||
            inputType === 'insertReplacementText';
    }

    function shouldSuppressNativeLivePreviewEdit(event) {
        const element = lineElementFromEvent(event);
        if (!isEditablePreviewBlockElement(element)) return false;
        if (livePreviewPointer?.sourceBlockSelection) return true;
        return Date.now() < suppressNativeLivePreviewInputUntil && isNativeDragEdit(event);
    }

    viewport.addEventListener('beforeinput', event => {
        if (!shouldSuppressNativeLivePreviewEdit(event)) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        clearNativeSelection();
    }, true);

    viewport.addEventListener('input', event => {
        if (!shouldSuppressNativeLivePreviewEdit(event)) return;

        event.stopImmediatePropagation();
        clearNativeSelection();
        queueRender(true);
    }, true);

    scrollContainer.addEventListener('pointerdown', event => {
        cancelPostEditFocusFollowUps();
        if (state.csvTableEnabled) return;
        if (event.button !== 0 || findPanel.contains(event.target)) return;

        if (!contextMenu.hidden && !contextMenu.contains(event.target)) {
            hideContextMenu();
        }
        const popup = document.getElementById('autocomplete-popup');
        if (autocompleteState.isOpen && popup && !popup.contains(event.target)) {
            hideAutocomplete();
        }

        cancelActiveSelectionInteraction({ render: false });

        if (isHexView()) {
            if (beginHexSelection(event)) {
                return;
            }
        }

        if (event.ctrlKey) {
            const position = positionFromPointer(event);
            if (position) {
                const text = state.cache.get(position.line) ?? lineTextFromElement(position.element);
                const token = getOpenableTokenAtColumn(text, position.column);
                if (token) {
                    event.preventDefault();
                    event.stopPropagation();
                    cancelOpenableHoverValidation();
                    post({
                        type: 'ctrlClick',
                        text: token.text,
                        isUrl: token.isUrl,
                        isPath: token.isPath
                    });
                    return;
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
                captureSelectionPointer(event);

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
        const isLivePreviewPointerTarget = isInlinePreviewRow || isEditablePreviewBlockElement(position.element);
        event.preventDefault();

        if (isLivePreviewPointerTarget) {
            const precisePos = isInlinePreviewRow
                ? getPreciseLivePreviewPosition(position.element, event)
                : position;
            beginLivePreviewPointer(event, precisePos);
        } else {
            captureSelectionPointer(event);
            cancelOpenableHoverValidation();
            clearNativeSelection();

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
            const collapseSelectionWithoutExtending = hadSelection && !event.shiftKey && !clickInsideSelection;
            const collapseScrollTop = collapseSelectionWithoutExtending ? scrollContainer.scrollTop : 0;
            const collapseScrollLeft = collapseSelectionWithoutExtending ? scrollContainer.scrollLeft : 0;

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
            if (event.shiftKey || collapseSelectionWithoutExtending) {
                queueRender(true);
                const focusTargetLine = state.currentLine;
                const focusTargetColumn = Math.max(0, state.currentColumn - 1);
                setTimeout(() => {
                    focusLine(focusTargetLine, focusTargetColumn);
                    if (collapseSelectionWithoutExtending) {
                        scrollContainer.scrollTop = collapseScrollTop;
                        scrollContainer.scrollLeft = collapseScrollLeft;
                    }
                }, 0);
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
        if (livePreviewPointer) {
            finishLivePreviewPointer(event);
            return;
        }

        if (isHexView() && state.isSelecting) {
            endHexSelection(event);
            return;
        }

        stopSelectionAutoScroll();

        if (state.isDragMoving && state.dragSelectionData) {
            const dragData = state.dragSelectionData;
            const dropPos = state.dragDropPosition;
            const isCopy = state.isDragCopy;
            state.isSelecting = false;
            state.isLineSelecting = false;
            document.body.classList.remove('selecting');
            releaseSelectionPointer(event);
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
                const clickColumn = Math.max(0, clickPos.column);
                const clickScrollTop = scrollContainer.scrollTop;
                const clickScrollLeft = scrollContainer.scrollLeft;
                state.selection = null;
                state.selectionAnchor = { line: clickPos.line, column: clickColumn };
                state.currentLine = clickPos.line;
                state.currentColumn = clickColumn + 1;
                keepEditablePreviewBlockFromElement(clickPos.element);
                syncCustomSelectionClass();
                queueRender(true);
                setTimeout(() => {
                    focusLine(clickPos.line, clickColumn);
                    scrollContainer.scrollTop = clickScrollTop;
                    scrollContainer.scrollLeft = clickScrollLeft;
                }, 0);
                reportCursorAndSelection(clickPos.element || document.activeElement);
            } else if (hasCustomSelection()) {
                state.selection = null;
                syncCustomSelectionClass();
                queueRender(true);
            }
            state.isSelecting = true;
        }

        if (!state.isSelecting) return;
        state.isSelecting = false;
        state.isLineSelecting = false;
        document.body.classList.remove('selecting');
        syncCustomSelectionClass();
        releaseSelectionPointer(event);
        state.dragStartPosition = null;
        const hadSelection = hasCustomSelection();
        const selection = normalizeSelection();
        if (selection && !hasCustomSelection()) {
            state.selection = null;
            syncCustomSelectionClass();
        } else if (selection) {
            if (selection.isColumn) {
                // Column selection is painted with overlays. Keep a collapsed
                // native caret in the original contenteditable so Windows IMEs
                // (notably Japanese IME) retain their active input context.
                const inputHost = document.activeElement?.closest?.('.line-text');
                const domSelection = window.getSelection();
                const hasInputCaret = !!(inputHost && domSelection?.rangeCount &&
                    inputHost.contains(domSelection.getRangeAt(0).startContainer));
                if (!hasInputCaret) {
                    const anchor = state.selectionAnchor || selection.start;
                    const anchorElement = viewport.querySelector(`.line-text[data-line="${anchor.line}"]`);
                    if (anchorElement?.getAttribute('contenteditable') === 'true') {
                        setCaret(anchorElement, anchor.column, 0, true, false);
                    }
                }
            } else {
                clearNativeSelection();
            }
            if (!selection.isColumn && selection.start.line === selection.end.line) {
                const releaseLine = state.currentLine;
                const releaseColumn = Math.max(0, state.currentColumn - 1);
                const releaseElement = viewport.querySelector(`.line-text[data-line="${releaseLine}"]`);
                if (releaseElement?.getAttribute('contenteditable') === 'true') {
                    setCaret(releaseElement, releaseColumn, 0, true, false);
                }
            } else if (!selection.isColumn) {
                prepareMultilineCompositionHost(selection);
            }
        }
        if (hadSelection || hasCustomSelection()) {
            drawEditableSelectionOverlays();
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
    let selectionWheelUpdateSeq = 0;

    function selectionPointerSnapshot(event) {
        return {
            clientX: event.clientX,
            clientY: event.clientY,
            altKey: event.altKey,
            target: null
        };
    }

    function updateSelectionFromPointer(event, options = {}) {
        const position = positionFromPointer(event);
        if (!position) return false;

        return updateSelectionFromPosition(position, event, options);
    }

    function updateSelectionFromPosition(position, event, options = {}) {
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
            if (!newSelection.isColumn) {
                clearNativeSelection();
            }
            syncCustomSelectionClass();
            state.currentLine = position.line;
            state.currentColumn = position.column + 1;
            if (options.render === false) {
                if (options.drawOverlays) {
                    clearCustomSelectionVisuals();
                    drawEditableSelectionOverlays();
                }
            } else {
                queueRender(true);
            }
            reportCursorAndSelection(position.element, null, false);
        }

        return true;
    }

    function updateSelectionAutoScrollPointer(event) {
        selectionAutoScrollPointer = selectionPointerSnapshot(event);

        if (!selectionAutoScrollFrame) {
            selectionAutoScrollFrame = requestAnimationFrame(runSelectionAutoScroll);
        }
    }

    function scheduleSelectionUpdateAfterWheel(event) {
        const pointer = selectionPointerSnapshot(event);
        selectionAutoScrollPointer = pointer;
        const seq = ++selectionWheelUpdateSeq;

        setTimeout(() => {
            requestAnimationFrame(() => {
                updateSelectionFromWheelPointer(pointer, seq);
                requestAnimationFrame(() => updateSelectionFromWheelPointer(pointer, seq));
            });
        }, 0);
    }

    function updateSelectionFromWheelPointer(pointer, seq) {
        if (seq !== selectionWheelUpdateSeq ||
            selectionAutoScrollPointer !== pointer ||
            livePreviewPointer ||
            isHexView() ||
            !state.isSelecting ||
            state.isDragMoving ||
            state.isDragPotential) {
            return;
        }

        updateSelectionFromPointer(pointer, { render: false, drawOverlays: true });
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
        const y = selectionAutoScrollPointer.clientY;
        let dx = 0;
        let dy = 0;

        if (!state.wordWrap && scrollContainer.scrollWidth > scrollContainer.clientWidth + 1) {
            if (x > rect.right - edge) {
                dx = Math.ceil(Math.min(1, (x - (rect.right - edge)) / edge) * maxStep);
            } else if (x < rect.left + edge) {
                dx = -Math.ceil(Math.min(1, ((rect.left + edge) - x) / edge) * maxStep);
            }
        }

        if (scrollContainer.scrollHeight > scrollContainer.clientHeight + 1) {
            if (y > rect.bottom - edge) {
                dy = Math.ceil(Math.min(1, (y - (rect.bottom - edge)) / edge) * maxStep);
            } else if (y < rect.top + edge) {
                dy = -Math.ceil(Math.min(1, ((rect.top + edge) - y) / edge) * maxStep);
            }
        }

        if (dx !== 0 || dy !== 0) {
            const beforeLeft = scrollContainer.scrollLeft;
            const beforeTop = scrollContainer.scrollTop;
            scrollContainer.scrollLeft = Math.max(0, beforeLeft + dx);
            const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
            scrollContainer.scrollTop = Math.min(maxScrollTop, Math.max(0, beforeTop + dy));
            if (scrollContainer.scrollLeft !== beforeLeft || scrollContainer.scrollTop !== beforeTop) {
                updateSelectionFromPointer(
                    selectionAutoScrollPointer,
                    { render: false, drawOverlays: true });
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
        updateOpenableHoverUnderline(event);

        if (handleLivePreviewPointerMove(event)) {
            return;
        }

        if (isHexView()) {
            if (!state.isSelecting) return;
            if ((event.buttons & 1) === 0) {
                endHexSelection(event);
                return;
            }

            event.preventDefault();
            updateHexSelectionFromPointer(event);
            return;
        }

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
        updateSelectionFromPointer(event, { render: false, drawOverlays: true });
    });

    scrollContainer.addEventListener('pointerleave', event => {
        cancelOpenableHoverValidation();
        updateHoveredLineFromCoordinates(event.clientX, event.clientY);
    });

    scrollContainer.addEventListener('lostpointercapture', event => {
        if (livePreviewPointer && activeSelectionPointerId === event.pointerId) {
            cancelLivePreviewPointer();
            return;
        }
        if (activeSelectionPointerId === event.pointerId &&
            (state.isSelecting || state.isDragPotential || state.isDragMoving)) {
            cancelActiveSelectionInteraction();
        }
        if (activeSelectionPointerId === event.pointerId) {
            activeSelectionPointerId = null;
        }
    });

    scrollContainer.addEventListener('wheel', event => {
        cancelOpenableHoverValidation();
        if (!livePreviewPointer &&
            !isHexView() &&
            state.isSelecting &&
            !state.isDragPotential &&
            !state.isDragMoving) {
            scheduleSelectionUpdateAfterWheel(event);
            return;
        }

        cancelLivePreviewPointer({ render: false });
        cancelActiveSelectionInteraction({ render: false });
    }, { capture: true });

    scrollContainer.addEventListener('scroll', () => {
        cancelOpenableHoverValidation();
    }, { passive: true });

    window.addEventListener('pointerup', event => {
        endSelection(event);
    });

    window.addEventListener('pointercancel', event => {
        if (livePreviewPointer) {
            cancelLivePreviewPointer();
            return;
        }
        if (!state.isSelecting) return;
        cancelActiveSelectionInteraction();
    });

    window.addEventListener('blur', () => {
        cancelOpenableHoverValidation();
        cancelLivePreviewPointer();
        cancelActiveSelectionInteraction();
    });

    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            cancelOpenableHoverValidation();
            cancelLivePreviewPointer();
            cancelActiveSelectionInteraction();
        }
    });

    document.addEventListener('keydown', event => {
        if ((event.key === 'Control' || event.key === 'Meta') && hasCustomSelection()) {
            cancelOpenableHoverValidation();
            clearNativeSelection();
        }
    });

    viewport.addEventListener('click', event => {
        if (isHexView()) {
            reportCursorAndSelection(document.activeElement);
            return;
        }

        if (suppressNextLivePreviewClick) {
            suppressNextLivePreviewClick = false;
            event.preventDefault();
            event.stopPropagation();
            reportCursorAndSelection(document.activeElement);
            return;
        }

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
        if (isHexView()) {
            event.preventDefault();
            event.stopPropagation();
            return;
        }
        if (event.target.closest?.('.line-number')) return;
        if (selectWordAtPointer(event)) {
            event.preventDefault();
            event.stopPropagation();
        }
    });

    scrollContainer.addEventListener('contextmenu', event => {
        if (findPanel.contains(event.target)) return;
        if (isPointOnScrollContainerScrollbar(event.clientX, event.clientY)) return;
        event.preventDefault();

        if (isHexView()) {
            showContextMenu(event.clientX, event.clientY);
            return;
        }

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

    return {
        handleOpenableHoverResult
    };
}
