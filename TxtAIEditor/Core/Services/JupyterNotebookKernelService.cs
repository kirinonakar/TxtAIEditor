using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class JupyterNotebookKernelService : IDisposable
    {
        private readonly Func<string, string, string> _getString;
        private readonly ConcurrentDictionary<string, KernelSession> _sessions = new();

        public JupyterNotebookKernelService(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public string ResolvePythonExecutable(string notebookDirectory)
        {
            foreach (string venvName in new[] { ".venv", "venv" })
            {
                string venvDir = Path.Combine(notebookDirectory, venvName);
                if (Directory.Exists(venvDir))
                {
                    string windowsExe = Path.Combine(venvDir, "Scripts", "python.exe");
                    if (File.Exists(windowsExe))
                    {
                        return windowsExe;
                    }

                    string unixExe = Path.Combine(venvDir, "bin", "python");
                    if (File.Exists(unixExe))
                    {
                        return unixExe;
                    }
                }
            }

            return "python";
        }

        public async Task<KernelSession> GetOrCreateSessionAsync(string tabId, string pythonExecutable, string workingDirectory)
        {
            if (_sessions.TryGetValue(tabId, out var existing) && existing.IsAlive)
            {
                return existing;
            }

            existing?.Dispose();

            var session = new KernelSession(pythonExecutable, workingDirectory);
            await session.StartAsync();
            _sessions[tabId] = session;
            return session;
        }

        public async Task<KernelExecutionResult> ExecuteAsync(string tabId, string pythonExecutable, string workingDirectory, string code)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.ExecuteAsync(code);
            }
            catch (Exception ex)
            {
                return new KernelExecutionResult("error", string.Empty, ex.Message);
            }
        }

        public async Task<string> GetVariablesAsync(string tabId, string pythonExecutable, string workingDirectory)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.GetVariablesAsync();
            }
            catch
            {
                return "[]";
            }
        }

        public async Task<string> UpdatePlotViewAsync(string tabId, string pythonExecutable, string workingDirectory, string figId, double elev, double azim)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.UpdatePlotViewAsync(figId, elev, azim);
            }
            catch
            {
                return string.Empty;
            }
        }

        public void CloseSession(string tabId)
        {
            if (_sessions.TryRemove(tabId, out var session))
            {
                session.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }

        public sealed class KernelSession : IDisposable
        {
            private Process? _process;
            private readonly string _pythonExecutable;
            private readonly string _workingDirectory;
            private static readonly string KernelScript = @"
import sys, json, io, base64, contextlib, traceback, ast

try:
    if hasattr(sys.stdin, 'reconfigure'): sys.stdin.reconfigure(line_buffering=True)
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

_ns = {'__name__': '__main__'}

def _get_variables():
    vars_list = []
    ignored = {'sys', 'json', 'io', 'base64', 'contextlib', 'traceback', 'ast', 'plt', 'matplotlib', '_ns', '_get_variables', '_render_figure_html', '_capture_figures', '_custom_show'}
    for k, v in list(_ns.items()):
        if k.startswith('_') or k in ignored:
            continue
        try:
            v_type = type(v).__name__
            v_size = ''
            if hasattr(v, 'shape'):
                try:
                    v_size = str(tuple(v.shape))
                except Exception:
                    v_size = ''
            elif hasattr(v, '__len__'):
                try:
                    v_size = str(len(v))
                except Exception:
                    v_size = ''

            v_val = ''
            try:
                v_val = repr(v)
            except Exception as re:
                v_val = f'<unreprable: {re}>'

            if len(v_val) > 200:
                v_val = v_val[:197] + '...'

            vars_list.append({
                'name': str(k),
                'type': str(v_type),
                'size': str(v_size),
                'value': str(v_val)
            })
        except Exception:
            pass
    return vars_list

_active_figures = {}

def _render_figure_html(fig, fig_id=None):
    import io, base64
    if not fig_id:
        fig_id = str(id(fig))
    _active_figures[fig_id] = fig

    is_3d = False
    cur_elev = 30
    cur_azim = -60
    try:
        for ax in fig.axes:
            if hasattr(ax, 'view_init') or getattr(ax, 'name', '') == '3d' or '3d' in str(type(ax)).lower():
                is_3d = True
                cur_elev = int(getattr(ax, 'elev', 30) or 30)
                cur_azim = int(getattr(ax, 'azim', -60) or -60)
                break
    except Exception:
        pass

    colorbars = []
    try:
        for ax in fig.axes:
            label = str(getattr(ax, 'get_label', lambda: '')())
            if getattr(ax, '_colorbar', None) is not None or label == '<colorbar>' or 'colorbar' in label.lower():
                colorbars.append(ax)
    except Exception:
        pass

    is_3d_str = 'true' if is_3d else 'false'

    if colorbars:
        try:
            for cb in colorbars: cb.set_visible(False)
            buf_main = io.BytesIO()
            fig.savefig(buf_main, format='png', bbox_inches='tight', transparent=True)
            buf_main.seek(0)
            b64_main = base64.b64encode(buf_main.read()).decode('utf-8')

            for cb in colorbars: cb.set_visible(True)
            for ax in fig.axes:
                if ax not in colorbars: ax.set_visible(False)
            buf_cbar = io.BytesIO()
            fig.savefig(buf_cbar, format='png', bbox_inches='tight', transparent=True)
            buf_cbar.seek(0)
            b64_cbar = base64.b64encode(buf_cbar.read()).decode('utf-8')

            for ax in fig.axes: ax.set_visible(True)

            return f'''<!--MPL_START--><div class=""mpl-interactive-wrapper"" data-mpl=""true"" data-is-3d=""{is_3d_str}"" data-fig-id=""{fig_id}"" data-elev=""{cur_elev}"" data-azim=""{cur_azim}"">
    <div class=""mpl-toolbar"">
        <button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button>
        <button class=""mpl-btn mpl-btn-pan active"" title=""Pan Mode"">✋ Pan</button>
        <button class=""mpl-btn mpl-btn-rotate"" title=""3D View Rotate"">🔄 3D Rotate</button>
        <div class=""mpl-rotate-ctrl"">
            <span>Azim(Azim):</span>
            <input type=""range"" class=""mpl-rotate-y-slider"" min=""-180"" max=""180"" value=""{cur_azim}"" />
            <span class=""mpl-angle-val-y"">{cur_azim}°</span>
        </div>
        <div class=""mpl-rotate-ctrl"">
            <span>Elev(Elev):</span>
            <input type=""range"" class=""mpl-rotate-x-slider"" min=""-90"" max=""90"" value=""{cur_elev}"" />
            <span class=""mpl-angle-val-x"">{cur_elev}°</span>
        </div>
        <button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button>
    </div>
    <div class=""mpl-viewport"">
        <div class=""mpl-plot-layer"">
            <img src=""data:image/png;base64,{b64_main}"" class=""mpl-plot-img"" />
        </div>
        <div class=""mpl-cbar-layer"">
            <img src=""data:image/png;base64,{b64_cbar}"" class=""mpl-cbar-img"" />
        </div>
    </div>
    <div class=""mpl-status-bar"">
        <span>3D Rotate: Drag / Right-Click (Elev/Azim Re-render) | Pan: Middle-Click | Wheel: Zoom</span>
    </div>
</div><!--MPL_END-->'''
        except Exception:
            pass

    buf = io.BytesIO()
    fig.savefig(buf, format='png', bbox_inches='tight')
    buf.seek(0)
    b64 = base64.b64encode(buf.read()).decode('utf-8')
    return f'''<!--MPL_START--><div class=""mpl-interactive-wrapper"" data-mpl=""true"" data-is-3d=""{is_3d_str}"" data-fig-id=""{fig_id}"" data-elev=""{cur_elev}"" data-azim=""{cur_azim}"">
    <div class=""mpl-toolbar"">
        <button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button>
        <button class=""mpl-btn mpl-btn-pan active"" title=""Pan Mode"">✋ Pan</button>
        <button class=""mpl-btn mpl-btn-rotate"" title=""3D View Rotate"">🔄 3D Rotate</button>
        <div class=""mpl-rotate-ctrl"">
            <span>Azim(Azim):</span>
            <input type=""range"" class=""mpl-rotate-y-slider"" min=""-180"" max=""180"" value=""{cur_azim}"" />
            <span class=""mpl-angle-val-y"">{cur_azim}°</span>
        </div>
        <div class=""mpl-rotate-ctrl"">
            <span>Elev(Elev):</span>
            <input type=""range"" class=""mpl-rotate-x-slider"" min=""-90"" max=""90"" value=""{cur_elev}"" />
            <span class=""mpl-angle-val-x"">{cur_elev}°</span>
        </div>
        <button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button>
    </div>
    <div class=""mpl-viewport"">
        <div class=""mpl-plot-layer"">
            <img src=""data:image/png;base64,{b64}"" class=""mpl-plot-img"" />
        </div>
    </div>
    <div class=""mpl-status-bar"">
        <span>3D Rotate: Drag / Right-Click (Elev/Azim Re-render) | Pan: Middle-Click | Wheel: Zoom</span>
    </div>
</div><!--MPL_END-->'''

def _capture_figures():
    imgs = []
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        if plt.get_fignums():
            for num in plt.get_fignums():
                fig = plt.figure(num)
                imgs.append(_render_figure_html(fig))
            plt.close('all')
    except Exception:
        pass
    return imgs

while True:
    line = sys.stdin.readline()
    if not line:
        break
    line = line.lstrip('\ufeff')
    if not line.strip():
        continue
    try:
        msg = json.loads(line)
        if msg.get('type') == 'getVariables':
            sys.stdout.write(json.dumps({'status': 'ok', 'stdout': '', 'stderr': '', 'result': '', 'variables': _get_variables()}, ensure_ascii=False) + '\n')
            sys.stdout.flush()
            continue

        if msg.get('type') == 'updatePlotView':
            fig_id = str(msg.get('figId', ''))
            elev = float(msg.get('elev', 30))
            azim = float(msg.get('azim', -60))
            fig = _active_figures.get(fig_id)
            if fig is not None:
                for ax in fig.axes:
                    if hasattr(ax, 'view_init'):
                        try:
                            ax.view_init(elev=elev, azim=azim)
                        except Exception:
                            pass
                html = _render_figure_html(fig, fig_id=fig_id)
                sys.stdout.write(json.dumps({'status': 'ok', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': html, 'elev': elev, 'azim': azim}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            else:
                sys.stdout.write(json.dumps({'status': 'error', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': ''}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            continue

        raw_code = msg.get('code', '')
        stdout_buf = io.StringIO()
        stderr_buf = io.StringIO()
        result_obj = None
        extra_html = []

        clean_lines = []
        for l in raw_code.split('\n'):
            stripped = l.lstrip()
            if stripped.startswith('%') or stripped.startswith('!'):
                clean_lines.append('# ' + l)
            else:
                clean_lines.append(l)
        code = '\n'.join(clean_lines)

        try:
            import matplotlib
            matplotlib.use('Agg')
            import matplotlib.pyplot as plt
            def _custom_show(*args, **kwargs):
                for num in plt.get_fignums():
                    fig = plt.figure(num)
                    extra_html.append(_render_figure_html(fig))
                plt.close('all')
            plt.show = _custom_show
        except Exception:
            pass

        with contextlib.redirect_stdout(stdout_buf), contextlib.redirect_stderr(stderr_buf):
            try:
                tree = ast.parse(code, mode='exec')
                if tree.body:
                    if isinstance(tree.body[-1], ast.Expr):
                        exec_body = tree.body[:-1]
                        eval_expr = tree.body[-1]
                        if exec_body:
                            exec_module = ast.Module(body=exec_body, type_ignores=[])
                            exec(compile(exec_module, '<cell>', 'exec'), _ns)
                        eval_module = ast.Expression(body=eval_expr.value)
                        result_obj = eval(compile(eval_module, '<cell>', 'eval'), _ns)
                    else:
                        exec(compile(tree, '<cell>', 'exec'), _ns)
            except Exception:
                raise

        extra_html.extend(_capture_figures())

        if result_obj is not None:
            if hasattr(result_obj, '_repr_html_'):
                try:
                    h = result_obj._repr_html_()
                    if h: extra_html.append(str(h))
                except Exception:
                    pass
            elif hasattr(result_obj, '_repr_png_'):
                try:
                    p = result_obj._repr_png_()
                    if p:
                        b64 = base64.b64encode(p).decode('utf-8') if isinstance(p, bytes) else str(p)
                        extra_html.append(f'<img src=""data:image/png;base64,{b64}"" style=""max-width:100%;height:auto;margin:8px 0;display:block;"" />')
                except Exception:
                    pass

        stdout_text = stdout_buf.getvalue()
        stderr_text = stderr_buf.getvalue()
        result_text = ''
        if result_obj is not None and not extra_html:
            try:
                result_text = repr(result_obj)
            except Exception as re:
                result_text = f'<unreprable: {re}>'

        if extra_html:
            stdout_text = (stdout_text or '') + ''.join(extra_html)

        result = {'status': 'ok', 'stdout': stdout_text, 'stderr': stderr_text, 'result': result_text, 'variables': _get_variables()}
    except SystemExit:
        break
    except Exception:
        result = {'status': 'error', 'stdout': '', 'stderr': traceback.format_exc(), 'result': '', 'variables': []}

    sys.stdout.write(json.dumps(result, ensure_ascii=False) + '\n')
    sys.stdout.flush()
";

            public KernelSession(string pythonExecutable, string workingDirectory)
            {
                _pythonExecutable = pythonExecutable;
                _workingDirectory = workingDirectory;
            }

            public bool IsAlive => _process != null && !_process.HasExited;

            public async Task StartAsync()
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonExecutable,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _workingDirectory,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(KernelScript);

                _process = new Process { StartInfo = psi };
                _process.Start();

                await Task.CompletedTask;
            }

            public async Task<KernelExecutionResult> ExecuteAsync(string code)
            {
                if (_process == null || _process.HasExited)
                {
                    string err = _process != null ? await _process.StandardError.ReadToEndAsync() : string.Empty;
                    if (string.IsNullOrWhiteSpace(err)) err = "Kernel process is not running.";
                    return new KernelExecutionResult("error", string.Empty, err);
                }

                var command = JsonSerializer.Serialize(new { code });
                await _process.StandardInput.WriteLineAsync(command);
                await _process.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (!cts.Token.IsCancellationRequested)
                {
                    if (_process.HasExited)
                    {
                        string err = await _process.StandardError.ReadToEndAsync();
                        if (string.IsNullOrWhiteSpace(err)) err = $"Kernel process exited with code {_process.ExitCode}.";
                        return new KernelExecutionResult("error", string.Empty, err);
                    }

                    string? line = await ReadLineWithTimeoutAsync(_process.StandardOutput, cts.Token);
                    if (line == null)
                    {
                        if (_process.HasExited)
                        {
                            string err = await _process.StandardError.ReadToEndAsync();
                            if (string.IsNullOrWhiteSpace(err)) err = $"Kernel process exited with code {_process.ExitCode}.";
                            return new KernelExecutionResult("error", string.Empty, err);
                        }
                        return new KernelExecutionResult("error", string.Empty, "Kernel did not respond in time.");
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s))
                        {
                            string status = s.GetString() ?? "ok";
                            string stdout = root.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
                            string stderr = root.TryGetProperty("stderr", out var se) ? se.GetString() ?? string.Empty : string.Empty;
                            string result = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                            string varsJson = root.TryGetProperty("variables", out var v) ? v.GetRawText() : "[]";
                            return new KernelExecutionResult(status, stdout, stderr, result, varsJson);
                        }
                    }
                    catch
                    {
                        // Ignore non-JSON output (e.g. package initialization text) and continue reading for kernel response
                    }
                }

                return new KernelExecutionResult("error", string.Empty, "Kernel did not respond in time.");
            }

            public async Task<string> GetVariablesAsync()
            {
                if (_process == null || _process.HasExited)
                {
                    return "[]";
                }

                var command = JsonSerializer.Serialize(new { type = "getVariables" });
                await _process.StandardInput.WriteLineAsync(command);
                await _process.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!cts.Token.IsCancellationRequested)
                {
                    if (_process.HasExited)
                    {
                        return "[]";
                    }

                    string? line = await ReadLineWithTimeoutAsync(_process.StandardOutput, cts.Token);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("variables", out var varsProp))
                        {
                            return varsProp.GetRawText();
                        }
                    }
                    catch
                    {
                    }
                }

                return "[]";
            }

            public async Task<string> UpdatePlotViewAsync(string figId, double elev, double azim)
            {
                if (_process == null || _process.HasExited)
                {
                    return string.Empty;
                }

                var command = JsonSerializer.Serialize(new { type = "updatePlotView", figId, elev, azim });
                await _process.StandardInput.WriteLineAsync(command);
                await _process.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!cts.Token.IsCancellationRequested)
                {
                    if (_process.HasExited) return string.Empty;

                    string? line = await ReadLineWithTimeoutAsync(_process.StandardOutput, cts.Token);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.GetString() == "plotViewUpdated")
                        {
                            string html = root.TryGetProperty("html", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                            return html;
                        }
                    }
                    catch { }
                }

                return string.Empty;
            }

            private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken token)
            {
                var readTask = reader.ReadLineAsync();
                var completed = await Task.WhenAny(readTask, Task.Delay(-1, token));
                if (completed == readTask)
                {
                    return await readTask;
                }
                return null;
            }

            public void Dispose()
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        try { _process.StandardInput.WriteLine(); _process.StandardInput.Flush(); } catch { }
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
                _process?.Dispose();
                _process = null;
            }
        }
    }

    public sealed record KernelExecutionResult(string Status, string Stdout, string Stderr, string Result = "", string VariablesJson = "[]")
    {
        public bool IsError => !string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    }
}