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
                                const img = e.querySelector('img[src^=""data:image/""]');
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
                        imgs.forEach(img => {
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
    const getNotebookJson = window.getNotebookJson;

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
                } else {
                    const outObj = {
                        output_type: ""stream"",
                        name: ""stdout"",
                        text: part.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l)
                    };
                    html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""output-stdout"">' + escapeHtml(part) + '</span></div>';
                }
            }
        }

        if (resp.stderr) {
            const isErrStatus = resp.status === 'error';
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
            html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '><span class=""' + cls + '""' + '>' + escapeHtml(resp.stderr) + '</span></div>';
        }

        if (resp.result) {
            const isHtmlResult = /^<div/i.test(resp.result) || /^<table/i.test(resp.result) || resp.result.includes('<table');
            const outObj = {
                output_type: ""execute_result"",
                data: isHtmlResult ? { ""text/html"": resp.result } : { ""text/plain"": resp.result.split('\n').map((l, idx, arr) => idx < arr.length - 1 ? l + '\n' : l) },
                metadata: {},
                execution_count: null
            };
            const resultHtml = isHtmlResult ? resp.result : ('<span class=""output-result"">' + escapeHtml(resp.result) + '</span>');
            html += '<div class=""output-entry"" data-output=""' + escapeHtmlAttr(JSON.stringify(outObj)) + '""' + '>' + resultHtml + '</div>';
        }

        return html;
    }
";
        }
    }
}
