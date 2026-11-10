// Runs in the isolated export WebView, never in the visible Office viewer.
export async function prepareOfficePdf() {
    const page = document.querySelector('main.page');
    const slide = document.querySelector('.slide');
    const workbook = typeof window.__txtAiEditorPrepareWorkbookPdf === 'function';
    if (!page && !slide && !workbook) {
        throw new Error(document.querySelector('.message')?.textContent || 'Could not render the Office document.');
    }
    const layout = { width: 210 / 25.4, height: 297 / 25.4, top: .5, right: .5, bottom: .5, left: .5 };
    if (page) {
        const style = getComputedStyle(page);
        layout.width = page.offsetWidth / 96;
        for (const side of ['top', 'right', 'bottom', 'left']) layout[side] = parseFloat(style.getPropertyValue(`padding-${side}`)) / 96;
        const height = getComputedStyle(document.documentElement).getPropertyValue('--document-page-height');
        if (height) {
            const ruler = document.createElement('div');
            ruler.style.height = height;
            document.body.appendChild(ruler);
            layout.height = ruler.getBoundingClientRect().height / 96;
            ruler.remove();
        }
    } else if (slide) {
        const canvas = slide.querySelector('.slide-canvas');
        layout.width = canvas.offsetWidth / 96;
        layout.height = canvas.offsetHeight / 96;
        layout.top = layout.right = layout.bottom = layout.left = 0;
    } else {
        [layout.width, layout.height] = [layout.height, layout.width];
        window.__txtAiEditorPrepareWorkbookPdf();
    }

    const printStyle = document.createElement('style');
    printStyle.textContent = `
        :root { color-scheme: light !important; --bg: #fff; --paper: #fff; --panel: #fff;
            --text: #111827; --muted: #667085; --line: #d8dee8; --line-strong: #b8c2d2;
            --header: #eef2f7; --table-head: #f1f4f8; --app-bg: #fff; }
        html, body { height: auto !important; min-height: 0 !important; overflow: visible !important;
            background: #fff !important; padding: 0 !important; margin: 0 !important; display: block !important; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        .toolbar, #txt-office-find-panel, .slide-number { display: none !important; }
        main.page { width: 100% !important; min-width: 0 !important; min-height: 0 !important;
            margin: 0 !important; padding: 0 !important; border: 0 !important; box-shadow: none !important; zoom: 1 !important; }
        .office-pdf-document-page { display: flow-root; min-height: 1px; }
        .office-pdf-document-page + .office-pdf-document-page { break-before: page; }
        .doc-table-wrap, .sheet-object-table-wrap, .sheet-objects { overflow: visible !important; }
        .deck { display: block; margin: 0; padding: 0; }
        .slide { width: ${layout.width * 96}px !important; height: ${layout.height * 96}px !important;
            margin: 0 !important; border: 0 !important; box-shadow: none !important; break-inside: avoid; break-after: page; }
        .slide:last-child { break-after: auto; }
        .slide-canvas { transform: none !important; }
        .office-pdf-sheet { break-before: page; }
        .office-pdf-sheet:first-child { break-before: auto; }
        .office-pdf-sheet h2 { margin: 0 0 12px; font-size: 18px; }
        .office-pdf-sheet table { width: 100%; table-layout: fixed; box-shadow: none; }
        .office-pdf-sheet th, .office-pdf-sheet td { min-width: 0 !important; max-width: none !important;
            position: static !important; white-space: pre-wrap !important; overflow-wrap: anywhere !important; }
        .office-pdf-sheet .row-header { width: 40px !important; }
        .office-pdf-sheet .xlsx-cell-text { position: static !important; width: auto !important;
            max-width: none !important; white-space: pre-wrap !important; overflow: visible !important; overflow-wrap: anywhere !important; }
        .office-pdf-sheet .selected-cell { outline: none !important; }
        .office-pdf-sheet .selected-cell:not([style*="background"]) { background: var(--panel); }
        .office-pdf-sheet thead { display: table-header-group; }
        .office-pdf-sheet tr { break-inside: avoid; }
        .sheet-object-chart, .sheet-object-image { max-width: 100%; }
        mark.txt-office-find-match { background: transparent; color: inherit; box-shadow: none; padding: 0; }
    `;
    document.head.appendChild(printStyle);
    document.querySelectorAll('.selected-cell, .active-cell, .selected-column-heading, .selected-row-heading')
        .forEach(element => element.classList.remove('selected-cell', 'active-cell', 'selected-column-heading', 'selected-row-heading'));
    document.querySelectorAll('.slide').forEach(element => window.fitSlide?.(element));
    await document.fonts.ready;
    if (page?.hasAttribute('data-saved-pagination')) {
        fitSavedDocumentPages(page, layout);
    }
    return layout;
}

function fitSavedDocumentPages(page, layout) {
    const separators = page.querySelectorAll('.doc-page-break');
    if (!separators.length) return;

    // Reserve a pixel for the rounding of the printable area.
    const width = Math.floor((layout.width - layout.left - layout.right) * 96) - 1;
    const height = Math.floor((layout.height - layout.top - layout.bottom) * 96) - 1;
    if (width <= 0 || height <= 0) return;

    const pages = document.createDocumentFragment();
    const range = document.createRange();
    const appendPage = () => {
        const section = document.createElement('section');
        section.className = 'office-pdf-document-page';
        section.style.width = width + 'px';
        section.append(range.extractContents());
        pages.append(section);
    };
    for (const separator of separators) {
        range.selectNodeContents(page);
        const parent = separator.parentElement;
        range.setEndBefore(parent.matches('p.doc-paragraph') && separator === parent.firstChild ? parent : separator);
        appendPage();
        separator.remove();
    }
    range.selectNodeContents(page);
    appendPage();
    page.replaceChildren(pages);

    // Large intervals can contain uncached breaks. Keep those readable and let them flow.
    const minScale = 0.65;
    for (const section of page.children) {
        let scale = 1;
        // Zoom can change wrapping after glyph advances are rounded.
        for (let attempt = 0; attempt < 3; attempt++) {
            const fit = Math.min(1, width / (section.scrollWidth * scale), height / (section.scrollHeight * scale));
            if (fit === 1) break;
            scale *= fit;
            if (scale < minScale) {
                section.style.removeProperty('zoom');
                break;
            }
            section.style.zoom = String(scale);
        }
    }
}
