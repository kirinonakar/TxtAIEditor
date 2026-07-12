import {
    scrollContainer,
    viewport
} from './editor-dom.js';
import {
    activeEditableElement,
    clearCustomSelectionVisuals,
    isHangulImeKeyEvent,
    isPlainTextKey,
    lineTop,
    post,
    queueRender,
    reportCursorAndSelection,
    requestLines,
    state,
    syncCustomSelectionClass
} from './editor-core.js';
import {
    activeColumnSelection,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    focusImeBypassTextarea,
    cancelImeBypassTextarea,
    changeLineIndent,
    clearPendingRepeatEdit,
    commitLine,
    compositionSelectionRange,
    focusLine,
    getCaretOffset,
    copySelectionToClipboard,
    cutSelectionToClipboard,
    insertPlainTextByModel,
    isModelRepeatKey,
    lineElementFromEvent,
    markNativeBeforeInputHandled,
    moveCaretHorizontal,
    moveCaretVertical,
    normalizedModelRepeatKey,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    pasteFromClipboard,
    scheduleModelRepeatEdit,
    selectAll,
    submitHexEdit,
    splitCurrentLine
} from './editor-commands.js';
import {
    autocompleteState,
    cancelAutocompleteCaretRestore,
    hideAutocomplete,
    insertSelectedCandidate,
    moveAutocompleteActiveIndex,
    triggerAutocomplete
} from './editor-autocomplete.js';
import { cancelPostEditFocusFollowUps } from './editor-edit-focus.js';

export function bindKeyboardEvents({ openFindPanel }) {
    function hasNativeSelectionInElement(element) {
        const selection = window.getSelection();
        if (!element || !selection || selection.rangeCount === 0 || selection.isCollapsed) {
            return false;
        }

        return element.contains(selection.anchorNode) && element.contains(selection.focusNode);
    }

    function focusOrSelectHome(extendSelection) {
        const targetLine = 1;
        const targetColumn = 0;

        const element = activeEditableElement();
        const lineNumber = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;
        const caret = element ? getCaretOffset(element) : (state.currentColumn - 1);

        if (extendSelection) {
            const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
            state.selectionAnchor = anchor;
            state.selection = (anchor.line === targetLine && anchor.column === targetColumn)
                ? null
                : { start: anchor, end: { line: targetLine, column: targetColumn } };
            state.currentLine = targetLine;
            state.currentColumn = targetColumn + 1;
            syncCustomSelectionClass();
            queueRender(true);
            setTimeout(() => focusLine(targetLine, targetColumn), 0);
            reportCursorAndSelection();
        } else {
            state.selection = null;
            state.selectionAnchor = { line: targetLine, column: targetColumn };
            state.currentLine = targetLine;
            state.currentColumn = targetColumn + 1;
            syncCustomSelectionClass();
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection();
        }
    }

    function focusOrSelectEnd(extendSelection) {
        const targetLine = state.lineCount;

        function proceed() {
            const targetText = state.cache.get(targetLine) || '';
            const targetColumn = targetText.length;

            const element = activeEditableElement();
            const lineNumber = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;
            const caret = element ? getCaretOffset(element) : (state.currentColumn - 1);

            if (extendSelection) {
                const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
                state.selectionAnchor = anchor;
                state.selection = (anchor.line === targetLine && anchor.column === targetColumn)
                    ? null
                    : { start: anchor, end: { line: targetLine, column: targetColumn } };
                state.currentLine = targetLine;
                state.currentColumn = targetColumn + 1;
                syncCustomSelectionClass();
                queueRender(true);
                setTimeout(() => focusLine(targetLine, targetColumn), 0);
                reportCursorAndSelection();
            } else {
                state.selection = null;
                state.selectionAnchor = { line: targetLine, column: targetColumn };
                state.currentLine = targetLine;
                state.currentColumn = targetColumn + 1;
                syncCustomSelectionClass();
                focusLine(targetLine, targetColumn);
                reportCursorAndSelection();
            }
        }

        if (state.cache.has(targetLine)) {
            proceed();
        } else {
            const wrappedTargetTop = lineTop(targetLine);
            scrollContainer.scrollTop = Math.max(0, wrappedTargetTop - Math.floor(scrollContainer.clientHeight / 2));
            requestLines(targetLine, 1);

            let attempts = 0;
            const interval = setInterval(() => {
                attempts++;
                if (state.cache.has(targetLine) || attempts > 50) {
                    clearInterval(interval);
                    proceed();
                }
            }, 20);
        }
    }

    const KEYBOARD_VERTICAL_REPEAT_INITIAL_DELAY_MS = 140;
    const KEYBOARD_VERTICAL_REPEAT_INTERVAL_MS = 32;
    let keyboardVerticalRepeat = {
        key: '',
        direction: 0,
        extendSelection: false,
        timer: 0
    };
    let autocompleteNavigationKey = '';
    let autocompleteNavigationAt = 0;

    function isDuplicateAutocompleteNavigationKey(event) {
        if (event.repeat) return false;

        const key = event.key || '';
        const now = performance.now();
        const isDuplicate = autocompleteNavigationKey === key && now - autocompleteNavigationAt < 80;
        autocompleteNavigationKey = key;
        autocompleteNavigationAt = now;
        return isDuplicate;
    }

    function clearAutocompleteNavigationKey(key = '') {
        if (!key || key === autocompleteNavigationKey) {
            autocompleteNavigationKey = '';
            autocompleteNavigationAt = 0;
        }
    }

    function clearSelectionBeforeUndoRedo() {
        cancelImeBypassTextarea();

        const selection = normalizeSelection();
        const fallbackLine = selection ? selection.start.line : 1;
        const fallbackColumn = selection ? selection.start.column : 0;
        const caretLine = Math.min(
            state.lineCount,
            Math.max(1, Number(state.currentLine || fallbackLine)));
        const lineText = state.cache.get(caretLine) || '';
        const caretColumn = Math.max(
            0,
            Math.min(Number(state.currentColumn || fallbackColumn + 1) - 1, lineText.length));

        state.selection = null;
        state.selectionAnchor = { line: caretLine, column: caretColumn };
        state.hexSelection = null;
        state.hexSelectionAnchorOffset = null;
        state.hexCursorOffset = 0;

        try {
            window.getSelection()?.removeAllRanges();
        } catch (e) { }

        clearCustomSelectionVisuals();
        syncCustomSelectionClass();
        queueRender(true);
        setTimeout(() => {
            if (!state.isComposing && !state.textareaImeBypassActive) {
                focusLine(caretLine, caretColumn);
            }
        }, 0);
        reportCursorAndSelection();
    }

    function handleUndoRedoShortcut(event, name) {
        event.preventDefault();
        event.stopImmediatePropagation();
        hideAutocomplete(700);

        if (state.isComposing) {
            return;
        }

        clearSelectionBeforeUndoRedo();
        post({ type: 'shortcut', name });
    }

    function clearKeyboardVerticalRepeatTimer() {
        if (keyboardVerticalRepeat.timer) {
            clearTimeout(keyboardVerticalRepeat.timer);
            keyboardVerticalRepeat.timer = 0;
        }
    }

    function stopKeyboardVerticalRepeat(key = '') {
        if (key && keyboardVerticalRepeat.key && keyboardVerticalRepeat.key !== key) return;
        clearKeyboardVerticalRepeatTimer();
        keyboardVerticalRepeat = {
            key: '',
            direction: 0,
            extendSelection: false,
            timer: 0
        };
    }

    function editableElementForKeyboardVerticalRepeat() {
        const currentLineElement = viewport.querySelector(`.line-text[data-line="${state.currentLine}"]`);
        if (currentLineElement && currentLineElement.getAttribute('contenteditable') === 'true') {
            return currentLineElement;
        }

        const active = activeEditableElement();
        if (active && active.getAttribute('contenteditable') === 'true' &&
            Number(active.dataset.line || 0) === state.currentLine) {
            return active;
        }

        return null;
    }

    function scheduleKeyboardVerticalRepeat(delayMs) {
        clearKeyboardVerticalRepeatTimer();
        if (!keyboardVerticalRepeat.key) return;

        keyboardVerticalRepeat.timer = setTimeout(() => {
            keyboardVerticalRepeat.timer = 0;
            if (!keyboardVerticalRepeat.key) return;

            const repeatElement = editableElementForKeyboardVerticalRepeat();
            if (repeatElement) {
                moveCaretVertical(
                    repeatElement,
                    keyboardVerticalRepeat.direction,
                    keyboardVerticalRepeat.extendSelection);
            }

            scheduleKeyboardVerticalRepeat(KEYBOARD_VERTICAL_REPEAT_INTERVAL_MS);
        }, delayMs);
    }

    function startKeyboardVerticalRepeat(event, element, direction) {
        event.preventDefault();

        const key = event.key;
        const extendSelection = !!event.shiftKey;
        const isSameRepeat = keyboardVerticalRepeat.key === key && keyboardVerticalRepeat.direction === direction;

        keyboardVerticalRepeat.key = key;
        keyboardVerticalRepeat.direction = direction;
        keyboardVerticalRepeat.extendSelection = extendSelection;

        // OS/browser key repeat can queue many keydown events while rendering/focusing is busy.
        // Drive vertical navigation from one cancellable timer instead so release stops immediately.
        if (event.repeat && isSameRepeat) {
            return true;
        }

        clearKeyboardVerticalRepeatTimer();
        moveCaretVertical(element, direction, extendSelection);
        scheduleKeyboardVerticalRepeat(KEYBOARD_VERTICAL_REPEAT_INITIAL_DELAY_MS);
        return true;
    }

    document.addEventListener('keydown', event => {
        // A delayed caret restore from an accepted completion must never move the
        // caret after the user has started the next physical key or IME input.
        cancelAutocompleteCaretRestore();
        cancelPostEditFocusFollowUps();
        const earlyCtrl = event.ctrlKey || event.metaKey;
        const earlyKey = event.key ? event.key.toLowerCase() : '';
        if (earlyCtrl && earlyKey === 's') {
            event.preventDefault();
            hideAutocomplete(300);
            post({ type: 'shortcut', name: 'save' });
            return;
        }

        const activeTarget = event.target;
        const isNativeHexInputTarget = activeTarget && (
            activeTarget.closest?.('#find-panel') ||
            activeTarget.tagName === 'INPUT' ||
            activeTarget.tagName === 'TEXTAREA'
        );
        if (state.language === 'hex' && state.hexEditable && !isNativeHexInputTarget &&
            !earlyCtrl && !event.altKey && handleHexEditorKey(event)) {
            return;
        }

        if (event.key === 'Escape' && (state.isDragPotential || state.isDragMoving)) {
            event.preventDefault();
            state.isSelecting = false;
            cleanupDragState();
            queueRender(true);
            return;
        }

        if (autocompleteState.isOpen) {
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' ||
                event.key === 'Backspace' || event.key === 'Delete') {
                hideAutocomplete();
            }
            if ((event.key === ' ' || event.code === 'Space') && !event.ctrlKey && !event.metaKey && !event.altKey) {
                hideAutocomplete(300);
            }
            if (event.key === 'ArrowDown') {
                event.preventDefault();
                event.stopImmediatePropagation();
                if (!isDuplicateAutocompleteNavigationKey(event)) {
                    moveAutocompleteActiveIndex(1);
                }
                return;
            }
            if (event.key === 'ArrowUp') {
                event.preventDefault();
                event.stopImmediatePropagation();
                if (!isDuplicateAutocompleteNavigationKey(event)) {
                    moveAutocompleteActiveIndex(-1);
                }
                return;
            }
            if (event.key === 'Enter') {
                if (state.autocompleteOnEnter) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    insertSelectedCandidate();
                    return;
                } else {
                    hideAutocomplete(300);
                }
            }
            if (event.key === 'Tab' && state.autocompleteOnTab) {
                event.preventDefault();
                event.stopImmediatePropagation();
                insertSelectedCandidate();
                return;
            }
            // event.code는 IME 상태와 무관하게 물리 키를 반환하므로, 한글 조합 중에도 ESC 감지 가능
            // suppressMs=300: compositionend 후 triggerAutocomplete 재호출로 팝업이 다시 열리는 것 방지
            if (event.key === 'Escape' || event.code === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                hideAutocomplete(300);
                return;
            }
        }

        if (isHangulImeKeyEvent(event)) {
            const active = document.activeElement;
            const isFindOrInput = active && (
                active.closest?.('#find-panel') ||
                active.tagName === 'INPUT' ||
                active.tagName === 'TEXTAREA'
            );
            if (!isFindOrInput && !state.isComposing && !event.ctrlKey && !event.metaKey && !event.altKey) {
                const imeElement = lineElementFromEvent(event) || activeEditableElement();
                const pendingSelection = compositionSelectionRange();
                if (imeElement && pendingSelection && !pendingSelection.isColumn) {
                    if (pendingSelection.start.line !== pendingSelection.end.line) {
                        focusImeBypassTextarea();
                    } else {
                        const isCollapsed = pendingSelection.start.line === pendingSelection.end.line &&
                                            pendingSelection.start.column === pendingSelection.end.column;
                        if (!isCollapsed) {
                            const replacedElement = replaceSelectionForCompositionStart(imeElement, true) || imeElement;
                            state.compositionLine = Number(replacedElement.dataset.line || state.currentLine || 1);
                            state.editingLine = state.compositionLine;
                        } else {
                            state.compositionLine = Number(imeElement.dataset.line || state.currentLine || 1);
                            state.editingLine = state.compositionLine;
                        }
                    }
                }
            }
            return;
        }

        if (event.key === 'F4') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'f4' });
            return;
        }
        if (event.key === 'F9') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'f9' });
            return;
        }
        if (event.key === 'F10') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'f10' });
            return;
        }
        if (event.key === 'F11') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'f11' });
            return;
        }
        if (event.key === 'F12') {
            event.preventDefault();
            post({ type: 'shortcut', name: 'f12' });
            return;
        }

        const ctrl = event.ctrlKey || event.metaKey;
        const key = event.key ? event.key.toLowerCase() : '';

        if (ctrl) {
            if (key === 'c' || key === 'x' || key === 'v') {
                const target = event.target;
                const isNativeInputTarget = target && (
                    target.closest?.('#find-panel') ||
                    target.tagName === 'INPUT' ||
                    target.tagName === 'TEXTAREA'
                );
                if (isNativeInputTarget && target.id !== 'ime-bypass-textarea') {
                    return;
                }

                event.preventDefault();
                if (key === 'c') {
                    copySelectionToClipboard();
                } else if (key === 'x') {
                    cutSelectionToClipboard();
                } else {
                    hideAutocomplete(500);
                    pasteFromClipboard();
                }
                return;
            }
            if (key === '1') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'toggleLeftPanel' });
                return;
            }
            if (key === '2') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'toggleRightPanel' });
                return;
            }
            if (key === '3') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'expandRightPanel' });
                return;
            }
            if (key === 'n') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'newTab' });
                return;
            }
            if (key === 's') {
                event.preventDefault();
                hideAutocomplete(300);
                post({ type: 'shortcut', name: 'save' });
                return;
            }
            if (key === 'o') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'open' });
                return;
            }
            if (key === 'w') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'closeTab' });
                return;
            }
            if (key === 'p') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'print' });
                return;
            }
            if (key === 'f') {
                event.preventDefault();
                if (event.shiftKey) post({ type: 'shortcut', name: 'searchAll' });
                else openFindPanel();
                return;
            }
            if (event.code === 'Backquote' || event.key === '`' || event.key === '~' || event.key === 'Dead') {
                event.preventDefault();
                post({ type: 'shortcut', name: 'terminal' });
                return;
            }
            if (key === 'a') {
                event.preventDefault();
                selectAll();
                return;
            }
            if (key === 'z') {
                handleUndoRedoShortcut(event, 'undo');
                return;
            }
            if (key === 'y') {
                handleUndoRedoShortcut(event, 'redo');
                return;
            }
        }

        const activeElement = document.activeElement;
        const isEditorImeBypass = activeElement?.id === 'ime-bypass-textarea';
        if (activeElement && !isEditorImeBypass &&
            (activeElement.closest('#find-panel') || activeElement.tagName === 'INPUT' || activeElement.tagName === 'TEXTAREA')) {
            return;
        }

        const element = activeEditableElement();
        if (state.csvTableEnabled || !element || element.getAttribute('contenteditable') !== 'true') return;

        const columnSelection = activeColumnSelection();
        if (columnSelection && isPlainTextKey(event)) {
            event.preventDefault();
            replaceSelectionWith(columnSelection, event.key);
            return;
        }

        if ((event.key === 'Home' || event.key === 'End') && event.ctrlKey) {
            event.preventDefault();
            if (event.key === 'Home') {
                focusOrSelectHome(event.shiftKey);
            } else {
                focusOrSelectEnd(event.shiftKey);
            }
            return;
        }

        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            moveCaretHorizontal(element, event.key === 'ArrowLeft' ? -1 : 1, event.shiftKey);
            return;
        }

        if (event.key === 'ArrowUp') {
            startKeyboardVerticalRepeat(event, element, -1);
            return;
        }

        if (event.key === 'ArrowDown') {
            startKeyboardVerticalRepeat(event, element, 1);
            return;
        }

        if (event.key === 'PageUp') {
            event.preventDefault();
            commitLine(element);
            const pageLines = Math.max(1, Math.floor(scrollContainer.clientHeight / state.lineHeight) - 1);
            const currentLineNum = Number(element.dataset.line || state.currentLine || 1);
            const currentCol = getCaretOffset(element);
            const targetLineNum = Math.max(1, currentLineNum - pageLines);
            if (event.shiftKey) {
                const anchor = state.selectionAnchor || { line: currentLineNum, column: currentCol };
                state.selectionAnchor = anchor;
                state.selection = (anchor.line === targetLineNum && anchor.column === currentCol)
                    ? null
                    : { start: anchor, end: { line: targetLineNum, column: currentCol } };
                state.currentLine = targetLineNum;
                state.currentColumn = currentCol + 1;
                syncCustomSelectionClass();
                queueRender(true);
            } else {
                state.selection = null;
                state.selectionAnchor = { line: targetLineNum, column: currentCol };
                state.currentLine = targetLineNum;
                state.currentColumn = currentCol + 1;
                syncCustomSelectionClass();
            }
            setTimeout(() => focusLine(targetLineNum, currentCol, 3 * state.lineHeight), 0);
            return;
        }

        if (event.key === 'PageDown') {
            event.preventDefault();
            commitLine(element);
            const pageLines = Math.max(1, Math.floor(scrollContainer.clientHeight / state.lineHeight) - 1);
            const currentLineNum = Number(element.dataset.line || state.currentLine || 1);
            const currentCol = getCaretOffset(element);
            const targetLineNum = Math.min(state.lineCount, currentLineNum + pageLines);
            if (event.shiftKey) {
                const anchor = state.selectionAnchor || { line: currentLineNum, column: currentCol };
                state.selectionAnchor = anchor;
                state.selection = (anchor.line === targetLineNum && anchor.column === currentCol)
                    ? null
                    : { start: anchor, end: { line: targetLineNum, column: currentCol } };
                state.currentLine = targetLineNum;
                state.currentColumn = currentCol + 1;
                syncCustomSelectionClass();
                queueRender(true);
            } else {
                state.selection = null;
                state.selectionAnchor = { line: targetLineNum, column: currentCol };
                state.currentLine = targetLineNum;
                state.currentColumn = currentCol + 1;
                syncCustomSelectionClass();
            }
            setTimeout(() => focusLine(targetLineNum, currentCol, 3 * state.lineHeight), 0);
            return;
        }

        if ((event.key === ' ' || event.code === 'Space') && !event.ctrlKey && !event.metaKey && !event.altKey) {
            event.preventDefault();
            markNativeBeforeInputHandled(['insertSpace', 'insertText'], 120);
            scheduleModelRepeatEdit('Space', event.repeat);
            return;
        }

        if (event.key === 'Tab') {
            event.preventDefault();
            if (event.shiftKey || hasCustomSelection()) {
                changeLineIndent(event.shiftKey ? -1 : 1);
                return;
            }
            insertPlainTextByModel(element, ' '.repeat(state.tabSize));
            return;
        }

        if (isPlainTextKey(event)) {
            if (hasCustomSelection() && hasNativeSelectionInElement(element)) {
                return;
            }

            event.preventDefault();
            markNativeBeforeInputHandled(['insertText'], 120);
            scheduleModelRepeatEdit(normalizedModelRepeatKey(event), event.repeat);
            if (!event.repeat) {
                triggerAutocomplete(activeEditableElement() || element);
            }
            return;
        }

        if (isModelRepeatKey(event)) {
            event.preventDefault();
            const keyName = normalizedModelRepeatKey(event);
            if (keyName === 'Enter') {
                markNativeBeforeInputHandled(['insertLineBreak', 'insertParagraph', 'insertText', 'insertSpace'], 120);
            } else {
                state.lastDeleteKeyDown = {
                    key: keyName,
                    line: Number(element.dataset.line || state.currentLine || 1),
                    column: getCaretOffset(element),
                    time: performance.now()
                };
                markNativeBeforeInputHandled(keyName === 'Backspace'
                    ? ['deleteContentBackward']
                    : ['deleteContentForward']);
            }
            scheduleModelRepeatEdit(keyName, event.repeat);
            return;
        }

        if (event.key === 'Enter') {
            event.preventDefault();
            markNativeBeforeInputHandled(['insertLineBreak', 'insertParagraph', 'insertText', 'insertSpace'], 120);
            splitCurrentLine(element);
            return;
        }
    });

    function handleHexEditorKey(event) {
        const key = event.key || '';
        const selection = state.hexSelection;
        const startOffset = selection
            ? Math.max(0, Math.min(Number(selection.startOffset || 0), Number(selection.endOffset || 0)))
            : Math.max(0, Number(state.hexCursorOffset || 0));

        if (key === 'Backspace' || key === 'Delete') {
            event.preventDefault();
            const length = selection
                ? Math.max(1, Math.abs(Number(selection.endOffset || 0) - Number(selection.startOffset || 0)))
                : 1;
            submitHexEdit(new Array(length).fill(0), startOffset);
            return true;
        }

        const movement = key === 'ArrowLeft' ? -1
            : key === 'ArrowRight' ? 1
                : key === 'ArrowUp' ? -16
                    : key === 'ArrowDown' ? 16
                        : 0;
        if (movement !== 0) {
            event.preventDefault();
            moveHexCursor(Math.max(0, startOffset + movement));
            return true;
        }

        if (state.hexSelectionPane === 'ascii' && key.length === 1 && key.charCodeAt(0) <= 0xFF) {
            event.preventDefault();
            submitHexEdit([key.charCodeAt(0)], startOffset);
            moveHexCursor(startOffset + 1);
            return true;
        }

        if (state.hexSelectionPane === 'hex' && /^[0-9a-f]$/i.test(key)) {
            event.preventDefault();
            const nibble = key.toUpperCase();
            const pending = state.hexPendingHighNibble;
            if (!pending || pending.offset !== startOffset) {
                state.hexPendingHighNibble = { offset: startOffset, value: nibble };
            } else {
                submitHexEdit([parseInt(pending.value + nibble, 16)], startOffset);
                moveHexCursor(startOffset + 1);
            }
            return true;
        }

        return false;
    }

    function moveHexCursor(offset) {
        const safeOffset = Math.max(0, Number(offset || 0));
        state.hexCursorOffset = safeOffset;
        state.hexSelectionAnchorOffset = safeOffset;
        state.hexSelection = { startOffset: safeOffset, endOffset: safeOffset + 1 };
        state.hexPendingHighNibble = null;
        state.currentLine = Math.floor(safeOffset / 16) + 2;

        const byteIndex = safeOffset % 16;
        const text = state.cache.get(state.currentLine) || '';
        const firstPipe = text.indexOf('|');
        const hexStart = Math.max(0, firstPipe > 0 ? firstPipe - 50 : 11);
        const column = state.hexSelectionPane === 'ascii' && firstPipe >= 0
            ? firstPipe + 1 + byteIndex
            : hexStart + (byteIndex * 3) + (byteIndex >= 8 ? 1 : 0);
        state.currentColumn = column + 1;
        queueRender(true);
        reportCursorAndSelection();
    }

    viewport.addEventListener('keyup', event => {
        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            stopKeyboardVerticalRepeat(event.key);
            clearAutocompleteNavigationKey(event.key);
        }

        if (isModelRepeatKey(event)) {
            clearPendingRepeatEdit(normalizedModelRepeatKey(event));
        }

        const element = lineElementFromEvent(event);
        reportCursorAndSelection(element || document.activeElement);

        if (event.key === 'Shift' && hasCustomSelection() && !state.isComposing) {
            const sel = normalizeSelection();
            if (sel && !sel.isColumn) {
                const startTextElement = viewport.querySelector(`.line-row[data-line="${sel.start.line}"] .line-text`);
                if (startTextElement && document.activeElement !== startTextElement) {
                    focusLine(sel.start.line, sel.start.column);
                }
            }
        }

        if ((state.autocompleteOnEnter || state.autocompleteOnTab) && element && element.getAttribute('contenteditable') === 'true') {
            const ignoredKeys = [
                'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
                'Enter', 'Escape', 'Tab', ' ', 'Shift', 'Control', 'Alt', 'Meta',
                'CapsLock', 'Home', 'End', 'PageUp', 'PageDown', 'Backspace', 'Delete',
                'Process', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12'
            ];
            if (!ignoredKeys.includes(event.key) && event.keyCode !== 229 && !event.ctrlKey && !event.metaKey) {
                triggerAutocomplete(element);
            }
        }
    });

    document.addEventListener('keyup', event => {
        if (isModelRepeatKey(event)) {
            clearPendingRepeatEdit(normalizedModelRepeatKey(event));
        }

        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            stopKeyboardVerticalRepeat(event.key);
            clearAutocompleteNavigationKey(event.key);
        } else if (event.key === 'Shift' && keyboardVerticalRepeat.key) {
            keyboardVerticalRepeat.extendSelection = false;
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Shift' && keyboardVerticalRepeat.key) {
            keyboardVerticalRepeat.extendSelection = true;
        }
    });

    window.addEventListener('blur', () => {
        stopKeyboardVerticalRepeat();
        clearPendingRepeatEdit();
    });
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            stopKeyboardVerticalRepeat();
            clearPendingRepeatEdit();
        }
    });
}
