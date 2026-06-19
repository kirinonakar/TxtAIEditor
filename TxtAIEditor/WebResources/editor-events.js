import {
    contextMenu,
    scrollContainer
} from './editor-dom.js';
import {
    lineAt,
    lineTop,
    post,
    prefetchAround,
    queueRender,
    reportCursorAndSelection,
    state
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

export function bindEditorEvents({
    findReplaceController,
    openFindPanel,
    getPreciseLivePreviewPosition,
    renderer
}) {
    bindTextInputEvents({ renderer });
    bindPointerSelectionEvents({ getPreciseLivePreviewPosition, renderer });
    bindKeyboardEvents({ openFindPanel });
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
        if (state.isSelecting) return;
        clearTimeout(nativeSelectionReportTimer);
        nativeSelectionReportTimer = setTimeout(() => {
            reportCursorAndSelection(document.activeElement);
        }, 30);
    });

    let isSyncingScroll = false;
    let lastSetScrollTop = -1;
    let lastProgrammaticScrollTime = 0;
    scrollContainer.addEventListener('scroll', () => {
        hideContextMenu();
        syncCsvHeaderScroll();
        prefetchAround(scrollContainer.scrollTop);
        queueRender();
        
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

    window.addEventListener('resize', () => queueRender(true));
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
        syncHostScroll
    };
}