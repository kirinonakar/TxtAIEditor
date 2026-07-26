namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookSerializationScript
    {
        internal static string GetScript()
        {
            return @"    function escapeHtmlAttr(text) {
        if (!text) return '';
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/""/g, '&quot;')
            .replace(/'/g, '&#39;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function extractImageMimeAndBase64(imgEl) {
        if (!imgEl) return null;
        const src = imgEl.getAttribute('src') || imgEl.src || '';
        if (!src || !src.startsWith('data:image/')) return null;
        const match = src.match(/^data:(image\/[a-zA-Z\+\-]+);base64,([\s\S]+)$/i);
        if (!match) return null;
        return {
            mime: match[1],
            b64: match[2].replace(/[\r\n\s]/g, '')
        };
    }

    function getCellType(cellDiv) {
        return cellDiv.getAttribute('data-cell-type');
    }

    window.getNotebookJson = function getNotebookJson() {
        const cells = [];
        container.querySelectorAll('.cell').forEach(cellDiv => {
            const type = getCellType(cellDiv);
            const source = getCellSource(cellDiv);
            const sourceLines = source.split('\n').map((l, i, arr) => i < arr.length - 1 ? l + '\n' : l);
            const outputs = [];
            if (type === 'code') {
                const outputDiv = cellDiv.querySelector('.cell-output');
                if (outputDiv) {
                    const entries = outputDiv.querySelectorAll('.output-entry');
                    if (entries.length > 0) {
                        entries.forEach(e => {
                            let outObj = null;
                            try {
                                const raw = e.getAttribute('data-output');
                                if (raw) outObj = JSON.parse(raw);
                            } catch (ex) {}

                            if (outObj) {
                                const img = e.querySelector('.mpl-notebook-img') ||
                                    e.querySelector('img[src^=""data:image/""]');
                                const imgData = extractImageMimeAndBase64(img);
                                if (imgData) {
                                    outObj.data = outObj.data || {};
                                    outObj.data[imgData.mime] = imgData.b64;
                                    if (outObj.output_type !== 'display_data' && outObj.output_type !== 'execute_result') {
                                        outObj.output_type = 'display_data';
                                    }
                                }
                                outputs.push(outObj);
                            }
                        });
                    } else {
                        const imgs = outputDiv.querySelectorAll('img[src^=""data:image/""]');
                        const seenMplWrappers = new Set();
                        imgs.forEach(foundImg => {
                            let img = foundImg;
                            const mplWrapper = foundImg.closest('.mpl-interactive-wrapper');
                            if (mplWrapper) {
                                if (seenMplWrappers.has(mplWrapper)) return;
                                seenMplWrappers.add(mplWrapper);
                                img = mplWrapper.querySelector('.mpl-notebook-img') ||
                                    mplWrapper.querySelector('.mpl-plot-img') ||
                                    foundImg;
                            }
                            const imgData = extractImageMimeAndBase64(img);
                            if (imgData) {
                                outputs.push({
                                    output_type: 'display_data',
                                    data: {
                                        [imgData.mime]: imgData.b64,
                                        'text/plain': '<Figure size>'
                                    },
                                    metadata: {}
                                });
                            }
                        });
                        if (outputs.length === 0) {
                            const stdoutSpan = outputDiv.querySelector('.output-stdout');
                            if (stdoutSpan && stdoutSpan.textContent) {
                                const txt = stdoutSpan.textContent;
                                outputs.push({
                                    output_type: 'stream',
                                    name: 'stdout',
                                    text: txt.split('\n').map((l, i, a) => i < a.length - 1 ? l + '\n' : l)
                                });
                            }
                            const stderrSpan = outputDiv.querySelector('.output-stderr');
                            if (stderrSpan && stderrSpan.textContent) {
                                const txt = stderrSpan.textContent;
                                outputs.push({
                                    output_type: 'stream',
                                    name: 'stderr',
                                    text: txt.split('\n').map((l, i, a) => i < a.length - 1 ? l + '\n' : l)
                                });
                            }
                            const resultSpan = outputDiv.querySelector('.output-result');
                            if (resultSpan && resultSpan.textContent) {
                                const txt = resultSpan.textContent;
                                outputs.push({
                                    output_type: 'execute_result',
                                    data: { 'text/plain': txt.split('\n').map((l, i, a) => i < a.length - 1 ? l + '\n' : l) },
                                    metadata: {},
                                    execution_count: null
                                });
                            }
                        }
                    }
                }
                cells.push({ cell_type: 'code', source: sourceLines, outputs: outputs, metadata: {}, execution_count: null });
            } else if (type === 'markdown') {
                cells.push({ cell_type: 'markdown', source: sourceLines, metadata: {} });
            } else {
                cells.push({ cell_type: 'raw', source: sourceLines, metadata: {} });
            }
        });
        return JSON.stringify({ cells: cells, metadata: {}, nbformat: 4, nbformat_minor: 5 }, null, 1);
    };
    window.parseTqdmText = function parseTqdmText(text) {
        if (!text) return { lastTqdm: null, nonTqdmText: '' };
        const lines = text.split('\n');
        let lastTqdm = null;
        const nonTqdmLines = [];

        const tqdmRegex = /^(.*?)\s*(\d{1,3})%\s*\|([^|]*)\|\s*(?:(\d+(?:\/\d+)?)\s*)?(?:\[(.*?)\])?/;

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const cr = String.fromCharCode(13);
            const crParts = line.split(cr);
            for (let j = 0; j < crParts.length; j++) {
                const part = crParts[j].trim();
                if (!part) continue;
                const match = part.match(tqdmRegex);
                if (match && (match[2] || match[3])) {
                    const pct = parseInt(match[2], 10);
                    const desc = match[1] ? match[1].trim() : '';
                    const counts = match[4] ? match[4].trim() : '';
                    const stats = match[5] ? match[5].trim() : '';
                    lastTqdm = { desc, pct, counts, stats };
                } else {
                    nonTqdmLines.push(part);
                }
            }
        }
        return { lastTqdm, nonTqdmText: nonTqdmLines.join('\n') };
    };

    window.buildTqdmWidgetHtml = function buildTqdmWidgetHtml(tqdmData) {
        if (!tqdmData) return '';
        const pct = Math.min(100, Math.max(0, tqdmData.pct));
        const isComplete = (pct === 100) || (tqdmData.counts && tqdmData.counts.split('/')[0] === tqdmData.counts.split('/')[1]);
        const completeCls = isComplete ? ' is-complete' : '';
        const descHtml = tqdmData.desc ? '<span class=""nb-tqdm-desc"">' + escapeHtmlAttr(tqdmData.desc) + '</span>' : '';
        const countsHtml = tqdmData.counts ? escapeHtmlAttr(tqdmData.counts) : '';
        const statsHtml = tqdmData.stats ? '[' + escapeHtmlAttr(tqdmData.stats) + ']' : '';
        const infoText = [countsHtml, statsHtml].filter(Boolean).join(' ');

        return '<div class=""nb-tqdm-widget' + completeCls + '"">' +
            '<div class=""nb-tqdm-header"">' +
            '<div class=""nb-tqdm-title"">' + descHtml + '<span class=""nb-tqdm-pct"">' + pct + '%</span></div>' +
            '<div class=""nb-tqdm-stats"">' + infoText + '</div>' +
            '</div>' +
            '<div class=""nb-tqdm-track"">' +
            '<div class=""nb-tqdm-fill"" style=""width: ' + pct + '%;""></div>' +
            '</div>' +
            '</div>';
    };

    function renderCellOutputsFromResponse(resp) {
        if (!resp) return '';
        let html = '';

        if (resp.stdout) {
            const parts = resp.stdout.split(/(<!--MPL_START-->[\s\S]*?<!--MPL_END-->|<img\s+src=""data:image\/[^"">]+""?[^>]*\/>|<div[\s\S]*?<table[\s\S]*?<\/table>[\s\S]*?<\/div>|<table[\s\S]*?<\/table>)/gi);
            for (let i = 0; i < parts.length; i++) {
                const part = parts[i];
                if (!part) continue;
                const isHtmlOutput = part.startsWith('<!--MPL_START-->') || 
                                     /^<img\s+src=""data:image\//i.test(part) || 
                                     /^<table/i.test(part) || 
                                     /^<div/i.test(part) || 
                                     part.includes('<table') || 
                                     part.includes('<style');
                if (isHtmlOutput) {
                    const imgMatch = part.match(/src=""data:(image\/[a-zA-Z\+\-]+);base64,([\s\S]+?)""/i);
                    const outObj = {
                        output_type: (part.includes('<table') || part.includes('dataframe')) ? ""execute_result"" : ""display_data"",
                        data: {},
                        metadata: {}
                    };
                    if (imgMatch) {
                        outObj.data[imgMatch[1]] = imgMatch[2].replace(/[\r\n\s]/g, '');
                        outObj.data[""text/plain""] = ""<Figure size>"";
                    } else if (part.includes('<table')) {
                        outObj.data[""text/html""] = part;
                    }
                    html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '>' + part + '</div>';
                } else if (window.parseTqdmText && (part.includes('%|') || part.includes('% |'))) {
                    const parsed = window.parseTqdmText(part);
                    if (parsed.lastTqdm) {
                        html += '<div class=""output-entry"">' + window.buildTqdmWidgetHtml(parsed.lastTqdm) + '</div>';
                    }
                    if (parsed.nonTqdmText) {
                        const outObj = {
                            output_type: ""stream"",
                            name: ""stdout"",
                            text: parsed.nonTqdmText.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l)
                        };
                        html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""output-stdout"">' + escapeHtmlAttr(parsed.nonTqdmText) + '</span></div>';
                    }
                } else {
                    const outObj = {
                        output_type: ""stream"",
                        name: ""stdout"",
                        text: part.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l)
                    };
                    html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""output-stdout"">' + escapeHtmlAttr(part) + '</span></div>';
                }
            }
        }

        if (resp.stderr) {
            const isErrStatus = resp.status === 'error';
            if (!isErrStatus && window.parseTqdmText && (resp.stderr.includes('%|') || resp.stderr.includes('% |'))) {
                const parsed = window.parseTqdmText(resp.stderr);
                if (parsed.lastTqdm) {
                    html += '<div class=""output-entry"">' + window.buildTqdmWidgetHtml(parsed.lastTqdm) + '</div>';
                }
                if (parsed.nonTqdmText) {
                    const outObj = {
                        output_type: ""stream"",
                        name: ""stderr"",
                        text: parsed.nonTqdmText.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l)
                    };
                    html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""output-stderr"">' + escapeHtmlAttr(parsed.nonTqdmText) + '</span></div>';
                }
            } else {
                const outObj = isErrStatus ? {
                    output_type: ""error"",
                    ename: ""ExecutionError"",
                    evalue: resp.stderr,
                    traceback: resp.stderr.split('\n')
                } : {
                    output_type: ""stream"",
                    name: ""stderr"",
                    text: resp.stderr.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l)
                };
                const cls = isErrStatus ? ""output-error"" : ""output-stderr"";
                html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""' + cls + '""' + '>' + escapeHtmlAttr(resp.stderr) + '</span></div>';
            }
        }

        if (resp.result) {
            const isHtmlResult = /^<div/i.test(resp.result) || /^<table/i.test(resp.result) || resp.result.includes('<table');
            const outObj = {
                output_type: ""execute_result"",
                data: isHtmlResult ? { ""text/html"": resp.result } : { ""text/plain"": resp.result.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l) },
                metadata: {},
                execution_count: null
            };
            const resultHtml = isHtmlResult ? resp.result : ('<span class=""output-result"">' + escapeHtmlAttr(resp.result) + '</span>');
            html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '>' + resultHtml + '</div>';
        }

        return html;
    }
";
        }
    }
}
