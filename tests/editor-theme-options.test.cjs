const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

// Exercise the real options handler without starting WebView2. DOM rendering and
// height invalidation are observed at their boundaries; neither is needed to repaint CSS colors.
const source = readFileSync(path.join(__dirname, '../TxtAIEditor/WebResources/editor-core.js'), 'utf8');
const handlerStart = source.indexOf('let appliedLayoutOptionsKey');
const handler = source.slice(handlerStart >= 0 ? handlerStart : source.indexOf('function applyOptions('),
    source.indexOf('\nfunction setupModel('));

function createEditor() {
    const styles = new Map();
    const measuredRows = new Map([[50000, 66], [50001, 88]]);
    const calls = { layout: 0, render: 0, clear: 0 };
    const context = vm.createContext({
        state: { lineCount: 100000, dirtyLines: new Map([[50000, 'mod']]) },
        viewportController: {
            lineHeight: 22,
            setLineHeight(value) { const previous = this.lineHeight; this.lineHeight = value; return previous; }
        },
        hexEditorMode: { setEditable() {} },
        document: {
            documentElement: { style: { setProperty: (key, value) => styles.set(key, value) } },
            body: { classList: { toggle() {} } },
            getElementById: () => null
        },
        contextMenu: { querySelector: () => null },
        resolveReadableColor: (_bg, fg) => fg,
        snapCssPixelsToDevicePixels: value => value,
        usesMeasuredLineHeights: () => true,
        clearMeasuredLineHeights: () => { calls.clear++; measuredRows.clear(); },
        setupVirtualHeight: () => { calls.layout++; },
        queueRender: () => { calls.render++; }
    });
    vm.runInContext(handler, context);
    const options = { theme: 'Light', fontSize: 14, fontFamily: 'Consolas', wordWrap: true };
    context.applyOptions(options);
    Object.keys(calls).forEach(key => { calls[key] = 0; });
    measuredRows.set(50000, 66);
    measuredRows.set(50001, 88);
    return { apply: updates => context.applyOptions({ ...options, ...updates }), calls, styles, measuredRows, context };
}

test('repeated theme changes preserve measured rows and do not schedule document rendering', () => {
    const editor = createEditor();
    for (let i = 0; i < 100; i++) {
        for (const theme of ['Dark', 'PastelDark', 'Light']) editor.apply({ theme });
    }
    assert.deepEqual(editor.calls, { layout: 0, render: 0, clear: 0 });
    assert.deepEqual([...editor.measuredRows], [[50000, 66], [50001, 88]]);
    assert.equal(editor.styles.get('--bg'), '#ffffff');
    assert.equal(editor.styles.get('--token-keyword'), '#0000ff');
    editor.apply({ theme: 'PastelDark' });
    assert.equal(editor.styles.get('--bg'), '#24273a');
    assert.equal(editor.styles.get('--token-keyword'), '#c6a0f6');
});

test('custom colors repaint without resetting layout or dirty markers', () => {
    const editor = createEditor();
    editor.apply({ customBackgroundColor: '#123456', customForegroundColor: '#ffffff' });
    assert.equal(editor.styles.get('--bg'), '#123456');
    assert.equal(editor.styles.get('--fg'), '#ffffff');
    assert.equal(editor.context.state.dirtyLines.get(50000), 'mod');
    assert.deepEqual(editor.calls, { layout: 0, render: 0, clear: 0 });
});

for (const changes of [{ fontSize: 18 }, { fontFamily: 'Arial' }, { tabSize: 8 }, { wordWrap: false }]) {
    test(`geometry change still recalculates layout: ${JSON.stringify(changes)}`, () => {
        const editor = createEditor();
        editor.apply(changes);
        assert.deepEqual(editor.calls, { layout: 1, render: 1, clear: 1 });
    });
}

for (const changes of [
    { syntaxHighlighting: false }, { bracketPairColorization: false },
    { showDirtyLines: false }, { readOnly: true }, { hexEditable: true },
    { longLineProtectionFormat: 'Too long: {0}' }, { csvJsonKeyHeader: 'Key' }
]) {
    test(`content option still updates rows: ${JSON.stringify(changes)}`, () => {
        const editor = createEditor();
        editor.apply(changes);
        assert.equal(editor.calls.render, 1);
    });
}
