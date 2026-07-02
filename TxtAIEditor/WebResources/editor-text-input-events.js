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
    getCaretOffset,
    inputRangeInElement,
    isPendingImeSelectionCollapseFor,
    isSpaceInputEvent,
    lineElementFromEvent,
    lineTextFromElement,
    makeEditablePlainText,
    mergeLineBackward,
    mergeLineForward,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    shouldSuppressNativeBeforeInput,
    updateSingleLine
} from './editor-commands.js';
import { triggerAutocomplete } from './editor-autocomplete.js';

export function bindTextInputEvents({ renderer }) {
    const { clearPendingInlineLivePreviewFocusForLine } = renderer;
    viewport.addEventListener('input', event => {
        if (shouldSuppressNativeBeforeInput(event)) {
            return;
        }
        const element = lineElementFromEvent(event);
        if (element) {
            if (!state.isComposing && isPendingImeSelectionCollapseFor(element, event)) {
                return;
            }
            if (!state.columnComposition) {
                state.selection = null;
                syncCustomSelectionClass();
            }
            commitLine(element);
            if (!state.isComposing && !state.rangeComposition) {
                triggerAutocomplete(element);
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
            if (state.inlineLivePreviewEnabled && !document.activeElement?.closest?.('.line-text')) {
                state.inlineLivePreviewSourceLine = null;
                state.inlineLivePreviewEditableBlock = null;
                state.editingLine = null;
                queueRender(true);
                return;
            }
            if (!document.activeElement?.closest?.('.line-text')) {
                state.editingLine = null;
                queueRender(true);
            }
        }, 80);
    });

    viewport.addEventListener('compositionstart', event => {
        let element = lineElementFromEvent(event) || activeEditableElement();
        const pendingCompositionSelection = compositionSelectionRange();
        let collapsedSelectionForComposition = false;

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

        state.isComposing = false;
        clearPendingImeSelectionCollapse();
        state.compositionLine = null;

        if (finishRangeComposition(element, lineNumber, event.data || '')) {
            return;
        }

        if (finishColumnComposition(element, lineNumber)) {
            return;
        }

        if (element && element.getAttribute('contenteditable') === 'true') {
            state.selection = null;
            syncCustomSelectionClass();
            state.editingLine = lineNumber;
            setTimeout(() => {
                const current = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
                if (current && current.getAttribute('contenteditable') === 'true') {
                    commitLine(current);
                    triggerAutocomplete(current);
                }
            }, 0);
        }
    });

    viewport.addEventListener('beforeinput', event => {
        if (state.csvTableEnabled) return;
        let element = lineElementFromEvent(event);

        if (event.isComposing || state.isComposing ||
            event.inputType === 'insertCompositionText' ||
            event.inputType === 'deleteCompositionText') {
            const pendingCompositionSelection = compositionSelectionRange(!state.isComposing);
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
