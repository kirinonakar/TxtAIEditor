import { createFunctionPlot, sampleFunctionPlot } from './chart/function-plot.js';

const baseTypes = new Map([
    ['bar', 'bar'], ['line', 'line'], ['pie', 'pie'], ['doughnut', 'doughnut'],
    ['polararea', 'polarArea'], ['radar', 'radar'], ['bubble', 'bubble'], ['scatter', 'scatter']
]);
const chartSelector = '.chart-block[data-chart-language][data-chart-source]';
const charts = new Map();
const pending = new WeakSet();
const forbiddenKeys = new Set(['__proto__', 'constructor', 'prototype']);
const functionPlotKey = Symbol('function plot');
const lineColors = ['#2684c7', '#d1433f', '#19877b', '#8757b9', '#b9691b', '#668331', '#a34996', '#198297'];
let chartLibrary;
let removalObserver;

export function isChartFence(language) {
    return language?.toLowerCase() === 'chart' || language?.toLowerCase() === 'advanced-chart';
}

function isRecord(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value) &&
        [Object.prototype, null].includes(Object.getPrototypeOf(value));
}

function validateChartData(value, depth = 0, budget = { remaining: 24000 }) {
    if (depth > 32 || --budget.remaining < 0) throw new Error('Chart configuration is too large');
    if (typeof value === 'number' && !Number.isFinite(value)) throw new Error('Invalid number');
    if (value === null || typeof value !== 'object') return;
    if (!Array.isArray(value) && !isRecord(value)) throw new Error('Invalid chart configuration value');
    for (const key of Object.keys(value)) {
        if (forbiddenKeys.has(key)) throw new Error('Invalid chart configuration key');
        validateChartData(value[key], depth + 1, budget);
    }
}

function applyFunctionData(target, graph) {
    const { datasets, yRange } = sampleFunctionPlot(graph);
    datasets.forEach((points, index) => { target.data.datasets[graph.curveIndexes[index]].data = points; });
    target.options.scales.y.min = yRange.min;
    target.options.scales.y.max = yRange.max;
}

function createFunctionChart(input, linear, options = {}, data = {}) {
    if (!isRecord(options) || !isRecord(data)) {
        throw new Error('Expected Chart.js options and data objects for a function chart');
    }
    const { scales = {}, plugins } = options;
    const { datasets = [] } = data;
    if (!isRecord(scales) || !Array.isArray(datasets) || !datasets.every(isRecord)) {
        throw new Error('Expected a scales object and a datasets array');
    }
    const { x = {}, y = {} } = scales;
    if (!isRecord(x) || !isRecord(y)) throw new Error('Expected x and y axis options');
    const { xMin = x.min, xMax = x.max, yMin = y.min, yMax = y.max } = input;
    const expressionIndexes = !linear && input.expression === undefined && input.series === undefined
        ? datasets.flatMap((dataset, index) => Object.hasOwn(dataset, 'expression') ? [index] : []) : [];
    const series = expressionIndexes.length
        ? expressionIndexes.map(index => ({ title: datasets[index].label, expression: datasets[index].expression }))
        : input.series;
    const graph = createFunctionPlot({ ...input, series, xMin, xMax, yMin, yMax }, linear);
    graph.curveIndexes = expressionIndexes.length ? expressionIndexes : graph.curves.map((_, index) => index);
    const renderedDatasets = datasets.slice();
    graph.curves.forEach((curve, index) => {
        const datasetIndex = graph.curveIndexes[index];
        const style = datasets[datasetIndex];
        renderedDatasets[datasetIndex] = {
            label: curve.label, pointRadius: 0, borderWidth: 2, spanGaps: false,
            ...(index >= lineColors.length && { borderDash: [6, 4] }),
            ...style, xAxisID: 'x', yAxisID: 'y', indexAxis: 'x'
        };
    });

    const config = {
        type: 'line',
        data: {
            ...data,
            datasets: renderedDatasets
        },
        options: {
            ...options,
            scales: {
                ...scales,
                x: { type: 'linear', title: { display: true, text: 'x' }, ...x, ...graph.xRange },
                y: { title: { display: true, text: 'y' }, ...y }
            },
            plugins: input.type === 'line' || plugins === false ? plugins : {
                ...plugins,
                legend: plugins?.legend ?? { display: graph.curves.length > 1 },
                tooltip: plugins?.tooltip ?? { enabled: false }
            }
        },
        [functionPlotKey]: graph
    };
    applyFunctionData(config, graph);
    return config;
}

function fromBasicChart(input) {
    const name = String(input.type || '').toLowerCase();
    const type = baseTypes.get(name);
    if (!type || !Array.isArray(input.series) || !input.series.length ||
        (input.labels !== undefined && !Array.isArray(input.labels))) {
        throw new Error('Expected a chart type, labels array and one or more series');
    }

    const options = {};
    if (['bar', 'line', 'scatter', 'bubble'].includes(type)) {
        if (input.indexAxis === 'y') options.indexAxis = 'y';
        if (typeof input.stacked === 'boolean') {
            options.scales = { x: { stacked: input.stacked }, y: { stacked: input.stacked } };
        }
        if (typeof input.beginAtZero === 'boolean') {
            options.scales ??= {};
            const valueAxis = options.indexAxis === 'y' ? 'x' : 'y';
            options.scales[valueAxis] = { ...options.scales[valueAxis], beginAtZero: input.beginAtZero };
        }
    } else if (['radar', 'polarArea'].includes(type) && typeof input.beginAtZero === 'boolean') {
        options.scales = { r: { beginAtZero: input.beginAtZero } };
    }
    if (typeof input.title === 'string') {
        options.plugins = { title: { display: true, text: input.title } };
    }
    if (typeof input.legend === 'boolean' || ['top', 'left', 'bottom', 'right', 'chartArea'].includes(input.legend)) {
        options.plugins ??= {};
        options.plugins.legend = typeof input.legend === 'boolean'
            ? { display: input.legend } : { position: input.legend };
    }

    const datasets = input.series.map(series => {
        if (!isRecord(series) || !Array.isArray(series.data)) throw new Error('Expected a data array for each series');
        const { title, color, ...dataset } = series;
        dataset.label ??= title === undefined ? '' : String(title);
        if (typeof color === 'string') {
            dataset.borderColor ??= color;
            dataset.backgroundColor ??= color;
        }
        if (typeof input.fill === 'boolean') dataset.fill ??= input.fill;
        if (typeof input.tension === 'number') dataset.tension ??= input.tension;
        return dataset;
    });
    return { type, data: { labels: input.labels || [], datasets }, options };
}

async function loadChartYaml(source) {
    const { load, CORE_SCHEMA } = await import('./chart/js-yaml.esm.min.js');
    let lines;
    for (let repairs = 0; ; repairs++) {
        try {
            return load(source, { schema: CORE_SCHEMA, maxDepth: 32 });
        } catch (error) {
            // Recover bare |…| only on the line rejected by YAML, leaving valid block scalars intact.
            const mark = error.mark;
            if (!mark || repairs >= 32) throw error;
            lines ??= source.split(/\r\n?|\n/);
            const match = /^([ ]*(?:-[ \t]+)?(?:title|expression):[ \t]*)(\|.*)$/.exec(lines[mark.line] || '');
            if (!match || mark.column <= match[1].length) throw error;
            const value = match[2].replace(/[ \t]+#.*$/, '').trimEnd();
            if (value.indexOf('|', 1) < 0) throw error;
            lines[mark.line] = match[1] + JSON.stringify(value);
            source = lines.join('\n');
        }
    }
}

export async function parseMarkdownChart(language, source) {
    if (!isChartFence(language) || source.length > 100_000) throw new Error('Invalid chart code block');
    const basic = language.toLowerCase() === 'chart';
    const input = basic ? await loadChartYaml(source) : JSON.parse(source);
    if (!isRecord(input)) throw new Error('Expected a chart configuration object');
    validateChartData(input);
    const type = typeof input.type === 'string' ? input.type.toLowerCase() : '';
    const expressionLine = !basic && type === 'line' && Array.isArray(input.data?.datasets) &&
        input.data.datasets.some(dataset => isRecord(dataset) && Object.hasOwn(dataset, 'expression'));
    if (type === 'function' || type === 'linear-function' || expressionLine) {
        return createFunctionChart(input, type === 'linear-function',
            basic ? undefined : input.options, basic ? undefined : input.data);
    }
    if (basic) return fromBasicChart(input);
    if (typeof input.type !== 'string' || !isRecord(input.data) || !Array.isArray(input.data.datasets) ||
        (input.options !== undefined && !isRecord(input.options))) {
        throw new Error('Expected a Chart.js type, data.datasets array and optional options object');
    }
    return input;
}

function loadChartLibrary() {
    chartLibrary ??= import('./chart/chart.umd.min.js').then(module => module.default ?? globalThis.Chart);
    return chartLibrary;
}

function chartAppearance(block) {
    const style = getComputedStyle(block);
    return [
        style.color,
        style.getPropertyValue('--border').trim() || 'rgba(127, 127, 127, 0.32)',
        style.fontFamily
    ];
}

function observeChartRemoval() {
    if (removalObserver) return;
    removalObserver = new MutationObserver(records => {
        if (!records.some(record => record.removedNodes.length > 0)) return;
        for (const [block, state] of charts) {
            if (block.isConnected) continue;
            state.chart.destroy();
            charts.delete(block);
        }
        if (!charts.size) {
            removalObserver.disconnect();
            removalObserver = null;
        }
    });
    removalObserver.observe(document.documentElement, { childList: true, subtree: true });
}

function addFunctionControls(block, frame, canvas, chart, graph) {
    const title = graph.curves.length === 1 ? document.createElement('div') : null;
    if (title) title.className = 'chart-function-equation';
    const controls = document.createElement('div');
    controls.className = 'chart-function-controls';
    controls.setAttribute('contenteditable', 'false');
    // Keep native range gestures and clicks out of inline preview's editing handlers.
    for (const name of ['pointerdown', 'click', 'dblclick']) {
        controls.addEventListener(name, event => event.stopPropagation());
    }
    const number = new Intl.NumberFormat(undefined, { maximumSignificantDigits: 6 });
    const updateEquation = () => {
        const equation = graph.formatEquation(number);
        if (title) title.textContent = equation;
        canvas.setAttribute('aria-label', equation);
    };

    for (const [key, parameter] of graph.parameters) {
        const label = document.createElement('label');
        const name = document.createElement('span');
        name.textContent = key;
        const value = document.createElement('span');
        value.textContent = number.format(parameter.value);
        value.setAttribute('aria-hidden', 'true');
        const slider = document.createElement('input');
        slider.type = 'range';
        slider.min = String(parameter.min);
        slider.max = String(parameter.max);
        slider.step = String(parameter.step);
        slider.value = String(parameter.value);
        slider.setAttribute('aria-label', key);
        slider.addEventListener('input', event => {
            event.stopPropagation();
            parameter.value = slider.valueAsNumber;
            value.textContent = number.format(parameter.value);
            updateEquation();
            applyFunctionData(chart, graph);
            chart.update('none');
        });
        label.append(name, value, slider);
        controls.append(label);
    }

    updateEquation();
    if (title) block.insertBefore(title, frame);
    if (graph.parameters.size > 0) block.insertBefore(controls, frame);
}

function applyLineColors({ type, data, options }) {
    const line = options.elements?.line;
    data.datasets.forEach((dataset, index) => {
        if (!isRecord(dataset)) return;
        const kind = dataset.type ?? type;
        const defaults = options.datasets?.[kind];
        const scatterLine = kind === 'scatter' && (dataset.showLine ?? defaults?.showLine ?? options.showLine);
        if (kind !== 'line' && kind !== 'radar' && !scatterLine) return;

        const configuredBorder = dataset.borderColor ?? defaults?.borderColor ?? line?.borderColor ?? options.borderColor;
        if (configuredBorder != null) return;

        const color = lineColors[index % lineColors.length];
        dataset.borderColor = color;
        const configuredFill = dataset.backgroundColor ?? defaults?.backgroundColor ?? line?.backgroundColor ?? options.backgroundColor;
        if (configuredFill == null) dataset.backgroundColor = `${color}40`;
    });
}

function drawChart(block, Chart, config, appearance) {
    const [color, borderColor, font] = appearance;
    const options = config.options ??= {};
    // Apply series colors before setting the theme border shared by Chart.js and the grid.
    applyLineColors(config);
    options.color ??= color;
    options.borderColor ??= borderColor;
    options.font = { family: font, ...options.font };
    options.responsive ??= true;
    options.maintainAspectRatio ??= false;
    options.animation ??= false;

    const frame = document.createElement('div');
    frame.className = 'chart-frame';
    const canvas = document.createElement('canvas');
    canvas.setAttribute('role', 'img');
    const title = options.plugins?.title?.text;
    const labels = config.data.datasets.map(dataset => dataset?.label).filter(Boolean);
    canvas.setAttribute('aria-label', [title || labels.join(', ') || config.type].flat().join(' '));
    frame.append(canvas);
    block.replaceChildren(frame);
    canvas.width = Math.max(1, frame.clientWidth);
    canvas.height = Math.max(1, frame.clientHeight);
    try {
        const chart = new Chart(canvas, config);
        if (!chart.ctx) {
            chart.destroy();
            throw new Error('Canvas context is unavailable');
        }
        const graph = config[functionPlotKey];
        if (graph) addFunctionControls(block, frame, canvas, chart, graph);
        return chart;
    } catch (error) {
        Chart.getChart(canvas)?.destroy();
        throw error;
    }
}

async function drawChartBlock(block, { errorLabel, shouldDefer } = {}) {
    if (pending.has(block)) return false;
    if (block.classList.contains('chart-error')) {
        block.querySelector('.chart-error-label').textContent = errorLabel || '';
        return false;
    }
    const previous = charts.get(block);
    if (previous && previous.appearance === JSON.stringify(chartAppearance(block))) return false;

    const source = block.dataset.chartSource;
    pending.add(block);
    try {
        const config = await parseMarkdownChart(block.dataset.chartLanguage, source);
        const Chart = await loadChartLibrary();
        if (!block.isConnected || shouldDefer?.()) return false;
        const graph = config[functionPlotKey];
        if (graph && previous?.graph) {
            for (const [name, parameter] of graph.parameters) {
                const value = previous.graph.parameters.get(name)?.value;
                if (value >= parameter.min && value <= parameter.max) parameter.value = value;
            }
            applyFunctionData(config, graph);
        }
        previous?.chart.destroy();
        charts.delete(block);
        const appearance = chartAppearance(block);
        const chart = drawChart(block, Chart, config, appearance);
        charts.set(block, { chart, appearance: JSON.stringify(appearance), graph });
        observeChartRemoval();
    } catch (error) {
        if (!block.isConnected || shouldDefer?.()) return false;
        charts.get(block)?.chart.destroy();
        charts.delete(block);
        const label = document.createElement('p');
        label.className = 'chart-error-label';
        label.textContent = errorLabel || '';
        const code = document.createElement('code');
        code.textContent = source;
        const fallback = document.createElement('pre');
        fallback.title = error.message;
        fallback.append(code);
        block.classList.add('chart-error');
        block.replaceChildren(label, fallback);
    } finally {
        pending.delete(block);
    }
    return true;
}

export async function renderChartBlocks(root, onRendered, options) {
    if (!root) return;
    const updated = await Promise.all([...root.querySelectorAll(chartSelector)]
        .map(block => drawChartBlock(block, options)));
    if (updated.some(Boolean)) onRendered?.();
}
