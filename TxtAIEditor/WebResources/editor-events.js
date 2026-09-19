import {
    contextMenu,
    scrollContainer
} from './editor-dom.js';
import {
    clearMeasuredLineHeights,
    clearPreservedScrollTop,
    configureEditorCoreRuntime,
    lineAt,
    lineTop,
    maximumVirtualScrollTop,
    post,
    prefetchAround,
    queueRender,
    reportCursorAndSelection,
    selectionController,
    setupVirtualHeight,
    state,
    usesCompressedScroll,
    usesFullDocumentRender,
    visualScrollDeltaToScrollTopDelta,
    viewportController
} from './editor-core.js';
import {
    autocompleteState,
    hideAutocomplete,
    insertSelectedCandidate
} from './editor-autocomplete.js';
import { bindContextMenu, hideContextMenu } from './editor-context-menu.js';
import { bindCsvTable, syncCsvHeaderScroll } from './editor-csv-table.js';
import { bindClipboardEvents } from './editor-clipboard-events.js';
import { bindKeyboardEvents } from './editor-keyboard-events.js';
import { bindPointerSelectionEvents } from './editor-pointer-selection-events.js';
import { bindTextInputEvents } from './editor-text-input-events.js';
import { hasCustomSelection } from './editor-selection.js';

export function bindEditorEvents({
    findReplaceController,
    openFindPanel,
    getPreciseLivePreviewPosition,
    renderer
}) {
    bindTextInputEvents({ renderer });
    const pointerEvents = bindPointerSelectionEvents({ getPreciseLivePreviewPosition, renderer });
    bindKeyboardEvents({
        openFindPanel,
        cancelDragInteraction: pointerEvents.cancelDragInteraction
    });
    const clipboardEvents = bindClipboardEvents();
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
        if (selectionController.isSelecting || hasCustomSelection()) return;
        clearTimeout(nativeSelectionReportTimer);
        nativeSelectionReportTimer = setTimeout(() => {
            reportCursorAndSelection(document.activeElement);
        }, 30);
    });

    let isSyncingScroll = false;
    let lastSetScrollTop = -1;
    let lastProgrammaticScrollTime = 0;
    let scrollWorkFrame = 0;

    function handleCompressedWheel(event) {
        if (!usesCompressedScroll() || event.ctrlKey || event.deltaY === 0) return;

        event.preventDefault();
        const visualDelta = event.deltaMode === WheelEvent.DOM_DELTA_LINE
            ? event.deltaY * viewportController.lineHeight
            : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
                ? event.deltaY * Math.max(scrollContainer.clientHeight, viewportController.lineHeight)
                : event.deltaY;
        const maxScrollTop = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
        scrollContainer.scrollTop = Math.min(
            maxScrollTop,
            Math.max(0, scrollContainer.scrollTop + visualScrollDeltaToScrollTopDelta(visualDelta)));
        if (event.deltaX !== 0) {
            scrollContainer.scrollLeft = Math.max(0, scrollContainer.scrollLeft + event.deltaX);
        }
    }

    let interceptsWheel = false;
    function syncWheelScrollMode() {
        const shouldIntercept = usesCompressedScroll();
        if (interceptsWheel === shouldIntercept) return;
        interceptsWheel = shouldIntercept;
        // A non-passive wheel listener blocks native scrolling even if its
        // callback immediately returns. Only huge, compressed documents need
        // custom wheel distances; ordinary files must stay compositor-scrollable.
        if (shouldIntercept) {
            scrollContainer.addEventListener('wheel', handleCompressedWheel, { passive: false });
        } else {
            scrollContainer.removeEventListener('wheel', handleCompressedWheel);
        }
    }
    configureEditorCoreRuntime({ syncWheelScrollMode });
    syncWheelScrollMode();

    scrollContainer.addEventListener('scroll', () => {
        const maximumScrollTop = maximumVirtualScrollTop();
        if (scrollContainer.scrollTop > maximumScrollTop + 0.5) {
            scrollContainer.scrollTop = maximumScrollTop;
            return;
        }

        hideContextMenu();
        if (viewportController.hasPreservedScrollTop &&
            Math.abs(scrollContainer.scrollTop - viewportController.preservedScrollTop) > 1) {
            clearPreservedScrollTop();
        }
        if (scrollWorkFrame) return;

        scrollWorkFrame = requestAnimationFrame(() => {
            scrollWorkFrame = 0;
            syncCsvHeaderScroll();
            // All rows of small documents are already loaded and in normal flow.
            // Scrolling them needs neither a cache scan nor another render frame.
            if (!usesFullDocumentRender()) {
                prefetchAround(scrollContainer.scrollTop);
                queueRender();
            }

            if (lastSetScrollTop !== -1 && Math.abs(scrollContainer.scrollTop - lastSetScrollTop) <= 1) {
                return;
            }
            lastSetScrollTop = -1;

            if (Date.now() - lastProgrammaticScrollTime < 100) {
                return;
            }

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
    });

    window.addEventListener('resize', () => {
        clearMeasuredLineHeights();
        setupVirtualHeight();
        queueRender(true);
    });
    window.addEventListener('dragstart', event => event.preventDefault(), false);
    window.addEventListener('dragover', event => event.preventDefault(), false);
    window.addEventListener('drop', event => event.preventDefault(), false);

    findReplaceController.bind();
    function beginProgrammaticScroll(targetScrollTop) {
        isSyncingScroll = true;
        lastProgrammaticScrollTime = Date.now();
        scrollContainer.scrollTop = targetScrollTop;
        lastSetScrollTop = scrollContainer.scrollTop;

        requestAnimationFrame(() => {
            isSyncingScroll = false;
        });
    }

    function syncHostScroll(firstLine, offset = 0) {
        if (!state.scrollSyncEnabled || !firstLine) {
            return;
        }

        const targetScrollTop = lineTop(firstLine) + (offset || 0);
        if (Math.abs(scrollContainer.scrollTop - targetScrollTop) <= 0.5) {
            lastSetScrollTop = -1;
            return;
        }

        beginProgrammaticScroll(targetScrollTop);
    }

    return {
        suppressNativePaste: clipboardEvents.suppressNativePaste,
        beginProgrammaticScroll,
        handleOpenableHoverResult: pointerEvents.handleOpenableHoverResult,
        syncHostScroll
    };
}
