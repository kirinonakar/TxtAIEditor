namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookCellInteractionScript
    {
        internal static string GetScript()
        {
            return @"    let runningCells = new Set();

    const nbAutocompleteState = {
        isOpen: false,
        candidates: [],
        activeIndex: 0,
        editor: null,
        wordStart: 0,
        wordEnd: 0,
        caretOffset: 0,
        word: ''
    };

    const pyKeywords = [
        'def', 'class', 'import', 'from', 'return', 'if', 'else', 'elif',
        'for', 'while', 'try', 'except', 'finally', 'raise', 'yield', 'with',
        'as', 'pass', 'break', 'continue', 'lambda', 'global', 'nonlocal',
        'assert', 'async', 'await', 'del', 'in', 'is', 'and', 'or', 'not'
    ];
    const pyBuiltins = [
        'print', 'len', 'range', 'str', 'int', 'float', 'list', 'dict', 'set',
        'tuple', 'object', 'open', 'enumerate', 'zip', 'abs', 'min', 'max',
        'sum', 'sorted', 'map', 'filter', 'any', 'all', 'dir', 'help', 'input',
        'super', 'isinstance', 'issubclass', 'type', 'vars', 'True', 'False', 'None'
    ];
    const pyLibraries = [
        'numpy', 'pandas', 'matplotlib', 'pyplot', 'plt', 'np', 'pd', 'sns',
        'seaborn', 'torch', 'tensorflow', 'tf', 'sklearn', 'scipy', 'os', 'sys',
        'json', 're', 'math', 'datetime', 'random', 'Path', 'DataFrame', 'Series',
        'ndarray', 'tqdm'
    ];

    function getWordUnderCaretInEditor(editor) {
        const caretOffset = getCaretOffsetInEditor(editor);
        if (!Number.isInteger(caretOffset)) return null;
        const text = getEditorText(editor);
        let start = caretOffset;
        while (start > 0 && /[\w_]/.test(text[start - 1])) {
            start--;
        }
        let end = caretOffset;
        while (end < text.length && /[\w_]/.test(text[end])) {
            end++;
        }
        const word = text.slice(start, caretOffset);
        return { word, start, end, caretOffset, text };
    }

    function getNbAutocompleteCandidates(word) {
        if (!word || word.length < 1) return [];
        const lowerWord = word.toLowerCase();
        const seen = new Set();
        const candidates = [];

        function addCandidate(label, kind, detail) {
            if (!label || label === word) return;
            if (label.length < word.length) return;
            const lowerLabel = label.toLowerCase();
            if (!lowerLabel.startsWith(lowerWord)) return;
            if (seen.has(label)) return;
            seen.add(label);
            candidates.push({ label, kind, detail });
        }

        pyKeywords.forEach(k => addCandidate(k, 'keyword', '키워드'));
        pyBuiltins.forEach(b => addCandidate(b, 'builtin', '내장함수'));
        pyLibraries.forEach(l => addCandidate(l, 'library', '라이브러리'));

        container.querySelectorAll('.cell-input-area').forEach(cellEd => {
            const cellText = getEditorText(cellEd);
            const matches = cellText.match(/[\w_]{2,}/g) || [];
            matches.forEach(w => addCandidate(w, 'word', '코드'));
        });

        candidates.sort((a, b) => a.label.localeCompare(b.label));
        return candidates.slice(0, 10);
    }

    function getOrCreateNbAutocompletePopup() {
        let popup = document.getElementById('nb-autocomplete-popup');
        if (!popup) {
            popup = document.createElement('div');
            popup.id = 'nb-autocomplete-popup';
            popup.className = 'nb-autocomplete-popup';
            popup.hidden = true;
            document.body.appendChild(popup);
        }
        return popup;
    }

    function renderNbAutocomplete() {
        const popup = getOrCreateNbAutocompletePopup();
        if (!nbAutocompleteState.isOpen || nbAutocompleteState.candidates.length === 0) {
            popup.hidden = true;
            return;
        }

        const itemsHtml = nbAutocompleteState.candidates.map((c, idx) => {
            const activeCls = idx === nbAutocompleteState.activeIndex ? ' active' : '';
            return '<button type=""button"" class=""nb-autocomplete-item' + activeCls + '"" data-index=""' + idx + '"">' +
                   '<span class=""nb-autocomplete-label"">' + escapeHtml(c.label) + '</span>' +
                   (c.detail ? '<span class=""nb-autocomplete-detail"">' + escapeHtml(c.detail) + '</span>' : '') +
                   '</button>';
        }).join('');

        popup.innerHTML = itemsHtml;
        popup.hidden = false;

        const sel = window.getSelection();
        let caretRect = null;
        if (sel && sel.rangeCount > 0) {
            const r = sel.getRangeAt(0).cloneRange();
            r.collapse(false);
            const rects = r.getClientRects();
            if (rects.length > 0) caretRect = rects[0];
        }
        if (!caretRect && nbAutocompleteState.editor) {
            caretRect = nbAutocompleteState.editor.getBoundingClientRect();
        }

        if (caretRect) {
            const popupRect = popup.getBoundingClientRect();
            let left = caretRect.left;
            let top = caretRect.bottom + 4;
            if (left + popupRect.width > window.innerWidth) {
                left = window.innerWidth - popupRect.width - 10;
            }
            if (top + popupRect.height > window.innerHeight) {
                top = caretRect.top - popupRect.height - 4;
            }
            popup.style.left = Math.max(10, left) + 'px';
            popup.style.top = Math.max(10, top) + 'px';
        }
    }

    function triggerNbAutocomplete(editor) {
        if (!editor || editor === composingCellEditor) {
            hideNbAutocomplete();
            return;
        }
        const info = getWordUnderCaretInEditor(editor);
        if (!info || !info.word || info.word.length < 1) {
            hideNbAutocomplete();
            return;
        }

        const candidates = getNbAutocompleteCandidates(info.word);
        if (candidates.length === 0) {
            hideNbAutocomplete();
            return;
        }

        nbAutocompleteState.isOpen = true;
        nbAutocompleteState.candidates = candidates;
        nbAutocompleteState.activeIndex = 0;
        nbAutocompleteState.editor = editor;
        nbAutocompleteState.wordStart = info.start;
        nbAutocompleteState.wordEnd = info.end;
        nbAutocompleteState.caretOffset = info.caretOffset;
        nbAutocompleteState.word = info.word;

        renderNbAutocomplete();
    }

    function hideNbAutocomplete() {
        nbAutocompleteState.isOpen = false;
        nbAutocompleteState.candidates = [];
        nbAutocompleteState.activeIndex = 0;
        nbAutocompleteState.editor = null;
        const popup = document.getElementById('nb-autocomplete-popup');
        if (popup) popup.hidden = true;
    }

    function moveNbAutocompleteIndex(delta) {
        if (!nbAutocompleteState.isOpen || nbAutocompleteState.candidates.length === 0) return;
        const len = nbAutocompleteState.candidates.length;
        nbAutocompleteState.activeIndex = (nbAutocompleteState.activeIndex + delta + len) % len;
        renderNbAutocomplete();
        const popup = document.getElementById('nb-autocomplete-popup');
        const activeItem = popup ? popup.querySelector('.nb-autocomplete-item.active') : null;
        if (activeItem) activeItem.scrollIntoView({ block: 'nearest' });
    }

    function insertSelectedNbCandidate() {
        if (!nbAutocompleteState.isOpen || !nbAutocompleteState.editor) return;
        const candidate = nbAutocompleteState.candidates[nbAutocompleteState.activeIndex];
        const editor = nbAutocompleteState.editor;
        if (!candidate || !editor) {
            hideNbAutocomplete();
            return;
        }

        const source = getEditorText(editor);
        const wordStart = nbAutocompleteState.wordStart;
        const wordEnd = nbAutocompleteState.wordEnd;
        const insertText = candidate.label;

        const newSource = source.slice(0, wordStart) + insertText + source.slice(wordEnd);
        const newCaretOffset = wordStart + insertText.length;

        const cellDiv = editor.closest('.cell');
        editor.setAttribute('data-source', newSource);
        if (cellDiv) cellDiv.setAttribute('data-source', newSource);

        editor.innerHTML = '<pre>' + highlightPythonCode(newSource) + '</pre>';
        setCaretOffsetInEditor(editor, newCaretOffset);

        hideNbAutocomplete();
        notifyModified();
    }

    function notebookString(key, fallback) {
        const strings = window.__notebookStrings || {};
        return typeof strings[key] === 'string' && strings[key] ? strings[key] : fallback;
    }

    function shortcutTitle(key, fallback, shortcut) {
        return notebookString(key, fallback) + ' (' + shortcut + ')';
    }

    async function runCell(cellDiv) {
        const type = getCellType(cellDiv);
        if (type === 'markdown') {
            renderMarkdownCell(cellDiv);
            return;
        }
        if (type !== 'code') return;
        const source = getCellSource(cellDiv);
        const outputDiv = cellDiv.querySelector('.cell-output');
        if (!outputDiv) return;

        const cellIndex = parseInt(cellDiv.getAttribute('data-cell-index'));
        const runBtn = cellDiv.querySelector('.cell-run');

        if (runningCells.has(cellIndex)) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'stopExecution' }));
            } catch (ex) {}
            return;
        }

        runningCells.add(cellIndex);
        cellDiv.classList.add('cell-running');
        if (runBtn) {
            runBtn.textContent = '■ Stop';
            runBtn.classList.add('is-running');
        }
        outputDiv.classList.add('has-output');
        outputDiv.innerHTML = '<span style=""color:#888;"">Running...</span>';

        try {
            const resp = await new Promise((resolve) => {
                window.chrome.webview.postMessage(JSON.stringify({
                    type: 'executeCell',
                    code: source,
                    cellIndex: cellIndex
                }));
                window.__pendingCellExecutions = window.__pendingCellExecutions || {};
                window.__pendingCellExecutions[String(cellIndex)] = resolve;
            });

            let html = renderCellOutputsFromResponse(resp);
            if (html) {
                outputDiv.classList.add('has-output');
                outputDiv.innerHTML = html;
                setTimeout(initMplInteractiveContainers, 50);
            } else {
                outputDiv.classList.remove('has-output');
                outputDiv.innerHTML = '';
            }
            notifyModified();
        } catch (e) {
            const errObj = {
                output_type: ""error"",
                ename: ""CellError"",
                evalue: String(e),
                traceback: [String(e)]
            };
            outputDiv.classList.add('has-output');
            outputDiv.innerHTML = '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(errObj)) + '""' + '><span class=""output-error"">' + escapeHtml(String(e)) + '</span></div>';
        } finally {
            runningCells.delete(cellIndex);
            cellDiv.classList.remove('cell-running');
            if (runBtn) {
                runBtn.textContent = '▶ Run';
                runBtn.classList.remove('is-running');
            }
        }
    }

    async function saveNotebook() {
        const json = getNotebookJson();
        window.chrome.webview.postMessage(JSON.stringify({ type: 'saveNotebook', content: json }));
    }

    function createCell(type, source) {
        const div = document.createElement('div');
        div.className = 'cell cell-' + type;
        div.setAttribute('data-cell-type', type);
        const idx = container.children.length;
        div.setAttribute('data-cell-index', idx);

        if (type === 'markdown') {
            const html = renderMarkdownJs(source || '');
            div.innerHTML = '<div class=""cell-input markdown-cell"" data-source=""' + escapeHtml(source || '') + '"">' +
                '<div class=""markdown-preview"" style=""display:none;"">' + html + '</div>' +
                '<div class=""cell-input-area markdown-editor"" contenteditable=""true"" spellcheck=""false"" style=""display:block;""><pre>' + escapeHtml(source || '') + '</pre></div>' +
                '</div>' +
                '<div class=""cell-toolbar"">' +
                '<button class=""cell-btn cell-run"" title=""Render Markdown (Shift+Enter)"">▶ Render</button>' +
                '<button class=""cell-btn cell-edit"" title=""Edit Markdown"">✎ Edit</button>' +
                '<button class=""cell-btn cell-toggle-type"" title=""Switch to Code"">Code</button>' +
                '<button class=""cell-btn cell-add-above"" title=""' + shortcutTitle('insertAbove', 'Insert Cell Above', 'A') + '"">+ Above</button>' +
                '<button class=""cell-btn cell-add-below"" title=""' + shortcutTitle('insertBelow', 'Insert Cell Below', 'B') + '"">+ Below</button>' +
                '<button class=""cell-btn cell-delete"" title=""' + shortcutTitle('deleteCell', 'Delete Cell', 'D, D') + '"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>';
        } else {
            const highlightedCode = source ? highlightPythonCode(source) : '';
            div.innerHTML = '<div class=""cell-input code-cell"">' +
                '<div class=""cell-input-area code-editor"" contenteditable=""true"" spellcheck=""false"" data-source=""' + escapeHtml(source || '') + '""><pre>' + highlightedCode + '</pre></div>' +
                '<div class=""cell-toolbar"">' +
                '<button class=""cell-btn cell-run"" title=""Run (Shift+Enter)"">▶ Run</button>' +
                '<button class=""cell-btn cell-run-below"" title=""Run Below"">▶|</button>' +
                '<button class=""cell-btn cell-toggle-type"" title=""Switch to Markdown"">Markdown</button>' +
                '<button class=""cell-btn cell-add-above"" title=""' + shortcutTitle('insertAbove', 'Insert Cell Above', 'A') + '"">+ Above</button>' +
                '<button class=""cell-btn cell-add-below"" title=""' + shortcutTitle('insertBelow', 'Insert Cell Below', 'B') + '"">+ Below</button>' +
                '<button class=""cell-btn cell-delete"" title=""' + shortcutTitle('deleteCell', 'Delete Cell', 'D, D') + '"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>' +
                '<div class=""cell-output""></div>' +
                '</div>';
        }
        prepareCell(div);
        return div;
    }

    let lastActiveMarkdownCell = null;
    let lastActiveMarkdownRange = null;
    let composingMarkdownEditor = null;
    let composingCellEditor = null;
    let pendingMarkdownCommand = null;

    function rememberMarkdownSelection(editor) {
        if (!editor || !editor.classList.contains('markdown-editor')) return false;
        const cellDiv = editor.closest('.cell');
        const sel = window.getSelection();
        if (!cellDiv || !sel || sel.rangeCount === 0 ||
            !editor.contains(sel.anchorNode) || !editor.contains(sel.focusNode)) {
            return false;
        }

        lastActiveMarkdownCell = cellDiv;
        lastActiveMarkdownRange = sel.getRangeAt(0).cloneRange();
        return true;
    }

    container.addEventListener('focusin', (e) => {
        const editor = e.target.closest('.markdown-editor');
        const cellDiv = editor ? editor.closest('.cell') : null;
        if (cellDiv && getCellType(cellDiv) === 'markdown') {
            lastActiveMarkdownCell = cellDiv;
            lastActiveMarkdownRange = null;
            rememberMarkdownSelection(editor);
        }
    });

    document.addEventListener('selectionchange', () => {
        const sel = window.getSelection();
        if (!sel || sel.rangeCount === 0 || !sel.anchorNode) return;
        const anchorElement = sel.anchorNode.nodeType === Node.ELEMENT_NODE
            ? sel.anchorNode
            : sel.anchorNode.parentElement;
        const editor = anchorElement && anchorElement.closest
            ? anchorElement.closest('.markdown-editor')
            : null;
        rememberMarkdownSelection(editor);
    });

    container.addEventListener('focusout', (e) => {
        const editor = e.target.closest('.markdown-editor');
        if (!editor) return;
        const cellDiv = editor.closest('.cell');
        if (!cellDiv || getCellType(cellDiv) !== 'markdown') return;
        rememberMarkdownSelection(editor);

        setTimeout(() => {
            if (!document.hasFocus()) {
                return;
            }
            const active = document.activeElement;
            if (active && cellDiv.contains(active)) {
                return;
            }
            renderMarkdownCell(cellDiv);
        }, 150);
    });

    container.addEventListener('compositionstart', (e) => {
        composingCellEditor = e.target.closest('.cell-input-area, [contenteditable=""true""]');
        const editor = e.target.closest('.markdown-editor');
        if (editor) {
            composingMarkdownEditor = editor;
        }
    });

    container.addEventListener('compositionend', (e) => {
        const endedEditor = e.target.closest('.cell-input-area, [contenteditable=""true""]');
        if (endedEditor && endedEditor === composingCellEditor) {
            composingCellEditor = null;
            if (endedEditor.classList.contains('code-editor')) {
                setTimeout(() => {
                    updateCellInputHighlight(endedEditor);
                    triggerNbAutocomplete(endedEditor);
                }, 0);
            }
        }
        const editor = e.target.closest('.markdown-editor');
        if (!editor || editor !== composingMarkdownEditor) return;

        composingMarkdownEditor = null;
        rememberMarkdownSelection(editor);
        const pending = pendingMarkdownCommand;
        pendingMarkdownCommand = null;
        if (pending) {
            setTimeout(() => {
                if (!pending.cell || !pending.cell.isConnected) return;
                applyMarkdownCommandToCell(pending.cell, pending.command, pending.color);
                notifyModified();
            }, 0);
        }
    });

    function focusEditorAtEnd(editor) {
        editor.focus();
        const sel = window.getSelection();
        if (sel) {
            const range = document.createRange();
            const target = editor.querySelector('pre') || editor;
            range.selectNodeContents(target);
            range.collapse(false);
            sel.removeAllRanges();
            sel.addRange(range);
        }
    }

    function focusEditorAtStart(editor) {
        editor.focus();
        const sel = window.getSelection();
        if (!sel) return;
        const target = editor.querySelector('pre') || editor;
        const walker = document.createTreeWalker(target, NodeFilter.SHOW_TEXT);
        const textNode = walker.nextNode();
        const range = document.createRange();
        if (textNode) {
            range.setStart(textNode, 0);
        } else {
            range.setStart(target, 0);
        }
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
    }

    function scrollEditorCaretIntoView(editor, direction) {
        editor.scrollIntoView({
            block: direction < 0 ? 'end' : 'start',
            inline: 'nearest',
            behavior: 'auto'
        });

        const header = document.getElementById('notebook-header');
        const headerRect = header?.getBoundingClientRect();
        const viewportTop = Math.max(8, (headerRect?.bottom || 0) + 8);
        const viewportBottom = window.innerHeight - 8;
        const sel = window.getSelection();
        const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
        let caretRect = range?.getBoundingClientRect();

        if (!caretRect || (caretRect.width === 0 && caretRect.height === 0)) {
            const editorRect = editor.getBoundingClientRect();
            const lineHeight = parseFloat(window.getComputedStyle(editor).lineHeight) || 21;
            caretRect = direction < 0
                ? { top: editorRect.bottom - lineHeight, bottom: editorRect.bottom }
                : { top: editorRect.top, bottom: editorRect.top + lineHeight };
        }

        let scrollDelta = 0;
        if (caretRect.top < viewportTop) {
            scrollDelta = caretRect.top - viewportTop;
        } else if (caretRect.bottom > viewportBottom) {
            scrollDelta = caretRect.bottom - viewportBottom;
        }

        if (Math.abs(scrollDelta) >= 1) {
            window.scrollBy({ top: scrollDelta, left: 0, behavior: 'auto' });
        }
    }

    function isCaretOnBoundaryLine(editor, direction) {
        const sel = window.getSelection();
        if (!sel || !sel.isCollapsed || sel.rangeCount === 0) return false;
        const caret = sel.getRangeAt(0);
        if (!editor.contains(caret.startContainer)) return false;

        const caretOffset = getCaretOffsetInEditor(editor);
        if (!Number.isInteger(caretOffset)) return false;
        const text = getEditorText(editor);
        if (direction < 0) {
            return !text.slice(0, caretOffset).includes('\n');
        }
        const textWithoutTerminalBreak = text.endsWith('\n')
            ? text.slice(0, -1)
            : text;
        return !textWithoutTerminalBreak
            .slice(Math.min(caretOffset, textWithoutTerminalBreak.length))
            .includes('\n');
    }

    function moveEditorFocusToAdjacentCell(cellDiv, direction) {
        const cells = Array.from(container.querySelectorAll('.cell'));
        const currentIndex = cells.indexOf(cellDiv);
        const targetCell = cells[currentIndex + direction];
        if (!targetCell) return false;

        selectCell(targetCell, false);
        if (getCellType(targetCell) === 'markdown') {
            editMarkdownCell(targetCell, false);
        }
        const targetEditor = targetCell.querySelector('.code-editor, .markdown-editor, .raw-editor, .cell-input-area');
        if (!targetEditor) return false;

        if (direction < 0) {
            focusEditorAtEnd(targetEditor);
        } else {
            focusEditorAtStart(targetEditor);
        }
        scrollEditorCaretIntoView(targetEditor, direction);
        return true;
    }

    function focusEditorAtLine(editor, lineIndex) {
        editor.focus();
        const sel = window.getSelection();
        if (!sel) return;
        const pre = editor.querySelector('pre') || editor;
        const textNode = pre.firstChild;
        if (!textNode || textNode.nodeType !== Node.TEXT_NODE) {
            focusEditorAtEnd(editor);
            return;
        }
        const text = textNode.nodeValue || '';
        const lines = text.split('\n');
        let offset = 0;
        const target = Math.min(Math.max(0, lineIndex), lines.length - 1);
        for (let i = 0; i <= target; i++) {
            offset += lines[i].length;
            if (i < target) offset += 1;
        }
        offset = Math.min(offset, text.length);
        const range = document.createRange();
        range.setStart(textNode, offset);
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
    }

    function cycleHeadingText(lineText) {
        const match = lineText.match(/^(#{1,6})\s*(.*)/);
        if (!match) {
            return '# ' + lineText;
        }
        const hashes = match[1];
        const content = match[2];
        if (hashes.length < 6) {
            return '#'.repeat(hashes.length + 1) + ' ' + content;
        } else {
            return content;
        }
    }

    function toggleLinePrefixText(lineText, prefix) {
        const listMatch = lineText.match(/^([-*+]\s+|\d+\.\s+|>\s*|- \[\s*\]\s*)/);
        if (lineText.startsWith(prefix)) {
            return lineText.slice(prefix.length);
        } else if (listMatch) {
            return prefix + lineText.slice(listMatch[0].length);
        } else {
            return prefix + lineText;
        }
    }

    function getLineIndexFromRange(editor, range) {
        if (!range || !editor) return 0;
        try {
            const preRange = document.createRange();
            preRange.selectNodeContents(editor);
            preRange.setEnd(range.startContainer, range.startOffset);
            const tempDiv = document.createElement('div');
            tempDiv.style.position = 'absolute';
            tempDiv.style.left = '-9999px';
            tempDiv.style.visibility = 'hidden';
            tempDiv.appendChild(preRange.cloneContents());
            document.body.appendChild(tempDiv);
            tempDiv.querySelectorAll('br').forEach(br => br.replaceWith(document.createTextNode('\n')));
            tempDiv.querySelectorAll('div, p').forEach(div => div.before(document.createTextNode('\n')));
            const textBefore = (tempDiv.textContent || tempDiv.innerText || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
            tempDiv.remove();
            const lineIndex = textBefore.split('\n').length - 1;
            return Math.max(0, lineIndex);
        } catch {
            return 0;
        }
    }

    function cycleHeadingInEditor(editor, range) {
        let text = getEditorText(editor);
        const lines = text.split('\n');
        let targetIndex = 0;
        if (range) {
            targetIndex = getLineIndexFromRange(editor, range);
        }
        if (targetIndex >= 0 && targetIndex < lines.length) {
            lines[targetIndex] = cycleHeadingText(lines[targetIndex]);
            editor.innerHTML = '<pre>' + escapeHtml(lines.join('\n')) + '</pre>';
            focusEditorAtLine(editor, targetIndex);
        }
    }

    function togglePrefixInEditor(editor, range, prefix) {
        let text = getEditorText(editor);
        const lines = text.split('\n');
        let targetIndex = 0;
        if (range) {
            targetIndex = getLineIndexFromRange(editor, range);
        }
        if (targetIndex >= 0 && targetIndex < lines.length) {
            lines[targetIndex] = toggleLinePrefixText(lines[targetIndex], prefix);
            editor.innerHTML = '<pre>' + escapeHtml(lines.join('\n')) + '</pre>';
            focusEditorAtLine(editor, targetIndex);
        }
    }

    function toggleWrapperInEditor(editor, range, opening, closing) {
        closing = closing || opening;
        if (range) {
            const selectedText = range.toString();
            if (selectedText.length > 0) {
                if (selectedText.length >= opening.length + closing.length &&
                    selectedText.startsWith(opening) && selectedText.endsWith(closing)) {
                    const inner = selectedText.slice(opening.length, selectedText.length - closing.length);
                    range.deleteContents();
                    const innerNode = document.createTextNode(inner);
                    range.insertNode(innerNode);
                    range.setStart(innerNode, 0);
                    range.setEnd(innerNode, inner.length);
                    const sel = window.getSelection();
                    sel.removeAllRanges();
                    sel.addRange(range);
                    return;
                }
                const nodeText = range.startContainer.textContent || '';
                const startCol = range.startOffset;
                const endCol = range.endOffset;
                if (startCol >= opening.length && endCol + closing.length <= nodeText.length &&
                    nodeText.slice(startCol - opening.length, startCol) === opening &&
                    nodeText.slice(endCol, endCol + closing.length) === closing) {
                    const node = range.startContainer;
                    const before = nodeText.slice(0, startCol - opening.length);
                    const mid = nodeText.slice(startCol, endCol);
                    const after = nodeText.slice(endCol + closing.length);
                    node.textContent = before + mid + after;
                    const sel = window.getSelection();
                    const newRange = document.createRange();
                    newRange.setStart(node, before.length);
                    newRange.setEnd(node, before.length + mid.length);
                    sel.removeAllRanges();
                    sel.addRange(newRange);
                    return;
                }
                range.deleteContents();
                const wrappedNode = document.createTextNode(opening + selectedText + closing);
                range.insertNode(wrappedNode);
                range.setStart(wrappedNode, opening.length);
                range.setEnd(wrappedNode, opening.length + selectedText.length);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            } else {
                const nodeText = range.startContainer.textContent || '';
                const caret = range.startOffset;
                if (caret >= opening.length && caret + closing.length <= nodeText.length &&
                    nodeText.slice(caret - opening.length, caret) === opening &&
                    nodeText.slice(caret, caret + closing.length) === closing) {
                    const node = range.startContainer;
                    const before = nodeText.slice(0, caret - opening.length);
                    const after = nodeText.slice(caret + closing.length);
                    node.textContent = before + after;
                    const sel = window.getSelection();
                    const newRange = document.createRange();
                    newRange.setStart(node, before.length);
                    newRange.collapse(true);
                    sel.removeAllRanges();
                    sel.addRange(newRange);
                    return;
                }

                const pNode = document.createTextNode(opening);
                const sNode = document.createTextNode(closing);
                range.deleteContents();
                if (closing) range.insertNode(sNode);
                range.insertNode(pNode);
                range.setStartAfter(pNode);
                range.collapse(true);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            }
        } else {
            const currentText = getEditorText(editor);
            const needNewline = currentText.length > 0 && !currentText.endsWith('\n');
            editor.innerHTML = '<pre>' + escapeHtml(currentText + (needNewline ? '\n' : '') + opening + closing) + '</pre>';
            focusEditorAtEnd(editor);
        }
    }

    function applyMarkdownCommandToCell(cellDiv, command, color) {
        if (getCellType(cellDiv) !== 'markdown') return;
        const editor = cellDiv.querySelector('.markdown-editor');
        if (!editor) return;

        const sel = window.getSelection();
        let range = null;

        if (sel && sel.rangeCount > 0 &&
            editor.contains(sel.anchorNode) && editor.contains(sel.focusNode)) {
            range = sel.getRangeAt(0).cloneRange();
        } else if (lastActiveMarkdownCell === cellDiv &&
            lastActiveMarkdownRange &&
            editor.contains(lastActiveMarkdownRange.startContainer) &&
            editor.contains(lastActiveMarkdownRange.endContainer)) {
            range = lastActiveMarkdownRange.cloneRange();
        }

        editMarkdownCell(cellDiv, false);
        if (range) {
            editor.focus();
            const restoredSelection = window.getSelection();
            if (restoredSelection) {
                restoredSelection.removeAllRanges();
                restoredSelection.addRange(range);
            }
        } else {
            focusEditorAtEnd(editor);
        }

        if (command === 'heading') {
            cycleHeadingInEditor(editor, range);
            return;
        }

        if (command === 'ul') { togglePrefixInEditor(editor, range, '- '); return; }
        if (command === 'ol') { togglePrefixInEditor(editor, range, '1. '); return; }
        if (command === 'quote') { togglePrefixInEditor(editor, range, '> '); return; }
        if (command === 'task') { togglePrefixInEditor(editor, range, '- [ ] '); return; }

        let opening = '', closing = '';
        switch (command) {
            case 'bold': opening = '**'; closing = '**'; break;
            case 'italic': opening = '*'; closing = '*'; break;
            case 'underline': opening = '<u>'; closing = '</u>'; break;
            case 'highlight': opening = '<mark>'; closing = '</mark>'; break;
            case 'inlineCode': opening = '`'; closing = '`'; break;
            case 'codeBlock': opening = '```\n'; closing = '\n```'; break;
            case 'link': opening = '['; closing = '](https://)'; break;
            case 'image': opening = '!['; closing = '](image_url)'; break;
            case 'table': opening = '\n| Header 1 | Header 2 |\n| --- | --- |\n|  |  |\n'; closing = ''; break;
            case 'arrow': opening = '-> '; closing = ''; break;
            case 'textColor':
                if (color) {
                    opening = '<span style=""color:' + color + ';"">';
                    closing = '</span>';
                }
                break;
            default:
                return;
        }

        if (!opening && !closing) return;

        toggleWrapperInEditor(editor, range, opening, closing);
        notifyModified();
    }

    window.addEventListener('appMarkdownCommand', (e) => {
        const detail = e.detail || {};
        const cmd = detail.command;
        const color = detail.color;
        if (!cmd) return;

        if (composingMarkdownEditor) {
            pendingMarkdownCommand = {
                cell: composingMarkdownEditor.closest('.cell'),
                command: cmd,
                color
            };
            return;
        }

        let activeCell = (document.activeElement && document.activeElement.closest) ? document.activeElement.closest('.cell') : null;
        if (!activeCell || getCellType(activeCell) !== 'markdown') {
            activeCell = lastActiveMarkdownCell || container.querySelector('.cell[data-cell-type=""markdown""]');
        }
        if (activeCell) {
            applyMarkdownCommandToCell(activeCell, cmd, color);
            notifyModified();
        }
    });

    function reindexCells() {
        container.querySelectorAll('.cell').forEach((c, i) => {
            c.setAttribute('data-cell-index', i);
        });
    }

    let selectedCell = null;
    let draggedCell = null;
    let dragPointerId = null;
    let dragStartX = 0;
    let dragStartY = 0;
    let dragDropPosition = null;
    let dragHasMoved = false;
    let commandMode = false;
    let pendingDeleteAt = 0;

    function selectCell(cellDiv, enterCommandMode) {
        if (!cellDiv || !cellDiv.classList.contains('cell')) return;
        if (selectedCell && selectedCell !== cellDiv) {
            selectedCell.classList.remove('is-selected', 'is-command-mode');
        }
        selectedCell = cellDiv;
        commandMode = !!enterCommandMode;
        selectedCell.classList.add('is-selected');
        selectedCell.classList.toggle('is-command-mode', commandMode);
        window.__notebookSelectedCell = selectedCell;
    }

    window.__notebookSelectCell = selectCell;

    function selectAdjacentCell(direction) {
        if (!selectedCell) return;
        const cells = Array.from(container.querySelectorAll('.cell'));
        const currentIndex = cells.indexOf(selectedCell);
        if (currentIndex < 0) return;
        const targetIndex = Math.max(0, Math.min(cells.length - 1, currentIndex + direction));
        const targetCell = cells[targetIndex];
        selectCell(targetCell, true);
        targetCell.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }

    function enterSelectedCellEditMode() {
        if (!selectedCell || !selectedCell.isConnected) return;
        selectCell(selectedCell, false);
        if (getCellType(selectedCell) === 'markdown') {
            editMarkdownCell(selectedCell);
            return;
        }

        const editor = selectedCell.querySelector('.code-editor, .raw-editor, .cell-input-area');
        if (editor) focusEditorAtEnd(editor);
    }

    function prepareCell(cellDiv) {
        if (!cellDiv || cellDiv.dataset.notebookPrepared === 'true') return;
        cellDiv.dataset.notebookPrepared = 'true';
        const handle = document.createElement('div');
        handle.className = 'cell-drag-handle';
        handle.draggable = false;
        handle.tabIndex = 0;
        handle.setAttribute('role', 'button');
        handle.textContent = '⠿';
        handle.title = notebookString('dragCell', 'Drag to reorder cell');
        handle.setAttribute('aria-label', handle.title);
        cellDiv.insertBefore(handle, cellDiv.firstChild);
    }

    function clearDragIndicators() {
        container.querySelectorAll('.cell.drag-before, .cell.drag-after').forEach(cell => {
            cell.classList.remove('drag-before', 'drag-after');
        });
    }

    function getCellDropPosition(clientY) {
        const cells = Array.from(container.querySelectorAll('.cell'))
            .filter(cell => cell !== draggedCell);
        if (cells.length === 0) return null;

        for (const cell of cells) {
            const rect = cell.getBoundingClientRect();
            if (clientY < rect.top + rect.height / 2) {
                return { cell, before: true };
            }
        }
        return { cell: cells[cells.length - 1], before: false };
    }

    function finishCellPointerDrag(commitMove) {
        const movingCell = draggedCell;
        if (commitMove && dragHasMoved && movingCell && dragDropPosition) {
            if (dragDropPosition.before) {
                container.insertBefore(movingCell, dragDropPosition.cell);
            } else {
                container.insertBefore(movingCell, dragDropPosition.cell.nextElementSibling);
            }
            reindexCells();
            selectCell(movingCell, true);
            notifyModified();
        }

        if (movingCell) movingCell.classList.remove('is-dragging');
        document.body.classList.remove('is-cell-reordering');
        clearDragIndicators();
        draggedCell = null;
        dragPointerId = null;
        dragDropPosition = null;
        dragHasMoved = false;
    }

    container.querySelectorAll('.cell').forEach(prepareCell);
    const initialCell = container.querySelector('.cell');
    if (initialCell) selectCell(initialCell, true);

    container.addEventListener('focusin', e => {
        const cellDiv = e.target.closest('.cell');
        if (cellDiv) selectCell(cellDiv, false);
    });

    container.addEventListener('pointerdown', e => {
        const cellDiv = e.target.closest('.cell');
        if (!cellDiv) return;
        const handle = e.target.closest('.cell-drag-handle');
        if (handle && !composingCellEditor) {
            e.preventDefault();
            e.stopPropagation();
            draggedCell = cellDiv;
            dragPointerId = e.pointerId;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            dragDropPosition = null;
            dragHasMoved = false;
            selectCell(cellDiv, true);
            try {
                handle.setPointerCapture(e.pointerId);
            } catch {}
            return;
        }
        const editingTarget = e.target.closest('[contenteditable=""true""], input, textarea, select');
        selectCell(cellDiv, !editingTarget);
    });

    container.addEventListener('pointermove', e => {
        if (!draggedCell || e.pointerId !== dragPointerId) return;
        const distance = Math.hypot(e.clientX - dragStartX, e.clientY - dragStartY);
        if (!dragHasMoved && distance < 4) return;

        e.preventDefault();
        if (!dragHasMoved) {
            dragHasMoved = true;
            draggedCell.classList.add('is-dragging');
            document.body.classList.add('is-cell-reordering');
        }

        clearDragIndicators();
        dragDropPosition = getCellDropPosition(e.clientY);
        if (dragDropPosition) {
            dragDropPosition.cell.classList.add(dragDropPosition.before ? 'drag-before' : 'drag-after');
        }

        const edgeSize = 48;
        if (e.clientY < edgeSize) {
            window.scrollBy(0, -Math.min(18, edgeSize - e.clientY));
        } else if (e.clientY > window.innerHeight - edgeSize) {
            window.scrollBy(0, Math.min(18, e.clientY - (window.innerHeight - edgeSize)));
        }
    });

    container.addEventListener('pointerup', e => {
        if (!draggedCell || e.pointerId !== dragPointerId) return;
        e.preventDefault();
        e.stopPropagation();
        finishCellPointerDrag(true);
    });

    container.addEventListener('pointercancel', e => {
        if (e.pointerId === dragPointerId) finishCellPointerDrag(false);
    });

    // Double click to edit markdown preview
    container.addEventListener('dblclick', (e) => {
        const preview = e.target.closest('.markdown-preview');
        if (preview) {
            const cellDiv = preview.closest('.cell');
            if (cellDiv) editMarkdownCell(cellDiv);
        }
    });

    // Event delegation
    container.addEventListener('click', async (e) => {
        const preview = e.target.closest('.markdown-preview');
        if (preview) {
            const cellDiv = preview.closest('.cell');
            if (cellDiv) {
                editMarkdownCell(cellDiv);
                return;
            }
        }

        const fmtBtn = e.target.closest('.cell-md-fmt');
        if (fmtBtn) {
            const cellDiv = fmtBtn.closest('.cell');
            const fmt = fmtBtn.getAttribute('data-fmt');
            if (cellDiv && fmt) {
                insertMarkdownFormatting(cellDiv, fmt);
                notifyModified();
                return;
            }
        }

        const btn = e.target.closest('.cell-btn');
        if (!btn) return;
        const cellDiv = btn.closest('.cell');
        if (!cellDiv) return;

        if (btn.classList.contains('cell-run')) {
            if (getCellType(cellDiv) === 'markdown') {
                renderMarkdownCell(cellDiv);
            } else {
                await runCell(cellDiv);
            }
        } else if (btn.classList.contains('cell-edit')) {
            if (getCellType(cellDiv) === 'markdown') {
                editMarkdownCell(cellDiv);
            }
        } else if (btn.classList.contains('cell-toggle-type')) {
            const currentType = getCellType(cellDiv);
            const source = getCellSource(cellDiv);
            const newType = currentType === 'markdown' ? 'code' : 'markdown';
            const newCell = createCell(newType, source);
            cellDiv.replaceWith(newCell);
            reindexCells();
            selectCell(newCell, false);
            if (newType === 'markdown') {
                editMarkdownCell(newCell);
            } else {
                const editor = newCell.querySelector('.cell-input-area');
                if (editor) editor.focus();
            }
            notifyModified();
        } else if (btn.classList.contains('cell-run-below')) {
            const cells = Array.from(container.querySelectorAll('.cell'));
            const startIdx = parseInt(cellDiv.getAttribute('data-cell-index'));
            for (let i = startIdx; i < cells.length; i++) {
                if (getCellType(cells[i]) === 'code') {
                    await runCell(cells[i]);
                }
            }
        } else if (btn.classList.contains('cell-add-above')) {
            handleContextMenuAction('add-above', cellDiv);
        } else if (btn.classList.contains('cell-add-below')) {
            handleContextMenuAction('add-below', cellDiv);
        } else if (btn.classList.contains('cell-delete')) {
            handleContextMenuAction('delete', cellDiv);
        } else if (btn.classList.contains('cell-move-up')) {
            const prev = cellDiv.previousElementSibling;
            if (prev) {
                container.insertBefore(cellDiv, prev);
                reindexCells();
                notifyModified();
            }
        } else if (btn.classList.contains('cell-move-down')) {
            const next = cellDiv.nextElementSibling;
            if (next) {
                container.insertBefore(next, cellDiv);
                reindexCells();
                notifyModified();
            }
        }
    });

    let clipboardCell = null;

    function getCaretOffsetInEditor(editor) {
        const sel = window.getSelection();
        if (!editor || !sel || sel.rangeCount === 0) return null;
        const caret = sel.getRangeAt(0);
        if (!editor.contains(caret.startContainer)) return null;

        const beforeCaret = document.createRange();
        beforeCaret.selectNodeContents(editor);
        beforeCaret.setEnd(caret.startContainer, caret.startOffset);
        const holder = document.createElement('div');
        holder.appendChild(beforeCaret.cloneContents());
        return getEditorText(holder).length;
    }

    function splitCellAtCursor(cellDiv, preservedCaretOffset = null) {
        if (!cellDiv) return;
        const type = getCellType(cellDiv);
        const fullSource = getCellSource(cellDiv);
        let head = fullSource;
        let tail = '';

        const editor = cellDiv.querySelector('.code-editor, .markdown-editor, .raw-editor, .cell-input-area');
        const liveCaretOffset = getCaretOffsetInEditor(editor);
        const requestedOffset = Number.isInteger(preservedCaretOffset)
            ? preservedCaretOffset
            : liveCaretOffset;
        if (Number.isInteger(requestedOffset)) {
            const caretPos = Math.max(0, Math.min(fullSource.length, requestedOffset));
            head = fullSource.substring(0, caretPos);
            tail = fullSource.substring(caretPos);
        } else {
            const lines = fullSource.split('\n');
            if (lines.length > 1) {
                const mid = Math.floor(lines.length / 2);
                head = lines.slice(0, mid).join('\n');
                tail = lines.slice(mid).join('\n');
            }
        }

        if (type === 'markdown') {
            const ed = cellDiv.querySelector('.markdown-editor');
            if (ed) {
                ed.innerHTML = '<pre>' + escapeHtml(head) + '</pre>';
                ed.setAttribute('data-source', head);
            }
            renderMarkdownCell(cellDiv);
        } else {
            const ed = cellDiv.querySelector('.cell-input-area');
            if (ed) {
                ed.innerHTML = '<pre>' + highlightPythonCode(head) + '</pre>';
                ed.setAttribute('data-source', head);
            }
        }
        cellDiv.setAttribute('data-source', head);

        const newCell = createCell(type, tail);
        const next = cellDiv.nextElementSibling;
        if (next) container.insertBefore(newCell, next);
        else container.appendChild(newCell);
        reindexCells();
        selectCell(newCell, false);
        if (type === 'markdown') editMarkdownCell(newCell, false);
        const newEditor = newCell.querySelector('.code-editor, .markdown-editor, .raw-editor, .cell-input-area');
        if (newEditor) focusEditorAtStart(newEditor);
        notifyModified();
    }

    function mergeCells(targetCell, sourceCell) {
        if (!targetCell || !sourceCell) return;
        const targetType = getCellType(targetCell);
        const targetSource = getCellSource(targetCell);
        const sourceSource = getCellSource(sourceCell);

        const merged = (targetSource ? targetSource + '\n' : '') + sourceSource;

        if (targetType === 'markdown') {
            const ed = targetCell.querySelector('.markdown-editor');
            if (ed) ed.innerHTML = '<pre>' + escapeHtml(merged) + '</pre>';
            renderMarkdownCell(targetCell);
        } else {
            const ed = targetCell.querySelector('.cell-input-area');
            if (ed) ed.innerHTML = '<pre>' + highlightPythonCode(merged) + '</pre>';
        }
        targetCell.setAttribute('data-source', merged);

        sourceCell.remove();
        reindexCells();
        notifyModified();
    }

    function showContextMenu(x, y, cellDiv, preservedCaretOffset = null) {
        let menu = document.getElementById('nb-context-menu');
        if (!menu) {
            menu = document.createElement('div');
            menu.id = 'nb-context-menu';
            menu.className = 'nb-context-menu';
            document.body.appendChild(menu);
        }

        const isCode = cellDiv && getCellType(cellDiv) === 'code';
        const hasPrev = cellDiv && cellDiv.previousElementSibling && cellDiv.previousElementSibling.classList.contains('cell');
        const hasNext = cellDiv && cellDiv.nextElementSibling && cellDiv.nextElementSibling.classList.contains('cell');
        const outputDiv = cellDiv ? cellDiv.querySelector('.cell-output') : null;
        const hasOutput = isCode && outputDiv && (outputDiv.classList.contains('has-output') || outputDiv.children.length > 0 || outputDiv.textContent.trim().length > 0);

        function menuItem(icon, label, action, shortcut, disabled) {
            return '<div class=""nb-context-menu-item ' + (disabled ? 'disabled' : '') + '"" data-action=""' + action + '"">' +
                '<span>' + icon + '</span><span class=""nb-context-menu-label"">' + escapeHtml(label) + '</span>' +
                (shortcut ? '<span class=""nb-context-menu-shortcut"">' + escapeHtml(shortcut) + '</span>' : '') +
                '</div>';
        }

        menu.innerHTML =
            menuItem('➕', notebookString('insertAbove', 'Insert Cell Above'), 'add-above', 'A', false) +
            menuItem('➕', notebookString('insertBelow', 'Insert Cell Below'), 'add-below', 'B', false) +
            '<div class=""nb-context-menu-divider""></div>' +
            menuItem('✂️', notebookString('cutCell', 'Cut Cell'), 'cut', 'X', !cellDiv) +
            menuItem('📋', notebookString('copyCell', 'Copy Cell'), 'copy', 'C', !cellDiv) +
            menuItem('📑', notebookString('pasteAbove', 'Paste Cell Above'), 'paste-above', '', !clipboardCell) +
            menuItem('📑', notebookString('pasteBelow', 'Paste Cell Below'), 'paste-below', 'V', !clipboardCell) +
            menuItem('🗑️', notebookString('deleteCell', 'Delete Cell'), 'delete', 'D, D', !cellDiv) +
            '<div class=""nb-context-menu-divider""></div>' +
            menuItem('✂️|', notebookString('splitCell', 'Split Cell'), 'split', '', !cellDiv) +
            menuItem('⬆️', notebookString('mergeAbove', 'Merge Cell Above'), 'merge-above', '', !hasPrev) +
            menuItem('⬇️', notebookString('mergeBelow', 'Merge Cell Below'), 'merge-below', '', !hasNext) +
            '<div class=""nb-context-menu-divider""></div>' +
            menuItem('🧹', notebookString('clearOutput', 'Clear Cell Output'), 'clear-output', '', !hasOutput) +
            '<div class=""nb-context-menu-divider""></div>' +
            menuItem('⌨️', notebookString('commandMode', 'Command Mode'), 'command-mode', 'Esc', !cellDiv);

        menu.style.display = 'block';
        const rect = menu.getBoundingClientRect();
        let left = x;
        let top = y;
        if (left + rect.width > window.innerWidth) left = window.innerWidth - rect.width - 8;
        if (top + rect.height > window.innerHeight) top = window.innerHeight - rect.height - 8;
        if (left < 0) left = 8;
        if (top < 0) top = 8;
        menu.style.left = left + 'px';
        menu.style.top = top + 'px';

        menu.onclick = function(e) {
            const item = e.target.closest('.nb-context-menu-item');
            if (!item || item.classList.contains('disabled')) return;
            const action = item.getAttribute('data-action');
            hideContextMenu();
            handleContextMenuAction(action, cellDiv, true, preservedCaretOffset);
        };
    }

    function hideContextMenu() {
        const menu = document.getElementById('nb-context-menu');
        if (menu) menu.style.display = 'none';
    }

    function handleContextMenuAction(action, cellDiv, enterEditMode = true, preservedCaretOffset = null) {
        if (!cellDiv && (action === 'add-above' || action === 'add-below')) {
            const cells = container.querySelectorAll('.cell');
            cellDiv = cells.length > 0 ? cells[cells.length - 1] : null;
        }

        switch (action) {
            case 'add-above': {
                const newCell = createCell('code', '');
                if (cellDiv) container.insertBefore(newCell, cellDiv);
                else container.appendChild(newCell);
                reindexCells();
                selectCell(newCell, !enterEditMode);
                const editor = newCell.querySelector('.cell-input-area');
                if (enterEditMode && editor) editor.focus();
                notifyModified();
                break;
            }
            case 'add-below': {
                const newCell = createCell('code', '');
                if (cellDiv) {
                    const next = cellDiv.nextElementSibling;
                    if (next) container.insertBefore(newCell, next);
                    else container.appendChild(newCell);
                } else {
                    container.appendChild(newCell);
                }
                reindexCells();
                selectCell(newCell, !enterEditMode);
                const editor = newCell.querySelector('.cell-input-area');
                if (enterEditMode && editor) editor.focus();
                notifyModified();
                break;
            }
            case 'cut': {
                if (!cellDiv) return;
                const fallbackCell = cellDiv.nextElementSibling || cellDiv.previousElementSibling;
                clipboardCell = { type: getCellType(cellDiv), source: getCellSource(cellDiv) };
                cellDiv.remove();
                reindexCells();
                if (fallbackCell && fallbackCell.classList.contains('cell')) selectCell(fallbackCell, true);
                else {
                    selectedCell = null;
                    window.__notebookSelectedCell = null;
                }
                notifyModified();
                break;
            }
            case 'copy': {
                if (!cellDiv) return;
                clipboardCell = { type: getCellType(cellDiv), source: getCellSource(cellDiv) };
                break;
            }
            case 'delete': {
                if (!cellDiv) return;
                const fallbackCell = cellDiv.nextElementSibling || cellDiv.previousElementSibling;
                cellDiv.remove();
                reindexCells();
                if (fallbackCell && fallbackCell.classList.contains('cell')) selectCell(fallbackCell, true);
                else {
                    selectedCell = null;
                    window.__notebookSelectedCell = null;
                }
                notifyModified();
                break;
            }
            case 'paste-above': {
                if (!clipboardCell) return;
                const newCell = createCell(clipboardCell.type, clipboardCell.source);
                if (cellDiv) container.insertBefore(newCell, cellDiv);
                else container.appendChild(newCell);
                reindexCells();
                selectCell(newCell, !enterEditMode);
                notifyModified();
                break;
            }
            case 'paste-below': {
                if (!clipboardCell) return;
                const newCell = createCell(clipboardCell.type, clipboardCell.source);
                if (cellDiv) {
                    const next = cellDiv.nextElementSibling;
                    if (next) container.insertBefore(newCell, next);
                    else container.appendChild(newCell);
                } else {
                    container.appendChild(newCell);
                }
                reindexCells();
                selectCell(newCell, !enterEditMode);
                notifyModified();
                break;
            }
            case 'split': {
                if (!cellDiv) return;
                splitCellAtCursor(cellDiv, preservedCaretOffset);
                break;
            }
            case 'merge-above': {
                if (!cellDiv) return;
                const prev = cellDiv.previousElementSibling;
                if (prev && prev.classList.contains('cell')) {
                    mergeCells(prev, cellDiv);
                    selectCell(prev, true);
                }
                break;
            }
            case 'merge-below': {
                if (!cellDiv) return;
                const next = cellDiv.nextElementSibling;
                if (next && next.classList.contains('cell')) {
                    mergeCells(cellDiv, next);
                    selectCell(cellDiv, true);
                }
                break;
            }
            case 'clear-output': {
                if (!cellDiv) return;
                const outputDiv = cellDiv.querySelector('.cell-output');
                if (outputDiv) {
                    outputDiv.innerHTML = '';
                    outputDiv.classList.remove('has-output');
                    notifyModified();
                }
                break;
            }
            case 'command-mode': {
                if (!cellDiv) return;
                const active = document.activeElement;
                if (active && active.blur) active.blur();
                selectCell(cellDiv, true);
                break;
            }
        }
    }

    document.addEventListener('click', (e) => {
        const item = e.target.closest('.nb-autocomplete-item');
        if (item) {
            const idx = parseInt(item.getAttribute('data-index') || '0');
            nbAutocompleteState.activeIndex = idx;
            insertSelectedNbCandidate();
            return;
        }
        hideNbAutocomplete();
        hideContextMenu();
    });
    document.addEventListener('scroll', hideContextMenu, true);
    document.addEventListener('contextmenu', (e) => {
        if (e.target.closest('.mpl-viewport, .mpl-toolbar')) return;
        const cellDiv = e.target.closest('.cell');
        const editor = e.target.closest('.code-editor, .markdown-editor, .raw-editor, .cell-input-area');
        const preservedCaretOffset = cellDiv && editor
            ? getCaretOffsetInEditor(editor)
            : null;
        e.preventDefault();
        if (cellDiv) selectCell(cellDiv, !e.target.closest('[contenteditable=""true""], input, textarea, select'));
        showContextMenu(e.clientX, e.clientY, cellDiv, preservedCaretOffset);
    });

    document.addEventListener('keydown', e => {
        if (e.target.closest && e.target.closest('#notebook-find-bar')) return;
        if (e.isComposing || e.keyCode === 229 || composingCellEditor) return;
        if (e.ctrlKey || e.metaKey || e.altKey) return;

        const activeCell = e.target.closest ? e.target.closest('.cell') : null;
        if (e.key === 'Escape') {
            const targetCell = activeCell || selectedCell;
            if (!targetCell) return;
            e.preventDefault();
            e.stopPropagation();
            const active = document.activeElement;
            if (active && active.blur) active.blur();
            selectCell(targetCell, true);
            pendingDeleteAt = 0;
            return;
        }

        const active = document.activeElement;
        const isEditing = active && active.closest &&
            active.closest('[contenteditable=""true""], input, textarea, select');
        if (!commandMode || isEditing || !selectedCell || !selectedCell.isConnected) return;

        const key = String(e.key || '').toLowerCase();
        if (e.shiftKey) {
            if (key !== 'enter') return;
            e.preventDefault();
            e.stopPropagation();
            pendingDeleteAt = 0;
            const executedCell = selectedCell;
            runCell(executedCell).then(() => {
                if (!executedCell.isConnected) return;
                let nextCell = executedCell.nextElementSibling;
                if (!nextCell) {
                    nextCell = createCell('code', '');
                    container.appendChild(nextCell);
                    reindexCells();
                    notifyModified();
                }
                selectCell(nextCell, true);
                nextCell.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
            });
            return;
        }
        if (key === 'arrowup' || key === 'arrowdown') {
            e.preventDefault();
            e.stopPropagation();
            pendingDeleteAt = 0;
            selectAdjacentCell(key === 'arrowup' ? -1 : 1);
            return;
        }
        if (key === 'enter') {
            e.preventDefault();
            e.stopPropagation();
            pendingDeleteAt = 0;
            enterSelectedCellEditMode();
            return;
        }
        if (e.repeat) return;
        if (key !== 'd') pendingDeleteAt = 0;

        if (key === 'a') {
            e.preventDefault();
            handleContextMenuAction('add-above', selectedCell, false);
        } else if (key === 'b') {
            e.preventDefault();
            handleContextMenuAction('add-below', selectedCell, false);
        } else if (key === 'x') {
            e.preventDefault();
            handleContextMenuAction('cut', selectedCell, false);
        } else if (key === 'c') {
            e.preventDefault();
            handleContextMenuAction('copy', selectedCell, false);
        } else if (key === 'v') {
            e.preventDefault();
            handleContextMenuAction('paste-below', selectedCell, false);
        } else if (key === 'd') {
            e.preventDefault();
            const now = Date.now();
            if (pendingDeleteAt && now - pendingDeleteAt <= 600) {
                pendingDeleteAt = 0;
                handleContextMenuAction('delete', selectedCell, false);
            } else {
                pendingDeleteAt = now;
            }
        }
    }, true);

    // Real-time stream output handler for tqdm / stdout / stderr
    window.__notebookReceiveStreamOutput = function(cellIndex, streamName, streamText) {
        const cellDiv = container.querySelector('.cell[data-cell-index=""' + cellIndex + '""]');
        if (!cellDiv) return;
        const outputDiv = cellDiv.querySelector('.cell-output');
        if (!outputDiv) return;

        outputDiv.classList.add('has-output');
        if ((outputDiv.textContent || '').trim() === 'Running...') {
            outputDiv.innerHTML = '';
        }

        let streamSpan = outputDiv.querySelector('.stream-output-' + streamName);
        if (!streamSpan) {
            const entry = document.createElement('div');
            entry.className = 'output-entry';
            const cls = streamName === 'stderr' ? 'output-stderr' : 'output-stdout';
            entry.innerHTML = '<span class=""' + cls + ' stream-output-' + streamName + '""></span>';
            outputDiv.appendChild(entry);
            streamSpan = entry.querySelector('.stream-output-' + streamName);
        }

        let currentText = streamSpan.getAttribute('data-raw-text') || '';
        currentText += streamText;
        streamSpan.setAttribute('data-raw-text', currentText);

        if (window.parseTqdmText && (currentText.includes('%|') || currentText.includes('% |'))) {
            const parsed = window.parseTqdmText(currentText);
            if (parsed.lastTqdm) {
                let tqdmContainer = outputDiv.querySelector('.tqdm-entry');
                if (!tqdmContainer) {
                    tqdmContainer = document.createElement('div');
                    tqdmContainer.className = 'output-entry tqdm-entry';
                    outputDiv.appendChild(tqdmContainer);
                }
                tqdmContainer.innerHTML = window.buildTqdmWidgetHtml(parsed.lastTqdm);

                if (parsed.nonTqdmText) {
                    streamSpan.textContent = parsed.nonTqdmText;
                    const parent = streamSpan.closest('.output-entry');
                    if (parent && parent !== tqdmContainer) parent.style.display = '';
                } else {
                    streamSpan.textContent = '';
                    const parent = streamSpan.closest('.output-entry');
                    if (parent && parent !== tqdmContainer) parent.style.display = 'none';
                }
                return;
            }
        }

        function processCarriageReturns(str) {
            const cr = String.fromCharCode(13);
            const lines = str.split('\n');
            const processed = lines.map(line => {
                const parts = line.split(cr);
                return parts[parts.length - 1];
            });
            return processed.join('\n');
        }

        streamSpan.textContent = processCarriageReturns(currentText);
    };

    // Keyboard shortcuts
    container.addEventListener('keydown', (e) => {
        const input = e.target.closest('.cell-input-area, .markdown-editor, .raw-cell');
        if (!input) return;
        const cellDiv = input.closest('.cell');
        if (!cellDiv) return;

        if (nbAutocompleteState.isOpen) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                e.stopPropagation();
                moveNbAutocompleteIndex(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                e.stopPropagation();
                moveNbAutocompleteIndex(-1);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                e.stopPropagation();
                insertSelectedNbCandidate();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                hideNbAutocomplete();
                return;
            }
        }

        if (!e.isComposing && e.keyCode !== 229 &&
            !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey &&
            (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
            const direction = e.key === 'ArrowUp' ? -1 : 1;
            if (isCaretOnBoundaryLine(input, direction) &&
                moveEditorFocusToAdjacentCell(cellDiv, direction)) {
                e.preventDefault();
                e.stopPropagation();
                return;
            }
        }

        if (e.key === 'Backspace' && !e.ctrlKey && !e.altKey && !e.metaKey) {
            const codeEditor = input.closest('.code-editor');
            if (codeEditor) {
                const sel = window.getSelection();
                if (sel && sel.isCollapsed && sel.rangeCount > 0) {
                    const range = sel.getRangeAt(0);
                    let textBefore = '';
                    if (range.startContainer.nodeType === Node.TEXT_NODE) {
                        textBefore = range.startContainer.textContent.slice(0, range.startOffset);
                    } else if (range.startContainer.nodeType === Node.ELEMENT_NODE) {
                        const childNodes = Array.from(range.startContainer.childNodes);
                        for (let i = 0; i < range.startOffset; i++) {
                            textBefore += childNodes[i]?.textContent || '';
                        }
                    }

                    const lineStart = textBefore.lastIndexOf('\n') + 1;
                    const linePrefix = textBefore.slice(lineStart);

                    if (linePrefix.length > 0 && /^\s+$/.test(linePrefix)) {
                        e.preventDefault();
                        const deleteCount = linePrefix.length % 4 === 0 ? 4 : linePrefix.length % 4;
                        for (let i = 0; i < deleteCount; i++) {
                            document.execCommand('delete', false, null);
                        }
                        notifyModified();
                        return;
                    }
                }
            }
        } else if (e.key === 'Tab' && !e.shiftKey && !e.ctrlKey && !e.altKey) {
            const codeEditor = input.closest('.code-editor');
            if (codeEditor) {
                e.preventDefault();
                document.execCommand('insertText', false, '    ');
                notifyModified();
                return;
            }
        } else if (e.key === 'Tab' && e.shiftKey && !e.ctrlKey && !e.altKey) {
            const codeEditor = input.closest('.code-editor');
            if (codeEditor) {
                e.preventDefault();
                const sel = window.getSelection();
                if (sel && sel.isCollapsed && sel.rangeCount > 0) {
                    const range = sel.getRangeAt(0);
                    let textBefore = '';
                    if (range.startContainer.nodeType === Node.TEXT_NODE) {
                        textBefore = range.startContainer.textContent.slice(0, range.startOffset);
                    } else if (range.startContainer.nodeType === Node.ELEMENT_NODE) {
                        const childNodes = Array.from(range.startContainer.childNodes);
                        for (let i = 0; i < range.startOffset; i++) {
                            textBefore += childNodes[i]?.textContent || '';
                        }
                    }
                    const lineStart = textBefore.lastIndexOf('\n') + 1;
                    const linePrefix = textBefore.slice(lineStart);
                    if (linePrefix.length > 0 && /^\s+$/.test(linePrefix)) {
                        const unindentCount = Math.min(4, linePrefix.length);
                        for (let i = 0; i < unindentCount; i++) {
                            document.execCommand('delete', false, null);
                        }
                    }
                }
                notifyModified();
                return;
            }
        }

        if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.altKey) {
            const editor = input.closest('.markdown-editor');
            if (editor) {
                e.preventDefault();
                document.execCommand('insertLineBreak');
                notifyModified();
                return;
            }
        }

        if (e.shiftKey && e.key === 'Enter') {
            e.preventDefault();
            const type = getCellType(cellDiv);
            if (type === 'markdown') {
                renderMarkdownCell(cellDiv);
                let next = cellDiv.nextElementSibling;
                if (!next) {
                    next = createCell('code', '');
                    container.appendChild(next);
                    reindexCells();
                }
                if (getCellType(next) === 'markdown') {
                    editMarkdownCell(next);
                } else {
                    const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                    if (focusTarget) focusTarget.focus();
                }
            } else if (type === 'code') {
                runCell(cellDiv).then(() => {
                    let next = cellDiv.nextElementSibling;
                    if (!next) {
                        next = createCell('code', '');
                        container.appendChild(next);
                        reindexCells();
                    }
                    if (getCellType(next) === 'markdown') {
                        editMarkdownCell(next);
                    } else {
                        const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                        if (focusTarget) focusTarget.focus();
                    }
                });
            }
        }

        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            saveNotebook();
        }
    });
";
        }
    }
}
