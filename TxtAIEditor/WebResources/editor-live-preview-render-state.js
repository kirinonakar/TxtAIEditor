// Keep a buffer in normal document flow so compositor scrolling can expose
// already painted rows before another JavaScript render is needed.
export function livePreviewLayoutRange(previous, first, last, lineCount, layoutKey, overscan = 80) {
    const guard = Math.min(20, Math.floor(overscan / 2));
    const requiredStart = Math.max(1, first - guard);
    const requiredEnd = Math.min(lineCount, last + guard);
    if (previous?.key === layoutKey && previous.start <= requiredStart && previous.end >= requiredEnd) {
        return previous;
    }
    return {
        key: layoutKey,
        start: Math.max(1, first - overscan),
        end: Math.min(lineCount, last + overscan)
    };
}

// Compare the generated markup, rather than innerHTML: Mermaid, image loading,
// list gutters and the editable selection may have enriched the live DOM.
export function patchLivePreviewRows(viewport, rows, markupByRow, preservedRows = new Set()) {
    const existing = new Map([...viewport.children].map(row => [Number(row.dataset.line), row]));
    const wantedLines = new Set(rows.map(row => row.line));
    const desired = new Set();
    let changed = false;
    // Remove the departed prefix first. Moving a retained contenteditable past
    // that prefix would disconnect its selection even though its HTML matches.
    for (const [line, row] of existing) {
        if (!wantedLines.has(line)) {
            row.remove();
            existing.delete(line);
            changed = true;
        }
    }
    let cursor = viewport.firstChild;
    for (const { line, html, pending } of rows) {
        let row = existing.get(line);
        if (!row || (!pending && !preservedRows.has(row) && markupByRow.get(row) !== html)) {
            const template = viewport.ownerDocument.createElement('template');
            template.innerHTML = html;
            const replacement = template.content.firstElementChild;
            if (row) {
                if (cursor === row) cursor = row.nextSibling;
                row.replaceWith(replacement);
            }
            row = replacement;
            markupByRow.set(row, html);
            changed = true;
        }
        desired.add(row);
        if (row !== cursor && !preservedRows.has(row)) {
            viewport.insertBefore(row, cursor);
            changed = true;
        }
        cursor = row.nextSibling;
    }
    for (const row of existing.values()) {
        if (!desired.has(row) && row.parentNode === viewport) {
            row.remove();
            changed = true;
        }
    }
    return changed;
}
