export class SearchController {
    #query = '';
    #matches = [];
    #matchesByLine = new Map();
    #index = -1;
    #activeMatch = null;
    #matchCase = false;
    #regex = false;
    #documentVersion = -1;
    #pendingNavigation = null;

    get query() {
        return this.#query;
    }

    set query(value) {
        this.#query = String(value || '');
    }

    get matches() {
        return this.#matches;
    }

    get matchesByLine() {
        return this.#matchesByLine;
    }

    get index() {
        return this.#index;
    }

    get activeMatch() {
        return this.#activeMatch;
    }

    set activeMatch(value) {
        this.#activeMatch = value;
    }

    get matchCase() {
        return this.#matchCase;
    }

    get regex() {
        return this.#regex;
    }

    get documentVersion() {
        return this.#documentVersion;
    }

    get pendingNavigation() {
        return this.#pendingNavigation;
    }

    toggleMatchCase() {
        this.#matchCase = !this.#matchCase;
        return this.#matchCase;
    }

    toggleRegex() {
        this.#regex = !this.#regex;
        return this.#regex;
    }

    invalidateDocument({ clearPendingNavigation = false } = {}) {
        this.#documentVersion = -1;
        if (clearPendingNavigation) {
            this.#pendingNavigation = null;
        }
    }

    hasFreshResults(query, documentVersion) {
        return this.#query === String(query || '') &&
            this.#documentVersion === Number(documentVersion);
    }

    clearResults({ documentVersion = this.#documentVersion, clearPendingNavigation = false } = {}) {
        this.#query = '';
        this.#matches = [];
        this.#matchesByLine = new Map();
        this.#index = -1;
        this.#activeMatch = null;
        this.#documentVersion = Number(documentVersion);
        if (clearPendingNavigation) {
            this.#pendingNavigation = null;
        }
    }

    queueNavigation({ query, reverse, line, column }) {
        this.#pendingNavigation = {
            query: String(query || ''),
            reverse: !!reverse,
            line: Number(line || 1),
            column: Number(column || 1)
        };
    }

    clearPendingNavigation() {
        this.#pendingNavigation = null;
    }

    applyResults({ query, matches, documentVersion }) {
        this.#query = String(query || '');
        this.#matches = Array.isArray(matches) ? matches : [];
        this.#matchesByLine = new Map();
        for (const match of this.#matches) {
            let lineMatches = this.#matchesByLine.get(match.lineNumber);
            if (!lineMatches) {
                lineMatches = [];
                this.#matchesByLine.set(match.lineNumber, lineMatches);
            }
            lineMatches.push(match);
        }
        this.#documentVersion = Number(documentVersion);
        this.#index = -1;
        this.#activeMatch = null;
    }

    selectFromPosition(line, column, reverse = false) {
        this.#index = this.#findMatchIndexFromPosition(line, column, reverse);
        return this.#activateCurrentIndex();
    }

    selectNext(line, column, reverse = false) {
        if (this.#matches.length === 0) return null;

        if (this.#index < 0) {
            this.#index = this.#findMatchIndexFromPosition(line, column, reverse);
        } else if (reverse) {
            this.#index = (this.#index - 1 + this.#matches.length) % this.#matches.length;
        } else {
            this.#index = (this.#index + 1) % this.#matches.length;
        }

        return this.#activateCurrentIndex();
    }

    #activateCurrentIndex() {
        this.#activeMatch = null;
        if (this.#index < 0 || this.#index >= this.#matches.length) return null;

        const match = this.#matches[this.#index];
        this.#activeMatch = {
            lineNumber: match.lineNumber,
            indexOfMatch: match.indexOfMatch,
            matchLength: match.matchLength,
            query: this.#query
        };
        return this.#activeMatch;
    }

    #findMatchIndexFromPosition(line, column, reverse) {
        if (this.#matches.length === 0) return -1;

        const currentLine = Math.max(1, Number(line || 1));
        const currentColumn = Math.max(1, Number(column || 1));
        if (reverse) {
            for (let index = this.#matches.length - 1; index >= 0; index--) {
                const matchLine = Number(this.#matches[index].lineNumber || 1);
                const matchColumn = Number(this.#matches[index].indexOfMatch || 0) + 1;
                if (matchLine < currentLine ||
                    (matchLine === currentLine && matchColumn < currentColumn)) {
                    return index;
                }
            }
            return this.#matches.length - 1;
        }

        for (let index = 0; index < this.#matches.length; index++) {
            const matchLine = Number(this.#matches[index].lineNumber || 1);
            const matchColumn = Number(this.#matches[index].indexOfMatch || 0) + 1;
            if (matchLine > currentLine ||
                (matchLine === currentLine && matchColumn >= currentColumn)) {
                return index;
            }
        }
        return 0;
    }
}
