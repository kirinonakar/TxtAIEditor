import { findInput, scrollContainer } from './editor-dom.js';
import {
    applyOptions,
    applyEditResultFromHost,
    captureScrollAnchor,
    clearMeasuredLineHeights,
    cleanDirtyMarker,
    csvTableMode,
    hostRequestController,
    imeController,
    markDirty,
    post,
    queueRender,
    receiveLineBlock,
    restoreScrollAnchor,
    searchController,
    selectionController,
    selectionInfo,
    setupModel,
    setupVirtualHeight,
    state,
    syncCustomSelectionClass,
    updateLineFromHost,
    viewportController
} from './editor-core.js';
import {
    applyMarkdownCommand,
    beginHostStreamInsert,
    copySelectionToClipboard,
    endHostStreamInsert,
    flushPendingEditForSave,
    focusLine,
    insertHostStreamText,
    insertTextAtCaret,
    pasteFromClipboard,
    runPendingLineActions
} from './editor-commands.js';
import { autocompleteState, hideAutocomplete, triggerAutocomplete } from './editor-autocomplete.js';
import {
    isJsonCsvTableMode,
    selectedCsvText,
    setCsvTableMode,
    updateCsvLocalization
} from './editor-csv-table.js';
import { isFullDocumentLivePreviewLanguage } from './editor-html-live-preview.js';

const EDITOR_PROTOCOL_VERSION = 1;
const VERSIONED_HOST_ACTIONS = new Set([
    'initModel',
    'setText',
    'updateLine',
    'applyEditResult',
    'applyLineReplacements'
]);

function syncLanguageClass() {
    document.body.classList.toggle('hex-view-mode', state.language === 'hex');
}

function syncRenderedDirtyLineClasses() {
    if (!state.showDirtyLines) {
        return;
    }

    for (const row of document.querySelectorAll('.line-row[data-line]')) {
        row.classList.remove('dirty-mod', 'dirty-add', 'dirty-del');
        const dirtyType = state.dirtyLines.get(Number(row.dataset.line || 0));
        if (dirtyType === 'mod' || dirtyType === 'add' || dirtyType === 'del') {
            row.classList.add(`dirty-${dirtyType}`);
        }
    }
}

let pendingModelResynchronization = null;

function isImeCompositionActive() {
    return imeController.isCompositionActive;
}

function applyModelInitialization(msg) {
    state.initialized = true;
    state.hostDocumentId = String(msg.documentId || '');
    state.hostDocumentVersion = Math.max(0, Number(msg.documentVersion || 0));
    state.viewId = String(msg.viewId || '');
    state.messageSequence = 0;
    state.pendingLinePatchBatch = null;
    state.language = msg.language || 'plaintext';
    syncLanguageClass();
    state.isSplitView = !!msg.isSplitView;
    if (msg.inlineLivePreviewEnabled !== undefined && msg.inlineLivePreviewEnabled !== null) {
        state.inlineLivePreviewEnabled = !!msg.inlineLivePreviewEnabled;
        state.livePreviewBaseHref = msg.livePreviewBaseHref || '';
        state.inlineLivePreviewSourceLine = state.inlineLivePreviewEnabled &&
            !isFullDocumentLivePreviewLanguage(state.language)
            ? Math.min(
                Math.max(1, Number(msg.lineCount || 1)),
                Math.max(1, Number(state.currentLine || 1)))
            : null;
        state.inlineLivePreviewEditableBlock = null;
        document.body.classList.toggle('inline-live-preview-enabled', state.inlineLivePreviewEnabled);
    }
    state.livePreviewLocalResourceVersion = String(Date.now());
    applyOptions(msg);
    updateCsvLocalization(msg);
    setupModel(msg.lineCount || 1);
    const initialLines = Array.isArray(msg.initialLines) ? msg.initialLines : [];
    if (receiveLineBlock(msg.initialStartLine || 1, initialLines) > 0) {
        queueRender(true);
    }
    document.getElementById('loading-overlay')?.classList.add('hidden');
    requestAnimationFrame(() => {
        requestAnimationFrame(() => post({ type: 'initialRenderComplete' }));
    });
}

document.addEventListener('compositionend', () => {
    if (!pendingModelResynchronization) {
        return;
    }

    queueMicrotask(() => {
        if (isImeCompositionActive() || !pendingModelResynchronization) {
            return;
        }

        const pending = pendingModelResynchronization;
        pendingModelResynchronization = null;
        const pendingDirtyLines = new Map(state.dirtyLines);
        applyModelInitialization(pending);
        if (state.showDirtyLines && pendingDirtyLines.size > 0) {
            state.dirtyLines = pendingDirtyLines;
            syncRenderedDirtyLineClasses();
        }
    });
});

function canApplyVersionedDocumentChange(msg) {
    if (!msg.documentId || msg.documentVersion === undefined || msg.documentVersion === null) {
        return true;
    }
    if (state.hostDocumentId && state.hostDocumentId !== String(msg.documentId)) {
        return false;
    }

    const version = Number(msg.documentVersion);
    const baseVersion = Number(msg.baseVersion);
    if (!Number.isFinite(version) || version <= state.hostDocumentVersion) {
        return false;
    }
    if (!Number.isFinite(baseVersion) || baseVersion === state.hostDocumentVersion) {
        return true;
    }

    // This view already applied its own edits locally before the host model
    // assigned versions to them. Allow its undo/redo response to bridge those
    // locally-applied versions, while keeping split-view updates strictly ordered.
    const isLocalSource = msg.sourceViewId && state.viewId &&
        String(msg.sourceViewId) === String(state.viewId);
    return isLocalSource && baseVersion > state.hostDocumentVersion;
}

function markVersionedDocumentChangeApplied(msg) {
    if (msg.documentId) {
        state.hostDocumentId = String(msg.documentId);
    }
    const version = Number(msg.documentVersion);
    if (Number.isFinite(version)) {
        state.hostDocumentVersion = version;
    }
}

function canApplyLinePatchBatch(msg) {
    const batchId = String(msg.batchId || '');
    const batchIndex = Number(msg.batchIndex);
    const version = Number(msg.documentVersion);
    if (!batchId || !Number.isInteger(batchIndex) || batchIndex < 0 || !Number.isFinite(version)) {
        return false;
    }

    if (batchIndex === 0) {
        if (!canApplyVersionedDocumentChange(msg)) {
            return false;
        }

        state.pendingLinePatchBatch = {
            batchId,
            documentId: String(msg.documentId || ''),
            documentVersion: version,
            nextIndex: 0
        };
    }

    const pending = state.pendingLinePatchBatch;
    return !!pending &&
        pending.batchId === batchId &&
        pending.documentId === String(msg.documentId || '') &&
        pending.documentVersion === version &&
        pending.nextIndex === batchIndex;
}

export function createHostMessageHandler({
    revealLine,
    revealHexOffset,
    openFindPanel,
    suppressNativePaste,
    syncHostScroll,
    findEditablePreviewBlockContaining,
    clearPendingInlineLivePreviewFocus,
    ensureInlinePreviewDependencies,
    handleOpenableHoverResult
}) {
    return function handleCsharpMessage(msg) {
    if (!msg ||
        (msg.protocolVersion !== undefined && Number(msg.protocolVersion) !== EDITOR_PROTOCOL_VERSION) ||
        (VERSIONED_HOST_ACTIONS.has(msg.action) && Number(msg.protocolVersion) !== EDITOR_PROTOCOL_VERSION)) {
        return;
    }
    switch (msg.action) {
        case 'initModel':
            if (msg.isResynchronization === true && isImeCompositionActive()) {
                pendingModelResynchronization = msg;
                break;
            }
            pendingModelResynchronization = null;
            applyModelInitialization(msg);
            if (state.inlineLivePreviewEnabled && !isFullDocumentLivePreviewLanguage(state.language)) {
                void ensureInlinePreviewDependencies?.();
            }
            break;
        case 'updateDirtyLines':
            {
                if (!state.showDirtyLines) {
                    break;
                }
                const markers = new Map();
                if (msg.dirtyLines) {
                    for (const [key, value] of Object.entries(msg.dirtyLines)) {
                        markers.set(Number(key), value);
                    }
                }
                state.dirtyLines = markers;
                // Dirty reconciliation can arrive between two Korean IME syllables.
                // Rebuilding viewport.innerHTML here disconnects the focused
                // contenteditable, so update only the marker classes of rows that
                // are already rendered. Future normal renders use state.dirtyLines.
                syncRenderedDirtyLineClasses();
            }
            break;
        case 'setText':
            {
                if (imeController.isComposing) {
                    break;
                }
                const incomingVersion = Number(msg.documentVersion);
                if (msg.documentId && state.hostDocumentId &&
                    state.hostDocumentId !== String(msg.documentId)) {
                    break;
                }
                if (Number.isFinite(incomingVersion) && incomingVersion < state.hostDocumentVersion) {
                    break;
                }
                const text = msg.text || '';
                const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
                selectionController.clear();
                try {
                    window.getSelection()?.removeAllRanges();
                } catch (e) { }
                syncCustomSelectionClass();
                const targetLine = Math.min(state.currentLine, lines.length);
                const targetCol = Math.min(Math.max(0, state.currentColumn - 1), (lines[targetLine - 1] || '').length);
                setupModel(Math.max(1, lines.length));
                lines.forEach((line, index) => state.cache.set(index + 1, line));
                markVersionedDocumentChangeApplied(msg);
                state.livePreviewLocalResourceVersion = String(Date.now());
                queueRender(true);
                if (msg.shouldFocus !== false) {
                    setTimeout(() => {
                        if (!imeController.isComposing && !imeController.textareaBypassActive) {
                            focusLine(targetLine, targetCol);
                        }
                    }, 20);
                }
            }
            break;
        case 'editAccepted':
            {
                const version = Number(msg.newVersion);
                if (Number.isFinite(version)) {
                    state.hostDocumentVersion = Math.max(state.hostDocumentVersion, version);
                }
            }
            break;
        case 'editRejected':
            {
                const version = Number(msg.currentVersion);
                if (Number.isFinite(version)) {
                    state.hostDocumentVersion = Math.max(0, version);
                }
            }
            break;
        case 'updateLine':
            {
                if (!canApplyVersionedDocumentChange(msg)) break;
                if (updateLineFromHost(msg.lineNumber || 1, msg.text || '', !!msg.isComposing)) {
                    markVersionedDocumentChangeApplied(msg);
                }
            }
            break;
        case 'applyEditResult':
            {
                if (!canApplyVersionedDocumentChange(msg)) break;
                hideAutocomplete(700);
                if (applyEditResultFromHost(
                    msg.startLine || 1,
                    msg.oldLineCount || 0,
                    msg.lines || [],
                    msg.lineCount || state.lineCount,
                    msg.caret || null)) {
                    markVersionedDocumentChangeApplied(msg);
                }
            }
            break;
        case 'applyLineReplacements':
            {
                if (imeController.isComposing || !canApplyLinePatchBatch(msg)) break;
                const replacements = Array.isArray(msg.replacements) ? msg.replacements : [];
                for (const replacement of replacements) {
                    const lineNumber = Math.max(1, Number(replacement?.lineNumber || 1));
                    if (lineNumber <= state.lineCount) {
                        state.cache.set(lineNumber, String(replacement?.text ?? ''));
                        if (!cleanDirtyMarker(lineNumber)) {
                            markDirty(lineNumber, 'mod');
                        }
                    }
                }
                state.pendingLinePatchBatch.nextIndex++;
                if (msg.isFinal === true) {
                    state.cacheVersion++;
                    state.documentVersion++;
                    searchController.invalidateDocument();
                    clearMeasuredLineHeights();
                    markVersionedDocumentChangeApplied(msg);
                    state.pendingLinePatchBatch = null;
                    state.livePreviewLocalResourceVersion = String(Date.now());
                    queueRender(true);
                }
            }
            break;
        case 'receiveLines':
            {
                const receivedStart = Math.max(1, Number(msg.startLine || 1));
                const receivedCount = receiveLineBlock(receivedStart, msg.lines || []);
                const receivedEnd = receivedStart + receivedCount - 1;
                const touchesRenderedRange = receivedCount > 0 &&
                    viewportController.overlapsRenderedRange(receivedStart, receivedEnd);
                runPendingLineActions();
                if (!imeController.isComposing && (touchesRenderedRange || isJsonCsvTableMode())) {
                    queueRender(true);
                }
            }
            break;
        case 'lineCountChanged':
            {
                const savedScroll = scrollContainer.scrollTop;
                state.lineCount = Math.max(1, Number(msg.lineCount || 1));
                state.cacheVersion++;
                setupVirtualHeight();
                const maxScroll = Math.max(0, scrollContainer.scrollHeight - scrollContainer.clientHeight);
                scrollContainer.scrollTop = Math.min(savedScroll, maxScroll);
                queueRender(true);
            }
            break;
        case 'setLanguage':
            {
                const nextLanguage = msg.language || 'plaintext';
                if (state.language !== nextLanguage) {
                    state.language = nextLanguage;
                    syncLanguageClass();
                    state.cache.clearDerivedContexts();
                    if (state.inlineLivePreviewEnabled && !isFullDocumentLivePreviewLanguage(state.language)) {
                        void ensureInlinePreviewDependencies?.();
                    }
                    queueRender(true);
                }
            }
            break;
        case 'updateOptions':
            applyOptions(msg);
            updateCsvLocalization(msg);
            break;
        case 'setSplitView':
            state.isSplitView = !!msg.enabled;
            break;
        case 'setTextOperationLock':
            {
                const locked = !!msg.locked;
                if (locked && !state.textOperationLocked) {
                    state.textOperationPreviousReadOnly = state.readOnly;
                    state.textOperationLocked = true;
                    state.readOnly = true;
                } else if (!locked && state.textOperationLocked) {
                    state.readOnly = state.textOperationPreviousReadOnly;
                    state.textOperationLocked = false;
                }

                for (const id of ['find-input', 'replace-input', 'find-match-case', 'find-regex',
                    'find-prev', 'find-next', 'replace-btn', 'replace-all-btn']) {
                    const element = document.getElementById(id);
                    if (element) element.disabled = locked;
                }
                if (!imeController.isComposing) {
                    queueRender(true);
                }
            }
            break;
        case 'setCsvTableMode':
            setCsvTableMode(!!msg.enabled, msg);
            break;
        case 'updateSnippets':
            state.snippets = Array.isArray(msg.snippets) ? msg.snippets : [];
            state.autocompleteWords = Array.isArray(msg.autocompleteWords) ? msg.autocompleteWords : [];
            if (autocompleteState.isOpen) {
                const element = autocompleteState.element;
                if (element) triggerAutocomplete(element);
            }
            break;
        case 'triggerFind':
            openFindPanel();
            break;
        case 'getSelection':
            if (csvTableMode.isEnabled) {
                post({ type: 'selectionResult', text: selectedCsvText(), startLine: 0, endLine: 0 });
            } else {
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
            break;
        case 'copySelection':
            void copySelectionToClipboard();
            break;
        case 'pasteFromClipboard':
            hideAutocomplete(500);
            void pasteFromClipboard();
            break;
        case 'flushForSave':
            hideAutocomplete();
            flushPendingEditForSave(msg.requestId || 0);
            break;
        case 'insertText':
            suppressNativePaste();
            insertTextAtCaret(msg.text || '', { preferStateCaret: true });
            break;
        case 'beginStreamInsert':
            hideAutocomplete();
            beginHostStreamInsert();
            break;
        case 'insertStreamText':
            suppressNativePaste();
            insertHostStreamText(msg.text || '');
            break;
        case 'endStreamInsert':
            endHostStreamInsert();
            break;
        case 'markdownCommand':
            applyMarkdownCommand(msg.command, msg.color);
            break;
        case 'revealLine':
            revealLine(msg.lineNumber || 1, msg.indexOfMatch || 0, msg.matchLength || 0, msg.query || '');
            break;
        case 'revealHexOffset':
            if (typeof revealHexOffset === 'function') {
                revealHexOffset(msg.offset || 0);
            }
            break;
        case 'findAllResult':
            {
                const resultQuery = msg.query || '';
                if (findInput.value !== resultQuery) {
                    if (searchController.pendingNavigation?.query === resultQuery) {
                        searchController.clearPendingNavigation();
                    }
                    break;
                }

                searchController.applyResults({
                    query: resultQuery,
                    matches: msg.matches,
                    documentVersion: state.documentVersion
                });

                const pendingNavigation = searchController.pendingNavigation;
                const usePendingNavigation = pendingNavigation && pendingNavigation.query === searchController.query;
                if (pendingNavigation && !usePendingNavigation) {
                    searchController.clearPendingNavigation();
                }

                const activeMatch = usePendingNavigation
                    ? searchController.selectFromPosition(
                        pendingNavigation.line,
                        pendingNavigation.column,
                        pendingNavigation.reverse)
                    : searchController.selectFromPosition(
                        state.currentLine,
                        state.currentColumn,
                        false);
                if (activeMatch) {
                    revealLine(
                        activeMatch.lineNumber,
                        activeMatch.indexOfMatch,
                        activeMatch.matchLength,
                        searchController.query,
                        true);
                }
                if (usePendingNavigation) {
                    searchController.clearPendingNavigation();
                }
                queueRender(true);
                break;
            }
        case 'findResult':
            if (msg.found) {
                revealLine(msg.lineNumber, msg.indexOfMatch || 0, msg.matchLength || 0, msg.query || findInput.value, true);
            }
            break;
        case 'focus':
            focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
            break;
        case 'clipboardReadResult':
            hostRequestController.completeClipboardRequest(msg.requestId, msg.text);
            break;
        case 'openableHoverResult':
            if (typeof handleOpenableHoverResult === 'function') {
                handleOpenableHoverResult(msg.requestId || 0, !!msg.isOpenable);
            }
            break;
        case 'scrollSyncChanged':
            state.scrollSyncEnabled = !!msg.enabled;
            break;
        case 'setInlineLivePreview':
            const livePreviewScrollAnchor = captureScrollAnchor();
            state.inlineLivePreviewEnabled = !!msg.enabled;
            state.livePreviewBaseHref = msg.baseHref || '';
            clearPendingInlineLivePreviewFocus();
            if (state.inlineLivePreviewEnabled && !isFullDocumentLivePreviewLanguage(state.language)) {
                void ensureInlinePreviewDependencies?.();
            }
            if (state.inlineLivePreviewEnabled && !isFullDocumentLivePreviewLanguage(state.language)) {
                const activeEl = document.activeElement?.closest?.('.line-text');
                const activeLine = activeEl ? Number(activeEl.dataset.line) : state.currentLine;
                if (activeLine) {
                    state.inlineLivePreviewSourceLine = activeLine;
                    state.inlineLivePreviewEditableBlock = findEditablePreviewBlockContaining(
                        activeLine,
                        state.lineCount,
                        line => state.cache.get(line),
                        { tabSize: state.tabSize || 4 }
                    );
                } else {
                    state.inlineLivePreviewSourceLine = null;
                    state.inlineLivePreviewEditableBlock = null;
                }
            } else {
                state.inlineLivePreviewSourceLine = null;
                state.inlineLivePreviewEditableBlock = null;
            }
            clearMeasuredLineHeights();
            document.body.classList.toggle('inline-live-preview-enabled', state.inlineLivePreviewEnabled);
            setupVirtualHeight();
            restoreScrollAnchor(livePreviewScrollAnchor);
            queueRender(true);
            requestAnimationFrame(() => restoreScrollAnchor(livePreviewScrollAnchor));
            break;
        case 'syncScroll':
            syncHostScroll(msg.firstLine, msg.offset || 0);
            break;
    }
};
}
