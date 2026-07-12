import { contextMenu } from './editor-dom.js';
import { post, state } from './editor-core.js';
import {
    changeLineIndent,
    copySelectionToClipboard,
    cutSelectionToClipboard,
    deleteSelectionOrForward,
    handleFormatText,
    handleLineSortingAndCleanup,
    handleTextConversion,
    pasteFromClipboard,
    selectAll,
    toggleComment
} from './editor-commands.js';

// Context Menu Operations
export function showContextMenu(clientX, clientY) {
    for (const button of contextMenu.querySelectorAll('.context-menu-button')) {
        const requiresEdit = button.dataset.requiresEdit === 'true';
        button.disabled = requiresEdit && state.readOnly && !state.hexEditable;
    }

    const scrollSyncBtn = contextMenu.querySelector('[data-action="toggleScrollSync"]');
    if (scrollSyncBtn) {
        scrollSyncBtn.textContent = (state.scrollSyncEnabled ? '✓ ' : '') + (state.menuScrollSync || '스크롤 동기화');
    }

    contextMenu.hidden = false;
    const menuRect = contextMenu.getBoundingClientRect();
    const left = Math.min(clientX, window.innerWidth - menuRect.width - 4);
    const top = Math.min(clientY, window.innerHeight - menuRect.height - 4);
    contextMenu.style.left = `${Math.max(4, left)}px`;
    contextMenu.style.top = `${Math.max(4, top)}px`;

    for (const item of contextMenu.querySelectorAll('.context-menu-item.has-submenu')) {
        const submenu = item.querySelector(':scope > .submenu');
        if (!submenu) continue;

        const prevDisplay = submenu.style.display;
        const prevVisibility = submenu.style.visibility;
        submenu.style.visibility = 'hidden';
        submenu.style.display = 'block';
        submenu.style.left = '100%';
        submenu.style.right = 'auto';
        submenu.style.top = '-4px';
        submenu.style.bottom = 'auto';

        const itemRect = item.getBoundingClientRect();
        const sw = submenu.offsetWidth;
        const sh = submenu.offsetHeight;

        submenu.style.display = prevDisplay;
        submenu.style.visibility = prevVisibility;

        const goLeft = (itemRect.right + sw) > window.innerWidth;
        submenu.style.left = goLeft ? 'auto' : '100%';
        submenu.style.right = goLeft ? '100%' : 'auto';

        const goUp = (itemRect.top - 4 + sh) > window.innerHeight;
        submenu.style.top = goUp ? 'auto' : '-4px';
        submenu.style.bottom = goUp ? '-4px' : 'auto';
    }
}

export function hideContextMenu() {
    contextMenu.hidden = true;
}

export function bindContextMenu() {
    contextMenu.addEventListener('click', async event => {
    const button = event.target.closest('.context-menu-button');
    if (!button || button.disabled) return;
    const action = button.dataset.action;
    if (!action) return;

    hideContextMenu();

    switch (action) {
        case 'cut':
            await cutSelectionToClipboard();
            break;
        case 'copy':
            await copySelectionToClipboard();
            break;
        case 'paste':
            await pasteFromClipboard();
            break;
        case 'delete':
            deleteSelectionOrForward();
            break;
        case 'selectAll':
            selectAll();
            break;
        case 'toggleComment':
            toggleComment();
            break;
        case 'indentLines':
            changeLineIndent(1);
            break;
        case 'outdentLines':
            changeLineIndent(-1);
            break;
        case 'sortAsc':
        case 'sortDesc':
        case 'removeDuplicates':
        case 'removeEmptyLines':
        case 'collapseConsecutiveEmptyLines':
        case 'trimSpaces':
            handleLineSortingAndCleanup(action);
            break;
        case 'toUpperCase':
        case 'toLowerCase':
        case 'toSentenceCase':
        case 'toTitleCase':
        case 'insertDivider':
        case 'urlEncode':
        case 'urlDecode':
        case 'base64Encode':
        case 'base64Decode':
        case 'hexToDec':
        case 'decToHex':
            handleTextConversion(action);
            break;
        case 'formatText':
            handleFormatText();
            break;
        case 'toggleScrollSync':
            state.scrollSyncEnabled = !state.scrollSyncEnabled;
            post({ type: 'scrollSyncChanged', enabled: state.scrollSyncEnabled });
            break;
    }
});
}
