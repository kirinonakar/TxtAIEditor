export function createClipboardCommandHandlers({
    activeEditableElement,
    copyCsvSelectionToClipboard,
    csvTableMode,
    cutCsvSelectionToClipboard,
    commitLine,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    hexEditorMode,
    insertTextAtCaret,
    normalizeSelection,
    post,
    queueRender,
    readClipboardText,
    replaceSelectionWith,
    reportCursorAndSelection,
    selectionController,
    selectedText,
    state,
    syncCustomSelectionClass,
    writeClipboardText
}) {
    function deleteSelectionOrForward() {
        if (state.language === 'hex' && hexEditorMode.isEditable) {
            replaceHexSelectionWithZeros();
            return;
        }
        if (state.readOnly) return;
        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, '');
            return;
        }

        const selection = window.getSelection();
        const element = activeEditableElement();
        if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
            const selected = selection.toString();
            if (selected) {
                document.execCommand('delete');
                commitLine(element);
                return;
            }
        }

        deleteForwardAtCaret(element);
    }

    async function cutSelectionToClipboard() {
        if (csvTableMode.isEnabled) {
            return await cutCsvSelectionToClipboard();
        }

        const text = selectedText();
        if (!text) return false;
        const copied = await writeClipboardText(text);
        if (copied && state.language === 'hex' && hexEditorMode.isEditable) {
            replaceHexSelectionWithZeros();
            return true;
        }
        if (!copied || state.readOnly) return copied;

        if (hasCustomSelection()) {
            const sel = normalizeSelection();
            if (sel) replaceSelectionWith(sel, '');
            return true;
        }

        const element = activeEditableElement();
        const selection = window.getSelection();
        if (element && selection?.rangeCount && element.contains(selection.anchorNode)) {
            document.execCommand('delete');
            commitLine(element);
        }

        return true;
    }

    async function copySelectionToClipboard() {
        if (csvTableMode.isEnabled) {
            return await copyCsvSelectionToClipboard();
        }

        const text = selectedText();
        if (!text) return false;
        return await writeClipboardText(text);
    }

    async function pasteFromClipboard() {
        if (state.language === 'hex' && hexEditorMode.isEditable) {
            const text = await readClipboardText();
            const bytes = parseHexClipboard(text);
            if (bytes.length > 0) submitHexEdit(bytes);
            return;
        }
        if (state.readOnly) return;
        const text = await readClipboardText();
        if (text) insertTextAtCaret(text);
    }

    function selectAll() {
        const lastLine = state.lineCount;
        const lastText = state.cache.get(lastLine) || '';
        const endColumn = lastText.length;
        selectionController.anchor = { line: 1, column: 0 };
        selectionController.selection = { start: { line: 1, column: 0 }, end: { line: lastLine, column: endColumn } };
        syncCustomSelectionClass();
        state.currentLine = lastLine;
        state.currentColumn = endColumn + 1;
        queueRender(true);
        setTimeout(() => focusLine(1, 0), 0);
        reportCursorAndSelection();
    }

    function parseHexClipboard(text) {
        const normalized = String(text || '').replace(/0x/gi, '').replace(/[\s,;:_-]+/g, '');
        if (!normalized || normalized.length % 2 !== 0 || !/^[0-9a-f]+$/i.test(normalized)) return [];
        const bytes = [];
        for (let i = 0; i < normalized.length; i += 2) {
            bytes.push(parseInt(normalized.slice(i, i + 2), 16));
        }
        return bytes;
    }

    function hexEditOffset() {
        const selection = hexEditorMode.selection;
        return selection
            ? Math.max(0, Math.min(Number(selection.startOffset || 0), Number(selection.endOffset || 0)))
            : Math.max(0, Number(hexEditorMode.cursorOffset || 0));
    }

    function submitHexEdit(bytes, offset = hexEditOffset()) {
        if (!Array.isArray(bytes) || bytes.length === 0) return;
        const hex = bytes.map(value => Number(value).toString(16).padStart(2, '0')).join('').toUpperCase();
        post({ type: 'hexEdit', offset, hex });
        post({ type: 'contentChanged' });
        const finalOffset = offset + bytes.length - 1;
        hexEditorMode.selectByte(finalOffset, { clearPendingHighNibble: true });
        queueRender(true);
        reportCursorAndSelection();
    }

    function replaceHexSelectionWithZeros() {
        const selection = hexEditorMode.selection;
        const start = hexEditOffset();
        const length = selection
            ? Math.max(1, Math.abs(Number(selection.endOffset || 0) - Number(selection.startOffset || 0)))
            : 1;
        submitHexEdit(new Array(length).fill(0), start);
    }

    return {
        copySelectionToClipboard,
        cutSelectionToClipboard,
        deleteSelectionOrForward,
        pasteFromClipboard,
        selectAll,
        submitHexEdit
    };
}
