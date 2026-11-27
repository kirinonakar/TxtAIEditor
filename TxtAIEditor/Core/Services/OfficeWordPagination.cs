namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWordPagination
    {
        internal const string Script = """
(() => {
    const page = document.querySelector('main.page[data-word-pagination]');
    if (!page) return;
    const templates = [...document.querySelectorAll('template[data-doc-page-part]')];

    let pagination;
    window.prepareWordPagination = () => pagination ??= paginate();
    window.prepareWordPagination().catch(error => console.error(error));

    async function paginate() {
        await document.fonts.ready;
        await loadImages(page);
        const probe = createPagePartProbe();
        if (probe) {
            await loadImages(probe);
            await document.fonts.ready;
        }
        const zoom = page.style.zoom;
        page.style.zoom = '1';
        try {
            const layout = readPageLayout();
            const pageParts = measurePageParts(probe);
            probe?.remove();
            if (pageParts.size && !page.hasAttribute('data-word-single-page')) {
                page.style.setProperty('--word-table-right-margin', layout.right + 'px');
            }
            const bodyHeight = index => {
                const { top, bottom } = pageMargins(layout, pageParts, index);
                // Allow for rounding at the edge of an exported PDF page.
                return Math.floor(layout.height - top - bottom) - 8;
            };
            liftManualBreaks();
            const fitted = bodyHeight(0) > 0 && fitSinglePage(bodyHeight(0), pageMargins(layout, pageParts, 0));
            if (!fitted) insertAutomaticBreaks(bodyHeight, pageParts.size > 0);
            placePages(layout, pageParts, fitted);
        } finally {
            probe?.remove();
            page.style.zoom = zoom;
        }
    }

    function fitSinglePage(pageHeight, margins) {
        if (!page.hasAttribute('data-word-single-page') || page.querySelector('.doc-page-break') ||
            !page.querySelector('template.doc-positioned-drawing')) return false;
        const blocks = [...page.children];
        const width = page.clientWidth - margins.left - margins.right;
        if (width <= 0 || blocks.length === 0) return false;

        const group = document.createElement('div');
        group.className = 'doc-fitted-page';
        group.style.width = width + 'px';
        group.append(...blocks);
        page.append(group);
        placeDrawings(blocks, margins);

        const origin = group.getBoundingClientRect().top;
        let height = Math.max(1, group.scrollHeight);
        for (const drawing of group.querySelectorAll('.doc-positioned-shape')) {
            height = Math.max(height, Math.ceil(drawing.getBoundingClientRect().bottom - origin));
        }
        const scale = Math.min(1, Math.floor(pageHeight / height * 10000) / 10000);
        if (scale < 0.65) {
            // The stored statistics can be stale. Allow normal pagination when
            // honoring them would make the document hard to read.
            page.append(...blocks);
            group.remove();
            return false;
        }
        // Keep line wrapping at the original width and scale the text and
        // floating drawings together. Reserve the full area the drawings use.
        group.style.minHeight = height + 'px';
        if (scale < 1) group.style.zoom = String(scale);
        return true;
    }

    function loadImages(root) {
        return Promise.all([...root.querySelectorAll('img')].map(image => image.decode().catch(() => {})));
    }

    function createPagePartProbe() {
        if (!templates.length) return null;
        const probe = document.createElement('div');
        const style = getComputedStyle(page);
        const width = parseFloat(style.width) - parseFloat(style.borderLeftWidth) - parseFloat(style.borderRightWidth)
            - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        probe.style.cssText = `position:absolute;visibility:hidden;width:${Math.max(1, width)}px`;
        for (const template of templates) probe.append(template.content.cloneNode(true));
        page.append(probe);
        return probe;
    }

    function measurePageParts(probe) {
        const parts = new Map();
        if (!probe) return parts;
        for (const [index, template] of templates.entries()) {
            const element = probe.children[index];
            if (element.tagName === 'FOOTER') preserveFooterSpaces(element);
            alignPagePartTabs(element);
            parts.set(template.dataset.docPagePart, {
                element,
                offset: Number(template.dataset.wordPartOffset) / 15,
                height: element.getBoundingClientRect().height,
                fixedMargin: template.hasAttribute('data-word-fixed-margin')
            });
        }
        return parts;
    }

    function preserveFooterSpaces(footer) {
        const isSpaceRun = run => run?.tagName === 'SPAN' && !run.children.length && /^ +$/.test(run.textContent);
        for (const paragraph of footer.querySelectorAll('.doc-paragraph')) {
            const runs = [...paragraph.children];
            for (let first = 0; first < runs.length; first++) {
                if (!isSpaceRun(runs[first])) continue;
                let end = first;
                let count = 0;
                while (isSpaceRun(runs[end])) count += runs[end++].textContent.length;
                if (count >= 4 && first > 0 && end < runs.length) {
                    // Keep the advance of space-aligned footer columns across adjacent formatted runs.
                    for (const run of runs.slice(first, end)) {
                        const advance = run.classList.contains('doc-word-east-asian-space') ? 0.5 : 1 / 3;
                        run.classList.add('doc-word-space');
                        run.style.minWidth = run.textContent.length * advance + 'em';
                    }
                }
                first = end - 1;
            }
        }
    }

    function alignPagePartTabs(part) {
        const range = document.createRange();
        for (const paragraph of part.querySelectorAll('[data-word-tab-stops]')) {
            const stops = paragraph.dataset.wordTabStops.split(' ').map(stop => {
                const [position, alignment] = stop.split(':');
                return { position: Number(position) / 15, alignment };
            });
            const style = getComputedStyle(paragraph);
            const margin = parseFloat(style.marginLeft);
            const defaultStop = style.tabSize.endsWith('px') ? parseFloat(style.tabSize) : 48;
            const origin = paragraph.getBoundingClientRect().left - margin;
            const whiteSpace = paragraph.style.whiteSpace;
            paragraph.style.whiteSpace = 'pre';
            const markers = [...paragraph.querySelectorAll('.doc-word-tab, br')];
            for (const [index, marker] of markers.entries()) {
                if (!marker.classList.contains('doc-word-tab')) continue;
                const position = marker.getBoundingClientRect().left - origin;
                const stop = stops.find(candidate => candidate.position > position + 0.1);
                const target = stop?.position ?? (Math.floor((position + 0.1) / defaultStop) + 1) * defaultStop;
                let width = target - position;
                if (stop?.alignment === 'center' || stop?.alignment === 'right') {
                    range.selectNodeContents(paragraph);
                    range.setStartAfter(marker);
                    if (markers[index + 1]) range.setEndBefore(markers[index + 1]);
                    const followingWidth = range.getBoundingClientRect().width;
                    width -= stop.alignment === 'center' ? followingWidth / 2 : followingWidth;
                }
                marker.style.width = Math.max(0, width) + 'px';
            }
            paragraph.style.whiteSpace = whiteSpace;
        }
    }

    function findPagePart(parts, kind, index) {
        const variant = index === 0 ? 'first' : index % 2 ? 'even' : 'default';
        return parts.get(kind + '-' + variant) ?? parts.get(kind + '-default');
    }

    function pageMargins(layout, parts, index) {
        const header = findPagePart(parts, 'header', index);
        const footer = findPagePart(parts, 'footer', index);
        const reserve = (margin, part) => part && !part.fixedMargin ? Math.max(margin, part.offset + part.height) : margin;
        return { ...layout, header, footer, top: reserve(layout.top, header), bottom: reserve(layout.bottom, footer) };
    }

    function placePages(layout, parts, fitted) {
        const blocks = [...page.children];
        let start = 0;
        let pageIndex = 0;
        for (let end = 0; end <= blocks.length; end++) {
            const separator = blocks[end];
            if (separator && !separator.classList.contains('doc-page-break')) continue;
            const content = blocks.slice(start, end);
            const margins = pageMargins(layout, parts, pageIndex);
            if (parts.size) {
                const frame = document.createElement('section');
                frame.className = 'doc-word-page';
                frame.style.padding = [margins.top, margins.right, margins.bottom, margins.left]
                    .map(value => value + 'px').join(' ');
                page.insertBefore(frame, content[0] ?? separator ?? null);
                if (margins.header) frame.append(positionPagePart(margins.header, 'top', margins));
                frame.append(...content);
                if (margins.footer) frame.append(positionPagePart(margins.footer, 'bottom', margins));
            }
            if (!fitted) placeDrawings(content, margins);
            start = end + 1;
            pageIndex++;
        }
        if (parts.size) page.setAttribute('data-word-page-frames', '');
    }

    function positionPagePart(part, edge, margins) {
        const element = part.element.cloneNode(true);
        element.style[edge] = part.offset + 'px';
        element.style.left = margins.left + 'px';
        element.style.right = margins.right + 'px';
        return element;
    }

    function placeDrawings(content, margins) {
        const anchors = content.flatMap(block => [...block.querySelectorAll('template.doc-positioned-drawing')]);
        if (!anchors.length) return;
        const layer = document.createElement('div');
        layer.className = 'doc-drawing-layer';
        content[0].before(layer);
        const origin = layer.getBoundingClientRect().top;
        for (const anchor of anchors) {
            const shape = anchor.content.firstElementChild.cloneNode(true);
            const paragraph = anchor.closest('.doc-paragraph') ?? anchor.parentElement;
            if (anchor.dataset.horizontal === 'page') {
                shape.style.left = `calc(${shape.style.left} - ${margins.left}px)`;
            }
            if (anchor.dataset.vertical === 'page') {
                shape.style.top = `calc(${shape.style.top} - ${margins.top}px)`;
            } else if (anchor.dataset.vertical === 'paragraph') {
                const offset = paragraph.getBoundingClientRect().top - origin;
                shape.style.top = `calc(${shape.style.top} + ${offset}px)`;
            }
            layer.append(shape);
        }
    }

    function readPageLayout() {
        const style = getComputedStyle(page);
        const ruler = document.createElement('div');
        ruler.style.cssText = 'position:absolute;visibility:hidden;width:0;height:var(--document-page-height)';
        page.append(ruler);
        const height = ruler.getBoundingClientRect().height;
        ruler.remove();
        return { height, top: parseFloat(style.paddingTop), right: parseFloat(style.paddingRight),
            bottom: parseFloat(style.paddingBottom), left: parseFloat(style.paddingLeft) };
    }

    function liftManualBreaks() {
        for (const separator of page.querySelectorAll(':scope > p > .doc-page-break')) {
            const paragraph = separator.parentElement;
            const continuation = paragraph.cloneNode(false);
            while (separator.nextSibling) continuation.append(separator.nextSibling);
            const marker = paragraph.firstElementChild;
            if (marker?.classList.contains('word-list-marker') && continuation.hasChildNodes()) {
                if (separator.previousSibling === marker) continuation.prepend(marker);
                else continuation.style.textIndent = '0';
            }
            paragraph.after(separator);
            if (continuation.hasChildNodes()) separator.after(continuation);
            if (!paragraph.hasChildNodes()) paragraph.remove();
        }
    }

    function insertAutomaticBreaks(bodyHeight, splitRows) {
        let pageIndex = 0;
        let pageStart = null;
        for (let block = page.firstElementChild; block; block = block.nextElementSibling) {
            if (block.classList.contains('doc-page-break')) {
                pageStart = null;
                pageIndex++;
                continue;
            }

            const bounds = block.getBoundingClientRect();
            const firstOnPage = pageStart === null;
            pageStart ??= bounds.top;
            const height = bodyHeight(pageIndex);
            const limit = pageStart + height;
            if (height <= 0 || bounds.bottom <= limit) continue;
            const tail = splitBlockAt(block, limit, splitRows, firstOnPage);
            const separator = document.createElement('span');
            separator.className = 'doc-page-break';
            separator.setAttribute('role', 'separator');
            if (tail) {
                block.after(separator, tail);
            } else if (!firstOnPage) {
                block.before(separator);
                block = separator;
                pageStart = null;
                pageIndex++;
            }
        }
    }

    function splitBlockAt(block, limit, splitRows, firstOnPage = false) {
        const body = block.querySelector(':scope > table.doc-table > tbody');
        if (!body?.rows.length) {
            return splitRows && block.matches('p.doc-paragraph') ? splitParagraphAt(block, limit) : null;
        }

        const rows = body.rows;
        for (let first = 0; first < rows.length;) {
            let last = first;
            // Rows covered by one vertically merged cell share a page.
            for (let row = first; row <= last; row++) {
                for (const cell of rows[row].cells) {
                    last = Math.max(last, Math.min(rows.length - 1, row + cell.rowSpan - 1));
                }
            }
            const bottom = rows[last].getBoundingClientRect().bottom;
            if (bottom > limit) {
                const row = rows[first];
                const tail = splitRows && first === last ? splitRowAt(row, limit) : null;
                if (tail) return takeTableTail(tail);
                if (first > 0) return takeTableTail(row);
                if (!firstOnPage) return null;
            }
            first = last + 1;
        }
        return null;
    }

    function splitRowAt(row, limit) {
        if (row.hasAttribute('data-word-keep-row') || row.cells.length !== 1) return null;
        const rowTop = row.getBoundingClientRect().top;
        if (rowTop >= limit) return null;
        const cell = row.cells[0];
        const style = getComputedStyle(cell);
        const cellLimit = limit - parseFloat(style.paddingBottom) - parseFloat(style.borderBottomWidth);
        const blocks = [...cell.children];
        const boundary = blocks.findIndex(block => block.getBoundingClientRect().bottom > cellLimit);
        if (boundary < 0) return null;
        const block = blocks[boundary];
        const tail = block.getBoundingClientRect().top < cellLimit ? splitBlockAt(block, cellLimit, true) : null;
        if (!tail && boundary === 0) return null;

        const continuation = row.cloneNode(false);
        const continuationCell = cell.cloneNode(false);
        continuation.append(continuationCell);
        if (tail) continuationCell.append(tail);
        continuationCell.append(...blocks.slice(boundary + (tail ? 1 : 0)));
        if (row.style.height) {
            const minimum = row.style.height;
            const available = limit - rowTop;
            row.style.height = `min(${minimum}, ${available}px)`;
            continuation.style.height = `max(0px, ${minimum} - ${available}px)`;
        }
        row.after(continuation);
        return continuation;
    }

    function splitParagraphAt(paragraph, limit) {
        if (paragraph.hasAttribute('data-word-keep-lines') || paragraph.querySelector('br, img, template') ||
            paragraph.getBoundingClientRect().top >= limit) return null;
        const range = document.createRange();
        range.selectNodeContents(paragraph);
        const finalLineInset = paragraph.getBoundingClientRect().bottom - range.getBoundingClientRect().bottom;
        const textLimit = limit - Math.max(0, finalLineInset);
        const walker = document.createTreeWalker(paragraph, NodeFilter.SHOW_TEXT);
        let hasPrefix = false;
        for (let text = walker.nextNode(); text; text = walker.nextNode()) {
            if (!text.length) continue;
            range.setStart(text, 0);
            range.setEnd(text, text.length);
            if (range.getBoundingClientRect().bottom <= textLimit) {
                hasPrefix ||= /\S/.test(text.data) && !text.parentElement.closest('.word-list-marker');
                continue;
            }

            // Find the first character on a line that will not fit above the footer.
            let lower = 0;
            let upper = text.length;
            while (upper - lower > 1) {
                const middle = (lower + upper) >>> 1;
                range.setEnd(text, middle);
                if (range.getBoundingClientRect().bottom <= textLimit) lower = middle;
                else upper = middle;
            }
            if (!hasPrefix && !/\S/.test(text.data.slice(0, lower))) return null;
            range.selectNodeContents(paragraph);
            range.setStart(text, lower);
            const continuation = paragraph.cloneNode(false);
            continuation.append(range.extractContents());
            continuation.style.marginTop = '0';
            continuation.style.textIndent = '0';
            paragraph.style.marginBottom = '0';
            if (getComputedStyle(paragraph).textAlign === 'justify') paragraph.style.textAlignLast = 'justify';
            return continuation;
        }
        return null;
    }

    function takeTableTail(row) {
        const body = row.parentElement;
        const table = body.parentElement;
        const wrapper = table.parentElement;
        const continuation = wrapper.cloneNode(false);
        const continuationTable = table.cloneNode(false);
        const continuationBody = body.cloneNode(false);
        for (const group of table.querySelectorAll(':scope > colgroup')) {
            continuationTable.append(group.cloneNode(true));
        }
        while (row.nextSibling) continuationBody.append(row.nextSibling);
        continuationBody.prepend(row);
        continuationTable.append(continuationBody);
        continuation.append(continuationTable);
        return continuation;
    }
})();
""";
    }
}
