// Retain raw cell text/padding so structure edits never rewrite inline Markdown,
// HTML, escaped pipes, or the alignment separator of an unrelated column.
export function parseMarkdownTableRow(source) {
    const text = String(source ?? '');
    const pipes = [];
    for (let index = 0; index < text.length; index++) {
        if (text[index] !== '|') continue;
        let slashes = 0;
        for (let previous = index - 1; previous >= 0 && text[previous] === '\\'; previous--) slashes++;
        if (slashes % 2 === 0) pipes.push(index);
    }
    if (pipes.length < 2 || text.slice(0, pipes[0]).trim() || text.slice(pipes.at(-1) + 1).trim()) return null;
    const cells = pipes.slice(1).map((pipe, index) => ({
        text: text.slice(pipes[index] + 1, pipe), start: pipes[index] + 1, end: pipe
    }));
    return { source: text, prefix: text.slice(0, pipes[0] + 1), suffix: text.slice(pipes.at(-1)), cells };
}

export function readMarkdownTable(startLine, lineCount, getLine) {
    const header = parseMarkdownTableRow(getLine(startLine));
    const separator = parseMarkdownTableRow(getLine(startLine + 1));
    if (!header || !separator || header.cells.length !== separator.cells.length ||
        !separator.cells.every(cell => /^:?-{3,}:?$/.test(cell.text.replace(/\s/g, '')))) return null;
    const rows = [header];
    let endLine = startLine + 1;
    for (let line = startLine + 2; line <= lineCount; line++) {
        const row = parseMarkdownTableRow(getLine(line));
        if (!row || row.cells.every(cell => /^:?-{3,}:?$/.test(cell.text.replace(/\s/g, '')))) break;
        rows.push(row);
        endLine = line;
    }
    return { startLine, endLine, rows, separator, columns: Math.max(...rows.map(row => row.cells.length)) };
}

export function tableSourceLine(table, row) {
    return table.startLine + row + (row > 0 ? 1 : 0);
}

export function serializeMarkdownTable(table) {
    const line = row => row.prefix + row.cells.map(cell => cell.text).join('|') + row.suffix;
    return [line(table.rows[0]), line(table.separator), ...table.rows.slice(1).map(line)].join('\n');
}

function cloneTable(table) {
    const cloneRow = row => ({ ...row, cells: row.cells.map(cell => ({ ...cell })) });
    return { ...table, rows: table.rows.map(cloneRow), separator: cloneRow(table.separator) };
}

export function markdownTableAlignment(separatorCell) {
    const cell = String(separatorCell || '').replace(/\s+/g, '');
    if (cell.startsWith(':') && cell.endsWith(':')) return 'center';
    if (cell.endsWith(':')) return 'right';
    return cell.startsWith(':') ? 'left' : '';
}

export function changeMarkdownTable(table, action, row, column, bounds = null) {
    const alignment = action === 'align-left' ? ':---' : action === 'align-center' ? ':---:'
        : action === 'align-right' ? '---:' : null;
    if (!['row-before', 'row-after', 'row-delete', 'row-up', 'row-down', 'column-before', 'column-after',
        'column-delete', 'column-left', 'column-right', 'clear-cells'].includes(action) && !alignment) return null;
    const next = cloneTable(table);
    for (const current of alignment ? [next.rows[0], next.separator] : [...next.rows, next.separator]) {
        while (current.cells.length < next.columns) current.cells.push({ text: current === next.separator ? ' --- ' : '  ' });
    }
    row = Math.max(0, Math.min(row, next.rows.length - 1));
    column = Math.max(0, Math.min(column, next.columns - 1));
    if (alignment) {
        // Rendered short rows include empty cells without source offsets. Give
        // the focused cell an offset before restoring the editing caret.
        while (next.rows[row].cells.length <= column) next.rows[row].cells.push({ text: '  ' });
    }
    const blankRow = () => ({ prefix: table.rows[0].prefix, suffix: table.rows[0].suffix,
        cells: Array.from({ length: next.columns }, () => ({ text: '  ' })) });
    if (alignment) {
        const left = Math.max(0, Math.min(bounds?.left ?? column, next.columns - 1));
        const right = Math.max(left, Math.min(bounds?.right ?? column, next.columns - 1));
        for (let c = left; c <= right; c++) {
            const cell = next.separator.cells[c];
            const leading = cell.text.match(/^\s*/)[0];
            const trailing = cell.text.match(/\s*$/)[0];
            const dashes = cell.text.replace(/[^-]/g, '');
            cell.text = leading + alignment.replace('---', dashes) + trailing;
        }
    } else if (action === 'row-before' || action === 'row-after') {
        row = Math.max(1, row + (action === 'row-after' ? 1 : 0));
        next.rows.splice(row, 0, blankRow());
    } else if (action === 'row-delete') {
        if (!row) return null;
        next.rows.splice(row, 1);
        row = Math.min(row, next.rows.length - 1);
    } else if (action === 'row-up' || action === 'row-down') {
        const target = row + (action === 'row-up' ? -1 : 1);
        if (row < 1 || target < 1 || target >= next.rows.length) return null;
        [next.rows[row], next.rows[target]] = [next.rows[target], next.rows[row]];
        row = target;
    } else if (action.startsWith('column-')) {
        if (action === 'column-delete' && next.columns <= 1) return null;
        const target = column + (action === 'column-left' ? -1 : 1);
        if (['column-left', 'column-right'].includes(action) && (target < 0 || target >= next.columns)) return null;
        const index = column + (action === 'column-after' ? 1 : 0);
        for (const current of [next.rows[0], next.separator, ...next.rows.slice(1)]) {
            while (current.cells.length < next.columns) current.cells.push({ text: current === next.separator ? ' --- ' : '  ' });
            if (action === 'column-before' || action === 'column-after') current.cells.splice(index, 0, { text: current === next.separator ? ' --- ' : '  ' });
            else if (action === 'column-delete') current.cells.splice(column, 1);
            else [current.cells[column], current.cells[target]] = [current.cells[target], current.cells[column]];
        }
        if (['column-before', 'column-after'].includes(action)) { next.columns++; column = index; }
        else if (action === 'column-delete') { next.columns--; column = Math.min(column, next.columns - 1); }
        else column = target;
    } else if (action === 'clear-cells') {
        const area = bounds || { top: row, bottom: row, left: column, right: column };
        for (let r = area.top; r <= area.bottom; r++) {
            while (next.rows[r].cells.length < next.columns) next.rows[r].cells.push({ text: '  ' });
            for (let c = area.left; c <= area.right; c++) next.rows[r].cells[c] = { text: '  ' };
        }
    } else return null;
    const text = serializeMarkdownTable(next);
    // Reparse offsets after moves/additions before positioning an editable caret.
    const lines = text.split('\n');
    const parsed = readMarkdownTable(table.startLine, table.startLine + lines.length - 1,
        line => lines[line - table.startLine]);
    return { table: parsed, text, row, column };
}
