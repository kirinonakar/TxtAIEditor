import {
    findClear,
    findClose,
    findInput,
    findNextButton,
    findPanel,
    findPrev,
    replaceAllBtn,
    replaceBtn,
    replaceClear,
    replaceInput
} from './editor-dom.js';
import {
    cleanDirtyMarker,
    markDirty,
    post,
    queueRender,
    searchController,
    selectedText,
    state
} from './editor-core.js';
import { focusLine } from './editor-commands.js';
import {
    isFullHtmlLivePreviewActive,
    postHtmlLivePreviewFind,
    resetHtmlLivePreviewFind
} from './editor-html-live-preview.js';

export function createFindReplaceController({ revealLine }) {
    let findDebounceTimer = 0;

    // Find & Replace panel operations
function openFindPanel() {
    findPanel.hidden = false;
    const replaceRow = document.getElementById('replace-row');
    if (replaceRow) {
        replaceRow.style.display = state.readOnly ? 'none' : 'flex';
    }
    const replaceActionsRow = document.getElementById('replace-actions-row');
    if (replaceActionsRow) {
        replaceActionsRow.style.display = state.readOnly ? 'none' : 'flex';
    }
    const selected = selectedText();
    if (selected && !/[\r\n]/.test(selected)) {
        findInput.value = selected;
    }
    findInput.focus();
    findInput.select();
    requestFindAll();
}

function executeReplace() {
    if (state.readOnly || !searchController.activeMatch) return;

    const replaceText = replaceInput.value || '';
    const { lineNumber, indexOfMatch, matchLength, query } = searchController.activeMatch;
    const originalText = state.cache.get(lineNumber);
    if (originalText === undefined) return;

    if (indexOfMatch + matchLength > originalText.length) return;

    let nextText = originalText;
    if (searchController.regex) {
        try {
            const regex = new RegExp(query, searchController.matchCase ? 'g' : 'gi');
            let replaced = false;
            nextText = originalText.replace(regex, (m, ...args) => {
                const offset = args[args.length - 2];
                if (offset === indexOfMatch && !replaced) {
                    replaced = true;
                    const cleanQuery = query.replace(/^\^/, '').replace(/\$$/, '');
                    const cleanRegex = new RegExp(cleanQuery, searchController.matchCase ? '' : 'i');
                    return m.replace(cleanRegex, replaceText);
                }
                return m;
            });
        } catch (e) {
            nextText = originalText.slice(0, indexOfMatch) + replaceText + originalText.slice(indexOfMatch + matchLength);
        }
    } else {
        nextText = originalText.slice(0, indexOfMatch) + replaceText + originalText.slice(indexOfMatch + matchLength);
    }

    state.cache.set(lineNumber, nextText);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    post({ type: 'lineChanged', lineNumber: lineNumber, text: nextText });
    post({ type: 'contentChanged' });
    queueRender(true);

    const currentQuery = findInput.value;
    if (currentQuery) {
        post({ type: 'findAll', query: currentQuery, matchCase: searchController.matchCase, isRegex: searchController.regex });
    }
}

function executeReplaceAll() {
    if (state.readOnly || searchController.matches.length === 0) return;

    const query = findInput.value;
    if (!query) return;

    clearTimeout(findDebounceTimer);
    const replaceText = replaceInput.value || '';
    post({
        type: 'replaceAll',
        query: query,
        replace: replaceText,
        matchCase: searchController.matchCase,
        isRegex: searchController.regex
    });
}

function closeFindPanel() {
    findPanel.hidden = true;
    searchController.clearResults();
    resetHtmlLivePreviewFind();
    queueRender(true);
    focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
}

function requestFindAll() {
    const query = findInput.value;
    if (!query) {
        searchController.clearResults({
            documentVersion: state.documentVersion,
            clearPendingNavigation: true
        });
        resetHtmlLivePreviewFind();
        queueRender(true);
        return;
    }
    searchController.query = query;
    post({
        type: 'findAll',
        query,
        matchCase: searchController.matchCase,
        isRegex: searchController.regex,
        currentLine: state.currentLine || 1
    });

    // The HTML live preview paints matches inside its sandboxed iframe, so the
    // query has to be forwarded there as well.
    if (isFullHtmlLivePreviewActive()) {
        postHtmlLivePreviewFind({
            query,
            matchCase: searchController.matchCase,
            isRegex: searchController.regex,
            direction: 0
        });
    }
}

function requestFind(reverse = false) {
    const query = findInput.value;
    if (!query) return;

    if (isFullHtmlLivePreviewActive()) {
        // Navigation must follow the matches painted in the preview frame, not
        // the source-text matches of the hidden editor viewport.
        postHtmlLivePreviewFind({
            query,
            matchCase: searchController.matchCase,
            isRegex: searchController.regex,
            direction: reverse ? -1 : 1
        });
        return;
    }

    if (!searchController.hasFreshResults(query, state.documentVersion)) {
        searchController.queueNavigation({
            query,
            reverse,
            line: state.currentLine,
            column: state.currentColumn
        });
        requestFindAll();
        return;
    }

    const activeMatch = searchController.selectNext(
        state.currentLine,
        state.currentColumn,
        reverse);
    if (!activeMatch) return;

    revealLine(
        activeMatch.lineNumber,
        activeMatch.indexOfMatch,
        activeMatch.matchLength,
        query,
        true);
}

function clearFindPanelInput(input, shouldRefreshSearch = false) {
    if (!input.value) {
        input.focus();
        return;
    }

    input.value = '';
    input.focus();
    if (shouldRefreshSearch) {
        clearTimeout(findDebounceTimer);
        requestFindAll();
    }
}

    function bind() {
        findInput.addEventListener('input', () => {
            clearTimeout(findDebounceTimer);
            findDebounceTimer = setTimeout(() => requestFindAll(), 200);
        });

        findInput.addEventListener('keydown', event => {
            if (event.key === 'Enter') {
                event.preventDefault();
                requestFind(event.shiftKey);
            } else if (event.key === 'Escape') {
                event.preventDefault();
                closeFindPanel();
            }
        });

        findPrev.addEventListener('click', () => requestFind(true));
        findNextButton.addEventListener('click', () => requestFind(false));
        findClose.addEventListener('click', closeFindPanel);
        findClear.addEventListener('click', () => clearFindPanelInput(findInput, true));

        const findMatchCase = document.getElementById('find-match-case');
        const findRegex = document.getElementById('find-regex');

        findMatchCase.addEventListener('click', () => {
            findMatchCase.classList.toggle('active', searchController.toggleMatchCase());
            requestFindAll();
        });

        findRegex.addEventListener('click', () => {
            findRegex.classList.toggle('active', searchController.toggleRegex());
            requestFindAll();
        });

        replaceBtn.addEventListener('click', () => executeReplace());
        replaceAllBtn.addEventListener('click', () => executeReplaceAll());
        replaceClear.addEventListener('click', () => clearFindPanelInput(replaceInput));
        replaceInput.addEventListener('keydown', event => {
            if (event.key === 'Enter') {
                event.preventDefault();
                executeReplace();
            } else if (event.key === 'Escape') {
                event.preventDefault();
                closeFindPanel();
            }
        });
    }

    return {
        bind,
        clearFindPanelInput,
        closeFindPanel,
        executeReplace,
        executeReplaceAll,
        openFindPanel,
        requestFind,
        requestFindAll
    };
}
