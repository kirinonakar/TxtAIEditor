import { findInput, scrollContainer } from './editor-dom.js';
import {
    applyOptions,
    applyEditResultFromHost,
    clearMeasuredLineHeights,
    cleanDirtyMarker,
    markDirty,
    post,
    queueRender,
    receiveLineBlock,
    recomputeDirtyLines,
    selectionInfo,
    setOriginalLines,
    setupModel,
    setupVirtualHeight,
    state,
    syncCustomSelectionClass,
    updateLineFromHost
} from './editor-core.js';
import {
    applyMarkdownCommand,
    beginHostStreamInsert,
    endHostStreamInsert,
    flushPendingEditForSave,
    focusLine,
    insertHostStreamText,
    insertTextAtCaret,
    runPendingLineActions
} from './editor-commands.js';
import { autocompleteState, hideAutocomplete, triggerAutocomplete } from './editor-autocomplete.js';
import {
    selectedCsvText,
    setCsvTableMode,
    updateCsvLocalization
} from './editor-csv-table.js';

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

function findSearchMatchIndexFromPosition(matches, line, column, reverse) {
    if (!Array.isArray(matches) || matches.length === 0) {
        return -1;
    }

    const safeLine = Math.max(1, Number(line || 1));
    const safeColumn = Math.max(1, Number(column || 1));

    if (reverse) {
        for (let i = matches.length - 1; i >= 0; i--) {
            const match = matches[i];
            const matchLine = Number(match.lineNumber || 1);
            const matchColumn = Number(match.indexOfMatch || 0) + 1;
            if (matchLine < safeLine || (matchLine === safeLine && matchColumn < safeColumn)) {
                return i;
            }
        }

        return matches.length - 1;
    }

    for (let i = 0; i < matches.length; i++) {
        const match = matches[i];
        const matchLine = Number(match.lineNumber || 1);
        const matchColumn = Number(match.indexOfMatch || 0) + 1;
        if (matchLine > safeLine || (matchLine === safeLine && matchColumn >= safeColumn)) {
            return i;
        }
    }

    return 0;
}

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
                state.inlineLivePreviewSourceLine = state.inlineLivePreviewEnabled && state.language !== 'html'
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
                if (state.showDirtyLines) {
                    recomputeDirtyLines();
                    queueRender(true);
                }
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
                if (state.isComposing) {
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
                state.selection = null;
                try {
                    window.getSelection()?.removeAllRanges();
                } catch (e) { }
                syncCustomSelectionClass();
                const targetLine = Math.min(state.currentLine, lines.length);
                const targetCol = Math.min(Math.max(0, state.currentColumn - 1), (lines[targetLine - 1] || '').length);
                setupModel(Math.max(1, lines.length));
                lines.forEach((line, index) => state.cache.set(index + 1, line));
                markVersionedDocumentChangeApplied(msg);
                recomputeDirtyLines();
                state.livePreviewLocalResourceVersion = String(Date.now());
                queueRender(true);
                if (msg.shouldFocus !== false) {
                    setTimeout(() => {
                        if (!state.isComposing && !state.textareaImeBypassActive) {
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
                if (state.isComposing || !canApplyLinePatchBatch(msg)) break;
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
                    state.searchDocumentVersion = -1;
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
                    state.renderedRangeStart > 0 &&
                    receivedEnd >= state.renderedRangeStart &&
                    receivedStart <= state.renderedRangeEnd;
                runPendingLineActions();
                if (!state.isComposing && touchesRenderedRange) {
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
                    state.lineEndStacks.clear();
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
                if (!state.isComposing) {
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
            if (state.csvTableEnabled) {
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
                    if (state.pendingSearchNavigation?.query === resultQuery) {
                        state.pendingSearchNavigation = null;
                    }
                    break;
                }

                state.searchQuery = resultQuery;
                state.searchMatches = msg.matches || [];
                
                const byLine = new Map();
                for (const match of state.searchMatches) {
                    let list = byLine.get(match.lineNumber);
                    if (!list) {
                        list = [];
                        byLine.set(match.lineNumber, list);
                    }
                    list.push(match);
                }
                state.searchMatchesByLine = byLine;

                state.searchDocumentVersion = state.documentVersion;

                const pendingNavigation = state.pendingSearchNavigation;
                const usePendingNavigation = pendingNavigation && pendingNavigation.query === state.searchQuery;
                if (pendingNavigation && !usePendingNavigation) {
                    state.pendingSearchNavigation = null;
                }

                state.searchIndex = usePendingNavigation
                    ? findSearchMatchIndexFromPosition(
                        state.searchMatches,
                        pendingNavigation.line,
                        pendingNavigation.column,
                        pendingNavigation.reverse)
                    : findSearchMatchIndexFromPosition(
                        state.searchMatches,
                        state.currentLine,
                        state.currentColumn,
                        false);
                state.activeSearch = null;
                if (state.searchIndex >= 0) {
                    const match = state.searchMatches[state.searchIndex];
                    state.activeSearch = {
                        lineNumber: match.lineNumber,
                        indexOfMatch: match.indexOfMatch,
                        matchLength: match.matchLength,
                        query: state.searchQuery
                    };
                    revealLine(match.lineNumber, match.indexOfMatch, match.matchLength, state.searchQuery, true);
                }
                if (usePendingNavigation) {
                    state.pendingSearchNavigation = null;
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
        case 'openableHoverResult':
            if (typeof handleOpenableHoverResult === 'function') {
                handleOpenableHoverResult(msg.requestId || 0, !!msg.isOpenable);
            }
            break;
        case 'scrollSyncChanged':
            state.scrollSyncEnabled = !!msg.enabled;
            break;
        case 'setInlineLivePreview':
            state.inlineLivePreviewEnabled = !!msg.enabled;
            state.livePreviewBaseHref = msg.baseHref || '';
            clearPendingInlineLivePreviewFocus();
            if (state.inlineLivePreviewEnabled && state.language !== 'html') {
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
            queueRender(true);
            break;
        case 'syncScroll':
            syncHostScroll(msg.firstLine, msg.offset || 0);
            break;
    }
};
}
