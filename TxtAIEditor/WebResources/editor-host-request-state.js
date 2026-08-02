function normalizeClipboardText(value) {
    return String(value || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

export class HostRequestController {
    #nextRequestId = 1;
    #clipboardRequests = new Map();
    #setTimer;
    #clearTimer;

    constructor({
        setTimer = (callback, delay) => setTimeout(callback, delay),
        clearTimer = timer => clearTimeout(timer)
    } = {}) {
        this.#setTimer = setTimer;
        this.#clearTimer = clearTimer;
    }

    get pendingClipboardRequestCount() {
        return this.#clipboardRequests.size;
    }

    nextRequestId() {
        return this.#nextRequestId++;
    }

    beginClipboardRequest(resolve, { timeoutMs = 1200 } = {}) {
        const requestId = this.nextRequestId();
        const pending = {
            resolve: typeof resolve === 'function' ? resolve : () => { },
            timer: null
        };
        this.#clipboardRequests.set(requestId, pending);
        pending.timer = this.#setTimer(() => {
            const timedOut = this.#clipboardRequests.get(requestId);
            if (!timedOut) return;
            this.#clipboardRequests.delete(requestId);
            timedOut.resolve('');
        }, Math.max(0, Number(timeoutMs || 0)));
        return requestId;
    }

    completeClipboardRequest(requestId, text) {
        const id = Number(requestId || 0);
        const pending = this.#clipboardRequests.get(id);
        if (!pending) return false;

        this.#clearTimer(pending.timer);
        this.#clipboardRequests.delete(id);
        pending.resolve(normalizeClipboardText(text));
        return true;
    }
}
