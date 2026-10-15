import { viewport } from './editor-dom.js';
import { ImePhase } from './editor-ime-state.js';
import {
    activeEditableElement,
    cancelPendingColumnTextInputs,
    consumePendingColumnTextInput,
    csvTableMode,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    imeController,
    MAX_RENDER_CHARS,
    queueRender,
    selectionController,
    state,
    syncCustomSelectionClass
} from './editor-core.js';
import { highlightPreviewSearchInElement, renderLineContent } from './editor-highlighter.js';
import { markdownPreviewDeleteRange, updateGroupedMarkdownElement } from './editor-markdown-preview-edit.js';
import { capturePreviewInputScrollAnchor } from './editor-caret.js';
import {
    activeColumnSelection,
    hasCustomSelection,
    normalizeSelection
} from './editor-selection.js';
import {
    beginDeferredRangeComposition,
    beginColumnComposition,
    clearPendingImeSelectionCollapse,
    commitLine,
    compositionSelectionRange,
    finishColumnComposition,
    finishRangeComposition,
    focusLine,
    getCaretOffset,
    inputRangeInElement,
    insertPlainTextByModel,
    isPendingImeSelectionCollapseFor,
    isSpaceInputEvent,
    lineElementFromEvent,
    lineTextFromElement,
    makeEditablePlainText,
    markNativeBeforeInputHandled,
    mergeLineBackward,
    mergeLineForward,
    moveCaretVertical,
    replaceSelectionForCompositionStart,
    replaceSelectionWith,
    resetModelRepeatState,
    setCaret,
    shouldSuppressNativeBeforeInput,
    splitCurrentLine,
    updateSingleLine
} from './editor-commands.js';
import {
    cancelAutocompleteCaretRestore,
    triggerAutocomplete
} from './editor-autocomplete.js';

export function bindTextInputEvents({ renderer }) {
    const { clearPendingInlineLivePreviewFocusForLine } = renderer;
    let postCompositionNavigationInputGuard = null;
    let pendingCompositionLineBreak = null;

    function restoreInputLineHighlighting(element, inputSnapshot) {
        if (!element?.isConnected || !inputSnapshot ||
            imeController.isComposing || imeController.rangeComposition || imeController.columnComposition) {
            return;
        }

        const lineNumber = Number(element.dataset.line || 0);
        if (!lineNumber || lineNumber !== inputSnapshot.lineNumber ||
            element.getAttribute('contenteditable') !== 'true') {
            return;
        }

        const text = inputSnapshot.text || '';
        if (text.length > MAX_RENDER_CHARS) {
            element.textContent = text;
        } else {
            if (!updateGroupedMarkdownElement(element, text, state)) {
                element.innerHTML = renderLineContent(lineNumber, text);
            }
            highlightPreviewSearchInElement(element, lineNumber);
        }
        setCaret(element, inputSnapshot.caretOffset);
    }

    function guardNextCompositionInput(lineNumber) {
        const guard = { lineNumber: Number(lineNumber || 0) };
        postCompositionNavigationInputGuard = guard;
        setTimeout(() => {
            if (postCompositionNavigationInputGuard === guard) {
                postCompositionNavigationInputGuard = null;
            }
        }, 0);
    }

    viewport.addEventListener('input', event => {
        if (shouldSuppressNativeBeforeInput(event)) {
            return;
        }

        const element = lineElementFromEvent(event);
        if (element) {
            for (const content of element.querySelectorAll('[data-markdown-empty-content]')) {
                if (content.textContent.length) delete content.dataset.markdownEmptyContent;
            }
            // Attribute-only styling leaves the native composition node and
            // selection untouched, while hiding an empty heading's prefix as
            // soon as the first title glyph arrives.
            const emptyHeading = element.querySelector('.md-preview-empty-heading');
            if (emptyHeading) emptyHeading.classList.toggle('md-preview-heading-has-content',
                !!emptyHeading.querySelector('.md-preview-heading-body')?.textContent.trim());
            const pendingColumnText = !imeController.isComposing
                ? consumePendingColumnTextInput(event.data)
                : null;
            const columnSelection = pendingColumnText ? activeColumnSelection() : null;
            if (columnSelection) {
                replaceSelectionWith(columnSelection, event.data || pendingColumnText);
                return;
            }
            if (postCompositionNavigationInputGuard &&
                Number(element.dataset.line || 0) === postCompositionNavigationInputGuard.lineNumber) {
                postCompositionNavigationInputGuard = null;
                return;
            }
            if (!imeController.isComposing && imeController.rangeComposition) {
                clearPendingImeSelectionCollapse();
                const lineNumber = Number(element.dataset.line || state.currentLine || 1);
                if (finishRangeComposition(element, lineNumber, event.data || '')) {
                    imeController.completeCommit();
                    return;
                }
            }
            if (!imeController.isComposing && isPendingImeSelectionCollapseFor(element, event)) {
                return;
            }
            if (!imeController.columnComposition && !imeController.rangeComposition?.deferred) {
                selectionController.clear();
                syncCustomSelectionClass();
            }
            const inputSnapshot = commitLine(element);
            if (!imeController.isComposing && !imeController.rangeComposition) {
                restoreInputLineHighlighting(element, inputSnapshot);
                triggerAutocomplete(element, inputSnapshot);
            }
        }
    });

    viewport.addEventListener('focusin', event => {
        if (csvTableMode.isEnabled) return;
        const element = lineElementFromEvent(event);
        if (element && element.getAttribute('contenteditable') === 'true') {
            state.editingLine = Number(element.dataset.line || state.currentLine || 1);
            clearPendingInlineLivePreviewFocusForLine(state.editingLine);
            queueRender();
        }
    });

    viewport.addEventListener('focusout', () => {
        if (csvTableMode.isEnabled) return;
        setTimeout(() => {
            const hasLineTextFocus = !!document.activeElement?.closest?.('.line-text');
            const shouldKeepSelectionEditContext = hasCustomSelection();
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
        resetModelRepeatState({ forComposition: true });
        cancelPendingColumnTextInputs();
        cancelAutocompleteCaretRestore();
        postCompositionNavigationInputGuard = null;
        pendingCompositionLineBreak = null;
        imeController.pendingVerticalNavigation = null;
        let element = lineElementFromEvent(event) || activeEditableElement();
        const pendingCompositionSelection = compositionSelectionRange();
        let collapsedSelectionForComposition = false;

        if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
            const isCollapsed = pendingCompositionSelection.start.line === pendingCompositionSelection.end.line &&
                                pendingCompositionSelection.start.column === pendingCompositionSelection.end.column;
            if (!isCollapsed) {
                element = pendingCompositionSelection.start.line !== pendingCompositionSelection.end.line
                    ? (beginDeferredRangeComposition(element, pendingCompositionSelection) || element)
                    : (replaceSelectionForCompositionStart(element) || element);
                collapsedSelectionForComposition = true;
            }
        }

        if (isPendingImeSelectionCollapseFor(element)) {
            clearPendingImeSelectionCollapse();
        }

        const compositionLine = element ? Number(element.dataset.line || state.currentLine || 1) : state.currentLine;
        let phase = imeController.rangeComposition ? ImePhase.RangeComposition : ImePhase.NativeComposition;

        if (element && element.getAttribute('contenteditable') === 'true') {
            state.editingLine = compositionLine;

            if (collapsedSelectionForComposition) {
                imeController.columnComposition = null;
            } else if (beginColumnComposition(element)) {
                phase = ImePhase.ColumnComposition;
            }
        } else {
            imeController.columnComposition = null;
        }

        if (!imeController.beginComposition(phase, compositionLine)) {
            return;
        }
        capturePreviewInputScrollAnchor(element);
    });

    viewport.addEventListener('compositionupdate', event => {
        imeController.updateComposition();
    });

    viewport.addEventListener('compositionend', event => {
        const element = lineElementFromEvent(event) || activeEditableElement();
        const lineNumber = element ? Number(element.dataset.line || imeController.compositionLine || state.currentLine) : imeController.compositionLine;
        const pendingVerticalNavigation = imeController.pendingVerticalNavigation;
        const lineBreak = pendingCompositionLineBreak;
        pendingCompositionLineBreak = null;
        let committedElement = null;

        imeController.beginCommit();
        clearPendingImeSelectionCollapse();
        imeController.pendingVerticalNavigation = null;

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
            imeController.completeCommit();
            if (pendingVerticalNavigation) {
                setTimeout(() => {
                    const current = activeEditableElement();
                    moveAfterComposition(current);
                }, 0);
            }
            return;
        }

        if (finishColumnComposition(element, lineNumber)) {
            imeController.completeCommit();
            if (pendingVerticalNavigation) {
                setTimeout(() => {
                    const current = activeEditableElement();
                    moveAfterComposition(current);
                }, 0);
            }
            return;
        }

        if (element && element.getAttribute('contenteditable') === 'true') {
            selectionController.clear();
            syncCustomSelectionClass();
            state.editingLine = lineNumber;
            const current = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
            if (current && current.getAttribute('contenteditable') === 'true') {
                // Finalize the composition before the next physical keydown. A
                // zero-delay timer here used to run after ArrowDown had already
                // moved the caret, and commitLine() then restored currentLine to
                // the composition source row.
                const inputSnapshot = commitLine(current);
                committedElement = current;
                if (lineBreak) {
                    // Split only after completeCommit has released the native
                    // composition node and its selection.
                } else if (moveAfterComposition(current)) {
                    guardNextCompositionInput(lineNumber);
                } else {
                    triggerAutocomplete(current, inputSnapshot);
                    queueRender(true);
                }
            }
        }
        imeController.completeCommit();
        if (lineBreak?.lineNumber === lineNumber && lineBreak.documentId === state.hostDocumentId && committedElement?.isConnected) {
            guardNextCompositionInput(lineNumber);
            markNativeBeforeInputHandled(['insertLineBreak', 'insertParagraph'], 120);
            splitCurrentLine(committedElement);
        }
    });

    viewport.addEventListener('beforeinput', event => {
        cancelAutocompleteCaretRestore();
        if (csvTableMode.isEnabled) return;
        let element = lineElementFromEvent(event);

        if (event.isComposing || imeController.isComposing ||
            event.inputType === 'insertCompositionText' ||
            event.inputType === 'deleteCompositionText') {
            cancelPendingColumnTextInputs();
            if (['insertParagraph', 'insertLineBreak'].includes(event.inputType) &&
                element?.getAttribute('contenteditable') === 'true' && element.dataset.markdownPreviewLine !== 'code-empty' &&
                !imeController.rangeComposition && !imeController.columnComposition) {
                event.preventDefault();
                pendingCompositionLineBreak = { lineNumber: Number(element.dataset.line), documentId: state.hostDocumentId };
                return;
            }
            if (imeController.rangeComposition?.deferred) {
                return;
            }
            const pendingCompositionSelection = compositionSelectionRange(!imeController.isComposing);

            if (pendingCompositionSelection && !pendingCompositionSelection.isColumn) {
                const isMultilineSelection = pendingCompositionSelection.start.line !==
                    pendingCompositionSelection.end.line;
                const replacedElement = isMultilineSelection
                    ? beginDeferredRangeComposition(
                        element || activeEditableElement(),
                        pendingCompositionSelection)
                    : replaceSelectionForCompositionStart(element || activeEditableElement());
                if (replacedElement) {
                    element = replacedElement;
                    state.editingLine = Number(replacedElement.dataset.line || state.currentLine || 1);
                    if (imeController.rangeComposition && !imeController.isComposing) {
                        imeController.beginComposition(ImePhase.RangeComposition, state.editingLine);
                    }
                }
            }
            return;
        }

        capturePreviewInputScrollAnchor(element);

        if (isPendingImeSelectionCollapseFor(element, event)) {
            return;
        }

        if (shouldSuppressNativeBeforeInput(event)) {
            event.preventDefault();
            return;
        }

        if (element?.dataset.markdownPreviewLine === 'code-empty' && !hasCustomSelection()) {
            event.preventDefault();
            if (event.inputType === 'insertText') insertPlainTextByModel(element, event.data || '');
            else if (['insertParagraph', 'insertLineBreak'].includes(event.inputType)) insertPlainTextByModel(element, '');
            return;
        }

        const columnSelection = activeColumnSelection();
        if (columnSelection && event.inputType?.startsWith('insert') &&
            event.inputType !== 'insertCompositionText' &&
            event.inputType !== 'insertFromPaste' &&
            event.inputType !== 'insertFromDrop') {
            event.preventDefault();
            const pendingColumnText = consumePendingColumnTextInput(event.data);
            replaceSelectionWith(
                columnSelection,
                event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph'
                    ? '\n'
                    : (event.data || pendingColumnText || ''));
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
            const previewRange = markdownPreviewDeleteRange(target, caret,
                event.inputType === 'deleteContentBackward' ? -1 : 1, graphemeDeleteStart, graphemeDeleteEnd);
            if (previewRange) {
                if (previewRange.merge) {
                    if (target.dataset.markdownPreviewLine !== 'table') mergeLineForward(target);
                }
                else updateSingleLine(target, text.slice(0, previewRange.start) + text.slice(previewRange.end),
                    event.inputType === 'deleteContentBackward' ? previewRange.start : caret);
                return;
            }
            if (target.dataset.markdownPreviewLine === 'table') return;
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
            if (element?.getAttribute('contenteditable') === 'true' &&
                ['insertLineBreak', 'insertParagraph'].includes(event.inputType)) {
                event.preventDefault();
                splitCurrentLine(element);
                return;
            }
            if (element?.dataset.markdownPreviewLine === 'table' && event.inputType === 'insertText') {
                event.preventDefault();
                insertPlainTextByModel(element, event.data || '');
                return;
            }
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
