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
                json = json.TrimStart('\uFEFF');
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
            sb.AppendLine("<div class=\"notebook-header-top\">");
            sb.AppendLine($"<span class=\"notebook-title\">{HtmlEncode(fileName)}</span>");
            sb.AppendLine("<div id=\"notebook-toolbar\">");
            sb.AppendLine("<button id=\"btn-add-code\" class=\"nb-btn nb-btn-add\">+ Code</button>");
            sb.AppendLine("<button id=\"btn-add-markdown\" class=\"nb-btn nb-btn-add\">+ Markdown</button>");
            sb.AppendLine("<button id=\"btn-run-all\" class=\"nb-btn nb-btn-run\">Run All</button>");
            sb.AppendLine($"<button id=\"btn-variables\" class=\"nb-btn nb-btn-vars\" title=\"{HtmlAttrEncode(_getString("JupyterVariablesPanelTitle", "Variable Explorer"))}\">🔍 {HtmlEncode(_getString("JupyterVariablesButton", "Variables"))}</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"variables-panel\" style=\"display:none;\">");
            sb.AppendLine("<div class=\"vars-panel-header\">");
            sb.AppendLine($"<span class=\"vars-panel-title\">🔍 {HtmlEncode(_getString("JupyterVariablesPanelTitle", "Variable Explorer"))}</span>");
            sb.AppendLine("<div class=\"vars-panel-controls\">");
            sb.AppendLine($"<input type=\"text\" id=\"vars-filter-input\" placeholder=\"{HtmlAttrEncode(_getString("JupyterVariablesFilterPlaceholder", "Filter variables..."))}\" />");
            sb.AppendLine("<button id=\"btn-refresh-vars\" class=\"nb-btn nb-btn-sm\" title=\"Refresh\">🔄</button>");
            sb.AppendLine("<button id=\"btn-close-vars\" class=\"nb-btn nb-btn-sm\" title=\"Close\">✕</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"vars-table-container\">");
            sb.AppendLine("<table class=\"vars-table\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("<tr>");
            sb.AppendLine($"<th>{HtmlEncode(_getString("JupyterVariablesName", "Name"))}</th>");
            sb.AppendLine($"<th>{HtmlEncode(_getString("JupyterVariablesType", "Type"))}</th>");
            sb.AppendLine($"<th>{HtmlEncode(_getString("JupyterVariablesSize", "Size"))}</th>");
            sb.AppendLine($"<th>{HtmlEncode(_getString("JupyterVariablesValue", "Value"))}</th>");
            sb.AppendLine("</tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody id=\"vars-table-body\">");
            sb.AppendLine($"<tr><td colspan=\"4\" class=\"vars-empty\">{HtmlEncode(_getString("JupyterVariablesEmpty", "No active variables."))}</td></tr>");
            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"cells-container\">");

            int cellIndex = 0;
            if (doc.Cells != null)
            {
                foreach (var cell in doc.Cells)
                {
                    sb.AppendLine(BuildCellHtml(cell, cellIndex));
                    cellIndex++;
                }
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
                sb.AppendLine($"<div class=\"markdown-preview\">{html}</div>");
                sb.AppendLine($"<div class=\"cell-input-area markdown-editor\" contenteditable=\"true\" spellcheck=\"false\" style=\"display:none;\"><pre>{HtmlEncode(source)}</pre></div>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-run\" title=\"Render Markdown (Shift+Enter)\">▶ Render</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-edit\" title=\"Edit Markdown\">✎ Edit</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Code\">Code</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"Delete\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
            }
            else if (cellType == "raw")
            {
                sb.AppendLine($"<div class=\"cell-input raw-cell\" data-source=\"{HtmlAttrEncode(source)}\">");
                sb.AppendLine($"<div class=\"cell-input-area raw-editor\" contenteditable=\"true\" spellcheck=\"false\"><pre>{HtmlEncode(source)}</pre></div>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Code\">Code</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"Delete\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
            }
            else
            {
                sb.AppendLine($"<div class=\"cell-input code-cell\">");
                sb.AppendLine($"<div class=\"cell-input-area code-editor\" contenteditable=\"true\" spellcheck=\"false\" data-source=\"{HtmlAttrEncode(source)}\"><pre>{HtmlEncode(source)}</pre></div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-run\" title=\"Run (Shift+Enter)\">▶ Run</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-run-below\" title=\"Run Below\">▶|</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Markdown\">Markdown</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"Delete\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
                string outputHtml = RenderCellOutputs(cell.Outputs);
                string hasOutputClass = !string.IsNullOrEmpty(outputHtml) ? "cell-output has-output" : "cell-output";
                sb.AppendLine($"<div class=\"{hasOutputClass}\">{outputHtml}</div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div>");
            return sb.ToString();
        }

        private static string RenderCellOutputs(List<JsonElement>? outputs)
        {
            if (outputs == null || outputs.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var outElement in outputs)
            {
                if (outElement.ValueKind != JsonValueKind.Object) continue;
                string outputType = outElement.TryGetProperty("output_type", out var ot) ? ot.GetString() ?? "" : "";
                if (outputType == "stream")
                {
                    string text = GetTextOrArray(outElement, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        string name = outElement.TryGetProperty("name", out var n) ? n.GetString() ?? "stdout" : "stdout";
                        string cls = name == "stderr" ? "output-stderr" : "output-stdout";
                        if (text.Contains("<img ") || text.Contains("<table") || text.Contains("<!--MPL_START-->") || text.Contains("mpl-interactive-wrapper"))
                        {
                            sb.Append(text);
                        }
                        else
                        {
                            sb.Append($"<span class=\"{cls}\">{HtmlEncode(text)}</span>");
                        }
                    }
                }
                else if (outputType == "execute_result" || outputType == "display_data")
                {
                    if (outElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        if (data.TryGetProperty("image/png", out var imgPngEl))
                        {
                            string b64 = GetTextOrArrayFromElement(imgPngEl).Trim();
                            sb.Append($"<img src=\"data:image/png;base64,{b64}\" style=\"max-width:100%;height:auto;margin:8px 0;display:block;\" />");
                        }
                        else if (data.TryGetProperty("image/jpeg", out var imgJpgEl))
                        {
                            string b64 = GetTextOrArrayFromElement(imgJpgEl).Trim();
                            sb.Append($"<img src=\"data:image/jpeg;base64,{b64}\" style=\"max-width:100%;height:auto;margin:8px 0;display:block;\" />");
                        }
                        else if (data.TryGetProperty("text/html", out var htmlEl))
                        {
                            string htmlText = GetTextOrArrayFromElement(htmlEl);
                            sb.Append(htmlText);
                        }
                        else if (data.TryGetProperty("text/plain", out var plainEl))
                        {
                            string plainText = GetTextOrArrayFromElement(plainEl);
                            sb.Append($"<span class=\"output-result\">{HtmlEncode(plainText)}</span>");
                        }
                    }
                }
                else if (outputType == "error")
                {
                    string ename = outElement.TryGetProperty("ename", out var en) ? en.GetString() ?? "" : "";
                    string evalue = outElement.TryGetProperty("evalue", out var ev) ? ev.GetString() ?? "" : "";
                    sb.Append($"<span class=\"output-error\">{HtmlEncode(ename)}: {HtmlEncode(evalue)}</span>");
                }
            }
            return sb.ToString();
        }

        private static string GetTextOrArray(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var prop)) return string.Empty;
            return GetTextOrArrayFromElement(prop);
        }

        private static string GetTextOrArrayFromElement(JsonElement prop)
        {
            if (prop.ValueKind == JsonValueKind.Array)
            {
                return string.Join("", prop.EnumerateArray().Select(e => e.GetString() ?? ""));
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString() ?? string.Empty;
            }
            return prop.ToString();
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
            text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\(([^)]+)\)", @"<img src=""$2"" alt=""$1"" style=""max-width:100%;height:auto;display:inline-block;vertical-align:middle;margin:4px 0;"" />");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", @"<a href=""$2"" target=""_blank"">$1</a>");
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
    display: flex; flex-direction: column; gap: 10px;
    padding: 12px 0; border-bottom: 2px solid var(--nb-border); margin-bottom: 16px;
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
.cell-output {
    padding: 10px 14px; background: var(--nb-output-bg); border-top: 1px solid var(--nb-border);
    font-family: 'Consolas', monospace; font-size: 13.5px; line-height: 1.5; white-space: pre-wrap; word-break: break-word;
    display: none; min-height: 0; box-sizing: border-box;
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
    margin: 12px 0;
    border: 1px solid var(--nb-border);
    border-radius: 8px;
    background: var(--nb-output-bg);
    box-shadow: 0 2px 8px rgba(0,0,0,0.06);
    overflow: hidden;
    user-select: none;
    font-family: 'Segoe UI', -apple-system, sans-serif;
}
.mpl-toolbar {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px;
    background: var(--nb-input-bg);
    border-bottom: 1px solid var(--nb-border);
    font-size: 12px;
    flex-wrap: wrap;
}
.mpl-btn {
    border: 1px solid var(--nb-border);
    background: var(--nb-bg);
    color: var(--nb-fg);
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 12px;
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
    font-size: 12px;
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
    padding: 16px;
    background: var(--nb-output-bg);
    min-height: 200px;
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
    margin-left: 12px;
    pointer-events: none;
}
.mpl-cbar-img {
    display: block;
    max-width: 100%;
    height: auto;
}
.mpl-status-bar {
    padding: 4px 12px;
    font-size: 11px;
    color: #888;
    background: var(--nb-input-bg);
    border-top: 1px solid var(--nb-border);
    text-align: right;
}
";
        }

        private static string GetNotebookJs()
        {
            return @"
(function() {
    (function installNotebookShortcutBridge() {
        if (window.__txtAiEditorNotebookShortcutBridge) return;
        window.__txtAiEditorNotebookShortcutBridge = true;

        function post(name) {
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut', name });
                }
            } catch {}
        }

        function handleKeyDown(event) {
            const ctrl = !!(event.ctrlKey || event.metaKey);
            const alt = !!event.altKey;
            const shift = !!event.shiftKey;
            const key = String(event.key || '').toLowerCase();
            const code = String(event.code || '');

            let name = '';

            if (!ctrl && !alt) {
                if (key === 'f4' || code === 'F4') {
                    name = 'f4';
                } else if (key === 'f9' || code === 'F9') {
                    name = 'f9';
                } else if (key === 'f10' || code === 'F10') {
                    name = 'f10';
                } else if (key === 'f11' || code === 'F11') {
                    name = 'f11';
                } else if (key === 'f12' || code === 'F12') {
                    name = 'f12';
                }
            } else if (alt && !ctrl && !shift && (key === 'z' || code === 'KeyZ')) {
                name = 'wordWrap';
            } else if (ctrl && !alt) {
                if (key === '1' || code === 'Digit1' || code === 'Numpad1') {
                    name = 'toggleLeftPanel';
                } else if (key === '2' || code === 'Digit2' || code === 'Numpad2') {
                    name = 'toggleRightPanel';
                } else if (key === '3' || code === 'Digit3' || code === 'Numpad3') {
                    name = 'expandRightPanel';
                } else if (key === 'n' || code === 'KeyN') {
                    name = 'newTab';
                } else if (key === 's' || code === 'KeyS') {
                    name = shift ? 'saveAs' : 'save';
                } else if (key === 'o' || code === 'KeyO') {
                    name = 'open';
                } else if (key === 'w' || code === 'KeyW') {
                    name = 'closeTab';
                } else if (key === 'p' || code === 'KeyP') {
                    name = 'print';
                } else if (key === 'f' || code === 'KeyF') {
                    name = shift ? 'searchAll' : 'find';
                } else if (code === 'Backquote' || key === '`' || key === '~' || key === 'dead') {
                    name = 'terminal';
                }
            }

            if (!name) return;
            event.preventDefault();
            event.stopPropagation();
            if (event.stopImmediatePropagation) {
                event.stopImmediatePropagation();
            }
            post(name);
        }

        window.addEventListener('keydown', handleKeyDown, true);
        document.addEventListener('keydown', handleKeyDown, true);
    })();

    const container = document.getElementById('cells-container');
    const path = window.__notebookPath;
    let isDirtyState = false;

    function notifyModified() {
        if (!isDirtyState) {
            isDirtyState = true;
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'markDirty' }));
                }
            } catch (e) {}
        }
    }

    container.addEventListener('input', () => {
        notifyModified();
    });

    function renderMarkdownJs(md) {
        if (!md) return '';
        const lines = md.replace(/\r\n/g, '\n').split('\n');
        let html = '';
        let inList = false, inOl = false, inCodeBlock = false;
        let codeBuffer = [];

        for (let i = 0; i < lines.length; i++) {
            let line = lines[i];

            if (line.trim().startsWith('```')) {
                if (inCodeBlock) {
                    html += '<pre><code>' + escapeHtml(codeBuffer.join('\n')) + '</code></pre>';
                    codeBuffer = [];
                    inCodeBlock = false;
                } else {
                    if (inList) { html += '</ul>'; inList = false; }
                    if (inOl) { html += '</ol>'; inOl = false; }
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock) {
                codeBuffer.push(line);
                continue;
            }

            const trimmed = line.trimEnd();

            if (/^#\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<h1>' + inlineMdJs(trimmed.slice(2)) + '</h1>';
            } else if (/^##\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<h2>' + inlineMdJs(trimmed.slice(3)) + '</h2>';
            } else if (/^###\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<h3>' + inlineMdJs(trimmed.slice(4)) + '</h3>';
            } else if (/^####\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<h4>' + inlineMdJs(trimmed.slice(5)) + '</h4>';
            } else if (/^[-*]\s+/.test(trimmed)) {
                if (inOl) { html += '</ol>'; inOl = false; }
                if (!inList) { html += '<ul>'; inList = true; }
                html += '<li>' + inlineMdJs(trimmed.slice(2)) + '</li>';
            } else if (/^\d+\.\s+/.test(trimmed)) {
                if (inList) { html += '</ul>'; inList = false; }
                if (!inOl) { html += '<ol>'; inOl = true; }
                html += '<li>' + inlineMdJs(trimmed.replace(/^\d+\.\s+/, '')) + '</li>';
            } else if (trimmed === '---' || trimmed === '***') {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<hr/>';
            } else if (trimmed.length > 0) {
                if (inList) { html += '</ul>'; inList = false; }
                if (inOl) { html += '</ol>'; inOl = false; }
                html += '<p>' + inlineMdJs(trimmed) + '</p>';
            }
        }
        if (inList) html += '</ul>';
        if (inOl) html += '</ol>';
        if (inCodeBlock) html += '<pre><code>' + escapeHtml(codeBuffer.join('\n')) + '</code></pre>';
        return html;
    }

    function inlineMdJs(str) {
        let s = escapeHtml(str);
        s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img src=""$2"" alt=""$1"" style=""max-width:100%;height:auto;display:inline-block;vertical-align:middle;margin:4px 0;"" />');
        s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href=""$2"" target=""_blank"" rel=""noopener"">$1</a>');
        s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/\*([^*]+)\*/g, '<em>$1</em>');
        s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
        return s;
    }

    function getEditorText(editor) {
        if (!editor) return '';
        const pre = editor.querySelector('pre') || editor;
        const clone = pre.cloneNode(true);
        clone.querySelectorAll('br').forEach(br => br.replaceWith(document.createTextNode('\n')));
        clone.querySelectorAll('div, p').forEach(div => {
            if (div.previousSibling) {
                div.before(document.createTextNode('\n'));
            }
        });
        const text = clone.textContent || '';
        return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    }

    function getCellSource(cellDiv) {
        const type = getCellType(cellDiv);
        if (type === 'markdown') {
            const editor = cellDiv.querySelector('.markdown-editor');
            if (editor) return getEditorText(editor);
            return cellDiv.getAttribute('data-source') || '';
        } else if (type === 'raw') {
            const editor = cellDiv.querySelector('.raw-editor, .cell-input-area');
            if (editor) return getEditorText(editor);
            return cellDiv.getAttribute('data-source') || '';
        } else {
            const input = cellDiv.querySelector('.cell-input-area');
            if (input) return getEditorText(input);
            return cellDiv.getAttribute('data-source') || '';
        }
    }

    function renderMarkdownCell(cellDiv) {
        if (getCellType(cellDiv) !== 'markdown') return;
        const editor = cellDiv.querySelector('.markdown-editor');
        const preview = cellDiv.querySelector('.markdown-preview');
        if (!editor || !preview) return;

        const source = getEditorText(editor);
        cellDiv.setAttribute('data-source', source);
        preview.innerHTML = renderMarkdownJs(source) || '<em style=""color:#888;"">(Empty Markdown Cell)</em>';
        editor.style.display = 'none';
        preview.style.display = 'block';
    }

    function editMarkdownCell(cellDiv) {
        if (getCellType(cellDiv) !== 'markdown') return;
        const editor = cellDiv.querySelector('.markdown-editor');
        const preview = cellDiv.querySelector('.markdown-preview');
        if (!editor || !preview) return;

        editor.style.display = 'block';
        preview.style.display = 'none';
        editor.focus();
    }

    function insertMarkdownFormatting(cellDiv, formatType) {
        if (getCellType(cellDiv) !== 'markdown') return;
        editMarkdownCell(cellDiv);
        const editor = cellDiv.querySelector('.markdown-editor');
        if (!editor) return;

        let prefix = '', suffix = '', defaultText = '';
        switch (formatType) {
            case 'bold':
                prefix = '**'; suffix = '**'; defaultText = 'bold text';
                break;
            case 'italic':
                prefix = '*'; suffix = '*'; defaultText = 'italic text';
                break;
            case 'heading':
                prefix = '# '; suffix = ''; defaultText = 'Heading';
                break;
            case 'link':
                prefix = '['; suffix = '](https://)'; defaultText = 'link text';
                break;
            case 'image':
                prefix = '!['; suffix = '](image_url)'; defaultText = 'image alt';
                break;
        }

        const sel = window.getSelection();
        let selectedText = '';
        let range = null;

        if (sel && sel.rangeCount > 0 && editor.contains(sel.anchorNode)) {
            range = sel.getRangeAt(0);
            selectedText = range.toString();
        }

        const textToWrap = selectedText || defaultText;
        const inserted = prefix + textToWrap + suffix;

        if (range) {
            range.deleteContents();
            const textNode = document.createTextNode(inserted);
            range.insertNode(textNode);
            range.setStartAfter(textNode);
            range.setEndAfter(textNode);
            sel.removeAllRanges();
            sel.addRange(range);
        } else {
            const currentText = editor.innerText || '';
            const needNewline = currentText.length > 0 && !currentText.endsWith('\n');
            editor.innerHTML = '<pre>' + escapeHtml(currentText + (needNewline ? '\n' : '') + inserted) + '</pre>';
        }
        editor.focus();
        notifyModified();
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

    function renderStdoutWithImages(text) {
        if (!text) return '';
        const parts = text.split(/(<!--MPL_START-->[\s\S]*?<!--MPL_END-->|<img\s+src=""data:image\/[^"">]+""?[^>]*\/>)/gi);
        let html = '';
        for (let i = 0; i < parts.length; i++) {
            const part = parts[i];
            if (!part) continue;
            if (part.startsWith('<!--MPL_START-->') || /^<img\s+src=""data:image\//i.test(part)) {
                html += part;
            } else {
                html += '<span class=""output-stdout"">' + escapeHtml(part) + '</span>';
            }
        }
        return html;
    }

    async function runCell(cellDiv) {
        const type = getCellType(cellDiv);
        if (type === 'markdown') {
            renderMarkdownCell(cellDiv);
            return;
        }
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
            if (resp.stdout) html += renderStdoutWithImages(resp.stdout);
            if (resp.stderr) html += '<span class=""output-stderr"">' + escapeHtml(resp.stderr) + '</span>';
            if (resp.result) html += '<span class=""output-result"">' + escapeHtml(resp.result) + '</span>';
            if (html) {
                outputDiv.classList.add('has-output');
                outputDiv.innerHTML = html;
                setTimeout(initMplInteractiveContainers, 50);
            } else {
                outputDiv.classList.remove('has-output');
                outputDiv.innerHTML = '';
            }
            notifyModified();
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
            const html = renderMarkdownJs(source || '');
            div.innerHTML = '<div class=""cell-input markdown-cell"" data-source=""' + escapeHtml(source || '') + '"">' +
                '<div class=""markdown-preview"" style=""display:none;"">' + html + '</div>' +
                '<div class=""cell-input-area markdown-editor"" contenteditable=""true"" spellcheck=""false"" style=""display:block;""><pre>' + escapeHtml(source || '') + '</pre></div>' +
                '</div>' +
                '<div class=""cell-toolbar"">' +
                '<button class=""cell-btn cell-run"" title=""Render Markdown (Shift+Enter)"">▶ Render</button>' +
                '<button class=""cell-btn cell-edit"" title=""Edit Markdown"">✎ Edit</button>' +
                '<button class=""cell-btn cell-toggle-type"" title=""Switch to Code"">Code</button>' +
                '<button class=""cell-btn cell-delete"" title=""Delete"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>';
        } else {
            div.innerHTML = '<div class=""cell-input code-cell"">' +
                '<div class=""cell-input-area code-editor"" contenteditable=""true"" spellcheck=""false"" data-source=""' + escapeHtml(source || '') + '""><pre>' + escapeHtml(source || '') + '</pre></div>' +
                '<div class=""cell-toolbar"">' +
                '<button class=""cell-btn cell-run"" title=""Run (Shift+Enter)"">▶ Run</button>' +
                '<button class=""cell-btn cell-run-below"" title=""Run Below"">▶|</button>' +
                '<button class=""cell-btn cell-toggle-type"" title=""Switch to Markdown"">Markdown</button>' +
                '<button class=""cell-btn cell-delete"" title=""Delete"">✕</button>' +
                '<button class=""cell-btn cell-move-up"" title=""Move Up"">↑</button>' +
                '<button class=""cell-btn cell-move-down"" title=""Move Down"">↓</button>' +
                '</div>' +
                '<div class=""cell-output""></div>' +
                '</div>';
        }
        return div;
    }

    let lastActiveMarkdownCell = null;
    let lastActiveMarkdownRange = null;

    container.addEventListener('focusin', (e) => {
        const cellDiv = e.target.closest('.cell');
        if (cellDiv && getCellType(cellDiv) === 'markdown') {
            lastActiveMarkdownCell = cellDiv;
        }
    });

    document.addEventListener('selectionchange', () => {
        const active = document.activeElement;
        if (active && active.classList && active.classList.contains('markdown-editor')) {
            const sel = window.getSelection();
            if (sel && sel.rangeCount > 0 && active.contains(sel.anchorNode)) {
                lastActiveMarkdownRange = sel.getRangeAt(0).cloneRange();
            }
        }
    });

    container.addEventListener('focusout', (e) => {
        const editor = e.target.closest('.markdown-editor');
        if (!editor) return;
        const cellDiv = editor.closest('.cell');
        if (!cellDiv || getCellType(cellDiv) !== 'markdown') return;

        setTimeout(() => {
            if (!document.hasFocus()) {
                return;
            }
            const active = document.activeElement;
            if (active && cellDiv.contains(active)) {
                return;
            }
            renderMarkdownCell(cellDiv);
        }, 150);
    });

    function focusEditorAtEnd(editor) {
        editor.focus();
        const sel = window.getSelection();
        if (sel) {
            const range = document.createRange();
            range.selectNodeContents(editor);
            range.collapse(false);
            sel.removeAllRanges();
            sel.addRange(range);
        }
    }

    function focusEditorAtLine(editor, lineIndex) {
        editor.focus();
        const sel = window.getSelection();
        if (!sel) return;
        const pre = editor.querySelector('pre') || editor;
        const textNode = pre.firstChild;
        if (!textNode || textNode.nodeType !== Node.TEXT_NODE) {
            focusEditorAtEnd(editor);
            return;
        }
        const text = textNode.nodeValue || '';
        const lines = text.split('\n');
        let offset = 0;
        const target = Math.min(Math.max(0, lineIndex), lines.length - 1);
        for (let i = 0; i <= target; i++) {
            offset += lines[i].length;
            if (i < target) offset += 1;
        }
        offset = Math.min(offset, text.length);
        const range = document.createRange();
        range.setStart(textNode, offset);
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
    }

    function cycleHeadingText(lineText) {
        const match = lineText.match(/^(#{1,6})\s*(.*)/);
        if (!match) {
            return '# ' + lineText;
        }
        const hashes = match[1];
        const content = match[2];
        if (hashes.length < 6) {
            return '#'.repeat(hashes.length + 1) + ' ' + content;
        } else {
            return content;
        }
    }

    function toggleLinePrefixText(lineText, prefix) {
        const listMatch = lineText.match(/^([-*+]\s+|\d+\.\s+|>\s*|- \[\s*\]\s*)/);
        if (lineText.startsWith(prefix)) {
            return lineText.slice(prefix.length);
        } else if (listMatch) {
            return prefix + lineText.slice(listMatch[0].length);
        } else {
            return prefix + lineText;
        }
    }

    function getLineIndexFromRange(editor, range) {
        if (!range || !editor) return 0;
        try {
            const preRange = document.createRange();
            preRange.selectNodeContents(editor);
            preRange.setEnd(range.startContainer, range.startOffset);
            const tempDiv = document.createElement('div');
            tempDiv.style.position = 'absolute';
            tempDiv.style.left = '-9999px';
            tempDiv.style.visibility = 'hidden';
            tempDiv.appendChild(preRange.cloneContents());
            document.body.appendChild(tempDiv);
            tempDiv.querySelectorAll('br').forEach(br => br.replaceWith(document.createTextNode('\n')));
            tempDiv.querySelectorAll('div, p').forEach(div => div.before(document.createTextNode('\n')));
            const textBefore = (tempDiv.textContent || tempDiv.innerText || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
            tempDiv.remove();
            const lineIndex = textBefore.split('\n').length - 1;
            return Math.max(0, lineIndex);
        } catch {
            return 0;
        }
    }

    function cycleHeadingInEditor(editor, range) {
        let text = getEditorText(editor);
        const lines = text.split('\n');
        let targetIndex = 0;
        if (range) {
            targetIndex = getLineIndexFromRange(editor, range);
        }
        if (targetIndex >= 0 && targetIndex < lines.length) {
            lines[targetIndex] = cycleHeadingText(lines[targetIndex]);
            editor.innerHTML = '<pre>' + escapeHtml(lines.join('\n')) + '</pre>';
            focusEditorAtLine(editor, targetIndex);
        }
    }

    function togglePrefixInEditor(editor, range, prefix) {
        let text = getEditorText(editor);
        const lines = text.split('\n');
        let targetIndex = 0;
        if (range) {
            targetIndex = getLineIndexFromRange(editor, range);
        }
        if (targetIndex >= 0 && targetIndex < lines.length) {
            lines[targetIndex] = toggleLinePrefixText(lines[targetIndex], prefix);
            editor.innerHTML = '<pre>' + escapeHtml(lines.join('\n')) + '</pre>';
            focusEditorAtLine(editor, targetIndex);
        }
    }

    function toggleWrapperInEditor(editor, range, opening, closing) {
        closing = closing || opening;
        if (range) {
            const selectedText = range.toString();
            if (selectedText.length > 0) {
                if (selectedText.length >= opening.length + closing.length &&
                    selectedText.startsWith(opening) && selectedText.endsWith(closing)) {
                    const inner = selectedText.slice(opening.length, selectedText.length - closing.length);
                    range.deleteContents();
                    const innerNode = document.createTextNode(inner);
                    range.insertNode(innerNode);
                    range.setStart(innerNode, 0);
                    range.setEnd(innerNode, inner.length);
                    const sel = window.getSelection();
                    sel.removeAllRanges();
                    sel.addRange(range);
                    return;
                }
                const nodeText = range.startContainer.textContent || '';
                const startCol = range.startOffset;
                const endCol = range.endOffset;
                if (startCol >= opening.length && endCol + closing.length <= nodeText.length &&
                    nodeText.slice(startCol - opening.length, startCol) === opening &&
                    nodeText.slice(endCol, endCol + closing.length) === closing) {
                    const node = range.startContainer;
                    const before = nodeText.slice(0, startCol - opening.length);
                    const mid = nodeText.slice(startCol, endCol);
                    const after = nodeText.slice(endCol + closing.length);
                    node.textContent = before + mid + after;
                    const sel = window.getSelection();
                    const newRange = document.createRange();
                    newRange.setStart(node, before.length);
                    newRange.setEnd(node, before.length + mid.length);
                    sel.removeAllRanges();
                    sel.addRange(newRange);
                    return;
                }
                range.deleteContents();
                const wrappedNode = document.createTextNode(opening + selectedText + closing);
                range.insertNode(wrappedNode);
                range.setStart(wrappedNode, opening.length);
                range.setEnd(wrappedNode, opening.length + selectedText.length);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            } else {
                const nodeText = range.startContainer.textContent || '';
                const caret = range.startOffset;
                if (caret >= opening.length && caret + closing.length <= nodeText.length &&
                    nodeText.slice(caret - opening.length, caret) === opening &&
                    nodeText.slice(caret, caret + closing.length) === closing) {
                    const node = range.startContainer;
                    const before = nodeText.slice(0, caret - opening.length);
                    const after = nodeText.slice(caret + closing.length);
                    node.textContent = before + after;
                    const sel = window.getSelection();
                    const newRange = document.createRange();
                    newRange.setStart(node, before.length);
                    newRange.collapse(true);
                    sel.removeAllRanges();
                    sel.addRange(newRange);
                    return;
                }

                const pNode = document.createTextNode(opening);
                const sNode = document.createTextNode(closing);
                range.deleteContents();
                if (closing) range.insertNode(sNode);
                range.insertNode(pNode);
                range.setStartAfter(pNode);
                range.collapse(true);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            }
        } else {
            const currentText = getEditorText(editor);
            const needNewline = currentText.length > 0 && !currentText.endsWith('\n');
            editor.innerHTML = '<pre>' + escapeHtml(currentText + (needNewline ? '\n' : '') + opening + closing) + '</pre>';
            focusEditorAtEnd(editor);
        }
    }

    function applyMarkdownCommandToCell(cellDiv, command, color) {
        if (getCellType(cellDiv) !== 'markdown') return;
        editMarkdownCell(cellDiv);
        const editor = cellDiv.querySelector('.markdown-editor');
        if (!editor) return;

        editor.focus();
        const sel = window.getSelection();
        let range = null;

        if (sel && sel.rangeCount > 0 && editor.contains(sel.anchorNode)) {
            range = sel.getRangeAt(0);
        } else if (lastActiveMarkdownRange && editor.contains(lastActiveMarkdownRange.anchorNode)) {
            range = lastActiveMarkdownRange;
        }

        if (command === 'heading') {
            cycleHeadingInEditor(editor, range);
            return;
        }

        if (command === 'ul') { togglePrefixInEditor(editor, range, '- '); return; }
        if (command === 'ol') { togglePrefixInEditor(editor, range, '1. '); return; }
        if (command === 'quote') { togglePrefixInEditor(editor, range, '> '); return; }
        if (command === 'task') { togglePrefixInEditor(editor, range, '- [ ] '); return; }

        let opening = '', closing = '';
        switch (command) {
            case 'bold': opening = '**'; closing = '**'; break;
            case 'italic': opening = '*'; closing = '*'; break;
            case 'underline': opening = '<u>'; closing = '</u>'; break;
            case 'highlight': opening = '<mark>'; closing = '</mark>'; break;
            case 'inlineCode': opening = '`'; closing = '`'; break;
            case 'codeBlock': opening = '```\n'; closing = '\n```'; break;
            case 'link': opening = '['; closing = '](https://)'; break;
            case 'image': opening = '!['; closing = '](image_url)'; break;
            case 'table': opening = '\n| Header 1 | Header 2 |\n| --- | --- |\n|  |  |\n'; closing = ''; break;
            case 'arrow': opening = '-> '; closing = ''; break;
            case 'textColor':
                if (color) {
                    opening = '<span style=""color:' + color + ';"">';
                    closing = '</span>';
                }
                break;
            default:
                return;
        }

        if (!opening && !closing) return;

        toggleWrapperInEditor(editor, range, opening, closing);
        notifyModified();
    }

    window.addEventListener('appMarkdownCommand', (e) => {
        const detail = e.detail || {};
        const cmd = detail.command;
        const color = detail.color;
        if (!cmd) return;

        let activeCell = (document.activeElement && document.activeElement.closest) ? document.activeElement.closest('.cell') : null;
        if (!activeCell || getCellType(activeCell) !== 'markdown') {
            activeCell = lastActiveMarkdownCell || container.querySelector('.cell[data-cell-type=""markdown""]');
        }
        if (activeCell) {
            applyMarkdownCommandToCell(activeCell, cmd, color);
            notifyModified();
        }
    });

    function reindexCells() {
        container.querySelectorAll('.cell').forEach((c, i) => {
            c.setAttribute('data-cell-index', i);
        });
    }

    // Double click to edit markdown preview
    container.addEventListener('dblclick', (e) => {
        const preview = e.target.closest('.markdown-preview');
        if (preview) {
            const cellDiv = preview.closest('.cell');
            if (cellDiv) editMarkdownCell(cellDiv);
        }
    });

    // Event delegation
    container.addEventListener('click', async (e) => {
        const preview = e.target.closest('.markdown-preview');
        if (preview) {
            const cellDiv = preview.closest('.cell');
            if (cellDiv) {
                editMarkdownCell(cellDiv);
                return;
            }
        }

        const fmtBtn = e.target.closest('.cell-md-fmt');
        if (fmtBtn) {
            const cellDiv = fmtBtn.closest('.cell');
            const fmt = fmtBtn.getAttribute('data-fmt');
            if (cellDiv && fmt) {
                insertMarkdownFormatting(cellDiv, fmt);
                notifyModified();
                return;
            }
        }

        const btn = e.target.closest('.cell-btn');
        if (!btn) return;
        const cellDiv = btn.closest('.cell');
        if (!cellDiv) return;

        if (btn.classList.contains('cell-run')) {
            if (getCellType(cellDiv) === 'markdown') {
                renderMarkdownCell(cellDiv);
            } else {
                await runCell(cellDiv);
            }
        } else if (btn.classList.contains('cell-edit')) {
            if (getCellType(cellDiv) === 'markdown') {
                editMarkdownCell(cellDiv);
            }
        } else if (btn.classList.contains('cell-toggle-type')) {
            const currentType = getCellType(cellDiv);
            const source = getCellSource(cellDiv);
            const newType = currentType === 'markdown' ? 'code' : 'markdown';
            const newCell = createCell(newType, source);
            cellDiv.replaceWith(newCell);
            reindexCells();
            if (newType === 'markdown') {
                editMarkdownCell(newCell);
            } else {
                const editor = newCell.querySelector('.cell-input-area');
                if (editor) editor.focus();
            }
            notifyModified();
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
            notifyModified();
        } else if (btn.classList.contains('cell-move-up')) {
            const prev = cellDiv.previousElementSibling;
            if (prev) {
                container.insertBefore(cellDiv, prev);
                reindexCells();
                notifyModified();
            }
        } else if (btn.classList.contains('cell-move-down')) {
            const next = cellDiv.nextElementSibling;
            if (next) {
                container.insertBefore(next, cellDiv);
                reindexCells();
                notifyModified();
            }
        }
    });

    // Keyboard shortcuts
    container.addEventListener('keydown', (e) => {
        const input = e.target.closest('.cell-input-area, .markdown-editor, .raw-cell');
        if (!input) return;
        const cellDiv = input.closest('.cell');
        if (!cellDiv) return;

        if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.altKey) {
            const editor = input.closest('.markdown-editor');
            if (editor) {
                e.preventDefault();
                document.execCommand('insertLineBreak');
                notifyModified();
                return;
            }
        }

        if (e.shiftKey && e.key === 'Enter') {
            e.preventDefault();
            const type = getCellType(cellDiv);
            if (type === 'markdown') {
                renderMarkdownCell(cellDiv);
                let next = cellDiv.nextElementSibling;
                if (!next) {
                    next = createCell('code', '');
                    container.appendChild(next);
                    reindexCells();
                }
                const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                if (focusTarget) focusTarget.focus();
            } else if (type === 'code') {
                runCell(cellDiv).then(() => {
                    let next = cellDiv.nextElementSibling;
                    if (!next) {
                        next = createCell('code', '');
                        container.appendChild(next);
                        reindexCells();
                    }
                    const focusTarget = next.querySelector('.cell-input-area, .markdown-editor');
                    if (focusTarget) focusTarget.focus();
                });
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
        notifyModified();
    });

    document.getElementById('btn-add-markdown').addEventListener('click', () => {
        const cell = createCell('markdown', '');
        container.appendChild(cell);
        reindexCells();
        editMarkdownCell(cell);
        notifyModified();
    });

    const btnSave = document.getElementById('btn-save');
    if (btnSave) btnSave.addEventListener('click', saveNotebook);

    document.getElementById('btn-run-all').addEventListener('click', async () => {
        const cells = Array.from(container.querySelectorAll('.cell'));
        for (const cell of cells) {
            if (getCellType(cell) === 'code') {
                await runCell(cell);
            }
        }
    });

    // Variables panel UI & handler logic
    window.__currentVariables = window.__currentVariables || [];

    function renderVariablesTable() {
        const tbody = document.getElementById('vars-table-body');
        if (!tbody) return;
        const filterInput = document.getElementById('vars-filter-input');
        const filterText = (filterInput ? filterInput.value || '' : '').toLowerCase().trim();

        const vars = (window.__currentVariables || []).filter(v => {
            if (!filterText) return true;
            return (v.name || '').toLowerCase().includes(filterText) || (v.type || '').toLowerCase().includes(filterText);
        });

        if (vars.length === 0) {
            const emptyMsg = filterText ? 'No matching variables.' : 'No active variables.';
            tbody.innerHTML = '<tr><td colspan=""4"" class=""vars-empty"">' + escapeHtml(emptyMsg) + '</td></tr>';
            return;
        }

        let html = '';
        for (let i = 0; i < vars.length; i++) {
            const v = vars[i];
            html += '<tr>' +
                '<td><strong>' + escapeHtml(v.name || '') + '</strong></td>' +
                '<td><code>' + escapeHtml(v.type || '') + '</code></td>' +
                '<td>' + escapeHtml(v.size || '-') + '</td>' +
                '<td title=""' + escapeHtml(v.value || '') + '"">' + escapeHtml(v.value || '') + '</td>' +
                '</tr>';
        }
        tbody.innerHTML = html;
    }

    const btnVars = document.getElementById('btn-variables');
    const varsPanel = document.getElementById('variables-panel');
    const btnRefreshVars = document.getElementById('btn-refresh-vars');
    const btnCloseVars = document.getElementById('btn-close-vars');
    const varsFilterInput = document.getElementById('vars-filter-input');

    if (btnVars && varsPanel) {
        btnVars.addEventListener('click', (e) => {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            if (varsPanel.style.display === 'none' || !varsPanel.style.display) {
                varsPanel.style.display = 'block';
                renderVariablesTable();
                try {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'getVariables' }));
                } catch (ex) {}
            } else {
                varsPanel.style.display = 'none';
            }
        });
    }

    if (btnRefreshVars) {
        btnRefreshVars.addEventListener('click', () => {
            try {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'getVariables' }));
            } catch (e) {}
        });
    }

    if (btnCloseVars && varsPanel) {
        btnCloseVars.addEventListener('click', () => {
            varsPanel.style.display = 'none';
        });
    }

    if (varsFilterInput) {
        varsFilterInput.addEventListener('input', () => {
            renderVariablesTable();
        });
    }

    // Receive variables from host
    window.__notebookReceiveVariables = function(vars) {
        if (Array.isArray(vars)) {
            window.__currentVariables = vars;
            renderVariablesTable();
        }
    };

    // Receive execution results from host
    window.__notebookReceiveResult = function(cellIndex, result, vars) {
        const resolve = (window.__pendingCellExecutions || {})[String(cellIndex)];
        if (resolve) {
            resolve(result);
            delete window.__pendingCellExecutions[String(cellIndex)];
        }
        if (Array.isArray(vars)) {
            window.__currentVariables = vars;
            renderVariablesTable();
        }
    };

    // Receive plot view update from host (3D re-render)
    window.__notebookReceivePlotUpdate = function(figId, html) {
        if (!figId || !html) return;
        const wrapper = document.querySelector('.mpl-interactive-wrapper[data-fig-id=""' + figId + '""]');
        if (wrapper && wrapper.__on3DUpdateReceived) {
            wrapper.__on3DUpdateReceived(html);
        }
    };

    // Receive save result from host
    window.__notebookSaveResult = function(success, message) {
        const btn = document.getElementById('btn-save');
        if (success) {
            isDirtyState = false;
            if (btn) {
                btn.textContent = 'Saved!';
                setTimeout(() => { btn.textContent = 'Save'; }, 1500);
            }
        } else {
            if (btn) {
                btn.textContent = 'Save Failed';
                setTimeout(() => { btn.textContent = 'Save'; }, 2000);
            }
        }
    };
    function initMplInteractiveContainers() {
        document.querySelectorAll('.cell-output').forEach(function(outputDiv) {
            outputDiv.querySelectorAll('img[src^=""data:image/png""]').forEach(function(img) {
                if (!img.closest('.mpl-interactive-wrapper')) {
                    const wrapper = document.createElement('div');
                    wrapper.className = 'mpl-interactive-wrapper';
                    wrapper.setAttribute('data-mpl', 'true');
                    wrapper.setAttribute('data-is-3d', 'false');
                    wrapper.innerHTML = 
                        '<div class=""mpl-toolbar"">' +
                            '<button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button>' +
                            '<button class=""mpl-btn mpl-btn-zoom"" title=""Toggle Zoom Mode (Scroll Wheel)"">🔍 Zoom</button>' +
                            '<button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button>' +
                        '</div>' +
                        '<div class=""mpl-viewport"">' +
                            '<div class=""mpl-plot-layer""></div>' +
                        '</div>' +
                        '<div class=""mpl-status-bar"">' +
                            '<span>Drag: Pan | Enable 🔍 Zoom + Wheel to Zoom</span>' +
                        '</div>';
                    img.parentNode.insertBefore(wrapper, img);
                    const plotLayer = wrapper.querySelector('.mpl-plot-layer');
                    img.className = 'mpl-plot-img';
                    plotLayer.appendChild(img);
                }
            });

            outputDiv.querySelectorAll('.mpl-interactive-wrapper').forEach(function(wrapper) {
                if (wrapper.__mplInited) return;
                wrapper.__mplInited = true;

                const viewport = wrapper.querySelector('.mpl-viewport');
                const plotLayer = wrapper.querySelector('.mpl-plot-layer');
                const btnReset = wrapper.querySelector('.mpl-btn-reset');
                const btnZoom = wrapper.querySelector('.mpl-btn-zoom');
                const btnDownload = wrapper.querySelector('.mpl-btn-download');
                const sliderY = wrapper.querySelector('.mpl-rotate-y-slider');
                const angleValY = wrapper.querySelector('.mpl-angle-val-y');
                const sliderX = wrapper.querySelector('.mpl-rotate-x-slider');
                const angleValX = wrapper.querySelector('.mpl-angle-val-x');

                let isZoomActive = false;

                const is3D = wrapper.getAttribute('data-is-3d') === 'true';
                const figId = wrapper.getAttribute('data-fig-id');
                let initElev = parseInt(wrapper.getAttribute('data-elev') || '30') || 30;
                let initAzim = parseInt(wrapper.getAttribute('data-azim') || '-60') || -60;
                let elev = initElev;
                let azim = initAzim;

                let panX = 0, panY = 0, scale = 1;
                let isDragging = false;
                let dragBtn = -1;
                let startX = 0, startY = 0;
                let startPanX = 0, startPanY = 0;
                let startElev = elev, startAzim = azim;

                let is3DInFlight = false;
                let pending3DElevAzim = null;

                /* 2D cumulative data state */
                let dataPanFracX = 0, dataPanFracY = 0, dataZoom = 1;
                let is2DInFlight = false;
                let pending2DState = null;

                function send3DViewRequest(eVal, aVal) {
                    if (!is3D || !figId) return;
                    if (is3DInFlight) {
                        pending3DElevAzim = { elev: eVal, azim: aVal };
                        return;
                    }
                    is3DInFlight = true;
                    try {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'updatePlotView',
                            figId: figId,
                            elev: eVal,
                            azim: aVal
                        }));
                    } catch (ex) {
                        is3DInFlight = false;
                    }
                }

                function send2DViewRequest(pfx, pfy, z) {
                    if (is3D || !figId) return;
                    if (is2DInFlight) {
                        pending2DState = { panFracX: pfx, panFracY: pfy, zoom: z };
                        return;
                    }
                    is2DInFlight = true;
                    try {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'update2DView',
                            figId: figId,
                            panFracX: pfx,
                            panFracY: pfy,
                            zoom: z
                        }));
                    } catch (ex) {
                        is2DInFlight = false;
                    }
                }

                let currentMouseX = 0, currentMouseY = 0;

                function setupPlotBounds() {
                    const rawBounds = wrapper.getAttribute('data-plot-bounds');
                    const clipDiv = wrapper.querySelector('.mpl-data-clip');
                    const imgWrapper = wrapper.querySelector('.mpl-data-img-wrapper');
                    const dataImg = wrapper.querySelector('.mpl-data-img');
                    const mainImg = wrapper.querySelector('.mpl-plot-img');

                    if (dataImg && mainImg && dataImg.src !== mainImg.src) {
                        dataImg.src = mainImg.src;
                    }

                    if (rawBounds && clipDiv && imgWrapper && mainImg) {
                        try {
                            const b = typeof rawBounds === 'string' ? JSON.parse(rawBounds) : rawBounds;
                            if (b && b.width > 0 && b.height > 0) {
                                clipDiv.style.display = 'block';
                                clipDiv.style.left = b.left + '%';
                                clipDiv.style.top = b.top + '%';
                                clipDiv.style.width = b.width + '%';
                                clipDiv.style.height = b.height + '%';

                                imgWrapper.style.left = (-b.left / b.width * 100) + '%';
                                imgWrapper.style.top = (-b.top / b.height * 100) + '%';

                                const wPx = mainImg.clientWidth;
                                const hPx = mainImg.clientHeight;
                                if (wPx > 0 && hPx > 0) {
                                    imgWrapper.style.width = (wPx / (b.width / 100)) + 'px';
                                    imgWrapper.style.height = (hPx / (b.height / 100)) + 'px';
                                    if (dataImg) {
                                        dataImg.style.width = wPx + 'px';
                                        dataImg.style.height = hPx + 'px';
                                        dataImg.style.maxWidth = 'none';
                                        dataImg.style.maxHeight = 'none';
                                    }
                                } else {
                                    imgWrapper.style.width = (100 / b.width * 100) + '%';
                                    imgWrapper.style.height = (100 / b.height * 100) + '%';
                                    if (dataImg) {
                                        dataImg.style.width = '100%';
                                        dataImg.style.height = '100%';
                                    }
                                }
                                return b;
                            }
                        } catch (ex) { }
                    }
                    if (clipDiv) {
                        clipDiv.style.display = 'none';
                    }
                    return null;
                }

                const mainImgRef = wrapper.querySelector('.mpl-plot-img');
                if (mainImgRef) {
                    mainImgRef.addEventListener('load', function() { setupPlotBounds(); });
                }
                setupPlotBounds();

                wrapper.__on3DUpdateReceived = function(html) {
                    const temp = document.createElement('div');
                    temp.innerHTML = html;
                    const newImg = temp.querySelector('.mpl-plot-img');
                    const oldImg = wrapper.querySelector('.mpl-plot-img');
                    if (newImg && oldImg) {
                        oldImg.src = newImg.src;
                    }
                    const newClipImg = temp.querySelector('.mpl-data-img');
                    const oldClipImg = wrapper.querySelector('.mpl-data-img');
                    if (newClipImg && oldClipImg) {
                        oldClipImg.src = newClipImg.src;
                    } else if (newImg && oldClipImg) {
                        oldClipImg.src = newImg.src;
                    }

                    const newWrapper = temp.querySelector('.mpl-interactive-wrapper');
                    if (newWrapper && newWrapper.hasAttribute('data-plot-bounds')) {
                        wrapper.setAttribute('data-plot-bounds', newWrapper.getAttribute('data-plot-bounds'));
                    }
                    setupPlotBounds();

                    const newCbar = temp.querySelector('.mpl-cbar-img');
                    const oldCbar = wrapper.querySelector('.mpl-cbar-img');
                    if (newCbar && oldCbar) {
                        oldCbar.src = newCbar.src;
                    }

                    if (is3D) {
                        if (isDragging) {
                            startX = currentMouseX;
                            startY = currentMouseY;
                            startElev = elev;
                            startAzim = azim;
                        }
                        is3DInFlight = false;
                        if (pending3DElevAzim) {
                            const next = pending3DElevAzim;
                            pending3DElevAzim = null;
                            send3DViewRequest(next.elev, next.azim);
                        }
                    } else {
                        /* 2D: reset CSS preview since new image has correct view */
                        if (isDragging) {
                            startX = currentMouseX;
                            startY = currentMouseY;
                            startPanX = 0;
                            startPanY = 0;
                        }
                        panX = 0;
                        panY = 0;
                        scale = 1;
                        updateTransform();
                        is2DInFlight = false;
                        if (pending2DState) {
                            const next = pending2DState;
                            pending2DState = null;
                            send2DViewRequest(next.panFracX, next.panFracY, next.zoom);
                        }
                    }
                };

                function updateTransform() {
                    const imgWrapper = wrapper.querySelector('.mpl-data-img-wrapper');
                    const hasBounds = wrapper.hasAttribute('data-plot-bounds') && wrapper.getAttribute('data-plot-bounds') !== '';
                    if (!is3D && imgWrapper && hasBounds) {
                        imgWrapper.style.transform = 'translate(' + panX + 'px, ' + panY + 'px)';
                    } else if (is3D && plotLayer) {
                        plotLayer.style.transform = 'translate(' + panX + 'px, ' + panY + 'px) scale(' + scale + ')';
                    }
                    if (is3D) {
                        if (sliderY) sliderY.value = azim;
                        if (angleValY) angleValY.textContent = 'Azim:' + azim + '°';
                        if (sliderX) sliderX.value = elev;
                        if (angleValX) angleValX.textContent = 'Elev:' + elev + '°';
                    }
                }

                if (viewport) {
                    viewport.addEventListener('mousedown', function(e) {
                        e.preventDefault();
                        isDragging = true;
                        dragBtn = e.button;
                        startX = e.clientX;
                        startY = e.clientY;
                        startPanX = panX;
                        startPanY = panY;
                        startElev = elev;
                        startAzim = azim;
                    });

                    window.addEventListener('mousemove', function(e) {
                        if (!isDragging) return;
                        currentMouseX = e.clientX;
                        currentMouseY = e.clientY;
                        const dx = e.clientX - startX;
                        const dy = e.clientY - startY;

                        if (is3D) {
                            if (dragBtn === 1) {
                                panX = startPanX + dx;
                                panY = startPanY + dy;
                            } else {
                                azim = Math.round(startAzim - dx * 0.5) % 360;
                                elev = Math.min(Math.max(-90, Math.round(startElev + dy * 0.5)), 90);
                                send3DViewRequest(elev, azim);
                            }
                        } else {
                            if (dragBtn === 0 || dragBtn === 1) {
                                panX = startPanX + dx;
                                panY = startPanY + dy;
                            }
                        }
                        updateTransform();
                    });

                    window.addEventListener('mouseup', function() {
                        if (isDragging) {
                            isDragging = false;
                            if (is3D && dragBtn !== 1) {
                                send3DViewRequest(elev, azim);
                            } else if (!is3D && figId && (panX !== 0 || panY !== 0)) {
                                const img = wrapper.querySelector('.mpl-plot-img');
                                const w = img ? (img.clientWidth || 600) : 600;
                                const h = img ? (img.clientHeight || 400) : 400;
                                dataPanFracX -= panX / w / dataZoom;
                                dataPanFracY += panY / h / dataZoom;
                                send2DViewRequest(dataPanFracX, dataPanFracY, dataZoom);
                            }
                        }
                    });

                    viewport.addEventListener('contextmenu', function(e) {
                        e.preventDefault();
                    });

                    viewport.addEventListener('wheel', function(e) {
                        if (!isZoomActive) return;
                        e.preventDefault();
                        const factor = e.deltaY < 0 ? 1.1 : 0.9;
                        scale = Math.min(Math.max(0.2, scale * factor), 5.0);
                        updateTransform();
                        if (!is3D && figId) {
                            dataZoom = Math.min(Math.max(0.2, dataZoom * factor), 5.0);
                            send2DViewRequest(dataPanFracX, dataPanFracY, dataZoom);
                        }
                    });
                }

                if (btnZoom) {
                    btnZoom.addEventListener('click', function() {
                        isZoomActive = !isZoomActive;
                        btnZoom.classList.toggle('active', isZoomActive);
                    });
                }

                if (sliderY) {
                    sliderY.addEventListener('input', function() {
                        if (is3D) {
                            azim = parseInt(sliderY.value) || 0;
                            send3DViewRequest(elev, azim);
                            updateTransform();
                        }
                    });
                }

                if (sliderX) {
                    sliderX.addEventListener('input', function() {
                        if (is3D) {
                            elev = parseInt(sliderX.value) || 0;
                            send3DViewRequest(elev, azim);
                            updateTransform();
                        }
                    });
                }

                if (btnReset) {
                    btnReset.addEventListener('click', function() {
                        panX = 0; panY = 0; scale = 1;
                        isZoomActive = false;
                        if (btnZoom) btnZoom.classList.remove('active');
                        if (is3D) {
                            elev = initElev;
                            azim = initAzim;
                            send3DViewRequest(elev, azim);
                        } else if (figId) {
                            dataPanFracX = 0;
                            dataPanFracY = 0;
                            dataZoom = 1;
                            send2DViewRequest(0, 0, 1);
                        }
                        updateTransform();
                    });
                }

                if (btnDownload) {
                    btnDownload.addEventListener('click', function() {
                        const mainImg = wrapper.querySelector('.mpl-plot-img');
                        const cbarImg = wrapper.querySelector('.mpl-cbar-img');
                        if (!mainImg) return;

                        const canvas = document.createElement('canvas');
                        const ctx = canvas.getContext('2d');
                        if (!ctx) return;

                        const w1 = mainImg.naturalWidth || mainImg.width || 600;
                        const h1 = mainImg.naturalHeight || mainImg.height || 400;
                        const w2 = cbarImg ? (cbarImg.naturalWidth || cbarImg.width || 100) : 0;
                        const h2 = cbarImg ? (cbarImg.naturalHeight || cbarImg.height || 400) : 0;

                        canvas.width = w1 + (w2 ? w2 + 20 : 0);
                        canvas.height = Math.max(h1, h2);

                        ctx.fillStyle = '#ffffff';
                        ctx.fillRect(0, 0, canvas.width, canvas.height);

                        ctx.drawImage(mainImg, 0, 0, w1, h1);
                        if (cbarImg && w2) {
                            ctx.drawImage(cbarImg, w1 + 20, 0, w2, h2);
                        }

                        const a = document.createElement('a');
                        a.download = 'matplotlib_plot.png';
                        a.href = canvas.toDataURL('image/png');
                        a.click();
                    });
                }
            });
        });
    }

    setTimeout(initMplInteractiveContainers, 300);
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