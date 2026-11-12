import { renderPreviewLineRange } from './markdown-preview-renderer.js';

const note = document.getElementById('note');
const htmlNamespace = 'http://www.w3.org/1999/xhtml';
const allowedTags = new Set([
    'a', 'b', 'blockquote', 'br', 'code', 'del', 'div', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'hr', 'i', 'img', 'input', 'li', 'ol', 'p', 'pre', 's', 'span', 'strong', 'sub', 'sup',
    'table', 'tbody', 'td', 'th', 'thead', 'tr', 'u', 'ul'
]);
const discardContents = new Set(['script', 'style', 'iframe', 'object', 'embed', 'template']);

function isWebUrl(value) {
    try {
        const url = new URL(value);
        return url.protocol === 'http:' || url.protocol === 'https:';
    } catch {
        return false;
    }
}

function sanitize(fragment) {
    for (const element of fragment.querySelectorAll('*')) {
        const tag = element.localName;
        if (element.namespaceURI !== htmlNamespace || discardContents.has(tag)) {
            element.remove();
            continue;
        }
        if (!allowedTags.has(tag)) {
            element.replaceWith(...element.childNodes);
            continue;
        }

        const href = element.getAttribute('href');
        const src = element.getAttribute('src');
        const alt = element.getAttribute('alt');
        const alignment = element.style.textAlign;
        const isChecked = element.hasAttribute('checked');
        const isCheckbox = element.getAttribute('type') === 'checkbox';
        const start = element.getAttribute('start');
        for (const attribute of [...element.attributes]) {
            element.removeAttribute(attribute.name);
        }

        if (tag === 'a' && href && isWebUrl(href)) {
            element.href = href;
            element.target = '_blank';
            element.rel = 'noreferrer';
        }
        if (tag === 'img') {
            element.alt = alt ?? '';
            if (src && (isWebUrl(src) || /^data:image\/(?:png|jpeg|gif|webp|avif);base64,/i.test(src))) {
                element.src = src;
                element.referrerPolicy = 'no-referrer';
            } else {
                element.replaceWith(document.createTextNode(element.alt));
            }
        }
        if (tag === 'input') {
            if (isCheckbox) {
                element.type = 'checkbox';
                element.checked = isChecked;
                element.disabled = true;
            } else {
                element.remove();
            }
        }
        if (tag === 'ol' && /^\d+$/.test(start ?? '')) {
            element.setAttribute('start', start);
        }
        if ((tag === 'td' || tag === 'th') && (alignment === 'right' || alignment === 'center')) {
            element.className = `align-${alignment}`;
        }
    }
}

function render({ markdown, background, foreground }) {
    document.documentElement.style.setProperty('--note-background', background);
    document.documentElement.style.setProperty('--note-foreground', foreground);
    const lines = markdown.split(/\r\n|\r|\n/);
    const rendered = renderPreviewLineRange(1, lines.length, lines.length, index => lines[index - 1]);
    const template = document.createElement('template');
    template.innerHTML = rendered.results.map(line => line.html).join('\n');
    sanitize(template.content);
    note.replaceChildren(template.content);
}

window.chrome.webview.addEventListener('message', ({ data }) => {
    try {
        render(data);
    } catch {
        window.chrome.webview.postMessage('renderFailed');
    }
});
