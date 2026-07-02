import {
    activeEditableElement,
    selectedText,
    state
} from './editor-core.js';
import { hasCustomSelection, normalizeSelection } from './editor-selection.js';
import {
    commitLine,
    insertTextAtCaret,
    replaceSelectionWith
} from './editor-commands.js';
import { hideAutocomplete } from './editor-autocomplete.js';

export function bindClipboardEvents() {
    let suppressNativePasteUntil = 0;
    document.addEventListener('copy', event => {
        if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
            if (event.target.id !== 'ime-bypass-textarea') return;
        }
        const text = selectedText();
        if (text) {
            event.clipboardData?.setData('text/plain', text);
            event.preventDefault();
        }
    });

    document.addEventListener('cut', event => {
        if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
            if (event.target.id !== 'ime-bypass-textarea') return;
        }
        const text = selectedText();
        if (!text) return;

        event.clipboardData?.setData('text/plain', text);
        event.preventDefault();
        if (state.readOnly) return;

        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, '');
            return;
        }

        const element = activeEditableElement();
        const selection = window.getSelection();
        if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
            document.execCommand('delete');
            commitLine(element);
        }
    });

    document.addEventListener('paste', event => {
        if (event.target && (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA' || event.target.closest('#find-panel'))) {
            if (event.target.id !== 'ime-bypass-textarea') return;
        }
        if (performance.now() < suppressNativePasteUntil) {
            event.preventDefault();
            return;
        }
        event.preventDefault();
        const clipboardText = (event.clipboardData?.getData('text/plain') || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        let element = document.activeElement?.closest?.('.line-text');
        if (!element && document.activeElement?.id === 'ime-bypass-textarea') {
            element = activeEditableElement();
        }
        if (!element || element.getAttribute('contenteditable') !== 'true') return;

        // Ctrl+V 붙여넣기 시 자동완성 팝업이 뜨지 않도록 억제
        hideAutocomplete(500);

        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) {
                replaceSelectionWith(sel, clipboardText);
                return;
            }
        }

        insertTextAtCaret(clipboardText);
    });
    function suppressNativePaste(durationMs = 250) {
        suppressNativePasteUntil = performance.now() + durationMs;
    }

    return { suppressNativePaste };
}