function cloneState(value) {
    return value ? { ...value } : null;
}

export class CsvInteractionController {
    #resizeState = null;
    #dragState = null;

    get isResizing() {
        return this.#resizeState !== null;
    }

    get isDragging() {
        return this.#dragState !== null;
    }

    get resizeState() {
        return cloneState(this.#resizeState);
    }

    get dragState() {
        return cloneState(this.#dragState);
    }

    beginResize({ column, startX, startWidth } = {}) {
        this.#resizeState = {
            column: Math.max(0, Number(column || 0)),
            startX: Number(startX || 0),
            startWidth: Math.max(0, Number(startWidth || 0))
        };
        return this.resizeState;
    }

    resizedColumnAt(pointerX, { minWidth = 0 } = {}) {
        if (!this.#resizeState) return null;
        return {
            column: this.#resizeState.column,
            width: Math.max(
                Math.max(0, Number(minWidth || 0)),
                this.#resizeState.startWidth + Number(pointerX || 0) - this.#resizeState.startX)
        };
    }

    endResize() {
        const finalState = this.resizeState;
        this.#resizeState = null;
        return finalState;
    }

    beginCellDrag({ startLine, startColumn, pointerId } = {}) {
        this.#dragState = {
            mode: 'cells',
            startLine: Math.max(1, Number(startLine || 1)),
            startColumn: Math.max(0, Number(startColumn || 0)),
            pointerId
        };
        return this.dragState;
    }

    beginColumnDrag({ startColumn, pointerId } = {}) {
        this.#dragState = {
            mode: 'columns',
            startColumn: Math.max(0, Number(startColumn || 0)),
            pointerId
        };
        return this.dragState;
    }

    endDrag() {
        const finalState = this.dragState;
        this.#dragState = null;
        return finalState;
    }

    reset() {
        this.#resizeState = null;
        this.#dragState = null;
    }
}
