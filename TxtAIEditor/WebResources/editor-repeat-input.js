export function createModelRepeatInputHandlers({
    activeEditableElement,
    cancelPendingRepeatFollowUps,
    deleteBackwardAtCaret,
    deleteForwardAtCaret,
    focusLine,
    hasCustomSelection,
    insertPlainTextByModel,
    isPlainTextKey,
    makeEditablePlainText,
    splitCurrentLine,
    state
}) {
    function isModelRepeatKey(event) {
        if (!event) return false;
        if (event.key === ' ' || event.code === 'Space' || event.key === 'Spacebar') {
            return !event.ctrlKey && !event.metaKey && !event.altKey;
        }
        if (isPlainTextKey(event)) {
            return true;
        }
        if (event.key === 'Enter') {
            return !event.ctrlKey && !event.metaKey && !event.altKey;
        }

        const isDelOrBack = event.key === 'Backspace' || event.key === 'Delete';
        if (!isDelOrBack) return false;
        if (event.ctrlKey || event.metaKey || event.altKey) {
            return hasCustomSelection();
        }
        return true;
    }

    function normalizedModelRepeatKey(event) {
        if (event.key === ' ' || event.code === 'Space' || event.key === 'Spacebar') return 'Space';
        if (isPlainTextKey(event)) return `Text:${event.key}`;
        if (event.key === 'Backspace') return 'Backspace';
        if (event.key === 'Delete') return 'Delete';
        if (event.key === 'Enter') return 'Enter';
        return event.key;
    }

    function isSpaceInputEvent(event) {
        if (!event) return false;
        const inputType = event.inputType || '';
        return (inputType === 'insertText' || inputType === 'insertSpace') && event.data === ' ';
    }

    function markNativeBeforeInputHandled(inputTypes, durationMs = 120) {
        state.repeatEdit.suppressBeforeInputUntil = performance.now() + durationMs;
        state.repeatEdit.suppressBeforeInputTypes = new Set(inputTypes);
    }

    function shouldSuppressNativeBeforeInput(event) {
        if (!event) return false;
        const now = performance.now();
        const inputType = event.inputType || '';

        if (state.repeatEdit.continuousKey && beforeInputMatchesRepeatKey(event, state.repeatEdit.continuousKey)) {
            return true;
        }

        for (const [key, until] of state.repeatEdit.releasedKeys.entries()) {
            if (now > until) {
                state.repeatEdit.releasedKeys.delete(key);
                continue;
            }
            if (beforeInputMatchesRepeatKey(event, key)) {
                return true;
            }
        }

        if (now > state.repeatEdit.suppressBeforeInputUntil) return false;
        const types = state.repeatEdit.suppressBeforeInputTypes;
        if (types.has(inputType)) return true;
        if (types.has('insertSpace') && inputType.startsWith('insert') && event.data === ' ') return true;
        return false;
    }

    function beforeInputMatchesRepeatKey(event, key) {
        const inputType = event.inputType || '';
        if (key?.startsWith?.('Text:')) {
            return inputType === 'insertText' && event.data === key.slice(5);
        }

        switch (key) {
            case 'Space':
                return (inputType === 'insertText' || inputType === 'insertSpace') && event.data === ' ';
            case 'Enter':
                return inputType === 'insertLineBreak' ||
                    inputType === 'insertParagraph' ||
                    (inputType === 'insertText' && (event.data === '\n' || event.data === '\r')) ||
                    (inputType === 'insertText' && event.data === null);
            case 'Backspace':
                return inputType === 'deleteContentBackward';
            case 'Delete':
                return inputType === 'deleteContentForward';
            default:
                return false;
        }
    }

    function rememberReleasedRepeatKey(key) {
        if (!key) return;
        const until = performance.now() + state.repeatEdit.releaseGuardMs;
        state.repeatEdit.releasedKeys.set(key, until);
    }

    function isReleaseGuardedRepeatKey(key) {
        if (!key) return false;
        const until = state.repeatEdit.releasedKeys.get(key);
        if (!until) return false;
        if (performance.now() > until) {
            state.repeatEdit.releasedKeys.delete(key);
            return false;
        }
        return true;
    }

    function markLineBoundaryTransition(targetLine, targetColumn) {
        state.currentLine = Math.min(Math.max(1, Number(targetLine || 1)), state.lineCount);
        state.currentColumn = Math.max(1, Number(targetColumn || 0) + 1);
        state.repeatEdit.lineBoundaryUntil = Math.max(
            state.repeatEdit.lineBoundaryUntil,
            performance.now() + state.repeatEdit.lineBoundaryHoldMs
        );
    }

    function clearPendingRepeatEdit(releasedKey = null, addReleaseGuard = true) {
        const activeKey = state.repeatEdit.continuousKey;
        const keyToGuard = releasedKey || activeKey;
        const hadContinuousRun = state.repeatEdit.hasContinuousRun;
        if (state.repeatEdit.timer) {
            clearTimeout(state.repeatEdit.timer);
            state.repeatEdit.timer = 0;
        }
        if (state.repeatEdit.continuousTimer) {
            clearTimeout(state.repeatEdit.continuousTimer);
            state.repeatEdit.continuousTimer = 0;
        }
        state.repeatEdit.pending = null;
        state.repeatEdit.continuousKey = null;
        state.repeatEdit.hasContinuousRun = false;
        state.repeatEdit.hasPhysicalRepeatSignal = false;
        state.repeatEdit.lastKeyDownAt = 0;
        if (addReleaseGuard) {
            rememberReleasedRepeatKey(keyToGuard);
            if (hadContinuousRun) {
                cancelPendingRepeatFollowUps(keyToGuard);
            }
            if (activeKey && activeKey !== keyToGuard) {
                rememberReleasedRepeatKey(activeKey);
                if (hadContinuousRun) {
                    cancelPendingRepeatFollowUps(activeKey);
                }
            }
        }
    }

    function repeatEditDelayFromNow() {
        const now = performance.now();
        const boundaryWait = Math.max(0, state.repeatEdit.lineBoundaryUntil - now);
        const intervalWait = Math.max(0, state.repeatEdit.intervalMs - (now - state.repeatEdit.lastRunAt));
        return Math.max(boundaryWait, intervalWait);
    }

    function scheduleContinuousModelRepeatEdit(key, delayMs) {
        if (state.repeatEdit.continuousTimer) {
            clearTimeout(state.repeatEdit.continuousTimer);
            state.repeatEdit.continuousTimer = 0;
        }

        state.repeatEdit.continuousTimer = setTimeout(() => {
            state.repeatEdit.continuousTimer = 0;
            if (state.repeatEdit.continuousKey !== key || state.readOnly || state.isComposing) {
                return;
            }

            if (state.repeatEdit.hasPhysicalRepeatSignal &&
                performance.now() - state.repeatEdit.lastKeyDownAt > state.repeatEdit.keyDownSilenceMs) {
                clearPendingRepeatEdit(key);
                return;
            }

            const wait = repeatEditDelayFromNow();
            if (wait > 0) {
                scheduleContinuousModelRepeatEdit(key, wait);
                return;
            }

            state.repeatEdit.lastRunAt = performance.now();
            state.repeatEdit.hasContinuousRun = true;
            runModelRepeatEdit(key);
            scheduleContinuousModelRepeatEdit(key, state.repeatEdit.intervalMs);
        }, Math.max(0, Number(delayMs || 0)));
    }

    function scheduleModelRepeatEdit(key, isRepeat) {
        if (state.readOnly || state.isComposing) return;
        if (isRepeat && isReleaseGuardedRepeatKey(key)) return;
        if (!isRepeat) {
            state.repeatEdit.releasedKeys.delete(key);
        }

        // Backspace/Delete/Enter are handled from one cancellable timer instead of
        // browser key-repeat events. This prevents queued keydown events from
        // continuing to delete or split lines after the physical key is released.
        if (state.repeatEdit.continuousKey === key) {
            state.repeatEdit.lastKeyDownAt = performance.now();
            if (isRepeat) {
                state.repeatEdit.hasPhysicalRepeatSignal = true;
                if (!state.repeatEdit.continuousTimer) {
                    scheduleContinuousModelRepeatEdit(key, repeatEditDelayFromNow());
                }
            }
            return;
        }

        clearPendingRepeatEdit(null, false);
        state.repeatEdit.continuousKey = key;
        state.repeatEdit.pending = null;
        state.repeatEdit.hasContinuousRun = false;
        state.repeatEdit.hasPhysicalRepeatSignal = !!isRepeat;
        state.repeatEdit.lastKeyDownAt = performance.now();
        state.repeatEdit.lastRunAt = performance.now();
        runModelRepeatEdit(key);
        if (isRepeat) {
            scheduleContinuousModelRepeatEdit(key, repeatEditDelayFromNow());
        }
    }

    function runModelRepeatEdit(key) {
        if (state.readOnly || state.isComposing) return;
        let element = activeEditableElement();
        if (!element || element.getAttribute('contenteditable') !== 'true') {
            focusLine(state.currentLine, Math.max(0, state.currentColumn - 1));
            return;
        }

        if (key === 'Enter') {
            const elementLineNumber = Number(element.dataset.line || 0);
            splitCurrentLine(element, { preferStateCaret: elementLineNumber !== state.currentLine });
            return;
        }

        if (key === 'Space') {
            insertPlainTextByModel(element, ' ');
            return;
        }

        if (key?.startsWith?.('Text:')) {
            insertPlainTextByModel(element, key.slice(5));
            return;
        }

        makeEditablePlainText(element);
        if (key === 'Backspace') {
            deleteBackwardAtCaret(element);
        } else if (key === 'Delete') {
            deleteForwardAtCaret(element);
        }
    }

    return {
        clearPendingRepeatEdit,
        isModelRepeatKey,
        isSpaceInputEvent,
        markLineBoundaryTransition,
        markNativeBeforeInputHandled,
        normalizedModelRepeatKey,
        scheduleModelRepeatEdit,
        shouldSuppressNativeBeforeInput
    };
}
