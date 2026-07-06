export function createHostStreamInsertCommands({
    activeEditableElement,
    beginEditTransaction,
    cleanDirtyMarker,
    clearCustomSelectionVisuals,
    endEditTransaction,
    focusLine,
    getCaretOffset,
    invalidateMeasuredLineHeightsAround,
    lineTextFromElement,
    markDirty,
    measureRenderedRows,
    post,
    queueRender,
    reportCursorAndSelection,
    setupVirtualHeight,
    shiftCachedLines,
    state,
    syncCustomSelectionClass
}) {
    let hostStreamInsert = null;

    function clearHostStreamSelection() {
        state.selection = null;
        state.selectionAnchor = null;
        clearCustomSelectionVisuals();
        try {
            window.getSelection()?.removeAllRanges();
        } catch { }
        syncCustomSelectionClass();
    }

    function beginHostStreamInsert() {
        let targetLine = Math.max(1, Math.min(Number(state.currentLine || 1), Math.max(1, state.lineCount || 1)));
        let targetColumn = Math.max(0, Number(state.currentColumn || 1) - 1);

        const active = activeEditableElement();
        if (active && active.getAttribute('contenteditable') === 'true') {
            const activeLine = Number(active.dataset.line || targetLine || 1);
            const activeText = lineTextFromElement(active);
            if (activeLine >= 1) {
                targetLine = activeLine;
                state.cache.set(activeLine, activeText);
                targetColumn = Math.max(0, Math.min(getCaretOffset(active), activeText.length));
            }
        } else {
            const currentText = state.cache.get(targetLine) ?? '';
            targetColumn = Math.max(0, Math.min(targetColumn, currentText.length));
        }

        clearHostStreamSelection();
        hostStreamInsert = { line: targetLine, column: targetColumn };
        state.currentLine = targetLine;
        state.currentColumn = targetColumn + 1;
        reportCursorAndSelection();
    }

    function insertHostStreamText(text) {
        const normalized = String(text || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        if (!normalized) return;
        if (!hostStreamInsert) beginHostStreamInsert();

        const targetLine = Math.max(1, Math.min(Number(hostStreamInsert.line || 1), Math.max(1, state.lineCount || 1)));
        const currentText = state.cache.get(targetLine) ?? '';
        const caret = Math.max(0, Math.min(Number(hostStreamInsert.column || 0), currentText.length));
        const before = currentText.slice(0, caret);
        const after = currentText.slice(caret);

        beginEditTransaction();
        try {
            if (normalized.includes('\n')) {
                const parts = normalized.split('\n');
                const insertedCount = parts.length - 1;
                const firstLine = before + parts[0];
                const lastLineNumber = targetLine + insertedCount;

                state.cache.set(targetLine, firstLine);
                shiftCachedLines(targetLine + 1, insertedCount);
                if (!cleanDirtyMarker(targetLine)) {
                    markDirty(targetLine, 'mod');
                }
                post({ type: 'lineChanged', lineNumber: targetLine, text: firstLine });

                for (let i = 1; i < parts.length; i++) {
                    const nextText = i === parts.length - 1 ? parts[i] + after : parts[i];
                    const nextLineNumber = targetLine + i;
                    state.cache.set(nextLineNumber, nextText);
                    state.dirtyLines.set(nextLineNumber, 'add');
                    post({ type: 'insertLine', lineNumber: nextLineNumber, text: nextText });
                }

                state.lineCount += insertedCount;
                hostStreamInsert = {
                    line: lastLineNumber,
                    column: parts[parts.length - 1].length
                };
                state.currentLine = lastLineNumber;
                state.currentColumn = hostStreamInsert.column + 1;
                setupVirtualHeight();
            } else {
                const nextText = before + normalized + after;
                state.cache.set(targetLine, nextText);
                state.cacheVersion++;
                invalidateMeasuredLineHeightsAround(targetLine);
                if (!cleanDirtyMarker(targetLine)) {
                    markDirty(targetLine, 'mod');
                }
                post({ type: 'lineChanged', lineNumber: targetLine, text: nextText });

                hostStreamInsert = {
                    line: targetLine,
                    column: caret + normalized.length
                };
                state.currentLine = targetLine;
                state.currentColumn = hostStreamInsert.column + 1;
            }

            clearHostStreamSelection();
            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }

        if (state.wordWrap) {
            measureRenderedRows(false);
        }
        queueRender(true);
    }

    function endHostStreamInsert() {
        if (!hostStreamInsert) return;

        const targetLine = hostStreamInsert.line;
        const targetColumn = hostStreamInsert.column;
        hostStreamInsert = null;
        queueRender(true);
        setTimeout(() => {
            focusLine(targetLine, targetColumn);
            reportCursorAndSelection();
        }, 0);
    }

    return {
        beginHostStreamInsert,
        endHostStreamInsert,
        insertHostStreamText
    };
}
