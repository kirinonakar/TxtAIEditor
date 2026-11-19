const unaryFunctions = new Map(Object.entries({
    abs: Math.abs, sqrt: Math.sqrt, cbrt: Math.cbrt,
    sin: Math.sin, cos: Math.cos, tan: Math.tan,
    asin: Math.asin, acos: Math.acos, atan: Math.atan,
    sinh: Math.sinh, cosh: Math.cosh, tanh: Math.tanh,
    exp: Math.exp, ln: Math.log, log: Math.log, log10: Math.log10, log2: Math.log2,
    floor: Math.floor, ceil: Math.ceil, round: Math.round, sign: Math.sign
}));
const numberLiteral = /^(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;

function compileExpression(expression, parameters) {
    const source = expression.replace(/[×·]/g, '*').replace(/÷/g, '/').replace(/−/g, '-')
        .replace(/π/g, 'pi').replaceAll('**', '^');
    const tokens = source.match(/(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?|[a-z_]\w*|\S/gi) ?? [];
    let position = 0;
    function take(symbol) {
        if (tokens[position] !== symbol) return false;
        position++;
        return true;
    }

    function primary() {
        const token = tokens[position++];
        if (token === '(' || token === '|') {
            const expression = sum();
            const absolute = token === '|';
            if (!take(absolute ? '|' : ')')) {
                throw new Error(absolute ? 'Expected a closing absolute-value bar' : 'Expected a closing parenthesis');
            }
            return absolute ? x => Math.abs(expression(x)) : expression;
        }
        if (token && numberLiteral.test(token)) {
            const value = Number(token);
            if (!Number.isFinite(value)) throw new Error('Expected a finite number');
            return () => value;
        }
        if (token === 'x') return x => x;
        if (token === 'pi') return () => Math.PI;
        if (token === 'e') return () => Math.E;
        const parameter = parameters.get(token);
        if (parameter) return () => parameter.value;

        const fn = unaryFunctions.get(token);
        if (fn && take('(')) {
            const argument = sum();
            if (!take(')')) throw new Error('Expected a closing parenthesis');
            return x => fn(argument(x));
        }
        throw new Error(token ? `Unexpected expression token: ${token}` : 'Expected an expression');
    }

    function unary() {
        if (take('+')) return unary();
        if (take('-')) {
            const operand = unary();
            return x => -operand(x);
        }
        const base = primary();
        if (!take('^')) return base;
        const exponent = unary();
        return x => base(x) ** exponent(x);
    }

    function product() {
        let result = unary();
        while (tokens[position] === '*' || tokens[position] === '/') {
            const multiply = take('*');
            if (!multiply) take('/');
            const left = result;
            const right = unary();
            result = multiply ? x => left(x) * right(x) : x => left(x) / right(x);
        }
        return result;
    }

    function sum() {
        let result = product();
        while (tokens[position] === '+' || tokens[position] === '-') {
            const add = take('+');
            if (!add) take('-');
            const left = result;
            const right = product();
            result = add ? x => left(x) + right(x) : x => left(x) - right(x);
        }
        return result;
    }

    const evaluate = sum();
    if (position !== tokens.length) throw new Error(`Unexpected expression token: ${tokens[position]}`);
    return evaluate;
}

function readParameter(specification, defaultLimit = 10) {
    const definition = typeof specification === 'number' ? { value: specification } : specification;
    if (!definition || typeof definition !== 'object' || Array.isArray(definition)) {
        throw new Error('Expected a parameter number or a mapping with value, min, max and step');
    }

    const { value } = definition;
    const limit = Math.ceil(Math.max(defaultLimit, Math.abs(value)));
    const { min = -limit, max = limit } = definition;
    if (![value, min, max].every(Number.isFinite) || min >= max ||
        !Number.isFinite(max - min) || value < min || value > max) {
        throw new Error('Expected a finite initial parameter value within its slider range');
    }

    // Native range steps start at min. Off-grid initial values use a grid anchored at value.
    const followsStep = step => {
        const index = (value - min) / step;
        return Math.abs(index - Math.round(index)) < 1e-8;
    };
    const step = definition.step ?? (followsStep(0.1) ? 0.1 : 'any');
    if (step !== 'any' && (!Number.isFinite(step) || step <= 0)) {
        throw new Error('Expected a positive slider step');
    }
    const stepBase = step !== 'any' && !followsStep(step) ? value : undefined;
    return { value, min, max, step, stepBase };
}

function readRange(min, max) {
    if (![min, max].every(Number.isFinite) || min >= max || !Number.isFinite(max - min)) {
        throw new Error('Expected a finite axis range with min < max');
    }
    return { min, max };
}

export function createFunctionPlot(input, linear) {
    const { xMin = -10, xMax = 10, yMin, yMax } = input;
    const xRange = readRange(xMin, xMax);
    let yRange = yMin === undefined && yMax === undefined ? null : readRange(yMin, yMax);
    if (linear) {
        const { a: aValue = 1, b: bValue = 0 } = input;
        const a = readParameter(aValue, 5);
        const b = readParameter(bValue);
        // Keep the same scale across all positions of the original linear sliders.
        const yLimit = Math.max(Math.abs(a.min), Math.abs(a.max)) * Math.max(Math.abs(xMin), Math.abs(xMax)) +
            Math.max(Math.abs(b.min), Math.abs(b.max));
        yRange ??= readRange(-yLimit, yLimit);
        return {
            curves: [{ label: 'y = ax + b', evaluate: x => a.value * x + b.value }],
            parameters: new Map([['a', a], ['b', b]]), xRange, yRange, intervals: 1,
            formatEquation: number => `y = ${number.format(a.value)}x ${b.value < 0 ? '−' : '+'} ${number.format(Math.abs(b.value))}`
        };
    }

    const definitions = input.parameters ?? input.params ?? {};
    if (typeof definitions !== 'object' || Array.isArray(definitions) || Object.keys(definitions).length > 12) {
        throw new Error('Expected a mapping of up to 12 parameters');
    }
    const parameters = new Map();
    for (const [name, definition] of Object.entries(definitions)) {
        if (!/^[a-z_]\w{0,31}$/i.test(name) || ['x', 'pi', 'e'].includes(name) || unaryFunctions.has(name)) {
            throw new Error(`Invalid parameter name: ${name}`);
        }
        parameters.set(name, readParameter(definition));
    }

    const series = input.series === undefined ? [input] : input.series;
    if (!Array.isArray(series) || !series.length || series.length > 16) {
        throw new Error('Expected 1 to 16 function series');
    }
    const curves = series.map(entry => {
        const expression = typeof entry?.expression === 'string'
            ? entry.expression.trim().replace(/^y\s*=\s*/, '') : '';
        if (!expression || expression.length > 512) throw new Error('Expected an expression of at most 512 characters');
        const equation = `y = ${expression}`;
        return {
            equation,
            label: typeof entry.title === 'string' && entry.title.trim() ? entry.title : equation,
            evaluate: compileExpression(expression, parameters)
        };
    });
    return {
        curves, parameters, xRange, yRange, intervals: 400,
        formatEquation: () => curves.map(curve => curve.equation).join(', ')
    };
}

function autoYRange(points) {
    const values = points.map(point => point.y).filter(Number.isFinite).sort((a, b) => a - b);
    if (!values.length) return null;
    const last = values.length - 1;
    const low = values[Math.floor(last * 0.025)];
    const high = values[Math.ceil(last * 0.975)];
    const typicalSpan = high - low || Math.max(Math.abs(low) * 0.1, 1);
    // Include ordinary extrema while keeping samples near poles from dominating the scale.
    const min = Math.max(values[0], low - 2 * typicalSpan);
    const max = Math.min(values[last], high + 2 * typicalSpan);
    const padding = (max - min || typicalSpan) * 0.08;
    return { min: min - padding, max: max + padding };
}

function sampleCurve({ xRange: { min, max }, intervals }, curve) {
    const valueAt = x => {
        const y = curve.evaluate(x);
        return Number.isFinite(4 * y) ? y : null;
    };
    const points = Array.from({ length: intervals + 1 }, (_, index) => {
        const x = index === intervals ? max : min + (max - min) * (index / intervals);
        return { x, y: valueAt(x) };
    });
    return { points, valueAt };
}

function breakAtPoles({ points, valueAt }, yRange) {
    if (points.length <= 2) return points;

    const connected = [points[0]];
    const jumpThreshold = (yRange.max - yRange.min) * 0.6;
    for (let index = 1; index < points.length; index++) {
        const previous = points[index - 1];
        const point = points[index];
        if (previous.y !== null && point.y !== null && Math.abs(point.y - previous.y) > jumpThreshold) {
            const x = previous.x + (point.x - previous.x) / 2;
            const midpoint = valueAt(x);
            // Opposite sides of a pole can both have finite samples. Break the line
            // when the midpoint is outside the values of the two endpoints.
            if (midpoint === null || midpoint < Math.min(previous.y, point.y) || midpoint > Math.max(previous.y, point.y)) {
                connected.push({ x, y: null });
            }
        }
        connected.push(point);
    }
    return connected;
}

export function sampleFunctionPlot(graph) {
    const samples = graph.curves.map(curve => sampleCurve(graph, curve));
    const yRange = graph.yRange ?? samples
        .map(sample => autoYRange(sample.points))
        .filter(Boolean)
        .reduce((combined, range) => combined
            ? { min: Math.min(combined.min, range.min), max: Math.max(combined.max, range.max) }
            : range, null) ?? { min: -1, max: 1 };
    return { datasets: samples.map(sample => breakAtPoles(sample, yRange)), yRange };
}
