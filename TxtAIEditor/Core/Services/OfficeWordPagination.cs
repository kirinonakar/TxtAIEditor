namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWordPagination
    {
        internal const string Script = """
(() => {
    const page = document.querySelector('main.page[data-word-pagination]');
    if (!page) return;
    const pageParts = new Map([...document.querySelectorAll('template[data-doc-page-part]')]
        .map(template => [template.dataset.docPagePart, template]));

    let pagination;
    window.prepareWordPagination = () => pagination ??= paginate();
    window.prepareWordPagination().catch(error => console.error(error));

    async function paginate() {
        await document.fonts.ready;
        await loadImages(page);
        const pagePartHeight = await measurePageParts();
        const zoom = page.style.zoom;
        page.style.zoom = '1';
        try {
            const pageHeight = readContentHeight();
            liftManualBreaks();
            if (pageHeight > 0 && fitSinglePage(pageHeight)) return;
            const height = pageHeight - pagePartHeight;
            if (height <= 0) return;
            const boundaries = measureBoundaries(height);
            for (const boundary of boundaries) {
                const separator = document.createElement('span');
                separator.className = 'doc-page-break';
                separator.setAttribute('role', 'separator');
                if (boundary.tagName === 'TR') splitTableBefore(boundary, separator);
                else boundary.before(separator);
            }
            placePagePartsAndDrawings();
        } finally {
            page.style.zoom = zoom;
        }
    }

    function fitSinglePage(pageHeight) {
        if (!page.hasAttribute('data-word-single-page') || page.querySelector('.doc-page-break')) return false;
        const blocks = [...page.children];
        const style = getComputedStyle(page);
        const width = page.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        if (width <= 0 || blocks.length === 0) return false;

        const group = document.createElement('div');
        group.className = 'doc-fitted-page';
        group.style.width = width + 'px';
        group.append(...blocks);
        page.append(group);
        placePagePartsAndDrawings(group);

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

    async function measurePageParts() {
        if (!pageParts.size) return 0;
        const probe = document.createElement('div');
        const style = getComputedStyle(page);
        const width = page.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        probe.style.cssText = `position:absolute;visibility:hidden;width:${Math.max(1, width)}px`;
        page.append(probe);
        try {
            const parts = [];
            for (const [name, template] of pageParts) {
                const part = template.content.firstElementChild.cloneNode(true);
                probe.append(part);
                parts.push([name, part]);
            }
            await loadImages(probe);
            await document.fonts.ready;
            let header = 0;
            let footer = 0;
            for (const [name, part] of parts) {
                if (name.startsWith('header-')) header = Math.max(header, part.offsetHeight);
                else footer = Math.max(footer, part.offsetHeight);
            }
            return header + footer;
        } finally {
            probe.remove();
        }
    }

    function findPagePart(kind, index) {
        const variant = index === 0 ? 'first' : index % 2 ? 'even' : 'default';
        return pageParts.get(kind + '-' + variant) ?? pageParts.get(kind + '-default');
    }

    function placePagePartsAndDrawings(container = page) {
        const blocks = [...container.children];
        let start = 0;
        let pageIndex = 0;
        for (let end = 0; end <= blocks.length; end++) {
            const separator = blocks[end];
            if (separator && !separator.classList.contains('doc-page-break')) continue;
            const content = blocks.slice(start, end);
            const header = findPagePart('header', pageIndex);
            const footer = findPagePart('footer', pageIndex);
            const first = content[0] ?? separator;
            if (header) container.insertBefore(header.content.cloneNode(true), first ?? null);
            if (footer) container.insertBefore(footer.content.cloneNode(true), separator ?? null);
            placeDrawings(content);
            start = end + 1;
            pageIndex++;
        }
    }

    function placeDrawings(content) {
        const anchors = content.flatMap(block => [...block.querySelectorAll('template.doc-positioned-drawing')]);
        if (!anchors.length) return;
        const layer = document.createElement('div');
        layer.className = 'doc-drawing-layer';
        content[0].before(layer);
        const style = getComputedStyle(page);
        const origin = layer.getBoundingClientRect().top;
        for (const anchor of anchors) {
            const shape = anchor.content.firstElementChild.cloneNode(true);
            const paragraph = anchor.closest('.doc-paragraph') ?? anchor.parentElement;
            if (anchor.dataset.horizontal === 'page') {
                shape.style.left = `calc(${shape.style.left} - ${style.paddingLeft})`;
            }
            if (anchor.dataset.vertical === 'page') {
                shape.style.top = `calc(${shape.style.top} - ${style.paddingTop})`;
            } else if (anchor.dataset.vertical === 'paragraph') {
                const offset = paragraph.getBoundingClientRect().top - origin;
                shape.style.top = `calc(${shape.style.top} + ${offset}px)`;
            }
            layer.append(shape);
        }
    }

    function readContentHeight() {
        const style = getComputedStyle(page);
        const ruler = document.createElement('div');
        ruler.style.cssText = 'position:absolute;visibility:hidden;width:0;height:var(--document-page-height)';
        page.append(ruler);
        // Reserve the same space as the PDF layout for rounding at the page edge.
        const height = Math.floor(ruler.getBoundingClientRect().height -
            parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)) - 8;
        ruler.remove();
        return height;
    }

    function liftManualBreaks() {
        for (const separator of page.querySelectorAll(':scope > p > .doc-page-break')) {
            const paragraph = separator.parentElement;
            const continuation = paragraph.cloneNode(false);
            while (separator.nextSibling) continuation.append(separator.nextSibling);
            paragraph.after(separator);
            if (continuation.hasChildNodes()) separator.after(continuation);
            if (!paragraph.hasChildNodes()) paragraph.remove();
        }
    }

    function measureBoundaries(height) {
        const boundaries = [];
        let pageStart = null;
        const consider = (element, top, bottom) => {
            if (pageStart === null) {
                pageStart = top;
            } else if (top > pageStart && bottom - pageStart > height) {
                boundaries.push(element);
                pageStart = top;
            }
        };

        for (const block of page.children) {
            if (block.classList.contains('doc-page-break')) {
                pageStart = null;
                continue;
            }
            const bounds = block.getBoundingClientRect();
            const body = block.querySelector(':scope > table.doc-table > tbody');
            if (!body || !body.rows.length) {
                consider(block, bounds.top, bounds.bottom);
                continue;
            }

            // Keep vertically merged cells together at automatic boundaries.
            const rows = body.rows;
            for (let first = 0; first < rows.length;) {
                let last = first;
                for (let row = first; row <= last; row++) {
                    for (const cell of rows[row].cells) {
                        last = Math.max(last, Math.min(rows.length - 1, row + Math.max(1, cell.rowSpan) - 1));
                    }
                }
                consider(first === 0 ? block : rows[first],
                    first === 0 ? bounds.top : rows[first].getBoundingClientRect().top,
                    last === rows.length - 1 ? bounds.bottom : rows[last].getBoundingClientRect().bottom);
                first = last + 1;
            }
        }
        return boundaries;
    }

    function splitTableBefore(row, separator) {
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
        wrapper.after(separator, continuation);
    }
})();
""";
    }
}
