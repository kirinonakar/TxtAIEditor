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

    function countNotebookMatches(query) {
        if (!query) return 0;
        const needle = query.toLocaleLowerCase();
        let count = 0;
        container.querySelectorAll('.cell').forEach(cell => {
            const output = cell.querySelector('.cell-output');
            const corpus = (getCellSource(cell) + '\n' + (output ? output.innerText || '' : '')).toLocaleLowerCase();
            let offset = 0;
            while (offset <= corpus.length - needle.length) {
                const found = corpus.indexOf(needle, offset);
                if (found < 0) break;
                count++;
                offset = found + Math.max(needle.length, 1);
            }
        });
        return count;
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

    function resetNotebookFindOrigin() {
        const selection = window.getSelection();
        const title = document.querySelector('.notebook-title');
        if (!selection || !title) return;
        const range = document.createRange();
        range.selectNodeContents(title);
        range.collapse(false);
        selection.removeAllRanges();
        selection.addRange(range);
    }

    function findNotebookMatch(backwards, resetOrigin) {
        const query = notebookFindInput ? notebookFindInput.value : '';
        if (!query) {
            notebookFindQuery = '';
            notebookFindMatchCount = 0;
            notebookFindMatchIndex = 0;
            updateNotebookFindCount();
            return false;
        }

        const changed = query !== notebookFindQuery;
        if (changed) {
            notebookFindQuery = query;
            notebookFindMatchCount = countNotebookMatches(query);
            notebookFindMatchIndex = 0;
        }

        if (resetOrigin || changed) {
            resetNotebookFindOrigin();
        }

        const selectionStart = notebookFindInput ? notebookFindInput.selectionStart : null;
        const selectionEnd = notebookFindInput ? notebookFindInput.selectionEnd : null;
        const found = window.find(query, false, !!backwards, true, false, false, false);
        if (notebookFindInput && !notebookFindBar.hidden) {
            notebookFindInput.focus({ preventScroll: true });
            if (selectionStart !== null && selectionEnd !== null) {
                notebookFindInput.setSelectionRange(selectionStart, selectionEnd);
            }
        }
        if (found && notebookFindMatchCount > 0) {
            if (backwards) {
                notebookFindMatchIndex = notebookFindMatchIndex <= 1
                    ? notebookFindMatchCount
                    : notebookFindMatchIndex - 1;
            } else {
                notebookFindMatchIndex = notebookFindMatchIndex >= notebookFindMatchCount
                    ? 1
                    : notebookFindMatchIndex + 1;
            }
        }
        updateNotebookFindCount();
        return found;
    }

    function openNotebookFind() {
        if (!notebookFindBar || !notebookFindInput) return false;
        notebookFindBar.hidden = false;
        notebookFindInput.focus();
        notebookFindInput.select();
        return true;
    }

    function closeNotebookFind() {
        if (!notebookFindBar) return;
        if (notebookFindInput) notebookFindInput.blur();
        notebookFindBar.hidden = true;
        if (window.__notebookSelectedCell && window.__notebookSelectedCell.isConnected) {
            window.__notebookSelectCell(window.__notebookSelectedCell, true);
        }
    }

    if (notebookFindButton) {
        notebookFindButton.addEventListener('click', openNotebookFind);
    }
    if (notebookFindPrevious) {
        notebookFindPrevious.addEventListener('click', () => findNotebookMatch(true, false));
    }
    if (notebookFindNext) {
        notebookFindNext.addEventListener('click', () => findNotebookMatch(false, false));
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
            findNotebookMatch(false, true);
        });
        notebookFindInput.addEventListener('input', () => {
            if (!notebookFindComposing) findNotebookMatch(false, true);
        });
        notebookFindInput.addEventListener('keydown', e => {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeNotebookFind();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                findNotebookMatch(e.shiftKey, false);
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
