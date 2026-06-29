import { findInput, scrollContainer } from './editor-dom.js';
import {
    applyOptions,
    applyEditResultFromHost,
    clearMeasuredLineHeights,
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
export function createHostMessageHandler({
    revealLine,
    openFindPanel,
    suppressNativePaste,
    syncHostScroll,
    findEditablePreviewBlockContaining,
    clearPendingInlineLivePreviewFocus
}) {
    return function handleCsharpMessage(msg) {
    switch (msg.action) {
        case 'initModel':
            state.initialized = true;
            state.language = msg.language || 'plaintext';
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
                state.livePreviewLocalResourceVersion = String(Date.now());
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
        case 'applyEditResult':
            {
                applyEditResultFromHost(
                    msg.startLine || 1,
                    msg.oldLineCount || 0,
                    msg.lines || [],
                    msg.lineCount || state.lineCount,
                    msg.caret || null);
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
                post({ type: 'selectionResult', text: selInfo.text, startLine: selInfo.startLine, endLine: selInfo.endLine });
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
            if (state.inlineLivePreviewEnabled) {
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
