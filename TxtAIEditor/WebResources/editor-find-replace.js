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
    selectedText,
    state
} from './editor-core.js';
import { focusLine } from './editor-commands.js';

function findSearchMatchIndexFromCurrentPosition(matches, reverse) {
    const currentLine = Math.max(1, Number(state.currentLine || 1));
    const currentColumn = Math.max(1, Number(state.currentColumn || 1));

    if (reverse) {
        for (let i = matches.length - 1; i >= 0; i--) {
            const matchLine = Number(matches[i].lineNumber || 1);
            const matchColumn = Number(matches[i].indexOfMatch || 0) + 1;
            if (matchLine < currentLine || (matchLine === currentLine && matchColumn < currentColumn)) {
                return i;
            }
        }
        return matches.length - 1;
    }

    for (let i = 0; i < matches.length; i++) {
        const matchLine = Number(matches[i].lineNumber || 1);
        const matchColumn = Number(matches[i].indexOfMatch || 0) + 1;
        if (matchLine > currentLine || (matchLine === currentLine && matchColumn >= currentColumn)) {
            return i;
        }
    }
    return 0;
}

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
    if (state.readOnly || !state.activeSearch) return;

    const replaceText = replaceInput.value || '';
    const { lineNumber, indexOfMatch, matchLength, query } = state.activeSearch;
    const originalText = state.cache.get(lineNumber);
    if (originalText === undefined) return;

    if (indexOfMatch + matchLength > originalText.length) return;

    let nextText = originalText;
    if (state.findRegex) {
        try {
            const regex = new RegExp(query, state.findMatchCase ? 'g' : 'gi');
            let replaced = false;
            nextText = originalText.replace(regex, (m, ...args) => {
                const offset = args[args.length - 2];
                if (offset === indexOfMatch && !replaced) {
                    replaced = true;
                    const cleanQuery = query.replace(/^\^/, '').replace(/\$$/, '');
                    const cleanRegex = new RegExp(cleanQuery, state.findMatchCase ? '' : 'i');
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

    const currentQuery = findInput.value;
    if (currentQuery) {
        post({ type: 'findAll', query: currentQuery, matchCase: state.findMatchCase, isRegex: state.findRegex });
    } else {
        queueRender(true);
    }
}

function executeReplaceAll() {
    if (state.readOnly || state.searchMatches.length === 0) return;

    const query = findInput.value;
    if (!query) return;

    const replaceText = replaceInput.value || '';
    post({
        type: 'replaceAll',
        query: query,
        replace: replaceText,
        matchCase: state.findMatchCase,
        isRegex: state.findRegex
    });
}

function closeFindPanel() {
    findPanel.hidden = true;
    state.searchQuery = '';
    state.searchMatches = [];
    state.searchMatchesByLine = new Map();
    state.searchIndex = -1;
    state.activeSearch = null;
    queueRender(true);
    focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
}

function requestFindAll() {
    const query = findInput.value;
    if (!query) {
        state.searchQuery = '';
        state.searchMatches = [];
        state.searchMatchesByLine = new Map();
        state.searchIndex = -1;
        state.activeSearch = null;
        state.searchDocumentVersion = state.documentVersion;
        state.pendingSearchNavigation = null;
        queueRender(true);
        return;
    }
    state.searchQuery = query;
    post({
        type: 'findAll',
        query,
        matchCase: state.findMatchCase,
        isRegex: state.findRegex,
        currentLine: state.currentLine || 1
    });
}

function requestFind(reverse = false) {
    const query = findInput.value;
    if (!query) return;

    if (state.searchQuery !== query || state.searchDocumentVersion !== state.documentVersion) {
        state.pendingSearchNavigation = {
            query,
            reverse,
            line: state.currentLine,
            column: state.currentColumn
        };
        requestFindAll();
        return;
    }

    if (state.searchMatches.length === 0) return;

    if (state.searchIndex < 0) {
        state.searchIndex = findSearchMatchIndexFromCurrentPosition(state.searchMatches, reverse);
    } else if (reverse) {
        state.searchIndex = (state.searchIndex - 1 + state.searchMatches.length) % state.searchMatches.length;
    } else {
        state.searchIndex = (state.searchIndex + 1) % state.searchMatches.length;
    }

    const match = state.searchMatches[state.searchIndex];
    state.activeSearch = {
        lineNumber: match.lineNumber,
        indexOfMatch: match.indexOfMatch,
        matchLength: match.matchLength,
        query
    };
    revealLine(match.lineNumber, match.indexOfMatch, match.matchLength, query, true);
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
            state.findMatchCase = !state.findMatchCase;
            findMatchCase.classList.toggle('active', state.findMatchCase);
            requestFindAll();
        });

        findRegex.addEventListener('click', () => {
            state.findRegex = !state.findRegex;
            findRegex.classList.toggle('active', state.findRegex);
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
