import { HangulAutocomplete } from './hangul-autocomplete.js';
import {
    cleanDirtyMarker,
    escapeHtml,
    markDirty,
    post,
    queueRender,
    setupVirtualHeight,
    shiftCachedLines,
    syncCustomSelectionClass,
    state
} from './editor-core.js';
import {
    focusLine,
    getCaretOffset,
    lineTextFromElement,
    makeEditablePlainText,
    setCaret,
    updateSingleLine
} from './editor-commands.js';
import { hasCustomSelection } from './editor-selection.js';

// Auto-complete Popup State
const autocompleteState = {
    isOpen: false,
    candidates: [],
    activeIndex: 0,
    element: null,
    wordStart: 0,
    caret: 0,
    word: '',
    textBeforeCaret: '',
    suppressUntil: 0
};

let autocompleteCaretRestoreToken = 0;
const AUTOCOMPLETE_NAVIGATION_REFRESH_SUPPRESS_MS = 240;

function cancelAutocompleteCaretRestore() {
    autocompleteCaretRestoreToken++;
}

function getWordUnderCaret(text, caretOffset) {
    let start = caretOffset;
    while (start > 0 && /[\w\-ㄱ-ㅎㅏ-ㅣ가-힣]/.test(text[start - 1])) {
        start--;
    }
    let end = caretOffset;
    while (end < text.length && /[\w\-ㄱ-ㅎㅏ-ㅣ가-힣]/.test(text[end])) {
        end++;
    }
    const word = text.slice(start, caretOffset);
    const fullWord = text.slice(start, end);
    return { word, start, end, fullWord };
}

function snippetKeywordSpecialPrefix(keyword) {
    const match = keyword.match(/^([^\wㄱ-ㅎㅏ-ㅣ가-힣]+)/);
    return match ? match[1] : '';
}

function normalizeAutocompleteComparisonText(value) {
    return String(value || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').normalize('NFC').toLowerCase();
}

function autocompleteReplaceStart(candidate, text, wordStart, caret = null) {
    let replaceStart = wordStart;
    if (candidate.kind === 'snippet') {
        const specialPrefix = snippetKeywordSpecialPrefix(candidate.label || '');
        if (specialPrefix && replaceStart >= specialPrefix.length) {
            const textBefore = text.slice(replaceStart - specialPrefix.length, replaceStart);
            if (textBefore === specialPrefix) {
                replaceStart -= specialPrefix.length;
            }
        }
    }

    if (Number.isFinite(caret) && caret > replaceStart) {
        const insertText = String(candidate.insertText || candidate.label || '')
            .replace(/\r\n/g, '\n')
            .replace(/\r/g, '\n');
        const textBeforeCaret = text.slice(0, caret);
        const currentReplaceLength = caret - replaceStart;
        const maxOverlap = Math.min(textBeforeCaret.length, insertText.length);

        for (let length = maxOverlap; length > currentReplaceLength; length--) {
            const existingSuffix = textBeforeCaret.slice(textBeforeCaret.length - length);
            const insertPrefix = insertText.slice(0, length);
            if (normalizeAutocompleteComparisonText(existingSuffix) === normalizeAutocompleteComparisonText(insertPrefix)) {
                replaceStart = caret - length;
                break;
            }
        }
    }

    return replaceStart;
}

function autocompleteReplaceEnd(candidate, text, replaceStart, caret, wordEnd) {
    const safeCaret = Math.max(0, Math.min(Number(caret || 0), text.length));
    const safeWordEnd = Math.max(safeCaret, Math.min(Number(wordEnd || safeCaret), text.length));
    if (safeWordEnd <= safeCaret) return safeCaret;

    const insertText = String(candidate.insertText || candidate.label || '')
        .replace(/\r\n/g, '\n')
        .replace(/\r/g, '\n');
    const firstInsertedLine = insertText.split('\n')[0] || '';
    const currentReplaceLength = Math.max(0, Math.min(safeCaret - replaceStart, firstInsertedLine.length));
    const textAfterCaret = text.slice(safeCaret, safeWordEnd);
    const maxOverlap = Math.min(textAfterCaret.length, firstInsertedLine.length - currentReplaceLength);

    for (let length = maxOverlap; length > 0; length--) {
        const existingPrefix = textAfterCaret.slice(0, length);
        const insertSuffix = firstInsertedLine.slice(currentReplaceLength, currentReplaceLength + length);
        if (normalizeAutocompleteComparisonText(existingPrefix) === normalizeAutocompleteComparisonText(insertSuffix)) {
            return safeCaret + length;
        }
    }

    return safeCaret;
}

function isAutocompleteSameAsInput(candidate, typedText) {
    const normalizedTypedText = normalizeAutocompleteComparisonText(typedText);
    if (!normalizedTypedText) return false;

    const normalizedLabel = normalizeAutocompleteComparisonText(candidate.label);
    const normalizedInsertText = normalizeAutocompleteComparisonText(candidate.insertText);
    return normalizedLabel === normalizedTypedText || normalizedInsertText === normalizedTypedText;
}

function getAutocompleteCandidates(currentWord, fullTextBeforeCaret) {
    if (!currentWord || currentWord.length < 1) return [];
    const candidates = [];
    const seen = new Set();
    const lowerCurrent = currentWord.toLowerCase();

    const containsHangul = /[\u3130-\u318F\uAC00-\uD7A3]/.test(currentWord);
    let regex = null;
    if (typeof HangulAutocomplete !== 'undefined' && containsHangul) {
        try {
            const pattern = HangulAutocomplete.makeRegex(currentWord);
            if (pattern) {
                regex = new RegExp('^' + pattern, 'i');
            }
        } catch (e) {
            console.error("HangulAutocomplete regex compile error:", e);
        }
    }

    for (const snippet of state.snippets) {
        const keyword = String(snippet.keyword || '').trim();
        const title = String(snippet.title || '').trim();
        const content = String(snippet.content || '');
        if (!keyword || !content) continue;

        const lowerKeyword = keyword.toLowerCase();

        const specialPrefix = snippetKeywordSpecialPrefix(keyword);
        let matched = false;
        let extraPrefixLen = 0;

        if (regex) {
            if (regex.test(keyword)) {
                matched = true;
            } else if (regex.test(title)) {
                matched = true;
            } else if (specialPrefix && fullTextBeforeCaret !== undefined) {
                try {
                    const prefixPattern = HangulAutocomplete.escapeRegExp(specialPrefix) + HangulAutocomplete.makeRegex(currentWord);
                    const prefixRegex = new RegExp('^' + prefixPattern, 'i');
                    if (prefixRegex.test(keyword)) {
                        const textBefore = fullTextBeforeCaret;
                        if (textBefore.endsWith(specialPrefix + currentWord) || textBefore.slice(-specialPrefix.length) === specialPrefix) {
                            matched = true;
                            extraPrefixLen = specialPrefix.length;
                        }
                    }
                } catch (e) {
                    console.error(e);
                }
            }
        } else {
            const lowerTitle = title.toLowerCase();
            if (lowerKeyword.startsWith(lowerCurrent)) {
                matched = true;
            } else if (lowerTitle.startsWith(lowerCurrent)) {
                matched = true;
            } else if (specialPrefix && fullTextBeforeCaret !== undefined) {
                const withPrefix = specialPrefix + currentWord;
                const lowerWithPrefix = withPrefix.toLowerCase();
                if (lowerKeyword.startsWith(lowerWithPrefix)) {
                    const textBefore = fullTextBeforeCaret;
                    if (textBefore.endsWith(specialPrefix + currentWord) || textBefore.slice(-specialPrefix.length) === specialPrefix) {
                        matched = true;
                        extraPrefixLen = specialPrefix.length;
                    }
                }
            }
        }

        if (!matched) continue;

        const key = `snippet:${lowerKeyword}`;
        if (seen.has(key)) continue;
        seen.add(key);
        const prefixText = state.autocompleteSnippetPrefix || '스니펫: ';
        const defaultText = state.autocompleteSnippet || '스니펫';
        const candidate = {
            kind: 'snippet',
            label: keyword,
            insertText: content,
            detail: title ? `${prefixText}${title}` : defaultText,
            extraPrefixLen
        };
        const typedText = extraPrefixLen > 0 && fullTextBeforeCaret
            ? fullTextBeforeCaret.slice(-(extraPrefixLen + currentWord.length))
            : currentWord;
        if (!isAutocompleteSameAsInput(candidate, typedText)) {
            candidates.push(candidate);
        }
    }

    for (const dictWord of state.autocompleteWords) {
        const word = String(dictWord || '').trim();
        if (!word) continue;
        if (word.length <= currentWord.length) continue;
        if (word === currentWord) continue;

        let isWordMatched = false;
        if (regex) {
            isWordMatched = regex.test(word);
        } else {
            isWordMatched = word.toLowerCase().startsWith(lowerCurrent);
        }

        if (isWordMatched) {
            const key = `dict:${word.toLowerCase()}`;
            if (seen.has(key)) continue;
            seen.add(key);
            const candidate = {
                kind: 'word',
                label: word,
                insertText: word,
                detail: ''
            };
            if (!isAutocompleteSameAsInput(candidate, currentWord)) {
                candidates.push(candidate);
            }
        }
    }

    for (const text of state.cache.values()) {
        if (!text) continue;
        const words = text.match(/[\w\-ㄱ-ㅎㅏ-ㅣ가-힣]+/g);
        if (!words) continue;
        for (const word of words) {
            if (word.length <= currentWord.length) continue;
            if (word === currentWord) continue;

            let isWordMatched = false;
            if (regex) {
                isWordMatched = regex.test(word);
            } else {
                isWordMatched = word.toLowerCase().startsWith(lowerCurrent);
            }

            if (isWordMatched) {
                const key = `word:${word.toLowerCase()}`;
                if (seen.has(key)) continue;
                seen.add(key);
                const candidate = {
                    kind: 'word',
                    label: word,
                    insertText: word,
                    detail: ''
                };
                if (!isAutocompleteSameAsInput(candidate, currentWord)) {
                    candidates.push(candidate);
                }
            }
        }
    }

    return candidates
        .sort((a, b) => {
            if (a.kind !== b.kind) return a.kind === 'snippet' ? -1 : 1;
            return a.label.localeCompare(b.label);
        })
        .slice(0, 10);
}

function getCaretCoordinates() {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return null;
    const range = sel.getRangeAt(0).cloneRange();
    range.collapse(false);
    const rects = range.getClientRects();
    if (rects.length > 0) {
        return rects[0];
    }
    return null;
}

function triggerAutocomplete(element, inputSnapshot = null) {
    if (!state.autocompleteOnEnter && !state.autocompleteOnTab) return;
    if (isAutocompleteSuppressed()) return;
    if (hasCustomSelection()) {
        hideAutocomplete();
        return;
    }
    const lineNumber = Number(element?.dataset?.line || 0);
    const canReuseInput = inputSnapshot && inputSnapshot.lineNumber === lineNumber;
    const text = canReuseInput ? inputSnapshot.text : lineTextFromElement(element);
    const caret = canReuseInput
        ? Math.max(0, Math.min(inputSnapshot.caretOffset, text.length))
        : getCaretOffset(element);
    const { word, start, end, fullWord } = getWordUnderCaret(text, caret);

    if (!word || word.length < 1) {
        hideAutocomplete();
        return;
    }

    const textBeforeCaret = text.slice(0, caret);
    const candidates = getAutocompleteCandidates(word, textBeforeCaret)
        .filter(candidate => {
            const replaceStart = autocompleteReplaceStart(candidate, text, start, caret);
            const typedText = text.slice(replaceStart, caret);
            const fullTypedText = text.slice(replaceStart, end);
            return !isAutocompleteSameAsInput(candidate, typedText)
                && !isAutocompleteSameAsInput(candidate, fullWord)
                && !isAutocompleteSameAsInput(candidate, fullTypedText);
        });
    if (candidates.length === 0) {
        hideAutocomplete();
        return;
    }

    const preserveActiveIndex = autocompleteState.isOpen && autocompleteState.word === word;

    autocompleteState.isOpen = true;
    autocompleteState.candidates = candidates;
    autocompleteState.activeIndex = preserveActiveIndex ? Math.min(autocompleteState.activeIndex, candidates.length - 1) : 0;
    autocompleteState.element = element;
    autocompleteState.wordStart = start;
    autocompleteState.caret = caret;
    autocompleteState.word = word;
    autocompleteState.textBeforeCaret = textBeforeCaret;

    renderAutocomplete();
}

function renderAutocomplete() {
    const popup = document.getElementById('autocomplete-popup');
    if (!popup) return;
    const caretRect = getCaretCoordinates();
    if (!caretRect) {
        hideAutocomplete();
        return;
    }

    const itemsHtml = autocompleteState.candidates.map((candidate, idx) => {
        const isActive = idx === autocompleteState.activeIndex ? ' active' : '';
        const detail = candidate.detail
            ? `<span class="autocomplete-detail">${escapeHtml(candidate.detail)}</span>`
            : '';
        return `<button class="autocomplete-item${isActive}" type="button" data-index="${idx}"><span class="autocomplete-label">${escapeHtml(candidate.label)}</span>${detail}</button>`;
    }).join('');

    popup.innerHTML = itemsHtml;
    popup.hidden = false;

    const popupRect = popup.getBoundingClientRect();
    let left = caretRect.left;
    let top = caretRect.bottom + 4;

    if (left + popupRect.width > window.innerWidth) {
        left = window.innerWidth - popupRect.width - 10;
    }
    if (top + popupRect.height > window.innerHeight) {
        top = caretRect.top - popupRect.height - 4;
    }

    popup.style.left = `${Math.max(10, left)}px`;
    popup.style.top = `${Math.max(10, top)}px`;
}

function hideAutocomplete(suppressMs = 0) {
    autocompleteState.isOpen = false;
    autocompleteState.candidates = [];
    autocompleteState.activeIndex = 0;
    autocompleteState.element = null;
    autocompleteState.caret = 0;
    autocompleteState.textBeforeCaret = '';
    if (suppressMs > 0) {
        autocompleteState.suppressUntil = performance.now() + suppressMs;
    }
    const popup = document.getElementById('autocomplete-popup');
    if (popup) popup.hidden = true;
}

function isAutocompleteSuppressed() {
    return performance.now() < autocompleteState.suppressUntil;
}

function scrollAutocompleteActiveIntoView() {
    const popup = document.getElementById('autocomplete-popup');
    if (!popup) return;
    const activeItem = popup.querySelector('.autocomplete-item.active');
    if (activeItem) {
        activeItem.scrollIntoView({ block: 'nearest' });
    }
}

function moveAutocompleteActiveIndex(delta) {
    if (!autocompleteState.isOpen || autocompleteState.candidates.length === 0) return;
    autocompleteState.suppressUntil = Math.max(
        autocompleteState.suppressUntil,
        performance.now() + AUTOCOMPLETE_NAVIGATION_REFRESH_SUPPRESS_MS);
    autocompleteState.activeIndex = (
        autocompleteState.activeIndex + delta + autocompleteState.candidates.length
    ) % autocompleteState.candidates.length;
    renderAutocomplete();
    scrollAutocompleteActiveIntoView();
}

function currentAutocompleteElement() {
    const savedElement = autocompleteState.element;
    if (savedElement?.isConnected && savedElement.getAttribute('contenteditable') === 'true') {
        return savedElement;
    }

    const lineNumber = Number(savedElement?.dataset?.line || state.currentLine || 0);
    if (!lineNumber) return null;

    const currentElement = document.querySelector(`.line-text[data-line="${lineNumber}"]`);
    if (!currentElement || currentElement.getAttribute('contenteditable') !== 'true') {
        return null;
    }

    autocompleteState.element = currentElement;
    return currentElement;
}

function insertSelectedCandidate() {
    const candidate = autocompleteState.candidates[autocompleteState.activeIndex];
    const element = currentAutocompleteElement();
    if (!candidate || !element) {
        hideAutocomplete();
        return;
    }

    const text = lineTextFromElement(element);
    const currentCaret = getCaretOffset(element);
    const savedCaret = Math.max(0, Math.min(Number(autocompleteState.caret || 0), text.length));
    const savedTextBeforeCaret = autocompleteState.textBeforeCaret || '';
    const caret = text.slice(0, currentCaret) === savedTextBeforeCaret
        ? currentCaret
        : savedCaret;
    const { end: wordEnd } = getWordUnderCaret(text, caret);
    const wordStart = autocompleteReplaceStart(candidate, text, autocompleteState.wordStart, caret);
    const replaceEnd = autocompleteReplaceEnd(candidate, text, wordStart, caret, wordEnd);

    replaceWordWithAutocompleteText(element, wordStart, replaceEnd, candidate.insertText || candidate.label || '');
    hideAutocomplete();
}

function replaceWordWithAutocompleteText(element, wordStart, replaceEnd, insertText) {
    const text = lineTextFromElement(element);
    const normalized = String(insertText || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const requestedReplaceEnd = Number.isFinite(Number(replaceEnd)) ? Number(replaceEnd) : wordStart;
    const safeReplaceEnd = Math.max(wordStart, Math.min(requestedReplaceEnd, text.length));
    if (!normalized.includes('\n')) {
        const nextText = text.slice(0, wordStart) + normalized + text.slice(safeReplaceEnd);
        const nextCaret = wordStart + normalized.length;
        const lineNumber = Number(element.dataset.line || 1);
        updateSingleLine(element, nextText, nextCaret);
        // Syntax-highlight spans can retain the native selection at the end of the
        // typed prefix (for example after "ao") even though the model caret moved.
        // Keep the actively edited row as one text node so the completion end and
        // the next IME composition share the same unambiguous DOM position.
        makeEditablePlainText(element, nextCaret);
        restoreAutocompleteCaretAfterCommit(lineNumber, nextCaret, nextText);
        return;
    }

    const lineNumber = Number(element.dataset.line || 1);
    const before = text.slice(0, wordStart);
    const after = text.slice(safeReplaceEnd);
    const parts = normalized.split('\n');
    const insertedCount = parts.length - 1;
    const firstLine = before + parts[0];
    const lastLineNumber = lineNumber + insertedCount;

    state.cache.set(lineNumber, firstLine);
    shiftCachedLines(lineNumber + 1, insertedCount);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    post({ type: 'lineChanged', lineNumber, text: firstLine });
    for (let i = 1; i < parts.length; i++) {
        const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
        const nextLineNumber = lineNumber + i;
        state.cache.set(nextLineNumber, nextText);
        state.dirtyLines.set(nextLineNumber, 'add');
        post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
    }

    state.lineCount += insertedCount;
    state.selection = null;
    state.selectionAnchor = {
        line: lastLineNumber,
        column: parts[parts.length - 1]?.length || 0
    };
    state.currentLine = lastLineNumber;
    state.currentColumn = (parts[parts.length - 1]?.length || 0) + 1;
    syncCustomSelectionClass();
    setupVirtualHeight();
    post({ type: 'contentChanged' });
    queueRender(true);
    restoreAutocompleteCaretAfterCommit(
        lastLineNumber,
        parts[parts.length - 1]?.length || 0,
        state.cache.get(lastLineNumber) || '');
}

function restoreAutocompleteCaretAfterCommit(lineNumber, columnZeroBased, expectedLineText) {
    const token = ++autocompleteCaretRestoreToken;
    const targetLine = Math.max(1, Number(lineNumber || 1));
    const targetColumn = Math.max(0, Number(columnZeroBased || 0));
    const expectedText = String(expectedLineText ?? '');

    const restore = () => {
        if (token !== autocompleteCaretRestoreToken) return;
        if (state.isComposing || state.compositionLine) {
            cancelAutocompleteCaretRestore();
            return;
        }
        if ((state.cache.get(targetLine) ?? '') !== expectedText) return;

        const activeElement = document.activeElement;
        const focusedLine = activeElement?.closest?.('.line-text');
        const focusedAutocompletePopup = activeElement?.closest?.('#autocomplete-popup');
        if (!focusedLine && !focusedAutocompletePopup &&
            activeElement && activeElement !== document.body && activeElement !== document.documentElement) {
            return;
        }
        if (focusedLine && Number(focusedLine.dataset.line || 0) !== targetLine) {
            return;
        }

        state.selection = null;
        state.selectionAnchor = { line: targetLine, column: targetColumn };
        state.currentLine = targetLine;
        state.currentColumn = targetColumn + 1;
        syncCustomSelectionClass();

        const targetElement = focusedLine || document.querySelector(`.line-text[data-line="${targetLine}"]`);
        if (targetElement && targetElement.getAttribute('contenteditable') === 'true') {
            setCaret(targetElement, targetColumn);
        } else {
            focusLine(targetLine, targetColumn);
        }
    };

    restore();
    setTimeout(restore, 0);
    requestAnimationFrame(() => {
        restore();
        requestAnimationFrame(restore);
    });
    setTimeout(restore, 40);
    setTimeout(restore, 120);
}

export {
    autocompleteState,
    cancelAutocompleteCaretRestore,
    hideAutocomplete,
    insertSelectedCandidate,
    moveAutocompleteActiveIndex,
    renderAutocomplete,
    scrollAutocompleteActiveIntoView,
    triggerAutocomplete
};
