function shortcutNameFromKeyboardEvent(event) {
    const key = String(event?.key || '').toLowerCase();
    if (key === 'f4' || key === 'f9' || key === 'f10' || key === 'f11' || key === 'f12') {
        return key;
    }

    if (event?.altKey && !event.ctrlKey && !event.metaKey && key === 'z') {
        return 'wordWrap';
    }

    const ctrl = !!(event?.ctrlKey || event?.metaKey);
    if (!ctrl) return '';

    if (key === '1') return 'toggleLeftPanel';
    if (key === '2') return 'toggleRightPanel';
    if (key === '3') return 'expandRightPanel';
    if (key === 'n') return 'newTab';
    if (key === 's') return event.shiftKey ? 'saveAs' : 'save';
    if (key === 'o') return 'open';
    if (key === 'w') return 'closeTab';
    if (key === 'p') return 'print';
    if (key === 'f') return event.shiftKey ? 'searchAll' : 'find';
    if (event.code === 'Backquote' || key === '`' || key === '~' || key === 'dead') return 'terminal';
    return '';
}

function htmlPreviewFrameBridge() {
    function createMemoryStorage() {
        const entries = new Map();
        return {
            get length() {
                return entries.size;
            },
            clear() {
                entries.clear();
            },
            getItem(key) {
                const normalizedKey = String(key);
                return entries.has(normalizedKey) ? entries.get(normalizedKey) : null;
            },
            key(index) {
                return [...entries.keys()][Number(index)] ?? null;
            },
            removeItem(key) {
                entries.delete(String(key));
            },
            setItem(key, value) {
                entries.set(String(key), String(value));
            }
        };
    }

    function installStorageFallback(storageName) {
        try {
            void window[storageName].length;
            return;
        } catch (error) { }

        try {
            Object.defineProperty(window, storageName, {
                configurable: true,
                enumerable: true,
                value: createMemoryStorage()
            });
        } catch (error) { }
    }

    installStorageFallback('localStorage');
    installStorageFallback('sessionStorage');

    function topOverlayOffset() {
        let offset = 0;
        const elements = document.body ? document.body.querySelectorAll('*') : [];
        for (const element of elements) {
            const style = getComputedStyle(element);
            if (style.position !== 'fixed' && style.position !== 'sticky') continue;

            const top = Number.parseFloat(style.top);
            if (!Number.isFinite(top) || top > 1) continue;

            const rect = element.getBoundingClientRect();
            if (rect.height <= 0 || rect.width <= 0 || rect.top > top + 1 || rect.bottom <= 0) continue;
            offset = Math.max(offset, rect.bottom);
        }
        return Math.max(0, offset) + (offset > 0 ? 8 : 0);
    }

    function scrollToFragment(href) {
        let fragment = href.slice(1);
        if (!fragment) {
            window.scrollTo({ top: 0, behavior: 'smooth' });
            return;
        }

        try {
            fragment = decodeURIComponent(fragment);
        } catch (error) { }

        const target = document.getElementById(fragment) || document.getElementsByName(fragment)[0];
        if (!target) return;

        const top = Math.max(0, window.scrollY + target.getBoundingClientRect().top - topOverlayOffset());
        window.scrollTo({ top, behavior: 'smooth' });
    }

    document.addEventListener('click', event => {
        if (event.defaultPrevented || event.button !== 0 ||
            event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        const anchor = event.target?.closest?.('a[href]');
        if (!anchor) return;

        const href = anchor.getAttribute('href') || '';
        if (!href.startsWith('#')) return;

        event.preventDefault();
        scrollToFragment(href);
    }, true);

    document.addEventListener('keydown', event => {
        const key = String(event.key || '').toLowerCase();
        let shortcut = '';
        if (key === 'f4' || key === 'f9' || key === 'f10' || key === 'f11' || key === 'f12') {
            shortcut = key;
        } else if (event.altKey && !event.ctrlKey && !event.metaKey && key === 'z') {
            shortcut = 'wordWrap';
        } else if (event.ctrlKey || event.metaKey) {
            if (key === '1') shortcut = 'toggleLeftPanel';
            else if (key === '2') shortcut = 'toggleRightPanel';
            else if (key === '3') shortcut = 'expandRightPanel';
            else if (key === 'n') shortcut = 'newTab';
            else if (key === 's') shortcut = event.shiftKey ? 'saveAs' : 'save';
            else if (key === 'o') shortcut = 'open';
            else if (key === 'w') shortcut = 'closeTab';
            else if (key === 'p') shortcut = 'print';
            else if (key === 'f') shortcut = event.shiftKey ? 'searchAll' : 'find';
            else if (event.code === 'Backquote' || key === '`' || key === '~' || key === 'dead') shortcut = 'terminal';
        }

        if (!shortcut) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        window.parent.postMessage({
            source: 'txtaieditor-html-preview',
            type: 'shortcut',
            name: shortcut
        }, '*');
    }, true);
}

function buildHtmlPreviewFrameBridgeScript() {
    return `<script>(${htmlPreviewFrameBridge.toString()})();<\/script>`;
}

export {
    buildHtmlPreviewFrameBridgeScript,
    shortcutNameFromKeyboardEvent
};
