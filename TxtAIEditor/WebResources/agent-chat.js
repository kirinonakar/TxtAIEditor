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
const copyAllButton = document.getElementById('copy-all');
const copyAllText = document.getElementById('copy-all-text');
const copyAllIcon = document.getElementById('copy-all-icon');
const composer = document.getElementById('composer');
const promptBox = document.getElementById('prompt');
const sendButton = document.getElementById('send');
const timestamp = /^\d{2}:\d{2}:\d{2}[ \t]+(.*)/;
const answerHeader = /^\[(?:Agent Response|assistant: (?:response|final answer))\]:?[ \t]*/i;
const logHeader = /^\[(?:Agent run\]|LLM API:|Tool (?:result|completed|failed)[:\]])/i;
const sectionHeader = /^\[(?:Agent tool call|assistant: (?:tool call|thinking|error)|tool: .+ (?:arguments|result)|Tool result: .+|Global agent rules|Retry detail: .+|Compressed context used for subsequent model requests)\]/i;
const sectionEnd = /^\[End (?:global agent rules|retry detail|compressed context)\]/i;
const markdownOptions = {
    allowHtml: false, deferOpenDiagrams: true, diagramLoadingText: '', renderCharts: true,
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
let userRoleLabel = '';
let assistantRoleLabel = '';
let sessionCopyId = null;
let sessionCopyFeedbackTimer;
let nextCopyId = 0;

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

function assistantBlocks(text) {
    const blocks = [];
    const markdown = [];
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
            lines.push(line);
            if (isFencedCodeClose(line, fence)) fence = null;
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
    node.append(content, copyButton);

    const view = { node, content, role, start, copyButton, copyId: String(++nextCopyId), copyText: '' };
    copyButton.addEventListener('click', () => {
        if (view.copyText) {
            host.postMessage({ action: 'copy', copyId: view.copyId, text: view.copyText });
        }
    });
    return view;
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
            if (view.copyText !== userText) view.content.textContent = userText;
            view.copyText = userText;
            view.copyButton.hidden = !userText;
            view.node.hidden = !userText;
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
    const visibleIds = new Set(items.map(item => item.id));
    for (const [id, button] of sessionButtons) {
        if (visibleIds.has(id)) continue;
        button.remove();
        sessionButtons.delete(id);
    }

    let cursor = sessions.firstElementChild;
    for (const { id, title, canSelect, isRunning, branch } of items) {
        let button = sessionButtons.get(id);
        if (!button) {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'session-link';
            const name = document.createElement('span');
            name.className = 'session-name';
            const connector = document.createElement('span');
            connector.className = 'session-connector';
            connector.setAttribute('aria-hidden', 'true');
            button.append(name, connector);
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
        if (button !== cursor) sessions.insertBefore(button, cursor);
        cursor = button.nextElementSibling;
    }
    sessionsEmpty.hidden = items.length > 0;
    syncSessionSelection();
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

function syncComposer() {
    if (!composing) {
        if (composerSessionId !== sessionId) {
            composerSessionId = sessionId;
            promptBox.value = drafts.get(sessionId) ?? '';
        }
        composer.hidden = !canSubmit;
        promptBox.disabled = !canSubmit;
    }
    sendButton.disabled = !canSubmit || composerSessionId !== sessionId ||
        pending.has(sessionId) || (!composing && !promptBox.value.trim());
}

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
    syncComposer();
    scrollToEnd();
}

promptBox.addEventListener('input', () => {
    drafts.set(composerSessionId, promptBox.value);
    syncComposer();
});
promptBox.addEventListener('compositionstart', () => {
    composing = true;
    syncComposer();
});
promptBox.addEventListener('compositionend', () => {
    composing = false;
    requestAnimationFrame(syncComposer);
});
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
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || composing || event.keyCode === 229) return;
    event.preventDefault();
    composer.requestSubmit();
});
composer.addEventListener('submit', event => {
    event.preventDefault();
    if (sendButton.disabled || composing || !promptBox.value.trim()) return;
    const prompt = promptBox.value;
    pending.set(sessionId, prompt);
    followOutput = true;
    host.postMessage({ action: 'submit', sessionId, prompt });
    syncComposer();
});

host.addEventListener('message', ({ data }) => {
    switch (data.action) {
        case 'labels':
            document.title = data.title;
            history.setAttribute('aria-label', data.title);
            turnTitle.textContent = data.turns;
            sessionsTitle.textContent = data.sessions;
            sessionsEmpty.textContent = data.emptySessions;
            untitledSessionLabel = data.untitledSession;
            runningSessionLabel = data.runningSession;
            promptBox.placeholder = data.prompt;
            promptBox.setAttribute('aria-label', data.prompt);
            sendButton.title = data.send;
            sendButton.setAttribute('aria-label', data.send);
            thinkingLabel = data.thinking;
            toolStatusPrefixes = data.toolStatusPrefixes.filter(Boolean);
            verboseToolPrefix = data.verboseToolPrefix;
            chartErrorLabel = data.chartErrorLabel;
            copyLabel = data.copy;
            copiedLabel = data.copied;
            copyFailedLabel = data.copyFailed;
            copyAllLabel = data.copyAll;
            userRoleLabel = data.userRole;
            assistantRoleLabel = data.assistantRole;
            setCopyAllLabel(copyAllLabel);
            break;
        case 'update':
            update(data);
            break;
        case 'sessions':
            showSessions(data.items);
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
    }
});

host.postMessage({ action: 'ready' });
