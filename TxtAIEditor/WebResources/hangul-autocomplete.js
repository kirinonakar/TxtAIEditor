/**
 * hangul-autocomplete.js
 * 
 * Provides robust Hangul (Korean) autocomplete regular expression generation by
 * decomposing the last character into Choseong, Jungseong, and Jongseong,
 * and mapping them to all possible matching syllables or split syllable groups.
 */

(function (global) {
    'use strict';

    const HANGUL_START = 0xAC00;
    const HANGUL_END = 0xD7A3;

    // Standard Choseong list (19 consonants)
    const CHOSEONG = [
        'ㄱ', 'ㄲ', 'ㄴ', 'ㄷ', 'ㄸ', 'ㄹ', 'ㅁ', 'ㅂ', 'ㅃ', 'ㅅ',
        'ㅆ', 'ㅇ', 'ㅈ', 'ㅉ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ'
    ];

    // Standard Jungseong list (21 vowels)
    const JUNGSEONG = [
        'ㅏ', 'ㅐ', 'ㅑ', 'ㅒ', 'ㅓ', 'ㅔ', 'ㅕ', 'ㅖ', 'ㅗ', 'ㅘ',
        'ㅙ', 'ㅚ', 'ㅛ', 'ㅜ', 'ㅝ', 'ㅞ', 'ㅟ', 'ㅡ', 'ㅢ', 'ㅣ'
    ];

    // Standard Jongseong list (28 elements, index 0 is empty/none)
    const JONGSEONG = [
        '', 'ㄱ', 'ㄲ', 'ㄳ', 'ㄴ', 'ㄵ', 'ㄶ', 'ㄷ', 'ㄹ', 'ㄺ',
        'ㄻ', 'ㄼ', 'ㄽ', 'ㄾ', 'ㄿ', 'ㅀ', 'ㅁ', 'ㅂ', 'ㅄ', 'ㅅ',
        'ㅆ', 'ㅇ', 'ㅈ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ'
    ];

    // Map Compatibility Jamo consonants (ㄱ to ㅎ) to their standard Choseong indices.
    const JAMO_CHOSEONG_MAP = {
        'ㄱ': 0, 'ㄲ': 1, 'ㄴ': 2, 'ㄷ': 3, 'ㄸ': 4, 'ㄹ': 5, 'ㅁ': 6, 'ㅂ': 7, 'ㅃ': 8, 'ㅅ': 9,
        'ㅆ': 10, 'ㅇ': 11, 'ㅈ': 12, 'ㅉ': 13, 'ㅊ': 14, 'ㅋ': 15, 'ㅌ': 16, 'ㅍ': 17, 'ㅎ': 18
    };

    // Map Jongseong index to [current_jongseong_idx, next_choseong_idx] for syllable splitting
    const JONGSEONG_SPLIT = {
        1: [0, 0],   // ㄱ -> [none, ㄱ]
        2: [0, 1],   // ㄲ -> [none, ㄲ]
        3: [1, 9],   // ㄳ -> [ㄱ, ㅅ]
        4: [0, 2],   // ㄴ -> [none, ㄴ]
        5: [4, 12],  // ㄵ -> [ㄴ, ㅈ]
        6: [4, 18],  // ㄶ -> [ㄴ, ㅎ]
        7: [0, 3],   // ㄷ -> [none, ㄷ]
        8: [0, 5],   // ㄹ -> [none, ㄹ]
        9: [8, 0],   // ㄺ -> [ㄹ, ㄱ]
        10: [8, 6],  // ㄻ -> [ㄹ, ㅁ]
        11: [8, 7],  // ㄼ -> [ㄹ, ㅂ]
        12: [8, 9],  // ㄽ -> [ㄹ, ㅅ]
        13: [8, 16], // ㄾ -> [ㄹ, ㅌ]
        14: [8, 17], // ㄿ -> [ㄹ, ㅍ]
        15: [8, 18], // ㅀ -> [ㄹ, ㅎ]
        16: [0, 6],  // ㅁ -> [none, ㅁ]
        17: [0, 7],  // ㅂ -> [none, ㅂ]
        18: [17, 9], // ㅄ -> [ㅂ, ㅅ]
        19: [0, 9],  // ㅅ -> [none, ㅅ]
        20: [0, 10], // ㅆ -> [none, ㅆ]
        // Note: 'ㅇ' (index 21) is intentionally omitted from JONGSEONG_SPLIT because
        // 'ㅇ' Jongseong never splits or carries over to the next syllable as a Choseong.
        // For example, '경' + '어' = '경어' (still starts with '경'), not '겨엉'.
        // This prevents false matches like matching '격전지' or '겨우' when the user types '경'.
        22: [0, 12], // ㅈ -> [none, ㅈ]
        23: [0, 14], // ㅊ -> [none, ㅊ]
        24: [0, 15], // ㅋ -> [none, ㅋ]
        25: [0, 16], // ㅌ -> [none, ㅌ]
        26: [0, 17], // ㅍ -> [none, ㅍ]
        27: [0, 18]  // ㅎ -> [none, ㅎ]
    };

    /**
     * Escapes regex special characters.
     */
    function escapeRegExp(str) {
        return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    /**
     * Checks if a character is a Hangul syllable or compatibility Jamo.
     */
    function isHangul(char) {
        if (!char) return false;
        const code = char.charCodeAt(0);
        return (code >= HANGUL_START && code <= HANGUL_END) || (code >= 0x3130 && code <= 0x318F);
    }

    /**
     * Decomposes the last character of currentWord if it is a Hangul character,
     * and generates a highly accurate regular expression pattern for matching.
     * 
     * @param {string} currentWord The word being autocompleted (e.g. "어학연ㅅ", "유학우", "해맑")
     * @returns {string} The regular expression pattern string
     */
    function makeRegex(currentWord) {
        if (!currentWord || currentWord.length === 0) {
            return '';
        }

        const lastChar = currentWord.slice(-1);
        const prefix = currentWord.slice(0, -1);
        const escapedPrefix = escapeRegExp(prefix);

        const code = lastChar.charCodeAt(0);

        // Scenario 1: Compatibility Jamo Consonant (Choseong search)
        if (lastChar in JAMO_CHOSEONG_MAP) {
            const c_idx = JAMO_CHOSEONG_MAP[lastChar];
            const startSyllable = String.fromCharCode(HANGUL_START + c_idx * 588);
            const endSyllable = String.fromCharCode(HANGUL_START + c_idx * 588 + 587);
            return escapedPrefix + '[' + startSyllable + '-' + endSyllable + ']';
        }

        // Scenario 2 & 3: Hangul Syllable
        if (code >= HANGUL_START && code <= HANGUL_END) {
            const s_idx = code - HANGUL_START;
            const c_idx = Math.floor(s_idx / 588);
            const v_idx = Math.floor((s_idx % 588) / 28);
            const r_idx = s_idx % 28;

            if (r_idx === 0) {
                // Scenario 2: Vowel/Jungseong search
                // Expand standard vowels that can form complex vowels (ㅗ, ㅜ, ㅡ)
                let end_v_idx = v_idx;
                if (v_idx === 8) { // ㅗ -> ㅚ (index 11)
                    end_v_idx = 11;
                } else if (v_idx === 13) { // ㅜ -> ㅟ (index 16)
                    end_v_idx = 16;
                } else if (v_idx === 17) { // ㅡ -> ㅢ (index 18)
                    end_v_idx = 18;
                }
                const startSyllable = String.fromCharCode(HANGUL_START + c_idx * 588 + v_idx * 28);
                const endSyllable = String.fromCharCode(HANGUL_START + c_idx * 588 + end_v_idx * 28 + 27);
                return escapedPrefix + '[' + startSyllable + '-' + endSyllable + ']';
            } else {
                // Scenario 3: Jongseong search
                const split = JONGSEONG_SPLIT[r_idx];
                if (split) {
                    const [current_jongseong_idx, next_choseong_idx] = split;
                    // Full syllable matching Option A
                    const optionA = lastChar;
                    // Splitted syllable + next Choseong range Option B
                    const S_split = String.fromCharCode(HANGUL_START + c_idx * 588 + v_idx * 28 + current_jongseong_idx);
                    const nextStart = String.fromCharCode(HANGUL_START + next_choseong_idx * 588);
                    const nextEnd = String.fromCharCode(HANGUL_START + next_choseong_idx * 588 + 587);
                    const optionB = S_split + '[' + nextStart + '-' + nextEnd + ']';
                    return escapedPrefix + '(' + optionA + '|' + optionB + ')';
                }
            }
        }

        // Default fallback: return escaped whole word
        return escapeRegExp(currentWord);
    }

    // Expose the helper module
    global.HangulAutocomplete = {
        isHangul: isHangul,
        makeRegex: makeRegex,
        escapeRegExp: escapeRegExp
    };

})(typeof window !== 'undefined' ? window : this);

export const HangulAutocomplete = window.HangulAutocomplete;
