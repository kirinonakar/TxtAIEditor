export class EditorDocumentCache {
    #lines = new Map();
    #lineEndStacks = new Map();
    #htmlLineEndContexts = new Map();

    get size() {
        return this.#lines.size;
    }

    clear() {
        this.#lines.clear();
        this.clearDerivedContexts();
    }

    clearDerivedContexts() {
        this.#lineEndStacks.clear();
        this.#htmlLineEndContexts.clear();
    }

    delete(lineNumber) {
        const deleted = this.#lines.delete(lineNumber);
        this.#invalidateDerivedContexts(lineNumber);
        return deleted;
    }

    entries() {
        return this.#lines.entries();
    }

    get(lineNumber) {
        return this.#lines.get(lineNumber);
    }

    has(lineNumber) {
        return this.#lines.has(lineNumber);
    }

    keys() {
        return this.#lines.keys();
    }

    set(lineNumber, text) {
        this.#lines.set(lineNumber, text);
        this.#invalidateDerivedContexts(lineNumber);
        return this;
    }

    values() {
        return this.#lines.values();
    }

    getLineEndStack(lineNumber) {
        return this.#lineEndStacks.get(lineNumber);
    }

    hasLineEndStack(lineNumber) {
        return this.#lineEndStacks.has(lineNumber);
    }

    setLineEndStack(lineNumber, stack) {
        this.#lineEndStacks.set(lineNumber, stack);
    }

    getHtmlLineEndContext(lineNumber) {
        return this.#htmlLineEndContexts.get(lineNumber);
    }

    setHtmlLineEndContext(lineNumber, context) {
        this.#htmlLineEndContexts.set(lineNumber, context);
    }

    #invalidateDerivedContexts(startLine) {
        const line = Number(startLine);
        if (!Number.isFinite(line) || line <= 1) {
            this.#lineEndStacks.clear();
            this.#htmlLineEndContexts.clear();
            return;
        }

        this.#deleteKeysFrom(this.#lineEndStacks, line);
        this.#deleteKeysFrom(this.#htmlLineEndContexts, line);
    }

    #deleteKeysFrom(map, startLine) {
        for (const key of map.keys()) {
            if (key >= startLine) {
                map.delete(key);
            }
        }
    }
}
