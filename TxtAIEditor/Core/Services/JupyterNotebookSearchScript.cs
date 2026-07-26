namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookSearchScript
    {
        internal static string GetScript()
        {
            return @"    const notebookFindBar = document.getElementById('notebook-find-bar');
    const notebookFindInput = document.getElementById('notebook-find-input');
    const notebookFindCount = document.getElementById('notebook-find-count');
    const notebookFindPrevious = document.getElementById('btn-find-previous');
    const notebookFindNext = document.getElementById('btn-find-next');
    const notebookFindClose = document.getElementById('btn-find-close');
    const notebookFindButton = document.getElementById('btn-find');
    let notebookFindQuery = '';
    let notebookFindMatchCount = 0;
    let notebookFindMatchIndex = 0;
    let notebookFindComposing = false;
    let notebookFindMatches = [];

    function clearNotebookFindHighlights() {
        container.querySelectorAll('.cell.is-find-current').forEach(cell => {
            cell.classList.remove('is-find-current');
        });
        if (window.CSS && CSS.highlights) {
            CSS.highlights.delete('notebook-search-results');
            CSS.highlights.delete('notebook-search-active');
        }
    }

    function collectNotebookMatches(query) {
        if (!query) return [];
        const needle = query.toLocaleLowerCase();
        const matches = [];
        const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, {
            acceptNode(node) {
                const parent = node.parentElement;
                if (!parent || !node.nodeValue || !node.nodeValue.trim()) {
                    return NodeFilter.FILTER_REJECT;
                }
                if (parent.closest('.cell-toolbar, .cell-drag-handle, #nb-context-menu') ||
                    parent.getClientRects().length === 0) {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        let node = walker.nextNode();
        while (node) {
            const text = (node.nodeValue || '').toLocaleLowerCase();
            let offset = 0;
            while (offset <= text.length - needle.length) {
                const found = text.indexOf(needle, offset);
                if (found < 0) break;
                const range = document.createRange();
                range.setStart(node, found);
                range.setEnd(node, found + needle.length);
                const cell = node.parentElement.closest('.cell');
                if (cell) matches.push({ range, cell });
                offset = found + Math.max(needle.length, 1);
            }
            node = walker.nextNode();
        }
        return matches;
    }

    function applyNotebookFindHighlights() {
        if (!window.CSS || !CSS.highlights || typeof Highlight !== 'function') return;
        CSS.highlights.delete('notebook-search-results');
        const highlight = new Highlight();
        notebookFindMatches.forEach(match => highlight.add(match.range));
        if (highlight.size > 0) {
            CSS.highlights.set('notebook-search-results', highlight);
        }
    }

    function updateNotebookFindCount() {
        if (!notebookFindCount) return;
        if (!notebookFindQuery) {
            notebookFindCount.textContent = '';
            return;
        }
        if (notebookFindMatchCount === 0) {
            notebookFindCount.textContent = notebookString('findNoMatches', 'No matches');
            return;
        }
        const format = notebookString('findMatchCount', '{0} of {1}');
        notebookFindCount.textContent = format
            .replace('{0}', String(notebookFindMatchIndex))
            .replace('{1}', String(notebookFindMatchCount));
    }

    function refreshNotebookFindMatches() {
        const query = notebookFindInput ? notebookFindInput.value : '';
        clearNotebookFindHighlights();
        notebookFindQuery = query;
        notebookFindMatchIndex = 0;
        notebookFindMatches = collectNotebookMatches(query);
        notebookFindMatchCount = notebookFindMatches.length;
        applyNotebookFindHighlights();
        updateNotebookFindCount();
    }

    function focusNotebookFindInputAtEnd() {
        if (!notebookFindInput || notebookFindBar.hidden) return;
        const end = notebookFindInput.value.length;
        notebookFindInput.focus({ preventScroll: true });
        notebookFindInput.setSelectionRange(end, end);
    }

    function findNotebookMatch(backwards) {
        const query = notebookFindInput ? notebookFindInput.value : '';
        if (!query) {
            refreshNotebookFindMatches();
            return false;
        }

        if (query !== notebookFindQuery ||
            notebookFindMatches.some(match => !match.range.startContainer.isConnected)) {
            refreshNotebookFindMatches();
        }

        if (notebookFindMatchCount === 0) {
            updateNotebookFindCount();
            focusNotebookFindInputAtEnd();
            return false;
        }

        if (backwards) {
            notebookFindMatchIndex = notebookFindMatchIndex <= 1
                ? notebookFindMatchCount
                : notebookFindMatchIndex - 1;
        } else {
            notebookFindMatchIndex = notebookFindMatchIndex >= notebookFindMatchCount
                ? 1
                : notebookFindMatchIndex + 1;
        }

        const activeMatch = notebookFindMatches[notebookFindMatchIndex - 1];
        container.querySelectorAll('.cell.is-find-current').forEach(cell => {
            cell.classList.remove('is-find-current');
        });
        activeMatch.cell.classList.add('is-find-current');
        if (window.__notebookSelectCell) {
            window.__notebookSelectCell(activeMatch.cell, false);
        }

        if (window.CSS && CSS.highlights && typeof Highlight === 'function') {
            CSS.highlights.set('notebook-search-active', new Highlight(activeMatch.range));
        }

        const rect = activeMatch.range.getBoundingClientRect();
        const targetTop = window.scrollY + rect.top - (window.innerHeight / 2) + (rect.height / 2);
        window.scrollTo({ top: Math.max(0, targetTop), behavior: 'smooth' });
        focusNotebookFindInputAtEnd();
        updateNotebookFindCount();
        return true;
    }

    function openNotebookFind() {
        if (!notebookFindBar || !notebookFindInput) return false;
        notebookFindBar.hidden = false;
        notebookFindInput.focus();
        notebookFindInput.select();
        refreshNotebookFindMatches();
        return true;
    }

    function closeNotebookFind() {
        if (!notebookFindBar) return;
        if (notebookFindInput) notebookFindInput.blur();
        notebookFindBar.hidden = true;
        clearNotebookFindHighlights();
        if (window.__notebookSelectedCell && window.__notebookSelectedCell.isConnected) {
            window.__notebookSelectCell(window.__notebookSelectedCell, true);
        }
    }

    if (notebookFindButton) {
        notebookFindButton.addEventListener('click', openNotebookFind);
    }
    if (notebookFindPrevious) {
        notebookFindPrevious.addEventListener('click', () => findNotebookMatch(true));
    }
    if (notebookFindNext) {
        notebookFindNext.addEventListener('click', () => findNotebookMatch(false));
    }
    if (notebookFindClose) {
        notebookFindClose.addEventListener('click', closeNotebookFind);
    }
    if (notebookFindInput) {
        notebookFindInput.addEventListener('compositionstart', () => {
            notebookFindComposing = true;
        });
        notebookFindInput.addEventListener('compositionend', () => {
            notebookFindComposing = false;
            refreshNotebookFindMatches();
            focusNotebookFindInputAtEnd();
        });
        notebookFindInput.addEventListener('input', () => {
            if (!notebookFindComposing) refreshNotebookFindMatches();
        });
        notebookFindInput.addEventListener('keydown', e => {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeNotebookFind();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                findNotebookMatch(e.shiftKey);
            }
        });
    }

    window.__txtAiEditorNotebookFind = {
        open: openNotebookFind,
        close: closeNotebookFind
    };
";
        }
    }
}
