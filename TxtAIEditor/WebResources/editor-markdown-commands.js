export function createMarkdownCommandHandlers(deps) {
    const {
        activeEditableElement,
        cleanDirtyMarker,
        comparePositions,
        focusLine,
        getCaretOffset,
        hasCustomSelection,
        hasTextAt,
        isStandaloneDelimiter,
        lineTextFromElement,
        markDirty,
        normalizeSelection,
        orderedRange,
        post,
        queueRender,
        replaceSelectionWith,
        reportCursorAndSelection,
        state,
        syncCustomSelectionClass,
        writeClipboardText
    } = deps;

    function offsetFromNode(element, node, offset) {
    try {
        const range = document.createRange();
        range.selectNodeContents(element);
        range.setEnd(node, offset);
        return range.toString().replace(/\r\n/g, '\n').replace(/\r/g, '\n').length;
    } catch {
        return getCaretOffset(element);
    }
}

function activeMarkdownRange() {
    if (hasCustomSelection()) return normalizeSelection();

    const element = activeEditableElement();
    const line = Number(element?.dataset.line || state.currentLine || 1);
    const text = state.cache.get(line) ?? (element ? lineTextFromElement(element) : '');
    const fallbackColumn = Math.max(0, Math.min((state.currentColumn || 1) - 1, text.length));
    if (!element) {
        return { start: { line, column: fallbackColumn }, end: { line, column: fallbackColumn } };
    }

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !element.contains(selection.anchorNode)) {
        const caret = Math.max(0, Math.min(getCaretOffset(element), text.length));
        return { start: { line, column: caret }, end: { line, column: caret } };
    }

    const anchor = Math.max(0, Math.min(offsetFromNode(element, selection.anchorNode, selection.anchorOffset), text.length));
    const focus = Math.max(0, Math.min(offsetFromNode(element, selection.focusNode, selection.focusOffset), text.length));
    return orderedRange({ start: { line, column: anchor }, end: { line, column: focus } });
}

function rangeIsCollapsed(range) {
    return range.start.line === range.end.line && range.start.column === range.end.column;
}

function textInRange(range) {
    const safeRange = orderedRange(range);
    const parts = [];
    for (let line = safeRange.start.line; line <= safeRange.end.line; line++) {
        const text = state.cache.get(line) ?? '';
        const start = line === safeRange.start.line ? safeRange.start.column : 0;
        const end = line === safeRange.end.line ? safeRange.end.column : text.length;
        parts.push(text.slice(Math.max(0, start), Math.max(0, end)));
    }
    return parts.join('\n');
}

function replaceMarkdownRange(range, replacement, startOffset = 0, endOffset = startOffset) {
    replaceSelectionWith(orderedRange(range), replacement, { startOffset, endOffset });
}

function setSelectionAfterLineEdits(start, end) {
    state.selectionAnchor = start;
    state.selection = comparePositions(start, end) === 0 ? null : { start, end };
    state.currentLine = end.line;
    state.currentColumn = end.column + 1;
    post({ type: 'contentChanged' });
    queueRender(true);
    setTimeout(() => {
        focusLine(end.line, end.column);
        reportCursorAndSelection();
    }, 0);
}

function applyLineText(lineNumber, text) {
    state.cache.set(lineNumber, text);
    if (!cleanDirtyMarker(lineNumber)) {
        markDirty(lineNumber, 'mod');
    }
    post({ type: 'lineChanged', lineNumber, text });
}

function wrapSelection(opening, closing = opening) {
    const range = activeMarkdownRange();
    const selected = textInRange(range);
    replaceMarkdownRange(
        range,
        opening + selected + closing,
        opening.length,
        opening.length + selected.length
    );
}

function toggleWrappedSelection(opening, closing = opening) {
    const range = activeMarkdownRange();
    const selected = textInRange(range);
    const sameDelimiter = opening === closing;
    const selectedStartsWrapped = selected.length >= opening.length + closing.length &&
        selected.startsWith(opening) &&
        selected.endsWith(closing);
    if (selectedStartsWrapped) {
        const inner = selected.slice(opening.length, selected.length - closing.length);
        replaceMarkdownRange(range, inner, 0, inner.length);
        return;
    }

    if (range.start.line === range.end.line) {
        const line = state.cache.get(range.start.line) ?? '';
        const openingStart = range.start.column - opening.length;
        const hasSurrounding = hasTextAt(line, openingStart, opening) &&
            hasTextAt(line, range.end.column, closing) &&
            (
                rangeIsCollapsed(range) ||
                !sameDelimiter ||
                (
                    isStandaloneDelimiter(line, openingStart, opening) &&
                    isStandaloneDelimiter(line, range.end.column, closing)
                )
            );
        if (hasSurrounding) {
            const surroundingRange = {
                start: { line: range.start.line, column: openingStart },
                end: { line: range.end.line, column: range.end.column + closing.length }
            };
            replaceMarkdownRange(surroundingRange, selected, 0, selected.length);
            return;
        }
    }

    wrapSelection(opening, closing);
}

function selectedMarkdownLineRange(range = activeMarkdownRange()) {
    const safeRange = orderedRange(range);
    const endLine = safeRange.end.column === 0 && safeRange.end.line > safeRange.start.line
        ? safeRange.end.line - 1
        : safeRange.end.line;
    return { range: safeRange, startLine: safeRange.start.line, endLine: Math.max(safeRange.start.line, endLine) };
}

function toggleLinePrefix(prefix) {
    const { range, startLine, endLine } = selectedMarkdownLineRange();
    const shouldRemove = Array.from({ length: endLine - startLine + 1 }, (_, i) => state.cache.get(startLine + i) ?? '')
        .every(line => line.startsWith(prefix));

    for (let line = startLine; line <= endLine; line++) {
        const original = state.cache.get(line) ?? '';
        applyLineText(line, shouldRemove ? original.slice(prefix.length) : prefix + original);
    }

    if (rangeIsCollapsed(range)) {
        const nextColumn = shouldRemove
            ? Math.max(0, range.start.column - Math.min(prefix.length, range.start.column))
            : range.start.column + prefix.length;
        setSelectionAfterLineEdits({ line: range.start.line, column: nextColumn }, { line: range.start.line, column: nextColumn });
    } else {
        const endText = state.cache.get(endLine) ?? '';
        setSelectionAfterLineEdits({ line: startLine, column: 0 }, { line: endLine, column: endText.length });
    }
}

function headingPrefix(line) {
    const match = line.match(/^(#{1,6})(?: |$)/);
    if (!match) return null;
    return {
        level: match[1].length,
        length: match[0].length
    };
}

function cycleHeadingLine(line) {
    const prefix = headingPrefix(line);
    if (!prefix) return '# ' + line;
    if (prefix.level < 6) return '#' + line;
    return line.slice(prefix.length);
}

function cycleHeadingPrefix() {
    const { range, startLine, endLine } = selectedMarkdownLineRange();
    const originalFirstLine = state.cache.get(startLine) ?? '';
    const firstPrefix = headingPrefix(originalFirstLine);
    for (let line = startLine; line <= endLine; line++) {
        applyLineText(line, cycleHeadingLine(state.cache.get(line) ?? ''));
    }

    if (rangeIsCollapsed(range)) {
        const delta = !firstPrefix
            ? 2
            : firstPrefix.level < 6
                ? 1
                : -Math.min(firstPrefix.length, range.start.column);
        const nextColumn = Math.max(0, range.start.column + delta);
        setSelectionAfterLineEdits({ line: range.start.line, column: nextColumn }, { line: range.start.line, column: nextColumn });
    } else {
        const endText = state.cache.get(endLine) ?? '';
        setSelectionAfterLineEdits({ line: startLine, column: 0 }, { line: endLine, column: endText.length });
    }
}

function findInlineCodeRange(range) {
    if (range.start.line !== range.end.line) return null;
    const line = state.cache.get(range.start.line) ?? '';
    const selected = textInRange(range);
    if (selected.length >= 2 && selected.startsWith('`') && selected.endsWith('`')) {
        return {
            range,
            content: selected.slice(1, -1)
        };
    }

    const opening = line.lastIndexOf('`', Math.max(0, range.start.column - 1));
    const closing = line.indexOf('`', range.end.column);
    if (opening >= 0 && closing >= range.end.column &&
        isStandaloneDelimiter(line, opening, '`') &&
        isStandaloneDelimiter(line, closing, '`')) {
        return {
            range: {
                start: { line: range.start.line, column: opening },
                end: { line: range.end.line, column: closing + 1 }
            },
            content: line.slice(opening + 1, closing)
        };
    }
    return null;
}

function findCodeBlockRange(range) {
    const selected = textInRange(range);
    if (selected.startsWith('```\n') && selected.endsWith('\n```')) {
        return { range, content: selected.slice(4, -4) };
    }

    let openingLine = -1;
    for (let line = range.start.line; line >= 1; line--) {
        const text = state.cache.get(line);
        if (text === undefined) break;
        if (text.trim() === '```') {
            openingLine = line;
            break;
        }
    }
    if (openingLine < 0) return null;

    let closingLine = -1;
    for (let line = openingLine + 1; line <= state.lineCount; line++) {
        const text = state.cache.get(line);
        if (text === undefined) break;
        if (text.trim() === '```') {
            closingLine = line;
            break;
        }
    }
    if (closingLine < 0 || range.end.line > closingLine) return null;

    const contentLines = [];
    for (let line = openingLine + 1; line < closingLine; line++) {
        contentLines.push(state.cache.get(line) ?? '');
    }
    return {
        range: {
            start: { line: openingLine, column: 0 },
            end: { line: closingLine, column: (state.cache.get(closingLine) ?? '').length }
        },
        content: contentLines.join('\n')
    };
}

function cycleCodeFormatting() {
    const range = activeMarkdownRange();
    const codeBlock = findCodeBlockRange(range);
    if (codeBlock) {
        replaceMarkdownRange(codeBlock.range, codeBlock.content, 0, codeBlock.content.length);
        return;
    }

    const inlineCode = findInlineCodeRange(range);
    if (inlineCode) {
        const replacement = '```\n' + inlineCode.content + '\n```';
        replaceMarkdownRange(inlineCode.range, replacement, 4, 4 + inlineCode.content.length);
        return;
    }

    toggleWrappedSelection('`');
}

const markdownTextColorOpeningRegex = /<span\s+style\s*=\s*["']\s*color\s*:\s*#[0-9a-fA-F]{6}\s*;?\s*["']\s*>/ig;

function findMarkdownTextColorRange(range) {
    if (range.start.line !== range.end.line) return null;
    const line = state.cache.get(range.start.line) ?? '';
    markdownTextColorOpeningRegex.lastIndex = 0;
    let match;
    while ((match = markdownTextColorOpeningRegex.exec(line)) !== null) {
        const contentStart = match.index + match[0].length;
        const closingStart = line.toLowerCase().indexOf('</span>', contentStart);
        if (closingStart < 0) continue;
        const closingEnd = closingStart + 7;
        if (range.start.column >= match.index && range.end.column <= closingEnd) {
            const colorMatch = /color\s*:\s*(#[0-9a-fA-F]{6})/i.exec(match[0]);
            return {
                fullRange: {
                    start: { line: range.start.line, column: match.index },
                    end: { line: range.end.line, column: closingEnd }
                },
                openingRange: {
                    start: { line: range.start.line, column: match.index },
                    end: { line: range.end.line, column: contentStart }
                },
                content: line.slice(contentStart, closingStart),
                contentStart,
                colorHex: colorMatch ? colorMatch[1].toUpperCase() : ''
            };
        }
    }
    return null;
}

function applyMarkdownTextColor(color) {
    const range = activeMarkdownRange();
    const colorHex = color || '#E53935';
    const colorRange = findMarkdownTextColorRange(range);
    if (colorHex.toLowerCase() === '#111111') {
        if (colorRange) {
            replaceMarkdownRange(colorRange.fullRange, colorRange.content, 0, colorRange.content.length);
        }
        return;
    }

    if (colorRange) {
        if (colorRange.colorHex.toLowerCase() === colorHex.toLowerCase()) {
            replaceMarkdownRange(colorRange.fullRange, colorRange.content, 0, colorRange.content.length);
            return;
        }
        const opening = `<span style="color: ${colorHex}">`;
        replaceMarkdownRange(colorRange.openingRange, opening, opening.length, opening.length);
        return;
    }

    toggleWrappedSelection(`<span style="color: ${colorHex}">`, '</span>');
}

function parseMarkdownLinkAt(line, openBracketIndex) {
    if (openBracketIndex < 0 || line[openBracketIndex] !== '[') return null;
    const textEnd = line.indexOf('](', openBracketIndex + 1);
    if (textEnd < 0) return null;
    const urlStart = textEnd + 2;
    const fullEnd = line.indexOf(')', urlStart);
    if (fullEnd < 0) return null;
    return {
        fullStart: openBracketIndex,
        textStart: openBracketIndex + 1,
        textEnd,
        urlStart,
        urlEnd: fullEnd,
        fullEnd: fullEnd + 1
    };
}

function findMarkdownLinkRange(range) {
    if (range.start.line !== range.end.line) return null;
    const line = state.cache.get(range.start.line) ?? '';
    for (let index = Math.min(range.start.column, line.length - 1); index >= 0; index--) {
        if (line[index] !== '[') continue;
        const link = parseMarkdownLinkAt(line, index);
        if (link && range.start.column >= link.fullStart && range.end.column <= link.fullEnd) {
            return {
                range: {
                    start: { line: range.start.line, column: link.fullStart },
                    end: { line: range.end.line, column: link.fullEnd }
                },
                text: line.slice(link.textStart, link.textEnd),
                urlStartOffset: link.urlStart - link.fullStart,
                urlEndOffset: link.urlEnd - link.fullStart
            };
        }
    }
    return null;
}

function toggleMarkdownLink() {
    const range = activeMarkdownRange();
    const link = findMarkdownLinkRange(range);
    if (link) {
        replaceMarkdownRange(link.range, link.text, 0, link.text.length);
        return;
    }

    const selected = textInRange(range);
    const linkText = selected || '링크';
    const replacement = `[${linkText}](url)`;
    if (selected) {
        const urlStart = linkText.length + 3;
        replaceMarkdownRange(range, replacement, urlStart, urlStart + 3);
    } else {
        replaceMarkdownRange(range, replacement, 1, 1 + linkText.length);
    }
}

function parseMarkdownImageAt(line, bangIndex) {
    if (bangIndex < 0 || line.slice(bangIndex, bangIndex + 2) !== '![') return null;
    const textEnd = line.indexOf('](', bangIndex + 2);
    if (textEnd < 0) return null;
    const urlStart = textEnd + 2;
    const fullEnd = line.indexOf(')', urlStart);
    if (fullEnd < 0) return null;
    return {
        fullStart: bangIndex,
        textStart: bangIndex + 2,
        textEnd,
        urlStart,
        urlEnd: fullEnd,
        fullEnd: fullEnd + 1
    };
}

function findMarkdownImageRange(range) {
    if (range.start.line !== range.end.line) return null;
    const line = state.cache.get(range.start.line) ?? '';
    for (let index = Math.min(range.start.column, line.length - 1); index >= 0; index--) {
        if (line[index] !== '!') continue;
        const image = parseMarkdownImageAt(line, index);
        if (image && range.start.column >= image.fullStart && range.end.column <= image.fullEnd) {
            return {
                range: {
                    start: { line: range.start.line, column: image.fullStart },
                    end: { line: range.end.line, column: image.fullEnd }
                },
                text: line.slice(image.textStart, image.textEnd),
                urlStartOffset: image.urlStart - image.fullStart,
                urlEndOffset: image.urlEnd - image.fullStart
            };
        }
    }
    return null;
}

function toggleMarkdownImage() {
    const range = activeMarkdownRange();
    const image = findMarkdownImageRange(range);
    if (image) {
        replaceMarkdownRange(image.range, image.text, 0, image.text.length);
        return;
    }

    const selected = textInRange(range);
    const altText = selected || '이미지';
    const replacement = `![${altText}](url)`;
    if (selected) {
        const urlStart = altText.length + 4;
        replaceMarkdownRange(range, replacement, urlStart, urlStart + 3);
    } else {
        replaceMarkdownRange(range, replacement, 2, 2 + altText.length);
    }
}

function nextMarkdownArrow(current) {
    const arrows = ['→', '←', '↑', '↓'];
    const index = arrows.indexOf(current);
    return index >= 0 ? arrows[(index + 1) % arrows.length] : '→';
}

function cycleOrInsertArrow() {
    const range = activeMarkdownRange();
    const selected = textInRange(range);
    if (!rangeIsCollapsed(range) && ['→', '←', '↑', '↓'].includes(selected)) {
        const next = nextMarkdownArrow(selected);
        replaceMarkdownRange(range, next, next.length, next.length);
        return;
    }

    if (range.start.line === range.end.line && range.start.column > 0) {
        const line = state.cache.get(range.start.line) ?? '';
        const previous = line.slice(range.start.column - 1, range.start.column);
        if (['→', '←', '↑', '↓'].includes(previous)) {
            const next = nextMarkdownArrow(previous);
            replaceMarkdownRange({
                start: { line: range.start.line, column: range.start.column - 1 },
                end: { line: range.start.line, column: range.start.column }
            }, next, next.length, next.length);
            return;
        }
    }

    replaceMarkdownRange(range, '→', 1, 1);
}

function buildMarkdownTable(size) {
    const header = '|' + Array.from({ length: size }, () => '  |').join('');
    const separator = '| ' + Array.from({ length: size }, () => '---').join(' | ') + ' |';
    const bodyRows = Array.from({ length: size - 1 }, () => header);
    return [header, separator, ...bodyRows].join('\n');
}

function findGeneratedMarkdownTable(range) {
    for (const size of [3, 2]) {
        const tableLines = buildMarkdownTable(size).split('\n');
        const firstCandidate = Math.max(1, range.start.line - tableLines.length + 1);
        for (let startLine = firstCandidate; startLine <= range.start.line; startLine++) {
            let matches = true;
            for (let i = 0; i < tableLines.length; i++) {
                if ((state.cache.get(startLine + i) ?? '') !== tableLines[i]) {
                    matches = false;
                    break;
                }
            }
            const endLine = startLine + tableLines.length - 1;
            if (matches && range.end.line <= endLine) {
                return { startLine, endLine, size };
            }
        }
    }
    return null;
}

function cycleMarkdownTable() {
    const range = activeMarkdownRange();
    const existing = findGeneratedMarkdownTable(range);
    if (existing) {
        if (existing.size === 2) {
            const existingRange = {
                start: { line: existing.startLine, column: 0 },
                end: { line: existing.endLine, column: (state.cache.get(existing.endLine) ?? '').length }
            };
            const table = buildMarkdownTable(3);
            replaceMarkdownRange(existingRange, table, 2, 2);
        } else {
            if (existing.endLine < state.lineCount) {
                replaceMarkdownRange({
                    start: { line: existing.startLine, column: 0 },
                    end: { line: existing.endLine + 1, column: 0 }
                }, '', 0, 0);
            } else if (existing.startLine > 1) {
                const previousText = state.cache.get(existing.startLine - 1) ?? '';
                replaceMarkdownRange({
                    start: { line: existing.startLine - 1, column: previousText.length },
                    end: { line: existing.endLine, column: (state.cache.get(existing.endLine) ?? '').length }
                }, '', 0, 0);
            } else {
                replaceMarkdownRange({
                    start: { line: existing.startLine, column: 0 },
                    end: { line: existing.endLine, column: (state.cache.get(existing.endLine) ?? '').length }
                }, '', 0, 0);
            }
        }
        return;
    }

    const endLineText = state.cache.get(range.end.line) ?? '';
    const needsLeadingBreak = range.start.column > 0;
    const needsTrailingBreak = range.end.column < endLineText.length;
    const table = buildMarkdownTable(2);
    const replacement = `${needsLeadingBreak ? '\n' : ''}${table}${needsTrailingBreak ? '\n' : ''}`;
    const firstCellOffset = (needsLeadingBreak ? 1 : 0) + 2;
    replaceMarkdownRange(range, replacement, firstCellOffset, firstCellOffset);
}

function parseCsvLine(line) {
    const cells = [];
    let currentCell = "";
    let inQuotes = false;
    
    for (let i = 0; i < line.length; i++) {
        const char = line[i];
        
        if (char === '"') {
            inQuotes = !inQuotes;
            currentCell += char;
        } else if (inQuotes) {
            currentCell += char;
        } else {
            if (char === ' ' || char === ',' || char === '-' || char === ':') {
                if (currentCell.length > 0) {
                    cells.push(currentCell);
                    currentCell = "";
                }
            } else {
                currentCell += char;
            }
        }
    }
    
    if (currentCell.length > 0) {
        cells.push(currentCell);
    }
    
    return cells;
}

function convertSelectionToMarkdownTable() {
    const range = activeMarkdownRange();
    if (rangeIsCollapsed(range)) {
        return;
    }
    const selectedText = textInRange(range);
    const lines = selectedText.split(/\r?\n/);
    
    const rows = [];
    let maxColumns = 0;
    
    for (const line of lines) {
        const protectedLine = line.replace(/\b\d{4}-\d{1,2}-\d{1,2}\b/g, m => m.replace(/-/g, '\uE000'));
        const cleaned = protectedLine.replace(/^[,:\- ]+|[,:\- ]+$/g, '');
        if (!cleaned) {
            continue;
        }
        const cells = parseCsvLine(cleaned);
        rows.push(cells);
        if (cells.length > maxColumns) {
            maxColumns = cells.length;
        }
    }
    
    if (maxColumns === 0) {
        return;
    }
    
    const headerCells = Array.from({ length: maxColumns }, () => '');
    const headerRow = '| ' + headerCells.join(' | ') + ' |';
    
    const separatorCells = Array.from({ length: maxColumns }, () => '---');
    const separatorRow = '| ' + separatorCells.join(' | ') + ' |';
    
    const dataRows = rows.map(cells => {
        const paddedCells = Array.from({ length: maxColumns }, (_, i) => {
            if (i < cells.length) {
                let cellText = cells[i].trim();
                if (cellText.startsWith('"') && cellText.endsWith('"')) {
                    cellText = cellText.slice(1, -1);
                }
                return cellText.replace(/\uE000/g, '-');
            }
            return '';
        });
        return '| ' + paddedCells.join(' | ') + ' |';
    });
    
    const tableText = [headerRow, separatorRow, ...dataRows].join('\n');
    
    replaceMarkdownRange(range, tableText, 0, tableText.length);
}

async function cutCurrentMarkdownLine() {
    const range = activeMarkdownRange();
    const lineNumber = range.start.line;
    const lineText = state.cache.get(lineNumber) ?? '';
    await writeClipboardText(lineText);

    if (state.lineCount <= 1) {
        applyLineText(1, '');
        setSelectionAfterLineEdits({ line: 1, column: 0 }, { line: 1, column: 0 });
        return;
    }

    if (lineNumber < state.lineCount) {
        replaceMarkdownRange({
            start: { line: lineNumber, column: 0 },
            end: { line: lineNumber + 1, column: 0 }
        }, '', 0, 0);
    } else {
        const previousText = state.cache.get(lineNumber - 1) ?? '';
        replaceMarkdownRange({
            start: { line: lineNumber - 1, column: previousText.length },
            end: { line: lineNumber, column: lineText.length }
        }, '', 0, 0);
    }
}

function applyMarkdownCommand(command, color) {
    switch (command) {
        case 'bold': toggleWrappedSelection('**'); break;
        case 'italic': toggleWrappedSelection('*'); break;
        case 'underline': toggleWrappedSelection('<u>', '</u>'); break;
        case 'highlight': toggleWrappedSelection('=='); break;
        case 'inlineCode': toggleWrappedSelection('`'); break;
        case 'math': toggleWrappedSelection('$$', '$$'); break;
        case 'quote': toggleLinePrefix('> '); break;
        case 'ul': toggleLinePrefix('- '); break;
        case 'task': toggleLinePrefix('- [ ] '); break;
        case 'image': toggleMarkdownImage(); break;
        case 'link': toggleMarkdownLink(); break;
        case 'textColor': applyMarkdownTextColor(color); break;
        case 'heading': cycleHeadingPrefix(); break;
        case 'arrow': cycleOrInsertArrow(); break;
        case 'fontIncrease': toggleWrappedSelection('<big>', '</big>'); break;
        case 'fontDecrease': toggleWrappedSelection('<small>', '</small>'); break;
        case 'cutLine': cutCurrentMarkdownLine(); break;
        case 'table': cycleMarkdownTable(); break;
        case 'convertToTable': convertSelectionToMarkdownTable(); break;
    }
}

    return { applyMarkdownCommand };
}
