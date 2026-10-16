import { imeController, post, queueRender, state } from './editor-core.js';
import { isFullHtmlLivePreviewActive } from './editor-html-live-preview.js';
const { renderMermaidBlocks, renderPreviewLineAt } = await import(
    `./markdown-preview-renderer.js?v=${encodeURIComponent(window.__TxtAIEditorVersion || Date.now())}`);

let printing = false;
let waitingForComposition = false;

function buildLivePrintHtml(fullText) {
    const lines = String(fullText ?? '').replace(/\r\n?/g, '\n').split('\n');
    const skipRef = { val: 0 };
    const options = {
        mode: 'markdown',
        baseHref: state.livePreviewBaseHref || '',
        localResourceVersion: state.livePreviewLocalResourceVersion || '0',
        tabSize: state.tabSize || 4,
        rangeEnd: lines.length,
        requireClosedFence: true
    };
    const html = [];
    for (let line = 1; line <= lines.length; line++) {
        const rendered = renderPreviewLineAt(line, lines.length, ln => lines[ln - 1], options, skipRef);
        if (rendered.skipped) continue;
        html.push(`<div class="live-preview-row print-preview-row">${rendered.html || '<br>'}</div>`);
    }
    return html.join('');
}

async function waitForPrintImages(root) {
    await Promise.all([...root.querySelectorAll('img')].map(image => {
        image.loading = 'eager';
        if (image.complete) return Promise.resolve();
        return new Promise(resolve => {
            const finish = () => {
                clearTimeout(timeout);
                image.removeEventListener('load', finish);
                image.removeEventListener('error', finish);
                resolve();
            };
            const timeout = setTimeout(finish, 5000);
            image.addEventListener('load', finish, { once: true });
            image.addEventListener('error', finish, { once: true });
        });
    }));
}

// Printing Support
export async function printDocument(fullText, ensurePreviewDependencies = async () => {}) {
    if (printing) return;
    if (imeController.isComposing) {
        if (!waitingForComposition) {
            waitingForComposition = true;
            document.addEventListener('compositionend', () => {
                waitingForComposition = false;
                // Ask the host again after the composed text reaches its session.
                setTimeout(() => post({ type: 'shortcut', name: 'print' }), 0);
            }, { once: true });
        }
        return;
    }
    if (isFullHtmlLivePreviewActive()) {
        document.getElementById('html-live-preview')?.contentWindow?.postMessage({
            source: 'txtaieditor-host', action: 'print'
        }, '*');
        return;
    }

    printing = true;
    const livePreview = state.inlineLivePreviewEnabled;
    try {
        if (livePreview) await ensurePreviewDependencies();
    } catch (error) {
        printing = false;
        throw error;
    }
    if (imeController.isComposing) {
        printing = false;
        return printDocument(fullText, ensurePreviewDependencies);
    }
    var printContainer = document.getElementById('print-container');
    if (!printContainer) {
        printContainer = document.createElement('div');
        printContainer.id = 'print-container';
        document.body.appendChild(printContainer);
    }
    printContainer.classList.toggle('live-preview-print', livePreview);
    if (livePreview) {
        printContainer.innerHTML = buildLivePrintHtml(fullText);
    } else {
        printContainer.textContent = fullText;
    }

    var editorHost = document.getElementById('editor-host');
    var currentBg = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim() || '#fff';
    var currentFg = getComputedStyle(document.documentElement).getPropertyValue('--fg').trim() || '#000';

    var editorFontSize = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--font-size').trim()) || 13;
    var printFontSize = Math.max(16, editorFontSize * 1.2);

    printContainer.style.cssText = 'display:block; font-family: ' + getComputedStyle(document.documentElement).getPropertyValue('--font-family').trim() + '; font-size: ' + printFontSize + 'px; white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-all; padding: 20px; color: ' + currentFg + '; background: ' + currentBg + '; margin: 0; position: absolute; inset: 0; z-index: 1000; overflow: auto;';
    editorHost.style.display = 'none';

    var baseFontSize = printFontSize;
    var currentZoom = 1.0;
    printContainer.style.fontSize = baseFontSize + 'px';

    printContainer.onwheel = function (e) {
        if (e.ctrlKey) {
            e.preventDefault();
            if (e.deltaY < 0) {
                currentZoom = Math.min(3.0, currentZoom + 0.1);
            } else {
                currentZoom = Math.max(0.5, currentZoom - 0.1);
            }
            printContainer.style.fontSize = (baseFontSize * currentZoom) + 'px';
        }
    };

    const cleanup = function () {
        editorHost.style.display = '';
        printContainer.style.cssText = 'display:none;';
        printContainer.replaceChildren();
        printContainer.onwheel = null;
        printing = false;
        window.removeEventListener('afterprint', cleanup);
        queueRender(true);
    };
    window.addEventListener('afterprint', cleanup, { once: true });

    try {
        if (livePreview) {
            await renderMermaidBlocks(printContainer);
            await waitForPrintImages(printContainer);
            await document.fonts?.ready;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
        window.print();
    } catch (error) {
        cleanup();
        throw error;
    }
}

export function bindPrintSupport(ensurePreviewDependencies) {
    window.printDocument = fullText => printDocument(fullText, ensurePreviewDependencies);
}
