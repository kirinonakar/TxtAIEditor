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
    const { points, yRange } = sampleFunctionPlot(graph);
    target.data.datasets[0].data = points;
    target.options.scales.y.min = yRange.min;
    target.options.scales.y.max = yRange.max;
}

function createFunctionChart(graph) {
    const config = {
        type: 'line',
        data: { datasets: [{ label: graph.equation, pointRadius: 0, borderWidth: 2, spanGaps: false }] },
        options: {
            scales: {
                x: { type: 'linear', ...graph.xRange, title: { display: true, text: 'x' } },
                y: { title: { display: true, text: 'y' } }
            },
            plugins: { legend: { display: false }, tooltip: { enabled: false } }
        },
        [functionPlotKey]: graph
    };
    applyFunctionData(config, graph);
    return config;
}

function fromBasicChart(input) {
    const name = String(input.type || '').toLowerCase();
    if (name === 'function' || name === 'linear-function') {
        return createFunctionChart(createFunctionPlot(input, name === 'linear-function'));
    }

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

export async function parseMarkdownChart(language, source) {
    if (!isChartFence(language) || source.length > 100_000) throw new Error('Invalid chart code block');
    const basic = language.toLowerCase() === 'chart';
    let input;
    if (basic) {
        const { load, CORE_SCHEMA } = await import('./chart/js-yaml.esm.min.js');
        input = load(source, { schema: CORE_SCHEMA, maxDepth: 32 });
    } else {
        input = JSON.parse(source);
    }
    if (!isRecord(input)) throw new Error('Expected a chart configuration object');
    validateChartData(input);
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
    const title = document.createElement('div');
    title.className = 'chart-function-equation';
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
        title.textContent = equation;
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
    block.insertBefore(title, frame);
    if (graph.parameters.size > 0) block.insertBefore(controls, frame);
}

function drawChart(block, Chart, config, appearance) {
    const [color, borderColor, font] = appearance;
    const options = config.options ??= {};
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
