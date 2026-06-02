const mermaidCache = new Map();

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function escapeAttribute(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

function cleanupLatex(text) {
    return String(text || '')
        .replace(/\\begin\{document\}|\\end\{document\}/g, '')
        .replace(/\\\(|\\\)|\\\[|\\\]|\$\$/g, '')
        .trim();
}

function renderLatex(text, display) {
    const cleaned = cleanupLatex(text);
    if (!cleaned) return '';
    try {
        if (typeof katex !== 'undefined') {
            const html = katex.renderToString(cleaned, { displayMode: display, throwOnError: false, trust: false });
            return display
                ? `<span class="math display">${html}</span>`
                : `<span class="math inline">${html}</span>`;
        }
    } catch { }
    return `<code>${escapeHtml(text)}</code>`;
}

function renderLatexLine(line) {
    const text = cleanupLatex(line);
    if (!text) return '';
    try {
        if (typeof katex !== 'undefined') {
            return `<div class="math display">${katex.renderToString(text, { displayMode: true, throwOnError: false, trust: false })}</div>`;
        }
    } catch { }
    return `<code>${escapeHtml(line)}</code>`;
}

function parseOptionalTitle(value) {
    const match = /^\s+(["'])(.*?)\1\s*$/.exec(String(value || ''));
    return match ? match[2] : '';
}

function parseMarkdownImageTarget(value) {
    const target = String(value || '').trim();
    if (!target) return { src: '', title: '' };
    if (target.startsWith('<')) {
        const closeIndex = target.indexOf('>');
        if (closeIndex > 0) {
            return {
                src: target.slice(1, closeIndex).trim(),
                title: parseOptionalTitle(target.slice(closeIndex + 1))
            };
        }
    }
    const titled = /^(\S+)\s+(["'])(.*?)\2\s*$/.exec(target);
    if (titled) return { src: titled[1], title: titled[3] };
    return { src: target, title: '' };
}

function isSafeMediaUrl(value) {
    const trimmed = String(value || '').trim().toLowerCase();
    return !!trimmed &&
        !trimmed.startsWith('javascript:') &&
        !trimmed.startsWith('vbscript:') &&
        !trimmed.startsWith('data:text/html');
}

function resolvePreviewResourceUrl(value, options = {}) {
    const raw = String(value || '').trim();
    const baseHref = options.baseHref || '';
    if (!raw || raw.startsWith('#') || !isSafeMediaUrl(raw)) return raw;
    if (/^[A-Za-z]:[\\/]/.test(raw)) {
        return `file:///${raw.replace(/\\/g, '/')}`;
    }
    if (/^\\\\/.test(raw)) {
        return `file:${raw.replace(/\\/g, '/')}`;
    }
    if (!baseHref) return raw.replace(/\\/g, '/');
    const normalized = /^[A-Za-z][A-Za-z0-9+.-]*:/.test(raw)
        ? raw
        : raw.replace(/\\/g, '/');
    try {
        return new URL(normalized, baseHref).href;
    } catch {
        return raw;
    }
}

function resolvePreviewSrcset(value, options = {}) {
    return String(value || '')
        .split(',')
        .map(candidate => {
            const trimmed = candidate.trim();
            if (!trimmed) return '';
            const parts = trimmed.split(/\s+/);
            const url = parts.shift() || '';
            return [resolvePreviewResourceUrl(url, options), ...parts].join(' ');
        })
        .filter(Boolean)
        .join(', ');
}

function renderMarkdownImage(alt, target, options) {
    const parsed = parseMarkdownImageTarget(target);
    if (!isSafeMediaUrl(parsed.src)) return escapeHtml(`![${alt}](${target})`);
    const src = resolvePreviewResourceUrl(parsed.src, options);
    const title = parsed.title ? ` title="${escapeAttribute(parsed.title)}"` : '';
    return `<img src="${escapeAttribute(src)}" alt="${escapeAttribute(alt)}"${title} decoding="async">`;
}

function sanitizeHtml(html, options = {}) {
    const template = document.createElement('template');
    template.innerHTML = html || '';
    template.content.querySelectorAll('script, iframe, object, embed, base, link, meta').forEach(node => {
        node.replaceWith(document.createComment('blocked by TxtAIEditor preview'));
    });
    template.content.querySelectorAll('*').forEach(node => {
        [...node.attributes].forEach(attr => {
            const name = attr.name.toLowerCase();
            const value = attr.value.trim().toLowerCase();
            if (name.startsWith('on') || value.startsWith('javascript:') || value.startsWith('data:text/html')) {
                node.removeAttribute(attr.name);
            }
        });
    });
    template.content.querySelectorAll('img[src], video[src], audio[src], source[src], track[src]').forEach(node => {
        node.setAttribute('src', resolvePreviewResourceUrl(node.getAttribute('src'), options));
    });
    template.content.querySelectorAll('img[srcset], source[srcset]').forEach(node => {
        node.setAttribute('srcset', resolvePreviewSrcset(node.getAttribute('srcset'), options));
    });
    template.content.querySelectorAll('video[poster]').forEach(node => {
        node.setAttribute('poster', resolvePreviewResourceUrl(node.getAttribute('poster'), options));
    });
    return template.innerHTML;
}

function normalizeCodeSpanText(value) {
    const normalized = String(value ?? '').replace(/\s*\n\s*/g, ' ');
    if (normalized.length >= 2 &&
        normalized.startsWith(' ') &&
        normalized.endsWith(' ') &&
        /[^ ]/.test(normalized.slice(1, -1))) {
        return normalized.slice(1, -1);
    }
    return normalized;
}

function stashCodeSpans(value, stash) {
    const source = String(value ?? '');
    let result = '';
    let index = 0;
    while (index < source.length) {
        if (source[index] !== '`') {
            result += source[index++];
            continue;
        }
        const openerStart = index;
        while (index < source.length && source[index] === '`') index++;
        const delimiter = source.slice(openerStart, index);
        const closeIndex = source.indexOf(delimiter, index);
        if (closeIndex < 0) {
            result += delimiter;
            continue;
        }
        result += stash(`<code>${escapeHtml(normalizeCodeSpanText(source.slice(index, closeIndex)))}</code>`);
        index = closeIndex + delimiter.length;
    }
    return result;
}

function stashInlineMath(value, stash) {
    return value
        .replace(/\\\[([\s\S]+?)\\\]/g, (_, expr) => stash(renderLatex(expr, true)))
        .replace(/\\\(([\s\S]+?)\\\)/g, (_, expr) => stash(renderLatex(expr, false)))
        .replace(/\$\$([\s\S]+?)\$\$/g, (_, expr) => stash(renderLatex(expr, true)))
        .replace(/(^|[^\w\\])\$([^$\n]+?)\$/g, (_, prefix, expr) => `${prefix}${stash(renderLatex(expr, false))}`);
}

function sanitizeInlineHtmlTag(tag) {
    const raw = String(tag || '');
    const match = /^<\/?\s*([A-Za-z][A-Za-z0-9:-]*)([^<>]*?)\s*\/?>$/.exec(raw);
    if (!match) return escapeHtml(raw);
    const isClosing = /^<\s*\//.test(raw);
    const tagName = match[1].toLowerCase();
    const allowedInlineTags = new Set(['span', 'strong', 'em', 'b', 'i', 'u', 'big', 'small', 'mark', 's', 'code', 'sub', 'sup', 'kbd']);
    if (!allowedInlineTags.has(tagName)) return escapeHtml(raw);
    if (isClosing) return `</${tagName}>`;
    if (tagName === 'span') {
        const styleMatch = /\bstyle\s*=\s*(["'])(.*?)\1/i.exec(match[2] || '');
        if (styleMatch) {
            const colorMatch = /(?:^|;)\s*color\s*:\s*(#[0-9a-fA-F]{6})\s*;?\s*(?:$|;)/i.exec(styleMatch[2]);
            if (colorMatch) return `<span style="color: ${colorMatch[1].toUpperCase()}">`;
        }
    }
    return `<${tagName}>`;
}

function stashHtmlTags(value, stash) {
    return value.replace(/<\/?[A-Za-z][A-Za-z0-9:-]*(?:\s+[^<>]*?)?\s*\/?>/g, tag => {
        return stash(sanitizeInlineHtmlTag(tag));
    });
}

function inlineMarkdown(text, options = {}) {
    const fragments = [];
    const stash = html => {
        const token = `\u0001${fragments.length}\u0001`;
        fragments.push(html);
        return token;
    };
    let out = String(text ?? '');
    out = stashCodeSpans(out, stash);
    out = out.replace(/!\[([^\]]*)\]\(([^)]*)\)/g, (_, alt, target) => stash(renderMarkdownImage(alt, target, options)));
    out = stashInlineMath(out, stash);
    out = stashHtmlTags(out, stash);
    out = escapeHtml(out);
    out = out.replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, (_, label, href) => {
        return `<a href="${escapeHtml(href)}" target="_blank" rel="noreferrer">${label}</a>`;
    });
    out = out.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    out = out.replace(/\*([^*]+)\*/g, '<em>$1</em>');
    out = out.replace(/__([^_]+)__/g, '<strong>$1</strong>');
    out = out.replace(/_([^_]+)_/g, '<em>$1</em>');
    out = out.replace(/==([^=]+)==/g, '<mark>$1</mark>');
    out = out.replace(/~~([^~]+)~~/g, '<s>$1</s>');
    fragments.forEach((fragment, index) => {
        out = out.split(`\u0001${index}\u0001`).join(fragment);
    });
    return sanitizeHtml(out, options);
}

function renderAozoraLine(line) {
    let html = escapeHtml(line);
    html = html.replace(/《《([\s\S]+?)》》/g, '<span style="text-emphasis: sesame; -webkit-text-emphasis: sesame;">$1</span>');
    html = html.replace(/[|｜]([^《\n\r]+?)《([^》\n\r]+?)》/g, '<ruby>$1<rt>$2</rt></ruby>');
    html = html.replace(/([\u4e00-\u9faf\u3400-\u4dbf\uF900-\uFAFF]+?)《([^》\n\r]+?)》/g, '<ruby>$1<rt>$2</rt></ruby>');
    html = html.replace(/［＃太字］([\s\S]*?)［＃太字終わり］/g, '<strong>$1</strong>');
    html = html.replace(/［＃斜体］([\s\S]*?)［＃斜体終わり］/g, '<em>$1</em>');
    html = html.replace(/［＃[\s\S]*?］/g, '');
    return `<p class="aozora">${html}</p>`;
}

function fencedCodeInfo(line) {
    const match = /^( {0,3})(`{3,}|~{3,})(.*)$/.exec(line || '');
    if (!match) return null;
    const info = (match[3] || '').trim();
    if (match[2].startsWith('`') && info.includes('`')) return null;
    return {
        marker: match[2][0],
        length: match[2].length,
        language: String(info.split(/\s+/)[0] || '').replace(/[^A-Za-z0-9_+-]/g, '')
    };
}

function isBareFence(line, fence) {
    const trimmed = String(line || '').trim();
    return !!fence &&
        trimmed.length >= fence.length &&
        [...trimmed].every(ch => ch === fence.marker);
}

function isMarkdownBlockBoundary(line) {
    const trimmed = String(line || '').trim();
    return /^#{1,6}\s+/.test(trimmed) ||
        /^>\s?/.test(trimmed) ||
        /^[-*+]\s+/.test(trimmed) ||
        /^\d+\.\s+/.test(trimmed) ||
        /^(\|.*\|)$/.test(trimmed);
}

function isAmbiguousBareFenceClose(lineNumber, getLine, fence) {
    if (!isBareFence(getLine(lineNumber), fence)) return false;

    const previous = getLine(lineNumber - 1);
    if (previous === undefined || !String(previous || '').trim()) return false;

    for (let line = lineNumber + 1; ; line++) {
        const text = getLine(line);
        if (text === undefined) return false;
        if (!String(text || '').trim()) continue;
        const nextFence = fencedCodeInfo(text);
        if (nextFence) return !isFencedCodeClose(text, fence);
        return isMarkdownBlockBoundary(text);
    }
}

function isFencedCodeClose(line, fence) {
    const trimmed = (line || '').trim();
    if (!trimmed || !trimmed.startsWith(fence.marker.repeat(fence.length))) return false;
    for (const ch of trimmed) {
        if (ch !== fence.marker) return false;
    }
    return trimmed.length >= fence.length;
}

function collectFencedCodeBlock(startLine, maxLine, getLine, options = {}) {
    const fence = fencedCodeInfo(getLine(startLine));
    if (!fence) return null;
    if (isAmbiguousBareFenceClose(startLine, getLine, fence)) return null;
    const parts = [];
    for (let line = startLine + 1; line <= maxLine; line++) {
        const text = getLine(line);
        if (text === undefined) {
            return { pending: true, endLine: line, extendRangeEnd: line };
        }
        if (isFencedCodeClose(text, fence)) {
            return { code: parts.join('\n'), language: fence.language, endLine: line };
        }
        parts.push(text);
    }
    if (options.requireClosedFence) return null;
    return { code: parts.join('\n'), language: fence.language, endLine: maxLine };
}

function renderFencedCodeBlock(block) {
    if ((block.language || '').toLowerCase() === 'mermaid') {
        const cachedSvg = mermaidCache.get(block.code);
        if (cachedSvg) {
            return `<div class="mermaid-block rendered" data-code="${escapeAttribute(block.code)}">${cachedSvg}</div>`;
        }
        return `<div class="mermaid-block" data-code="${escapeAttribute(block.code)}">Rendering...</div>`;
    }
    const langClass = block.language ? ` class="language-${escapeAttribute(block.language)}"` : '';
    return `<pre><code${langClass}>${escapeHtml(block.code)}</code></pre>`;
}

function isDisplayMathFence(line) {
    const trimmed = (line || '').trim();
    return trimmed === '$$' || trimmed === '\\[';
}

function isDisplayMathClose(line, opener) {
    const trimmed = (line || '').trim();
    return opener === '$$' ? trimmed === '$$' : trimmed === '\\]';
}

function collectDisplayMathBlock(startLine, maxLine, getLine) {
    const opener = (getLine(startLine) || '').trim();
    const parts = [];
    for (let line = startLine + 1; line <= maxLine; line++) {
        const text = getLine(line);
        if (text === undefined) return null;
        if (isDisplayMathClose(text, opener)) {
            return { text: parts.join('\n'), endLine: line };
        }
        parts.push(text);
    }
    return { text: parts.join('\n'), endLine: maxLine };
}

function htmlBlockTagName(line) {
    const trimmed = (line || '').trim();
    const match = /^<([A-Za-z][A-Za-z0-9:-]*)(?:\s[^<>]*?)?>\s*$/.exec(trimmed);
    if (!match || /\/>\s*$/.test(trimmed)) return '';
    const tag = match[1].toLowerCase();
    return [
        'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
        'p', 'div', 'center', 'figure', 'figcaption',
        'ol', 'ul', 'li', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'pre', 'code'
    ].includes(tag) ? tag : '';
}

function collectHtmlBlock(startLine, maxLine, getLine) {
    const tag = htmlBlockTagName(getLine(startLine));
    if (!tag) return null;
    const closePattern = new RegExp(`</${tag}\\s*>`, 'i');
    const parts = [];
    for (let line = startLine; line <= maxLine; line++) {
        const text = getLine(line);
        if (text === undefined) return null;
        parts.push(text);
        if (closePattern.test(text)) return { html: parts.join('\n'), endLine: line };
    }
    return { html: parts.join('\n'), endLine: maxLine };
}

function splitTableRow(line) {
    const trimmed = String(line || '').trim();
    if (!trimmed.startsWith('|') || !trimmed.endsWith('|')) return null;

    const body = trimmed.slice(1, -1);
    const cells = [];
    let current = '';
    for (let i = 0; i < body.length; i++) {
        const ch = body[i];
        if (ch === '\\' && body[i + 1] === '|') {
            current += '|';
            i++;
        } else if (ch === '|') {
            cells.push(current.trim());
            current = '';
        } else {
            current += ch;
        }
    }
    cells.push(current.trim());
    return cells;
}

function isTableSeparator(line) {
    const cells = splitTableRow(line);
    return !!cells?.length && cells.every(cell => /^:?-{3,}:?$/.test(cell.replace(/\s+/g, '')));
}

function tableAlignment(separatorCell) {
    const cell = String(separatorCell || '').replace(/\s+/g, '');
    if (cell.startsWith(':') && cell.endsWith(':')) return 'center';
    if (cell.endsWith(':')) return 'right';
    return '';
}

function isMarkdownTableStart(lineNumber, getLine) {
    const current = getLine(lineNumber);
    const next = getLine(lineNumber + 1);
    return current !== undefined && next !== undefined && !!splitTableRow(current) && isTableSeparator(next);
}

function collectMarkdownTableBlock(startLine, maxLine, getLine) {
    if (!isMarkdownTableStart(startLine, getLine)) return null;
    const header = splitTableRow(getLine(startLine));
    const separator = splitTableRow(getLine(startLine + 1));
    const rows = [];
    let endLine = startLine + 1;
    for (let line = startLine + 2; line <= maxLine; line++) {
        const text = getLine(line);
        const row = splitTableRow(text);
        if (text === undefined || !row || isTableSeparator(text)) break;
        rows.push(row);
        endLine = line;
    }
    return { header, separator, rows, endLine };
}

function findFencedCodeBlockContaining(lineNumber, maxLine, getLine) {
    const targetLine = Math.min(Math.max(1, Number(lineNumber || 1)), maxLine);
    let firstKnownLine = targetLine;
    while (firstKnownLine > 1 && getLine(firstKnownLine - 1) !== undefined) {
        firstKnownLine--;
    }

    let openFence = null;
    let startLine = 0;

    for (let line = firstKnownLine; line <= targetLine; line++) {
        const text = getLine(line);
        if (text === undefined) return null;

        if (openFence) {
            if (isFencedCodeClose(text, openFence)) {
                if (targetLine <= line) {
                    return {
                        kind: 'code',
                        startLine,
                        endLine: line,
                        bodyStartLine: startLine + 1,
                        bodyEndLine: line - 1,
                        language: openFence.language
                    };
                }
                openFence = null;
                startLine = 0;
            }
            continue;
        }

        const fence = fencedCodeInfo(text);
        if (fence) {
            openFence = fence;
            startLine = line;
        }
    }

    if (openFence && startLine > 0) {
        const block = collectFencedCodeBlock(startLine, maxLine, getLine);
        if (block && targetLine <= block.endLine) {
            return {
                kind: 'code',
                startLine,
                endLine: block.endLine,
                bodyStartLine: startLine + 1,
                bodyEndLine: block.endLine - 1,
                language: openFence.language
            };
        }
    }

    return null;
}

function findMarkdownTableBlockContaining(lineNumber, maxLine, getLine) {
    const targetLine = Math.min(Math.max(1, Number(lineNumber || 1)), maxLine);
    for (let startLine = targetLine; startLine >= 1; startLine--) {
        const text = getLine(startLine);
        if (text === undefined) return null;
        if (!splitTableRow(text) && startLine < targetLine) break;
        if (!isMarkdownTableStart(startLine, getLine)) continue;

        const block = collectMarkdownTableBlock(startLine, maxLine, getLine);
        if (block && targetLine >= startLine && targetLine <= block.endLine) {
            return {
                kind: 'table',
                startLine,
                endLine: block.endLine,
                separatorLine: startLine + 1
            };
        }
    }

    return null;
}

function findEditablePreviewBlockContaining(lineNumber, maxLine, getLine, options = {}) {
    if (!lineNumber || lineNumber < 1) return null;
    return (options.renderListsAsBlocks !== false
        ? findMarkdownListBlockContaining(lineNumber, maxLine, getLine, options)
        : null) ||
        findFencedCodeBlockContaining(lineNumber, maxLine, getLine) ||
        findMarkdownTableBlockContaining(lineNumber, maxLine, getLine);
}

function renderMarkdownTableBlock(block, options = {}) {
    const aligns = block.separator.map(tableAlignment);
    const cellStyle = (content, i) => {
        const hasBr = /<br\s*\/?>/i.test(content);
        const align = aligns[i] || '';
        if (align) return ` style="text-align:${align}${hasBr ? ';white-space:pre-line' : ''}"`;
        return hasBr ? ' style="white-space:pre-line"' : '';
    };
    const renderCell = (cell, tag, i) => {
        const processed = String(cell || '').replace(/<br\s*\/?>/gi, '\u0003');
        const rendered = inlineMarkdown(processed, options).replace(/\u0003/g, '<br>');
        return `<${tag}${cellStyle(cell, i)}>${rendered || '&nbsp;'}</${tag}>`;
    };
    let html = '<table><thead><tr>';
    block.header.forEach((cell, i) => {
        html += renderCell(cell, 'th', i);
    });
    html += '</tr></thead><tbody>';
    block.rows.forEach(row => {
        html += '<tr>';
        const count = Math.max(block.header.length, row.length);
        for (let i = 0; i < count; i++) {
            html += renderCell(row[i] || '', 'td', i);
        }
        html += '</tr>';
    });
    return `${html}</tbody></table>`;
}

function indentColumnLength(value, tabSize = 4) {
    let column = 0;
    for (const ch of String(value || '')) {
        if (ch === '\t') {
            column += Math.max(1, tabSize) - (column % Math.max(1, tabSize));
        } else {
            column++;
        }
    }
    return column;
}

function stripIndentColumns(line, columns, tabSize = 4) {
    const text = String(line || '');
    const target = Math.max(0, Number(columns || 0));
    let column = 0;
    let index = 0;
    while (index < text.length && column < target) {
        const ch = text[index];
        if (ch !== ' ' && ch !== '\t') break;
        column += ch === '\t'
            ? Math.max(1, tabSize) - (column % Math.max(1, tabSize))
            : 1;
        index++;
    }
    return text.slice(index);
}

function leadingWhitespaceLength(line, tabSize = 4) {
    const match = /^(\s*)/.exec(line || '');
    return match ? indentColumnLength(match[1], tabSize) : 0;
}

function listItemInfo(text, options = {}) {
    const tabSize = Number(options.tabSize || 4);
    const match = /^([ \t]*)([-*+]|\d+\.)(?:\s+\[([ xX])\])?\s+(.+)$/.exec(text || '');
    if (!match) return null;
    const ordered = /\d+\./.test(match[2]);
    return {
        type: ordered ? 'ol' : 'ul',
        indent: indentColumnLength(match[1], tabSize),
        marker: match[2],
        content: match[4],
        startNum: ordered ? Number.parseInt(match[2], 10) : 1,
        taskChecked: match[3] ? match[3].toLowerCase() === 'x' : null
    };
}

function isMarkdownListStart(lineNumber, getLine, options = {}) {
    const current = listItemInfo(getLine(lineNumber), options);
    if (!current) return false;
    const previous = getLine(lineNumber - 1);
    const previousItem = listItemInfo(previous, options);
    if (previousItem) return previousItem.indent > current.indent;
    return true;
}

function collectMarkdownListBlock(startLine, maxLine, getLine, options = {}) {
    const first = listItemInfo(getLine(startLine), options);
    if (!first) return null;

    const lines = [];
    let endLine = startLine;
    for (let lineNumber = startLine; lineNumber <= maxLine; lineNumber++) {
        const line = getLine(lineNumber);
        if (line === undefined) break;

        const item = listItemInfo(line, options);
        if (item) {
            if (item.indent < first.indent) break;
            lines.push(line);
            endLine = lineNumber;
            continue;
        }

        if (!String(line || '').trim() ||
            leadingWhitespaceLength(line, Number(options.tabSize || 4)) > first.indent) {
            lines.push(line);
            endLine = lineNumber;
            continue;
        }

        break;
    }

    return lines.length ? { lines, startLine, endLine, baseIndent: first.indent } : null;
}

function findMarkdownListBlockContaining(lineNumber, maxLine, getLine, options = {}) {
    const targetLine = Math.min(Math.max(1, Number(lineNumber || 1)), maxLine);
    let firstKnownLine = targetLine;
    while (firstKnownLine > 1 && getLine(firstKnownLine - 1) !== undefined) {
        firstKnownLine--;
    }

    for (let startLine = targetLine; startLine >= firstKnownLine; startLine--) {
        if (!isMarkdownListStart(startLine, getLine, options)) continue;

        const block = collectMarkdownListBlock(startLine, maxLine, getLine, options);
        if (block && targetLine >= block.startLine && targetLine <= block.endLine) {
            return {
                kind: 'list',
                startLine: block.startLine,
                endLine: block.endLine
            };
        }
    }

    return null;
}

function renderListContinuationParagraph(lines, startIndex, parentIndent, options = {}) {
    const tabSize = Number(options.tabSize || 4);
    const paragraphs = [];
    let index = startIndex;
    while (index < lines.length) {
        const text = lines[index];
        if (listItemInfo(text, options)) break;
        if (!String(text || '').trim()) {
            index++;
            continue;
        }
        if (leadingWhitespaceLength(text, tabSize) <= parentIndent) break;
        paragraphs.push(stripIndentColumns(text, parentIndent + tabSize, tabSize));
        index++;
    }

    if (!paragraphs.length) return { html: '', nextIndex: index };

    const firstFence = fencedCodeInfo(paragraphs[0]);
    if (firstFence) {
        const code = [];
        let codeIndex = 1;
        for (; codeIndex < paragraphs.length; codeIndex++) {
            if (isFencedCodeClose(paragraphs[codeIndex], firstFence)) {
                return {
                    html: renderFencedCodeBlock({ code: code.join('\n'), language: firstFence.language }),
                    nextIndex: startIndex + codeIndex + 1
                };
            }
            code.push(paragraphs[codeIndex]);
        }
        return {
            html: renderFencedCodeBlock({ code: code.join('\n'), language: firstFence.language }),
            nextIndex: startIndex + codeIndex
        };
    }

    const html = paragraphs
        .filter(text => String(text || '').trim())
        .map(text => `<p>${inlineMarkdown(text.trim(), options)}</p>`)
        .join('');
    return { html, nextIndex: index };
}

function renderMarkdownListAt(lines, startIndex, indent, options = {}) {
    const firstInfo = listItemInfo(lines[startIndex], options);
    if (!firstInfo || firstInfo.indent !== indent) return null;

    const tag = firstInfo.type;
    const startAttr = tag === 'ol' && firstInfo.startNum !== 1 ? ` start="${firstInfo.startNum}"` : '';
    const items = [];
    let index = startIndex;

    while (index < lines.length) {
        const info = listItemInfo(lines[index], options);
        if (!info || info.indent !== indent || info.type !== tag) break;

        let itemHtml = info.taskChecked === null
            ? inlineMarkdown(info.content, options)
            : `<input type="checkbox" disabled${info.taskChecked ? ' checked' : ''}> ${inlineMarkdown(info.content, options)}`;
        const itemClass = info.taskChecked === null ? '' : ' class="task-list-item"';
        index++;

        while (index < lines.length) {
            const nextInfo = listItemInfo(lines[index], options);
            if (nextInfo) {
                if (nextInfo.indent > indent) {
                    const nested = renderMarkdownListAt(lines, index, nextInfo.indent, options);
                    if (!nested) break;
                    itemHtml += nested.html;
                    index = nested.nextIndex;
                    continue;
                }
                break;
            }

            const continuation = renderListContinuationParagraph(lines, index, indent, options);
            if (!continuation.html && continuation.nextIndex === index) break;
            itemHtml += continuation.html;
            index = continuation.nextIndex;
        }

        items.push(`<li${itemClass}>${itemHtml}</li>`);
    }

    return { html: `<${tag}${startAttr}>${items.join('')}</${tag}>`, nextIndex: index };
}

function renderMarkdownListBlock(block, options = {}) {
    let html = '';
    let index = 0;
    while (index < block.lines.length) {
        const info = listItemInfo(block.lines[index], options);
        if (!info) {
            index++;
            continue;
        }

        const rendered = renderMarkdownListAt(block.lines, index, info.indent, options);
        if (!rendered) {
            index++;
            continue;
        }
        html += rendered.html;
        index = rendered.nextIndex;
    }
    return html;
}

function renderMarkdownLine(line, options = {}) {
    const raw = String(line ?? '');
    const trimmed = raw.trim();
    if (!trimmed) return '';
    if (/^---+$|^\*\*\*+$|^___+$/.test(trimmed)) return '<hr>';
    const heading = /^(#{1,6})\s+(.*)$/.exec(raw);
    if (heading) {
        const level = heading[1].length;
        return `<h${level}>${inlineMarkdown(heading[2], options)}</h${level}>`;
    }
    const quote = /^\s*>\s?(.*)$/.exec(raw);
    if (quote) return `<blockquote>${inlineMarkdown(quote[1], options)}</blockquote>`;
    const listInfo = listItemInfo(raw, options);
    if (listInfo) {
        const startAttr = listInfo.type === 'ol' && listInfo.startNum !== 1 ? ` start="${listInfo.startNum}"` : '';
        const indentStyle = listInfo.indent > 0 ? ` style="margin-left:${Math.max(0, listInfo.indent) * 0.75}ch"` : '';
        const itemClass = listInfo.taskChecked === null ? '' : ' class="task-list-item"';
        const itemHtml = listInfo.taskChecked === null
            ? inlineMarkdown(listInfo.content, options)
            : `<input type="checkbox" disabled${listInfo.taskChecked ? ' checked' : ''}> ${inlineMarkdown(listInfo.content, options)}`;
        return `<${listInfo.type}${startAttr}${indentStyle}><li${itemClass}>${itemHtml}</li></${listInfo.type}>`;
    }
    const singleLineHtml = /^<([A-Za-z][A-Za-z0-9:-]*)(?:\s[^<>]*?)?>[\s\S]*<\/\1\s*>$/.exec(trimmed);
    if (singleLineHtml) return sanitizeHtml(trimmed, options);
    return `<p>${inlineMarkdown(raw, options)}</p>`;
}

function renderLine(line, mode = 'markdown', options = {}) {
    if (mode === 'html') return sanitizeHtml(line, options);
    if (mode === 'latex') return renderLatexLine(line);
    if (mode === 'aozora') return renderAozoraLine(line);
    return renderMarkdownLine(line, options);
}

function renderBlockAt(lineNumber, maxLine, getLine, options = {}) {
    const line = getLine(lineNumber);
    if (line === undefined) return null;
    const mode = options.mode || 'markdown';
    if (mode !== 'markdown') {
        return { html: renderLine(line, mode, options), endLine: lineNumber };
    }
    if (fencedCodeInfo(line)) {
        const block = collectFencedCodeBlock(lineNumber, maxLine, getLine, options);
        if (block?.pending) {
            return block;
        }
        if (block) {
            return { html: renderFencedCodeBlock(block), endLine: block.endLine };
        }
    }
    if (htmlBlockTagName(line)) {
        const block = collectHtmlBlock(lineNumber, maxLine, getLine);
        if (block) {
            return { html: sanitizeHtml(block.html, options), endLine: block.endLine };
        }
    }
    if (isDisplayMathFence(line)) {
        const block = collectDisplayMathBlock(lineNumber, maxLine, getLine);
        if (block) {
            return { html: renderLatexLine(block.text), endLine: block.endLine };
        }
    }
    if (options.renderListsAsBlocks !== false && isMarkdownListStart(lineNumber, getLine, options)) {
        const block = collectMarkdownListBlock(lineNumber, maxLine, getLine, options);
        if (block) {
            return { html: renderMarkdownListBlock(block, options), endLine: block.endLine };
        }
    }
    if (isMarkdownTableStart(lineNumber, getLine)) {
        const block = collectMarkdownTableBlock(lineNumber, maxLine, getLine);
        if (block) {
            return { html: renderMarkdownTableBlock(block, options), endLine: block.endLine };
        }
    }
    return { html: renderMarkdownLine(line, options), endLine: lineNumber };
}

function renderPreviewLineAt(lineNumber, maxLine, getLine, options = {}, skipUntilRef = { val: 0 }) {
    const rangeEnd = Number(options.rangeEnd || lineNumber);
    const line = getLine(lineNumber);
    if (line === undefined) {
        return { pending: true, extendRangeEnd: rangeEnd };
    }

    if (lineNumber <= skipUntilRef.val) {
        return {
            html: '',
            skipped: true,
            extraClass: 'skipped',
            extendRangeEnd: rangeEnd
        };
    }

    const block = renderBlockAt(lineNumber, maxLine, getLine, options);
    if (block?.pending) {
        return {
            pending: true,
            extendRangeEnd: Math.max(rangeEnd, block.extendRangeEnd || block.endLine || lineNumber)
        };
    }

    const rendered = block ? block.html : renderLine(line || '', options.mode || 'markdown', options);
    const endLine = block?.endLine || lineNumber;
    const newSkip = Math.max(skipUntilRef.val, endLine);
    skipUntilRef.val = newSkip;

    return {
        html: rendered,
        source: false,
        skipped: false,
        empty: !rendered,
        extraClass: rendered === '' ? 'empty' : '',
        endLine,
        extendRangeEnd: Math.max(rangeEnd, newSkip),
        activeInside: false
    };
}

function renderPreviewLineRange(startLine, endLine, maxLine, getLine, options = {}) {
    const results = [];
    const skipRef = { val: Number(options.skipUntil || 0) };
    let rangeEnd = endLine;

    for (let lineNumber = startLine; lineNumber <= rangeEnd; lineNumber++) {
        const result = renderPreviewLineAt(
            lineNumber,
            maxLine,
            getLine,
            { ...options, rangeEnd },
            skipRef);

        if (result.extendRangeEnd > rangeEnd) {
            rangeEnd = result.extendRangeEnd;
        }

        results.push({ lineNumber, ...result });
    }

    return { results, endLine: rangeEnd, skipUntil: skipRef.val };
}

let mermaidCounter = 0;
async function renderMermaidBlocks(root, onRendered) {
    if (typeof mermaid === 'undefined' || !root) return;
    const blocks = root.querySelectorAll('.mermaid-block:not(.rendered)');
    if (blocks.length === 0) return;
    for (const block of blocks) {
        if (block.classList.contains('rendering') || block.classList.contains('rendered')) continue;
        block.classList.add('rendering');
        const code = block.dataset.code || '';
        const id = `mermaid-svg-${++mermaidCounter}`;
        try {
            const { svg } = await mermaid.render(id, code);
            block.innerHTML = svg;
            block.classList.remove('rendering');
            block.classList.add('rendered');
            mermaidCache.set(code, svg);
            if (typeof onRendered === 'function') onRendered();
        } catch (err) {
            block.innerHTML = `<pre class="mermaid-error">${escapeHtml(err?.message || err)}</pre>`;
            block.classList.remove('rendering');
            block.classList.add('rendered');
            document.getElementById(id)?.remove();
            if (typeof onRendered === 'function') onRendered();
        }
    }
}

function initializeMermaid(theme = 'dark') {
    if (typeof mermaid !== 'undefined') {
        mermaidCache.clear();
        mermaid.initialize({
            startOnLoad: false,
            securityLevel: 'loose',
            theme: String(theme || '').toLowerCase() === 'light' ? 'default' : 'dark'
        });
    }
}

export {
    escapeAttribute,
    escapeHtml,
    fencedCodeInfo,
    findEditablePreviewBlockContaining,
    initializeMermaid,
    inlineMarkdown,
    renderBlockAt,
    renderLatex,
    renderLatexLine,
    renderLine,
    renderMarkdownLine,
    renderMermaidBlocks,
    renderPreviewLineAt,
    renderPreviewLineRange,
    sanitizeHtml
};
