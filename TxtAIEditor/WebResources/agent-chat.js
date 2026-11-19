import { configureDiagramRenderer, renderBlockAt, renderMermaidBlocks } from './markdown-preview-renderer.js';

const host = window.chrome.webview;
const history = document.getElementById('history');
const messages = document.getElementById('messages');
const turns = document.getElementById('turns');
const turnTitle = document.getElementById('turn-title');
const composer = document.getElementById('composer');
const promptBox = document.getElementById('prompt');
const sendButton = document.getElementById('send');
const timestamp = /^\d{2}:\d{2}:\d{2} {2}/;
const markdownOptions = { allowHtml: false, deferOpenDiagrams: true, diagramLoadingText: '' };

const drafts = new Map();
const pending = new Map();
let promptRanges = [];
let messageViews = [];
let jumpLinks = [];
let selectedJump = null;
let output = '';
let sessionId = null;
let composerSessionId = null;
let canSubmit = false;
let composing = false;
let followOutput = true;
let theme = '';
let usePele = false;

function scrollToEnd() {
    if (followOutput) history.scrollTop = history.scrollHeight;
}

history.addEventListener('scroll', () => {
    followOutput = history.scrollHeight - history.scrollTop - history.clientHeight < 44;
});

function renderAssistant(view, text) {
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    const getLine = number => lines[number - 1];
    const blocks = [];
    for (let number = 1; number <= lines.length;) {
        const line = getLine(number);
        if (timestamp.test(line) || /^\[(?:Agent run|Agent Response|assistant: final answer)\]:?$/i.test(line)) {
            blocks.push({ activity: line });
            number++;
            continue;
        }
        const block = renderBlockAt(number, lines.length, getLine, markdownOptions);
        if (block?.html) blocks.push({ html: block.html });
        number = (block?.endLine ?? number) + 1;
    }

    for (const [index, block] of blocks.entries()) {
        let node = view.node.children[index];
        if (node && node.blockHtml === block.html && node.activity === block.activity) continue;
        if (!node) {
            node = document.createElement('div');
            view.node.append(node);
        }
        node.blockHtml = block.html;
        node.activity = block.activity;
        node.className = block.activity === undefined ? 'block' : 'activity';
        if (block.activity === undefined) node.innerHTML = block.html;
        else node.textContent = block.activity;
    }
    while (view.node.children.length > blocks.length) view.node.lastElementChild.remove();
    void renderMermaidBlocks(view.node, scrollToEnd);
}

function renderConversation(offset, appearanceChanged) {
    const nextViews = [];
    let index = 0;

    function show(role, start, end, userText = null, preamble = '') {
        const previous = messageViews[index];
        let view = previous;
        const reusable = previous?.role === role && previous.start === start;
        if (!reusable) {
            const node = document.createElement('div');
            node.className = `message ${role}`;
            if (previous) previous.node.replaceWith(node);
            else messages.append(node);
            view = { node, role, start };
        }
        if (role === 'user') {
            if (view.userText !== userText) view.node.textContent = userText;
            view.userText = userText;
        } else if (!reusable || appearanceChanged || view.end !== end || offset < end) {
            renderAssistant(view, preamble + output.slice(start, end));
        }
        view.end = end;
        nextViews.push(view);
        index++;
        return view.node;
    }

    const targets = [];
    for (const [turn, range] of promptRanges.entries()) {
        targets.push(show('user', range.start, range.end, range.text));
        const end = promptRanges[turn + 1]?.start ?? output.length;
        show('assistant', range.end, end, null, turn === 0 ? output.slice(0, range.start) : '');
    }
    if (!promptRanges.length) show('assistant', 0, output.length);
    for (; index < messageViews.length; index++) messageViews[index].node.remove();
    messageViews = nextViews;
    return targets;
}

function showJumpLinks(targets) {
    for (const [index, target] of targets.entries()) {
        const text = promptRanges[index].text.trim().replace(/\s+/g, ' ');
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
            selectedJump?.removeAttribute('aria-current');
            selectedJump = button;
            button.setAttribute('aria-current', 'location');
            followOutput = false;
            target.scrollIntoView({ block: 'start' });
        });
        if (existing) existing.button.replaceWith(button);
        else turns.append(button);
        jumpLinks[index] = { target, excerpt, button };
    }
    while (jumpLinks.length > targets.length) jumpLinks.pop().button.remove();
}

function syncComposer() {
    if (!composing) {
        if (composerSessionId !== sessionId) {
            composerSessionId = sessionId;
            promptBox.value = drafts.get(sessionId) ?? '';
        }
        composer.hidden = !canSubmit;
        promptBox.disabled = !canSubmit;
    }
    sendButton.disabled = composing || !canSubmit || composerSessionId !== sessionId ||
        pending.has(sessionId) || !promptBox.value.trim();
}

function update(message) {
    const switched = sessionId !== message.sessionId;
    if (switched) {
        sessionId = message.sessionId;
        messages.replaceChildren();
        turns.replaceChildren();
        messageViews = [];
        jumpLinks = [];
        selectedJump = null;
        output = '';
        promptRanges = [];
        followOutput = true;
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
    if (switched || appearanceChanged || message.prompts || nextOutput !== output) {
        output = nextOutput;
        const targets = renderConversation(message.offset, appearanceChanged);
        if (switched || message.prompts) showJumpLinks(targets);
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
promptBox.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || composing || event.keyCode === 229) return;
    event.preventDefault();
    composer.requestSubmit();
});
composer.addEventListener('submit', event => {
    event.preventDefault();
    if (sendButton.disabled) return;
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
            promptBox.placeholder = data.prompt;
            promptBox.setAttribute('aria-label', data.prompt);
            sendButton.title = data.send;
            sendButton.setAttribute('aria-label', data.send);
            break;
        case 'update':
            update(data);
            break;
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
