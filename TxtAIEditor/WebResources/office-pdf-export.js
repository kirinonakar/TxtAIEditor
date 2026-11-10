// Runs in the isolated export WebView, never in the visible Office viewer.
const hwpxSingleLineSelector = '.hwpx-saved-single-line:not(:has(.doc-image, .hwpx-group-shape, br))';
const hwpxCellLineSelector = `.hwpx-squeeze-cell > ${hwpxSingleLineSelector},
    .hwpx-landscape .hwpx-table td > ${hwpxSingleLineSelector}`;

export async function prepareOfficePdf() {
    const page = document.querySelector('main.page');
    const slide = document.querySelector('.slide');
    const workbook = typeof window.__txtAiEditorPrepareWorkbookPdf === 'function';
    if (!page && !slide && !workbook) {
        throw new Error(document.querySelector('.message')?.textContent || 'Could not render the Office document.');
    }
    page?.classList.add('office-pdf-export');
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
        .hwpx-section { display: contents; }
        .hwpx-split-table, .hwpx-split-table > tbody > tr, .hwpx-split-table > tbody > tr > td {
            height: auto !important;
        }
        ${hwpxCellLineSelector} {
            white-space: pre;
            line-height: var(--hwpx-saved-line-height, 1) !important;
        }
        .hwpx-squeeze-cell > .empty-paragraph, .hwpx-landscape .hwpx-table td > .empty-paragraph {
            min-height: var(--hwpx-saved-line-height, 1em);
        }
        .hwpx-landscape .hwpx-zero-advance {
            height: 0;
            min-height: 0;
            line-height: 1 !important;
            margin-block: 0 !important;
        }
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
    if (page) {
        const namedPages = readHwpxPrintLayouts(page);
        const hasSavedPagination = page.hasAttribute('data-saved-pagination') ||
            !!page.querySelector('.hwpx-split-table .doc-page-break');
        if (hasSavedPagination || namedPages.size) {
            fitSavedDocumentPages(page, layout, namedPages, hasSavedPagination);
        } else {
            const cover = page.querySelector('.hwpx-positioned-cover');
            if (cover) {
                cover.style.width = getPrintableArea(layout).width + 'px';
                window.fitHwpxCover?.();
            }
            fitHwpxLines(page, hwpxCellLineSelector);
        }
        layout.preferCssPageSize = namedPages.size > 0;
    }
    return layout;
}

function readHwpxPrintLayouts(page) {
    const layouts = new Map();
    for (const section of page.querySelectorAll('.hwpx-section')) {
        const key = section.dataset.hwpxPrintLayout;
        if (!key || layouts.has(key)) continue;
        const [width, height, top, right, bottom, left, header] = key.split(' ').map(Number.parseFloat);
        if ([width, height, top, right, bottom, left, header].some(value => !Number.isFinite(value) || value < 0) ||
            width <= left + right || height <= top + bottom) continue;
        const name = 'hwpx' + layouts.size;
        layouts.set(key, {
            name, header,
            rule: `@page ${name} { size: ${width}pt ${height}pt; margin: ${top}pt ${right}pt ${bottom}pt ${left}pt; }`,
            layout: { width: width / 72, height: height / 72, top: top / 72,
                right: right / 72, bottom: bottom / 72, left: left / 72 }
        });
    }
    if (layouts.size === 1) {
        const [{ layout }] = layouts.values();
        if (layout.width <= layout.height) layouts.clear();
    }
    return layouts;
}

function getPrintableArea(layout) {
    // WebView2 rounds paper and margins separately when paginating. Allow a
    // few layout pixels in height so a table edge cannot spill onto a blank page.
    return {
        width: Math.floor((layout.width - layout.left - layout.right) * 96) - 1,
        height: Math.floor((layout.height - layout.top - layout.bottom) * 96) - 8
    };
}

function fitSavedDocumentPages(page, layout, namedPages, hasSavedPagination) {
    const separators = page.querySelectorAll('.doc-page-break');
    if (!separators.length && !namedPages.size) return;
    if (namedPages.size) {
        const pageRules = document.createElement('style');
        pageRules.textContent = [...namedPages.values()].map(entry => entry.rule).join('\n');
        document.head.appendChild(pageRules);
    }

    const pages = document.createDocumentFragment();
    const printableAreas = new Map();
    const range = document.createRange();
    const appendPage = () => {
        const section = document.createElement('section');
        section.className = 'office-pdf-document-page';
        section.append(range.extractContents());
        const source = section.querySelector('.hwpx-section');
        const named = namedPages.get(source?.dataset.hwpxPrintLayout);
        const area = getPrintableArea(named?.layout || layout);
        if (named) {
            section.style.setProperty('page', named.name);
            section.style.setProperty('--hwpx-cover-origin', named.header + 'pt');
            if (named.layout.width > named.layout.height) section.classList.add('hwpx-landscape');
        }
        if (area.width > 0 && area.height > 0) {
            section.style.width = area.width + 'px';
            printableAreas.set(section, area);
        }
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
    window.fitHwpxCover?.();
    fitHwpxLines(page, hwpxCellLineSelector);
    if (!hasSavedPagination) return;

    const minScale = 0.65;
    for (const [section, { width, height }] of printableAreas) {
        // Reflow at the full printable width while scaling both glyph axes equally.
        const fitsAtScale = scale => {
            section.style.width = (width / scale) + 'px';
            section.style.zoom = scale === 1 ? '' : String(scale);
            const contentHeight = Math.max(section.scrollHeight * scale, section.getBoundingClientRect().height);
            return section.scrollWidth * scale <= width + 1 && contentHeight <= height;
        };
        if (fitsAtScale(1)) continue;
        if (!fitsAtScale(minScale)) {
            // Large intervals can contain uncached breaks. Let them flow across pages.
            section.style.width = width + 'px';
            section.style.removeProperty('zoom');
            continue;
        }

        let lower = minScale;
        let upper = 1;
        for (let attempt = 0; attempt < 5; attempt++) {
            const scale = (lower + upper) / 2;
            if (fitsAtScale(scale)) lower = scale;
            else upper = scale;
        }
        fitsAtScale(lower);
    }
}

function fitHwpxLines(root, selector) {
    const range = document.createRange();
    for (const paragraph of root.querySelectorAll(selector)) {
        range.selectNodeContents(paragraph);
        const width = paragraph.getBoundingClientRect().width;
        const textWidth = range.getBoundingClientRect().width;
        if (width > 1 && textWidth > width) {
            const scale = (width - 1) / textWidth;
            paragraph.style.width = (paragraph.clientWidth / scale) + 'px';
            paragraph.style.zoom = String(scale);
        }
    }
}
