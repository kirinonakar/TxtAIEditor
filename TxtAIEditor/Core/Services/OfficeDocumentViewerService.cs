using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class OfficeDocumentViewerService
    {
        private readonly Func<string, string, string> _getString;

        public OfficeDocumentViewerService(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            string tempFilePath = string.Empty;
            bool isTempFile = false;

            try
            {
                try
                {
                    if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToDocxAsync(filePath).ConfigureAwait(false);
                        filePath = tempFilePath;
                        extension = ".docx";
                        isTempFile = true;
                    }
                    else if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToXlsxAsync(filePath).ConfigureAwait(false);
                        filePath = tempFilePath;
                        extension = ".xlsx";
                        isTempFile = true;
                    }
                    else if (extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToPptxAsync(filePath).ConfigureAwait(false);
                        filePath = tempFilePath;
                        extension = ".pptx";
                        isTempFile = true;
                    }
                }
                catch (Exception ex)
                {
                    return AddFindPanel(BuildErrorHtml(ex.Message));
                }

                if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await OfficeTextDocumentHtmlRenderer.BuildWordAsync(filePath, _getString).ConfigureAwait(false);
                    return AddFindPanel(html);
                }

                if (extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await OfficeTextDocumentHtmlRenderer.BuildHwpxAsync(filePath, _getString).ConfigureAwait(false);
                    return AddFindPanel(html);
                }

                if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await OfficePresentationDocumentHtmlRenderer.BuildAsync(filePath, _getString).ConfigureAwait(false);
                    return AddFindPanel(html);
                }

                if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await OfficeWorkbookDocumentHtmlRenderer.BuildAsync(filePath, _getString).ConfigureAwait(false);
                    return AddFindPanel(html);
                }

                return AddFindPanel(BuildErrorHtml(_getString("OfficeViewerUnsupportedDocument", "Unsupported Office document.")));
            }
            finally
            {
                if (isTempFile && !string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    {
                        // Ignore delete errors for temp files
                    }
                }
            }
        }

        private string AddFindPanel(string html)
        {
            if (string.IsNullOrWhiteSpace(html) ||
                html.Contains("__txtAiEditorOfficeFind", StringComparison.Ordinal))
            {
                return html;
            }

            string assets = BuildFindPanelAssets();
            int bodyCloseIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            return bodyCloseIndex >= 0
                ? html.Insert(bodyCloseIndex, assets)
                : html + assets;
        }

        private string BuildFindPanelAssets()
        {
            string optionsJson = JsonSerializer.Serialize(new
            {
                findPlaceholder = _getString("EditorFindPlaceholder", "찾기"),
                findClearTooltip = _getString("EditorFindClearTooltip", "지우기"),
                findMatchCaseTooltip = _getString("EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)"),
                findRegexTooltip = _getString("EditorFindRegexTooltip", "정규식 사용 (.*)"),
                findPrevTooltip = _getString("EditorFindPrevTooltip", "이전"),
                findNextTooltip = _getString("EditorFindNextTooltip", "다음"),
                findCloseTooltip = _getString("EditorFindCloseTooltip", "닫기")
            });

            return $$"""

<style id="txt-office-find-style">
#txt-office-find-panel {
    position: fixed;
    top: 10px;
    right: 18px;
    z-index: 2147483647;
    display: flex;
    align-items: center;
    gap: 6px;
    max-width: min(420px, calc(100vw - 24px));
    padding: 6px;
    border: 1px solid color-mix(in srgb, CanvasText 35%, transparent);
    border-radius: 4px;
    background: color-mix(in srgb, Canvas 92%, CanvasText 8%);
    color: CanvasText;
    box-shadow: 0 8px 24px rgba(0, 0, 0, .28);
    font: 12px/1.2 system-ui, "Segoe UI", sans-serif;
}
#txt-office-find-panel[hidden] { display: none; }
#txt-office-find-panel * { box-sizing: border-box; }
#txt-office-find-input-wrap {
    position: relative;
    display: flex;
    align-items: center;
    min-width: 180px;
    width: min(260px, calc(100vw - 156px));
}
#txt-office-find-input {
    width: 100%;
    height: 28px;
    border: 1px solid color-mix(in srgb, CanvasText 45%, transparent);
    border-radius: 3px;
    padding: 4px 58px 4px 8px;
    outline: 0;
    background: Canvas;
    color: CanvasText;
    font: inherit;
}
#txt-office-find-input:focus {
    border-color: rgba(0, 120, 212, .75);
    box-shadow: 0 0 0 1px rgba(0, 120, 212, .45);
}
#txt-office-find-input-actions {
    position: absolute;
    right: 5px;
    z-index: 1;
    display: flex;
    gap: 2px;
    align-items: center;
}
.txt-office-find-clear,
.txt-office-find-option,
.txt-office-find-button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    height: 20px;
    border-radius: 3px;
    background: transparent;
    color: CanvasText;
    cursor: pointer;
    font: inherit;
}
.txt-office-find-clear,
.txt-office-find-option {
    min-width: 20px;
    padding: 0 4px;
    border: 1px solid transparent;
    opacity: .5;
    font-weight: 700;
}
.txt-office-find-clear {
    width: 20px;
    padding: 0;
    font-size: 14px;
    font-weight: 400;
    line-height: 1;
}
.txt-office-find-button {
    width: 28px;
    height: 28px;
    padding: 0;
    border: 1px solid color-mix(in srgb, CanvasText 28%, transparent);
    font-size: 13px;
    line-height: 1;
}
.txt-office-find-clear:hover,
.txt-office-find-option:hover,
.txt-office-find-button:hover {
    background: color-mix(in srgb, CanvasText 16%, transparent);
    opacity: .85;
}
.txt-office-find-option.active {
    border-color: rgba(0, 120, 212, .4);
    background: rgba(0, 120, 212, .25);
    opacity: 1;
}
.txt-office-find-clear:focus,
.txt-office-find-option:focus,
.txt-office-find-button:focus {
    outline: none;
    box-shadow: none;
}
#txt-office-find-status {
    min-width: 42px;
    color: color-mix(in srgb, CanvasText 64%, transparent);
    text-align: center;
    white-space: nowrap;
}
#txt-office-find-close {
    position: relative;
    overflow: hidden;
    color: transparent;
}
#txt-office-find-close::before,
#txt-office-find-close::after {
    content: "";
    position: absolute;
    left: 50%;
    top: 50%;
    width: 13px;
    height: 1.5px;
    border-radius: 1px;
    background: CanvasText;
    transform-origin: center;
}
#txt-office-find-close::before { transform: translate(-50%, -50%) rotate(45deg); }
#txt-office-find-close::after { transform: translate(-50%, -50%) rotate(-45deg); }
mark.txt-office-find-match {
    background: #ffd43b;
    color: #212529;
    border-radius: 2px;
    padding: 0 1px;
}
mark.txt-office-find-match.txt-office-find-active {
    background: #fd7e14;
    color: #fff;
    box-shadow: 0 0 3px rgba(253, 126, 20, .6);
}
@media (max-width: 520px) {
    #txt-office-find-panel {
        left: 8px;
        right: 8px;
        max-width: none;
    }
    #txt-office-find-input-wrap {
        min-width: 0;
        width: 100%;
    }
}
</style>
<div id="txt-office-find-panel" hidden>
    <div id="txt-office-find-input-wrap">
        <input id="txt-office-find-input" type="text" autocomplete="off" spellcheck="false">
        <div id="txt-office-find-input-actions">
            <button id="txt-office-find-clear" class="txt-office-find-clear" type="button">×</button>
            <button id="txt-office-find-match-case" class="txt-office-find-option" type="button">Aa</button>
            <button id="txt-office-find-regex" class="txt-office-find-option" type="button">.*</button>
        </div>
    </div>
    <span id="txt-office-find-status">0/0</span>
    <button id="txt-office-find-prev" class="txt-office-find-button" type="button">↑</button>
    <button id="txt-office-find-next" class="txt-office-find-button" type="button">↓</button>
    <button id="txt-office-find-close" class="txt-office-find-button" type="button">×</button>
</div>
<script>
(() => {
    if (window.__txtAiEditorOfficeFind) return;

    const options = {{optionsJson}};
    const panel = document.getElementById('txt-office-find-panel');
    const input = document.getElementById('txt-office-find-input');
    const clearButton = document.getElementById('txt-office-find-clear');
    const matchCaseButton = document.getElementById('txt-office-find-match-case');
    const regexButton = document.getElementById('txt-office-find-regex');
    const previousButton = document.getElementById('txt-office-find-prev');
    const nextButton = document.getElementById('txt-office-find-next');
    const closeButton = document.getElementById('txt-office-find-close');
    const status = document.getElementById('txt-office-find-status');
    const ignoredTags = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEXTAREA', 'INPUT', 'SELECT', 'BUTTON']);
    let matches = [];
    let activeIndex = -1;
    let matchCase = false;
    let regex = false;
    let debounceTimer = 0;

    input.placeholder = options.findPlaceholder || '';
    clearButton.title = options.findClearTooltip || '';
    matchCaseButton.title = options.findMatchCaseTooltip || '';
    regexButton.title = options.findRegexTooltip || '';
    previousButton.title = options.findPrevTooltip || '';
    nextButton.title = options.findNextTooltip || '';
    closeButton.title = options.findCloseTooltip || '';

    function isInsideFindUi(node) {
        return !!(node && node.nodeType === Node.ELEMENT_NODE && node.closest('#txt-office-find-panel'));
    }

    function shouldSkipTextNode(node) {
        const parent = node.parentElement;
        if (!parent || isInsideFindUi(parent)) return true;
        if (ignoredTags.has(parent.tagName)) return true;
        return !!parent.closest('script,style,noscript,#txt-office-find-panel,mark.txt-office-find-match');
    }

    function clearHighlights() {
        document.querySelectorAll('mark.txt-office-find-match').forEach(mark => {
            const text = document.createTextNode(mark.textContent || '');
            mark.replaceWith(text);
            text.parentNode?.normalize();
        });
        matches = [];
        activeIndex = -1;
        updateStatus();
    }

    function updateStatus() {
        status.textContent = matches.length > 0 && activeIndex >= 0
            ? `${activeIndex + 1}/${matches.length}`
            : `0/${matches.length}`;
    }

    function collectTextNodes() {
        const walker = document.createTreeWalker(
            document.body,
            NodeFilter.SHOW_TEXT,
            {
                acceptNode(node) {
                    if (shouldSkipTextNode(node)) return NodeFilter.FILTER_REJECT;
                    return node.nodeValue && node.nodeValue.trim()
                        ? NodeFilter.FILTER_ACCEPT
                        : NodeFilter.FILTER_REJECT;
                }
            });

        const nodes = [];
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            nodes.push(node);
        }
        return nodes;
    }

    function findMatchesInText(text, query) {
        const found = [];
        if (!query) return found;

        if (regex) {
            let expression;
            try {
                expression = new RegExp(query, matchCase ? 'g' : 'gi');
            } catch {
                return found;
            }

            let match;
            while ((match = expression.exec(text)) !== null) {
                const value = match[0] || '';
                if (!value.length) {
                    expression.lastIndex++;
                    continue;
                }
                found.push({ index: match.index, length: value.length });
            }
            return found;
        }

        const haystack = matchCase ? text : text.toLocaleLowerCase();
        const needle = matchCase ? query : query.toLocaleLowerCase();
        let index = 0;
        while ((index = haystack.indexOf(needle, index)) >= 0) {
            found.push({ index, length: query.length });
            index += Math.max(1, query.length);
        }
        return found;
    }

    function rebuildMatches() {
        clearHighlights();

        const query = input.value || '';
        if (!query) return;

        const nodeMatches = [];
        collectTextNodes().forEach(node => {
            const found = findMatchesInText(node.nodeValue || '', query);
            if (found.length) {
                nodeMatches.push({ node, found });
            }
        });

        nodeMatches.forEach(item => {
            const created = [];
            for (let i = item.found.length - 1; i >= 0; i--) {
                const match = item.found[i];
                const node = item.node;
                if (!node.parentNode) continue;

                node.splitText(match.index + match.length);
                const matched = node.splitText(match.index);
                const mark = document.createElement('mark');
                mark.className = 'txt-office-find-match';
                mark.textContent = matched.nodeValue || '';
                matched.replaceWith(mark);
                created.unshift(mark);
            }

            matches.push(...created);
        });

        if (matches.length) {
            activeIndex = 0;
            revealActive();
        } else {
            updateStatus();
        }
    }

    function revealActive() {
        matches.forEach((mark, index) => {
            mark.classList.toggle('txt-office-find-active', index === activeIndex);
        });
        updateStatus();
        const active = matches[activeIndex];
        if (active) {
            active.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
        }
    }

    function move(reverse) {
        if (!matches.length) return;
        activeIndex = activeIndex < 0
            ? 0
            : (activeIndex + (reverse ? -1 : 1) + matches.length) % matches.length;
        revealActive();
    }

    function open() {
        panel.hidden = false;
        input.focus();
        input.select();
        rebuildMatches();
        return true;
    }

    function close() {
        panel.hidden = true;
        clearHighlights();
        document.body.focus?.();
    }

    input.addEventListener('input', () => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(rebuildMatches, 180);
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            move(event.shiftKey);
        } else if (event.key === 'Escape') {
            event.preventDefault();
            close();
        }
    });
    clearButton.addEventListener('click', () => {
        input.value = '';
        input.focus();
        rebuildMatches();
    });
    matchCaseButton.addEventListener('click', () => {
        matchCase = !matchCase;
        matchCaseButton.classList.toggle('active', matchCase);
        rebuildMatches();
    });
    regexButton.addEventListener('click', () => {
        regex = !regex;
        regexButton.classList.toggle('active', regex);
        rebuildMatches();
    });
    previousButton.addEventListener('click', () => move(true));
    nextButton.addEventListener('click', () => move(false));
    closeButton.addEventListener('click', close);

    document.addEventListener('keydown', event => {
        const ctrl = event.ctrlKey || event.metaKey;
        const key = event.key ? event.key.toLowerCase() : '';
        if (ctrl && key === 'f') {
            event.preventDefault();
            event.stopPropagation();
            open();
        }
    }, true);

    window.__txtAiEditorOfficeFind = { open, close };
})();
</script>
""";
        }

        private static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
