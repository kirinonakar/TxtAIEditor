// HarfBuzz subsets the original installed font and freezes variable axes. The
// generated FontFace belongs only to the isolated PDF document.
let subsetEngine;
let bindings = [];
const tag = value => [...value].reduce((result, c) => (result * 256 + c.charCodeAt(0)) >>> 0, 0);
const u16 = (bytes, offset) => new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint16(offset);
const u32 = (bytes, offset) => new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset);

export function collectFontRequests(root = document.getElementById('print-container') || document.body) {
    const groups = new Map();
    bindings = [];
    function groupFor(element, create = true) {
        const style = getComputedStyle(element);
        if (style.visibility === 'hidden') return null;
        const families = style.fontFamily.match(/"[^"]+"|'[^']+'|[^,]+/g)
            .map(name => name.trim().replace(/^['"]|['"]$/g, ''));
        const spec = { families, weight: Number(style.fontWeight) || 400, style: style.fontStyle,
            stretch: style.fontStretch, variations: style.fontVariationSettings, size: parseFloat(style.fontSize),
            opticalSizing: style.fontOpticalSizing };
        const key = JSON.stringify(spec);
        let group = groups.get(key);
        if (!group && create) {
            group = { ...spec, id: groups.size, characters: new Set(), elements: new Set() };
            groups.set(key, group);
        }
        return group;
    }
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
        const node = walker.currentNode;
        const element = node.parentElement;
        if (!node.textContent.trim() || !element || !element.getClientRects().length ||
            element.closest('script,style,noscript')) continue;
        const group = groupFor(element);
        if (!group) continue;
        for (const c of node.textContent) group.characters.add(c);
    }
    for (const element of [root, ...root.querySelectorAll('*')]) {
        if (!element.getClientRects().length || element.closest('script,style,noscript')) continue;
        const alt = element instanceof HTMLImageElement && !element.naturalWidth ? element.alt : '';
        const marker = element.tagName === 'LI' ? getComputedStyle(element, '::marker') : null;
        const markerText = marker ? ({ disc: '•', circle: '◦', square: '▪' }[getComputedStyle(element).listStyleType] || '0123456789.() ') : '';
        const group = groupFor(element, !!(alt || markerText));
        if (!group) continue;
        for (const c of alt + markerText) group.characters.add(c);
        group.elements.add(element);
        element.setAttribute('data-txtaieditor-pdf-font', group.id);
    }
    bindings = [...groups.values()];
    return bindings.map(({ characters, elements, ...spec }) => ({ ...spec, text: [...characters].join('') }));
}

function tables(bytes) {
    const result = new Map();
    const count = u16(bytes, 4);
    if (count > 256 || 12 + count * 16 > bytes.length) throw new Error('Invalid font directory.');
    for (let i = 0; i < count; i++) {
        const at = 12 + i * 16;
        const name = String.fromCharCode(...bytes.subarray(at, at + 4));
        const offset = u32(bytes, at + 8), length = u32(bytes, at + 12);
        if (offset + length > bytes.length) throw new Error('Invalid font table bounds.');
        result.set(name, bytes.slice(offset, offset + length));
    }
    return result;
}

async function engine() {
    if (!subsetEngine) subsetEngine = (async () => {
        const response = await fetch(new URL('./harfbuzz/harfbuzz-subset.wasm', import.meta.url));
        if (!response.ok) throw new Error('Could not load the PDF font subsetter.');
        const { instance } = await WebAssembly.instantiate(await response.arrayBuffer());
        instance.exports.__wasm_call_ctors?.();
        return instance.exports;
    })();
    return subsetEngine;
}

export async function subsetFont(bytes, spec) {
    const sourceTables = tables(bytes);
    // This subsetter build does not preserve color tables. Let Skia retain its
    // native colored-glyph subsets rather than silently turn emoji monochrome.
    if (['COLR', 'CBDT', 'sbix', 'SVG '].some(name => sourceTables.has(name))) return null;
    const os2 = sourceTables.get('OS/2');
    // Restricted-license/bitmap-only fonts cannot be embedded as outline fonts.
    if (os2 && (u16(os2, 8) & 0x0202)) throw new Error(`The font does not permit outline embedding: ${spec.family}`);
    const hb = await engine();
    const pointer = hb.malloc(bytes.length);
    if (!pointer) throw new Error('Could not allocate the font subset buffer.');
    let blob = 0, face = 0, input = 0, subset = 0, outputBlob = 0;
    try {
        new Uint8Array(hb.memory.buffer, pointer, bytes.length).set(bytes);
        blob = hb.hb_blob_create(pointer, bytes.length, 2, 0, 0); // READONLY
        face = hb.hb_face_create(blob, 0);
        input = hb.hb_subset_input_create_or_fail();
        if (!input) throw new Error('Could not create font subset input.');
        // Retain names, shaping features and notdef outline. The layout closure
        // keeps ligatures, combining marks and contextual/vertical alternates.
        hb.hb_subset_input_set_flags(input, 0x40 | 0x08);
        for (const type of [4, 5, 6]) {
            const set = hb.hb_subset_input_set(input, type);
            hb.hb_set_clear(set);
            hb.hb_set_invert(set);
        }
        const unicodeSet = hb.hb_subset_input_unicode_set(input);
        for (const c of spec.text) hb.hb_set_add(unicodeSet, c.codePointAt(0));
        if (os2 && (u16(os2, 8) & 0x0100)) {
            // Respect "no subsetting" rather than stripping its rights flags.
            hb.hb_set_clear(unicodeSet);
            hb.hb_set_invert(unicodeSet);
        }
        if (sourceTables.has('fvar')) {
            hb.hb_subset_input_pin_all_axes_to_default(input, face);
            const axes = { wght: spec.weight, wdth: parseFloat(spec.stretch) || 100 };
            if (/italic/.test(spec.style)) axes.ital = 1;
            if (/oblique/.test(spec.style)) axes.slnt = -(parseFloat(spec.style.slice(7)) || 14);
            if (spec.opticalSizing === 'auto') axes.opsz = spec.size * 0.75;
            for (const match of String(spec.variations).matchAll(/["']([ -~]{4})["']\s+(-?[\d.]+)/g)) axes[match[1]] = Number(match[2]);
            for (const [axis, value] of Object.entries(axes)) hb.hb_subset_input_pin_axis_location(input, face, tag(axis), value);
        }
        subset = hb.hb_subset_or_fail(face, input);
        if (!subset) throw new Error(`Could not subset the original font: ${spec.family}`);
        outputBlob = hb.hb_face_reference_blob(subset);
        const length = hb.hb_blob_get_length(outputBlob);
        const data = hb.hb_blob_get_data(outputBlob, 0);
        if (!data || !length) throw new Error('The generated font subset is empty.');
        let output = new Uint8Array(hb.memory.buffer, data, length).slice();
        const outputTables = tables(output);
        if (outputTables.has('fvar')) throw new Error('The PDF font still contains variable axes.');
        if (outputTables.has('CFF ') || outputTables.has('CFF2')) output = await cffToTrueType(output);
        return output;
    } finally {
        if (outputBlob) hb.hb_blob_destroy(outputBlob);
        if (subset) hb.hb_face_destroy(subset);
        if (input) hb.hb_subset_input_destroy(input);
        if (face) hb.hb_face_destroy(face);
        if (blob) hb.hb_blob_destroy(blob);
        hb.free(pointer);
    }
}

export async function embedPdfFonts(sources) {
    const familiesByGroup = new Map();
    const dataByUrl = new Map();
    const stats = [];
    for (const source of sources) {
        let pending = dataByUrl.get(source.url);
        if (!pending) {
            pending = fetch(source.url).then(async response => {
                if (!response.ok) throw new Error(`Could not read the original font: ${source.family}`);
                return new Uint8Array(await response.arrayBuffer());
            });
            dataByUrl.set(source.url, pending);
        }
        const program = await pending;
        const bytes = await subsetFont(program, source);
        if (!bytes) continue;
        const alias = `TxtAIEditorPdfSubset${source.id}_${source.familyIndex}`;
        const original = tables(bytes);
        stats.push({ family: source.family, originalGlyphs: u16(tables(program).get('maxp'), 4),
            subsetGlyphs: u16(original.get('maxp'), 4), originalBytes: program.length, subsetBytes: bytes.length });
        const os2 = original.get('OS/2');
        const isItalic = os2 && os2.length >= 64 && (u16(os2, 62) & 1);
        const weight = source.weight;
        const font = new FontFace(alias, bytes, { weight: String(weight), style: isItalic ? 'italic' : 'normal' });
        await font.load();
        document.fonts.add(font);
        if (!familiesByGroup.has(source.id)) familiesByGroup.set(source.id, new Map());
        familiesByGroup.get(source.id).set(source.familyIndex, alias);
        const group = bindings.find(group => group.id === source.id);
        if (group && source.familyIndex >= group.families.length) group.families[source.familyIndex] = source.family;
    }
    for (const group of bindings) {
        const aliases = familiesByGroup.get(group.id);
        if (!aliases) continue;
        // Put the actual fallback faces before CSS generic families; otherwise
        // e.g. monospace can choose a variable CJK fallback before our subset.
        const entries = group.families.map((name, index) => ({ name, alias: aliases.get(index) }));
        const generic = entry => /^(serif|sans-serif|monospace|system-ui|cursive|fantasy|emoji|math|fangsong|ui-.+)$/i.test(entry.name);
        const family = [...entries.filter(entry => !generic(entry)), ...entries.filter(generic)]
            .map(entry => entry.alias ? JSON.stringify(entry.alias) : generic(entry) ? entry.name : JSON.stringify(entry.name)).join(',');
        for (const element of group.elements) element.style.setProperty('font-family', family, 'important');
    }
    await document.fonts.ready;
    return { subsets: stats };
}

// Skia also converts CFF fonts to Type 3. Re-encode the same glyph outlines as
// quadratic TrueType, preserving glyph IDs and original metrics/shaping tables.
// Cubic approximation stays within 0.25 font units (before integer rounding).
async function cffToTrueType(bytes) {
    const hb = await import('./harfbuzz/index.mjs');
    const blob = new hb.Blob(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
    const face = new hb.Face(blob);
    const font = new hb.Font(face);
    font.setScale(face.upem, face.upem);
    const fontTables = tables(bytes);
    const count = u16(fontTables.get('maxp'), 4);
    const chunks = [], offsets = [0];
    let maxPoints = 0, maxContours = 0;
    for (let gid = 0; gid < count; gid++) {
        const contours = glyphContours(font.glyphToJson(gid));
        const points = contours.flat();
        maxPoints = Math.max(maxPoints, points.length);
        maxContours = Math.max(maxContours, contours.length);
        const data = encodeGlyph(contours);
        chunks.push(data);
        offsets.push(offsets[offsets.length - 1] + data.length);
    }
    const glyf = new Uint8Array(offsets[offsets.length - 1]);
    chunks.forEach((data, i) => glyf.set(data, offsets[i]));
    const loca = new Uint8Array(offsets.length * 4);
    offsets.forEach((offset, i) => new DataView(loca.buffer).setUint32(i * 4, offset));
    const maxp = new Uint8Array(32), maxpView = new DataView(maxp.buffer);
    maxpView.setUint32(0, 0x00010000);
    maxpView.setUint16(4, count);
    maxpView.setUint16(6, maxPoints);
    maxpView.setUint16(8, maxContours);
    maxpView.setUint16(14, 1); // maxZones
    const head = fontTables.get('head');
    new DataView(head.buffer, head.byteOffset).setInt16(50, 1); // long loca
    for (const name of ['CFF ', 'CFF2', 'VORG', 'DSIG']) fontTables.delete(name);
    fontTables.set('glyf', glyf);
    fontTables.set('loca', loca);
    fontTables.set('maxp', maxp);
    // CFF post tables can contain a CFF-specific glyph-name format.
    const post = fontTables.get('post').slice(0, 32);
    new DataView(post.buffer).setUint32(0, 0x00030000);
    fontTables.set('post', post);
    return writeSfnt(fontTables);
}

function glyphContours(commands) {
    const contours = [];
    let contour = [], current;
    const point = (x, y, on = true) => ({ x: Math.round(x), y: Math.round(y), on });
    const close = () => {
        if (contour.length > 1 && contour[0].x === contour.at(-1).x && contour[0].y === contour.at(-1).y && contour.at(-1).on) contour.pop();
        if (contour.length) contours.push(contour);
        contour = [];
    };
    function cubic(p0, p1, p2, p3, depth = 0) {
        const control = { x: (3 * p1.x + 3 * p2.x - p0.x - p3.x) / 4, y: (3 * p1.y + 3 * p2.y - p0.y - p3.y) / 4 };
        const dx = p3.x - 3 * p2.x + 3 * p1.x - p0.x, dy = p3.y - 3 * p2.y + 3 * p1.y - p0.y;
        if (Math.hypot(dx, dy) <= 2.5 || depth >= 12) {
            contour.push(point(control.x, control.y, false), point(p3.x, p3.y));
        } else {
            const mid = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
            const a = mid(p0, p1), b = mid(p1, p2), c = mid(p2, p3), d = mid(a, b), e = mid(b, c), f = mid(d, e);
            cubic(p0, a, d, f, depth + 1);
            cubic(f, e, c, p3, depth + 1);
        }
    }
    for (const command of commands) {
        const v = command.values;
        if (command.type === 'M') { close(); current = { x: v[0], y: v[1] }; contour.push(point(...v)); }
        else if (command.type === 'L') { current = { x: v[0], y: v[1] }; contour.push(point(...v)); }
        else if (command.type === 'Q') { contour.push(point(v[0], v[1], false), point(v[2], v[3])); current = { x: v[2], y: v[3] }; }
        else if (command.type === 'C') { cubic(current, { x: v[0], y: v[1] }, { x: v[2], y: v[3] }, { x: v[4], y: v[5] }); current = { x: v[4], y: v[5] }; }
        else if (command.type === 'Z') close();
    }
    close();
    return contours;
}

function encodeGlyph(contours) {
    const points = contours.flat();
    if (!points.length) return new Uint8Array();
    if (points.length > 65535 || contours.length > 32767) throw new Error('Font outline exceeds TrueType limits.');
    const data = new Uint8Array((12 + contours.length * 2 + points.length * 5 + 3) & ~3);
    const view = new DataView(data.buffer);
    view.setInt16(0, contours.length);
    const xs = points.map(p => p.x), ys = points.map(p => p.y);
    [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)].forEach((v, i) => view.setInt16(2 + i * 2, v));
    let total = 0;
    contours.forEach((c, i) => { total += c.length; view.setUint16(10 + i * 2, total - 1); });
    let at = 12 + contours.length * 2;
    for (const p of points) data[at++] = p.on ? 1 : 0;
    for (const axis of ['x', 'y']) {
        let previous = 0;
        for (const p of points) {
            const delta = p[axis] - previous;
            if (delta < -32768 || delta > 32767) throw new Error('Font coordinate exceeds TrueType limits.');
            view.setInt16(at, delta); at += 2; previous = p[axis];
        }
    }
    return data;
}

function checksum(bytes) {
    let sum = 0;
    for (let at = 0; at < bytes.length; at += 4) {
        let value = 0;
        for (let j = 0; j < 4; j++) value = ((value << 8) | (bytes[at + j] || 0)) >>> 0;
        sum = (sum + value) >>> 0;
    }
    return sum;
}

function writeSfnt(fontTables) {
    const entries = [...fontTables].sort(([a], [b]) => a < b ? -1 : 1);
    const size = 12 + entries.length * 16 + entries.reduce((sum, [, data]) => sum + ((data.length + 3) & ~3), 0);
    const bytes = new Uint8Array(size), view = new DataView(bytes.buffer);
    view.setUint32(0, 0x00010000);
    view.setUint16(4, entries.length);
    const power = 2 ** Math.floor(Math.log2(entries.length));
    view.setUint16(6, power * 16); view.setUint16(8, Math.log2(power)); view.setUint16(10, entries.length * 16 - power * 16);
    let offset = 12 + entries.length * 16, headOffset = 0;
    entries.forEach(([name, data], i) => {
        if (name === 'head') { data.fill(0, 8, 12); headOffset = offset; }
        const at = 12 + i * 16;
        view.setUint32(at, tag(name)); view.setUint32(at + 4, checksum(data));
        view.setUint32(at + 8, offset); view.setUint32(at + 12, data.length);
        bytes.set(data, offset); offset += (data.length + 3) & ~3;
    });
    view.setUint32(headOffset + 8, (0xb1b0afba - checksum(bytes)) >>> 0);
    return bytes;
}
