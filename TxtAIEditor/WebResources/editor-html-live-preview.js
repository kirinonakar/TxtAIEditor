import { htmlLivePreview, viewport } from './editor-dom.js';
import { post, requestMissingLines, state, viewportController } from './editor-core.js';
import { buildHtmlPreviewFrameBridgeScript } from './html-preview-frame-bridge.js';

function escapeAttribute(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

function buildHtmlPreviewDocument(html, baseHref) {
    const source = String(html ?? '');
    const base = baseHref ? `<base href="${escapeAttribute(baseHref)}">` : '';
    const injection = `${base}<meta name="viewport" content="width=device-width, initial-scale=1.0">${buildHtmlPreviewFrameBridgeScript()}`;

    if (/<head(?:\s[^>]*)?>/i.test(source)) {
        return source.replace(/<head(\s[^>]*)?>/i, match => `${match}${injection}`);
    }
    if (/<html(?:\s[^>]*)?>/i.test(source)) {
        return source.replace(/<html(\s[^>]*)?>/i, match => `${match}<head>${injection}</head>`);
    }

    const bodySource = source.replace(/^\s*<!doctype[^>]*>\s*/i, '');
    if (/<body(?:\s[^>]*)?>/i.test(bodySource)) {
        return `<!DOCTYPE html><html><head>${injection}</head>${bodySource}</html>`;
    }
    return `<!DOCTYPE html><html><head>${injection}</head><body>${bodySource}</body></html>`;
}

function isFullDocumentLivePreviewLanguage(language) {
    return ['html', 'svg'].includes(String(language || '').toLowerCase());
}

function isFullHtmlLivePreviewActive() {
    return state.inlineLivePreviewEnabled &&
        isFullDocumentLivePreviewLanguage(state.language);
}

function fullDocumentText() {
    const lines = new Array(state.lineCount);
    for (let line = 1; line <= state.lineCount; line++) {
        if (!state.cache.has(line)) {
            return null;
        }
        lines[line - 1] = state.cache.get(line) ?? '';
    }
    return lines.join('\n');
}

function createFullHtmlLivePreviewRenderer() {
    let active = false;
    let lastDocumentKey = '';

    window.addEventListener('message', event => {
        const message = event.data;
        if (event.source !== htmlLivePreview.contentWindow ||
            message?.source !== 'txtaieditor-html-preview' ||
            message?.type !== 'shortcut' ||
            !message.name ||
            String(message.name).toLowerCase() === 'f7') {
            return;
        }
        post({ type: 'shortcut', name: String(message.name) });
    });

    function deactivate() {
        if (!active) return;

        active = false;
        lastDocumentKey = '';
        document.body.classList.remove('full-html-live-preview-enabled');
        htmlLivePreview.hidden = true;
        htmlLivePreview.removeAttribute('srcdoc');
        viewportController.invalidateRenderRange();
        viewportController.clearRenderedRange();
    }

    function activate() {
        if (active) return;

        active = true;
        document.activeElement?.blur?.();
        window.getSelection()?.removeAllRanges();
        state.inlineLivePreviewSourceLine = null;
        state.inlineLivePreviewEditableBlock = null;
        state.editingLine = null;
        viewportController.invalidateRenderRange();
        viewport.innerHTML = '';
        document.body.classList.add('full-html-live-preview-enabled');
        htmlLivePreview.hidden = false;
    }

    function renderIfActive() {
        if (!isFullHtmlLivePreviewActive()) {
            deactivate();
            return false;
        }

        activate();
        viewportController.setRenderedRange(1, state.lineCount);

        const text = fullDocumentText();
        if (text === null) {
            requestMissingLines(1, state.lineCount);
            return true;
        }

        const documentKey = [
            state.hostDocumentId,
            state.hostDocumentVersion,
            state.documentVersion,
            state.cacheVersion,
            state.lineCount,
            state.livePreviewBaseHref || ''
        ].join(':');
        if (documentKey !== lastDocumentKey) {
            lastDocumentKey = documentKey;
            htmlLivePreview.srcdoc = buildHtmlPreviewDocument(
                text,
                state.livePreviewBaseHref || '');
        }
        return true;
    }

    return { renderIfActive };
}

export {
    buildHtmlPreviewDocument,
    createFullHtmlLivePreviewRenderer,
    isFullDocumentLivePreviewLanguage,
    isFullHtmlLivePreviewActive
};
