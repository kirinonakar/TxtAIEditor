export const ImePhase = Object.freeze({
    Idle: 'Idle',
    NativeComposition: 'NativeComposition',
    RangeComposition: 'RangeComposition',
    ColumnComposition: 'ColumnComposition',
    TextareaBypassComposition: 'TextareaBypassComposition',
    Committing: 'Committing',
    Cancelled: 'Cancelled'
});

const compositionPhases = new Set([
    ImePhase.NativeComposition,
    ImePhase.RangeComposition,
    ImePhase.ColumnComposition,
    ImePhase.TextareaBypassComposition
]);

export class ImeController {
    #phase = ImePhase.Idle;
    #isComposing = false;
    #compositionLine = null;
    #rangeComposition = null;
    #preparedRangeCompositionLine = null;
    #columnComposition = null;
    #pendingVerticalNavigation = null;
    #pendingSelectionCollapse = null;
    #textareaBypassActive = false;
    #bypassStartLine = null;
    #bypassCursorLine = null;
    #bypassCursorColumn = null;
    #onRangeCompositionCleared;

    constructor({ onRangeCompositionCleared = () => { } } = {}) {
        this.#onRangeCompositionCleared = onRangeCompositionCleared;
    }

    get phase() {
        return this.#phase;
    }

    get isComposing() {
        return this.#isComposing;
    }

    get compositionLine() {
        return this.#compositionLine;
    }

    get rangeComposition() {
        return this.#rangeComposition;
    }

    set rangeComposition(value) {
        this.#rangeComposition = value;
    }

    get preparedRangeCompositionLine() {
        return this.#preparedRangeCompositionLine;
    }

    set preparedRangeCompositionLine(value) {
        this.#preparedRangeCompositionLine = value;
    }

    get columnComposition() {
        return this.#columnComposition;
    }

    set columnComposition(value) {
        this.#columnComposition = value;
    }

    get pendingVerticalNavigation() {
        return this.#pendingVerticalNavigation;
    }

    set pendingVerticalNavigation(value) {
        this.#pendingVerticalNavigation = value;
    }

    get pendingSelectionCollapse() {
        return this.#pendingSelectionCollapse;
    }

    set pendingSelectionCollapse(value) {
        this.#pendingSelectionCollapse = value;
    }

    get textareaBypassActive() {
        return this.#textareaBypassActive;
    }

    get bypassStartLine() {
        return this.#bypassStartLine;
    }

    set bypassStartLine(value) {
        this.#bypassStartLine = value;
    }

    get bypassCursorLine() {
        return this.#bypassCursorLine;
    }

    set bypassCursorLine(value) {
        this.#bypassCursorLine = value;
    }

    get bypassCursorColumn() {
        return this.#bypassCursorColumn;
    }

    set bypassCursorColumn(value) {
        this.#bypassCursorColumn = value;
    }

    get isCompositionActive() {
        return !!(this.#isComposing || this.#rangeComposition || this.#columnComposition);
    }

    beginComposition(phase, lineNumber) {
        if (!compositionPhases.has(phase)) return false;

        const canStart = this.#phase === ImePhase.Idle ||
            this.#phase === phase ||
            (this.#phase === ImePhase.TextareaBypassComposition &&
                phase === ImePhase.TextareaBypassComposition);
        if (!canStart) return false;

        this.#phase = phase;
        this.#isComposing = true;
        this.#compositionLine = Math.max(1, Number(lineNumber || 1));
        if (phase === ImePhase.TextareaBypassComposition) {
            this.#textareaBypassActive = true;
        }
        return true;
    }

    updateComposition() {
        return compositionPhases.has(this.#phase) && this.#isComposing;
    }

    beginCommit() {
        if (!compositionPhases.has(this.#phase)) return false;
        this.#phase = ImePhase.Committing;
        this.#isComposing = false;
        return true;
    }

    completeCommit(keepTextareaBypass = false) {
        this.#isComposing = false;
        this.#compositionLine = null;
        this.#rangeComposition = null;
        this.#preparedRangeCompositionLine = null;
        this.#onRangeCompositionCleared();
        this.#columnComposition = null;
        if (keepTextareaBypass && this.#textareaBypassActive) {
            this.#phase = ImePhase.TextareaBypassComposition;
            return;
        }

        this.#phase = ImePhase.Idle;
        this.#textareaBypassActive = false;
    }

    activateTextareaBypass(lineNumber) {
        if (this.#phase !== ImePhase.Idle &&
            this.#phase !== ImePhase.TextareaBypassComposition) {
            return false;
        }

        this.#phase = ImePhase.TextareaBypassComposition;
        this.#textareaBypassActive = true;
        this.#compositionLine = null;
        this.#isComposing = false;
        if (lineNumber) {
            this.#bypassStartLine = Math.max(1, Number(lineNumber));
        }
        return true;
    }

    cancelComposition() {
        if (this.#phase === ImePhase.Idle) return false;
        this.#phase = ImePhase.Cancelled;
        this.#isComposing = false;
        this.#compositionLine = null;
        return true;
    }

    reset() {
        this.#phase = ImePhase.Idle;
        this.#isComposing = false;
        this.#compositionLine = null;
        this.#rangeComposition = null;
        this.#preparedRangeCompositionLine = null;
        this.#onRangeCompositionCleared();
        this.#columnComposition = null;
        this.#textareaBypassActive = false;
    }
}
