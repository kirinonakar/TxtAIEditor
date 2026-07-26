namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookBootstrapScript
    {
        internal static string GetScript()
        {
            return @"(function() {
    (function installNotebookShortcutBridge() {
        if (window.__txtAiEditorNotebookShortcutBridge) return;
        window.__txtAiEditorNotebookShortcutBridge = true;

        function post(name) {
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut', name });
                }
            } catch {}
        }

        function handleKeyDown(event) {
            const ctrl = !!(event.ctrlKey || event.metaKey);
            const alt = !!event.altKey;
            const shift = !!event.shiftKey;
            const key = String(event.key || '').toLowerCase();
            const code = String(event.code || '');

            let name = '';

            if (!ctrl && !alt) {
                if (key === 'f4' || code === 'F4') {
                    name = 'f4';
                } else if (key === 'f9' || code === 'F9') {
                    name = 'f9';
                } else if (key === 'f10' || code === 'F10') {
                    name = 'f10';
                } else if (key === 'f11' || code === 'F11') {
                    name = 'f11';
                } else if (key === 'f12' || code === 'F12') {
                    name = 'f12';
                }
            } else if (alt && !ctrl && !shift && (key === 'z' || code === 'KeyZ')) {
                name = 'wordWrap';
            } else if (ctrl && !alt) {
                if (key === '1' || code === 'Digit1' || code === 'Numpad1') {
                    name = 'toggleLeftPanel';
                } else if (key === '2' || code === 'Digit2' || code === 'Numpad2') {
                    name = 'toggleRightPanel';
                } else if (key === '3' || code === 'Digit3' || code === 'Numpad3') {
                    name = 'expandRightPanel';
                } else if (key === 'n' || code === 'KeyN') {
                    name = 'newTab';
                } else if (key === 's' || code === 'KeyS') {
                    name = shift ? 'saveAs' : 'save';
                } else if (key === 'o' || code === 'KeyO') {
                    name = 'open';
                } else if (key === 'w' || code === 'KeyW') {
                    name = 'closeTab';
                } else if (key === 'p' || code === 'KeyP') {
                    name = 'print';
                } else if (key === 'f' || code === 'KeyF') {
                    if (!shift && window.__txtAiEditorNotebookFind &&
                        window.__txtAiEditorNotebookFind.open()) {
                        event.preventDefault();
                        event.stopPropagation();
                        if (event.stopImmediatePropagation) {
                            event.stopImmediatePropagation();
                        }
                        return;
                    }
                    name = shift ? 'searchAll' : 'find';
                } else if (code === 'Backquote' || key === '`' || key === '~' || key === 'dead') {
                    name = 'terminal';
                }
            }

            if (!name) return;
            event.preventDefault();
            event.stopPropagation();
            if (event.stopImmediatePropagation) {
                event.stopImmediatePropagation();
            }
            post(name);
        }

        window.addEventListener('keydown', handleKeyDown, true);
        document.addEventListener('keydown', handleKeyDown, true);
    })();

    const container = document.getElementById('cells-container');
    const path = window.__notebookPath;
    let isDirtyState = false;

    function notifyModified() {
        if (!isDirtyState) {
            isDirtyState = true;
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'markDirty' }));
                }
            } catch (e) {}
        }
    }

    container.addEventListener('input', () => {
        notifyModified();
    });

    container.addEventListener('focusout', (e) => {
        const editor = e.target.closest('.cell-input-area.code-editor');
        if (!editor) return;
        const cellDiv = editor.closest('.cell');
        if (!cellDiv || getCellType(cellDiv) !== 'code') return;
        setTimeout(() => {
            applyCodeSyntaxHighlight(cellDiv);
        }, 50);
    });
";
        }
    }
}
