namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookCellInteractionScript
    {
        internal static string GetScript()
        {
            return @"    let runningCells = new Set();

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
                '<button class=""cell-btn cell-add-above"" title=""Insert Cell Above"">+ Above</button>' +
                '<button class=""cell-btn cell-add-below"" title=""Insert Cell Below"">+ Below</button>' +
                '<button class=""cell-btn cell-delete"" title=""Delete"">✕</button>' +
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
                '<button class=""cell-btn cell-add-above"" title=""Insert Cell Above"">+ Above</button>' +
                '<button class=""cell-btn cell-add-below"" title=""Insert Cell Below"">+ Below</button>' +
                '<button class=""cell-btn cell-delete"" title=""Delete"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>' +
                '<div class=""cell-output""></div>' +
                '</div>';
        }
        return div;
    }

    let lastActiveMarkdownCell = null;
    let lastActiveMarkdownRange = null;
    let composingMarkdownEditor = null;
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
        const editor = e.target.closest('.markdown-editor');
        if (editor) {
            composingMarkdownEditor = editor;
        }
    });

    container.addEventListener('compositionend', (e) => {
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
            cellDiv.remove();
            reindexCells();
            notifyModified();
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

    function splitCellAtCursor(cellDiv) {
        if (!cellDiv) return;
        const type = getCellType(cellDiv);
        const fullSource = getCellSource(cellDiv);
        let head = fullSource;
        let tail = '';

        const sel = window.getSelection();
        const editor = cellDiv.querySelector('.cell-input-area, .markdown-editor');
        if (sel && sel.rangeCount > 0 && editor && editor.contains(sel.anchorNode)) {
            const range = sel.getRangeAt(0);
            const preRange = range.cloneRange();
            preRange.selectNodeContents(editor);
            preRange.setEnd(range.startContainer, range.startOffset);
            const caretPos = preRange.toString().length;
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
            if (ed) ed.innerHTML = '<pre>' + escapeHtml(head) + '</pre>';
            renderMarkdownCell(cellDiv);
        } else {
            const ed = cellDiv.querySelector('.cell-input-area');
            if (ed) ed.innerHTML = '<pre>' + highlightPythonCode(head) + '</pre>';
        }
        cellDiv.setAttribute('data-source', head);

        const newCell = createCell(type, tail);
        const next = cellDiv.nextElementSibling;
        if (next) container.insertBefore(newCell, next);
        else container.appendChild(newCell);
        reindexCells();
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

    function showContextMenu(x, y, cellDiv) {
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

        menu.innerHTML = 
            '<div class=""nb-context-menu-item"" data-action=""add-above"">➕ Insert Cell Above</div>' +
            '<div class=""nb-context-menu-item"" data-action=""add-below"">➕ Insert Cell Below</div>' +
            '<div class=""nb-context-menu-divider""></div>' +
            '<div class=""nb-context-menu-item ' + (cellDiv ? '' : 'disabled') + '"" data-action=""cut"">✂️ Cut Cell</div>' +
            '<div class=""nb-context-menu-item ' + (cellDiv ? '' : 'disabled') + '"" data-action=""copy"">📋 Copy Cell</div>' +
            '<div class=""nb-context-menu-item ' + (clipboardCell ? '' : 'disabled') + '"" data-action=""paste-above"">📑 Paste Cell Above</div>' +
            '<div class=""nb-context-menu-item ' + (clipboardCell ? '' : 'disabled') + '"" data-action=""paste-below"">📑 Paste Cell Below</div>' +
            '<div class=""nb-context-menu-divider""></div>' +
            '<div class=""nb-context-menu-item ' + (cellDiv ? '' : 'disabled') + '"" data-action=""split"">✂️| Split Cell</div>' +
            '<div class=""nb-context-menu-item ' + (hasPrev ? '' : 'disabled') + '"" data-action=""merge-above"">⬆️ Merge Cell Above</div>' +
            '<div class=""nb-context-menu-item ' + (hasNext ? '' : 'disabled') + '"" data-action=""merge-below"">⬇️ Merge Cell Below</div>' +
            '<div class=""nb-context-menu-divider""></div>' +
            '<div class=""nb-context-menu-item ' + (hasOutput ? '' : 'disabled') + '"" data-action=""clear-output"">🧹 Clear Cell Output</div>';

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
            handleContextMenuAction(action, cellDiv);
        };
    }

    function hideContextMenu() {
        const menu = document.getElementById('nb-context-menu');
        if (menu) menu.style.display = 'none';
    }

    function handleContextMenuAction(action, cellDiv) {
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
                const editor = newCell.querySelector('.cell-input-area');
                if (editor) editor.focus();
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
                const editor = newCell.querySelector('.cell-input-area');
                if (editor) editor.focus();
                notifyModified();
                break;
            }
            case 'cut': {
                if (!cellDiv) return;
                clipboardCell = { type: getCellType(cellDiv), source: getCellSource(cellDiv) };
                cellDiv.remove();
                reindexCells();
                notifyModified();
                break;
            }
            case 'copy': {
                if (!cellDiv) return;
                clipboardCell = { type: getCellType(cellDiv), source: getCellSource(cellDiv) };
                break;
            }
            case 'paste-above': {
                if (!clipboardCell) return;
                const newCell = createCell(clipboardCell.type, clipboardCell.source);
                if (cellDiv) container.insertBefore(newCell, cellDiv);
                else container.appendChild(newCell);
                reindexCells();
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
                notifyModified();
                break;
            }
            case 'split': {
                if (!cellDiv) return;
                splitCellAtCursor(cellDiv);
                break;
            }
            case 'merge-above': {
                if (!cellDiv) return;
                const prev = cellDiv.previousElementSibling;
                if (prev && prev.classList.contains('cell')) mergeCells(prev, cellDiv);
                break;
            }
            case 'merge-below': {
                if (!cellDiv) return;
                const next = cellDiv.nextElementSibling;
                if (next && next.classList.contains('cell')) mergeCells(cellDiv, next);
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
        }
    }

    document.addEventListener('click', hideContextMenu);
    document.addEventListener('scroll', hideContextMenu, true);
    document.addEventListener('contextmenu', (e) => {
        if (e.target.closest('.mpl-viewport, .mpl-toolbar')) return;
        const cellDiv = e.target.closest('.cell');
        e.preventDefault();
        showContextMenu(e.clientX, e.clientY, cellDiv);
    });

    // Keyboard shortcuts
    container.addEventListener('keydown', (e) => {
        const input = e.target.closest('.cell-input-area, .markdown-editor, .raw-cell');
        if (!input) return;
        const cellDiv = input.closest('.cell');
        if (!cellDiv) return;

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
                const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                if (focusTarget) focusTarget.focus();
            } else if (type === 'code') {
                runCell(cellDiv).then(() => {
                    let next = cellDiv.nextElementSibling;
                    if (!next) {
                        next = createCell('code', '');
                        container.appendChild(next);
                        reindexCells();
                    }
                    const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                    if (focusTarget) focusTarget.focus();
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
