import { viewport } from './editor-dom.js';
import {
    activeEditableElement,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    queueRender,
    state,
    syncCustomSelectionClass
} from './editor-core.js';
import {
    activeColumnSelection,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    beginColumnComposition,
    clearPendingImeSelectionCollapse,
    commitLine,
    compositionSelectionRange,
    finishColumnComposition,
    finishRangeComposition,
    focusImeBypassTextarea,
    focusLine,
    getCaretOffset,
    inputRangeInElement,
    isPendingImeSelectionCollapseFor,
    isSpaceInputEvent,
    lineElementFromEvent,
    lineTextFromElement,
    makeEditablePlainText,
    mergeLineBackward,
    mergeLineForward,
    moveCaretVertical,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    shouldSuppressNativeBeforeInput,
    updateSingleLine
} from './editor-commands.js';
import {
    cancelAutocompleteCaretRestore,
    triggerAutocomplete
} from './editor-autocomplete.js';

export function bindTextInputEvents({ renderer }) {
    const { clearPendingInlineLivePreviewFocusForLine } = renderer;
    let postCompositionNavigationInputGuard = null;

    function guardNextCompositionInput(lineNumber) {
        const guard = { lineNumber: Number(lineNumber || 0) };
        postCompositionNavigationInputGuard = guard;
        setTimeout(() => {
            if (postCompositionNavigationInputGuard === guard) {
                postCompositionNavigationInputGuard = null;
            }
        }, 0);
    }

    function isSplitMultilineCompositionSelection(selection) {
        return !!state.isSplitView &&
            !!selection &&
            !selection.isColumn &&
            selection.start.line !== selection.end.line;
    }

    viewport.addEventListener('input', event => {
        if (shouldSuppressNativeBeforeInput(event)) {
            return;
        }
        const element = lineElementFromEvent(event);
        if (element) {
            if (postCompositionNavigationInputGuard &&
                Number(element.dataset.line || 0) === postCompositionNavigationInputGuard.lineNumber) {
                postCompositionNavigationInputGuard = null;
                return;
            }
            if (!state.isComposing && isPendingImeSelectionCollapseFor(element, event)) {
                return;
            }
            if (!state.columnComposition) {
                state.selection = null;
                syncCustomSelectionClass();
            }
            const inputSnapshot = commitLine(element);
            if (!state.isComposing && !state.rangeComposition) {
                triggerAutocomplete(element, inputSnapshot);
            }
        }
    });

    viewport.addEventListener('focusin', event => {
        if (state.csvTableEnabled) return;
        const element = lineElementFromEvent(event);
        if (element && element.getAttribute('contenteditable') === 'true') {
            state.editingLine = Number(element.dataset.line || state.currentLine || 1);
            clearPendingInlineLivePreviewFocusForLine(state.editingLine);
            queueRender();
        }
    });

    viewport.addEventListener('focusout', () => {
        if (state.csvTableEnabled) return;
        setTimeout(() => {
            const hasLineTextFocus = !!document.activeElement?.closest?.('.line-text');
            const shouldKeepSelectionEditContext = hasCustomSelection() || state.textareaImeBypassActive;
            if (state.inlineLivePreviewEnabled && !hasLineTextFocus) {
                if (shouldKeepSelectionEditContext && state.inlineLivePreviewSourceLine) {
                    return;
                }
                state.inlineLivePreviewSourceLine = null;
                state.inlineLivePreviewEditableBlock = null;
                state.editingLine = null;
                queueRender(true);
                return;
            }
            if (!hasLineTextFocus) {
                if (shouldKeepSelectionEditContext) {
                    return;
                }
                state.editingLine = null;
                queueRender(true);
            }
        }, 80);
    });

    viewport.addEventListener('compositionstart', event => {
        cancelAutocompleteCaretRestore();
        postCompositionNavigationInputGuard = null;
        state.pendingImeVerticalNavigation = null;
        let element = lineElementFromEvent(event) || activeEditableElement();
        const pendingCompositionSelection = compositionSelectionRange();
        let collapsedSelectionForComposition = false;

        if (isSplitMultilineCompositionSelection(pendingCompositionSelection)) {
            focusImeBypassTextarea();
            state.columnComposition = null;
            return;
        }

        if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
            const isCollapsed = pendingCompositionSelection.start.line === pendingCompositionSelection.end.line &&
                                pendingCompositionSelection.start.column === pendingCompositionSelection.end.column;
            if (!isCollapsed) {
                element = replaceSelectionForCompositionStart(element) || element;
                collapsedSelectionForComposition = true;
            }
        }

        if (isPendingImeSelectionCollapseFor(element)) {
            clearPendingImeSelectionCollapse();
        }

        state.isComposing = true;
        state.compositionLine = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;

        if (element && element.getAttribute('contenteditable') === 'true') {
            state.editingLine = state.compositionLine;

            if (collapsedSelectionForComposition) {
                state.columnComposition = null;
                return;
            }

            beginColumnComposition(element);
        } else {
            state.columnComposition = null;
        }
    });

    viewport.addEventListener('compositionupdate', event => {
        state.isComposing = true;
    });

    viewport.addEventListener('compositionend', event => {
        const element = lineElementFromEvent(event) || activeEditableElement();
        const lineNumber = element ? Number(element.dataset.line || state.compositionLine || state.currentLine) : state.compositionLine;
        const pendingVerticalNavigation = state.pendingImeVerticalNavigation;

        state.isComposing = false;
        clearPendingImeSelectionCollapse();
        state.compositionLine = null;
        state.pendingImeVerticalNavigation = null;

        const moveAfterComposition = current => {
            if (!pendingVerticalNavigation || !current ||
                current.getAttribute('contenteditable') !== 'true') {
                return false;
            }

            const sourceLine = Math.min(
                state.lineCount,
                Math.max(1, Number(pendingVerticalNavigation.lineNumber || lineNumber || state.currentLine || 1)));
            const sourceColumn = Math.max(0, Number(pendingVerticalNavigation.column || 0));
            focusLine(sourceLine, sourceColumn);
            const source = viewport.querySelector(`.line-text[data-line="${sourceLine}"]`) || current;

            return moveCaretVertical(
                source,
                pendingVerticalNavigation.direction,
                pendingVerticalNavigation.extendSelection);
        };

        if (finishRangeComposition(element, lineNumber, event.data || '')) {
            if (pendingVerticalNavigation) {
                setTimeout(() => {
                    const current = activeEditableElement();
                    moveAfterComposition(current);
                }, 0);
            }
            return;
        }

        if (finishColumnComposition(element, lineNumber)) {
            if (pendingVerticalNavigation) {
                setTimeout(() => {
                    const current = activeEditableElement();
                    moveAfterComposition(current);
                }, 0);
            }
            return;
        }

        if (element && element.getAttribute('contenteditable') === 'true') {
            state.selection = null;
            syncCustomSelectionClass();
            state.editingLine = lineNumber;
            const current = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
            if (current && current.getAttribute('contenteditable') === 'true') {
                // Finalize the composition before the next physical keydown. A
                // zero-delay timer here used to run after ArrowDown had already
                // moved the caret, and commitLine() then restored currentLine to
                // the composition source row.
                const inputSnapshot = commitLine(current);
                if (moveAfterComposition(current)) {
                    guardNextCompositionInput(lineNumber);
                } else {
                    triggerAutocomplete(current, inputSnapshot);
                    queueRender(true);
                }
            }
        }
    });

    viewport.addEventListener('beforeinput', event => {
        cancelAutocompleteCaretRestore();
        if (state.csvTableEnabled) return;
        let element = lineElementFromEvent(event);

        if (event.isComposing || state.isComposing ||
            event.inputType === 'insertCompositionText' ||
            event.inputType === 'deleteCompositionText') {
            const pendingCompositionSelection = compositionSelectionRange(!state.isComposing);
            if (isSplitMultilineCompositionSelection(pendingCompositionSelection)) {
                if (focusImeBypassTextarea()) {
                    event.preventDefault();
                }
                state.columnComposition = null;
                return;
            }

            if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
                const replacedElement = replaceSelectionForCompositionStart(element || activeEditableElement());
                if (replacedElement) {
                    element = replacedElement;
                    state.compositionLine = Number(replacedElement.dataset.line || state.currentLine || 1);
                    state.editingLine = state.compositionLine;
                }
            }
            return;
        }

        if (isPendingImeSelectionCollapseFor(element, event)) {
            return;
        }

        if (shouldSuppressNativeBeforeInput(event)) {
            event.preventDefault();
            return;
        }

        const columnSelection = activeColumnSelection();
        if (columnSelection && event.inputType?.startsWith('insert') &&
            event.inputType !== 'insertCompositionText' &&
            event.inputType !== 'insertFromPaste' &&
            event.inputType !== 'insertFromDrop') {
            event.preventDefault();
            replaceSelectionWith(columnSelection, event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph' ? '\n' : (event.data || ''));
            return;
        }

        if (isSpaceInputEvent(event)) {
            event.preventDefault();
            const target = element || activeEditableElement();
            if (!target || target.getAttribute('contenteditable') !== 'true') return;

            if (hasCustomSelection()) {
                const sel = normalizeSelection();
                if (sel) replaceSelectionWith(sel, ' ');
                return;
            }

            const text = lineTextFromElement(target);
            const range = inputRangeInElement(event, target);
            const start = range ? range.start : getCaretOffset(target);
            const end = range ? range.end : start;
            makeEditablePlainText(target, start);
            updateSingleLine(target, text.slice(0, start) + ' ' + text.slice(end), start + 1);
            return;
        }

        if (event.inputType === 'deleteContentBackward' || event.inputType === 'deleteContentForward') {
            event.preventDefault();
            const target = element || activeEditableElement();
            if (!target || target.getAttribute('contenteditable') !== 'true') return;

            if (hasCustomSelection()) {
                const sel = normalizeSelection();
                if (sel) replaceSelectionWith(sel, '');
                return;
            }

            const text = lineTextFromElement(target);
            const range = inputRangeInElement(event, target);
            if (range && range.start !== range.end) {
                makeEditablePlainText(target, range.start);
                updateSingleLine(target, text.slice(0, range.start) + text.slice(range.end), range.start);
                return;
            }

            const caret = range ? range.start : getCaretOffset(target);
            makeEditablePlainText(target, caret);
            if (event.inputType === 'deleteContentBackward') {
                if (caret > 0) {
                    const tabSize = state.tabSize || 4;
                    const prefix = text.slice(0, caret);
                    const onlySpacesBefore = prefix.length > 0 && /^ *$/.test(prefix);
                    if (onlySpacesBefore && prefix.length % tabSize === 0) {
                        const deleteStart = caret - Math.min(tabSize, caret);
                        updateSingleLine(target, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
                    } else {
                        const deleteStart = graphemeDeleteStart(text, caret);
                        updateSingleLine(target, text.slice(0, deleteStart) + text.slice(caret), deleteStart);
                    }
                } else {
                    mergeLineBackward(target);
                }
            } else {
                if (caret < text.length) {
                    const delEnd = graphemeDeleteEnd(text, caret);
                    updateSingleLine(target, text.slice(0, caret) + text.slice(delEnd), caret);
                } else {
                    mergeLineForward(target);
                }
            }
            return;
        }

        if (!hasCustomSelection()) {
            if (element && element.getAttribute('contenteditable') === 'true' && event.inputType?.startsWith('insert')) {
                makeEditablePlainText(element);
            }
            return;
        }

        const sel = normalizeSelection();
        if (!sel) return;

        if (event.inputType === 'insertText') {
            event.preventDefault();
            replaceSelectionWith(sel, event.data || '');
        } else if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') {
            event.preventDefault();
            replaceSelectionWith(sel, '\n');
        } else if (event.inputType === 'insertFromPaste' || event.inputType === 'insertFromDrop') {
            event.preventDefault();
        } else if (event.inputType && event.inputType.startsWith('insert')) {
            event.preventDefault();
            replaceSelectionWith(sel, event.data || '');
        }
    });
}
