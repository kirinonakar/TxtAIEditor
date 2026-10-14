import { containsInlineMath, escapeHtml, findEditablePreviewBlockContaining, inlineMarkdown, renderPreviewLineAt, sanitizeHtml } from './markdown-preview-renderer.js';
import { parseMarkdownTableRow } from './editor-markdown-table-model.js';

// Every text node remains part of the Markdown source, in source order. Hiding
// delimiters rather than deleting them lets the existing edit/undo/save protocol
// keep using source columns, including during native IME composition.
const syntax = text => `<span class="md-preview-syntax" contenteditable="false">${escapeHtml(text)}</span>`;
const content = (html, options = {}) => `<span class="md-preview-content${options.htmlSource ? ' md-preview-html-content' : ''}"${options.editableCell !== undefined ? ` contenteditable="${options.editableCell}" tabindex="-1"` : ''}>${html}</span>`;

function safeOpeningTag(raw, options) {
    const template = document.createElement('template');
    template.innerHTML = sanitizeHtml(raw, options);
    const element = template.content.firstElementChild;
    if (!element) return '';
    const attrs = [...element.attributes]
        .filter(attr => attr.name !== 'contenteditable' && attr.name !== 'data-line' && attr.name !== 'data-markdown-preview-line')
        .map(attr => ` ${attr.name}="${escapeHtml(attr.value)}"`).join('');
    return `<${element.tagName.toLowerCase()}${attrs}>`;
}

export function usesMarkdownPreviewEditing(state) {
    return state.editInMarkdownPreview && state.inlineLivePreviewEnabled &&
        ['markdown', 'md', 'plaintext', 'latex'].includes(state.language);
}

export function usesMarkdownTableEditing(state) {
    return state.inlineLivePreviewEnabled &&
        (['markdown', 'md'].includes(state.language) || usesMarkdownPreviewEditing(state));
}

export function isStandaloneLatexExpression(text) {
    return /^(?:\$\$[^\n]+\$\$|\$[^$\n]+\$|\\\[[^\n]+\\\]|\\\([^\n]+\\\))$/.test(String(text || '').trim());
}

export function paragraphContainsInlineMath(block, getLine) {
    if (block?.kind !== 'paragraph') return false;
    for (let line = block.startLine; line <= block.endLine; line++) {
        if (containsInlineMath(getLine(line))) return true;
    }
    return false;
}

export function needsMarkdownPreviewBlockRefresh(state, lineNumber) {
    if (!usesMarkdownPreviewEditing(state)) return false;
    const element = document.querySelector(`#viewport .line-text[data-line="${lineNumber}"]`);
    const kind = element?.dataset.markdownPreviewLine;
    const text = state.cache.get(lineNumber) || '';
    if ((!kind && containsInlineMath(text)) || (kind === 'paragraph' && containsInlineMath(text)) ||
        element?.closest('.line-row')?.classList.contains('live-preview-source-math')) return true;
    if (text.trim() && element?.closest('.line-row')?.classList.contains('live-preview-empty')) return true;
    const list = /^[ \t]*(?:[-*+]|\d+\.)[ \t]+/.test(text);
    if ((kind === 'paragraph' && (list || /^ {0,3}#{1,6}[ \t]/.test(text))) || (kind === 'list' && !list)) return true;
    const block = state.inlineLivePreviewEditableBlock;
    return block &&
        lineNumber >= block.startLine && lineNumber <= block.endLine &&
        (block.kind === 'math' || (block.kind === 'code' &&
            ['math', 'latex', 'mermaid'].includes(block.language?.toLowerCase())));
}

function inlineSource(text, options, depth = 0) {
    if (depth > 24) return escapeHtml(text);
    let html = '';
    let index = 0;
    while (index < text.length) {
        const rest = text.slice(index);
        if (/^[ \t]+$/.test(rest) && (!options.htmlSource || index > 0)) {
            html += `<span class="md-preview-trailing-space">${escapeHtml(rest)}</span>`;
            break;
        }
        const escaped = !options.htmlSource && /^\\([\\`*_{}\[\]()#+.!>~|=-])/.exec(rest);
        if (escaped) {
            html += syntax('\\') + escapeHtml(escaped[1]);
            index += escaped[0].length;
            continue;
        }
        const htmlTag = /^(<(strong|b|em|i|s|del|mark|u|code|a|span|font|big|small|sub|sup|kbd|div|p|center|figure|figcaption|h[1-6])(?:\s[^>]*)?>)([\s\S]*?)(<\/\2\s*>)/i.exec(rest);
        if (htmlTag) {
            const name = htmlTag[2].toLowerCase();
            const opening = name === 'del' ? '<s>' : safeOpeningTag(htmlTag[1], options);
            const closing = `</${name === 'del' ? 's' : name}>`;
            html += syntax(htmlTag[1]) + opening + content(name === 'code'
                ? escapeHtml(htmlTag[3]) : inlineSource(htmlTag[3], options, depth + 1), options) + closing + syntax(htmlTag[4]);
            index += htmlTag[0].length;
            continue;
        }
        const rawTag = /^(?:<!--[\s\S]*?-->|<![A-Za-z][^>]*>|<\/?[A-Za-z][A-Za-z0-9:-]*(?:\s+[^<>]*?)?\s*\/?>)/.exec(rest);
        if (rawTag && !/^<br(?:\s|\/|>)/i.test(rawTag[0])) {
            // Standalone/unsupported tags are source metadata, never literal
            // preview text. Render only safe void elements with no text nodes.
            const rendered = inlineMarkdown(rawTag[0], options);
            html += /^<(?:img|hr)(?:\s|>)/i.test(rendered)
                ? `<span class="md-preview-atom">${syntax(rawTag[0])}${rendered}</span>`
                : syntax(rawTag[0]);
            index += rawTag[0].length;
            continue;
        }
        const br = /^<br(?:\s+[^<>]*?)?\s*\/?>/i.exec(rest);
        if (br) {
            html += `<span class="md-preview-atom">${syntax(br[0])}<br></span>`;
            if (br[0].length === rest.length) html += '<span class="md-preview-content md-preview-br-anchor"></span>';
            index += br[0].length;
            continue;
        }
        const code = !options.htmlSource && /^(`+)([\s\S]*?)\1(?!`)/.exec(rest);
        if (code) {
            html += syntax(code[1]) + `<code>${content(escapeHtml(code[2]))}</code>` + syntax(code[1]);
            index += code[0].length;
            continue;
        }
        const image = !options.htmlSource && /^!\[([^\]]*)\]\(([^)]*)\)/.exec(rest);
        if (image) {
            const rendered = inlineMarkdown(image[0], options);
            // Only an img has no text nodes of its own. Unsafe/failing images
            // stay literal, so their fallback text cannot duplicate the source.
            html += rendered.startsWith('<img ')
                ? `<span class="md-preview-atom">${syntax(image[0])}${rendered}</span>`
                : escapeHtml(image[0]);
            index += image[0].length;
            continue;
        }
        const link = !options.htmlSource && /^\[([^\]]+)\]\(([^)]*)\)/.exec(rest);
        if (link) {
            html += syntax('[') + `<span class="md-preview-link">${content(inlineSource(link[1], options, depth + 1))}</span>` +
                syntax(`](${link[2]})`);
            index += link[0].length;
            continue;
        }
        const emphasis = !options.htmlSource && /^(\*\*\*|___|\*\*|__|~~|==|\*|_)(\S[\s\S]*?)\1/.exec(rest);
        if (emphasis) {
            const marker = emphasis[1];
            const tag = marker === '~~' ? 's' : marker === '==' ? 'mark'
                : marker.length === 1 ? 'em' : 'strong';
            const body = content(inlineSource(emphasis[2], options, depth + 1));
            html += syntax(marker) + `<${tag}>${marker.length === 3 ? `<em>${body}</em>` : body}</${tag}>` + syntax(marker);
            index += emphasis[0].length;
            continue;
        }
        html += escapeHtml(text[index++]);
    }
    return html;
}

function tableSource(text, header, options) {
    let html = '';
    let start = 0;
    for (let index = 0; index <= text.length; index++) {
        if (index !== text.length && (text[index] !== '|' || text[index - 1] === '\\')) continue;
        const cell = text.slice(start, index);
        if (cell || (start > 0 && index < text.length)) {
            html += `<span class="md-preview-cell${header ? ' md-preview-cell-header' : ''}">${content(inlineSource(cell, options))}</span>`;
        }
        if (index < text.length) html += syntax('|');
        start = index + 1;
    }
    return `<span class="md-preview-table">${html}</span>`;
}

function editableAttributes(text, lineNumber, state, kind) {
    return `class="line-text md-preview-embedded-line" data-markdown-preview-line="${kind}" data-line="${lineNumber}" contenteditable="${!state.readOnly && text.length <= 20000}" spellcheck="false"`;
}

function listItemSource(text, info, options) {
    const prefix = text.slice(0, text.length - info.content.length);
    if (info.taskChecked !== null) {
        const space = /[ \t]+$/.exec(prefix)?.[0] || '';
        return syntax(prefix.slice(0, prefix.length - space.length)) +
            `<input type="checkbox" disabled${info.taskChecked ? ' checked' : ''}>` + escapeHtml(space) + content(inlineSource(info.content, options));
    }
    return syntax(prefix) + content(inlineSource(info.content, options));
}

function trimmedInlineSource(text, options, cellPadding = false) {
    // One boundary space is conventional table padding. Additional spaces are
    // editable content: hiding them snaps the caret back before a newly typed
    // space and puts the next Korean syllable in the preceding word.
    const leading = text.length - text.trimStart().length;
    const start = cellPadding ? Math.min(1, leading) : leading;
    const end = cellPadding && /[ \t]$/.test(text) ? Math.max(start, text.length - 1) : text.length;
    if (end <= start) return syntax(text.slice(0, start)) + content('', options) + syntax(text.slice(start));
    return syntax(text.slice(0, start)) + content(inlineSource(text.slice(start, end), options), options) + syntax(text.slice(end));
}

function tableRowSource(text, header, aligns, columnCount, options) {
    const parsed = parseMarkdownTableRow(text);
    const first = (parsed?.prefix.length || 1) - 1;
    const last = parsed ? text.length - parsed.suffix.length : text.length - 1;
    const cells = parsed?.cells.map(cell => cell.text) || [];
    const count = Math.max(columnCount, cells.length);
    const tag = header ? 'th' : 'td';
    return Array.from({ length: count }, (_, index) => {
        const cell = cells[index] || '';
        const align = aligns[index] || '';
        const style = [align ? `text-align:${align}` : '', /<br\s*\/?>/i.test(cell) ? 'white-space:pre-line' : ''].filter(Boolean).join(';');
        const prefix = index === 0 ? syntax(text.slice(0, first + 1)) : index < cells.length ? syntax('|') : '';
        const suffix = index === cells.length - 1 ? syntax(text.slice(last)) : '';
        return `<${tag} contenteditable="false"${style ? ` style="${style}"` : ''}>${prefix}${trimmedInlineSource(cell, options, true)}${suffix}</${tag}>`;
    }).join('');
}

function editableHtmlBlock(block, startLine, state, options) {
    const lines = block.html.split('\n');
    const opening = /^\s*<(div|p|center|figure|figcaption|h[1-6])(?:\s[^>]*)?>\s*$/i.exec(lines[0]);
    const htmlOptions = { ...options, htmlSource: true };
    if (lines.length === 1 && /^\s*<(?:div|p|center|figure|figcaption|h[1-6])(?:\s|>)/i.test(lines[0])) {
        return `<span ${editableAttributes(lines[0], startLine, state, 'html')}>${content(inlineSource(lines[0], htmlOptions), htmlOptions)}</span>`;
    }
    if (!opening || !new RegExp(`^\\s*</${opening[1]}\\s*>\\s*$`, 'i').test(lines.at(-1))) return null;
    // Keep the original HTML containers around the editable source lines.
    // Tag-only boundary lines carry source metadata without adding a visual
    // line or another copy of an image contained in the block.
    return lines.map((text, index) => {
        const line = startLine + index;
        const tag = /^\s*(<\/?(?:div|p|center|figure|figcaption|h[1-6])(?:\s[^>]*)?>)\s*$/i.exec(text);
        if (tag) {
            const boundary = `<span class="line-text md-preview-embedded-line md-preview-html-boundary" data-markdown-preview-line="html" data-line="${line}" contenteditable="false">${syntax(text)}</span>`;
            return /^<\//.test(tag[1]) ? boundary + tag[1] : safeOpeningTag(tag[1], options) + boundary;
        }
        return `<span ${editableAttributes(text, line, state, 'html')}>${content(inlineSource(text, htmlOptions), htmlOptions)}</span>`;
    }).join('\n');
}

function editableCodeBlock(block, startLine, state, options, sourceLines = null, sourceLineNumbers = null) {
    if (['math', 'latex', 'mermaid'].includes(block.language?.toLowerCase())) return null;
    const lines = sourceLines || Array.from({ length: block.endLine - startLine + 1 }, (_, index) => state.cache.get(startLine + index) || '');
    const boundary = (text, line) => `<span class="line-text md-preview-embedded-line md-preview-code-fence" data-markdown-preview-line="code-fence" data-line="${line}" contenteditable="false">${syntax(text)}</span>`;
    const langClass = block.language ? ` class="language-${escapeHtml(block.language)}"` : '';
    if (lines.length === 2 && !block.code) {
        return `<pre><code${langClass}>${boundary(lines[0], startLine)}<span ${editableAttributes(lines[1], block.endLine, state, 'code-empty')}><span class="md-preview-content md-preview-code-content"></span>${syntax(lines[1])}</span></code></pre>`;
    }
    const body = block.code.split('\n');
    const html = body.map((text, index) => {
        const source = lines[index + 1] ?? text;
        const prefix = source.endsWith(text) ? source.slice(0, source.length - text.length) : '';
        const line = sourceLineNumbers?.[index + 1] ?? startLine + index + 1;
        return `<span ${editableAttributes(source, line, state, 'code')}>${syntax(prefix)}<span class="md-preview-content md-preview-code-content">${escapeHtml(text)}</span></span>`;
    }).join('\n');
    return `<pre><code${langClass}>${boundary(lines[0], startLine)}${html}${boundary(lines.at(-1), block.endLine)}</code></pre>`;
}

export function renderGroupedEditableMarkdown(lineNumber, state, options = {}, skipUntil = 0) {
    const editableOptions = {
        ...options, sourceLine: 0,
        editableListItem: (text, line, info) => `<span ${editableAttributes(text, line, state, 'list')}>${listItemSource(text, info, options)}</span>`,
        editableListContinuation: (text, line) => `<span ${editableAttributes(text, line, state, 'continuation')}>${trimmedInlineSource(text, options)}</span>`,
        editableParagraphLine: (text, line) => `<span ${editableAttributes(text, line, state, 'paragraph')} data-markdown-paragraph-line="${line}">${content(inlineSource(text, options))}</span>`,
        editableHtmlBlock: (block, line) => editableHtmlBlock(block, line, state, options),
        editableCodeBlock: (block, line, lines, numbers) => editableCodeBlock(block, line, state, options, lines, numbers),
        editableTableRow: (text, line, header, aligns, count) =>
            `<tr ${editableAttributes(text, line, state, 'table')} data-preview-table-header="${header}" data-preview-table-aligns="${escapeHtml(JSON.stringify(aligns))}" data-preview-table-columns="${count}">${tableRowSource(text, header, aligns, count, { ...options, editableCell: !state.readOnly && text.length <= 20000 })}</tr>`
    };
    return renderPreviewLineAt(lineNumber, state.lineCount,
        line => state.cache.get(line), editableOptions, { val: skipUntil });
}

// Patch only the original editable item/row. Its surrounding ul/ol/li or
// table/thead/tbody/td layout stays owned by the canonical preview renderer.
export function updateGroupedMarkdownElement(element, text, state) {
    const kind = element?.dataset.markdownPreviewLine;
    if (!kind || !(usesMarkdownPreviewEditing(state) || kind === 'table' && usesMarkdownTableEditing(state))) return false;
    if (kind === 'paragraph' && /^(?:[ \t]*(?:[-*+]|\d+\.)[ \t]+| {0,3}#{1,6}[ \t])/.test(text)) return false;
    const options = { baseHref: state.livePreviewBaseHref || '', tabSize: state.tabSize || 4,
        localResourceVersion: state.livePreviewLocalResourceVersion || '0' };
    if (kind === 'table') {
        element.innerHTML = tableRowSource(text, element.dataset.previewTableHeader === 'true',
            JSON.parse(element.dataset.previewTableAligns || '[]'), Number(element.dataset.previewTableColumns || 0), { ...options, editableCell: !state.readOnly });
    } else if (kind === 'list') {
        const match = /^([ \t]*)([-*+]|\d+\.)([ \t]+)(?:\[([ xX])\]([ \t]+))?(.*)$/.exec(text);
        element.innerHTML = match ? listItemSource(text, { content: match[6], taskChecked: match[4] === undefined ? null : match[4].toLowerCase() === 'x' }, options)
            : content(inlineSource(text, options));
    } else if (kind === 'continuation') {
        element.innerHTML = trimmedInlineSource(text, options);
    } else if (kind === 'html') {
        const htmlOptions = { ...options, htmlSource: true };
        element.innerHTML = content(inlineSource(text, htmlOptions), htmlOptions);
    } else if (kind === 'code') {
        const prefix = element.querySelector('.md-preview-syntax')?.textContent || '';
        const indentation = text.startsWith(prefix) ? prefix : '';
        element.innerHTML = syntax(indentation) + `<span class="md-preview-content md-preview-code-content">${escapeHtml(text.slice(indentation.length))}</span>`;
    } else {
        element.innerHTML = content(inlineSource(text, options));
    }
    return true;
}

export function renderEditableMarkdownLine(text, lineNumber, state) {
    const options = {
        baseHref: state.livePreviewBaseHref || '',
        localResourceVersion: state.livePreviewLocalResourceVersion || '0',
        tabSize: state.tabSize || 4
    };
    if (!text.trim()) return syntax(text) + content('');
    const block = findEditablePreviewBlockContaining(lineNumber, state.lineCount,
        line => line === lineNumber ? text : state.cache.get(line), options);
    if ((!block && containsInlineMath(text)) || paragraphContainsInlineMath(block, line => line === lineNumber ? text : state.cache.get(line))) {
        return escapeHtml(text);
    }
    if (block?.kind === 'math' || (block?.kind === 'code' && ['math', 'latex', 'mermaid'].includes(block.language?.toLowerCase()))) {
        return escapeHtml(text);
    }
    if (block?.kind === 'code') {
        const fence = lineNumber < block.bodyStartLine || lineNumber > block.bodyEndLine;
        return fence ? syntax(text)
            : `<span class="md-preview-code-line">${content(escapeHtml(text))}</span>`;
    }
    if (block?.kind === 'table') {
        return lineNumber === block.separatorLine ? syntax(text)
            : tableSource(text, lineNumber === block.startLine, options);
    }
    if (/^\s*</.test(text)) {
        const htmlOptions = { ...options, htmlSource: true };
        return content(inlineSource(text, htmlOptions), htmlOptions);
    }
    const heading = /^( {0,3}#{1,6})([ \t])(.*)$/.exec(text);
    if (heading) {
        const level = heading[1].trim().length;
        if (!heading[3].trim()) {
            return `<h${level} class="md-preview-empty-heading"><span class="md-preview-syntax md-preview-heading-prefix" contenteditable="false">${escapeHtml(text)}</span><span class="md-preview-content md-preview-heading-body"></span></h${level}>`;
        }
        const closing = /^(.*?)([ \t]#+[ \t]*)$/.exec(heading[3]);
        return syntax(heading[1] + heading[2]) + `<h${level}>${content(inlineSource(closing ? closing[1] : heading[3], options))}</h${level}>` +
            (closing ? syntax(closing[2]) : '');
    }
    const quote = /^( {0,3}>[ \t]?)(.*)$/.exec(text);
    if (quote) return syntax(quote[1]) + `<blockquote>${content(inlineSource(quote[2], options))}</blockquote>`;
    const list = /^([ \t]*)([-*+]|\d+\.)([ \t]+)(?:\[([ xX])\]([ \t]+))?(.*)$/.exec(text);
    if (list) {
        const prefix = text.slice(0, text.length - list[6].length);
        const marker = list[4] !== undefined ? (list[4].toLowerCase() === 'x' ? '☑' : '☐')
            : /^\d/.test(list[2]) ? list[2] : '•';
        const indent = list[1].replace(/\t/g, ' '.repeat(options.tabSize)).length;
        return syntax(prefix) + `<span class="md-preview-list" style="margin-left:${indent}ch" data-marker="${escapeHtml(marker)}">${content(inlineSource(list[6], options))}</span>`;
    }
    if (/^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$/.test(text)) {
        return content(`<span class="md-preview-atom">${syntax(text)}<hr></span>`);
    }
    return content(inlineSource(text, options));
}

// A source offset can point into an invisible delimiter. Find the nearest
// visible text boundary, while still counting the hidden source characters.
export function ensureMarkdownPreviewCaretAnchors(element) {
    for (const empty of element.querySelectorAll('.md-preview-content:empty, .md-preview-content[data-markdown-empty-content]')) {
        if (empty.textContent.length) continue;
        if (![...empty.childNodes].some(node => node.nodeType === Node.TEXT_NODE)) empty.prepend(document.createTextNode(''));
        // Give empty bodies their own editing host. Chromium may normalize an
        // empty text boundary to a neighboring hidden prefix in the outer host.
        if (!empty.hasAttribute('contenteditable')) {
            empty.setAttribute('contenteditable', 'true');
            empty.tabIndex = -1;
        }
        empty.dataset.markdownEmptyContent = 'true';
        if (!empty.querySelector('br[data-markdown-caret-anchor]')) {
            const anchor = document.createElement('br');
            anchor.dataset.markdownCaretAnchor = 'true';
            empty.appendChild(anchor);
        }
    }
}

export function markdownPreviewTextPosition(element, offset, direction = 1) {
    if (!element?.querySelector('.md-preview-content')) return null;
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    const visible = [];
    const atoms = new Set();
    let sourceOffset = 0;
    let node;
    while ((node = walker.nextNode())) {
        const end = sourceOffset + node.textContent.length;
        const atom = node.parentElement?.closest('.md-preview-atom');
        if (atom && !atoms.has(atom)) {
            atoms.add(atom);
            visible.push({ node: atom, start: sourceOffset, end: sourceOffset + atom.textContent.length, atom: true });
        } else if (!atom && !node.parentElement?.closest('.md-preview-syntax')) {
            visible.push({ node, start: sourceOffset, end });
        }
        sourceOffset = end;
    }
    for (const empty of element.querySelectorAll('.md-preview-content:empty')) {
        const before = document.createRange();
        before.selectNodeContents(element);
        before.setEndBefore(empty);
        const start = before.toString().length;
        if (!empty.firstChild) visible.push({ node: empty, start, end: start, empty: true });
    }
    visible.sort((a, b) => a.start - b.start);
    const target = Math.max(0, Number(offset || 0));
    const emptyBoundary = visible.find(item => item.empty && item.start === target);
    if (emptyBoundary) return { node: emptyBoundary.node, offset: 0, sourceOffset: target };
    for (const item of visible) {
        if (target >= item.start && target <= item.end) {
            if (item.empty) return { node: item.node, offset: 0, sourceOffset: item.start };
            if (item.atom) {
                const after = target === item.end || (target > item.start && direction > 0);
                return { node: item.node, offset: after ? item.node.childNodes.length : 0,
                    sourceOffset: after ? item.end : item.start };
            }
            return { node: item.node, offset: target - item.start, sourceOffset: target };
        }
    }
    const previous = visible.filter(item => item.end < target).at(-1);
    const next = visible.find(item => item.start > target);
    const item = direction < 0 ? (previous || next) : (next || previous);
    return item ? { node: item.node, offset: item === previous
        ? (item.atom ? item.node.childNodes.length : item.empty ? 0 : item.node.textContent.length) : 0,
        sourceOffset: item === previous ? item.end : item.start } : null;
}

export function markdownPreviewDeleteRange(element, offset, direction, deleteStart, deleteEnd) {
    if (!element?.querySelector('.md-preview-content')) return null;
    const selectionNode = window.getSelection()?.rangeCount ? window.getSelection().getRangeAt(0).startContainer : null;
    const caretCell = element.dataset.markdownPreviewLine === 'table'
        ? (selectionNode?.nodeType === Node.ELEMENT_NODE ? selectionNode : selectionNode?.parentElement)?.closest('td, th') : null;
    const cellScope = caretCell && element.contains(caretCell) ? caretCell : null;
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let sourceOffset = 0;
    let previous = null;
    const atoms = new Set();
    let node;
    while ((node = walker.nextNode())) {
        const text = node.textContent;
        const end = sourceOffset + text.length;
        if (cellScope && !cellScope.contains(node)) {
            sourceOffset = end;
            continue;
        }
        const atom = node.parentElement?.closest('.md-preview-atom');
        if (atom && !atoms.has(atom)) {
            atoms.add(atom);
            const atomEnd = sourceOffset + atom.textContent.length;
            if (direction > 0 && atomEnd > offset) return { start: sourceOffset, end: atomEnd };
            if (direction < 0 && sourceOffset < offset) previous = { start: sourceOffset, end: atomEnd };
        } else if (!atom && !node.parentElement?.closest('.md-preview-syntax')) {
            if (direction > 0 && end > offset) {
                const start = Math.max(sourceOffset, offset);
                return { start, end: sourceOffset + deleteEnd(text, start - sourceOffset) };
            }
            if (direction < 0 && sourceOffset < offset && text.length) {
                const boundary = Math.min(offset, end);
                previous = { start: sourceOffset + deleteStart(text, boundary - sourceOffset), end: boundary };
            }
        }
        sourceOffset = end;
    }
    return direction < 0 ? previous : { start: sourceOffset, end: sourceOffset, merge: true };
}
