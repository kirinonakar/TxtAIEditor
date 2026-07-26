namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookMarkdownScript
    {
        internal static string GetScript()
        {
            return @"    function renderMarkdownJs(md) {
        if (!md) return '';
        const lines = md.replace(/\r\n/g, '\n').split('\n');
        let html = '';
        let inList = false, inTaskList = false, inOl = false, inQuote = false, inCodeBlock = false;
        let codeBuffer = [];

        for (let i = 0; i < lines.length; i++) {
            let line = lines[i];

            if (line.trim().startsWith('```')) {
                if (inCodeBlock) {
                    html += '<pre><code>' + escapeHtml(codeBuffer.join('\n')) + '</code></pre>';
                    codeBuffer = [];
                    inCodeBlock = false;
                } else {
                    if (inList) { html += '</ul>'; inList = false; }
                    if (inTaskList) { html += '</ul>'; inTaskList = false; }
                    if (inOl) { html += '</ol>'; inOl = false; }
                    if (inQuote) { html += '</blockquote>'; inQuote = false; }
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock) {
                codeBuffer.push(line);
                continue;
            }

            const trimmed = line.trimEnd();

            const taskMatch = trimmed.match(/^[-*+]\s+\[([ xX])\]\s+(.*)/);

            if (/^\s{0,3}>\s?/.test(line)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (!inQuote) { html += '<blockquote>'; inQuote = true; }
                const quoteContent = line.replace(/^\s{0,3}>\s?/, '');
                html += '<p>' + inlineMdJs(quoteContent) + '</p>';
            } else if (/^#\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<h1>' + inlineMdJs(trimmed.slice(2)) + '</h1>';
            } else if (/^##\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<h2>' + inlineMdJs(trimmed.slice(3)) + '</h2>';
            } else if (/^###\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<h3>' + inlineMdJs(trimmed.slice(4)) + '</h3>';
            } else if (/^####\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<h4>' + inlineMdJs(trimmed.slice(5)) + '</h4>';
            } else if (taskMatch) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                if (!inTaskList) { html += '<ul class=""task-list"">'; inTaskList = true; }
                const isChecked = taskMatch[1].toLowerCase() === 'x';
                const checkedAttr = isChecked ? ' checked=""checked""' : '';
                html += '<li class=""task-list-item""><input type=""checkbox"" disabled' + checkedAttr + ' /> ' + inlineMdJs(taskMatch[2]) + '</li>';
            } else if (/^[-*]\s+/.test(trimmed)) {
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                if (!inList) { html += '<ul>'; inList = true; }
                html += '<li>' + inlineMdJs(trimmed.slice(2)) + '</li>';
            } else if (/^\d+\.\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                if (!inOl) { html += '<ol>'; inOl = true; }
                html += '<li>' + inlineMdJs(trimmed.replace(/^\d+\.\s+/, '')) + '</li>';
            } else if (trimmed === '---' || trimmed === '***') {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<hr/>';
            } else if (trimmed.length > 0) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
                html += '<p>' + inlineMdJs(trimmed) + '</p>';
            } else {
                if (inList) { html += '</ul>'; inList = false; }
                if (inTaskList) { html += '</ul>'; inTaskList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                if (inQuote) { html += '</blockquote>'; inQuote = false; }
            }
        }
        if (inList) html += '</ul>';
        if (inTaskList) html += '</ul>';
        if (inOl) html += '</ol>';
        if (inQuote) html += '</blockquote>';
        if (inCodeBlock) html += '<pre><code>' + escapeHtml(codeBuffer.join('\n')) + '</code></pre>';
        return html;
    }

    function renderLatex(text) {
        if (!text) return text;
        if (typeof katex === 'undefined') return text;
        try {
            // Display math $$...$$
            text = text.replace(/\$\$([\s\S]*?)\$\$/g, (_, expr) => {
                const cleanExpr = expr.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
                try { return katex.renderToString(cleanExpr.trim(), { displayMode: true, throwOnError: false }); }
                catch(e) { return `<span class=""token-comment"">$$${escapeHtml(expr)}$$</span>`; }
            });
            // Display math \[...\]
            text = text.replace(/\\\[([\s\S]*?)\\\]/g, (_, expr) => {
                const cleanExpr = expr.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
                try { return katex.renderToString(cleanExpr.trim(), { displayMode: true, throwOnError: false }); }
                catch(e) { return `<span class=""token-comment"">\\[${escapeHtml(expr)}\\]</span>`; }
            });
            // Inline math $...$
            text = text.replace(/(?<!\$)\$([^$\n]+?)\$(?!\$)/g, (_, expr) => {
                const cleanExpr = expr.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
                try { return katex.renderToString(cleanExpr.trim(), { displayMode: false, throwOnError: false }); }
                catch(e) { return `<span class=""token-comment"">$${escapeHtml(expr)}$</span>`; }
            });
            // Inline math \(...\)
            text = text.replace(/\\\(([\s\S]*?)\\\)/g, (_, expr) => {
                const cleanExpr = expr.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
                try { return katex.renderToString(cleanExpr.trim(), { displayMode: false, throwOnError: false }); }
                catch(e) { return `<span class=""token-comment"">\\(${escapeHtml(expr)}\\)</span>`; }
            });
        } catch(e) {}
        return text;
    }

    function inlineMdJs(str) {
        let s = escapeHtml(str);
        s = s.replace(/&lt;span style=&quot;color:\s*([^&;\""|]+);?&quot;&gt;([\s\S]*?)&lt;\/span&gt;/gi, '<span style=""color:$1;"">$2</span>');
        s = s.replace(/&lt;span style=&quot;background-color:\s*([^&;\""|]+);?&quot;&gt;([\s\S]*?)&lt;\/span&gt;/gi, '<span style=""background-color:$1;"">$2</span>');
        s = s.replace(/&lt;font color=&quot;([^&;\""|]+)&quot;&gt;([\s\S]*?)&lt;\/font&gt;/gi, '<font color=""$1"">$2</font>');
        s = s.replace(/&lt;mark&gt;([\s\S]*?)&lt;\/mark&gt;/gi, '<mark>$1</mark>');
        s = s.replace(/&lt;u&gt;([\s\S]*?)&lt;\/u&gt;/gi, '<u>$1</u>');
        s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img src=""$2"" alt=""$1"" style=""max-width:100%;height:auto;display:inline-block;vertical-align:middle;margin:4px 0;"" />');
        s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href=""$2"" target=""_blank"" rel=""noopener"">$1</a>');
        s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/\*([^*]+)\*/g, '<em>$1</em>');
        s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
        s = renderLatex(s);
        return s;
    }

    function getEditorText(editor) {
        if (!editor) return '';

        function readNode(node) {
            let text = '';
            node.childNodes.forEach(child => {
                if (child.nodeType === Node.TEXT_NODE) {
                    text += child.nodeValue || '';
                    return;
                }
                if (child.nodeType !== Node.ELEMENT_NODE) {
                    return;
                }
                if (child.tagName === 'BR') {
                    text += '\n';
                    return;
                }

                const isBlock = child.tagName === 'DIV' || child.tagName === 'P' || child.tagName === 'PRE';
                if (isBlock && text.length > 0 && !text.endsWith('\n')) {
                    text += '\n';
                }
                const childText = readNode(child);
                text += childText;
                if (isBlock && childText.length > 0 && child.nextSibling && !text.endsWith('\n')) {
                    text += '\n';
                }
            });
            return text;
        }

        return readNode(editor).replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    }

    function getCellSource(cellDiv) {
        const type = getCellType(cellDiv);
        if (type === 'markdown') {
            const editor = cellDiv.querySelector('.markdown-editor');
            if (editor) return getEditorText(editor) || cellDiv.getAttribute('data-source') || '';
            return cellDiv.getAttribute('data-source') || '';
        } else if (type === 'raw') {
            const editor = cellDiv.querySelector('.raw-editor, .cell-input-area');
            if (editor) return getEditorText(editor) || cellDiv.getAttribute('data-source') || '';
            return cellDiv.getAttribute('data-source') || '';
        } else {
            const input = cellDiv.querySelector('.cell-input-area');
            if (input) return getEditorText(input) || cellDiv.getAttribute('data-source') || '';
            return cellDiv.getAttribute('data-source') || '';
        }
    }

    function renderMarkdownCell(cellDiv) {
        if (getCellType(cellDiv) !== 'markdown') return;
        const editor = cellDiv.querySelector('.markdown-editor');
        const preview = cellDiv.querySelector('.markdown-preview');
        if (!preview) return;

        const source = (editor ? getEditorText(editor) : '') || cellDiv.getAttribute('data-source') || '';
        cellDiv.setAttribute('data-source', source);
        preview.innerHTML = renderMarkdownJs(source) || '<em style=""color:#888;"">(Empty Markdown Cell)</em>';
        if (editor) editor.style.display = 'none';
        preview.style.display = 'block';
    }

    function editMarkdownCell(cellDiv, focusAtEnd = true) {
        if (getCellType(cellDiv) !== 'markdown') return;
        const editor = cellDiv.querySelector('.markdown-editor');
        const preview = cellDiv.querySelector('.markdown-preview');
        if (!editor || !preview) return;

        editor.style.display = 'block';
        preview.style.display = 'none';
        if (focusAtEnd) {
            focusEditorAtEnd(editor);
        }
    }

    function insertMarkdownFormatting(cellDiv, formatType) {
        if (getCellType(cellDiv) !== 'markdown') return;
        editMarkdownCell(cellDiv);
        const editor = cellDiv.querySelector('.markdown-editor');
        if (!editor) return;

        let prefix = '', suffix = '', defaultText = '';
        switch (formatType) {
            case 'bold':
                prefix = '**'; suffix = '**'; defaultText = 'bold text';
                break;
            case 'italic':
                prefix = '*'; suffix = '*'; defaultText = 'italic text';
                break;
            case 'heading':
                prefix = '# '; suffix = ''; defaultText = 'Heading';
                break;
            case 'link':
                prefix = '['; suffix = '](https://)'; defaultText = 'link text';
                break;
            case 'image':
                prefix = '!['; suffix = '](image_url)'; defaultText = 'image alt';
                break;
        }

        const sel = window.getSelection();
        let selectedText = '';
        let range = null;

        if (sel && sel.rangeCount > 0 && editor.contains(sel.anchorNode)) {
            range = sel.getRangeAt(0);
            selectedText = range.toString();
        }

        const textToWrap = selectedText || defaultText;
        const inserted = prefix + textToWrap + suffix;

        if (range) {
            range.deleteContents();
            const textNode = document.createTextNode(inserted);
            range.insertNode(textNode);
            range.setStartAfter(textNode);
            range.setEndAfter(textNode);
            sel.removeAllRanges();
            sel.addRange(range);
        } else {
            const currentText = editor.innerText || '';
            const needNewline = currentText.length > 0 && !currentText.endsWith('\n');
            editor.innerHTML = '<pre>' + escapeHtml(currentText + (needNewline ? '\n' : '') + inserted) + '</pre>';
        }
        editor.focus();
        notifyModified();
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    /* Python syntax highlighting for code cells */
    function highlightPythonCode(text) {
        if (!text) return escapeHtml(text);
        const language = 'python';
        const isPython = true;
        let workingText = text;
        const tokens = [];
        function stash(html) {
            const placeholder = `\u0002_TOKEN_${tokens.length}_\u0002`;
            tokens.push(html);
            return placeholder;
        }
        // 1. Comments
        workingText = workingText.replace(/#.*/g, m => stash(`<span class=""token-comment"">${escapeHtml(m)}</span>`));
        // 2. Triple-quoted strings
        workingText = workingText.replace(/""""""[\s\S]*?""""""|'''[\s\S]*?'''/g, m => stash(`<span class=""token-string"">${escapeHtml(m)}</span>`));
        // 3. Strings
        workingText = workingText.replace(/""(?:\\.|[^""\\])*""/g, m => stash(`<span class=""token-string"">${escapeHtml(m)}</span>`));
        workingText = workingText.replace(/'(?:\\.|[^'\\])*'/g, m => stash(`<span class=""token-string"">${escapeHtml(m)}</span>`));
        // 4. Numbers
        workingText = workingText.replace(/\b\d+(?:\.\d+)?\b/g, m => stash(`<span class=""token-number"">${escapeHtml(m)}</span>`));
        // 5. Control Flow
        workingText = workingText.replace(/\b(if|elif|else|return|for|while|break|continue|try|except|finally|raise|yield|pass|assert|with|as)\b/g, m => stash(`<span class=""token-control"">${escapeHtml(m)}</span>`));
        // 6. Keywords
        workingText = workingText.replace(/\b(def|class|import|from|global|nonlocal|lambda|in|is|and|or|not|del)\b/g, m => stash(`<span class=""token-keyword"">${escapeHtml(m)}</span>`));
        // 7. Builtins
        workingText = workingText.replace(/\b(True|False|None|self|print|len|range|str|int|float|list|dict|set|tuple|object|open|enumerate|zip)\b/g, m => stash(`<span class=""token-type"">${escapeHtml(m)}</span>`));
        // 8. Function calls
        workingText = workingText.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, m => stash(`<span class=""token-function"">${escapeHtml(m)}</span>`));
        // 9. Decorators
        workingText = workingText.replace(/@[a-zA-Z_]\w*(?:\.[a-zA-Z_]\w*)*/g, m => stash(`<span class=""token-keyword"">${escapeHtml(m)}</span>`));
        // 10. Operators
        workingText = workingText.replace(/\*\*|\/\/|<<|>>|<=|>=|==|!=|<>|:=|->|&&|\|\||[+\-*\/%=<>&|^~]/g, m => stash(`<span class=""token-operator"">${escapeHtml(m)}</span>`));
        // 11. Punctuation
        workingText = workingText.replace(/[{}()\[\].;,:]/g, m => stash(`<span class=""token-punctuation"">${escapeHtml(m)}</span>`));

        let escapedText = escapeHtml(workingText);
        while (escapedText.includes('\u0002_TOKEN_')) {
            escapedText = escapedText.replace(/\u0002_TOKEN_(\d+)_\u0002/g, (match, idx) => {
                return tokens[Number(idx)];
            });
        }
        return escapedText;
    }

    function applyCodeSyntaxHighlight(cellDiv) {
        if (!cellDiv) return;
        const editor = cellDiv.querySelector('.cell-input-area.code-editor');
        if (!editor) return;
        const pre = editor.querySelector('pre');
        if (!pre) return;
        const source = getCellSource(cellDiv);
        if (!source) return;
        pre.innerHTML = highlightPythonCode(source);
    }

    function applyAllCodeCellsHighlight() {
        container.querySelectorAll('.cell[data-cell-type=""code""]').forEach(cellDiv => {
            applyCodeSyntaxHighlight(cellDiv);
        });
    }
";
        }
    }
}
