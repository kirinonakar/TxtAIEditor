import { queueRender } from './editor-core.js';

// Printing Support
export function printDocument(fullText) {
    var printContainer = document.getElementById('print-container');
    if (!printContainer) {
        printContainer = document.createElement('div');
        printContainer.id = 'print-container';
        document.body.appendChild(printContainer);
    }
    printContainer.textContent = fullText;

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

    window.onafterprint = function () {
        editorHost.style.display = '';
        printContainer.style.cssText = 'display:none;';
        printContainer.onwheel = null;
        window.onafterprint = null;
        queueRender(true);
    };

    setTimeout(function () {
        window.print();
    }, 100);
}

export function bindPrintSupport() {
    window.printDocument = printDocument;
}
