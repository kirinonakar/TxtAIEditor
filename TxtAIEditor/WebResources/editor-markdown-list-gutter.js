import { viewport } from './editor-dom.js';

// A canonical nested list owns one outer source row. Paint its source numbers
// in that row's gutter, outside editable content and without adding row height.
export function syncMarkdownListLineNumbers() {
    if (!viewport.querySelector('[data-markdown-list-line]')) return;
    for (const row of viewport.querySelectorAll('.line-row.live-preview-row')) {
        if (!row.querySelector('[data-markdown-list-line]')) continue;
        const items = row.querySelectorAll('[data-markdown-list-line], [data-markdown-preview-line="code"]');
        if (!items.length) continue;
        const gutter = row.querySelector(':scope > .line-number');
        if (!gutter) continue;
        const rowRect = row.getBoundingClientRect();
        const height = Number.parseFloat(getComputedStyle(items[0]).lineHeight) || 22;
        // Finish geometry reads before changing any gutter labels, so large
        // lists do not alternate layout measurement and DOM writes per item.
        const positions = [...items].map(item => ({
            line: Number(item.dataset.markdownListLine || item.dataset.line),
            top: Math.max(0, item.getBoundingClientRect().top - rowRect.top)
        }));
        gutter.classList.add('md-preview-list-gutter');
        for (const node of [...gutter.childNodes]) {
            if (node.nodeType === Node.TEXT_NODE) node.remove();
        }
        const existing = new Map([...gutter.querySelectorAll('.md-preview-list-line-number')]
            .map(label => [Number(label.dataset.line), label]));
        for (const { line, top } of positions) {
            if (!line) continue;
            let label = existing.get(line);
            existing.delete(line);
            if (!label) {
                label = document.createElement('span');
                label.className = 'line-number md-preview-list-line-number';
                label.dataset.line = String(line);
                label.textContent = String(line);
                gutter.appendChild(label);
            }
            label.style.top = `${top}px`;
            label.style.height = `${height}px`;
            label.style.lineHeight = `${height}px`;
        }
        for (const label of existing.values()) label.remove();
    }
}
