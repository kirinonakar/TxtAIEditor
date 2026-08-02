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
    #isEditable = false;
    #selection = null;
    #selectionAnchorOffset = null;
    #selectionPane = 'hex';
    #cursorOffset = 0;
    #pendingHighNibble = null;

    constructor({ renderOverscan, prefetchAhead }) {
        super('hex');
        this.hexRenderOverscan = renderOverscan;
        this.hexPrefetchAhead = prefetchAhead;
    }

    get isEditable() {
        return this.#isEditable;
    }

    get selection() {
        return this.#selection ? { ...this.#selection } : null;
    }

    get selectionAnchorOffset() {
        return this.#selectionAnchorOffset;
    }

    get selectionPane() {
        return this.#selectionPane;
    }

    get cursorOffset() {
        return this.#cursorOffset;
    }

    get pendingHighNibble() {
        return this.#pendingHighNibble ? { ...this.#pendingHighNibble } : null;
    }

    setEditable(value) {
        this.#isEditable = !!value;
    }

    setCursor(offset, { pane = null } = {}) {
        this.#cursorOffset = Math.max(0, Number(offset || 0));
        if (pane === 'hex' || pane === 'ascii') {
            this.#selectionPane = pane;
        }
        return this.#cursorOffset;
    }

    setSelectionAnchor(offset) {
        this.#selectionAnchorOffset = offset === null || offset === undefined
            ? null
            : Math.max(0, Number(offset || 0));
        return this.#selectionAnchorOffset;
    }

    setSelection(selection) {
        this.#selection = selection
            ? {
                startOffset: Math.max(0, Number(selection.startOffset || 0)),
                endOffset: Math.max(0, Number(selection.endOffset || 0))
            }
            : null;
    }

    setSelectionFromOffsets(anchorOffset, cursorOffset) {
        const anchor = Math.max(0, Number(anchorOffset || 0));
        const cursor = Math.max(0, Number(cursorOffset || 0));
        this.#selection = {
            startOffset: Math.min(anchor, cursor),
            endOffset: Math.max(anchor, cursor) + 1
        };
        return this.selection;
    }

    selectByte(offset, { pane = null, clearPendingHighNibble = false } = {}) {
        const safeOffset = this.setCursor(offset, { pane });
        this.setSelectionAnchor(safeOffset);
        this.#selection = { startOffset: safeOffset, endOffset: safeOffset + 1 };
        if (clearPendingHighNibble) {
            this.clearPendingHighNibble();
        }
        return this.selection;
    }

    clearSelection() {
        this.#selection = null;
    }

    resetSelection() {
        this.#selection = null;
        this.#selectionAnchorOffset = null;
        this.#cursorOffset = 0;
    }

    setPendingHighNibble(offset, value) {
        this.#pendingHighNibble = {
            offset: Math.max(0, Number(offset || 0)),
            value: String(value || '')
        };
    }

    clearPendingHighNibble() {
        this.#pendingHighNibble = null;
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
    #isEnabled = false;
    #virtualLineCount = 0;
    #tableVersion = 0;
    #columnCount = 0;
    #columnWidths = [];
    #cellComposing = false;
    #jsonNavigationPath = [];
    #jsonTableCache = null;
    #jsonKeyHeader = 'key';
    #jsonValueHeader = 'value';
    #breadcrumbRoot = 'root';
    #initialized = false;
    #selectedLine = 1;
    #selectedColumn = 0;
    #editMode = 'select';
    #selection = null;
    #selectedRows = [];
    #selectedColumns = [];
    #rowSelectionAnchor = null;
    #columnSelectionAnchor = null;
    #pendingFocus = null;

    constructor() {
        super('csv');
    }

    get isEnabled() {
        return this.#isEnabled;
    }

    set isEnabled(value) {
        this.#isEnabled = !!value;
    }

    get virtualLineCount() {
        return this.#virtualLineCount;
    }

    set virtualLineCount(value) {
        this.#virtualLineCount = Math.max(0, Number(value || 0));
    }

    get tableVersion() {
        return this.#tableVersion;
    }

    set tableVersion(value) {
        this.#tableVersion = Number(value || 0);
    }

    get columnCount() {
        return this.#columnCount;
    }

    set columnCount(value) {
        this.#columnCount = Math.max(0, Number(value || 0));
    }

    get columnWidths() {
        return [...this.#columnWidths];
    }

    get cellComposing() {
        return this.#cellComposing;
    }

    set cellComposing(value) {
        this.#cellComposing = !!value;
    }

    get jsonNavigationPath() {
        return [...this.#jsonNavigationPath];
    }

    get jsonNavigationKey() {
        return this.#jsonNavigationPath.join('.');
    }

    get jsonKeyHeader() {
        return this.#jsonKeyHeader || 'key';
    }

    get jsonValueHeader() {
        return this.#jsonValueHeader || 'value';
    }

    get breadcrumbRoot() {
        return this.#breadcrumbRoot || 'root';
    }

    ensureTableState({ minColumnCount = 0 } = {}) {
        this.#isEnabled = !!this.#isEnabled;
        this.#virtualLineCount = Math.max(0, Number(this.#virtualLineCount || 0));
        this.#tableVersion = Number(this.#tableVersion || 0);
        this.#columnCount = Math.max(
            Math.max(0, Number(minColumnCount || 0)),
            Number(this.#columnCount || 0));
        this.#cellComposing = !!this.#cellComposing;
    }

    bumpTableVersion() {
        this.#tableVersion++;
        return this.#tableVersion;
    }

    columnWidth(index, fallbackWidth = 0) {
        const safeIndex = Math.max(0, Number(index || 0));
        return Number(this.#columnWidths[safeIndex] || fallbackWidth || 0);
    }

    setColumnWidth(index, width) {
        const safeIndex = Math.max(0, Number(index || 0));
        this.#columnWidths[safeIndex] = Number(width || 0);
        return this.#columnWidths[safeIndex];
    }

    setJsonNavigationPath(path) {
        this.#jsonNavigationPath = Array.isArray(path) ? [...path] : [];
        this.invalidateJsonTableModel();
        return this.jsonNavigationPath;
    }

    resetJsonNavigation() {
        return this.setJsonNavigationPath([]);
    }

    trimJsonNavigation(index) {
        const lastIndex = Number(index);
        if (!Number.isInteger(lastIndex) || lastIndex < 0 || lastIndex >= this.#jsonNavigationPath.length) {
            return this.resetJsonNavigation();
        }
        return this.setJsonNavigationPath(this.#jsonNavigationPath.slice(0, lastIndex + 1));
    }

    readJsonTableCache(cacheKey) {
        if (!this.#jsonTableCache || this.#jsonTableCache.cacheKey !== cacheKey) {
            return { hit: false, model: null };
        }
        return { hit: true, model: this.#jsonTableCache.model };
    }

    cacheJsonTableModel(cacheKey, model) {
        this.#jsonTableCache = { cacheKey, model: model ?? null };
        return this.#jsonTableCache.model;
    }

    invalidateJsonTableModel() {
        this.#jsonTableCache = null;
    }

    setJsonKeyHeader(value) {
        this.#jsonKeyHeader = value;
        this.invalidateJsonTableModel();
        return this.jsonKeyHeader;
    }

    setJsonValueHeader(value) {
        this.#jsonValueHeader = value;
        this.invalidateJsonTableModel();
        return this.jsonValueHeader;
    }

    setBreadcrumbRoot(value) {
        this.#breadcrumbRoot = value;
        return this.breadcrumbRoot;
    }

    get selectedLine() {
        return this.#selectedLine;
    }

    set selectedLine(value) {
        this.#initialized = true;
        this.#selectedLine = Math.max(1, Number(value || 1));
    }

    get selectedColumn() {
        return this.#selectedColumn;
    }

    set selectedColumn(value) {
        this.#selectedColumn = Math.max(0, Number(value || 0));
    }

    get editMode() {
        return this.#editMode;
    }

    set editMode(value) {
        this.#editMode = value === 'edit' ? 'edit' : 'select';
    }

    get selection() {
        if (!this.#selection) return null;
        return {
            ...this.#selection,
            ...(Array.isArray(this.#selection.rows) ? { rows: [...this.#selection.rows] } : {}),
            ...(Array.isArray(this.#selection.columns) ? { columns: [...this.#selection.columns] } : {})
        };
    }

    set selection(value) {
        this.#selection = value
            ? {
                ...value,
                ...(Array.isArray(value.rows) ? { rows: [...value.rows] } : {}),
                ...(Array.isArray(value.columns) ? { columns: [...value.columns] } : {})
            }
            : null;
    }

    get selectedRows() {
        return [...this.#selectedRows];
    }

    set selectedRows(value) {
        this.#selectedRows = Array.isArray(value) ? [...value] : [];
    }

    get selectedColumns() {
        return [...this.#selectedColumns];
    }

    set selectedColumns(value) {
        this.#selectedColumns = Array.isArray(value) ? [...value] : [];
    }

    get rowSelectionAnchor() {
        return this.#rowSelectionAnchor;
    }

    set rowSelectionAnchor(value) {
        this.#rowSelectionAnchor = value === null || value === undefined
            ? null
            : Math.max(1, Number(value || 1));
    }

    get columnSelectionAnchor() {
        return this.#columnSelectionAnchor;
    }

    set columnSelectionAnchor(value) {
        this.#columnSelectionAnchor = value === null || value === undefined
            ? null
            : Math.max(0, Number(value || 0));
    }

    get pendingFocus() {
        return this.#pendingFocus ? { ...this.#pendingFocus } : null;
    }

    set pendingFocus(value) {
        this.#pendingFocus = value ? { ...value } : null;
    }

    ensureSelection(fallbackLine = 1) {
        if (!this.#initialized) {
            this.#selectedLine = Math.max(1, Number(fallbackLine || 1));
            this.#initialized = true;
        }
        this.#selectedLine = Math.max(1, Number(this.#selectedLine || 1));
        this.#selectedColumn = Math.max(0, Number(this.#selectedColumn || 0));
        this.#editMode = this.#editMode === 'edit' ? 'edit' : 'select';
    }

    resetSelection() {
        this.#initialized = true;
        this.#selectedLine = 1;
        this.#selectedColumn = 0;
        this.#editMode = 'select';
        this.#selection = null;
        this.#selectedRows = [];
        this.#selectedColumns = [];
    }

    effectiveLineCount({ sourceLineCount }) {
        return this.#virtualLineCount > 0
            ? Math.max(1, this.#virtualLineCount)
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

    resolve({ language }) {
        if (this.csvMode.isEnabled) return this.csvMode;
        if (language === 'hex') return this.hexMode;
        return this.textMode;
    }
}
