export function createCaretNavigationCommands({
    caretRectForOffset,
    changedTextBetween,
    clearCustomSelectionVisuals,
    clearPendingImeSelectionCollapse,
    commitLine,
    commitLineForSave,
    finishColumnComposition,
    finishRangeComposition,
    focusLine,
    getCaretOffset,
    graphemeDeleteEnd,
    graphemeDeleteStart,
    hasCustomSelection,
    lineTextFromElement,
    normalizeSelection,
    offsetFromPointInElement,
    queueRender,
    reportCursorAndSelection,
    scrollContainer,
    setCaret,
    state,
    syncCustomSelectionClass,
    viewport
}) {
    let verticalCaretVisualAnchor = null;

    function commitDomLineBeforeCaretNavigation(element) {
        if (!element || element.getAttribute?.('contenteditable') !== 'true') return false;

        const lineNumber = Number(element.dataset.line || state.currentLine || 1);
        const domText = lineTextFromElement(element);
        if (state.cache.get(lineNumber) === domText) return false;

        // 화살표키 이동은 queueRender/focusLine을 거치므로, 이동 전에 contenteditable DOM에만
        // 남아 있는 최종 IME 문자열을 모델 캐시에 먼저 저장해야 한다. 그렇지 않으면 렌더링이
        // 이전 캐시 값으로 돌아가면서 방금 입력한 한글이 사라질 수 있다.
        commitLine(element);
        return true;
    }

    function finishPendingImeBeforeCaretNavigation(element) {
        if (!element || element.getAttribute?.('contenteditable') !== 'true') return false;

        if (state.rangeComposition) {
            const pending = state.rangeComposition;
            const lineNumber = Number(pending.lineNumber || element.dataset.line || state.currentLine || 1);
            const targetElement = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`) || element;
            const finalText = targetElement?.getAttribute?.('contenteditable') === 'true'
                ? lineTextFromElement(targetElement)
                : pending.beforeText;
            const insertedText = changedTextBetween(pending.beforeText, finalText);

            if (finishRangeComposition(targetElement, lineNumber, insertedText)) {
                state.isComposing = false;
                state.compositionLine = null;
                clearPendingImeSelectionCollapse();
                reportCursorAndSelection(targetElement);
                return true;
            }
        }

        if (state.columnComposition) {
            const lineNumber = Number(element.dataset.line || state.compositionLine || state.currentLine || 1);
            if (finishColumnComposition(element, lineNumber)) {
                state.isComposing = false;
                state.compositionLine = null;
                clearPendingImeSelectionCollapse();
                reportCursorAndSelection(element);
                return true;
            }
        }

        if (state.isComposing) {
            commitLineForSave(element);
            clearPendingImeSelectionCollapse();
            return true;
        }

        commitDomLineBeforeCaretNavigation(element);
        return false;
    }

    function anchoredCaretRectForVerticalMove(element, lineNumber, caret, fallbackRect) {
        const anchor = verticalCaretVisualAnchor;
        if (!anchor ||
            anchor.line !== lineNumber ||
            anchor.column !== caret ||
            performance.now() - anchor.time > 2000 ||
            document.activeElement?.closest?.('.line-text') !== element) {
            return fallbackRect;
        }

        // Wrapped 줄 내부에서 위/아래를 계속 누르면 setCaret/focusLine이 scrollTop을 바꿀 수 있다.
        // 저장된 visual anchor는 viewport 좌표이므로, 스크롤 변화량만큼 보정하지 않으면
        // 다음 반복 이동이 현재 caret 위치보다 몇 visual line 아래/위를 기준으로 계산되어 건너뛰게 된다.
        const scrollDelta = scrollContainer.scrollTop - Number(anchor.scrollTop || 0);
        const top = anchor.top - scrollDelta;
        const left = anchor.left;

        return {
            left,
            right: left,
            top,
            bottom: top + anchor.height,
            height: anchor.height
        };
    }

    function rememberVerticalCaretVisualAnchor(line, column, left, top, height) {
        const safeHeight = Math.max(1, Number(height || state.lineHeight));
        verticalCaretVisualAnchor = {
            line,
            column,
            left,
            top,
            bottom: top + safeHeight,
            height: safeHeight,
            scrollTop: scrollContainer.scrollTop,
            time: performance.now()
        };
    }

    function clearVerticalCaretVisualAnchor() {
        verticalCaretVisualAnchor = null;
    }

    function samePosition(a, b) {
        return !!a && !!b && a.line === b.line && a.column === b.column;
    }

    function selectionFocusPosition(fallbackPosition) {
        if (!state.selection || !state.selectionAnchor) return fallbackPosition;

        if (samePosition(state.selection.start, state.selectionAnchor) &&
            !samePosition(state.selection.end, state.selectionAnchor)) {
            return state.selection.end;
        }

        if (samePosition(state.selection.end, state.selectionAnchor) &&
            !samePosition(state.selection.start, state.selectionAnchor)) {
            return state.selection.start;
        }

        return state.selection.end || fallbackPosition;
    }

    function editableElementForLine(lineNumber, fallbackElement) {
        if (Number(fallbackElement?.dataset?.line || 0) === lineNumber &&
            fallbackElement.getAttribute?.('contenteditable') === 'true') {
            return fallbackElement;
        }

        const element = viewport.querySelector(`.line-text[data-line="${lineNumber}"]`);
        return element?.getAttribute?.('contenteditable') === 'true' ? element : null;
    }

    function visualLineBoundsForElement(element) {
        const textRect = element?.getBoundingClientRect?.();
        const rowRect = element?.closest?.('.line-row')?.getBoundingClientRect?.();

        if (!textRect && !rowRect) return null;
        if (!rowRect || rowRect.height <= 0) return textRect;
        if (!textRect || textRect.height <= 0) return rowRect;

        // word-wrap 상태에서는 실제 visual line 높이가 .line-text가 아니라
        // gutter를 포함한 .line-row 높이로 잡히는 경우가 있다.
        // 위/아래 이동의 세로 범위를 .line-text rect만으로 판단하면
        // 같은 원본 줄 안의 다음 wrap 줄을 건너뛰고 인접 원본 줄로 이동한다.
        return {
            left: textRect.left,
            right: textRect.right,
            top: Math.min(textRect.top, rowRect.top),
            bottom: Math.max(textRect.bottom, rowRect.bottom),
            width: textRect.width,
            height: Math.max(textRect.height, rowRect.height)
        };
    }

    function adjacentLogicalLineTarget(lineNumber, direction, preferredX, lineStep, fallbackColumn) {
        const targetLine = lineNumber + direction;
        if (targetLine < 1 || targetLine > state.lineCount) return null;

        const targetText = state.cache.get(targetLine) || '';
        const fallback = {
            line: targetLine,
            column: Math.min(Math.max(0, Number(fallbackColumn || 0)), targetText.length)
        };

        if (!state.wordWrap) {
            return fallback;
        }

        const targetElement = viewport.querySelector(`.line-text[data-line="${targetLine}"]`);
        if (!targetElement || targetElement.getAttribute('contenteditable') !== 'true') {
            return fallback;
        }

        const targetTextRect = targetElement.getBoundingClientRect();
        const targetBounds = visualLineBoundsForElement(targetElement);
        if (!targetTextRect || !targetBounds || targetBounds.height <= 0) {
            return fallback;
        }

        const x = Math.max(targetTextRect.left + 1, Math.min(preferredX, targetTextRect.right - 1));
        const y = direction < 0
            ? targetBounds.bottom - lineStep / 2
            : targetBounds.top + lineStep / 2;
        const column = offsetFromPointInElement(targetElement, x, y);
        const targetColumn = column === null ? fallback.column : column;
        const visualTop = direction < 0
            ? Math.max(targetBounds.top, targetBounds.bottom - lineStep)
            : targetBounds.top;
        return {
            line: targetLine,
            column: targetColumn,
            visualLeft: x,
            visualTop,
            visualHeight: lineStep
        };
    }

    function moveCaretVertical(element, direction, extendSelection = false) {
        if (!element || element.getAttribute('contenteditable') !== 'true') return false;
        if (finishPendingImeBeforeCaretNavigation(element)) return true;

        let lineNumber = Number(element.dataset.line || state.currentLine || 1);
        let moveElement = element;
        let text = lineTextFromElement(moveElement);
        let caret = Math.max(0, Math.min(getCaretOffset(moveElement), text.length));

        if (extendSelection) {
            const focusPosition = selectionFocusPosition({ line: lineNumber, column: caret });
            lineNumber = Math.min(Math.max(1, Number(focusPosition.line || lineNumber)), state.lineCount);
            moveElement = editableElementForLine(lineNumber, element) || moveElement;
            text = Number(moveElement.dataset.line || 0) === lineNumber
                ? lineTextFromElement(moveElement)
                : (state.cache.get(lineNumber) || '');
            caret = Math.max(0, Math.min(Number(focusPosition.column || 0), text.length));
        }

        const anchor = extendSelection
            ? (state.selectionAnchor || { line: lineNumber, column: caret })
            : null;

        let target = null;

        const hasMoveElement = Number(moveElement?.dataset?.line || 0) === lineNumber;
        let caretRect = hasMoveElement ? caretRectForOffset(moveElement, caret) : null;
        let preferredX = null;
        let lineStep = state.lineHeight;
        if (!caretRect) {
            target = adjacentLogicalLineTarget(lineNumber, direction, caret, lineStep, caret);
        } else {
            caretRect = anchoredCaretRectForVerticalMove(moveElement, lineNumber, caret, caretRect);
            const elementRect = moveElement.getBoundingClientRect();
            const visualBounds = visualLineBoundsForElement(moveElement) || elementRect;
            const styles = window.getComputedStyle(moveElement);
            const parsedLineHeight = Number.parseFloat(styles.lineHeight);
            lineStep = Math.max(1, Number.isFinite(parsedLineHeight) ? parsedLineHeight : (caretRect.height || state.lineHeight));
            preferredX = Math.max(elementRect.left + 1, Math.min(caretRect.left, elementRect.right - 1));
            const targetY = direction < 0
                ? caretRect.top - lineStep / 2
                : caretRect.bottom + lineStep / 2;

            if (targetY >= visualBounds.top - 1 && targetY <= visualBounds.bottom + 1) {
                const targetColumn = offsetFromPointInElement(moveElement, preferredX, targetY, caretRect, direction, lineStep);
                if (targetColumn !== null) {
                    target = {
                        line: lineNumber,
                        column: targetColumn,
                        visualLeft: preferredX,
                        visualTop: targetY - lineStep / 2,
                        visualHeight: lineStep
                    };
                }
            }

            if (!target) {
                target = adjacentLogicalLineTarget(lineNumber, direction, preferredX ?? caret, lineStep, caret);
            }
        }

        if (target) {
            if (Number.isFinite(target.visualTop) && Number.isFinite(target.visualLeft)) {
                rememberVerticalCaretVisualAnchor(
                    target.line,
                    target.column,
                    target.visualLeft,
                    target.visualTop,
                    target.visualHeight ?? lineStep);
            } else {
                clearVerticalCaretVisualAnchor();
            }

            if (extendSelection) {
                state.selectionAnchor = anchor;
                state.selection = (anchor.line === target.line && anchor.column === target.column)
                    ? null
                    : { start: anchor, end: target };
                state.currentLine = target.line;
                state.currentColumn = target.column + 1;
                syncCustomSelectionClass();
                clearCustomSelectionVisuals();
                if (target.line === lineNumber &&
                    Number(moveElement?.dataset?.line || 0) === target.line) {
                    setCaret(moveElement, target.column, 3 * state.lineHeight, false);
                } else {
                    queueRender(true);
                    setTimeout(() => focusLine(target.line, target.column, 3 * state.lineHeight), 0);
                }
            } else {
                state.selection = null;
                state.selectionAnchor = { line: target.line, column: target.column };
                state.currentLine = target.line;
                state.currentColumn = target.column + 1;
                syncCustomSelectionClass();
                if (target.line === lineNumber) {
                    setCaret(moveElement, target.column, 3 * state.lineHeight);
                } else {
                    focusLine(target.line, target.column, 3 * state.lineHeight);
                }
            }
            return true;
        }

        return false;
    }

    function moveCaretHorizontal(element, direction, extendSelection = false) {
        if (!element || element.getAttribute('contenteditable') !== 'true') return false;
        if (finishPendingImeBeforeCaretNavigation(element)) return true;
        clearVerticalCaretVisualAnchor();

        let lineNumber = Number(element.dataset.line || state.currentLine || 1);
        let moveElement = element;
        let text = lineTextFromElement(moveElement);
        let caret = Math.max(0, Math.min(getCaretOffset(moveElement), text.length));

        if (extendSelection) {
            const focusPosition = selectionFocusPosition({ line: lineNumber, column: caret });
            lineNumber = Math.min(Math.max(1, Number(focusPosition.line || lineNumber)), state.lineCount);
            moveElement = editableElementForLine(lineNumber, element) || moveElement;
            text = Number(moveElement.dataset.line || 0) === lineNumber
                ? lineTextFromElement(moveElement)
                : (state.cache.get(lineNumber) || '');
            caret = Math.max(0, Math.min(Number(focusPosition.column || 0), text.length));
        }

        let target = { line: lineNumber, column: caret };
        let collapsedCustomSelection = false;

        if (!extendSelection && hasCustomSelection()) {
            const selection = normalizeSelection();
            if (selection) {
                collapsedCustomSelection = true;
                target = direction < 0
                    ? { line: selection.start.line, column: selection.start.column }
                    : { line: selection.end.line, column: selection.end.column };
            }
        } else if (direction < 0) {
            if (caret > 0) {
                target = { line: lineNumber, column: graphemeDeleteStart(text, caret) };
            } else if (lineNumber > 1) {
                const previousText = state.cache.get(lineNumber - 1) || '';
                target = { line: lineNumber - 1, column: previousText.length };
            }
        } else {
            if (caret < text.length) {
                target = { line: lineNumber, column: graphemeDeleteEnd(text, caret) };
            } else if (lineNumber < state.lineCount) {
                target = { line: lineNumber + 1, column: 0 };
            }
        }

        if (extendSelection) {
            const anchor = state.selectionAnchor || { line: lineNumber, column: caret };
            state.selectionAnchor = anchor;
            state.selection = (anchor.line === target.line && anchor.column === target.column)
                ? null
                : { start: anchor, end: target };
            state.currentLine = target.line;
            state.currentColumn = target.column + 1;
            syncCustomSelectionClass();
            clearCustomSelectionVisuals();
            if (target.line === lineNumber &&
                Number(moveElement?.dataset?.line || 0) === target.line) {
                setCaret(moveElement, target.column, 3 * state.lineHeight, false);
            } else {
                queueRender(true);
                setTimeout(() => focusLine(target.line, target.column, 3 * state.lineHeight), 0);
            }
        } else {
            state.selection = null;
            state.selectionAnchor = { line: target.line, column: target.column };
            syncCustomSelectionClass();
            if (collapsedCustomSelection) {
                clearCustomSelectionVisuals();
            }
            if (target.line === lineNumber &&
                Number(moveElement?.dataset?.line || 0) === target.line) {
                setCaret(moveElement, target.column, 3 * state.lineHeight);
            } else {
                focusLine(target.line, target.column, 3 * state.lineHeight);
            }
        }

        return true;
    }

    return {
        moveCaretHorizontal,
        moveCaretVertical
    };
}
