namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookToolbarScript
    {
        internal static string GetScript()
        {
            return @"    // Toolbar buttons
    document.getElementById('btn-add-code').addEventListener('click', () => {
        const cell = createCell('code', '');
        container.appendChild(cell);
        reindexCells();
        cell.querySelector('.cell-input-area').focus();
        notifyModified();
    });

    document.getElementById('btn-add-markdown').addEventListener('click', () => {
        const cell = createCell('markdown', '');
        container.appendChild(cell);
        reindexCells();
        editMarkdownCell(cell);
        notifyModified();
    });

    const btnSave = document.getElementById('btn-save');
    if (btnSave) btnSave.addEventListener('click', saveNotebook);

    let isRunAllActive = false;

    const btnRunAll = document.getElementById('btn-run-all');
    if (btnRunAll) {
        btnRunAll.addEventListener('click', async () => {
            if (isRunAllActive) {
                try {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'stopExecution' }));
                } catch (ex) {}
                isRunAllActive = false;
                btnRunAll.textContent = 'Run All';
                btnRunAll.classList.remove('is-running');
                return;
            }
            isRunAllActive = true;
            btnRunAll.textContent = '■ Stop All';
            btnRunAll.classList.add('is-running');
            try {
                const cells = Array.from(container.querySelectorAll('.cell'));
                for (const cell of cells) {
                    if (!isRunAllActive) break;
                    if (getCellType(cell) === 'code') {
                        await runCell(cell);
                    }
                }
            } finally {
                isRunAllActive = false;
                btnRunAll.textContent = 'Run All';
                btnRunAll.classList.remove('is-running');
            }
        });
    }

    const btnClearOutputs = document.getElementById('btn-clear-outputs');
    if (btnClearOutputs) {
        btnClearOutputs.addEventListener('click', () => {
            container.querySelectorAll('.cell-output').forEach(outputDiv => {
                outputDiv.innerHTML = '';
                outputDiv.classList.remove('has-output');
            });
            notifyModified();
        });
    }

    function exportToPythonScript() {
        const cells = Array.from(container.querySelectorAll('.cell'));
        let pyScript = '# -*- coding: utf-8 -*-\n\n';
        for (let i = 0; i < cells.length; i++) {
            const cell = cells[i];
            const type = getCellType(cell);
            const source = getCellSource(cell);
            if (type === 'code') {
                pyScript += '# %% [code]\n';
                pyScript += source.trimEnd() + '\n\n';
            } else if (type === 'markdown') {
                pyScript += '# %% [markdown]\n';
                const lines = source.split('\n');
                for (let j = 0; j < lines.length; j++) {
                    pyScript += '# ' + lines[j] + '\n';
                }
                pyScript += '\n';
            } else {
                pyScript += '# %% [raw]\n';
                const lines = source.split('\n');
                for (let j = 0; j < lines.length; j++) {
                    pyScript += '# ' + lines[j] + '\n';
                }
                pyScript += '\n';
            }
        }
        try {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'exportPy',
                content: pyScript
            }));
        } catch (e) {}
    }

    const btnExportPy = document.getElementById('btn-export-py');
    if (btnExportPy) {
        btnExportPy.addEventListener('click', exportToPythonScript);
    }

    // Variables panel UI & handler logic
    window.__currentVariables = window.__currentVariables || [];

    function renderVariablesTable() {
        const tbody = document.getElementById('vars-table-body');
        if (!tbody) return;
        const filterInput = document.getElementById('vars-filter-input');
        const filterText = (filterInput ? filterInput.value || '' : '').toLowerCase().trim();

        const vars = (window.__currentVariables || []).filter(v => {
            if (!filterText) return true;
            return (v.name || '').toLowerCase().includes(filterText) || (v.type || '').toLowerCase().includes(filterText);
        });

        if (vars.length === 0) {
            const emptyMsg = filterText ? 'No matching variables.' : 'No active variables.';
            tbody.innerHTML = '<tr><td colspan=""4"" class=""vars-empty"">' + escapeHtml(emptyMsg) + '</td></tr>';
            return;
        }

        let html = '';
        for (let i = 0; i < vars.length; i++) {
            const v = vars[i];
            html += '<tr>' +
                '<td><strong>' + escapeHtml(v.name || '') + '</strong></td>' +
                '<td><code>' + escapeHtml(v.type || '') + '</code></td>' +
                '<td>' + escapeHtml(v.size || '-') + '</td>' +
                '<td title=""' + escapeHtml(v.value || '') + '"">' + escapeHtml(v.value || '') + '</td>' +
                '</tr>';
        }
        tbody.innerHTML = html;
    }

    const btnVars = document.getElementById('btn-variables');
    const varsPanel = document.getElementById('variables-panel');
    const btnRefreshVars = document.getElementById('btn-refresh-vars');
    const btnCloseVars = document.getElementById('btn-close-vars');
    const varsFilterInput = document.getElementById('vars-filter-input');

    if (btnVars && varsPanel) {
        btnVars.addEventListener('click', (e) => {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            if (varsPanel.style.display === 'none' || !varsPanel.style.display) {
                varsPanel.style.display = 'block';
                renderVariablesTable();
                try {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'getVariables' }));
                } catch (ex) {}
            } else {
                varsPanel.style.display = 'none';
            }
        });
    }

    if (btnRefreshVars) {
        btnRefreshVars.addEventListener('click', () => {
            try {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'getVariables' }));
            } catch (e) {}
        });
    }

    if (btnCloseVars && varsPanel) {
        btnCloseVars.addEventListener('click', () => {
            varsPanel.style.display = 'none';
        });
    }

    if (varsFilterInput) {
        varsFilterInput.addEventListener('input', () => {
            renderVariablesTable();
        });
    }

    // Receive variables from host
    window.__notebookReceiveVariables = function(vars) {
        if (Array.isArray(vars)) {
            window.__currentVariables = vars;
            renderVariablesTable();
        }
    };

    // Receive execution results from host
    window.__notebookReceiveResult = function(cellIndex, result, vars) {
        const resolve = (window.__pendingCellExecutions || {})[String(cellIndex)];
        if (resolve) {
            resolve(result);
            delete window.__pendingCellExecutions[String(cellIndex)];
        }
        if (Array.isArray(vars)) {
            window.__currentVariables = vars;
            renderVariablesTable();
        }
    };

    // Receive plot view update from host (3D re-render)
    window.__notebookReceivePlotUpdate = function(figId, html) {
        if (!figId || !html) return;
        const wrapper = document.querySelector('.mpl-interactive-wrapper[data-fig-id=""' + figId + '""]');
        if (wrapper && wrapper.__on3DUpdateReceived) {
            wrapper.__on3DUpdateReceived(html);
        }
    };

    // Receive save result from host
    window.__notebookSaveResult = function(success, message) {
        const btn = document.getElementById('btn-save');
        if (success) {
            isDirtyState = false;
            if (btn) {
                btn.textContent = 'Saved!';
                setTimeout(() => { btn.textContent = 'Save'; }, 1500);
            }
        } else {
            if (btn) {
                btn.textContent = 'Save Failed';
                setTimeout(() => { btn.textContent = 'Save'; }, 2000);
            }
        }
    };

    // Receive input request from Python kernel
    window.__notebookReceiveInputRequest = function(cellIndex, prompt) {
        const cellDiv = container.querySelector('.cell[data-cell-index=""' + cellIndex + '""]');
        if (!cellDiv) return;
        const outputDiv = cellDiv.querySelector('.cell-output');
        if (!outputDiv) return;

        outputDiv.classList.add('has-output');
        if ((outputDiv.textContent || '').trim() === 'Running...') {
            outputDiv.innerHTML = '';
        }

        const inputContainer = document.createElement('div');
        inputContainer.className = 'nb-input-request-box';
        inputContainer.innerHTML = 
            '<div class=""nb-input-prompt"">' + escapeHtml(prompt || 'Input:') + '</div>' +
            '<div class=""nb-input-controls"">' +
                '<input type=""text"" class=""nb-input-field"" placeholder=""Enter input..."" />' +
                '<button class=""nb-btn nb-input-submit"">Submit</button>' +
            '</div>';

        const field = inputContainer.querySelector('.nb-input-field');
        const submitBtn = inputContainer.querySelector('.nb-input-submit');

        function sendInput() {
            const val = field.value || '';
            inputContainer.remove();
            const valDiv = document.createElement('div');
            valDiv.className = 'output-entry';
            valDiv.innerHTML = '<span class=""output-stdout"">' + escapeHtml((prompt || '') + val + '\n') + '</span>';
            outputDiv.appendChild(valDiv);
            try {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'inputReply', value: val }));
            } catch (ex) {}
        }

        submitBtn.addEventListener('click', sendInput);
        field.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                sendInput();
            }
        });

        outputDiv.appendChild(inputContainer);
        setTimeout(() => { if (field) field.focus(); }, 50);
    };

    // Receive plot image saved result from host
    window.__notebookPlotSavedResult = function(success, fileName) {
        const btn = window.__lastSavePlotBtn;
        if (btn) {
            if (success) {
                const orig = btn.getAttribute('data-orig-text') || '💾 Save PNG';
                btn.textContent = 'Saved ' + (fileName || 'image') + '!';
                setTimeout(() => { btn.textContent = orig; }, 2500);
            } else {
                btn.textContent = 'Save Failed';
                setTimeout(() => { btn.textContent = '💾 Save PNG'; }, 2000);
            }
        }
    };";
        }
    }
}
