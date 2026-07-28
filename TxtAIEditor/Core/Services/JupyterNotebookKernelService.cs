using System;
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
        public JupyterNotebookKernelService(Func<string, string, string> getString)
        {
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

        public async Task<KernelSession> CreateSessionAsync(
            string pythonExecutable,
            string workingDirectory)
        {
            var session = new KernelSession(pythonExecutable, workingDirectory);
            await session.StartAsync();
            return session;
        }

        public void Dispose()
        {
        }

        public sealed class KernelSession : IDisposable
        {
            private Process? _process;
            private readonly string _pythonExecutable;
            private readonly string _workingDirectory;
            private static string KernelScriptPath => Path.Combine(
                PreviewWebResourceService.WebResourcesPath,
                "Notebook",
                "kernel_host.py");

            public KernelSession(string pythonExecutable, string workingDirectory)
            {
                _pythonExecutable = pythonExecutable;
                _workingDirectory = workingDirectory;
            }

            public bool IsAlive => _process != null && !_process.HasExited;

            public async Task StartAsync()
            {
                if (!File.Exists(KernelScriptPath))
                {
                    throw new FileNotFoundException(
                        "Notebook kernel host resource was not found.",
                        KernelScriptPath);
                }

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
                psi.ArgumentList.Add(KernelScriptPath);

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

            public async Task<string> UpdatePlotViewAsync(string figId, double elev, double azim, double panFracX, double panFracY, double zoom)
            {
                if (_process == null || _process.HasExited)
                {
                    return string.Empty;
                }

                var command = JsonSerializer.Serialize(new { type = "updatePlotView", figId, elev, azim, panFracX, panFracY, zoom });
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
