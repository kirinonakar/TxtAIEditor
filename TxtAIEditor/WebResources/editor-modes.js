export class EditorMode {
    constructor(id) {
        this.id = id;
    }

    effectiveLineCount({ sourceLineCount }) {
        return Math.max(1, Number(sourceLineCount || 1));
    }

    renderOverscan({ defaultOverscan }) {
        return Math.max(0, Number(defaultOverscan || 0));
    }

    prefetchAhead({ defaultPrefetchAhead }) {
        return Math.max(0, Number(defaultPrefetchAhead || 0));
    }

    usesFullDocumentRender() {
        return false;
    }

    allowsCompressedScroll() {
        return true;
    }

    shouldTrimDocumentCache() {
        return false;
    }

    selectionInfo({ textSelectionInfo }) {
        return textSelectionInfo();
    }

    selectedText({ textSelectedText }) {
        return textSelectedText();
    }
}

export class TextEditorMode extends EditorMode {
    constructor() {
        super('text');
    }

    usesFullDocumentRender({ inlineLivePreviewEnabled, lineCount, fullDocumentLineLimit }) {
        return !inlineLivePreviewEnabled && lineCount <= fullDocumentLineLimit;
    }
}

export class HexEditorMode extends EditorMode {
    constructor({ renderOverscan, prefetchAhead }) {
        super('hex');
        this.hexRenderOverscan = renderOverscan;
        this.hexPrefetchAhead = prefetchAhead;
    }

    renderOverscan() {
        return this.hexRenderOverscan;
    }

    prefetchAhead() {
        return this.hexPrefetchAhead;
    }

    shouldTrimDocumentCache() {
        return true;
    }

    selectionInfo({ hexSelectionInfo }) {
        return hexSelectionInfo();
    }

    selectedText({ hexSelectedText }) {
        return hexSelectedText();
    }
}

export class CsvTableMode extends EditorMode {
    constructor() {
        super('csv');
    }

    effectiveLineCount({ sourceLineCount, csvVirtualLineCount }) {
        const virtualLineCount = Number(csvVirtualLineCount || 0);
        return virtualLineCount > 0
            ? Math.max(1, virtualLineCount)
            : Math.max(1, Number(sourceLineCount || 1));
    }

    allowsCompressedScroll() {
        return false;
    }
}

export class EditorModeCoordinator {
    constructor({ textMode, hexMode, csvMode }) {
        this.textMode = textMode;
        this.hexMode = hexMode;
        this.csvMode = csvMode;
    }

    resolve({ language, csvTableEnabled }) {
        if (csvTableEnabled) return this.csvMode;
        if (language === 'hex') return this.hexMode;
        return this.textMode;
    }
}
