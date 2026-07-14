import { escapeHtml, state } from './editor-core.js';
import { selectionBoundsForLine } from './editor-selection.js';

const MAX_SYNTAX_CONTEXT_LOOKBACK_LINES = 200;

function cleanLineForBrackets(text) {
    let clean = text.replace(/\/\/.*/g, '');
    clean = clean.replace(/"(?:\\.|[^"\\])*"/g, '');
    clean = clean.replace(/'(?:\\.|[^'\\])*'/g, '');
    clean = clean.replace(/`(?:\\.|[^`\\])*`/g, '');
    clean = clean.replace(/\/\*[\s\S]*?\*\//g, '');
    return clean;
}

function getBracketsFromText(text) {
    const clean = cleanLineForBrackets(text);
    const matches = clean.match(/[(){}\[\]]/g);
    return matches || [];
}

function computeLineEndStack(lineNumber, startStack) {
    const text = state.cache.get(lineNumber) || '';
    const brackets = getBracketsFromText(text);
    const stack = [...startStack];
    const matching = { ')': '(', '}': '{', ']': '[' };
    for (const ch of brackets) {
        if (ch === '(' || ch === '{' || ch === '[') {
            stack.push(ch);
        } else if (ch === ')' || ch === '}' || ch === ']') {
            const target = matching[ch];
            if (stack.length > 0 && stack[stack.length - 1] === target) {
                stack.pop();
            } else {
                stack.pop();
            }
        }
    }
    return stack;
}

function getLineStartStack(lineNumber) {
    if (lineNumber <= 1) return [];
    const prev = state.lineEndStacks.get(lineNumber - 1);
    if (prev) return prev;

    let startLine = lineNumber - 1;
    const minimumContextLine = Math.max(1, lineNumber - MAX_SYNTAX_CONTEXT_LOOKBACK_LINES);
    while (startLine > minimumContextLine && !state.lineEndStacks.has(startLine)) {
        startLine--;
    }

    const cachedStack = state.lineEndStacks.get(startLine);
    let currentStack = cachedStack ? [...cachedStack] : [];
    const scanStart = cachedStack ? startLine + 1 : startLine;
    for (let l = scanStart; l < lineNumber; l++) {
        if (!state.cache.has(l)) {
            currentStack = [];
            continue;
        }
        currentStack = computeLineEndStack(l, currentStack);
        state.lineEndStacks.set(l, currentStack);
    }
    return currentStack;
}

const htmlNamePattern = /^[A-Za-z_][A-Za-z0-9:._-]*/;

function createHtmlLineContext() {
    return {
        inTag: false,
        tagNameSeen: false,
        quote: null,
        special: null
    };
}

function cloneHtmlLineContext(context) {
    return {
        inTag: !!context?.inTag,
        tagNameSeen: !!context?.tagNameSeen,
        quote: context?.quote || null,
        special: context?.special || null
    };
}

function closeHtmlTagContext(context) {
    context.inTag = false;
    context.tagNameSeen = false;
    context.quote = null;
}

function advanceHtmlContext(context, text) {
    const next = cloneHtmlLineContext(context);
    let i = 0;

    while (i < text.length) {
        if (next.special === 'comment') {
            const end = text.indexOf('-->', i);
            if (end < 0) return next;
            next.special = null;
            i = end + 3;
            continue;
        }

        if (next.special === 'cdata') {
            const end = text.indexOf(']]>', i);
            if (end < 0) return next;
            next.special = null;
            i = end + 3;
            continue;
        }

        if (next.special === 'declaration') {
            const end = text.indexOf('>', i);
            if (end < 0) return next;
            next.special = null;
            i = end + 1;
            continue;
        }

        if (next.inTag) {
            if (next.quote) {
                const end = text.indexOf(next.quote, i);
                if (end < 0) return next;
                next.quote = null;
                i = end + 1;
                continue;
            }

            const ch = text[i];
            if (ch === '"' || ch === "'") {
                next.quote = ch;
                i++;
                continue;
            }

            if ((ch === '/' || ch === '?') && text[i + 1] === '>') {
                closeHtmlTagContext(next);
                i += 2;
                continue;
            }

            if (ch === '>') {
                closeHtmlTagContext(next);
                i++;
                continue;
            }

            if (!next.tagNameSeen) {
                const nameMatch = htmlNamePattern.exec(text.slice(i));
                if (nameMatch) {
                    next.tagNameSeen = true;
                    i += nameMatch[0].length;
                    continue;
                }
            }

            i++;
            continue;
        }

        const tagStart = text.indexOf('<', i);
        if (tagStart < 0) return next;

        if (text.startsWith('<!--', tagStart)) {
            next.special = 'comment';
            i = tagStart + 4;
            continue;
        }

        if (text.startsWith('<![CDATA[', tagStart)) {
            next.special = 'cdata';
            i = tagStart + 9;
            continue;
        }

        if (text.startsWith('<!', tagStart)) {
            next.special = 'declaration';
            i = tagStart + 2;
            continue;
        }

        next.inTag = true;
        next.tagNameSeen = false;
        next.quote = null;
        i = tagStart + 1;
        if (text[i] === '/' || text[i] === '?') {
            i++;
        }
    }

    return next;
}

function getHtmlLineStartContext(lineNumber) {
    if (!lineNumber || lineNumber <= 1) return createHtmlLineContext();

    if (!state.htmlLineEndContexts) {
        state.htmlLineEndContexts = new Map();
    }

    const contexts = state.htmlLineEndContexts;
    const previousLine = lineNumber - 1;
    const previousContext = contexts.get(previousLine);
    if (previousContext) return cloneHtmlLineContext(previousContext);

    let context = createHtmlLineContext();
    const minimumContextLine = Math.max(1, lineNumber - MAX_SYNTAX_CONTEXT_LOOKBACK_LINES);
    let scanStart = minimumContextLine;
    let anchor = previousLine;

    while (anchor >= minimumContextLine) {
        const anchorContext = contexts.get(anchor);
        if (anchorContext) {
            context = cloneHtmlLineContext(anchorContext);
            scanStart = anchor + 1;
            break;
        }

        if (!state.cache.has(anchor)) {
            scanStart = anchor + 1;
            break;
        }

        anchor--;
    }

    for (let line = scanStart; line < lineNumber; line++) {
        if (!state.cache.has(line)) {
            context = createHtmlLineContext();
            continue;
        }

        context = advanceHtmlContext(context, state.cache.get(line) || '');
        contexts.set(line, cloneHtmlLineContext(context));
    }

    return cloneHtmlLineContext(context);
}

function getHtmlContextAt(lineNumber, startCharIndex, fallbackText) {
    let context = getHtmlLineStartContext(lineNumber);
    if (lineNumber !== null && startCharIndex > 0) {
        const fullLineText = state.cache.get(lineNumber) ?? fallbackText;
        context = advanceHtmlContext(context, fullLineText.slice(0, startCharIndex));
    }
    return context;
}

function highlightHtmlLine(text, lineNumber, startCharIndex, stash) {
    let context = getHtmlContextAt(lineNumber, startCharIndex, text);
    let output = "";
    let i = 0;

    const emitPlain = value => {
        output += value;
    };
    const emitToken = (className, value) => {
        if (value.length > 0) {
            output += stash(`<span class="${className}">${escapeHtml(value)}</span>`);
        }
    };
    const emitPunctuation = value => emitToken('token-punctuation', value);

    while (i < text.length) {
        if (context.special === 'comment') {
            const end = text.indexOf('-->', i);
            const endIndex = end < 0 ? text.length : end + 3;
            emitToken('token-comment', text.slice(i, endIndex));
            i = endIndex;
            if (end >= 0) {
                context.special = null;
            }
            continue;
        }

        if (context.special === 'cdata') {
            const end = text.indexOf(']]>', i);
            const endIndex = end < 0 ? text.length : end + 3;
            emitPunctuation(text.slice(i, endIndex));
            i = endIndex;
            if (end >= 0) {
                context.special = null;
            }
            continue;
        }

        if (context.special === 'declaration') {
            const end = text.indexOf('>', i);
            const endIndex = end < 0 ? text.length : end + 1;
            emitPunctuation(text.slice(i, endIndex));
            i = endIndex;
            if (end >= 0) {
                context.special = null;
            }
            continue;
        }

        if (context.inTag) {
            if (context.quote) {
                const end = text.indexOf(context.quote, i);
                const endIndex = end < 0 ? text.length : end + 1;
                emitToken('token-string', text.slice(i, endIndex));
                i = endIndex;
                if (end >= 0) {
                    context.quote = null;
                }
                continue;
            }

            const ch = text[i];
            if (/\s/.test(ch)) {
                const whitespace = /^\s+/.exec(text.slice(i))[0];
                emitPlain(whitespace);
                i += whitespace.length;
                continue;
            }

            if ((ch === '/' || ch === '?') && text[i + 1] === '>') {
                emitPunctuation(text.slice(i, i + 2));
                i += 2;
                closeHtmlTagContext(context);
                continue;
            }

            if (ch === '>') {
                emitPunctuation(ch);
                i++;
                closeHtmlTagContext(context);
                continue;
            }

            if (ch === '/' || ch === '?') {
                emitPunctuation(ch);
                i++;
                continue;
            }

            if (ch === '=') {
                emitToken('token-operator', ch);
                i++;
                continue;
            }

            if (ch === '"' || ch === "'") {
                const end = text.indexOf(ch, i + 1);
                const endIndex = end < 0 ? text.length : end + 1;
                emitToken('token-string', text.slice(i, endIndex));
                i = endIndex;
                context.quote = end < 0 ? ch : null;
                continue;
            }

            const nameMatch = htmlNamePattern.exec(text.slice(i));
            if (nameMatch) {
                const className = context.tagNameSeen ? 'token-attr' : 'token-tag';
                emitToken(className, nameMatch[0]);
                context.tagNameSeen = true;
                i += nameMatch[0].length;
                continue;
            }

            emitPlain(ch);
            i++;
            continue;
        }

        const tagStart = text.indexOf('<', i);
        if (tagStart < 0) {
            emitPlain(text.slice(i));
            break;
        }

        emitPlain(text.slice(i, tagStart));

        if (text.startsWith('<!--', tagStart)) {
            const end = text.indexOf('-->', tagStart + 4);
            const endIndex = end < 0 ? text.length : end + 3;
            emitToken('token-comment', text.slice(tagStart, endIndex));
            i = endIndex;
            context.special = end < 0 ? 'comment' : null;
            continue;
        }

        if (text.startsWith('<![CDATA[', tagStart)) {
            const end = text.indexOf(']]>', tagStart + 9);
            const endIndex = end < 0 ? text.length : end + 3;
            emitPunctuation(text.slice(tagStart, endIndex));
            i = endIndex;
            context.special = end < 0 ? 'cdata' : null;
            continue;
        }

        if (text.startsWith('<!', tagStart)) {
            const end = text.indexOf('>', tagStart + 2);
            const endIndex = end < 0 ? text.length : end + 1;
            emitPunctuation(text.slice(tagStart, endIndex));
            i = endIndex;
            context.special = end < 0 ? 'declaration' : null;
            continue;
        }

        emitPunctuation('<');
        i = tagStart + 1;
        context.inTag = true;
        context.tagNameSeen = false;
        context.quote = null;

        if (text[i] === '/' || text[i] === '?') {
            emitPunctuation(text[i]);
            i++;
        }
    }

    return output;
}

function renderTokenizedSegment(fullText, segmentStart, segmentLength, tokens) {
    const segmentEnd = segmentStart + segmentLength;
    let cursor = segmentStart;
    let output = '';

    for (const token of tokens) {
        if (token.end <= segmentStart || token.start >= segmentEnd) continue;

        if (token.start > cursor) {
            output += escapeHtml(fullText.slice(cursor, Math.min(token.start, segmentEnd)));
        }

        const start = Math.max(token.start, segmentStart);
        const end = Math.min(token.end, segmentEnd);
        const value = fullText.slice(start, end);
        output += token.className
            ? `<span class="${token.className}">${escapeHtml(value)}</span>`
            : escapeHtml(value);
        cursor = Math.max(cursor, end);
    }

    if (cursor < segmentEnd) {
        output += escapeHtml(fullText.slice(cursor, segmentEnd));
    }

    return output;
}

function highlightHexLine(text, lineNumber = null, startCharIndex = 0) {
    const fullText = lineNumber !== null && state.cache.has(lineNumber)
        ? state.cache.get(lineNumber) || ''
        : text;
    const segmentStart = Math.max(0, Number(startCharIndex || 0));
    const segmentLength = text.length;

    if (/^\s*Offset\(h\)/.test(fullText)) {
        return renderTokenizedSegment(
            fullText,
            segmentStart,
            segmentLength,
            [{ start: 0, end: fullText.length, className: 'hex-header' }]);
    }

    const offsetMatch = /^(\s*[0-9A-F]{8,16})(\s{2})/.exec(fullText);
    if (!offsetMatch) {
        return escapeHtml(text);
    }

    const tokens = [
        { start: 0, end: offsetMatch[1].length, className: 'hex-offset' }
    ];

    const hexStart = offsetMatch[0].length;
    const firstPipe = fullText.indexOf('|', hexStart);
    const lastPipe = fullText.lastIndexOf('|');
    if (firstPipe < 0 || lastPipe <= firstPipe) {
        return renderTokenizedSegment(fullText, segmentStart, segmentLength, tokens);
    }

    const hexEnd = Math.max(hexStart, firstPipe - 1);
    const rowOffset = lineNumber !== null && lineNumber >= 2
        ? (lineNumber - 2) * 16
        : 0;
    const selection = normalizedHexSelection();
    let byteIndex = 0;
    let pos = hexStart;
    while (pos < hexEnd) {
        const pair = /^[0-9A-F]{2}/.exec(fullText.slice(pos));
        if (pair) {
            const selected = isHexByteSelected(selection, rowOffset + byteIndex);
            tokens.push({
                start: pos,
                end: pos + 2,
                className: `${byteIndex % 2 === 0 ? 'hex-data-even' : 'hex-data-odd'}${selected ? ' hex-selected' : ''}`
            });
            pos += 2;
            byteIndex++;
            continue;
        }

        pos++;
    }

    const asciiStart = firstPipe + 1;
    const asciiEnd = lastPipe;
    for (let i = asciiStart; i < asciiEnd; i++) {
        const asciiIndex = i - asciiStart;
        const selected = isHexByteSelected(selection, rowOffset + asciiIndex);
        tokens.push({
            start: i,
            end: i + 1,
            className: `${asciiIndex % 2 === 0 ? 'hex-data-even' : 'hex-data-odd'}${selected ? ' hex-selected' : ''}`
        });
    }

    tokens.sort((a, b) => a.start - b.start);
    return renderTokenizedSegment(fullText, segmentStart, segmentLength, tokens);
}

function normalizedHexSelection() {
    const selection = state.hexSelection;
    if (!selection) return null;

    const startOffset = Math.max(0, Math.min(Number(selection.startOffset || 0), Number(selection.endOffset || 0)));
    const endOffset = Math.max(startOffset, Math.max(Number(selection.startOffset || 0), Number(selection.endOffset || 0)));
    return endOffset > startOffset
        ? { startOffset, endOffset }
        : null;
}

function isHexByteSelected(selection, byteOffset) {
    return !!selection &&
        byteOffset >= selection.startOffset &&
        byteOffset < selection.endOffset;
}

function highlightLine(text, language, lineNumber = null, startCharIndex = 0) {
    if (!state.syntaxHighlighting && language !== 'hex') {
        return escapeHtml(text);
    }

    if (!language || language === 'plaintext') {
        return escapeHtml(text);
    }

    if (language === 'hex') {
        return highlightHexLine(text, lineNumber, startCharIndex);
    }

    const tokens = [];
    function stash(html) {
        const placeholder = `\u0002_TOKEN_${tokens.length}_\u0002`;
        tokens.push(html);
        return placeholder;
    }

    let workingText = text;

    let currentLineStack = [];
    if (state.bracketPairColorization && lineNumber !== null) {
        const startStack = getLineStartStack(lineNumber);
        const fullLineText = state.cache.get(lineNumber) || '';
        const prefixText = fullLineText.slice(0, startCharIndex);
        const prefixBrackets = getBracketsFromText(prefixText);
        currentLineStack = [...startStack];
        const matchingBrackets = { ')': '(', '}': '{', ']': '[' };
        for (const ch of prefixBrackets) {
            if (ch === '(' || ch === '{' || ch === '[') {
                currentLineStack.push(ch);
            } else if (ch === ')' || ch === '}' || ch === ']') {
                const target = matchingBrackets[ch];
                if (currentLineStack.length > 0 && currentLineStack[currentLineStack.length - 1] === target) {
                    currentLineStack.pop();
                } else {
                    currentLineStack.pop();
                }
            }
        }
    }

    // Check language category
    const isClike = ['csharp', 'javascript', 'typescript', 'cpp', 'java', 'go', 'rust', 'php', 'swift', 'dart', 'kotlin'].includes(language);
    const isPython = language === 'python';
    const isHtml = ['html', 'xml', 'xaml', 'resw'].includes(language);
    const isCss = ['css', 'scss', 'less'].includes(language);
    const isJson = language === 'json';
    const isSql = language === 'sql';
    const isShell = ['shell', 'bash', 'powershell'].includes(language);
    const isMarkdown = ['markdown', 'md'].includes(language);
    const isR = language === 'r';
    const isRuby = language === 'ruby';
    const isLua = language === 'lua';
    const isLatex = language === 'latex';
    const isDataConfig = ['yaml', 'toml', 'ini'].includes(language);
    const isDiff = language === 'diff';
    const isDockerfile = language === 'dockerfile';
    const isMakefile = language === 'makefile';
    const isFsharp = language === 'fsharp';
    const isVb = ['vb', 'vbscript'].includes(language);
    const isReg = language === 'reg';

    if (isHtml) {
        workingText = highlightHtmlLine(workingText, lineNumber, startCharIndex, stash);
    }
    else if (isCss) {
        // 1. Comments
        workingText = workingText.replace(/\/\*[\s\S]*?\*\//g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\/\/.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Selectors
        workingText = workingText.replace(/\b([a-zA-Z0-9._#-]+)(?=\s*\{)/g, m => stash(`<span class="token-tag">${escapeHtml(m)}</span>`));
        // 4. Variables
        workingText = workingText.replace(/--[a-zA-Z0-9_-]+/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        // 5. Hex Colors
        workingText = workingText.replace(/#[0-9a-fA-F]{3,8}/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 6. Properties
        workingText = workingText.replace(/\b(margin|padding|background|color|width|height|border|display|position|top|left|right|bottom|font-size|font-family|font-weight|line-height|overflow|align-items|justify-content|grid-template-columns|inset|pointer-events|inset|will-change|contain|align-self|box-sizing|border-radius|box-shadow|z-index|gap|transition|animation|transform|cursor|outline|white-space|overflow-wrap|tab-size|caret-color|content)\b(?=\s*:)/gi, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 7. Numbers
        workingText = workingText.replace(/\b\d+(?:px|em|rem|%|vh|vw|ms|s|deg)?\b/gi, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 8. Punctuation
        workingText = workingText.replace(/[{}:;]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isJson) {
        // 1. Keys (strings followed by :)
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"(?=\s*:)/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 2. Values (strings)
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b-?\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Builtins
        workingText = workingText.replace(/\b(true|false|null)\b/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 5. Punctuation
        workingText = workingText.replace(/[{}()\[\]:,]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isSql) {
        // 1. Comments
        workingText = workingText.replace(/--.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\/\*[\s\S]*?\*\//g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/'(?:''|[^'])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Keywords
        workingText = workingText.replace(/\b(SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TABLE|INDEX|VIEW|JOIN|INNER|LEFT|RIGHT|OUTER|ON|AND|OR|NOT|IN|LIKE|IS|NULL|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|UNION|ALL|AS|DISTINCT|COUNT|SUM|AVG|MIN|MAX|INTO|VALUES|SET|DEFAULT|PRIMARY|KEY|FOREIGN|REFERENCES|CONSTRAINT|INDEX|DATABASE|TRIGGER|PROCEDURE|FUNCTION)\b/gi, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 4. Types
        workingText = workingText.replace(/\b(INT|VARCHAR|CHAR|TEXT|DATE|TIME|TIMESTAMP|BOOLEAN|FLOAT|DOUBLE|DECIMAL|NUMERIC)\b/gi, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 5. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
    }
    else if (isMarkdown) {
        // 1. Code ticks / fenced code block indicators
        workingText = workingText.replace(/^```.*/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 2. Inline Code
        workingText = workingText.replace(/`[^`]+`/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Headers
        workingText = workingText.replace(/^#{1,6}\s+.*/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 4. Lists
        workingText = workingText.replace(/^\s*[-*+]\s+|^\s*\d+\.\s+/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 5. Blockquotes
        workingText = workingText.replace(/^\s*>\s+/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 6. Bold/Italic
        workingText = workingText.replace(/\*\*[^*]+\*\*/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\*[^*]+\*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 7. Links
        workingText = workingText.replace(/!?\[[^\]]*\]\([^)]*\)/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
    }
    else if (isShell) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Variables
        workingText = workingText.replace(/\$[a-zA-Z_]\w*|\$\{[a-zA-Z_]\w*\}/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        // 4. Control Flow
        workingText = workingText.replace(/\b(if|then|else|elif|fi|case|esac|for|while|until|do|done|in)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 5. Keywords
        workingText = workingText.replace(/\b(function|return|exit|local|export|alias|echo|set|param|Write-Host|Get-ChildItem)\b/gi, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
    }
    else if (isPython) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Tripled Strings
        workingText = workingText.replace(/"""[\s\S]*?"""|'''[\s\S]*?'''/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Normal Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 4. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 5. Control Flow
        workingText = workingText.replace(/\b(if|elif|else|return|for|while|break|continue|try|except|finally|raise|yield|pass|assert|with|as)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 6. Keywords
        workingText = workingText.replace(/\b(def|class|import|from|global|nonlocal|lambda|in|is|and|or|not|del)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 7. Builtins
        workingText = workingText.replace(/\b(True|False|None|self|print|len|range|str|int|float|list|dict|set|tuple|object|open|enumerate|zip)\b/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 8. Functions
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
        // 9. Decorators
        workingText = workingText.replace(/@[a-zA-Z_]\w*(?:\.[a-zA-Z_]\w*)*/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 10. Operators
        workingText = workingText.replace(/\*\*|\/\/|<<|>>|<=|>=|==|!=|<>|:=|->|&&|\|\||[+\-*\/%=<>&|^~]/g, m => stash(`<span class="token-operator">${escapeHtml(m)}</span>`));
        // 11. Punctuation (includes brackets for bracket-pair colorization)
        workingText = workingText.replace(/[{}()\[\].;,:]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isR) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b(?:Inf|NaN|NA|NULL|TRUE|FALSE|T|F)\b|\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[iL]?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Control Flow
        workingText = workingText.replace(/\b(if|else|for|while|repeat|break|next|return|in)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 5. Keywords and builtins
        workingText = workingText.replace(/\b(function|library|require|source|setwd|getwd|data|print|cat|paste|paste0|c|list|matrix|data\.frame|tibble|factor|length|nrow|ncol|names|colnames|rownames|apply|lapply|sapply|tapply|aggregate|subset|merge|read\.csv|read\.table|write\.csv|ggplot|aes|mutate|filter|select|summarise|arrange|group_by)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 6. Functions
        workingText = workingText.replace(/\b([a-zA-Z.][\w.]*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
        // 7. Operators
        workingText = workingText.replace(/<-|<<-|->|->>|%[^%\s]+%|::|:::|\|\||&&|<=|>=|==|!=|[+\-*\/=<>!&|~:$@^]/g, m => stash(`<span class="token-operator">${escapeHtml(m)}</span>`));
        // 8. Punctuation
        workingText = workingText.replace(/[{}()\[\],;]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isRuby) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings / symbols
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/:[a-zA-Z_]\w*[?!]?/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Control Flow
        workingText = workingText.replace(/\b(if|unless|else|elsif|case|when|while|until|for|in|do|end|begin|rescue|ensure|retry|return|yield|break|next|redo)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 5. Keywords / builtins
        workingText = workingText.replace(/\b(def|class|module|include|extend|require|load|attr_reader|attr_writer|attr_accessor|private|protected|public|self|super|nil|true|false)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 6. Variables and functions
        workingText = workingText.replace(/[@$][a-zA-Z_]\w*/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\b([a-zA-Z_]\w*[?!]?)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
    }
    else if (isLua) {
        // 1. Comments
        workingText = workingText.replace(/--\[\[[\s\S]*?\]\]/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/--.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Control Flow
        workingText = workingText.replace(/\b(if|then|elseif|else|end|for|while|repeat|until|do|break|return|goto|in)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 5. Keywords / builtins
        workingText = workingText.replace(/\b(function|local|nil|true|false|and|or|not|require|print|pairs|ipairs|string|table|math|io|os|coroutine)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 6. Functions
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
    }
    else if (isLatex) {
        // 1. Comments
        workingText = workingText.replace(/%.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Commands
        workingText = workingText.replace(/\\[a-zA-Z@]+|\\./g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 3. Environments and references
        workingText = workingText.replace(/\{(?:document|figure|table|itemize|enumerate|align|equation|array|tabular|section|subsection|subsubsection)\}/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 4. Math delimiters
        workingText = workingText.replace(/\$\$?|\[|\]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
        // 5. Braces
        workingText = workingText.replace(/[{}]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isDataConfig) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Section headers and keys
        workingText = workingText.replace(/^\s*\[[^\]]+\]/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/^\s*[-?]?\s*[a-zA-Z0-9_.-]+(?=\s*[:=])/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 4. Scalars
        workingText = workingText.replace(/\b(true|false|null|yes|no|on|off)\b/gi, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\b-?\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 5. Punctuation
        workingText = workingText.replace(/[:=\[\]{}.,-]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }
    else if (isDiff) {
        if (/^(diff --git|index |@@|\+\+\+|---)/.test(workingText)) {
            workingText = stash(`<span class="token-keyword">${escapeHtml(workingText)}</span>`);
        } else if (workingText.startsWith('+')) {
            workingText = stash(`<span class="token-string">${escapeHtml(workingText)}</span>`);
        } else if (workingText.startsWith('-')) {
            workingText = stash(`<span class="token-comment">${escapeHtml(workingText)}</span>`);
        }
    }
    else if (isDockerfile) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Instructions
        workingText = workingText.replace(/^\s*(FROM|RUN|CMD|LABEL|MAINTAINER|EXPOSE|ENV|ADD|COPY|ENTRYPOINT|VOLUME|USER|WORKDIR|ARG|ONBUILD|STOPSIGNAL|HEALTHCHECK|SHELL)\b/gi, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 4. Variables
        workingText = workingText.replace(/\$[a-zA-Z_]\w*|\$\{[a-zA-Z_]\w*\}/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
    }
    else if (isMakefile) {
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Targets and variables
        workingText = workingText.replace(/^[^\s:=#][^:=#]*(?=\s*:)/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/^[A-Za-z_][A-Za-z0-9_]*(?=\s*[:+?]?=)/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\$\([^)]+\)|\$\{[^}]+\}/g, m => stash(`<span class="token-variable">${escapeHtml(m)}</span>`));
        // 3. Directives
        workingText = workingText.replace(/\b(ifdef|ifndef|ifeq|ifneq|else|endif|include|define|endef|export|override|private|vpath)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
    }
    else if (isFsharp) {
        // 1. Comments
        workingText = workingText.replace(/\(\*[\s\S]*?\*\)/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\/\/.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/@"[^"]*"|"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Control Flow / keywords
        workingText = workingText.replace(/\b(if|then|else|match|with|for|while|do|done|try|finally|return|yield|async|let|use|fun|function|member|type|module|namespace|open|interface|abstract|override|static|mutable|rec|and|or|not|in|of|as|new)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 5. Types and functions
        workingText = workingText.replace(/\b(unit|bool|string|int|int64|float|double|decimal|list|array|seq|option|Result|Some|None|Ok|Error|true|false|null)\b/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
    }
    else if (isVb) {
        // 1. Comments
        workingText = workingText.replace(/'.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Strings
        workingText = workingText.replace(/"(?:[^"]|"")*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 3. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 4. Control Flow / keywords
        workingText = workingText.replace(/\b(If|Then|Else|ElseIf|End|For|Each|Next|While|Do|Loop|Select|Case|Try|Catch|Finally|Throw|Return|Exit|Continue|Imports|Namespace|Class|Module|Structure|Interface|Enum|Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|Sub|Function|Property|Dim|Const|Static|New|In|As|Is|And|Or|Not|ByVal|ByRef)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 5. Types / builtins
        workingText = workingText.replace(/\b(Boolean|Byte|Char|Date|Decimal|Double|Integer|Long|Object|Short|Single|String|True|False|Nothing|Console|Math)\b/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
    }
    else if (isReg) {
        // 1. Comments (semicolons) - must be first
        if (/^\s*;/.test(workingText)) {
            return `<span class="token-comment">${escapeHtml(workingText)}</span>`;
        }
        // 2. Registry header line
        if (/^Windows Registry Editor/i.test(workingText)) {
            return `<span class="token-keyword">${escapeHtml(workingText)}</span>`;
        }
        // 3. Section headers [HKEY_...] (full line is the section)
        if (/^\[/.test(workingText)) {
            const cls = /^\[-/.test(workingText) ? 'token-comment' : 'token-type';
            return `<span class="${cls}">${escapeHtml(workingText)}</span>`;
        }
        // 4. Value assignment lines: "Name"=data or @=data
        const assignMatch = workingText.match(/^(@|"(?:[^"\\]|\\.)*")(=)(.*)/);
        if (assignMatch) {
            const namePart  = `<span class="token-variable">${escapeHtml(assignMatch[1])}</span>`;
            const eqPart    = `<span class="token-operator">=</span>`;
            let   valuePart = assignMatch[3];

            // dword:XXXXXXXX
            const dwordM = valuePart.match(/^(dword:)([0-9a-fA-F]+)(.*)/i);
            if (dwordM) {
                valuePart = `<span class="token-keyword">${escapeHtml(dwordM[1])}</span><span class="token-number">${escapeHtml(dwordM[2])}</span>${escapeHtml(dwordM[3])}`;
            }
            // hex(N): or hex:
            else if (/^hex[:(]/i.test(valuePart)) {
                const hexTypeM = valuePart.match(/^(hex(?:\(\d+\))?:)(.*)/i);
                if (hexTypeM) {
                    valuePart = `<span class="token-keyword">${escapeHtml(hexTypeM[1])}</span><span class="token-number">${escapeHtml(hexTypeM[2])}</span>`;
                } else {
                    valuePart = escapeHtml(valuePart);
                }
            }
            // String value "..."
            else if (/^"/.test(valuePart)) {
                valuePart = `<span class="token-string">${escapeHtml(valuePart)}</span>`;
            }
            // Delete value (-)
            else if (valuePart === '-') {
                valuePart = `<span class="token-comment">-</span>`;
            }
            else {
                valuePart = escapeHtml(valuePart);
            }

            return namePart + eqPart + valuePart;
        }
        // 5. Continuation hex lines (  xx,xx,xx,\\ )
        if (/^\s+[0-9a-fA-F]{2}/.test(workingText)) {
            return workingText.replace(/[0-9a-fA-F]{2}/g, m => `<span class="token-number">${escapeHtml(m)}</span>`);
        }
    }
    else if (isClike) {
        // 1. Multi-line comments
        workingText = workingText.replace(/\/\*[\s\S]*?\*\//g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 2. Single-line comments
        workingText = workingText.replace(/\/\/.*/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
        // 3. Strings
        workingText = workingText.replace(/"(?:\\.|[^"\\])*"/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/`(?:\\.|[^`\\])*`/g, m => stash(`<span class="token-string">${escapeHtml(m)}</span>`));
        // 4. Numbers
        workingText = workingText.replace(/\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?\b/g, m => stash(`<span class="token-number">${escapeHtml(m)}</span>`));
        // 5. Control Flow
        workingText = workingText.replace(/\b(if|else|return|for|while|do|switch|case|default|break|continue|goto|throw|try|catch|finally|yield|await|async)\b/g, m => stash(`<span class="token-control">${escapeHtml(m)}</span>`));
        // 6. Keywords
        workingText = workingText.replace(/\b(class|struct|interface|enum|public|private|protected|internal|static|readonly|volatile|virtual|override|abstract|sealed|extern|unsafe|fixed|lock|typeof|sizeof|new|delete|var|let|const|function|fn|pub|use|mod|impl|trait|type|package|import|export|namespace|using|as|is|in|out|ref|params|base|this|void|int|float|double|char|string|bool|boolean|long|short|byte|sbyte|uint|ulong|ushort|decimal|object|dynamic)\b/g, m => stash(`<span class="token-keyword">${escapeHtml(m)}</span>`));
        // 7. Types / Builtins
        workingText = workingText.replace(/\b(true|false|null|undefined|Console|Math|System|String|Object|Array|window|document|process|global|require|self)\b/g, m => stash(`<span class="token-type">${escapeHtml(m)}</span>`));
        // 8. Functions
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class="token-function">${escapeHtml(m)}</span>`));
        // 9. Operators
        workingText = workingText.replace(/&&|\|\||===|==|!==|!=|=>|\+=|-=|\*=|\/=|<=|>=|[+\-*\/=<>!%&|^~?:]/g, m => stash(`<span class="token-operator">${escapeHtml(m)}</span>`));
        // 10. Punctuation
        workingText = workingText.replace(/[{}()\[\].;,]/g, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
    }

    // Default escaping of the remaining text (plain parts) and restoring placeholders
    let escapedText = escapeHtml(workingText);

    // Bracket pair colorization: colorize bracket characters in stashed tokens in sequential occurrence order
    const matchingBrackets = { ')': '(', '}': '{', ']': '[' };
    function colorizeBrackets(html, stack) {
        if (html.includes('token-comment') || html.includes('token-string')) {
            return html;
        }
        const opening = { '(': '(', '{': '{', '[': '[' };
        const closing = { ')': '(', '}': '{', ']': '[' };
        return html.replace(/[(){}\[\]]/g, ch => {
            if (opening[ch]) {
                const depth = stack.length;
                stack.push(ch);
                const cls = `bracket-depth-${depth % 6}`;
                return `<span class="${cls}">${ch}</span>`;
            }
            if (closing[ch]) {
                const target = matchingBrackets[ch];
                if (stack.length > 0 && stack[stack.length - 1] === target) {
                    stack.pop();
                } else {
                    stack.pop();
                }
                const depth = stack.length;
                const cls = `bracket-depth-${depth % 6}`;
                return `<span class="${cls}">${ch}</span>`;
            }
            return ch;
        });
    }

    while (escapedText.includes('\u0002_TOKEN_')) {
        escapedText = escapedText.replace(/\u0002_TOKEN_(\d+)_\u0002/g, (match, idx) => {
            let tokenHtml = tokens[Number(idx)];
            if (state.bracketPairColorization && lineNumber !== null) {
                tokenHtml = colorizeBrackets(tokenHtml, currentLineStack);
            }
            return tokenHtml;
        });
    }

    return escapedText;
}

const MAX_JSON_SYNTAX_HIGHLIGHT_CHARS = 1000;

function shouldSkipLineSyntaxHighlighting(text, language = state.language) {
    return language === 'json' && String(text ?? '').length >= MAX_JSON_SYNTAX_HIGHLIGHT_CHARS;
}

function renderLineContent(lineNumber, text, forcePlainText = false, suppressSelectionFragments = false) {
    if (state.isComposing && state.compositionLine === lineNumber) {
        return escapeHtml(text);
    }

    const suppressSyntaxHighlighting = forcePlainText || shouldSkipLineSyntaxHighlighting(text);

    if (!suppressSelectionFragments) {
        const selectionBounds = selectionBoundsForLine(lineNumber, text.length);
        if (selectionBounds) {
            return renderLineContentWithSelection(
                lineNumber,
                text,
                selectionBounds,
                suppressSyntaxHighlighting);
        }
    }

    if (state.searchQuery && state.searchMatches.length > 0) {
        const active = state.activeSearch;
        const activeMatchOnLine = active && active.lineNumber === lineNumber
            ? active
            : null;
        const matchesOnLine = state.searchMatchesByLine?.get(lineNumber) || [];
        return renderSearchMatchesForLine(
            lineNumber,
            text,
            matchesOnLine,
            activeMatchOnLine,
            suppressSyntaxHighlighting);
    }

    return renderHighlightedText(text, lineNumber, 0, suppressSyntaxHighlighting);
}

function renderHighlightedText(text, lineNumber, startCharIndex, suppressSyntaxHighlighting) {
    return suppressSyntaxHighlighting
        ? escapeHtml(text)
        : highlightLine(text, state.language, lineNumber, startCharIndex);
}

function renderLineContentWithSelection(lineNumber, text, selectionBounds, suppressSyntaxHighlighting) {
    const highlighted = renderHighlightedText(text, lineNumber, 0, suppressSyntaxHighlighting);
    const start = Math.max(0, Math.min(selectionBounds.start, text.length));
    const end = Math.max(start, Math.min(selectionBounds.end, text.length));
    if (start === end) {
        return highlighted;
    }

    return wrapHighlightedSelection(highlighted, start, end);
}

function wrapHighlightedSelection(html, selectionStart, selectionEnd) {
    const template = document.createElement('template');
    template.innerHTML = html;

    const selectedTextNodes = [];
    const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT);
    let offset = 0;
    let node;

    while ((node = walker.nextNode())) {
        const text = node.nodeValue || '';
        const nodeStart = offset;
        const nodeEnd = nodeStart + text.length;

        if (nodeEnd > selectionStart && nodeStart < selectionEnd) {
            selectedTextNodes.push({
                node,
                start: Math.max(selectionStart, nodeStart) - nodeStart,
                end: Math.min(selectionEnd, nodeEnd) - nodeStart
            });
        }

        offset = nodeEnd;
    }

    for (const item of selectedTextNodes) {
        const text = item.node.nodeValue || '';
        const fragment = document.createDocumentFragment();
        if (item.start > 0) {
            fragment.appendChild(document.createTextNode(text.slice(0, item.start)));
        }

        const selected = document.createElement('span');
        selected.className = 'selection-fragment';
        selected.textContent = text.slice(item.start, item.end);
        fragment.appendChild(selected);

        if (item.end < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(item.end)));
        }

        item.node.replaceWith(fragment);
    }

    return template.innerHTML;
}

function renderSearchMatchesForLine(lineNumber, text, matches, activeMatch, suppressSyntaxHighlighting) {
    if (matches.length === 0) {
        return renderHighlightedText(text, lineNumber, 0, suppressSyntaxHighlighting);
    }

    matches = [...matches].sort((a, b) => a.indexOfMatch - b.indexOfMatch);

    const parts = [];
    let pos = 0;

    for (const match of matches) {
        const idx = match.indexOfMatch;
        const len = match.matchLength;

        if (idx < pos || idx >= text.length) continue;

        if (idx > pos) {
            parts.push(renderHighlightedText(
                text.slice(pos, idx),
                lineNumber,
                pos,
                suppressSyntaxHighlighting));
        }

        const isActive = activeMatch && 
            idx === activeMatch.indexOfMatch && 
            lineNumber === activeMatch.lineNumber;

        const matchText = text.slice(idx, idx + len);
        const cls = isActive ? 'search-match active-match' : 'search-match';
        parts.push(`<mark class="${cls}">${escapeHtml(matchText)}</mark>`);
        pos = idx + len;
    }

    if (pos < text.length) {
        parts.push(renderHighlightedText(
            text.slice(pos),
            lineNumber,
            pos,
            suppressSyntaxHighlighting));
    }

    return parts.join('');
}

export {
    highlightLine,
    renderLineContent,
    shouldSkipLineSyntaxHighlighting
};
