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
import sys, json, io, contextlib, traceback

_ns = {'__name__': '__main__'}

while True:
    line = sys.stdin.readline()
    if not line:
        break
    try:
        msg = json.loads(line)
        code = msg.get('code', '')
        stdout_buf = io.StringIO()
        stderr_buf = io.StringIO()
        result_obj = None
        is_expression = False
        try:
            compile(code, '<cell>', 'eval')
            is_expression = True
        except SyntaxError:
            is_expression = False

        with contextlib.redirect_stdout(stdout_buf), contextlib.redirect_stderr(stderr_buf):
            if is_expression:
                result_obj = eval(code, _ns)
            else:
                exec(code, _ns)

        stdout_text = stdout_buf.getvalue()
        stderr_text = stderr_buf.getvalue()
        result_text = ''
        if result_obj is not None:
            try:
                result_text = repr(result_obj)
            except Exception as re:
                result_text = f'<unreprable: {re}>'
        result = {'status': 'ok', 'stdout': stdout_text, 'stderr': stderr_text, 'result': result_text}
    except SystemExit:
        break
    except Exception:
        result = {'status': 'error', 'stdout': '', 'stderr': traceback.format_exc(), 'result': ''}

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
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
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
                    return new KernelExecutionResult("error", string.Empty, "Kernel process is not running.");
                }

                var command = JsonSerializer.Serialize(new { code });
                await _process.StandardInput.WriteLineAsync(command);
                await _process.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                string? line = await ReadLineWithTimeoutAsync(_process.StandardOutput, cts.Token);
                if (line == null)
                {
                    return new KernelExecutionResult("error", string.Empty, "Kernel did not respond in time.");
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "ok" : "ok";
                    string stdout = root.TryGetProperty("stdout", out var so) ? so.GetString() ?? string.Empty : string.Empty;
                    string stderr = root.TryGetProperty("stderr", out var se) ? se.GetString() ?? string.Empty : string.Empty;
                    string result = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                    return new KernelExecutionResult(status, stdout, stderr, result);
                }
                catch (Exception ex)
                {
                    return new KernelExecutionResult("error", string.Empty, "Failed to parse kernel response: " + ex.Message);
                }
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

    public sealed record KernelExecutionResult(string Status, string Stdout, string Stderr, string Result = "")
    {
        public bool IsError => !string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    }
}