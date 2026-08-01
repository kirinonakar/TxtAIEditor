const DragPhase = Object.freeze({
    Idle: 'idle',
    Potential: 'potential',
    Moving: 'moving'
});

function clonePosition(position) {
    return position ? { ...position } : null;
}

function cloneSelectionData(selectionData) {
    if (!selectionData) return null;
    return {
        start: clonePosition(selectionData.start),
        end: clonePosition(selectionData.end),
        isColumn: !!selectionData.isColumn,
        text: String(selectionData.text || '')
    };
}

export class DragDropController {
    #phase = DragPhase.Idle;
    #pointerStart = null;
    #selectionData = null;
    #dropPosition = null;
    #isCopy = false;

    get isPotential() {
        return this.#phase === DragPhase.Potential;
    }

    get isMoving() {
        return this.#phase === DragPhase.Moving;
    }

    get isActive() {
        return this.#phase !== DragPhase.Idle;
    }

    get pointerStart() {
        return clonePosition(this.#pointerStart);
    }

    get isCopy() {
        return this.#isCopy;
    }

    beginPointer(pointerStart) {
        this.reset();
        this.#pointerStart = clonePosition(pointerStart);
    }

    armSelection(selectionData) {
        const snapshot = cloneSelectionData(selectionData);
        if (!snapshot) return false;

        this.#phase = DragPhase.Potential;
        this.#selectionData = snapshot;
        this.#dropPosition = null;
        this.#isCopy = false;
        return true;
    }

    disarmSelection() {
        this.#phase = DragPhase.Idle;
        this.#selectionData = null;
        this.#dropPosition = null;
        this.#isCopy = false;
    }

    beginMove({ text, isCopy = false } = {}) {
        if (!this.isPotential || !this.#selectionData) return false;

        this.#phase = DragPhase.Moving;
        this.#selectionData.text = String(text || '');
        this.#isCopy = !!isCopy;
        return true;
    }

    updateMove({ dropPosition, isCopy = this.#isCopy } = {}) {
        if (!this.isMoving) return false;

        this.#isCopy = !!isCopy;
        if (dropPosition !== undefined) {
            this.#dropPosition = clonePosition(dropPosition);
        }
        return true;
    }

    movementSnapshot() {
        if (!this.isMoving || !this.#selectionData) return null;
        return {
            selectionData: cloneSelectionData(this.#selectionData),
            dropPosition: clonePosition(this.#dropPosition),
            isCopy: this.#isCopy
        };
    }

    clearPointerStart() {
        this.#pointerStart = null;
    }

    reset() {
        this.#phase = DragPhase.Idle;
        this.#pointerStart = null;
        this.#selectionData = null;
        this.#dropPosition = null;
        this.#isCopy = false;
    }
}
