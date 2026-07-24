using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace TxtAIEditor.Core.Services
{
    public sealed class JupyterNotebookViewerService
    {
        private readonly Func<string, string, string> _getString;

        public JupyterNotebookViewerService(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return BuildErrorHtml(ex.Message);
            }

            NotebookDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<NotebookDocument>(json);
            }
            catch (Exception ex)
            {
                return BuildErrorHtml(ex.Message);
            }

            if (doc == null || doc.Cells == null)
            {
                return BuildErrorHtml(_getString("JupyterNotebookInvalid", "Invalid Jupyter notebook format."));
            }

            return BuildNotebookHtml(doc, filePath);
        }

        private string BuildNotebookHtml(NotebookDocument doc, string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            string dirPath = Path.GetDirectoryName(filePath) ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ko\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"UTF-8\" />");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
            sb.AppendLine($"<title>{HtmlEncode(fileName)}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(GetNotebookCss());
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div id=\"notebook-container\">");

            sb.AppendLine("<div id=\"notebook-header\">");
            sb.AppendLine($"<span class=\"notebook-title\">{HtmlEncode(fileName)}</span>");
            sb.AppendLine("<div id=\"notebook-toolbar\">");
            sb.AppendLine("<button id=\"btn-add-code\" class=\"nb-btn nb-btn-add\">+ Code</button>");
            sb.AppendLine("<button id=\"btn-add-markdown\" class=\"nb-btn nb-btn-add\">+ Markdown</button>");
            sb.AppendLine("<button id=\"btn-save\" class=\"nb-btn nb-btn-save\">Save</button>");
            sb.AppendLine("<button id=\"btn-run-all\" class=\"nb-btn nb-btn-run\">Run All</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"cells-container\">");

            int cellIndex = 0;
            foreach (var cell in doc.Cells)
            {
                sb.AppendLine(BuildCellHtml(cell, cellIndex));
                cellIndex++;
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<script>");
            sb.AppendLine($"window.__notebookPath = {JsonSerializer.Serialize(filePath)};");
            sb.AppendLine($"window.__notebookDir = {JsonSerializer.Serialize(dirPath)};");
            sb.AppendLine(GetNotebookJs());
            sb.AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private string BuildCellHtml(NotebookCell cell, int index)
        {
            string source = cell.Source is JsonElement je && je.ValueKind == JsonValueKind.Array
                ? string.Join("", je.EnumerateArray().Select(e => e.GetString() ?? ""))
                : cell.Source?.ToString() ?? "";

            string cellType = cell.CellType ?? "code";
            var sb = new StringBuilder();
            sb.AppendLine($"<div class=\"cell cell-{cellType}\" data-cell-index=\"{index}\" data-cell-type=\"{cellType}\">");

            if (cellType == "markdown")
            {
                string html = RenderMarkdown(source);
                sb.AppendLine($"<div class=\"cell-input markdown-cell\" data-source=\"{HtmlAttrEncode(source)}\">");
                sb.AppendLine(html);
                sb.AppendLine("</div>");
            }
            else if (cellType == "raw")
            {
                sb.AppendLine($"<div class=\"cell-input raw-cell\" data-source=\"{HtmlAttrEncode(source)}\">");
                sb.AppendLine($"<pre>{HtmlEncode(source)}</pre>");
                sb.AppendLine("</div>");
            }
            else
            {
                sb.AppendLine($"<div class=\"cell-input code-cell\">");
                sb.AppendLine($"<div class=\"cell-input-area\" contenteditable=\"true\" spellcheck=\"false\" data-source=\"{HtmlAttrEncode(source)}\">");
                sb.AppendLine($"<pre>{HtmlEncode(source)}</pre>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-run\" title=\"Run\">▶</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-run-below\" title=\"Run Below\">▶|</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"Delete\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-output\"></div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div>");
            return sb.ToString();
        }

        private static string RenderMarkdown(string source)
        {
            string html = SimpleMarkdownToHtml(source);
            return html;
        }

        private static string SimpleMarkdownToHtml(string md)
        {
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            bool inList = false;
            bool inOl = false;

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();

                if (line.StartsWith("# "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h1>{InlineMd(line.Substring(2))}</h1>");
                }
                else if (line.StartsWith("## "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h2>{InlineMd(line.Substring(3))}</h2>");
                }
                else if (line.StartsWith("### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h3>{InlineMd(line.Substring(4))}</h3>");
                }
                else if (line.StartsWith("#### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h4>{InlineMd(line.Substring(5))}</h4>");
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                    sb.AppendLine($"<li>{InlineMd(line.Substring(2))}</li>");
                }
                else if (line.Length > 0 && char.IsDigit(line[0]) && line.Contains(". "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (!inOl) { sb.AppendLine("<ol>"); inOl = true; }
                    int dot = line.IndexOf(". ");
                    sb.AppendLine($"<li>{InlineMd(line.Substring(dot + 2))}</li>");
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                }
                else
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<p>{InlineMd(line)}</p>");
                }
            }

            if (inList) sb.AppendLine("</ul>");
            if (inOl) sb.AppendLine("</ol>");
            return sb.ToString();
        }

        private static string InlineMd(string text)
        {
            text = HtmlEncode(text);
            text = ReplaceSimple(text, "**", "<strong>", "</strong>");
            text = ReplaceSimple(text, "*", "<em>", "</em>");
            text = ReplaceSimple(text, "`", "<code>", "</code>");
            return text;
        }

        private static string ReplaceSimple(string text, string delim, string open, string close)
        {
            var sb = new StringBuilder();
            bool openTag = true;
            int i = 0;
            while (i < text.Length)
            {
                int idx = text.IndexOf(delim, i, StringComparison.Ordinal);
                if (idx < 0)
                {
                    sb.Append(text.AsSpan(i));
                    break;
                }
                sb.Append(text.AsSpan(i, idx - i));
                sb.Append(openTag ? open : close);
                openTag = !openTag;
                i = idx + delim.Length;
            }
            return sb.ToString();
        }

        private string BuildErrorHtml(string message)
        {
            return $"<!DOCTYPE html><html><head><meta charset=\"UTF-8\"></head><body><div style=\"padding:24px;color:#d33;font-family:sans-serif;\">{_getString("JupyterNotebookLoadError", "노트북을 불러올 수 없습니다.")}<br/><pre>{HtmlEncode(message)}</pre></div></body></html>";
        }

        private static string HtmlEncode(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }

        private static string HtmlAttrEncode(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }

        private static string GetNotebookCss()
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
#notebook-container { max-width: 1000px; margin: 0 auto; padding: 16px; }
#notebook-header {
    display: flex; justify-content: space-between; align-items: center;
    padding: 12px 0; border-bottom: 2px solid var(--nb-border); margin-bottom: 16px;
    position: sticky; top: 0; background: var(--nb-bg); z-index: 100;
}
.notebook-title { font-size: 18px; font-weight: 600; }
#notebook-toolbar { display: flex; gap: 8px; }
.nb-btn {
    padding: 6px 14px; border: 1px solid var(--nb-border); border-radius: 4px;
    background: var(--nb-input-bg); color: var(--nb-fg); cursor: pointer;
    font-size: 13px; transition: background 0.15s;
}
.nb-btn:hover { background: var(--nb-accent); color: #fff; border-color: var(--nb-accent); }
#cells-container { display: flex; flex-direction: column; gap: 8px; }
.cell {
    border: 1px solid var(--nb-border); border-radius: 6px; overflow: hidden;
    position: relative;
}
.cell-code { background: var(--nb-input-bg); }
.cell-markdown { background: var(--nb-bg); padding: 12px 16px; }
.cell-raw { background: var(--nb-input-bg); padding: 12px 16px; }
.cell-input-area {
    padding: 10px 12px; min-height: 24px; font-family: 'Consolas', 'Courier New', monospace;
    font-size: 13.5px; white-space: pre-wrap; word-break: break-word; outline: none;
    line-height: 1.45; cursor: text;
}
.cell-input-area:focus { background: var(--nb-bg); box-shadow: inset 0 0 0 2px var(--nb-accent); }
.cell-input-area pre { white-space: pre-wrap; word-break: break-word; margin: 0; font-family: inherit; }
.cell pre { white-space: pre-wrap; word-break: break-word; margin: 0; font-family: 'Consolas', monospace; font-size: 13.5px; }
.cell-toolbar {
    display: flex; gap: 4px; padding: 4px 8px; background: var(--nb-input-bg);
    border-top: 1px solid var(--nb-border);
}
.cell-btn {
    padding: 3px 8px; border: none; border-radius: 3px; background: transparent;
    color: var(--nb-fg); cursor: pointer; font-size: 12px; opacity: 0.6;
}
.cell-btn:hover { opacity: 1; background: var(--nb-accent); color: #fff; }
.cell-output {
    padding: 8px 12px; background: var(--nb-output-bg); border-top: 1px solid var(--nb-border);
    font-family: 'Consolas', monospace; font-size: 13px; white-space: pre-wrap; word-break: break-word;
    display: none; min-height: 0;
}
.cell-output.has-output { display: block; }
.cell-output .output-stdout { color: var(--nb-fg); white-space: pre-wrap; }
.cell-output .output-stderr { color: var(--nb-error); }
.cell-output .output-error { color: var(--nb-error); }
.cell-output .output-result { color: var(--nb-accent); font-style: italic; }
.cell-running .cell-run { background: var(--nb-accent); color: #fff; opacity: 1; }
h1, h2, h3, h4 { margin: 8px 0; }
h1 { font-size: 1.6em; }
h2 { font-size: 1.4em; }
h3 { font-size: 1.2em; }
h4 { font-size: 1.1em; }
ul, ol { padding-left: 24px; margin: 4px 0; }
p { margin: 4px 0; }
code { background: var(--nb-input-bg); padding: 1px 4px; border-radius: 3px; font-size: 0.9em; }
strong { font-weight: 700; }
";
        }

        private static string GetNotebookJs()
        {
            return @"
(function() {
    const container = document.getElementById('cells-container');
    const path = window.__notebookPath;

    function getCellSource(cellDiv) {
        const input = cellDiv.querySelector('.cell-input-area, .markdown-cell, .raw-cell');
        if (!input) return '';
        return input.innerText;
    }

    function setCellSource(cellDiv, source) {
        const input = cellDiv.querySelector('.cell-input-area');
        if (input) {
            input.innerHTML = '<pre>' + escapeHtml(source) + '</pre>';
        }
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function getCellType(cellDiv) {
        return cellDiv.getAttribute('data-cell-type');
    }

    function getNotebookJson() {
        const cells = [];
        container.querySelectorAll('.cell').forEach(cellDiv => {
            const type = getCellType(cellDiv);
            const source = getCellSource(cellDiv);
            const sourceLines = source.split('\n').map((l, i, arr) => i < arr.length - 1 ? l + '\n' : l);
            const outputs = [];
            if (type === 'code') {
                const outputDiv = cellDiv.querySelector('.cell-output');
                if (outputDiv) {
                    outputDiv.querySelectorAll('.output-entry').forEach(e => {
                        outputs.push(JSON.parse(e.getAttribute('data-output') || '{}'));
                    });
                }
                cells.push({ cell_type: 'code', source: sourceLines, outputs: outputs, metadata: {}, execution_count: null });
            } else if (type === 'markdown') {
                cells.push({ cell_type: 'markdown', source: sourceLines, metadata: {} });
            } else {
                cells.push({ cell_type: 'raw', source: sourceLines, metadata: {} });
            }
        });
        return JSON.stringify({ cells: cells, metadata: {}, nbformat: 4, nbformat_minor: 5 }, null, 1);
    }

    async function runCell(cellDiv) {
        const type = getCellType(cellDiv);
        if (type !== 'code') return;
        const source = getCellSource(cellDiv);
        const outputDiv = cellDiv.querySelector('.cell-output');
        if (!outputDiv) return;

        cellDiv.classList.add('cell-running');
        outputDiv.classList.add('has-output');
        outputDiv.innerHTML = '<span style=""color:#888;"">Running...</span>';

        try {
            const resp = await new Promise((resolve) => {
                window.chrome.webview.postMessage(JSON.stringify({
                    type: 'executeCell',
                    code: source,
                    cellIndex: parseInt(cellDiv.getAttribute('data-cell-index'))
                }));
                window.__pendingCellExecutions = window.__pendingCellExecutions || {};
                window.__pendingCellExecutions[cellDiv.getAttribute('data-cell-index')] = resolve;
            });

            let html = '';
            if (resp.stdout) html += '<span class=""output-stdout"">' + escapeHtml(resp.stdout) + '</span>';
            if (resp.stderr) html += '<span class=""output-stderr"">' + escapeHtml(resp.stderr) + '</span>';
            if (resp.result) html += '<span class=""output-result"">' + escapeHtml(resp.result) + '</span>';
            if (!html && resp.status === 'ok') html = '<span style=""color:#888;"">(no output)</span>';
            outputDiv.innerHTML = html;
        } catch (e) {
            outputDiv.innerHTML = '<span class=""output-error"">' + escapeHtml(String(e)) + '</span>';
        }
        cellDiv.classList.remove('cell-running');
    }

    async function saveNotebook() {
        const json = getNotebookJson();
        window.chrome.webview.postMessage(JSON.stringify({ type: 'saveNotebook', content: json }));
    }

    function createCell(type, source) {
        const div = document.createElement('div');
        div.className = 'cell cell-' + type;
        div.setAttribute('data-cell-type', type);
        const idx = container.children.length;
        div.setAttribute('data-cell-index', idx);

        if (type === 'markdown') {
            div.innerHTML = '<div class=""cell-input markdown-cell"" contenteditable=""true"" spellcheck=""false""><pre>' + escapeHtml(source || '') + '</pre></div>';
        } else if (type === 'raw') {
            div.innerHTML = '<div class=""cell-input raw-cell"" contenteditable=""true"" spellcheck=""false""><pre>' + escapeHtml(source || '') + '</pre></div>';
        } else {
            div.innerHTML = '<div class=""cell-input code-cell"">' +
                '<div class=""cell-input-area"" contenteditable=""true"" spellcheck=""false""><pre>' + escapeHtml(source || '') + '</pre></div>' +
                '<div class=""cell-toolbar"">' +
                '<button class=""cell-btn cell-run"" title=""Run"">▶</button>' +
                '<button class=""cell-btn cell-run-below"" title=""Run Below"">▶|</button>' +
                '<button class=""cell-btn cell-delete"" title=""Delete"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>' +
                '<div class=""cell-output""></div>' +
                '</div>';
        }
        return div;
    }

    function reindexCells() {
        container.querySelectorAll('.cell').forEach((c, i) => {
            c.setAttribute('data-cell-index', i);
        });
    }

    // Event delegation
    container.addEventListener('click', async (e) => {
        const btn = e.target.closest('.cell-btn');
        if (!btn) return;
        const cellDiv = btn.closest('.cell');
        if (!cellDiv) return;

        if (btn.classList.contains('cell-run')) {
            await runCell(cellDiv);
        } else if (btn.classList.contains('cell-run-below')) {
            const cells = Array.from(container.querySelectorAll('.cell'));
            const startIdx = parseInt(cellDiv.getAttribute('data-cell-index'));
            for (let i = startIdx; i < cells.length; i++) {
                if (getCellType(cells[i]) === 'code') {
                    await runCell(cells[i]);
                }
            }
        } else if (btn.classList.contains('cell-delete')) {
            cellDiv.remove();
            reindexCells();
        } else if (btn.classList.contains('cell-move-up')) {
            const prev = cellDiv.previousElementSibling;
            if (prev) {
                container.insertBefore(cellDiv, prev);
                reindexCells();
            }
        } else if (btn.classList.contains('cell-move-down')) {
            const next = cellDiv.nextElementSibling;
            if (next) {
                container.insertBefore(next, cellDiv);
                reindexCells();
            }
        }
    });

    // Keyboard shortcuts
    container.addEventListener('keydown', (e) => {
        const input = e.target.closest('.cell-input-area, .markdown-cell, .raw-cell');
        if (!input) return;
        const cellDiv = input.closest('.cell');
        if (!cellDiv) return;

        if (e.shiftKey && e.key === 'Enter') {
            e.preventDefault();
            if (getCellType(cellDiv) === 'code') {
                runCell(cellDiv).then(() => {
                    const next = cellDiv.nextElementSibling;
                    if (next) {
                        const nextInput = next.querySelector('.cell-input-area, .markdown-cell, .raw-cell');
                        if (nextInput) { nextInput.focus(); }
                    } else {
                        const newCell = createCell('code', '');
                        container.appendChild(newCell);
                        reindexCells();
                        newCell.querySelector('.cell-input-area').focus();
                    }
                });
            } else {
                const next = cellDiv.nextElementSibling;
                if (next) {
                    const nextInput = next.querySelector('.cell-input-area, .markdown-cell, .raw-cell');
                    if (nextInput) { nextInput.focus(); }
                }
            }
        }

        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            saveNotebook();
        }
    });

    // Toolbar buttons
    document.getElementById('btn-add-code').addEventListener('click', () => {
        const cell = createCell('code', '');
        container.appendChild(cell);
        reindexCells();
        cell.querySelector('.cell-input-area').focus();
    });

    document.getElementById('btn-add-markdown').addEventListener('click', () => {
        const cell = createCell('markdown', '');
        container.appendChild(cell);
        reindexCells();
        cell.querySelector('.markdown-cell').focus();
    });

    document.getElementById('btn-save').addEventListener('click', saveNotebook);

    document.getElementById('btn-run-all').addEventListener('click', async () => {
        const cells = Array.from(container.querySelectorAll('.cell'));
        for (const cell of cells) {
            if (getCellType(cell) === 'code') {
                await runCell(cell);
            }
        }
    });

    // Receive execution results from host
    window.__notebookReceiveResult = function(cellIndex, result) {
        const resolve = (window.__pendingCellExecutions || {})[String(cellIndex)];
        if (resolve) {
            resolve(result);
            delete window.__pendingCellExecutions[String(cellIndex)];
        }
    };

    // Receive save result from host
    window.__notebookSaveResult = function(success, message) {
        const btn = document.getElementById('btn-save');
        if (success) {
            btn.textContent = 'Saved!';
            setTimeout(() => { btn.textContent = 'Save'; }, 1500);
        } else {
            btn.textContent = 'Save Failed';
            setTimeout(() => { btn.textContent = 'Save'; }, 2000);
        }
    };
})();
";
        }
    }

    public sealed class NotebookDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("cells")]
        public List<NotebookCell>? Cells { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nbformat")]
        public int? NbFormat { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nbformat_minor")]
        public int? NbFormatMinor { get; set; }
    }

    public sealed class NotebookCell
    {
        [System.Text.Json.Serialization.JsonPropertyName("cell_type")]
        public string? CellType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public JsonElement? Source { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("outputs")]
        public List<JsonElement>? Outputs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("execution_count")]
        public JsonElement? ExecutionCount { get; set; }
    }
}