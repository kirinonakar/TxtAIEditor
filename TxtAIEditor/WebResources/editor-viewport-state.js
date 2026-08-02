export class ViewportController {
    #lineHeight;
    #overscan;
    #measuredLineHeights = new Map();
    #lineHeightIndex = null;
    #lastRangeKey = '';
    #renderedRangeStart = 0;
    #renderedRangeEnd = 0;
    #renderQueued = false;
    #preservedScrollTop = null;

    constructor({ lineHeight = 22, overscan = 80 } = {}) {
        this.#lineHeight = Math.max(1, Number(lineHeight || 1));
        this.#overscan = Math.max(0, Number(overscan || 0));
    }

    get lineHeight() {
        return this.#lineHeight;
    }

    get overscan() {
        return this.#overscan;
    }

    get hasMeasuredLineHeights() {
        return this.#measuredLineHeights.size > 0;
    }

    get preservedScrollTop() {
        return this.#preservedScrollTop;
    }

    get hasPreservedScrollTop() {
        return this.#preservedScrollTop !== null;
    }

    setLineHeight(lineHeight, lineCount = 1) {
        const previous = this.#lineHeight;
        this.#lineHeight = Math.max(1, Number(lineHeight || 1));
        if (previous !== this.#lineHeight && this.hasMeasuredLineHeights) {
            this.#rebuildLineHeightIndex(lineCount);
        }
        return previous;
    }

    lineHeightFor(lineNumber, { useMeasured = false } = {}) {
        return useMeasured
            ? (this.#measuredLineHeights.get(Number(lineNumber)) || this.#lineHeight)
            : this.#lineHeight;
    }

    measuredLineHeightDeltaBefore(lineNumber, lineCount) {
        if (!this.hasMeasuredLineHeights) return 0;

        this.#ensureLineHeightIndex(lineCount);
        const index = this.#lineHeightIndex;
        let position = Math.max(0, Math.min(Number(lineNumber || 1) - 1, index.size));
        let sum = 0;
        while (position > 0) {
            sum += index.tree.get(position) || 0;
            position -= position & -position;
        }
        return sum;
    }

    totalMeasuredLineHeightDelta(lineCount) {
        if (!this.hasMeasuredLineHeights) return 0;

        this.#ensureLineHeightIndex(lineCount);
        return this.#lineHeightIndex.totalDelta;
    }

    setMeasuredLineHeight(lineNumber, height, lineCount) {
        const line = Math.max(1, Number(lineNumber || 1));
        const measured = Math.max(0, Number(height || 0));
        const previous = this.#measuredLineHeights.get(line);
        if (previous === measured) return false;

        this.#ensureLineHeightIndex(lineCount);
        this.#measuredLineHeights.set(line, measured);
        const previousDelta = previous === undefined ? 0 : this.#measuredLineHeightDelta(previous);
        const nextDelta = this.#measuredLineHeightDelta(measured);
        this.#addMeasuredLineHeightDelta(line, nextDelta - previousDelta, lineCount);
        return true;
    }

    deleteMeasuredLineHeight(lineNumber, lineCount) {
        const line = Math.max(1, Number(lineNumber || 1));
        const previous = this.#measuredLineHeights.get(line);
        if (previous === undefined) return false;

        this.#ensureLineHeightIndex(lineCount);
        this.#measuredLineHeights.delete(line);
        this.#addMeasuredLineHeightDelta(line, -this.#measuredLineHeightDelta(previous), lineCount);
        return true;
    }

    clearMeasuredLineHeights(lineCount) {
        this.#measuredLineHeights.clear();
        this.#lineHeightIndex = this.#createLineHeightIndex(lineCount);
    }

    shiftMeasuredLineHeights(fromLine, delta, lineCount) {
        const entries = [...this.#measuredLineHeights.entries()]
            .filter(([line]) => line >= fromLine)
            .sort((a, b) => delta > 0 ? b[0] - a[0] : a[0] - b[0]);
        if (entries.length === 0) return false;

        for (const [line] of entries) {
            this.#measuredLineHeights.delete(line);
        }
        for (const [line, value] of entries) {
            const nextLine = line + delta;
            if (nextLine >= 1 && nextLine <= lineCount + Math.max(delta, 0)) {
                this.#measuredLineHeights.set(nextLine, value);
            }
        }
        this.#rebuildLineHeightIndex(lineCount);
        return true;
    }

    acceptRangeKey(rangeKey) {
        const key = String(rangeKey || '');
        if (key === this.#lastRangeKey) return false;

        this.#lastRangeKey = key;
        return true;
    }

    invalidateRenderRange() {
        this.#lastRangeKey = '';
    }

    setRenderedRange(startLine, endLine) {
        this.#renderedRangeStart = Math.max(0, Number(startLine || 0));
        this.#renderedRangeEnd = Math.max(0, Number(endLine || 0));
    }

    clearRenderedRange() {
        this.#renderedRangeStart = 0;
        this.#renderedRangeEnd = 0;
    }

    overlapsRenderedRange(startLine, endLine) {
        const start = Math.max(1, Number(startLine || 1));
        const end = Math.max(start, Number(endLine || start));
        return this.#renderedRangeStart > 0 &&
            end >= this.#renderedRangeStart &&
            start <= this.#renderedRangeEnd;
    }

    beginQueuedRender({ force = false } = {}) {
        if (force) {
            this.invalidateRenderRange();
        }
        if (this.#renderQueued) return false;

        this.#renderQueued = true;
        return true;
    }

    completeQueuedRender() {
        this.#renderQueued = false;
    }

    preserveScrollTop(scrollTop) {
        const nextScrollTop = Math.max(0, Number(scrollTop || 0));
        this.#preservedScrollTop = Math.max(
            Number(this.#preservedScrollTop ?? 0),
            nextScrollTop);
        return this.#preservedScrollTop;
    }

    clampPreservedScrollTop(maximumScrollTop) {
        if (!this.hasPreservedScrollTop) return null;

        this.#preservedScrollTop = Math.min(
            Math.max(0, Number(maximumScrollTop || 0)),
            Math.max(0, Number(this.#preservedScrollTop || 0)));
        return this.#preservedScrollTop;
    }

    preservedContentHeight(viewportHeight) {
        return this.hasPreservedScrollTop
            ? Math.max(0, Number(this.#preservedScrollTop || 0)) + Math.max(0, Number(viewportHeight || 0))
            : 0;
    }

    clearPreservedScrollTop() {
        if (!this.hasPreservedScrollTop) return false;

        this.#preservedScrollTop = null;
        return true;
    }

    resetDocument() {
        this.invalidateRenderRange();
        this.clearRenderedRange();
        this.#preservedScrollTop = null;
    }

    #createLineHeightIndex(lineCount) {
        return {
            size: Math.max(2, Number(lineCount || 1) + 2),
            tree: new Map(),
            totalDelta: 0
        };
    }

    #measuredLineHeightDelta(height) {
        return Math.max(0, Number(height || 0)) - this.#lineHeight;
    }

    #rebuildLineHeightIndex(lineCount) {
        this.#lineHeightIndex = this.#createLineHeightIndex(lineCount);
        for (const [line, height] of this.#measuredLineHeights.entries()) {
            this.#addMeasuredLineHeightDelta(
                line,
                this.#measuredLineHeightDelta(height),
                lineCount);
        }
    }

    #ensureLineHeightIndex(lineCount) {
        const requiredSize = Math.max(2, Number(lineCount || 1) + 2);
        if (!this.#lineHeightIndex || this.#lineHeightIndex.size < requiredSize) {
            this.#rebuildLineHeightIndex(lineCount);
        }
    }

    #addMeasuredLineHeightDelta(lineNumber, delta, lineCount) {
        if (!delta) return;

        this.#ensureLineHeightIndex(lineCount);
        const index = this.#lineHeightIndex;
        const line = Math.max(1, Math.min(Number(lineNumber || 1), index.size));
        for (let position = line; position <= index.size; position += position & -position) {
            const next = (index.tree.get(position) || 0) + delta;
            if (Math.abs(next) < 0.0001) {
                index.tree.delete(position);
            } else {
                index.tree.set(position, next);
            }
        }
        index.totalDelta += delta;
    }
}
