import { escapeHtml, state } from './editor-core.js';
import { selectionBoundsForLine } from './editor-selection.js';

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
    while (startLine > 1 && !state.lineEndStacks.has(startLine)) {
        startLine--;
    }

    let currentStack = startLine > 1 ? [...state.lineEndStacks.get(startLine)] : [];
    for (let l = startLine + 1; l < lineNumber; l++) {
        currentStack = computeLineEndStack(l, currentStack);
        state.lineEndStacks.set(l, currentStack);
    }
    return currentStack;
}

function highlightLine(text, language, lineNumber = null, startCharIndex = 0) {
    if (!language || language === 'plaintext') {
        return escapeHtml(text);
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
    const isHtml = ['html', 'xml', 'xaml'].includes(language);
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
        // 1. Comments
        workingText = workingText.replace(/<!--[\s\S]*?-->/g, m => stash(`<span class="token-comment">${escapeHtml(m)}</span>`));
            // 2. CDATA / DOCTYPE
            workingText = workingText.replace(/<!DOCTYPE[^>]*>|<!\[CDATA\[.*?\]\]>/gi, m => stash(`<span class="token-punctuation">${escapeHtml(m)}</span>`));
        // 3. Tags
        workingText = workingText.replace(/<([^>]+)>/g, (match, tagContent) => {
            let tagHtml = "";
            let remainder = tagContent;
            const tagNameMatch = /^([^\s/]+)/.exec(remainder);
            if (tagNameMatch) {
                const tagName = tagNameMatch[1];
                tagHtml += `<span class="token-tag">${escapeHtml(tagName)}</span>`;
                remainder = remainder.slice(tagName.length);
            }
            remainder = remainder.replace(/([a-zA-Z0-9:-]+)(\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?/g, (attrMatch, attrName, attrVal) => {
                let attrHtml = `<span class="token-attr">${escapeHtml(attrName)}</span>`;
                if (attrVal) {
                    const eqIndex = attrVal.indexOf('=');
                    const valPart = attrVal.slice(eqIndex + 1);
                    attrHtml += `<span class="token-operator">=</span><span class="token-string">${escapeHtml(valPart)}</span>`;
                }
                return ' ' + attrHtml;
            });
            tagHtml += remainder;
            return stash(`<span class="token-punctuation">&lt;</span>${tagHtml}<span class="token-punctuation">&gt;</span>`);
        });
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

function renderLineContent(lineNumber, text, forcePlainText = false) {
    if (forcePlainText || (state.isComposing && state.compositionLine === lineNumber)) {
        return escapeHtml(text);
    }

    const selectionBounds = selectionBoundsForLine(lineNumber, text.length);
    if (selectionBounds) {
        return highlightLine(text, state.language, lineNumber, 0);
    }

    if (state.searchQuery && state.searchMatches.length > 0) {
        const active = state.activeSearch;
        const activeMatchOnLine = active && active.lineNumber === lineNumber
            ? active
            : null;
        const matchesOnLine = state.searchMatches.filter(m => m.lineNumber === lineNumber);
        return renderSearchMatchesForLine(lineNumber, text, matchesOnLine, activeMatchOnLine);
    }

    return highlightLine(text, state.language, lineNumber, 0);
}

function renderSearchMatchesForLine(lineNumber, text, matches, activeMatch) {
    if (matches.length === 0) {
        return highlightLine(text, state.language, lineNumber, 0);
    }

    matches = [...matches].sort((a, b) => a.indexOfMatch - b.indexOfMatch);

    const parts = [];
    let pos = 0;

    for (const match of matches) {
        const idx = match.indexOfMatch;
        const len = match.matchLength;

        if (idx < pos || idx >= text.length) continue;

        if (idx > pos) {
            parts.push(highlightLine(text.slice(pos, idx), state.language, lineNumber, pos));
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
        parts.push(highlightLine(text.slice(pos), state.language, lineNumber, pos));
    }

    return parts.join('');
}

export {
    highlightLine,
    renderLineContent
};
