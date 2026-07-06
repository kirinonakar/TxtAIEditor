export function createClipboardCommandHandlers({
    activeEditableElement,
    copyCsvSelectionToClipboard,
    cutCsvSelectionToClipboard,
    commitLine,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    insertTextAtCaret,
    normalizeSelection,
    queueRender,
    readClipboardText,
    replaceSelectionWith,
    reportCursorAndSelection,
    selectedText,
    state,
    syncCustomSelectionClass,
    writeClipboardText
}) {
    function deleteSelectionOrForward() {
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
        if (state.csvTableEnabled) {
            return await cutCsvSelectionToClipboard();
        }

        const text = selectedText();
        if (!text) return false;
        const copied = await writeClipboardText(text);
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
        if (state.csvTableEnabled) {
            return await copyCsvSelectionToClipboard();
        }

        const text = selectedText();
        if (!text) return false;
        return await writeClipboardText(text);
    }

    async function pasteFromClipboard() {
        if (state.readOnly) return;
        const text = await readClipboardText();
        if (text) insertTextAtCaret(text);
    }

    function selectAll() {
        const lastLine = state.lineCount;
        const lastText = state.cache.get(lastLine) || '';
        const endColumn = lastText.length;
        state.selectionAnchor = { line: 1, column: 0 };
        state.selection = { start: { line: 1, column: 0 }, end: { line: lastLine, column: endColumn } };
        syncCustomSelectionClass();
        state.currentLine = lastLine;
        state.currentColumn = endColumn + 1;
        queueRender(true);
        setTimeout(() => focusLine(1, 0), 0);
        reportCursorAndSelection();
    }

    return {
        copySelectionToClipboard,
        cutSelectionToClipboard,
        deleteSelectionOrForward,
        pasteFromClipboard,
        selectAll
    };
}
