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
    #pendingColumnTextInputs = [];
    #onRangeCompositionCleared;
    #setTimer;
    #clearTimer;

    constructor({
        onRangeCompositionCleared = () => { },
        setTimer = (callback, delay) => setTimeout(callback, delay),
        clearTimer = timer => clearTimeout(timer)
    } = {}) {
        this.#onRangeCompositionCleared = onRangeCompositionCleared;
        this.#setTimer = setTimer;
        this.#clearTimer = clearTimer;
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

    get pendingColumnTextInputCount() {
        return this.#pendingColumnTextInputs.length;
    }

    queueColumnTextInputFallback(text, callback, delayMs = 40) {
        const value = String(text ?? '');
        if (!value || typeof callback !== 'function') return null;

        const pending = { text: value, timer: null };
        this.#pendingColumnTextInputs.push(pending);
        pending.timer = this.#setTimer(() => {
            const index = this.#pendingColumnTextInputs.indexOf(pending);
            if (index < 0) return;
            this.#pendingColumnTextInputs.splice(index, 1);
            callback(value);
        }, Math.max(0, Number(delayMs || 0)));
        return { ...pending };
    }

    consumePendingColumnTextInput(text = null) {
        if (this.#pendingColumnTextInputs.length === 0) return null;

        const value = text === null || text === undefined ? '' : String(text);
        const index = value
            ? this.#pendingColumnTextInputs.findIndex(pending => pending.text === value)
            : 0;
        if (index < 0) return null;

        const [pending] = this.#pendingColumnTextInputs.splice(index, 1);
        if (pending.timer) this.#clearTimer(pending.timer);
        return pending.text;
    }

    cancelPendingColumnTextInputs() {
        for (const pending of this.#pendingColumnTextInputs) {
            if (pending.timer) this.#clearTimer(pending.timer);
        }
        this.#pendingColumnTextInputs.length = 0;
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
        this.cancelPendingColumnTextInputs();
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
