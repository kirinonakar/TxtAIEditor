import {
    configureDiagramRenderer, fencedCodeInfo, isFencedCodeClose, renderBlockAt, renderPreviewEmbeds
} from './markdown-preview-renderer.js';
import { shortcutNameFromKeyboardEvent } from './html-preview-frame-bridge.js';

const host = window.chrome.webview;
const history = document.getElementById('history');
const messages = document.getElementById('messages');
const turns = document.getElementById('turns');
const turnTitle = document.getElementById('turn-title');
const sessions = document.getElementById('sessions');
const sessionsTitle = document.getElementById('sessions-title');
const sessionsEmpty = document.getElementById('sessions-empty');
const newSessionButton = document.getElementById('new-session');
const closeAllSessionsButton = document.getElementById('close-all-sessions');
const copyAllButton = document.getElementById('copy-all');
const copyAllText = document.getElementById('copy-all-text');
const copyAllIcon = document.getElementById('copy-all-icon');
const composer = document.getElementById('composer');
const promptBox = document.getElementById('prompt');
const sendButton = document.getElementById('send');
const sendIcon = document.getElementById('send-icon');
const clearButton = document.getElementById('clear-prompt');
const attachHint = document.getElementById('attach-hint');
const attachHintText = document.getElementById('attach-hint-text');
const chipBar = document.getElementById('chip-bar');
const chipsRow = document.getElementById('chips');
const clearChipsButton = document.getElementById('clear-chips');
const attachmentBar = document.getElementById('attachment-bar');
const attachmentsRow = document.getElementById('attachments');
const clearAttachmentsButton = document.getElementById('clear-attachments');
const slashPalette = document.getElementById('slash-palette');
const slashCategoryOrder = ['plugin', 'mcp', 'skill', 'preset'];
const timestamp = /^\d{2}:\d{2}:\d{2}[ \t]+(.*)/;
const answerHeader = /^\[(?:Agent Response|assistant: (?:response|final answer))\]:?[ \t]*/i;
const logHeader = /^\[(?:Agent run\]|LLM API:|Tool (?:result|completed|failed)[:\]])/i;
const sectionHeader = /^\[(?:Agent tool call|assistant: (?:tool call|thinking|error)|tool: .+ (?:arguments|result)|Tool result: .+|Global agent rules|Retry detail: .+|Compressed context used for subsequent model requests)\]/i;
const sectionEnd = /^\[End (?:global agent rules|retry detail|compressed context)\]/i;
const markdownOptions = {
    allowHtml: false, allowUnderline: true, deferOpenDiagrams: true, diagramLoadingText: '', renderCharts: true,
    deferLocalImages: true, resolveImageUrl: resolveChatImageUrl
};

const drafts = new Map();
const pending = new Map();
const imageLoads = new Map();
const sessionButtons = new Map();
let promptRanges = [];
let pendingPrompts = [];
let hiddenOutput = [];
let messageViews = [];
let jumpLinks = [];
let selectedJump = null;
let selectedSession = null;
let jumpSyncFrame = 0;
let output = '';
let sessionId = null;
let untitledSessionLabel = '';
let runningSessionLabel = '';
let imageWorkspaceKey = null;
let nextImageRequestId = 0;
let composerSessionId = null;
let canSubmit = false;
let sendLabel = '';
let stopLabel = '';
let composing = false;
let followOutput = true;
let theme = '';
let usePele = false;
let thinkingLabel = '';
let toolStatusPrefixes = [];
let verboseToolPrefix = '';
let chartErrorLabel = '';
let copyLabel = '';
let copiedLabel = '';
let copyFailedLabel = '';
let copyAllLabel = '';
let branchLabel = '';
let userRoleLabel = '';
let assistantRoleLabel = '';
let sessionCopyId = null;
let sessionCopyFeedbackTimer;
let nextCopyId = 0;
let canBranch = false;
let canRewind = false;
let sessionItems = [];
let editLabel = '';
let editSendLabel = '';
let editCancelLabel = '';
let rewindLabel = '';

function scrollToEnd() {
    if (followOutput) history.scrollTop = history.scrollHeight;
    queueCurrentJumpSync();
}

function resolveChatImageUrl(source) {
    if (/^data:image\//i.test(source)) return source;
    const scheme = /^[a-z][a-z0-9+.-]*:/i.exec(source)?.[0].toLowerCase();
    if (!imageWorkspaceKey || source.startsWith('//') ||
        (scheme && scheme !== 'file:' && !/^[a-z]:[\\/]/i.test(source))) return '';
    return source;
}

function syncChatImages() {
    const visiblePaths = new Set();
    for (const img of messages.querySelectorAll('img[data-preview-image-source]')) {
        const path = img.dataset.previewImageSource;
        visiblePaths.add(path);
        let load = imageLoads.get(path);
        if (!load) {
            load = { requestId: String(++nextImageRequestId) };
            imageLoads.set(path, load);
            host.postMessage({ action: 'loadImage', workspaceKey: imageWorkspaceKey, requestId: load.requestId, path });
        }
        if (load.failed) replaceChatImageWithAlt(img);
        else if (load.url && img.getAttribute('src') !== load.url) {
            img.src = load.url;
            img.hidden = false;
        }
    }
    for (const [path, load] of imageLoads) {
        if (visiblePaths.has(path)) continue;
        if (load.url) URL.revokeObjectURL(load.url);
        imageLoads.delete(path);
    }
}

function replaceChatImageWithAlt(img) {
    img.replaceWith(document.createTextNode(img.alt || ''));
}

function getImageLoad({ workspaceKey, requestId, path }) {
    const load = imageLoads.get(path);
    return imageWorkspaceKey === workspaceKey && load?.requestId === requestId ? load : null;
}

history.addEventListener('scroll', () => {
    followOutput = history.scrollHeight - history.scrollTop - history.clientHeight < 44;
    queueCurrentJumpSync();
});
const jumpLayoutObserver = new ResizeObserver(queueCurrentJumpSync);
jumpLayoutObserver.observe(history);
jumpLayoutObserver.observe(messages);
messages.addEventListener('load', scrollToEnd, true);
messages.addEventListener('error', event => {
    if (event.target.dataset?.previewImageSource) replaceChatImageWithAlt(event.target);
}, true);

host.addEventListener('sharedbufferreceived', event => {
    const bytes = event.getBuffer();
    try {
        const data = event.additionalData;
        const load = data && getImageLoad(data);
        if (!load || load.url) return;
        load.url = URL.createObjectURL(new Blob([bytes], { type: data.contentType }));
        syncChatImages();
    } finally {
        host.releaseBuffer(bytes);
    }
});

function findInlineLogTag(line) {
    let codeSpanWidth = 0;
    for (const match of line.matchAll(/`+|<(think|thought|tool_call|log_tool_call)\b[^>]*>/gi)) {
        if (!match[1]) {
            if (!codeSpanWidth) codeSpanWidth = match[0].length;
            else if (codeSpanWidth === match[0].length) codeSpanWidth = 0;
        } else if (!codeSpanWidth) {
            return match;
        }
    }
    return null;
}

function hasEscapedMarkdownBlockSyntax(text) {
    return String(text ?? '').split(/\r\n?/).some(line => {
        const trimmed = line.trimStart();
        return /^\\#{1,6}\s/.test(trimmed) ||
            /^\\[-*+]\s/.test(trimmed) ||
            /^\\\|/.test(trimmed) ||
            /^\\(?=`{3,}|~{3,})/.test(trimmed) ||
            /^\d+\\[.)]\s/.test(trimmed);
    });
}

function unescapeMarkdownPunctuation(line) {
    return line.replace(/\\([\s\S])/g, (match, character) => {
        const code = character.charCodeAt(0);
        const isPunctuation = (code >= 33 && code <= 47) || (code >= 58 && code <= 64) ||
            (code >= 91 && code <= 96) || (code >= 123 && code <= 126);
        return isPunctuation ? character : match;
    });
}

function decodeEscapedMarkdownLine(line, preserveLeadingQuote = false) {
    const literalQuote = preserveLeadingQuote ? /^(\s*)\\>/.exec(line) : null;
    const withoutContinuation = line.endsWith('\\') ? line.slice(0, -1) : line;
    let decoded = unescapeMarkdownPunctuation(withoutContinuation);
    // An escaped leading > is literal text, not a Markdown blockquote.
    if (literalQuote) decoded = `${literalQuote[1]}\\${decoded.slice(literalQuote[1].length)}`;
    return decoded;
}

function assistantBlocks(text) {
    const blocks = [];
    const markdown = [];
    const decodeEscapedMarkdown = hasEscapedMarkdownBlockSyntax(text);
    let lines = [];
    function flushMarkdown() {
        let first = 0;
        let last = lines.length;
        while (first < last && !lines[first].trim()) first++;
        while (last > first && !lines[last - 1].trim()) last--;
        if (first < last) markdown.push(lines.slice(first, last).join('\n'));
        for (let number = 1; number <= lines.length;) {
            const block = renderBlockAt(number, lines.length, lineNumber => lines[lineNumber - 1], markdownOptions);
            if (block?.html) blocks.push({ html: block.html });
            number = (block?.endLine ?? number) + 1;
        }
        lines = [];
    }

    let fence = null;
    let inLogSection = false;
    let logTag = null;
    for (let line of text.replace(/\r\n?/g, '\n').split('\n')) {
        if (fence) {
            const fenceLine = decodeEscapedMarkdown ? decodeEscapedMarkdownLine(line) : line;
            lines.push(fenceLine);
            if (isFencedCodeClose(fenceLine, fence)) fence = null;
            continue;
        }
        const label = line.trimStart();
        const activity = timestamp.exec(label);
        if (activity) {
            inLogSection = false;
            logTag = null;
            const message = activity[1];
            if (thinkingLabel && (message === thinkingLabel ||
                [' ', '.'].includes(message.charAt(thinkingLabel.length)) && message.startsWith(thinkingLabel))) {
                flushMarkdown();
                blocks.push({ activity: line });
            }
            continue;
        }
        const answer = answerHeader.exec(label);
        if (answer) {
            inLogSection = false;
            logTag = null;
            line = label.slice(answer[0].length);
        } else if (sectionHeader.test(label) || (verboseToolPrefix && label.startsWith(verboseToolPrefix))) {
            inLogSection = true;
            continue;
        } else if (sectionEnd.test(label)) {
            inLogSection = false;
            continue;
        } else if (inLogSection || logHeader.test(label) || toolStatusPrefixes.some(prefix => label.startsWith(prefix))) {
            continue;
        }

        if (decodeEscapedMarkdown) {
            line = decodeEscapedMarkdownLine(line, true);
        }
        fence = logTag ? null : fencedCodeInfo(line);
        if (!fence) {
            // Keep prose before or after native inline reasoning and tool-call payloads.
            let visible = '';
            while (line) {
                if (logTag) {
                    const end = line.toLowerCase().indexOf(`</${logTag}>`);
                    if (end < 0) break;
                    line = line.slice(end + logTag.length + 3);
                    logTag = null;
                } else {
                    const start = findInlineLogTag(line);
                    if (!start) {
                        visible += line;
                        break;
                    }
                    visible += line.slice(0, start.index);
                    line = line.slice(start.index + start[0].length);
                    logTag = start[1].toLowerCase();
                }
            }
            line = visible;
        }
        lines.push(line);
    }
    flushMarkdown();
    return { blocks, copyText: markdown.join('\n\n') };
}

function renderAssistant(view, text) {
    const { blocks, copyText } = assistantBlocks(text);
    const content = view.content;
    view.copyText = copyText;
    view.copyButton.hidden = !copyText;
    view.node.hidden = !blocks.length;
    syncBranchButton(view);

    for (const [index, block] of blocks.entries()) {
        let node = content.children[index];
        const workspace = block.html?.includes('data-preview-image-source=') ? imageWorkspaceKey : null;
        if (node && node.blockHtml === block.html && node.activity === block.activity && node.imageWorkspace === workspace) continue;
        if (!node) {
            node = document.createElement('div');
            content.append(node);
        }
        node.blockHtml = block.html;
        node.activity = block.activity;
        node.imageWorkspace = workspace;
        node.className = block.activity === undefined ? 'block' : 'activity';
        if (block.activity === undefined) {
            node.innerHTML = block.html;
            for (const cell of node.querySelectorAll('th, td')) {
                if (cell.textContent.trim().length <= 3) cell.classList.add('short-cell');
            }
        } else {
            node.textContent = block.activity;
        }
    }
    while (content.children.length > blocks.length) content.lastElementChild.remove();
    void renderPreviewEmbeds(content, scrollToEnd, { errorLabel: chartErrorLabel });
}

function createMessageView(role, start) {
    const node = document.createElement('div');
    node.className = `message ${role}`;
    const content = document.createElement('div');
    content.className = 'message-content';
    const copyButton = document.createElement('button');
    copyButton.type = 'button';
    copyButton.className = 'message-copy';
    copyButton.textContent = copyLabel;
    copyButton.setAttribute('aria-live', 'polite');
    copyButton.hidden = true;
    const editButton = document.createElement('button');
    editButton.type = 'button';
    editButton.className = 'message-copy message-edit';
    editButton.hidden = true;
    const rewindButton = document.createElement('button');
    rewindButton.type = 'button';
    rewindButton.className = 'message-copy message-rewind';
    rewindButton.hidden = true;
    const branchButton = document.createElement('button');
    branchButton.type = 'button';
    branchButton.className = 'message-copy message-branch';
    branchButton.hidden = true;
    const actions = document.createElement('div');
    actions.className = 'message-actions';
    actions.append(copyButton, editButton, rewindButton, branchButton);
    node.append(content, actions);

    const view = {
        node, content, actions, role, start, copyButton, editButton, rewindButton, branchButton,
        copyId: String(++nextCopyId), copyText: '', editing: false, editor: null
    };
    copyButton.addEventListener('click', () => {
        if (view.copyText) {
            host.postMessage({ action: 'copy', copyId: view.copyId, text: view.copyText });
        }
    });
    editButton.addEventListener('click', () => beginEdit(view));
    rewindButton.addEventListener('click', () => rewindTo(view));
    branchButton.addEventListener('click', () => {
        if (view.role !== 'assistant' || view.end == null || !sessionId) return;
        host.postMessage({ action: 'branch', sessionId, offset: view.end });
    });
    syncBranchButton(view);
    syncEditButtons(view);
    return view;
}

function syncBranchButton(view) {
    const visible = view.role === 'assistant' && canBranch && !!branchLabel && !view.node.hidden;
    view.branchButton.textContent = branchLabel;
    view.branchButton.title = branchLabel;
    view.branchButton.setAttribute('aria-label', branchLabel);
    view.branchButton.hidden = !visible;
}

function isCurrentSessionRunning() {
    return sessionItems.some(item => item.id === sessionId && item.isRunning);
}

// While the current session is running and no follow-up text is pending, the composer button stops the run.
function isStopMode() {
    return canSubmit && !!sessionId && isCurrentSessionRunning() && !promptBox.value.trim();
}

function setMessageButton(button, label, visible) {
    if (!label || !visible) {
        button.hidden = true;
        return;
    }
    if (button.textContent !== label) {
        button.textContent = label;
        button.title = label;
        button.setAttribute('aria-label', label);
    }
    button.hidden = false;
}

function syncEditButtons(view) {
    const running = isCurrentSessionRunning();
    const editable = view.role === 'user' && view.start != null && !!view.copyText && canSubmit && !running && !view.editing;
    const rewindable = view.role === 'user' && view.start != null && canRewind && !running && !view.editing;
    setMessageButton(view.editButton, editLabel, editable);
    setMessageButton(view.rewindButton, rewindLabel, rewindable);
}

function rewindTo(view) {
    if (view.role !== 'user' || view.start == null || !sessionId) return;
    host.postMessage({ action: 'rewindPrompt', sessionId, offset: view.start, prompt: view.copyText ?? '' });
}

function beginEdit(view) {
    if (view.role !== 'user' || view.start == null || view.editing) return;
    const editor = ensureEditor(view);
    view.editing = true;
    view.node.classList.add('editing');
    editor.input.value = view.copyText;
    editor.node.hidden = false;
    view.content.hidden = true;
    // Keep the copy action on the same row as cancel/send instead of above the editor.
    if (view.copyText) {
        editor.actions.prepend(view.copyButton);
        view.copyButton.hidden = false;
    }
    resizeEditorInput(editor.input);
    syncEditButtons(view);
    syncEditorButtons(view);
    editor.input.focus();
    editor.input.setSelectionRange(editor.input.value.length, editor.input.value.length);
}

function endEdit(view) {
    if (!view.editing) return;
    view.editing = false;
    view.node.classList.remove('editing');
    if (view.editor) {
        view.editor.node.hidden = true;
        view.actions.insertBefore(view.copyButton, view.editButton);
    }
    view.content.hidden = false;
    view.copyButton.hidden = !view.copyText;
    syncEditButtons(view);
}

function ensureEditor(view) {
    if (view.editor) return view.editor;
    const node = document.createElement('div');
    node.className = 'message-editor';
    node.hidden = true;
    const input = document.createElement('textarea');
    input.className = 'message-editor-input';
    input.rows = 2;
    input.spellcheck = false;
    const actions = document.createElement('div');
    actions.className = 'message-editor-actions';
    const cancelButton = document.createElement('button');
    cancelButton.type = 'button';
    cancelButton.className = 'message-copy';
    const sendButton = document.createElement('button');
    sendButton.type = 'button';
    sendButton.className = 'message-copy message-editor-send';
    actions.append(cancelButton, sendButton);
    node.append(input, actions);
    view.node.append(node);

    const editor = { node, input, actions, cancelButton, sendButton, composing: false };
    view.editor = editor;
    cancelButton.addEventListener('click', () => endEdit(view));
    sendButton.addEventListener('click', () => submitEdit(view));
    input.addEventListener('input', () => {
        resizeEditorInput(input);
        syncEditorButtons(view);
    });
    input.addEventListener('compositionstart', () => { editor.composing = true; });
    input.addEventListener('compositionend', () => { editor.composing = false; });
    input.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            event.preventDefault();
            endEdit(view);
            return;
        }
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || editor.composing || event.keyCode === 229) return;
        event.preventDefault();
        submitEdit(view);
    });
    syncEditorButtons(view);
    return editor;
}

function syncEditorButtons(view) {
    const editor = view.editor;
    if (!editor) return;
    if (editor.cancelButton.textContent !== editCancelLabel) {
        editor.cancelButton.textContent = editCancelLabel;
        editor.cancelButton.title = editCancelLabel;
        editor.cancelButton.setAttribute('aria-label', editCancelLabel);
    }
    if (editor.sendButton.textContent !== editSendLabel) {
        editor.sendButton.textContent = editSendLabel;
        editor.sendButton.title = editSendLabel;
        editor.sendButton.setAttribute('aria-label', editSendLabel);
    }
    editor.sendButton.disabled = !editor.input.value.trim();
}

function resizeEditorInput(input) {
    input.style.height = 'auto';
    input.style.height = `${input.scrollHeight}px`;
}

function submitEdit(view) {
    const editor = view.editor;
    if (!editor || !sessionId || view.start == null) return;
    const prompt = editor.input.value.trim();
    if (!prompt) {
        editor.input.focus();
        return;
    }
    const offset = view.start;
    view.copyText = prompt;
    view.content.textContent = prompt;
    endEdit(view);
    host.postMessage({ action: 'editPrompt', sessionId, offset, prompt });
}

function renderConversation(offset, renderAll) {
    const nextViews = [];
    let index = 0;
    let hiddenIndex = 0;

    function visibleOutput(start, end) {
        let text = '';
        let position = start;
        while (hiddenIndex < hiddenOutput.length && hiddenOutput[hiddenIndex].end <= start) hiddenIndex++;
        for (let i = hiddenIndex; i < hiddenOutput.length && hiddenOutput[i].start < end; i++) {
            const { start: hiddenStart, end: hiddenEnd } = hiddenOutput[i];
            if (hiddenStart > position) text += output.slice(position, hiddenStart);
            position = hiddenEnd;
        }
        return text + (position < end ? output.slice(position, end) : '');
    }

    function show(role, start, end, userText = '', preamble = '') {
        const previous = messageViews[index];
        let view = previous;
        const reusable = previous?.role === role && previous.start === start;
        if (!reusable) {
            view = createMessageView(role, start);
            if (previous) previous.node.replaceWith(view.node);
            else messages.append(view.node);
        }
        if (role === 'user') {
            if (!view.editing && view.copyText !== userText) view.content.textContent = userText;
            view.copyText = userText;
            view.copyButton.hidden = !userText;
            view.node.hidden = !userText;
            syncEditButtons(view);
        } else if (!reusable || renderAll || view.end !== end || offset < end) {
            renderAssistant(view, preamble + visibleOutput(start, end));
        }
        view.end = end;
        nextViews.push(view);
        index++;
        return view;
    }

    const targets = [];
    for (const [turn, range] of promptRanges.entries()) {
        targets.push(show('user', range.start, range.end, range.text));
        const end = promptRanges[turn + 1]?.start ?? output.length;
        show('assistant', range.end, end, '', turn === 0 ? visibleOutput(0, range.start) : '');
    }
    if (!promptRanges.length) show('assistant', 0, output.length);
    for (const text of pendingPrompts) targets.push(show('user', null, null, text));
    for (; index < messageViews.length; index++) messageViews[index].node.remove();
    messageViews = nextViews;
    syncChatImages();
    return targets;
}

function jumpTop(target) {
    const margin = Number.parseFloat(getComputedStyle(target).scrollMarginTop) || 0;
    return target.getBoundingClientRect().top - margin;
}

function findCurrentJump() {
    if (!jumpLinks.length) return null;
    const lastScrollTop = history.scrollHeight - history.clientHeight;
    // The last request may be too close to the end to reach the top of the viewport.
    if (lastScrollTop > 0 && history.scrollTop >= lastScrollTop - 1) return jumpLinks.at(-1).button;

    const top = history.getBoundingClientRect().top + history.clientTop;
    let first = 0;
    let end = jumpLinks.length;
    while (first < end) {
        const middle = Math.floor((first + end) / 2);
        if (jumpTop(jumpLinks[middle].target) <= top + 1) first = middle + 1;
        else end = middle;
    }
    return jumpLinks[Math.max(0, first - 1)].button;
}

function queueCurrentJumpSync() {
    if (jumpSyncFrame) return;
    jumpSyncFrame = requestAnimationFrame(() => {
        jumpSyncFrame = 0;
        if (!history.clientHeight) return;
        const current = findCurrentJump();
        if (current === selectedJump) return;
        selectedJump?.removeAttribute('aria-current');
        selectedJump = current;
        if (!current) return;

        current.setAttribute('aria-current', 'location');
        revealSidebarButton(turns, current);
    });
}

function revealSidebarButton(list, button) {
    const top = list.getBoundingClientRect().top + list.clientTop;
    const bottom = top + list.clientHeight;
    const bounds = button.getBoundingClientRect();
    if (bounds.top < top) list.scrollTop += bounds.top - top;
    else if (bounds.bottom > bottom) list.scrollTop += bounds.bottom - bottom;
}

function showJumpLinks(targets) {
    const visibleTargets = targets.filter(({ node }) => !node.hidden);
    for (const [index, { node: target, copyText }] of visibleTargets.entries()) {
        const text = copyText.trim().replace(/\s+/g, ' ');
        const excerpt = text.slice(0, 200);
        const existing = jumpLinks[index];
        if (existing?.target === target && existing.excerpt === excerpt) continue;
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'turn-link';
        button.title = `${index + 1}. ${excerpt}`;
        button.setAttribute('aria-label', button.title);
        const number = document.createElement('span');
        number.className = 'turn-number';
        number.textContent = String(index + 1);
        const summary = document.createElement('span');
        summary.className = 'turn-excerpt';
        summary.textContent = excerpt;
        button.append(number, summary);
        button.addEventListener('click', () => {
            followOutput = false;
            history.scrollTop += jumpTop(target) - history.getBoundingClientRect().top - history.clientTop;
            queueCurrentJumpSync();
        });
        if (existing) existing.button.replaceWith(button);
        else turns.append(button);
        jumpLinks[index] = { target, excerpt, button };
    }
    while (jumpLinks.length > visibleTargets.length) jumpLinks.pop().button.remove();
}

function syncSessionSelection() {
    const current = sessionButtons.get(sessionId);
    if (selectedSession === current) return;
    selectedSession?.removeAttribute('aria-current');
    selectedSession = current;
    if (current) {
        current.setAttribute('aria-current', 'true');
        revealSidebarButton(sessions, current);
    }
}

function showSessions(items) {
    sessionItems = items;
    const visibleIds = new Set(items.map(item => item.id));
    for (const [id, button] of sessionButtons) {
        if (visibleIds.has(id)) continue;
        button.remove();
        sessionButtons.delete(id);
    }

    let cursor = sessions.firstElementChild;
    for (const { id, title, canSelect, isRunning, completedNotificationCount, branch } of items) {
        let button = sessionButtons.get(id);
        if (!button) {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'session-link';
            const name = document.createElement('span');
            name.className = 'session-name';
            const status = document.createElement('span');
            status.className = 'session-status';
            status.setAttribute('aria-hidden', 'true');
            const connector = document.createElement('span');
            connector.className = 'session-connector';
            connector.setAttribute('aria-hidden', 'true');
            button.append(name, status, connector);
            button.addEventListener('click', () => {
                if (sessionId !== id) host.postMessage({ action: 'selectSession', sessionId: id });
            });
            sessionButtons.set(id, button);
        }
        const name = title || untitledSessionLabel;
        const label = isRunning ? `${name} (${runningSessionLabel})` : name;
        if (button.firstElementChild.textContent !== name) button.firstElementChild.textContent = name;
        button.title = label;
        button.setAttribute('aria-label', label);
        button.disabled = !canSelect;
        button.dataset.branch = branch;
        button.classList.toggle('session-running', isRunning);
        button.classList.toggle('session-unread', completedNotificationCount > 0);
        if (button !== cursor) sessions.insertBefore(button, cursor);
        cursor = button.nextElementSibling;
    }
    sessionsEmpty.hidden = items.length > 0;
    closeAllSessionsButton.disabled = items.length === 0;
    syncSessionSelection();
    syncComposer();
}

function setCopyAllLabel(label, icon = '⧉') {
    copyAllText.textContent = label;
    copyAllIcon.textContent = icon;
    copyAllButton.title = label;
    copyAllButton.setAttribute('aria-label', label);
}

copyAllButton.addEventListener('click', () => {
    const entries = messageViews.filter(view => view.copyText);
    if (!entries.length) return;
    const text = entries.map(view => {
        const role = view.role === 'user' ? userRoleLabel : assistantRoleLabel;
        return `## ${role}\n\n${view.copyText}`;
    }).join('\n\n');
    sessionCopyId = String(++nextCopyId);
    host.postMessage({ action: 'copy', copyId: sessionCopyId, text });
});

newSessionButton.addEventListener('click', () => {
    host.postMessage({ action: 'newSession' });
});

closeAllSessionsButton.addEventListener('click', () => {
    host.postMessage({ action: 'closeAllSessions' });
});

function showChips(items) {
    const entries = Array.isArray(items) ? items : [];
    chipsRow.replaceChildren();
    for (const entry of entries) {
        const chip = document.createElement('span');
        chip.className = 'composer-chip';
        const label = document.createElement('span');
        label.className = 'composer-chip-label';
        label.textContent = entry.label ?? entry.name ?? '';
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'composer-chip-remove';
        remove.textContent = '\u00d7';
        const removeLabel = entry.removeTooltip || entry.label || entry.name || '';
        remove.title = removeLabel;
        remove.setAttribute('aria-label', removeLabel);
        remove.addEventListener('click', () => {
            host.postMessage({ action: 'removeChip', kind: entry.kind, name: entry.name });
        });
        chip.append(label, remove);
        chipsRow.append(chip);
    }
    chipBar.hidden = chipsRow.childElementCount === 0;
}

function showAttachments(items) {
    const entries = Array.isArray(items) ? items : [];
    attachmentsRow.replaceChildren();
    for (const entry of entries) {
        const chip = document.createElement('span');
        chip.className = 'composer-attachment';
        const label = document.createElement('span');
        label.className = 'composer-attachment-label';
        label.textContent = entry.label ?? '';
        chip.title = [entry.label, entry.detail, entry.token].filter(Boolean).join(' · ');
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'composer-attachment-remove';
        remove.textContent = '\u00d7';
        const removeLabel = entry.removeTooltip || entry.label || '';
        remove.title = removeLabel;
        remove.setAttribute('aria-label', removeLabel);
        remove.addEventListener('click', () => {
            host.postMessage({ action: 'removeAttachment', id: entry.id });
        });
        chip.append(label, remove);
        attachmentsRow.append(chip);
    }
    attachmentBar.hidden = attachmentsRow.childElementCount === 0;
}

clearChipsButton.addEventListener('click', () => {
    host.postMessage({ action: 'clearChips' });
});

clearAttachmentsButton.addEventListener('click', () => {
    host.postMessage({ action: 'clearAttachments' });
});

function syncComposer() {
    if (!composing) {
        if (composerSessionId !== sessionId) {
            composerSessionId = sessionId;
            promptBox.value = drafts.get(sessionId) ?? '';
        }
        // Disabling or hiding a focused textarea tears down the WebView2 IME host, which can drop
        // a Korean IME back to alphanumeric mode mid-typing. Defer the state change until the
        // composer loses focus; the send button and submit handler still block unusable prompts.
        if (document.activeElement !== promptBox) {
            composer.hidden = !canSubmit;
            promptBox.disabled = !canSubmit;
        }
    }
    const stopMode = isStopMode();
    sendButton.classList.toggle('composer-stop', stopMode);
    sendIcon.textContent = stopMode ? '■' : '↑';
    const buttonLabel = stopMode ? stopLabel : sendLabel;
    if (buttonLabel && sendButton.title !== buttonLabel) {
        sendButton.title = buttonLabel;
        sendButton.setAttribute('aria-label', buttonLabel);
    }
    sendButton.disabled = stopMode
        ? false
        : !canSubmit || composerSessionId !== sessionId ||
            pending.has(sessionId) || (!composing && !promptBox.value.trim());
    clearButton.hidden = promptBox.value.length === 0;
    if (composer.hidden || promptBox.disabled) {
        closeSlashPalette();
    }
}

function prefillComposer(text) {
    if (!text || composing) return;
    drafts.set(sessionId, text);
    composerSessionId = sessionId;
    promptBox.value = text;
    syncComposer();
    if (canSubmit && !promptBox.disabled) {
        promptBox.focus();
        promptBox.setSelectionRange(text.length, text.length);
    }
}

const slashState = { open: false, loaded: false, items: [], matches: [], index: 0, start: 0, end: 0, query: '' };
let slashLabels = { plugin: '', mcp: '', skill: '', preset: '', empty: '', active: '' };

function getSlashToken() {
    if (composing || promptBox.disabled || composer.hidden) return null;
    const value = promptBox.value;
    const caret = promptBox.selectionStart ?? 0;
    if (promptBox.selectionEnd !== caret) return null;
    let start = caret;
    while (start > 0 && !/\s/.test(value[start - 1])) start--;
    if (start >= caret || value[start] !== '/') return null;
    if (start > 0 && !/\s/.test(value[start - 1])) return null;
    return { start, end: caret, query: value.slice(start + 1, caret) };
}

function syncSlashPalette() {
    const token = getSlashToken();
    if (!token) {
        closeSlashPalette();
        return;
    }

    const wasOpen = slashState.open;
    slashState.open = true;
    slashState.start = token.start;
    slashState.end = token.end;
    slashState.query = token.query;
    if (!wasOpen) {
        slashState.index = 0;
        host.postMessage({ action: 'paletteRequest' });
    }

    applySlashFilter();
}

function closeSlashPalette() {
    if (!slashState.open && slashPalette.hidden) return;
    slashState.open = false;
    slashState.matches = [];
    slashState.index = 0;
    slashPalette.hidden = true;
    slashPalette.replaceChildren();
}

function applySlashFilter() {
    const query = slashState.query.trim().toLowerCase();
    slashState.matches = slashState.items
        .filter(item => matchesSlashQuery(item, query))
        .sort(compareSlashCategories);
    if (slashState.index >= slashState.matches.length) {
        slashState.index = 0;
    }

    renderSlashPalette();
}

function matchesSlashQuery(item, query) {
    if (!query) return true;
    const category = slashLabels[item.category] || item.category || '';
    return `${item.name ?? ''}\n${item.description ?? ''}\n${category}`.toLowerCase().includes(query);
}

function compareSlashCategories(left, right) {
    const leftIndex = slashCategoryOrder.indexOf(left.category);
    const rightIndex = slashCategoryOrder.indexOf(right.category);
    return (leftIndex < 0 ? slashCategoryOrder.length : leftIndex) -
        (rightIndex < 0 ? slashCategoryOrder.length : rightIndex);
}

function renderSlashPalette() {
    if (!slashState.open) {
        closeSlashPalette();
        return;
    }

    slashPalette.replaceChildren();
    if (!slashState.loaded) {
        // The host list has not arrived yet; keep the palette out of the way instead of
        // showing a misleading "no matching items" message.
        slashPalette.hidden = true;
        return;
    }

    if (slashState.matches.length === 0) {
        const empty = document.createElement('p');
        empty.className = 'slash-palette-empty';
        empty.textContent = slashLabels.empty;
        slashPalette.append(empty);
    } else {
        let category = null;
        slashState.matches.forEach((item, index) => {
            if (item.category !== category) {
                category = item.category;
                const header = document.createElement('p');
                header.className = 'slash-palette-group';
                header.textContent = slashLabels[category] || category;
                slashPalette.append(header);
            }

            slashPalette.append(createSlashRow(item, index));
        });
    }

    slashPalette.hidden = false;
    positionSlashPalette();
    const activeRow = slashPalette.querySelector('.slash-palette-item.active');
    if (activeRow) activeRow.scrollIntoView({ block: 'nearest' });
}

function createSlashRow(item, index) {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'slash-palette-item' + (index === slashState.index ? ' active' : '');
    row.dataset.index = String(index);
    row.setAttribute('aria-pressed', item.active ? 'true' : 'false');
    row.title = item.description || item.name || '';
    if (item.active) {
        row.setAttribute('aria-label', `${item.name} (${slashLabels.active})`);
    }

    const check = document.createElement('span');
    check.className = 'slash-palette-check';
    check.setAttribute('aria-hidden', 'true');
    check.textContent = item.active ? '✓' : '';

    const name = document.createElement('span');
    name.className = 'slash-palette-name';
    name.textContent = item.name ?? '';

    const detail = document.createElement('span');
    detail.className = 'slash-palette-detail';
    detail.textContent = item.description ?? '';

    row.append(check, name, detail);
    row.addEventListener('mousedown', event => event.preventDefault());
    row.addEventListener('click', () => chooseSlashItem(index));
    return row;
}

function positionSlashPalette() {
    if (composer.hidden) return;
    const rect = composer.getBoundingClientRect();
    const width = Math.min(Math.max(rect.width, 280), 560);
    const maxLeft = Math.max(12, window.innerWidth - width - 12);
    const left = Math.min(Math.max(rect.left + (rect.width - width) / 2, 12), maxLeft);
    slashPalette.style.left = `${Math.round(left)}px`;
    slashPalette.style.width = `${Math.round(width)}px`;
    slashPalette.style.bottom = `${Math.round(window.innerHeight - rect.top + 8)}px`;
    slashPalette.style.maxHeight = `${Math.max(140, Math.round(rect.top - 24))}px`;
}

function chooseSlashItem(index) {
    const item = slashState.matches[index];
    if (!item) return;
    consumeSlashToken();
    host.postMessage({ action: 'paletteActivate', id: item.id, active: !item.active });
    if (!promptBox.disabled && document.activeElement !== promptBox) {
        promptBox.focus();
    }
}

function consumeSlashToken() {
    const value = promptBox.value;
    const start = slashState.start;
    const end = Math.min(slashState.end, value.length);
    if (value[start] !== '/') {
        closeSlashPalette();
        return;
    }

    const next = value.slice(0, start) + value.slice(end);
    promptBox.value = next;
    promptBox.setSelectionRange(start, start);
    drafts.set(composerSessionId, next);
    closeSlashPalette();
    syncComposer();
}

function handleSlashKeyDown(event) {
    if (!slashState.open || event.isComposing || composing || event.keyCode === 229) return false;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        const count = slashState.matches.length;
        if (count === 0) return false;
        const delta = event.key === 'ArrowDown' ? 1 : -1;
        slashState.index = (slashState.index + delta + count) % count;
        renderSlashPalette();
        return true;
    }

    if (event.key === 'Tab' || event.key === 'Enter') {
        if (slashState.matches.length === 0) {
            closeSlashPalette();
            return false;
        }

        chooseSlashItem(slashState.index);
        return true;
    }

    if (event.key === 'Escape') {
        closeSlashPalette();
        return true;
    }

    return false;
}

window.addEventListener('resize', () => {
    if (slashState.open) positionSlashPalette();
});

function update(message) {
    const switched = sessionId !== message.sessionId;
    const imageWorkspaceChanged = imageWorkspaceKey !== message.imageWorkspaceKey;
    if (imageWorkspaceChanged) {
        for (const load of imageLoads.values()) {
            if (load.url) URL.revokeObjectURL(load.url);
        }
        imageLoads.clear();
        imageWorkspaceKey = message.imageWorkspaceKey;
    }
    if (switched) {
        sessionId = message.sessionId;
        syncSessionSelection();
        messages.replaceChildren();
        turns.replaceChildren();
        messageViews = [];
        jumpLinks = [];
        selectedJump = null;
        output = '';
        promptRanges = [];
        pendingPrompts = [];
        hiddenOutput = [];
        followOutput = true;
        sessionCopyId = null;
        clearTimeout(sessionCopyFeedbackTimer);
        setCopyAllLabel(copyAllLabel);
    }

    const nextTheme = /dark/i.test(message.theme) ? 'Dark' : 'Light';
    const appearanceChanged = theme !== nextTheme || usePele !== message.usePele;
    if (appearanceChanged) {
        theme = nextTheme;
        usePele = message.usePele;
        document.documentElement.dataset.theme = theme.toLowerCase();
        configureDiagramRenderer(usePele, theme, '', 0, true);
    }

    const nextOutput = output.slice(0, message.offset) + message.text;
    if (message.prompts) promptRanges = message.prompts;
    if (message.pendingPrompts) pendingPrompts = message.pendingPrompts;
    if (message.hiddenOutput) hiddenOutput = message.hiddenOutput;
    if (switched || appearanceChanged || imageWorkspaceChanged || message.prompts || message.pendingPrompts || message.hiddenOutput || nextOutput !== output) {
        const offset = message.hiddenOutput && nextOutput === output ? 0 : message.offset;
        output = nextOutput;
        const targets = renderConversation(offset, appearanceChanged || imageWorkspaceChanged);
        copyAllButton.disabled = !messageViews.some(view => view.copyText);
        if (switched || message.prompts || message.pendingPrompts) showJumpLinks(targets);
    }
    canSubmit = message.canSubmit;
    canRewind = message.canRewind === true;
    if (canBranch !== canSubmit) {
        canBranch = canSubmit;
        for (const view of messageViews) syncBranchButton(view);
    }
    for (const view of messageViews) syncEditButtons(view);
    syncComposer();
    scrollToEnd();
}

clearButton.addEventListener('click', () => {
    if (composing) return;
    closeSlashPalette();
    promptBox.value = '';
    drafts.delete(composerSessionId);
    syncComposer();
    if (!promptBox.disabled) {
        promptBox.focus();
    }
});
promptBox.addEventListener('input', () => {
    drafts.set(composerSessionId, promptBox.value);
    syncComposer();
    syncSlashPalette();
});
promptBox.addEventListener('compositionstart', () => {
    composing = true;
    syncComposer();
});
promptBox.addEventListener('compositionend', () => {
    composing = false;
    requestAnimationFrame(() => {
        syncComposer();
        syncSlashPalette();
    });
});
promptBox.addEventListener('blur', () => requestAnimationFrame(syncComposer));
window.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.isComposing || composing || event.keyCode === 229) return;
    if (event.key === 'F7') {
        event.preventDefault();
        return;
    }
    const altOnly = event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey;
    const shortcut = altOnly && event.key === 'ArrowLeft' ? 'previousTab'
        : altOnly && event.key === 'ArrowRight' ? 'nextTab'
        : shortcutNameFromKeyboardEvent(event);
    if (!shortcut) return;
    event.preventDefault();
    event.stopPropagation();
    if (!event.repeat) host.postMessage({ action: 'shortcut', name: shortcut });
}, true);
promptBox.addEventListener('keydown', event => {
    if (!handleSlashKeyDown(event)) return;
    event.preventDefault();
    event.stopPropagation();
});
promptBox.addEventListener('keyup', () => {
    if (!composing) syncSlashPalette();
});
promptBox.addEventListener('click', () => {
    if (!composing) syncSlashPalette();
});
promptBox.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.key !== 'Enter' || event.shiftKey || event.isComposing || composing || event.keyCode === 229) return;
    event.preventDefault();
    // An empty composer shows the stop button, so Enter must not cancel the run accidentally.
    if (!promptBox.value.trim()) return;
    composer.requestSubmit();
});
composer.addEventListener('submit', event => {
    event.preventDefault();
    closeSlashPalette();
    if (isStopMode()) {
        host.postMessage({ action: 'stop', sessionId });
        return;
    }
    if (sendButton.disabled || composing || !promptBox.value.trim()) return;
    const prompt = promptBox.value;
    pending.set(sessionId, prompt);
    followOutput = true;
    host.postMessage({ action: 'submit', sessionId, prompt });
    syncComposer();
});

function hasDraggedFiles(event) {
    const transfer = event.dataTransfer;
    return !!transfer && Array.from(transfer.types ?? []).includes('Files');
}

window.addEventListener('dragenter', event => {
    if (!hasDraggedFiles(event)) return;
    event.preventDefault();
    if (attachHint) attachHint.hidden = false;
}, true);
window.addEventListener('dragover', event => {
    if (!hasDraggedFiles(event)) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    if (attachHint) attachHint.hidden = false;
}, true);
window.addEventListener('dragleave', event => {
    if (event.relatedTarget || !attachHint) return;
    attachHint.hidden = true;
}, true);
window.addEventListener('drop', event => {
    if (!hasDraggedFiles(event)) return;
    event.preventDefault();
    event.stopPropagation();
    if (attachHint) attachHint.hidden = true;
    const files = Array.from(event.dataTransfer?.files ?? []);
    if (files.length === 0) return;
    // WebView2 gives the host the absolute path of each dropped file through
    // postMessageWithAdditionalObjects, which the HTML5 File object never exposes.
    host.postMessageWithAdditionalObjects({ action: 'attachFiles' }, files);
}, true);

host.addEventListener('message', ({ data }) => {
    switch (data.action) {
        case 'labels':
            document.title = data.title;
            history.setAttribute('aria-label', data.title);
            turnTitle.textContent = data.turns;
            sessionsTitle.textContent = data.sessions;
            sessionsEmpty.textContent = data.emptySessions;
            newSessionButton.title = data.newSession;
            newSessionButton.setAttribute('aria-label', data.newSession);
            newSessionButton.disabled = false;
            closeAllSessionsButton.title = data.closeAllSessions;
            closeAllSessionsButton.setAttribute('aria-label', data.closeAllSessions);
            untitledSessionLabel = data.untitledSession;
            runningSessionLabel = data.runningSession;
            promptBox.placeholder = data.prompt;
            promptBox.setAttribute('aria-label', data.prompt);
            sendLabel = data.send;
            stopLabel = data.stop;
            sendButton.title = data.send;
            sendButton.setAttribute('aria-label', data.send);
            syncComposer();
            clearButton.title = data.clear;
            clearButton.setAttribute('aria-label', data.clear);
            thinkingLabel = data.thinking;
            toolStatusPrefixes = data.toolStatusPrefixes.filter(Boolean);
            verboseToolPrefix = data.verboseToolPrefix;
            chartErrorLabel = data.chartErrorLabel;
            copyLabel = data.copy;
            copiedLabel = data.copied;
            copyFailedLabel = data.copyFailed;
            copyAllLabel = data.copyAll;
            branchLabel = data.branch;
            editLabel = data.edit;
            editSendLabel = data.editSend;
            editCancelLabel = data.cancel;
            rewindLabel = data.rewind;
            userRoleLabel = data.userRole;
            assistantRoleLabel = data.assistantRole;
            clearChipsButton.title = data.clearChips ?? '';
            clearChipsButton.setAttribute('aria-label', data.clearChips ?? '');
            clearAttachmentsButton.title = data.clearAttachments ?? '';
            clearAttachmentsButton.setAttribute('aria-label', data.clearAttachments ?? '');
            if (attachHintText) attachHintText.textContent = data.attachFiles ?? '';
            slashLabels = {
                plugin: data.palettePlugin ?? '',
                mcp: data.paletteMcp ?? '',
                skill: data.paletteSkill ?? '',
                preset: data.palettePreset ?? '',
                empty: data.paletteEmpty ?? '',
                active: data.paletteActive ?? ''
            };
            slashPalette.setAttribute('aria-label', data.prompt ?? '');
            if (slashState.open) renderSlashPalette();
            setCopyAllLabel(copyAllLabel);
            for (const view of messageViews) {
                syncBranchButton(view);
                syncEditButtons(view);
                syncEditorButtons(view);
            }
            break;
        case 'update':
            update(data);
            break;
        case 'sessions':
            showSessions(data.items);
            break;
        case 'chips':
            showChips(data.items);
            break;
        case 'attachments':
            showAttachments(data.items);
            break;
        case 'paletteItems':
            slashState.items = Array.isArray(data.items) ? data.items : [];
            slashState.loaded = true;
            if (slashState.open) applySlashFilter();
            break;
        case 'imageUnavailable': {
            const load = getImageLoad(data);
            if (load) {
                load.failed = true;
                syncChatImages();
                scrollToEnd();
            }
            break;
        }
        case 'font': {
            const style = document.documentElement.style;
            style.setProperty('--chat-font-family', JSON.stringify(data.family));
            style.setProperty('--chat-font-size', `${data.size}px`);
            scrollToEnd();
            break;
        }
        case 'copyResult': {
            if (data.copyId === sessionCopyId) {
                clearTimeout(sessionCopyFeedbackTimer);
                setCopyAllLabel(data.success ? copiedLabel : copyFailedLabel, data.success ? '✓' : '×');
                sessionCopyFeedbackTimer = setTimeout(() => setCopyAllLabel(copyAllLabel), 1600);
                break;
            }
            const view = messageViews.find(message => message.copyId === data.copyId);
            if (!view) break;
            clearTimeout(view.copyFeedbackTimer);
            view.copyButton.textContent = data.success ? copiedLabel : copyFailedLabel;
            view.copyFeedbackTimer = setTimeout(() => { view.copyButton.textContent = copyLabel; }, 1600);
            break;
        }
        case 'submitResult': {
            const sent = pending.get(data.sessionId);
            pending.delete(data.sessionId);
            if (data.accepted && drafts.get(data.sessionId) === sent &&
                (!composing || composerSessionId !== data.sessionId)) {
                drafts.delete(data.sessionId);
                if (composerSessionId === data.sessionId) promptBox.value = '';
            }
            syncComposer();
            break;
        }
        case 'rewindResult': {
            if (data.rewound && data.sessionId === sessionId) prefillComposer(data.prompt);
            break;
        }
    }
});

host.postMessage({ action: 'ready' });
