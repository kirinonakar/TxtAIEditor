const braceLanguages = new Set(['javascript', 'typescript', 'jsx', 'tsx', 'c', 'cpp', 'csharp',
    'java', 'kotlin', 'swift', 'go', 'rust', 'dart', 'php', 'css', 'scss', 'less', 'json',
    'jsonc', 'powershell', 'r', 'objective-c']);

export function supportsFolding(language) {
    return ['markdown', 'python', 'html', 'svg', 'xml'].includes(language) || braceLanguages.has(language);
}

// Folding changes the view only. All ranges retain original document line numbers.
export class FoldingController {
    ranges = new Map();
    collapsed = new Map();
    hidden = [];
    pendingLines = [];
    version = 0;
    #key = '';
    #collapseNew = false;

    reset() {
        this.ranges.clear();
        this.collapsed.clear();
        this.hidden = [];
        this.pendingLines = [];
        this.#key = '';
        this.#collapseNew = false;
        this.version++;
    }

    update(cache, lineCount, language, revision, enabled = true) {
        const key = `${revision}:${lineCount}:${language}:${enabled}`;
        if (key === this.#key) return false;
        this.#key = key;
        const result = enabled ? findFoldingRanges(cache, lineCount, language) : { ranges: [], pendingLines: [] };
        this.ranges = new Map(result.ranges.map(range => [range.start, range]));
        this.pendingLines = result.pendingLines;
        for (const [line, text] of this.collapsed) {
            if (!this.ranges.has(line) || cache.get(line) !== text) this.collapsed.delete(line);
        }
        if (this.#collapseNew) {
            for (const line of this.ranges.keys()) this.collapsed.set(line, cache.get(line));
        }
        this.#rebuildHidden();
        this.version++;
        return true;
    }

    toggle(line, cache) {
        this.#collapseNew = false;
        if (!this.ranges.has(line)) return false;
        if (!this.collapsed.delete(line)) this.collapsed.set(line, cache.get(line));
        this.#rebuildHidden();
        this.version++;
        return true;
    }

    setAll(collapsed, cache) {
        this.#collapseNew = collapsed;
        this.collapsed.clear();
        if (collapsed) {
            for (const line of this.ranges.keys()) this.collapsed.set(line, cache.get(line));
        }
        this.#rebuildHidden();
        this.version++;
    }

    reveal(line) {
        return this.revealLines([line]);
    }

    revealLines(lines, { includeHeaders = false } = {}) {
        if (!this.collapsed.size) return false;
        const targets = [...lines].map(Number).filter(Number.isFinite).sort((a, b) => a - b);
        let changed = false;
        for (const start of this.collapsed.keys()) {
            const range = this.ranges.get(start);
            if (!range) continue;
            let low = 0;
            let high = targets.length;
            const firstHiddenLine = includeHeaders ? start : start + 1;
            while (low < high) {
                const mid = (low + high) >>> 1;
                if (targets[mid] < firstHiddenLine) low = mid + 1;
                else high = mid;
            }
            if (low < targets.length && targets[low] <= range.end) {
                this.collapsed.delete(start);
                changed = true;
            }
        }
        if (changed) {
            this.#collapseNew = false;
            this.#rebuildHidden();
            this.version++;
        }
        return changed;
    }

    shift(fromLine, delta) {
        this.collapsed = new Map([...this.collapsed].map(([line, text]) =>
            [line >= fromLine ? line + delta : line, text]).filter(([line]) => line > 0));
        this.#key = '';
    }

    collapsedRange(line) {
        return this.collapsed.has(line) ? this.ranges.get(line) : null;
    }

    containing(line) {
        // Hidden intervals are disjoint and ordered, including nested folds only once.
        let low = 0;
        let high = this.hidden.length - 1;
        while (low <= high) {
            const mid = (low + high) >>> 1;
            const range = this.hidden[mid];
            if (line < range.start) high = mid - 1;
            else if (line > range.end) low = mid + 1;
            else return range;
        }
        return null;
    }

    hiddenCountBefore(line) {
        let low = 0;
        let high = this.hidden.length - 1;
        let count = 0;
        while (low <= high) {
            const mid = (low + high) >>> 1;
            const range = this.hidden[mid];
            if (line <= range.start) high = mid - 1;
            else {
                count = range.before + Math.min(line - range.start, range.end - range.start + 1);
                low = mid + 1;
            }
        }
        return count;
    }

    visibleIndex(line) {
        const visibleLine = this.containing(line)?.header ?? line;
        return visibleLine - this.hiddenCountBefore(visibleLine);
    }

    lineAtVisibleIndex(index, lineCount) {
        const target = Math.max(1, Math.min(index, lineCount - this.hiddenCountBefore(lineCount + 1)));
        if (!this.hidden.length) return target;
        let low = 1;
        let high = lineCount;
        while (low < high) {
            const mid = (low + high) >>> 1;
            if (mid - this.hiddenCountBefore(mid + 1) < target) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    adjacentLine(line, direction, lineCount) {
        return this.lineAtVisibleIndex(this.visibleIndex(line) + direction, lineCount);
    }

    expandSelection(selection, cache) {
        if (!selection) return null;
        let { start, end, isColumn } = selection;
        for (const hidden of this.hidden) {
            const headerLength = (cache.get(hidden.header) || '').length;
            const touchesBody = start.line <= hidden.end && end.line >= hidden.start &&
                !(end.line === hidden.start && end.column === 0);
            const selectsHeaderEnd = end.line === hidden.header && end.column >= headerLength &&
                (start.line < end.line || start.column < end.column);
            if (!touchesBody && !selectsHeaderEnd) continue;
            if (start.line >= hidden.start && start.line <= hidden.end) {
                start = { line: hidden.header, column: headerLength };
            }
            if (end.line >= hidden.header && end.line <= hidden.end) {
                end = { line: hidden.end, column: (cache.get(hidden.end) || '').length };
            }
            // A rectangle cannot represent an indivisible folded block.
            isColumn = false;
        }
        return { start, end, isColumn: !!isColumn };
    }

    #rebuildHidden() {
        this.hidden = [];
        let count = 0;
        for (const start of [...this.collapsed.keys()].sort((a, b) => a - b)) {
            const range = this.ranges.get(start);
            if (!range || start <= (this.hidden.at(-1)?.end ?? 0)) continue;
            this.hidden.push({ header: start, start: start + 1, end: range.end, before: count });
            count += range.end - start;
        }
    }
}

export function findFoldingRanges(cache, lineCount, language) {
    const ranges = new Map();
    const pendingLines = new Set();
    const add = (start, end) => {
        if (end > start && (!ranges.has(start) || ranges.get(start).end < end)) {
            ranges.set(start, { start, end });
        }
    };
    const numbers = [...cache.keys()].filter(line => line > 0 && line <= lineCount).sort((a, b) => a - b);
    const markup = ['html', 'svg', 'xml'].includes(language);
    if (!supportsFolding(language)) {
        return { ranges: [], pendingLines: [] };
    }

    // Scan each contiguous cache block independently; never invent a closing boundary
    // across missing source. Open blocks request the next batch until their end is known.
    for (let offset = 0; offset < numbers.length;) {
        const blockStart = offset;
        while (++offset < numbers.length && numbers[offset] === numbers[offset - 1] + 1) { }
        const block = numbers.slice(blockStart, offset);
        const last = block.at(-1);
        const atEof = last === lineCount;
        let open = false;
        if (markup) {
            const source = block.map(line => cache.get(line)).join('\n');
            const tags = /<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\/?([\w:.-]+)\b(?:"[^"]*"|'[^']*'|[^'">])*>/g;
            const stack = [];
            const voidTags = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);
            let cursor = 0;
            let currentLine = block[0];
            for (const match of source.matchAll(tags)) {
                currentLine += (source.slice(cursor, match.index).match(/\n/g) || []).length;
                cursor = match.index;
                if (!match[1]) continue;
                const name = language === 'html' ? match[1].toLowerCase() : match[1];
                if (match[0].startsWith('</')) {
                    if (stack.at(-1)?.name === name) add(stack.pop().line, currentLine);
                } else if (!/\/\s*>$/.test(match[0]) && !(language === 'html' && voidTags.has(name))) {
                    stack.push({ name, line: currentLine });
                }
            }
            open = stack.length > 0;
        } else if (language === 'markdown') {
            const headings = [];
            let fence = null;
            for (const line of block) {
                const text = cache.get(line);
                const marker = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(text);
                if (fence) {
                    if (marker && marker[1][0] === fence.char && marker[1].length >= fence.length && !marker[2].trim()) {
                        add(fence.line, line);
                        fence = null;
                    }
                    continue;
                }
                if (marker) {
                    fence = { line, char: marker[1][0], length: marker[1].length };
                    continue;
                }
                const heading = /^ {0,3}(#{1,6})(?:\s+|$)/.exec(text);
                const setext = /^ {0,3}(=+|-+)\s*$/.exec(text);
                const previous = cache.get(line - 1);
                const level = heading?.[1].length ||
                    (setext && line > block[0] && previous?.trim() && !/^\s*(?:#|>|[-*+]\s)/.test(previous)
                        ? (setext[1][0] === '=' ? 1 : 2) : 0);
                if (!level) continue;
                const start = heading ? line : line - 1;
                while (headings.length && headings.at(-1).level >= level) add(headings.pop().line, start - 1);
                headings.push({ line: start, level });
            }
            if (atEof) {
                for (const heading of headings) add(heading.line, last);
                if (fence) add(fence.line, last);
            }
            open = headings.length > 0 || !!fence;
        } else if (language === 'python') {
            const stack = [];
            let triple = null;
            let declaration = null;
            let parens = 0;
            let lastContent = block[0];
            for (const line of block) {
                const text = cache.get(line);
                const wasTriple = !!triple;
                // Blank/comment rows do not terminate indentation blocks; strings do
                // not introduce fake def/class declarations or dedents.
                let code = '';
                for (let i = 0; i < text.length; i++) {
                    if (triple) {
                        if (text.startsWith(triple, i)) { i += 2; triple = null; }
                    } else if (text.startsWith('"""', i) || text.startsWith("'''", i)) {
                        triple = text.slice(i, i + 3); i += 2;
                    } else if (text[i] === '#') break;
                    else if (text[i] === '"' || text[i] === "'") {
                        const quote = text[i];
                        code += 's';
                        while (++i < text.length) {
                            if (text[i] === '\\') i++;
                            else if (text[i] === quote) break;
                        }
                    } else code += text[i];
                }
                if (wasTriple || !code.trim()) { if (text.trim()) lastContent = line; continue; }
                const indent = (text.match(/^\s*/)[0] || '').replace(/\t/g, '        ').length;
                if (parens === 0) {
                    while (stack.length && indent <= stack.at(-1).indent) add(stack.pop().line, lastContent);
                    if (/^\s*(?:async\s+)?(?:def|class)\b/.test(code)) declaration = { line, indent };
                }
                for (const char of code) {
                    if ('([{'.includes(char)) parens++;
                    else if (')]}'.includes(char)) parens = Math.max(0, parens - 1);
                }
                if (parens === 0 && /:\s*$/.test(code)) {
                    stack.push(declaration || { line, indent });
                    declaration = null;
                }
                lastContent = line;
            }
            if (atEof && !triple) for (const entry of stack) add(entry.line, lastContent);
            open = stack.length > 0 || !!triple;
        } else {
            const stack = [];
            let quote = null;
            let comment = false;
            let statementStart = null;
            for (const line of block) {
                const text = cache.get(line);
                let code = '';
                if (!quote && !comment && /^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|export|default|async|final|open|data|suspend|override)\s+)*(?:class|struct|interface|enum|record|namespace|function|fun)\b/.test(text)) {
                    statementStart = line;
                }
                for (let i = 0; i < text.length; i++) {
                    const char = text[i];
                    if (comment) {
                        if (text.startsWith('*/', i)) { comment = false; i++; }
                        continue;
                    }
                    if (quote) {
                        if (char === '\\') i++;
                        else if (char === quote) quote = null;
                        continue;
                    }
                    if (text.startsWith('//', i) || (['powershell', 'r'].includes(language) && char === '#')) break;
                    if (text.startsWith('/*', i)) { comment = true; i++; continue; }
                    if ('"\'`'.includes(char)) { quote = char; code += 's'; continue; }
                    // Regex literals may contain braces, quotes and escaped slashes.
                    if (char === '/' && ['javascript', 'typescript', 'jsx', 'tsx'].includes(language) &&
                        /(?:^|[=(:,!\[?;]|\breturn)\s*$/.test(code)) {
                        let inClass = false;
                        while (++i < text.length) {
                            if (text[i] === '\\') i++;
                            else if (text[i] === '[') inClass = true;
                            else if (text[i] === ']') inClass = false;
                            else if (text[i] === '/' && !inClass) break;
                        }
                        code += 's';
                        continue;
                    }
                    if (char.trim() && statementStart === null) statementStart = line;
                    if (char === '{' || (['json', 'jsonc'].includes(language) && char === '[')) {
                        const start = ['json', 'jsonc'].includes(language) ? line : (statementStart ?? line);
                        stack.push({ start, opener: line, char });
                        statementStart = null;
                    } else if (char === '}' || (['json', 'jsonc'].includes(language) && char === ']')) {
                        const entry = stack.at(-1);
                        if (entry && entry.char === (char === '}' ? '{' : '[')) {
                            stack.pop();
                            if (line > entry.opener) add(entry.start, line);
                        }
                        statementStart = null;
                    }
                    if (char === ';') statementStart = null;
                    code += char;
                }
                if (quote !== '`' && !text.endsWith('\\')) quote = null;
            }
            open = stack.length > 0 || comment || !!quote;
        }
        if (open && !atEof) pendingLines.add(last + 1);
    }
    return { ranges: [...ranges.values()], pendingLines: [...pendingLines] };
}
