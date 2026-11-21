function shortcutNameFromKeyboardEvent(event) {
    const key = String(event?.key || '').toLowerCase();
    if (key === 'f3' || key === 'f4' ||
        key === 'f9' || key === 'f10' || key === 'f11' || key === 'f12') {
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
    if (key === '4' && !event.altKey && !event.shiftKey) return 'toggleChatTab';
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

    // Find support. The preview document runs in a sandboxed iframe without
    // same-origin access, so the host posts the find query here and this script
    // paints every hit with the CSS Custom Highlight API.
    const findHighlight = 'txtaieditor-find-match';
    const activeFindHighlight = 'txtaieditor-find-active';
    let findRanges = [];
    let findActiveIndex = -1;
    let findQueryKey = '';

    function clearFindHighlights() {
        findRanges = [];
        findActiveIndex = -1;
        findQueryKey = '';
        if (window.CSS && CSS.highlights) {
            CSS.highlights.delete(findHighlight);
            CSS.highlights.delete(activeFindHighlight);
        }
    }

    function ensureFindStyles() {
        if (document.getElementById('txtaieditor-find-style')) return;
        const style = document.createElement('style');
        style.id = 'txtaieditor-find-style';
        style.textContent = '::highlight(' + findHighlight + '){background:#ffd43b;color:#212529;}' +
            '::highlight(' + activeFindHighlight + '){background:#fd7e14;color:#ffffff;}';
        (document.head || document.documentElement).appendChild(style);
    }

    function collectFindTextNodes() {
        const nodes = [];
        if (!document.body) return nodes;
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode(node) {
                if (!node.nodeValue) return NodeFilter.FILTER_REJECT;
                const parent = node.parentElement;
                if (!parent || parent.closest('script,style,noscript,template,title')) {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });
        let node = walker.nextNode();
        while (node) {
            nodes.push(node);
            node = walker.nextNode();
        }
        return nodes;
    }

    function buildFindRanges(query, matchCase, isRegex) {
        const ranges = [];
        let matcher;
        try {
            const pattern = isRegex ? query : query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            matcher = new RegExp(pattern, matchCase ? 'g' : 'gi');
        } catch (error) {
            return ranges;
        }

        // Matches are collected per text node: hits never cross element bounds,
        // which is acceptable for a rendered document preview.
        for (const node of collectFindTextNodes()) {
            const text = node.nodeValue;
            matcher.lastIndex = 0;
            let match = matcher.exec(text);
            while (match) {
                const length = match[0].length;
                if (length === 0) {
                    matcher.lastIndex += 1;
                } else {
                    const range = document.createRange();
                    range.setStart(node, match.index);
                    range.setEnd(node, match.index + length);
                    ranges.push(range);
                    if (ranges.length >= 5000) return ranges;
                }
                match = matcher.exec(text);
            }
        }
        return ranges;
    }

    function scrollActiveFindRangeIntoView() {
        const range = findRanges[findActiveIndex];
        if (!range || !range.startContainer.isConnected) return;
        const element = range.startContainer.parentElement;
        if (!element) return;
        element.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'auto' });
    }

    function applyFindState(data) {
        const query = String(data.query || '');
        if (!query) {
            clearFindHighlights();
            return;
        }
        if (!window.CSS || !CSS.highlights || typeof Highlight !== 'function') {
            return;
        }

        ensureFindStyles();
        const matchCase = !!data.matchCase;
        const isRegex = !!data.isRegex;
        const direction = Number(data.direction || 0);
        const requestedIndex = Number(data.activeIndex);
        const queryKey = [query, matchCase ? 1 : 0, isRegex ? 1 : 0].join('\u0000');

        if (queryKey !== findQueryKey) {
            findQueryKey = queryKey;
            findRanges = buildFindRanges(query, matchCase, isRegex);
            if (findRanges.length === 0) {
                findActiveIndex = -1;
            } else if (Number.isInteger(requestedIndex) && requestedIndex >= 0 &&
                requestedIndex < findRanges.length) {
                findActiveIndex = requestedIndex;
            } else {
                findActiveIndex = direction < 0 ? findRanges.length - 1 : 0;
            }
        } else if (findRanges.length > 0) {
            if (direction > 0) {
                findActiveIndex = (findActiveIndex + 1) % findRanges.length;
            } else if (direction < 0) {
                findActiveIndex = (findActiveIndex - 1 + findRanges.length) % findRanges.length;
            } else if (findActiveIndex < 0) {
                findActiveIndex = 0;
            }
        }

        CSS.highlights.set(findHighlight, new Highlight(...findRanges));
        if (findActiveIndex >= 0 && findActiveIndex < findRanges.length) {
            CSS.highlights.set(activeFindHighlight, new Highlight(findRanges[findActiveIndex]));
        } else {
            CSS.highlights.delete(activeFindHighlight);
        }
        scrollActiveFindRangeIntoView();

        try {
            window.parent.postMessage({
                source: 'txtaieditor-html-preview',
                type: 'previewFindState',
                query,
                count: findRanges.length,
                activeIndex: findActiveIndex
            }, '*');
        } catch (error) { }
    }

    window.addEventListener('message', event => {
        if (event.source !== window.parent) return;

        const data = event.data;
        if (!data || data.source !== 'txtaieditor-host') return;

        if (data.action === 'print') {
            window.print();
        } else if (data.action === 'previewFind') {
            applyFindState(data);
        } else if (data.action === 'previewFindClear') {
            clearFindHighlights();
        }
    });

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
        if (key === 'f7') {
            event.preventDefault();
            event.stopPropagation();
            event.stopImmediatePropagation();
            return;
        }

        let shortcut = '';
        if (key === 'f3' || key === 'f4' ||
            key === 'f9' || key === 'f10' || key === 'f11' || key === 'f12') {
            shortcut = key;
        } else if (event.altKey && !event.ctrlKey && !event.metaKey && key === 'z') {
            shortcut = 'wordWrap';
        } else if (event.ctrlKey || event.metaKey) {
            if (key === '1') shortcut = 'toggleLeftPanel';
            else if (key === '2') shortcut = 'toggleRightPanel';
            else if (key === '3') shortcut = 'expandRightPanel';
            else if (key === '4' && !event.altKey && !event.shiftKey) shortcut = 'toggleChatTab';
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
        if (event.repeat && shortcut === 'toggleChatTab') return;
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
