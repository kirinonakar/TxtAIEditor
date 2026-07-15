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

function currentPhase(state) {
    return state.imePhase || ImePhase.Idle;
}

export function beginImeComposition(state, phase, lineNumber) {
    if (!compositionPhases.has(phase)) return false;

    const current = currentPhase(state);
    const canStart = current === ImePhase.Idle ||
        current === phase ||
        (current === ImePhase.TextareaBypassComposition && phase === ImePhase.TextareaBypassComposition);
    if (!canStart) return false;

    state.imePhase = phase;
    state.isComposing = true;
    state.compositionLine = Math.max(1, Number(lineNumber || state.currentLine || 1));
    if (phase === ImePhase.TextareaBypassComposition) {
        state.textareaImeBypassActive = true;
    }
    return true;
}

export function updateImeComposition(state) {
    return compositionPhases.has(currentPhase(state)) && state.isComposing;
}

export function beginImeCommit(state) {
    if (!compositionPhases.has(currentPhase(state))) return false;
    state.imePhase = ImePhase.Committing;
    state.isComposing = false;
    return true;
}

export function completeImeCommit(state, keepTextareaBypass = false) {
    state.isComposing = false;
    state.compositionLine = null;
    state.rangeComposition = null;
    state.preparedRangeCompositionLine = null;
    globalThis.document?.body?.classList.remove('range-composition-active');
    state.columnComposition = null;
    if (keepTextareaBypass && state.textareaImeBypassActive) {
        state.imePhase = ImePhase.TextareaBypassComposition;
        return;
    }

    state.imePhase = ImePhase.Idle;
    state.textareaImeBypassActive = false;
}

export function activateTextareaImeBypass(state, lineNumber) {
    const current = currentPhase(state);
    if (current !== ImePhase.Idle && current !== ImePhase.TextareaBypassComposition) {
        return false;
    }

    state.imePhase = ImePhase.TextareaBypassComposition;
    state.textareaImeBypassActive = true;
    state.compositionLine = null;
    state.isComposing = false;
    if (lineNumber) {
        state.bypassStartLine = Math.max(1, Number(lineNumber));
    }
    return true;
}

export function cancelImeComposition(state) {
    const current = currentPhase(state);
    if (current === ImePhase.Idle) return false;
    state.imePhase = ImePhase.Cancelled;
    state.isComposing = false;
    state.compositionLine = null;
    return true;
}

export function resetImeState(state) {
    state.imePhase = ImePhase.Idle;
    state.isComposing = false;
    state.compositionLine = null;
    state.rangeComposition = null;
    state.preparedRangeCompositionLine = null;
    globalThis.document?.body?.classList.remove('range-composition-active');
    state.columnComposition = null;
    state.textareaImeBypassActive = false;
}
