function createTextCommandActions({
    beginEditTransaction,
    cleanDirtyMarker,
    endEditTransaction,
    focusLine,
    hasCustomSelection,
    insertTextAtCaret,
    lineCommentSyntax,
    markDirty,
    normalizeSelection,
    post,
    queueRender,
    replaceSelectionWith,
    selectedLineRange,
    selectedText,
    state
}) {
    function toggleCommentForLine(lineNumber, syntax, shouldUncomment) {
        const original = state.cache.get(lineNumber) ?? '';
        const indent = original.match(/^\s*/)?.[0] || '';
        const body = original.slice(indent.length);
        let next = original;

        if (shouldUncomment && body.trim().length === 0) {
            return;
        }

        if (syntax.prefix) {
            next = shouldUncomment && body.startsWith(syntax.prefix)
                ? indent + body.slice(syntax.prefix.length)
                : indent + syntax.prefix + body;
        } else {
            const { blockStart, blockEnd } = syntax;
            const trimmed = body.trim();
            if (shouldUncomment && trimmed.startsWith(blockStart) && trimmed.endsWith(blockEnd)) {
                const leading = body.slice(0, body.indexOf(trimmed));
                const inner = trimmed.slice(blockStart.length, trimmed.length - blockEnd.length);
                next = indent + leading + inner;
            } else {
                next = indent + blockStart + body + blockEnd;
            }
        }

        state.cache.set(lineNumber, next);
        post({ type: 'lineChanged', lineNumber, text: next });
    }

    function toggleComment() {
        if (state.readOnly) return;
        const { startLine, endLine } = selectedLineRange();
        const syntax = lineCommentSyntax();
        const shouldUncomment = (() => {
            for (let line = startLine; line <= endLine; line++) {
                const text = state.cache.get(line) ?? '';
                const body = text.slice((text.match(/^\s*/)?.[0] || '').length);
                if (syntax.prefix && body.length > 0 && !body.startsWith(syntax.prefix)) return false;
                if (!syntax.prefix) {
                    const trimmed = body.trim();
                    if (trimmed.length > 0 && !(trimmed.startsWith(syntax.blockStart) && trimmed.endsWith(syntax.blockEnd))) {
                        return false;
                    }
                }
            }
            return true;
        })();

        beginEditTransaction();
        try {
            for (let line = startLine; line <= endLine; line++) {
                toggleCommentForLine(line, syntax, shouldUncomment);
            }

            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }

        queueRender(true);
        setTimeout(() => focusLine(startLine, 0), 0);
    }

    function changeLineIndent(direction) {
        if (state.readOnly) return;

        const { startLine, endLine } = selectedLineRange();
        const indentText = ' '.repeat(Math.max(1, state.tabSize || 4));
        let changed = false;

        beginEditTransaction();
        try {
            for (let line = startLine; line <= endLine; line++) {
                const original = state.cache.get(line);
                if (original === undefined) continue;

                let next = original;
                if (direction > 0) {
                    next = indentText + original;
                } else if (original.startsWith('\t')) {
                    next = original.slice(1);
                } else {
                    const leadingSpaces = original.match(/^ +/)?.[0].length || 0;
                    const removeCount = Math.min(indentText.length, leadingSpaces);
                    if (removeCount > 0) {
                        next = original.slice(removeCount);
                    }
                }

                if (next !== original) {
                    state.cache.set(line, next);
                    if (!cleanDirtyMarker(line)) {
                        markDirty(line, 'mod');
                    }
                    post({ type: 'lineChanged', lineNumber: line, text: next });
                    changed = true;
                }
            }

            if (!changed) return;

            post({ type: 'contentChanged' });
        } finally {
            endEditTransaction();
        }
        queueRender(true);
        setTimeout(() => focusLine(startLine, 0), 0);
    }

    function handleLineSortingAndCleanup(action) {
        if (state.readOnly) return;

        let startLine;
        let endLine;

        if (!hasCustomSelection()) {
            startLine = 1;
            endLine = state.lineCount;
        } else {
            const range = selectedLineRange();
            startLine = range.startLine;
            endLine = range.endLine;
        }

        const lineSelection = {
            start: { line: startLine, column: 0 },
            end: { line: endLine, column: (state.cache.get(endLine) || '').length }
        };

        const lines = [];
        for (let i = startLine; i <= endLine; i++) {
            lines.push(state.cache.get(i) || '');
        }

        let newLines = [...lines];

        switch (action) {
            case 'sortAsc':
                newLines.sort((a, b) => a.localeCompare(b));
                break;
            case 'sortDesc':
                newLines.sort((a, b) => b.localeCompare(a));
                break;
            case 'removeDuplicates':
                {
                    const seen = new Set();
                    newLines = lines.filter(line => {
                        if (seen.has(line)) return false;
                        seen.add(line);
                        return true;
                    });
                }
                break;
            case 'removeEmptyLines':
                newLines = lines.filter(line => line.trim() !== '');
                break;
            case 'collapseConsecutiveEmptyLines':
                newLines = [];
                {
                    let prevEmpty = false;
                    for (let i = 0; i < lines.length; i++) {
                        const isEmpty = lines[i].trim() === '';
                        if (isEmpty) {
                            if (!prevEmpty) {
                                newLines.push(lines[i]);
                                prevEmpty = true;
                            }
                        } else {
                            newLines.push(lines[i]);
                            prevEmpty = false;
                        }
                    }
                }
                break;
            case 'trimSpaces':
                newLines = lines.map(line => line.trim());
                break;
        }

        const transformedText = newLines.join('\n');
        replaceSelectionWith(lineSelection, transformedText);
    }

    function handleTextConversion(action) {
        if (state.readOnly) return;

        if (action === 'insertDivider') {
            const divider = '\n---\n';
            if (hasCustomSelection()) {
                const sel = normalizeSelection();
                if (sel) replaceSelectionWith(sel, divider);
            } else {
                insertTextAtCaret(divider);
            }
            return;
        }

        const { selection, text } = selectedOrWholeDocumentText();
        if (!selection || !text) return;

        let transformed = text;

        switch (action) {
            case 'toUpperCase':
                transformed = text.toUpperCase();
                break;
            case 'toLowerCase':
                transformed = text.toLowerCase();
                break;
            case 'toSentenceCase':
                transformed = text.replace(/((?:^|[.!?]\s+)\s*)(\S)/g, (match, p1, p2) => p1 + p2.toUpperCase());
                break;
            case 'toTitleCase':
                transformed = text.toLowerCase().replace(/\b([a-z])/g, match => match.toUpperCase());
                break;
            case 'urlEncode':
                try {
                    transformed = encodeURIComponent(text);
                } catch (e) {
                    transformed = text;
                }
                break;
            case 'urlDecode':
                try {
                    transformed = decodeURIComponent(text);
                } catch (e) {
                    transformed = text;
                }
                break;
            case 'base64Encode':
                try {
                    transformed = btoa(encodeURIComponent(text).replace(/%([0-9A-F]{2})/g, (match, p1) => {
                        return String.fromCharCode(parseInt(p1, 16));
                    }));
                } catch (e) {
                    transformed = text;
                }
                break;
            case 'base64Decode':
                try {
                    transformed = decodeURIComponent(atob(text).split('').map(c => {
                        return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
                    }).join(''));
                } catch (e) {
                    transformed = text;
                }
                break;
            case 'hexToDec':
                try {
                    const trimmed = text.trim();
                    const cleaned = trimmed.replace(/^(0x|0X)/, '');
                    transformed = BigInt('0x' + cleaned).toString();
                } catch (e) {
                    transformed = text;
                }
                break;
            case 'decToHex':
                try {
                    const trimmed = text.trim();
                    if (/^-?\d+$/.test(trimmed)) {
                        const dec = BigInt(trimmed);
                        transformed = dec < 0n
                            ? '-' + (-dec).toString(16).toUpperCase()
                            : '0x' + dec.toString(16).toUpperCase();
                    }
                } catch (e) {
                    transformed = text;
                }
                break;
        }

        replaceSelectionWith(selection, transformed);
    }

    function handleFormatText() {
        if (state.readOnly) return;

        const { selection, text } = selectedOrWholeDocumentText();
        if (!selection || !text) return;

        let transformed = text;
        let lines = text.split('\n');

        lines = lines.map(line => line.trimEnd());

        if (state.language === 'markdown') {
            lines = lines.map(line => {
                const headerMatch = line.match(/^(#{1,6})([^\s#].*)$/);
                if (headerMatch) {
                    return headerMatch[1] + ' ' + headerMatch[2].trim();
                }
                const listMatch = line.match(/^(\s*)([-*+]|\d+\.)([^\s].*)$/);
                if (listMatch) {
                    return listMatch[1] + listMatch[2] + ' ' + listMatch[3].trim();
                }
                const quoteMatch = line.match(/^(\s*)(>+)([^\s>].*)$/);
                if (quoteMatch) {
                    return quoteMatch[1] + quoteMatch[2] + ' ' + quoteMatch[3].trim();
                }
                return line;
            });
            transformed = lines.join('\n');
        } else if (state.language === 'json') {
            try {
                transformed = JSON.stringify(JSON.parse(text), null, state.tabSize || 4);
            } catch (e) {
                transformed = lines.join('\n');
            }
        } else {
            transformed = lines.join('\n');
        }

        replaceSelectionWith(selection, transformed);
    }

    function selectedOrWholeDocumentText() {
        if (hasCustomSelection()) {
            return {
                selection: normalizeSelection(),
                text: selectedText()
            };
        }

        const selection = {
            start: { line: 1, column: 0 },
            end: { line: state.lineCount, column: (state.cache.get(state.lineCount) || '').length }
        };
        const parts = [];
        for (let i = 1; i <= state.lineCount; i++) {
            parts.push(state.cache.get(i) || '');
        }

        return {
            selection,
            text: parts.join('\n')
        };
    }

    return {
        changeLineIndent,
        handleFormatText,
        handleLineSortingAndCleanup,
        handleTextConversion,
        toggleComment
    };
}

export { createTextCommandActions };
