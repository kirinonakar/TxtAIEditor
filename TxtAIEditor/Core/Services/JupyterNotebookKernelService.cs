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

        public async Task<KernelExecutionResult> ExecuteAsync(string tabId, string pythonExecutable, string workingDirectory, string code, Func<string, Task>? onInputRequest = null, Func<string, string, Task>? onStreamOutput = null)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.ExecuteAsync(code, onInputRequest, onStreamOutput);
            }
            catch (Exception ex)
            {
                return new KernelExecutionResult("error", string.Empty, ex.Message);
            }
        }

        public async Task SendInputReplyAsync(string tabId, string replyValue)
        {
            if (_sessions.TryGetValue(tabId, out var session))
            {
                await session.SendInputReplyAsync(replyValue);
            }
        }

        public void InterruptSession(string tabId)
        {
            if (_sessions.TryRemove(tabId, out var session))
            {
                session.Dispose();
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

        public async Task<string> UpdatePlotViewAsync(string tabId, string pythonExecutable, string workingDirectory, string figId, double elev, double azim, double zoom)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.UpdatePlotViewAsync(figId, elev, azim, zoom);
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<string> Update2DViewAsync(string tabId, string pythonExecutable, string workingDirectory, string figId, double panFracX, double panFracY, double zoom)
        {
            try
            {
                var session = await GetOrCreateSessionAsync(tabId, pythonExecutable, workingDirectory);
                return await session.Update2DViewAsync(figId, panFracX, panFracY, zoom);
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
import sys, json, io, base64, contextlib, traceback, ast, builtins

try:
    if hasattr(sys.stdin, 'reconfigure'): sys.stdin.reconfigure(line_buffering=True)
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

def _custom_input(prompt=''):
    try:
        p_str = str(prompt) if prompt is not None else ''
        sys.__stdout__.write(json.dumps({'type': 'input_request', 'prompt': p_str}, ensure_ascii=False) + '\n')
        sys.__stdout__.flush()
        line = sys.stdin.readline()
        if not line:
            return ''
        try:
            data = json.loads(line)
            if isinstance(data, dict) and 'value' in data:
                return str(data['value'])
            return line.rstrip('\r\n')
        except Exception:
            return line.rstrip('\r\n')
    except Exception:
        return ''

builtins.input = _custom_input

class _StreamWrapper(io.TextIOBase):
    def __init__(self, name, buf):
        self.name = name
        self.buf = buf
    def write(self, s):
        if not s: return 0
        res = self.buf.write(s)
        try:
            sys.__stdout__.write(json.dumps({'type': 'stream', 'name': self.name, 'text': str(s)}, ensure_ascii=False) + '\n')
            sys.__stdout__.flush()
        except Exception:
            pass
        return res
    def flush(self):
        try: self.buf.flush()
        except Exception: pass

_ns = {'__name__': '__main__'}
_inline_backend_config = {'figure_format': 'retina', 'dpi': 200}

def _process_magic(line):
    line = line.strip()
    if 'InlineBackend.figure_format' in line or 'InlineBackend.figure_formats' in line:
        line_lower = line.lower()
        if 'retina' in line_lower:
            _inline_backend_config['figure_format'] = 'retina'
            _inline_backend_config['dpi'] = 200
        elif 'svg' in line_lower:
            _inline_backend_config['figure_format'] = 'svg'
        elif 'png' in line_lower:
            _inline_backend_config['figure_format'] = 'png'
            _inline_backend_config['dpi'] = 200
        elif 'jpeg' in line_lower or 'jpg' in line_lower:
            _inline_backend_config['figure_format'] = 'jpeg'
            _inline_backend_config['dpi'] = 200

def _get_variables():
    vars_list = []
    ignored = {'sys', 'json', 'io', 'base64', 'contextlib', 'traceback', 'ast', 'plt', 'matplotlib', '_ns', '_get_variables', '_render_figure_html', '_capture_figures', '_custom_show', '_inline_backend_config', '_process_magic', '_get_2d_plot_bounds'}
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
_figure_view_state = {}

def _store_figure_limits(fig, fig_id):
    limits = []
    limits_3d = []
    for ax in fig.axes:
        is_ax_3d = hasattr(ax, 'view_init') or getattr(ax, 'name', '') == '3d' or '3d' in str(type(ax)).lower()
        if is_ax_3d:
            try:
                limits_3d.append({
                    'xlim': list(ax.get_xlim3d()),
                    'ylim': list(ax.get_ylim3d()),
                    'zlim': list(ax.get_zlim3d())
                })
            except Exception:
                pass
            continue
        try:
            limits.append({'xlim': list(ax.get_xlim()), 'ylim': list(ax.get_ylim())})
        except Exception:
            pass
    if limits or limits_3d:
        _figure_view_state[fig_id] = {
            'orig_limits': limits,
            'orig_3d_limits': limits_3d
        }

def _get_2d_plot_bounds(fig):
    try:
        import matplotlib
        fig.canvas.draw()
        renderer = fig.canvas.get_renderer()
        tight_bbox = fig.get_tightbbox(renderer)
        if not tight_bbox or tight_bbox.width <= 0 or tight_bbox.height <= 0:
            return None
        tight_bbox = tight_bbox.padded(0.08)

        target_ax = None
        for ax in fig.axes:
            if not (hasattr(ax, 'view_init') or getattr(ax, 'name', '') == '3d' or '3d' in str(type(ax)).lower()):
                label = str(getattr(ax, 'get_label', lambda: '')())
                if getattr(ax, '_colorbar', None) is not None or label == '<colorbar>' or 'colorbar' in label.lower():
                    continue
                target_ax = ax
                break

        if target_ax is None:
            return None

        ax_bbox = target_ax.get_window_extent(renderer)
        dpi = fig.dpi
        tb_x0, tb_y0, tb_w, tb_h = tight_bbox.x0 * dpi, tight_bbox.y0 * dpi, tight_bbox.width * dpi, tight_bbox.height * dpi

        left_pct = (ax_bbox.x0 - tb_x0) / tb_w * 100.0
        top_pct = (tb_y0 + tb_h - ax_bbox.y1) / tb_h * 100.0
        width_pct = ax_bbox.width / tb_w * 100.0
        height_pct = ax_bbox.height / tb_h * 100.0

        left_pct = max(0.0, min(100.0, left_pct))
        top_pct = max(0.0, min(100.0, top_pct))
        width_pct = max(0.0, min(100.0, width_pct))
        height_pct = max(0.0, min(100.0, height_pct))

        return {
            'left': round(left_pct, 2),
            'top': round(top_pct, 2),
            'width': round(width_pct, 2),
            'height': round(height_pct, 2)
        }
    except Exception:
        return None

def _render_2d_figure_layers(fig, save_kwargs):
    import io, base64

    layer_kwargs = dict(save_kwargs)
    try:
        fig.canvas.draw()
        renderer = fig.canvas.get_renderer()
        render_bbox = fig.get_tightbbox(renderer)
        if render_bbox and render_bbox.width > 0 and render_bbox.height > 0:
            pad_inches = float(layer_kwargs.pop('pad_inches', 0.0) or 0.0)
            layer_kwargs['bbox_inches'] = render_bbox.padded(pad_inches)
    except Exception:
        pass

    data_artists = []
    data_axes = []
    other_axes = []
    for ax in fig.axes:
        label = str(getattr(ax, 'get_label', lambda: '')())
        is_colorbar = getattr(ax, '_colorbar', None) is not None or label == '<colorbar>' or 'colorbar' in label.lower()
        is_ax_3d = hasattr(ax, 'view_init') or getattr(ax, 'name', '') == '3d' or '3d' in str(type(ax)).lower()
        if is_colorbar or is_ax_3d:
            other_axes.append(ax)
            continue

        data_axes.append(ax)
        candidates = (
            list(getattr(ax, 'lines', [])) +
            list(getattr(ax, 'collections', [])) +
            list(getattr(ax, 'images', [])) +
            list(getattr(ax, 'patches', [])) +
            list(getattr(ax, 'texts', [])) +
            list(getattr(ax, 'artists', []))
        )
        for artist in candidates:
            if artist is getattr(ax, 'patch', None) or artist in data_artists:
                continue
            data_artists.append(artist)

    data_visibility = [(artist, artist.get_visible()) for artist in data_artists]
    try:
        for artist, _ in data_visibility:
            artist.set_visible(False)
        background_buf = io.BytesIO()
        fig.savefig(background_buf, facecolor='white', edgecolor='none', **layer_kwargs)
        background_buf.seek(0)
        background_b64 = base64.b64encode(background_buf.read()).decode('utf-8')
    finally:
        for artist, visible in data_visibility:
            artist.set_visible(visible)

    decoration_artists = [fig.patch]
    decoration_artists.extend(list(getattr(fig, 'texts', [])))
    decoration_artists.extend(list(getattr(fig, 'legends', [])))
    for ax in data_axes:
        decoration_artists.extend([
            getattr(ax, 'patch', None),
            getattr(ax, 'xaxis', None),
            getattr(ax, 'yaxis', None),
            getattr(ax, 'title', None),
            getattr(ax, '_left_title', None),
            getattr(ax, '_right_title', None),
            ax.get_legend()
        ])
        decoration_artists.extend(list(getattr(ax, 'spines', {}).values()))
    decoration_artists.extend(other_axes)

    unique_decorations = []
    for artist in decoration_artists:
        if artist is not None and artist not in unique_decorations:
            unique_decorations.append(artist)
    decoration_visibility = [(artist, artist.get_visible()) for artist in unique_decorations]

    data_buf = io.BytesIO()
    try:
        for artist, _ in decoration_visibility:
            artist.set_visible(False)
        data_kwargs = dict(layer_kwargs)
        data_kwargs['format'] = 'png'
        data_buf = io.BytesIO()
        fig.savefig(data_buf, transparent=True, facecolor='none', edgecolor='none', **data_kwargs)
        data_buf.seek(0)
        data_b64 = base64.b64encode(data_buf.read()).decode('utf-8')
    finally:
        for artist, visible in decoration_visibility:
            artist.set_visible(visible)

    return background_b64, data_b64

def _render_figure_html(fig, fig_id=None):
    import io, base64, json
    if not fig_id:
        fig_id = str(id(fig))
    _active_figures[fig_id] = fig
    if fig_id not in _figure_view_state:
        _store_figure_limits(fig, fig_id)

    fmt = _inline_backend_config.get('figure_format', 'retina')
    dpi_val = _inline_backend_config.get('dpi', 200)
    try:
        import matplotlib
        import matplotlib.pyplot as plt
        user_savefig_dpi = plt.rcParams.get('savefig.dpi', None)
        if isinstance(user_savefig_dpi, (int, float)) and user_savefig_dpi > 0:
            dpi_val = max(dpi_val, int(user_savefig_dpi))
        elif hasattr(fig, 'dpi') and fig.dpi:
            target_multiplier = 2 if fmt == 'retina' else 1
            dpi_val = max(dpi_val, int(fig.dpi * target_multiplier))
    except Exception:
        pass

    if fmt == 'svg':
        mime = 'image/svg+xml'
        save_kwargs = {'format': 'svg', 'bbox_inches': 'tight', 'pad_inches': 0.08}
    elif fmt in ('jpeg', 'jpg'):
        mime = 'image/jpeg'
        save_kwargs = {'format': 'jpeg', 'bbox_inches': 'tight', 'pad_inches': 0.08, 'dpi': dpi_val}
    else:
        mime = 'image/png'
        save_kwargs = {'format': 'png', 'bbox_inches': 'tight', 'pad_inches': 0.08, 'dpi': dpi_val}

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

    bounds_attr = ''
    if not is_3d:
        b = _get_2d_plot_bounds(fig)
        if b:
            b_json = json.dumps(b).replace('""', '&quot;')
            bounds_attr = f' data-plot-bounds=""{b_json}""'

    try:
        fig_w, _ = fig.get_size_inches()
        logical_w = int(fig_w * 100)
    except Exception:
        logical_w = 640
    if logical_w < 300:
        logical_w = 300
    style_attr = f' style=""max-width:{logical_w}px; width:100%;""'

    colorbars = []
    try:
        for ax in fig.axes:
            label = str(getattr(ax, 'get_label', lambda: '')())
            if getattr(ax, '_colorbar', None) is not None or label == '<colorbar>' or 'colorbar' in label.lower():
                colorbars.append(ax)
    except Exception:
        pass

    is_3d_str = 'true' if is_3d else 'false'
    status_3d = 'Drag: Pan | 🔍 Zoom + Wheel: Zoom | Right-Click Drag: Rotate'
    status_2d = 'Drag: Pan | 🔍 Zoom + Wheel: Zoom'
    status_text = status_3d if is_3d else status_2d

    toolbar_3d = f'''<div class=""mpl-toolbar""><button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button><button class=""mpl-btn mpl-btn-zoom"" title=""Toggle Zoom Mode (Scroll Wheel)"">🔍 Zoom</button><div class=""mpl-rotate-ctrl""><span>Azim:</span><input type=""range"" class=""mpl-rotate-y-slider"" min=""-180"" max=""180"" value=""{cur_azim}"" /><span class=""mpl-angle-val-y"">{cur_azim}°</span></div><div class=""mpl-rotate-ctrl""><span>Elev:</span><input type=""range"" class=""mpl-rotate-x-slider"" min=""-90"" max=""90"" value=""{cur_elev}"" /><span class=""mpl-angle-val-x"">{cur_elev}°</span></div><button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button><span class=""mpl-status-text"">{status_text}</span></div>'''

    toolbar_2d = f'''<div class=""mpl-toolbar""><button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button><button class=""mpl-btn mpl-btn-zoom"" title=""Toggle Zoom Mode (Scroll Wheel)"">🔍 Zoom</button><button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button><span class=""mpl-status-text"">{status_text}</span></div>'''

    toolbar = toolbar_3d if is_3d else toolbar_2d

    if not is_3d:
        try:
            b64_background, b64_data = _render_2d_figure_layers(fig, save_kwargs)
            return f'''<!--MPL_START--><div class=""mpl-interactive-wrapper"" data-mpl=""true"" data-is-3d=""false"" data-fig-id=""{fig_id}"" data-elev=""{cur_elev}"" data-azim=""{cur_azim}""{bounds_attr}{style_attr}>{toolbar}<div class=""mpl-viewport""><div class=""mpl-plot-layer""><img src=""data:{mime};base64,{b64_background}"" class=""mpl-plot-img"" /><div class=""mpl-data-clip""><div class=""mpl-data-img-wrapper""><img src=""data:image/png;base64,{b64_data}"" class=""mpl-data-img"" /></div></div></div></div></div><!--MPL_END-->'''
        except Exception:
            pass

    if colorbars:
        try:
            for cb in colorbars: cb.set_visible(False)
            buf_main = io.BytesIO()
            fig.savefig(buf_main, facecolor='white', edgecolor='none', **save_kwargs)
            buf_main.seek(0)
            b64_main = base64.b64encode(buf_main.read()).decode('utf-8')

            for cb in colorbars: cb.set_visible(True)
            for ax in fig.axes:
                if ax not in colorbars: ax.set_visible(False)
            buf_cbar = io.BytesIO()
            fig.savefig(buf_cbar, transparent=True, **save_kwargs)
            buf_cbar.seek(0)
            b64_cbar = base64.b64encode(buf_cbar.read()).decode('utf-8')

            for ax in fig.axes: ax.set_visible(True)

            return f'''<!--MPL_START--><div class=""mpl-interactive-wrapper"" data-mpl=""true"" data-is-3d=""{is_3d_str}"" data-fig-id=""{fig_id}"" data-elev=""{cur_elev}"" data-azim=""{cur_azim}""{bounds_attr}{style_attr}>{toolbar}<div class=""mpl-viewport""><div class=""mpl-plot-layer""><img src=""data:{mime};base64,{b64_main}"" class=""mpl-plot-img"" /><div class=""mpl-data-clip""><div class=""mpl-data-img-wrapper""><img src=""data:{mime};base64,{b64_main}"" class=""mpl-data-img"" /></div></div></div><div class=""mpl-cbar-layer""><img src=""data:{mime};base64,{b64_cbar}"" class=""mpl-cbar-img"" /></div></div></div><!--MPL_END-->'''
        except Exception:
            pass

    buf = io.BytesIO()
    fig.savefig(buf, facecolor='white', edgecolor='none', **save_kwargs)
    buf.seek(0)
    b64 = base64.b64encode(buf.read()).decode('utf-8')
    return f'''<!--MPL_START--><div class=""mpl-interactive-wrapper"" data-mpl=""true"" data-is-3d=""{is_3d_str}"" data-fig-id=""{fig_id}"" data-elev=""{cur_elev}"" data-azim=""{cur_azim}""{bounds_attr}{style_attr}>{toolbar}<div class=""mpl-viewport""><div class=""mpl-plot-layer""><img src=""data:{mime};base64,{b64}"" class=""mpl-plot-img"" /><div class=""mpl-data-clip""><div class=""mpl-data-img-wrapper""><img src=""data:{mime};base64,{b64}"" class=""mpl-data-img"" /></div></div></div></div></div><!--MPL_END-->'''

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
            zoom_val = float(msg.get('zoom', 1.0))
            if zoom_val <= 0: zoom_val = 1.0
            fig = _active_figures.get(fig_id)
            state = _figure_view_state.get(fig_id)
            if fig is not None:
                orig_3d_limits = state.get('orig_3d_limits', []) if state is not None else []
                ax_idx = 0
                for ax in fig.axes:
                    if hasattr(ax, 'view_init'):
                        try:
                            ax.view_init(elev=elev, azim=azim)
                        except Exception:
                            pass
                        if ax_idx < len(orig_3d_limits):
                            lim = orig_3d_limits[ax_idx]
                            for axis_name in ('x', 'y', 'z'):
                                lo, hi = lim[axis_name + 'lim']
                                center = (lo + hi) / 2.0
                                radius = (hi - lo) / (2.0 * zoom_val)
                                getattr(ax, 'set_' + axis_name + 'lim3d')(center - radius, center + radius)
                            ax_idx += 1
                html = _render_figure_html(fig, fig_id=fig_id)
                sys.stdout.write(json.dumps({'status': 'ok', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': html, 'elev': elev, 'azim': azim, 'zoom': zoom_val}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            else:
                sys.stdout.write(json.dumps({'status': 'error', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': ''}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            continue

        if msg.get('type') == 'update2DView':
            fig_id = str(msg.get('figId', ''))
            pan_frac_x = float(msg.get('panFracX', 0))
            pan_frac_y = float(msg.get('panFracY', 0))
            zoom_val = float(msg.get('zoom', 1.0))
            if zoom_val <= 0: zoom_val = 1.0
            fig = _active_figures.get(fig_id)
            state = _figure_view_state.get(fig_id)
            if fig is not None and state is not None:
                orig_limits = state['orig_limits']
                ax_idx = 0
                for ax in fig.axes:
                    if hasattr(ax, 'view_init') or getattr(ax, 'name', '') == '3d' or '3d' in str(type(ax)).lower():
                        continue
                    if ax_idx < len(orig_limits):
                        lim = orig_limits[ax_idx]
                        ox0, ox1 = lim['xlim']
                        oy0, oy1 = lim['ylim']
                        xr = ox1 - ox0
                        yr = oy1 - oy0
                        xc = (ox0 + ox1) / 2.0 + pan_frac_x * xr
                        yc = (oy0 + oy1) / 2.0 + pan_frac_y * yr
                        nxr = xr / zoom_val
                        nyr = yr / zoom_val
                        ax.set_xlim(xc - nxr / 2, xc + nxr / 2)
                        ax.set_ylim(yc - nyr / 2, yc + nyr / 2)
                        ax_idx += 1
                html = _render_figure_html(fig, fig_id=fig_id)
                sys.stdout.write(json.dumps({'status': 'ok', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': html}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            else:
                sys.stdout.write(json.dumps({'status': 'error', 'type': 'plotViewUpdated', 'figId': fig_id, 'html': ''}, ensure_ascii=False) + '\n')
                sys.stdout.flush()
            continue

        raw_code = msg.get('code', '')
        stdout_buf = io.StringIO()
        stderr_buf = io.StringIO()
        stream_out = _StreamWrapper('stdout', stdout_buf)
        stream_err = _StreamWrapper('stderr', stderr_buf)
        result_obj = None
        extra_html = []

        clean_lines = []
        for l in raw_code.split('\n'):
            stripped = l.lstrip()
            if stripped.startswith('%') or stripped.startswith('!'):
                try:
                    _process_magic(stripped)
                except Exception:
                    pass
                clean_lines.append('# ' + l)
            else:
                clean_lines.append(l)
        code = '\n'.join(clean_lines)

        try:
            import matplotlib
            matplotlib.use('Agg')
            import matplotlib.pyplot as plt
            plt.rcParams['figure.dpi'] = 144
            plt.rcParams['savefig.dpi'] = 200
            if sys.platform.startswith('win'):
                plt.rcParams['font.family'] = 'Malgun Gothic'
            elif sys.platform.startswith('darwin'):
                plt.rcParams['font.family'] = 'AppleGothic'
            else:
                plt.rcParams['font.family'] = 'NanumGothic'
            plt.rcParams['axes.unicode_minus'] = False
            def _custom_show(*args, **kwargs):
                for num in plt.get_fignums():
                    fig = plt.figure(num)
                    extra_html.append(_render_figure_html(fig))
                plt.close('all')
            plt.show = _custom_show
        except Exception:
            pass

        with contextlib.redirect_stdout(stream_out), contextlib.redirect_stderr(stream_err):
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

            public async Task SendInputReplyAsync(string replyValue)
            {
                if (_process != null && !_process.HasExited)
                {
                    var json = JsonSerializer.Serialize(new { value = replyValue });
                    await _process.StandardInput.WriteLineAsync(json);
                    await _process.StandardInput.FlushAsync();
                }
            }

            public async Task<KernelExecutionResult> ExecuteAsync(string code, Func<string, Task>? onInputRequest = null, Func<string, string, Task>? onStreamOutput = null)
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

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));
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
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("type", out var tProp))
                            {
                                string typeStr = tProp.GetString() ?? "";
                                if (string.Equals(typeStr, "input_request", StringComparison.Ordinal))
                                {
                                    string prompt = root.TryGetProperty("prompt", out var pProp) ? pProp.GetString() ?? "" : "";
                                    if (onInputRequest != null)
                                    {
                                        await onInputRequest(prompt);
                                    }
                                    continue;
                                }
                                if (string.Equals(typeStr, "stream", StringComparison.Ordinal))
                                {
                                    string sName = root.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "stdout" : "stdout";
                                    string sText = root.TryGetProperty("text", out var txtProp) ? txtProp.GetString() ?? "" : "";
                                    if (onStreamOutput != null)
                                    {
                                        await onStreamOutput(sName, sText);
                                    }
                                    continue;
                                }
                            }

                            if (root.TryGetProperty("status", out var s))
                            {
                                string status = s.GetString() ?? "ok";
                                string stdout = root.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
                                string stderr = root.TryGetProperty("stderr", out var se) ? se.GetString() ?? string.Empty : string.Empty;
                                string result = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                                string varsJson = root.TryGetProperty("variables", out var v) ? v.GetRawText() : "[]";
                                return new KernelExecutionResult(status, stdout, stderr, result, varsJson);
                            }
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

            public async Task<string> UpdatePlotViewAsync(string figId, double elev, double azim, double zoom)
            {
                if (_process == null || _process.HasExited)
                {
                    return string.Empty;
                }

                var command = JsonSerializer.Serialize(new { type = "updatePlotView", figId, elev, azim, zoom });
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

            public async Task<string> Update2DViewAsync(string figId, double panFracX, double panFracY, double zoom)
            {
                if (_process == null || _process.HasExited)
                {
                    return string.Empty;
                }

                var command = JsonSerializer.Serialize(new { type = "update2DView", figId, panFracX, panFracY, zoom });
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
