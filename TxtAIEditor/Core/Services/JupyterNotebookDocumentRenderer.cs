using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal sealed class JupyterNotebookDocumentRenderer
    {
        private readonly Func<string, string, string> _getString;

        internal JupyterNotebookDocumentRenderer(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        internal string Render(NotebookDocument doc, string filePath, EditorSettings? settings = null)
        {
            string fileName = Path.GetFileName(filePath);
            string dirPath = Path.GetDirectoryName(filePath) ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ko\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"UTF-8\" />");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
            sb.AppendLine($"<title>{NotebookHtmlEncoder.Encode(fileName)}</title>");
            sb.AppendLine("<link rel=\"stylesheet\" href=\"http://txtaieditor.local/katex/katex.min.css\" />");
            sb.AppendLine("<style>");
            sb.AppendLine(JupyterNotebookViewerStyles.GetCss());
            sb.AppendLine("</style>");
            sb.AppendLine("<script src=\"http://txtaieditor.local/katex/katex.min.js\"></script>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div id=\"notebook-container\">");

            sb.AppendLine("<div id=\"notebook-header\">");
            sb.AppendLine("<div class=\"notebook-header-top\">");
            sb.AppendLine($"<span class=\"notebook-title\">{NotebookHtmlEncoder.Encode(fileName)}</span>");
            sb.AppendLine("<div id=\"notebook-toolbar\">");
            sb.AppendLine($"<button id=\"btn-find\" class=\"nb-btn nb-btn-find\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterFindTooltip", "Find in notebook (Ctrl+F)"))}\">🔍</button>");
            sb.AppendLine("<button id=\"btn-add-code\" class=\"nb-btn nb-btn-add\">+ Code</button>");
            sb.AppendLine("<button id=\"btn-add-markdown\" class=\"nb-btn nb-btn-add\">+ Markdown</button>");
            sb.AppendLine("<button id=\"btn-run-all\" class=\"nb-btn nb-btn-run\">Run All</button>");
            sb.AppendLine($"<button id=\"btn-clear-outputs\" class=\"nb-btn nb-btn-clear\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterClearOutputsButton", "Clear Outputs"))}\">🧹 {NotebookHtmlEncoder.Encode(_getString("JupyterClearOutputsButton", "Clear Outputs"))}</button>");
            sb.AppendLine("<button id=\"btn-export-py\" class=\"nb-btn nb-btn-export\" title=\"Save as Python Script (.py)\">🐍 Save as .py</button>");
            sb.AppendLine($"<button id=\"btn-variables\" class=\"nb-btn nb-btn-vars\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterVariablesPanelTitle", "Variable Explorer"))}\">🔍 {NotebookHtmlEncoder.Encode(_getString("JupyterVariablesButton", "Variables"))}</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"notebook-find-bar\" hidden>");
            sb.AppendLine($"<input id=\"notebook-find-input\" type=\"search\" autocomplete=\"off\" spellcheck=\"false\" placeholder=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterFindPlaceholder", "Find in notebook..."))}\" />");
            sb.AppendLine("<span id=\"notebook-find-count\" aria-live=\"polite\"></span>");
            sb.AppendLine($"<button id=\"btn-find-previous\" class=\"nb-btn nb-btn-sm\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterFindPreviousTooltip", "Previous match (Shift+Enter)"))}\">↑</button>");
            sb.AppendLine($"<button id=\"btn-find-next\" class=\"nb-btn nb-btn-sm\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterFindNextTooltip", "Next match (Enter)"))}\">↓</button>");
            sb.AppendLine($"<button id=\"btn-find-close\" class=\"nb-btn nb-btn-sm\" title=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterFindCloseTooltip", "Close (Esc)"))}\">✕</button>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"variables-panel\" style=\"display:none;\">");
            sb.AppendLine("<div class=\"vars-panel-header\">");
            sb.AppendLine($"<span class=\"vars-panel-title\">🔍 {NotebookHtmlEncoder.Encode(_getString("JupyterVariablesPanelTitle", "Variable Explorer"))}</span>");
            sb.AppendLine("<div class=\"vars-panel-controls\">");
            sb.AppendLine($"<input type=\"text\" id=\"vars-filter-input\" placeholder=\"{NotebookHtmlEncoder.AttributeEncode(_getString("JupyterVariablesFilterPlaceholder", "Filter variables..."))}\" />");
            sb.AppendLine("<button id=\"btn-refresh-vars\" class=\"nb-btn nb-btn-sm\" title=\"Refresh\">🔄</button>");
            sb.AppendLine("<button id=\"btn-close-vars\" class=\"nb-btn nb-btn-sm\" title=\"Close\">✕</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"vars-table-container\">");
            sb.AppendLine("<table class=\"vars-table\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("<tr>");
            sb.AppendLine($"<th>{NotebookHtmlEncoder.Encode(_getString("JupyterVariablesName", "Name"))}</th>");
            sb.AppendLine($"<th>{NotebookHtmlEncoder.Encode(_getString("JupyterVariablesType", "Type"))}</th>");
            sb.AppendLine($"<th>{NotebookHtmlEncoder.Encode(_getString("JupyterVariablesSize", "Size"))}</th>");
            sb.AppendLine($"<th>{NotebookHtmlEncoder.Encode(_getString("JupyterVariablesValue", "Value"))}</th>");
            sb.AppendLine("</tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody id=\"vars-table-body\">");
            sb.AppendLine($"<tr><td colspan=\"4\" class=\"vars-empty\">{NotebookHtmlEncoder.Encode(_getString("JupyterVariablesEmpty", "No active variables."))}</td></tr>");
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
            sb.AppendLine($"window.__notebookStrings = {BuildNotebookStringsJson()};");
            sb.AppendLine($"window.__notebookSettings = {JsonSerializer.Serialize(new { autocompleteOnEnter = settings?.AutocompleteOnEnter ?? true, autocompleteOnTab = settings?.AutocompleteOnTab ?? true })};");
            sb.AppendLine(JupyterNotebookViewerScripts.GetJavaScript());
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
                string html = JupyterNotebookMarkdownRenderer.Render(source);
                sb.AppendLine($"<div class=\"cell-input markdown-cell\" data-source=\"{NotebookHtmlEncoder.AttributeEncode(source)}\">");
                sb.AppendLine($"<div class=\"markdown-preview\">{html}</div>");
                sb.AppendLine($"<div class=\"cell-input-area markdown-editor\" contenteditable=\"true\" spellcheck=\"false\" style=\"display:none;\"><pre>{NotebookHtmlEncoder.Encode(source)}</pre></div>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-run\" title=\"Render Markdown (Shift+Enter)\">▶ Render</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-edit\" title=\"Edit Markdown\">✎ Edit</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Code\">Code</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-above\" title=\"{BuildShortcutTitle("JupyterCellInsertAbove", "Insert Cell Above", "A")}\">+ Above</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-below\" title=\"{BuildShortcutTitle("JupyterCellInsertBelow", "Insert Cell Below", "B")}\">+ Below</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"{BuildShortcutTitle("JupyterCellDelete", "Delete Cell", "D, D")}\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
            }
            else if (cellType == "raw")
            {
                sb.AppendLine($"<div class=\"cell-input raw-cell\" data-source=\"{NotebookHtmlEncoder.AttributeEncode(source)}\">");
                sb.AppendLine($"<div class=\"cell-input-area raw-editor\" contenteditable=\"true\" spellcheck=\"false\"><pre>{NotebookHtmlEncoder.Encode(source)}</pre></div>");
                sb.AppendLine("</div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Code\">Code</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-above\" title=\"{BuildShortcutTitle("JupyterCellInsertAbove", "Insert Cell Above", "A")}\">+ Above</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-below\" title=\"{BuildShortcutTitle("JupyterCellInsertBelow", "Insert Cell Below", "B")}\">+ Below</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"{BuildShortcutTitle("JupyterCellDelete", "Delete Cell", "D, D")}\">✕</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-up\" title=\"Move Up\">↑</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-move-down\" title=\"Move Down\">↓</button>");
                sb.AppendLine("</div>");
            }
            else
            {
                sb.AppendLine($"<div class=\"cell-input code-cell\">");
                sb.AppendLine($"<div class=\"cell-input-area code-editor\" contenteditable=\"true\" spellcheck=\"false\" data-source=\"{NotebookHtmlEncoder.AttributeEncode(source)}\"><pre>{NotebookHtmlEncoder.Encode(source)}</pre></div>");
                sb.AppendLine("<div class=\"cell-toolbar\">");
                sb.AppendLine($"<button class=\"cell-btn cell-run\" title=\"Run (Shift+Enter)\">▶ Run</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-run-below\" title=\"Run Below\">▶|</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-toggle-type\" title=\"Switch to Markdown\">Markdown</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-above\" title=\"{BuildShortcutTitle("JupyterCellInsertAbove", "Insert Cell Above", "A")}\">+ Above</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-add-below\" title=\"{BuildShortcutTitle("JupyterCellInsertBelow", "Insert Cell Below", "B")}\">+ Below</button>");
                sb.AppendLine($"<button class=\"cell-btn cell-delete\" title=\"{BuildShortcutTitle("JupyterCellDelete", "Delete Cell", "D, D")}\">✕</button>");
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

        private string BuildShortcutTitle(string key, string fallback, string shortcut)
        {
            return NotebookHtmlEncoder.AttributeEncode($"{_getString(key, fallback)} ({shortcut})");
        }

        private string BuildNotebookStringsJson()
        {
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["findNoMatches"] = _getString("JupyterFindNoMatches", "No matches"),
                ["findMatchCount"] = _getString("JupyterFindMatchCount", "{0} of {1}"),
                ["dragCell"] = _getString("JupyterCellDragTooltip", "Drag to reorder cell"),
                ["commandMode"] = _getString("JupyterCellCommandMode", "Command Mode"),
                ["insertAbove"] = _getString("JupyterCellInsertAbove", "Insert Cell Above"),
                ["insertBelow"] = _getString("JupyterCellInsertBelow", "Insert Cell Below"),
                ["cutCell"] = _getString("JupyterCellCut", "Cut Cell"),
                ["copyCell"] = _getString("JupyterCellCopy", "Copy Cell"),
                ["pasteAbove"] = _getString("JupyterCellPasteAbove", "Paste Cell Above"),
                ["pasteBelow"] = _getString("JupyterCellPasteBelow", "Paste Cell Below"),
                ["deleteCell"] = _getString("JupyterCellDelete", "Delete Cell"),
                ["splitCell"] = _getString("JupyterCellSplit", "Split Cell"),
                ["mergeAbove"] = _getString("JupyterCellMergeAbove", "Merge Cell Above"),
                ["mergeBelow"] = _getString("JupyterCellMergeBelow", "Merge Cell Below"),
                ["clearOutput"] = _getString("JupyterCellClearOutput", "Clear Cell Output")
            });
        }

        private static string RenderCellOutputs(List<JsonElement>? outputs)
        {
            if (outputs == null || outputs.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var outElement in outputs)
            {
                if (outElement.ValueKind != JsonValueKind.Object) continue;
                string jsonAttr = NotebookHtmlEncoder.AttributeEncode(outElement.GetRawText());
                sb.Append($"<div class=\"output-entry\" data-output=\"{jsonAttr}\">");

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
                            sb.Append($"<span class=\"{cls}\">{NotebookHtmlEncoder.Encode(text)}</span>");
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
                            sb.Append($"<span class=\"output-result\">{NotebookHtmlEncoder.Encode(plainText)}</span>");
                        }
                    }
                }
                else if (outputType == "error")
                {
                    string ename = outElement.TryGetProperty("ename", out var en) ? en.GetString() ?? "" : "";
                    string evalue = outElement.TryGetProperty("evalue", out var ev) ? ev.GetString() ?? "" : "";
                    sb.Append($"<span class=\"output-error\">{NotebookHtmlEncoder.Encode(ename)}: {NotebookHtmlEncoder.Encode(evalue)}</span>");
                }

                sb.Append("</div>");
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

    }
}
