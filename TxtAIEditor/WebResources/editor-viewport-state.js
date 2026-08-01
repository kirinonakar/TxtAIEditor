export class ViewportController {
    #lastRangeKey = '';
    #renderedRangeStart = 0;
    #renderedRangeEnd = 0;
    #renderQueued = false;
    #preservedScrollTop = null;

    get preservedScrollTop() {
        return this.#preservedScrollTop;
    }

    get hasPreservedScrollTop() {
        return this.#preservedScrollTop !== null;
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
}
