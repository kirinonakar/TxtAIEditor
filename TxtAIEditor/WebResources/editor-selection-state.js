export class SelectionController {
    #selection = null;
    #anchor = null;
    #isSelecting = false;
    #isLineSelecting = false;

    get selection() {
        return this.#selection;
    }

    set selection(value) {
        this.#selection = value;
    }

    get anchor() {
        return this.#anchor;
    }

    set anchor(value) {
        this.#anchor = value;
    }

    get isSelecting() {
        return this.#isSelecting;
    }

    set isSelecting(value) {
        this.#isSelecting = !!value;
    }

    get isLineSelecting() {
        return this.#isLineSelecting;
    }

    set isLineSelecting(value) {
        this.#isLineSelecting = !!value;
    }

    normalize(selection = this.#selection) {
        if (!selection) return null;
        const a = selection.start;
        const b = selection.end;
        if (a.line < b.line || (a.line === b.line && a.column <= b.column)) {
            return { start: a, end: b, isColumn: !!selection.isColumn };
        }
        return { start: b, end: a, isColumn: !!selection.isColumn };
    }

    hasSelection() {
        const normalized = this.normalize();
        return !!normalized &&
            (normalized.start.line !== normalized.end.line ||
                normalized.start.column !== normalized.end.column);
    }

    activeColumnSelection() {
        const normalized = this.normalize();
        return normalized?.isColumn && this.hasSelection() ? normalized : null;
    }

    clear({ clearAnchor = false } = {}) {
        this.#selection = null;
        if (clearAnchor) {
            this.#anchor = null;
        }
    }

    endPointerSelection() {
        this.#isSelecting = false;
        this.#isLineSelecting = false;
    }
}
