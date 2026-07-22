const requiredElement = id => {
    const element = document.getElementById(id);
    if (!element) {
        throw new Error(`Missing required editor element: #${id}`);
    }
    return element;
};

export const scrollContainer = requiredElement('scroll-container');
export const virtualSpacer = requiredElement('virtual-spacer');
export const viewport = requiredElement('viewport');
export const htmlLivePreview = requiredElement('html-live-preview');
export const findPanel = requiredElement('find-panel');
export const findInput = requiredElement('find-input');
export const findClear = requiredElement('find-clear');
export const findPrev = requiredElement('find-prev');
export const findNextButton = requiredElement('find-next');
export const findClose = requiredElement('find-close');
export const contextMenu = requiredElement('context-menu');
export const replaceInput = requiredElement('replace-input');
export const replaceClear = requiredElement('replace-clear');
export const replaceBtn = requiredElement('replace-btn');
export const replaceAllBtn = requiredElement('replace-all-btn');
export const csvToolbar = requiredElement('csv-toolbar');
export const csvNameBox = requiredElement('csv-name-box');
export const csvFormulaInput = requiredElement('csv-formula-input');
export const csvColumnHeader = requiredElement('csv-column-header');
export const csvColumnHeaderInner = requiredElement('csv-column-header-inner');
