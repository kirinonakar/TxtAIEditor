namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookViewerStyles
    {
        internal static string GetCss()
        {
            return @"
:root {
    --nb-bg: #ffffff;
    --nb-fg: #1a1a1a;
    --nb-border: #ddd;
    --nb-input-bg: #f7f7f7;
    --nb-output-bg: #ffffff;
    --nb-accent: #4285f4;
    --nb-accent-hover: #3367d6;
    --nb-error: #d32f2f;
    --nb-success: #4caf50;
}
@media (prefers-color-scheme: dark) {
    :root {
        --nb-bg: #1e1e1e;
        --nb-fg: #e0e0e0;
        --nb-border: #3a3a3a;
        --nb-input-bg: #2d2d2d;
        --nb-output-bg: #1e1e1e;
        --nb-accent: #64b5f6;
        --nb-accent-hover: #90caf9;
    }
}
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
    font-family: 'Segoe UI', -apple-system, sans-serif;
    background: var(--nb-bg);
    color: var(--nb-fg);
    line-height: 1.5;
}
#notebook-container { max-width: 1000px; margin: 0 auto; padding: 0 16px 16px 16px; }
#notebook-header {
    display: flex; flex-direction: column; gap: 8px;
    padding: 6px 0 8px 0; border-bottom: 2px solid var(--nb-border); margin-bottom: 12px;
    position: sticky; top: 0; background: var(--nb-bg); z-index: 100;
}
.notebook-header-top {
    display: flex; justify-content: space-between; align-items: center; width: 100%;
}
.notebook-title { font-size: 18px; font-weight: 600; }
#notebook-toolbar { display: flex; gap: 8px; }
.nb-btn {
    padding: 6px 14px; border: 1px solid var(--nb-border); border-radius: 4px;
    background: var(--nb-input-bg); color: var(--nb-fg); cursor: pointer;
    font-size: 13px; transition: background 0.15s;
}
.nb-btn:hover { background: var(--nb-accent); color: #fff; border-color: var(--nb-accent); }
#variables-panel {
    background: var(--nb-input-bg);
    border: 1px solid var(--nb-border);
    border-radius: 6px;
    padding: 12px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.15);
    width: 100%;
}
.vars-panel-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 10px;
    gap: 12px;
}
.vars-panel-title {
    font-weight: 600;
    font-size: 14px;
}
.vars-panel-controls {
    display: flex;
    align-items: center;
    gap: 6px;
}
#vars-filter-input {
    padding: 4px 8px;
    border: 1px solid var(--nb-border);
    border-radius: 4px;
    background: var(--nb-bg);
    color: var(--nb-fg);
    font-size: 12px;
    outline: none;
    width: 160px;
}
#vars-filter-input:focus {
    border-color: var(--nb-accent);
}
.nb-btn-sm {
    padding: 3px 8px;
    font-size: 12px;
}
.vars-table-container {
    max-height: 220px;
    overflow-y: auto;
    border: 1px solid var(--nb-border);
    border-radius: 4px;
    background: var(--nb-bg);
}
.vars-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 12.5px;
    font-family: 'Consolas', 'Cascadia Code', monospace;
}
.vars-table th {
    position: sticky;
    top: 0;
    background: var(--nb-input-bg);
    border-bottom: 1px solid var(--nb-border);
    padding: 6px 10px;
    text-align: left;
    font-weight: 600;
    font-size: 12px;
    color: var(--nb-fg);
}
.vars-table td {
    padding: 5px 10px;
    border-bottom: 1px solid var(--nb-border);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 300px;
}
.vars-table tr:last-child td {
    border-bottom: none;
}
.vars-table tr:hover {
    background: var(--nb-input-bg);
}
.vars-empty {
    text-align: center;
    color: #888;
    padding: 16px !important;
    font-style: italic;
}
#cells-container { display: flex; flex-direction: column; gap: 8px; }
.cell {
    border: 1px solid var(--nb-border); border-radius: 6px; overflow: hidden;
    position: relative; box-sizing: border-box;
}
.cell-code { background: var(--nb-input-bg); }
.cell-markdown { background: var(--nb-bg); padding: 0; }
.cell-raw { background: var(--nb-input-bg); padding: 0; }
.cell-input-area {
    padding: 12px 14px; min-height: 42px; font-family: 'Consolas', 'Cascadia Code', 'Courier New', monospace;
    font-size: 14px; white-space: pre-wrap; word-break: break-word; outline: none;
    line-height: 1.6; cursor: text; box-sizing: border-box;
}
.cell-input-area:focus { background: var(--nb-bg); box-shadow: inset 0 0 0 2px var(--nb-accent); }
.cell-input-area pre { white-space: pre-wrap; word-break: break-word; margin: 0; padding: 0; font-family: inherit; font-size: inherit; line-height: inherit; }
.cell pre { white-space: pre-wrap; word-break: break-word; margin: 0; padding: 0; font-family: 'Consolas', monospace; font-size: 14px; line-height: 1.6; }
.cell-toolbar {
    display: flex; gap: 4px; padding: 6px 10px; background: var(--nb-input-bg);
    border-top: 1px solid var(--nb-border);
}
.cell-btn {
    padding: 4px 10px; border: none; border-radius: 4px; background: transparent;
    color: var(--nb-fg); cursor: pointer; font-size: 12px; opacity: 0.7;
}
.cell-btn:hover { opacity: 1; background: var(--nb-accent); color: #fff; }
.nb-context-menu {
    position: fixed;
    z-index: 10000;
    background: var(--nb-bg);
    border: 1px solid var(--nb-border);
    border-radius: 8px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.25);
    padding: 6px 0;
    min-width: 190px;
    font-family: 'Segoe UI', -apple-system, sans-serif;
    font-size: 13px;
    color: var(--nb-fg);
    user-select: none;
    display: none;
}
.nb-context-menu-item {
    padding: 6px 14px;
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    transition: background 0.12s, color 0.12s;
}
.nb-context-menu-item:hover {
    background: var(--nb-accent);
    color: #ffffff;
}
.nb-context-menu-item.disabled {
    opacity: 0.4;
    cursor: default;
    pointer-events: none;
}
.nb-context-menu-divider {
    height: 1px;
    background: var(--nb-border);
    margin: 4px 0;
}
.cell-output {
    padding: 8px 14px; background: var(--nb-output-bg); border-top: 1px solid var(--nb-border);
    font-family: 'Consolas', monospace; font-size: 13.5px; line-height: 1.5; white-space: pre-wrap; word-break: break-word;
    display: none; min-height: 0; box-sizing: border-box;
}
.cell-output.has-output { display: block; }
.cell-output .output-entry { margin: 0; padding: 0; }
.cell-output .output-entry + .output-entry { margin-top: 6px; }
.cell-output .output-stdout { color: var(--nb-fg); white-space: pre-wrap; }
.cell-output .output-stderr { color: var(--nb-error); }
.cell-output .output-error { color: var(--nb-error); }
.cell-output .output-result { color: var(--nb-accent); font-style: italic; }
.cell-running .cell-run { background: var(--nb-accent); color: #fff; opacity: 1; }
.cell-btn.is-running, .nb-btn-run.is-running { background: #d32f2f !important; color: #fff !important; border-color: #d32f2f !important; opacity: 1 !important; }
blockquote {
    border-left: 4px solid var(--nb-accent);
    margin: 8px 0;
    padding: 6px 14px;
    background: rgba(128,128,128,0.08);
    border-radius: 0 4px 4px 0;
    color: var(--nb-fg);
}
blockquote p {
    margin: 4px 0;
}
blockquote p:first-child {
    margin-top: 0;
}
blockquote p:last-child {
    margin-bottom: 0;
}
.cell-output table.dataframe {
    border-collapse: collapse;
    margin: 4px 0;
    font-size: 13px;
    font-family: 'Segoe UI', 'Consolas', monospace;
    width: auto;
    min-width: 50%;
    max-width: 100%;
    display: table;
    table-layout: auto;
    border: 1px solid var(--nb-border);
    border-radius: 4px;
    box-sizing: border-box;
}
.cell-output table.dataframe th, .cell-output table.dataframe td {
    padding: 5px 12px;
    border: 1px solid var(--nb-border);
    text-align: right;
    vertical-align: middle;
    white-space: nowrap;
    box-sizing: border-box;
}
.cell-output table.dataframe th {
    background: var(--nb-input-bg);
    font-weight: 600;
    color: var(--nb-fg);
    text-align: right;
}
.cell-output table.dataframe tbody tr:nth-child(even) {
    background: rgba(128,128,128,0.05);
}
.nb-input-request-box {
    margin: 6px 0;
    padding: 8px 12px;
    border: 1px solid var(--nb-accent);
    border-radius: 4px;
    background: var(--nb-input-bg);
    display: flex;
    flex-direction: column;
    gap: 6px;
}
.nb-input-prompt {
    font-size: 13px;
    font-weight: 600;
    color: var(--nb-fg);
}
.nb-input-controls {
    display: flex;
    gap: 8px;
}
.nb-input-field {
    flex: 1;
    padding: 4px 8px;
    border: 1px solid var(--nb-border);
    border-radius: 4px;
    background: var(--nb-bg);
    color: var(--nb-fg);
    font-family: monospace;
    font-size: 13px;
    outline: none;
}
.nb-input-field:focus {
    border-color: var(--nb-accent);
}
.mpl-status-text {
    margin-left: auto;
    font-size: 11px;
    color: #888;
    white-space: nowrap;
    user-select: none;
}
h1, h2, h3, h4 { margin: 8px 0; }
h1 { font-size: 1.6em; }
h2 { font-size: 1.4em; }
h3 { font-size: 1.2em; }
h4 { font-size: 1.1em; }
ul, ol { padding-left: 24px; margin: 4px 0; }
p { margin: 4px 0; }
code { background: var(--nb-input-bg); padding: 1px 4px; border-radius: 3px; font-size: 0.9em; }
strong { font-weight: 700; }
.markdown-cell { padding: 0; }
.markdown-preview {
    padding: 12px 14px; min-height: 42px; cursor: pointer; border-radius: 4px; line-height: 1.6; font-size: 14px; box-sizing: border-box;
}
.markdown-preview:hover {
    outline: 1px dashed var(--nb-accent);
}
.markdown-editor {
    display: none; background: var(--nb-input-bg); padding: 12px 14px; min-height: 42px; font-size: 14px; line-height: 1.6; box-sizing: border-box;
}
.cell-toggle-type { opacity: 0.8; font-weight: 500; }
.mpl-interactive-wrapper {
    display: block;
    max-width: 100%;
    box-sizing: border-box;
    margin: 8px 0;
    padding: 0;
    border: 1px solid var(--nb-border);
    border-radius: 6px;
    background: var(--nb-output-bg);
    box-shadow: 0 1px 4px rgba(0,0,0,0.06);
    overflow: hidden;
    user-select: none;
    font-family: 'Segoe UI', -apple-system, sans-serif;
}
.mpl-toolbar {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 6px;
    margin: 0;
    line-height: 1;
    height: 26px;
    box-sizing: border-box;
    background: var(--nb-input-bg);
    border-bottom: 1px solid var(--nb-border);
    font-size: 11px;
    flex-wrap: wrap;
}
.mpl-btn {
    border: 1px solid var(--nb-border);
    background: var(--nb-bg);
    color: var(--nb-fg);
    padding: 2px 6px;
    margin: 0;
    height: 20px;
    line-height: 18px;
    box-sizing: border-box;
    border-radius: 3px;
    cursor: pointer;
    font-size: 11px;
    transition: background 0.15s, border-color 0.15s;
}
.mpl-btn:hover {
    background: var(--nb-accent);
    color: #ffffff;
}
.mpl-btn.active {
    background: var(--nb-accent);
    color: #ffffff;
    border-color: var(--nb-accent);
}
.mpl-rotate-ctrl {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 0 4px;
    font-size: 11px;
    line-height: 1;
}
.mpl-rotate-slider {
    width: 100px;
    cursor: pointer;
}
.mpl-angle-val {
    min-width: 32px;
    font-size: 11px;
    font-weight: bold;
}
.mpl-viewport {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    overflow: hidden;
    padding: 8px 12px;
    margin: 0;
    background: var(--nb-output-bg);
    min-height: 40px;
    cursor: grab;
    max-width: 100%;
    box-sizing: border-box;
}
.mpl-viewport:active {
    cursor: grabbing;
}
.mpl-plot-layer {
    position: relative;
    display: inline-block;
    max-width: 100%;
    box-sizing: border-box;
    transform-origin: center center;
}
.mpl-plot-img {
    display: block;
    max-width: 100%;
    height: auto;
    object-fit: contain;
    pointer-events: none;
}
.mpl-data-clip {
    position: absolute;
    overflow: hidden;
    pointer-events: none;
    display: none;
    background: var(--nb-output-bg, white);
}
.mpl-data-img-wrapper {
    position: absolute;
    pointer-events: none;
    will-change: transform;
}
.mpl-data-img {
    display: block;
    width: 100%;
    height: 100%;
    pointer-events: none;
}
.mpl-cbar-layer {
    display: inline-block;
    margin-left: 8px;
    pointer-events: none;
}
.mpl-cbar-img {
    display: block;
    max-width: 100%;
    height: auto;
}
.mpl-status-bar {
    padding: 2px 8px;
    font-size: 10px;
    color: #888;
    background: var(--nb-input-bg);
    border-top: 1px solid var(--nb-border);
    text-align: right;
    min-height: 18px;
}

/* Syntax highlight token colors - light theme */
.token-comment { color: #008000; }
.token-string { color: #a31515; }
.token-number { color: #098658; }
.token-keyword { color: #0000ff; }
.token-control { color: #795e26; }
.token-type { color: #267f99; }
.token-function { color: #795e26; }
.token-variable { color: #001080; }
.token-operator { color: #000000; }
.token-punctuation { color: #000000; }
/* Syntax highlight token colors - dark theme */
@media (prefers-color-scheme: dark) {
    .token-comment { color: #6a9955; }
    .token-string { color: #ce9178; }
    .token-number { color: #b5cea8; }
    .token-keyword { color: #569cd6; }
    .token-control { color: #c586c0; }
    .token-type { color: #4ec9b0; }
    .token-function { color: #dcdcaa; }
    .token-variable { color: #9cdcfe; }
    .token-operator { color: #d4d4d4; }
    .token-punctuation { color: #d4d4d4; }
}
";
        }
    }
}
