import { state } from './editor-core.js';

export function createLivePreviewPositionResolver({
    findEditablePreviewBlockContaining,
    inlineMarkdown,
    listItemInfo
}) {
    return function getPreciseLivePreviewPosition(element, event) {
    const defaultLine = Number(element.dataset.line || 1);
    
    try {
        let range = null;
        if (document.caretRangeFromPoint) {
            range = document.caretRangeFromPoint(event.clientX, event.clientY);
        } else if (document.caretPositionFromPoint) {
            const pos = document.caretPositionFromPoint(event.clientX, event.clientY);
            if (pos) {
                range = document.createRange();
                range.setStart(pos.offsetNode, pos.offset);
            }
        }

        if (!range) {
            return { line: defaultLine, column: 0, element };
        }

        const targetNode = range.startContainer;
        const targetOffset = range.startOffset;
        if (!targetNode) {
            return { line: defaultLine, column: 0, element };
        }

        const block = typeof findEditablePreviewBlockContaining === 'function'
            ? findEditablePreviewBlockContaining(
                defaultLine,
                state.lineCount,
                l => state.cache.get(l),
                { tabSize: state.tabSize || 4 }
              )
            : null;

        if (!block) {
            const rawLineText = state.cache.get(defaultLine) || '';
            const htmlLineText = element.textContent || '';
            const offset = getTextOffsetInElement(element, targetNode, targetOffset);
            const finalColumn = mapHtmlOffsetToRawOffset(rawLineText, htmlLineText, offset);
            return { line: defaultLine, column: finalColumn, element };
        }

        let clickedLine = defaultLine;
        let finalColumn = 0;

        if (block.kind === 'paragraph') {
            const brCount = countPrecedingBrs(element, targetNode);
            clickedLine = Math.min(block.endLine, block.startLine + brCount);
            const rawLineText = state.cache.get(clickedLine) || '';
            const offset = getTextOffsetAfterLastBr(element, targetNode, targetOffset);
            
            const tempDiv = document.createElement('div');
            tempDiv.innerHTML = inlineMarkdown(rawLineText.trim(), { tabSize: state.tabSize || 4 });
            const htmlLineText = tempDiv.textContent || '';
            
            finalColumn = mapHtmlOffsetToRawOffset(rawLineText, htmlLineText, offset);
        } else if (block.kind === 'list') {
            const targetLi = targetNode.nodeType === Node.ELEMENT_NODE
                ? targetNode.closest('li')
                : targetNode.parentElement?.closest('li');
            if (targetLi) {
                const liIndex = getPrecedingLiCount(element, targetLi);
                clickedLine = Math.min(block.endLine, block.startLine + Math.max(0, liIndex));
                const rawLineText = state.cache.get(clickedLine) || '';
                const offset = getTextOffsetInLi(targetLi, targetNode, targetOffset);
                
                const tempDiv = document.createElement('div');
                const itemInfo = listItemInfo(rawLineText, { tabSize: state.tabSize || 4 });
                const textToRender = itemInfo ? itemInfo.content : rawLineText;
                tempDiv.innerHTML = inlineMarkdown(textToRender, { tabSize: state.tabSize || 4 });
                const htmlLineText = tempDiv.textContent || '';
                
                finalColumn = mapHtmlOffsetToRawOffset(rawLineText, htmlLineText, offset);
            } else {
                clickedLine = defaultLine;
                finalColumn = 0;
            }
        } else if (block.kind === 'table') {
            const targetCell = targetNode.nodeType === Node.ELEMENT_NODE
                ? targetNode.closest('td, th')
                : targetNode.parentElement?.closest('td, th');
            const targetTr = targetCell?.closest('tr');
            if (targetCell && targetTr) {
                const isHeader = targetCell.tagName.toLowerCase() === 'th';
                if (isHeader) {
                    clickedLine = block.startLine;
                } else {
                    const tbody = targetTr.closest('tbody');
                    const tbodyRowIndex = tbody ? Array.from(tbody.querySelectorAll('tr')).indexOf(targetTr) : 0;
                    clickedLine = Math.min(block.endLine, block.startLine + 2 + Math.max(0, tbodyRowIndex));
                }
                
                const rawLineText = state.cache.get(clickedLine) || '';
                const cellIndex = Array.from(targetTr.querySelectorAll('td, th')).indexOf(targetCell);
                const offset = getTextOffsetInElement(targetCell, targetNode, targetOffset);
                
                const cells = rawLineText.split('|');
                if (cellIndex + 1 < cells.length) {
                    let startIdx = 0;
                    for (let i = 0; i <= cellIndex; i++) {
                        startIdx += cells[i].length + 1;
                    }
                    const cellRaw = cells[cellIndex + 1];
                    const tempDiv = document.createElement('div');
                    tempDiv.innerHTML = inlineMarkdown(cellRaw.trim(), { tabSize: state.tabSize || 4 });
                    const htmlLineText = tempDiv.textContent || '';
                    
                    const mappedOffset = mapHtmlOffsetToRawOffset(cellRaw, htmlLineText, offset);
                    finalColumn = startIdx + mappedOffset;
                } else {
                    finalColumn = 0;
                }
            } else {
                clickedLine = defaultLine;
                finalColumn = 0;
            }
        } else if (block.kind === 'code') {
            const { lineOffset, column } = getCodePosition(element, targetNode, targetOffset);
            clickedLine = Math.min(block.endLine, block.bodyStartLine + lineOffset);
            finalColumn = column;
        } else {
            clickedLine = defaultLine;
            finalColumn = 0;
        }

        return { line: clickedLine, column: finalColumn, element };
    } catch (e) {
        console.error('getPreciseLivePreviewPosition error:', e);
        return { line: defaultLine, column: 0, element };
    }
};
}

function getTextOffsetInElement(container, targetNode, targetOffset) {
    let offset = 0;
    let found = false;
    
    function walk(node) {
        if (node === targetNode) {
            if (node.nodeType === Node.TEXT_NODE) {
                offset += targetOffset;
            }
            found = true;
            return;
        }
        if (node.nodeType === Node.TEXT_NODE) {
            offset += node.textContent.length;
        }
        for (let child = node.firstChild; child; child = child.nextSibling) {
            walk(child);
            if (found) return;
        }
    }
    
    walk(container);
    return offset;
}

function countPrecedingBrs(container, targetNode) {
    let brCount = 0;
    let found = false;
    
    function walk(node) {
        if (node === targetNode) {
            found = true;
            return;
        }
        if (node.nodeName === 'BR') {
            brCount++;
        }
        for (let child = node.firstChild; child; child = child.nextSibling) {
            walk(child);
            if (found) return;
        }
    }
    
    walk(container);
    return brCount;
}

function getTextOffsetAfterLastBr(container, targetNode, targetOffset) {
    let offset = 0;
    let found = false;
    
    function walk(node) {
        if (node === targetNode) {
            if (node.nodeType === Node.TEXT_NODE) {
                offset += targetOffset;
            }
            found = true;
            return;
        }
        if (node.nodeName === 'BR') {
            offset = 0;
        } else if (node.nodeType === Node.TEXT_NODE) {
            offset += node.textContent.length;
        }
        for (let child = node.firstChild; child; child = child.nextSibling) {
            walk(child);
            if (found) return;
        }
    }
    
    walk(container);
    return offset;
}

function getPrecedingLiCount(container, targetLi) {
    const lis = Array.from(container.querySelectorAll('li'));
    return lis.indexOf(targetLi);
}

function getTextOffsetInLi(liElement, targetNode, targetOffset) {
    let offset = 0;
    let found = false;
    
    function walk(node) {
        if (node === targetNode) {
            if (node.nodeType === Node.TEXT_NODE) {
                offset += targetOffset;
            }
            found = true;
            return;
        }
        if (node.nodeName === 'UL' || node.nodeName === 'OL') {
            return;
        }
        if (node.nodeType === Node.TEXT_NODE) {
            offset += node.textContent.length;
        }
        for (let child = node.firstChild; child; child = child.nextSibling) {
            walk(child);
            if (found) return;
        }
    }
    
    walk(liElement);
    return offset;
}

function getCodePosition(codeElement, targetNode, targetOffset) {
    let offset = 0;
    let found = false;
    
    function walk(node) {
        if (node === targetNode) {
            if (node.nodeType === Node.TEXT_NODE) {
                offset += targetOffset;
            }
            found = true;
            return;
        }
        if (node.nodeType === Node.TEXT_NODE) {
            offset += node.textContent.length;
        }
        for (let child = node.firstChild; child; child = child.nextSibling) {
            walk(child);
            if (found) return;
        }
    }
    
    walk(codeElement);
    
    const textBefore = (codeElement.textContent || '').slice(0, offset);
    const linesBefore = textBefore.split('\n');
    const clickedLineOffset = linesBefore.length - 1;
    const clickedColumn = linesBefore[linesBefore.length - 1].length;
    return { lineOffset: clickedLineOffset, column: clickedColumn };
}

function mapHtmlOffsetToRawOffset(rawText, htmlText, htmlOffset) {
    if (htmlOffset <= 0) return 0;
    if (htmlOffset >= htmlText.length) return rawText.length;

    let r = 0;
    let h = 0;
    
    while (h < htmlOffset && r < rawText.length) {
        const charHtml = htmlText[h];
        const charRaw = rawText[r];
        
        if (charHtml === charRaw) {
            h++;
            r++;
        } else if (isMarkdownSyntaxChar(charRaw)) {
            r++;
        } else {
            const nextMatch = rawText.indexOf(charHtml, r);
            if (nextMatch !== -1 && nextMatch - r < 10) {
                r = nextMatch + 1;
                h++;
            } else {
                r++;
                h++;
            }
        }
    }
    
    while (r < rawText.length && isMarkdownSyntaxChar(rawText[r])) {
        r++;
    }
    
    return Math.min(r, rawText.length);
}

function isMarkdownSyntaxChar(char) {
    return char === '*' || char === '_' || char === '~' || char === '=' || 
           char === '[' || char === ']' || char === '(' || char === ')' || 
           char === '`' || char === '#' || char === '>';
}
