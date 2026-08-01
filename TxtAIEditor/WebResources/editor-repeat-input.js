export function createKeyboardRepeatController({
    activeEditableElement,
    cancelPendingRepeatFollowUps,
    deleteBackwardAtCaret,
    deleteForwardAtCaret,
    focusLine,
    getCursor,
    getLineCount,
    hasCustomSelection,
    insertPlainTextByModel,
    isImeComposing,
    isPlainTextKey,
    isReadOnly,
    makeEditablePlainText,
    setCursor,
    splitCurrentLine,
}) {
    const repeatState = {
        lastRunAt: 0,
        continuousTimer: 0,
        continuousKey: null,
        hasContinuousRun: false,
        hasPhysicalRepeatSignal: false,
        lastKeyDownAt: 0,
        keyDownSilenceMs: 350,
        intervalMs: 32,
        lineBoundaryHoldMs: 65,
        lineBoundaryUntil: 0,
        releaseGuardMs: 250,
        releasedKeys: new Map(),
        suppressBeforeInputUntil: 0,
        suppressBeforeInputTypes: new Set()
    };

    function isModelRepeatKey(event) {
        if (!event) return false;
        if (event.key === ' ' || event.code === 'Space' || event.key === 'Spacebar') {
            return !event.ctrlKey && !event.metaKey && !event.altKey;
        }
        // Plain text keys are handled by the browser's native
        // beforeinput/input flow. Treating them as model-repeat keys makes
        // keyup guard the next occurrence of the same character for 250 ms
        // (for example, the final "t" in a quickly typed "test").
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
        repeatState.suppressBeforeInputUntil = performance.now() + durationMs;
        repeatState.suppressBeforeInputTypes = new Set(inputTypes);
    }

    function shouldSuppressNativeBeforeInput(event) {
        if (!event) return false;
        const now = performance.now();
        const inputType = event.inputType || '';

        if (repeatState.continuousKey && beforeInputMatchesRepeatKey(event, repeatState.continuousKey)) {
            return true;
        }

        for (const [key, until] of repeatState.releasedKeys.entries()) {
            if (now > until) {
                repeatState.releasedKeys.delete(key);
                continue;
            }
            if (beforeInputMatchesRepeatKey(event, key)) {
                return true;
            }
        }

        if (now > repeatState.suppressBeforeInputUntil) return false;
        const types = repeatState.suppressBeforeInputTypes;
        if (types.has(inputType)) return true;
        if (types.has('insertSpace') && inputType.startsWith('insert') && event.data === ' ') return true;
        if (types.has('insertLineBreak') && beforeInputMatchesRepeatKey(event, 'Enter')) return true;
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
        const until = performance.now() + repeatState.releaseGuardMs;
        repeatState.releasedKeys.set(key, until);
    }

    function isReleaseGuardedRepeatKey(key) {
        if (!key) return false;
        const until = repeatState.releasedKeys.get(key);
        if (!until) return false;
        if (performance.now() > until) {
            repeatState.releasedKeys.delete(key);
            return false;
        }
        return true;
    }

    function markLineBoundaryTransition(targetLine, targetColumn) {
        setCursor({
            line: Math.min(Math.max(1, Number(targetLine || 1)), getLineCount()),
            column: Math.max(1, Number(targetColumn || 0) + 1)
        });
        repeatState.lineBoundaryUntil = Math.max(
            repeatState.lineBoundaryUntil,
            performance.now() + repeatState.lineBoundaryHoldMs
        );
    }

    function clearPendingRepeatEdit(releasedKey = null, addReleaseGuard = true) {
        const activeKey = repeatState.continuousKey;
        const keyToGuard = releasedKey || activeKey;
        const hadContinuousRun = repeatState.hasContinuousRun;
        if (repeatState.continuousTimer) {
            clearTimeout(repeatState.continuousTimer);
            repeatState.continuousTimer = 0;
        }
        repeatState.continuousKey = null;
        repeatState.hasContinuousRun = false;
        repeatState.hasPhysicalRepeatSignal = false;
        repeatState.lastKeyDownAt = 0;
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
        const boundaryWait = Math.max(0, repeatState.lineBoundaryUntil - now);
        const intervalWait = Math.max(0, repeatState.intervalMs - (now - repeatState.lastRunAt));
        return Math.max(boundaryWait, intervalWait);
    }

    function scheduleContinuousModelRepeatEdit(key, delayMs) {
        if (repeatState.continuousTimer) {
            clearTimeout(repeatState.continuousTimer);
            repeatState.continuousTimer = 0;
        }

        repeatState.continuousTimer = setTimeout(() => {
            repeatState.continuousTimer = 0;
            if (repeatState.continuousKey !== key || isReadOnly() || isImeComposing()) {
                return;
            }

            if (repeatState.hasPhysicalRepeatSignal &&
                performance.now() - repeatState.lastKeyDownAt > repeatState.keyDownSilenceMs) {
                clearPendingRepeatEdit(key);
                return;
            }

            const wait = repeatEditDelayFromNow();
            if (wait > 0) {
                scheduleContinuousModelRepeatEdit(key, wait);
                return;
            }

            repeatState.lastRunAt = performance.now();
            repeatState.hasContinuousRun = true;
            runModelRepeatEdit(key);
            scheduleContinuousModelRepeatEdit(key, repeatState.intervalMs);
        }, Math.max(0, Number(delayMs || 0)));
    }

    function scheduleModelRepeatEdit(key, isRepeat) {
        if (isReadOnly() || isImeComposing()) return;
        if (isRepeat && isReleaseGuardedRepeatKey(key)) return;
        if (!isRepeat) {
            repeatState.releasedKeys.delete(key);
        }

        // Backspace/Delete/Enter are handled from one cancellable timer instead of
        // browser key-repeat events. This prevents queued keydown events from
        // continuing to delete or split lines after the physical key is released.
        if (repeatState.continuousKey === key) {
            repeatState.lastKeyDownAt = performance.now();
            if (isRepeat) {
                repeatState.hasPhysicalRepeatSignal = true;
                if (!repeatState.continuousTimer) {
                    scheduleContinuousModelRepeatEdit(key, repeatEditDelayFromNow());
                }
            }
            return;
        }

        clearPendingRepeatEdit(null, false);
        repeatState.continuousKey = key;
        repeatState.hasContinuousRun = false;
        repeatState.hasPhysicalRepeatSignal = !!isRepeat;
        repeatState.lastKeyDownAt = performance.now();
        repeatState.lastRunAt = performance.now();
        runModelRepeatEdit(key);
        if (isRepeat) {
            scheduleContinuousModelRepeatEdit(key, repeatEditDelayFromNow());
        }
    }

    function runModelRepeatEdit(key) {
        if (isReadOnly() || isImeComposing()) return;
        const cursor = getCursor();
        let element = activeEditableElement();
        if (!element || element.getAttribute('contenteditable') !== 'true') {
            focusLine(cursor.line, Math.max(0, cursor.column - 1));
            return;
        }

        if (key === 'Enter') {
            const elementLineNumber = Number(element.dataset.line || 0);
            splitCurrentLine(element, { preferStateCaret: elementLineNumber !== cursor.line });
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
