using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentProcessToolService
    {
        internal const int DefaultPowerShellTimeoutMs = 120000;
        internal const int MinimumProcessTimeoutMs = 1000;
        internal const int MaximumProcessTimeoutMs = 300000;

        private readonly AgentWorkspaceFileResolver _workspace;
        private readonly Func<string, int, Task<string>> _searchTextFallbackAsync;
        private readonly Func<string, Task<bool>> _confirmPowerShellAsync;

        public AgentProcessToolService(
            AgentWorkspaceFileResolver workspace,
            Func<string, int, Task<string>> searchTextFallbackAsync,
            Func<string, Task<bool>> confirmPowerShellAsync)
        {
            _workspace = workspace;
            _searchTextFallbackAsync = searchTextFallbackAsync;
            _confirmPowerShellAsync = confirmPowerShellAsync;
        }

        public async Task<string> RunRgAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "run_rg failed: arguments are empty.";
            }

            string resolvedRg = ResolveExecutablePath("rg");
            string workspaceRoot = _workspace.ResolveWorkspaceRoot();

            string result = await RunProcessAsync(resolvedRg, arguments, workspaceRoot, timeoutMs <= 0 ? 10000 : timeoutMs, cancellationToken);

            if (result.Contains("failed to start") || result.Contains("timed out after"))
            {
                string query = ExtractQueryFromRgArguments(arguments);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    string fallbackResult = await _searchTextFallbackAsync(query, 80);
                    return $"[run_rg failed: fell back to search_text for query \"{query}\"]\n{fallbackResult}";
                }
            }

            return result;
        }

        public async Task<string> RunRgaAsync(string arguments, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "run_rga failed: arguments are empty.";
            }

            string resolvedRga = ResolveExecutablePath("rga");
            string workspaceRoot = _workspace.ResolveWorkspaceRoot();

            string result = await RunProcessAsync(resolvedRga, arguments, workspaceRoot, timeoutMs <= 0 ? 10000 : timeoutMs, cancellationToken);

            if (result.Contains("failed to start"))
            {
                return $"{result}\nNote: Make sure 'ripgrep-all' (rga) is installed and available in the system PATH.";
            }

            return result;
        }

        public async Task<string> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(command))
            {
                return "run_powershell failed: command is empty.";
            }

            command = NormalizePowerShellTextWriteSafety(command);

            if (!IsClearlySafePowerShell(command) && !await _confirmPowerShellAsync(command))
            {
                return "run_powershell cancelled by user.";
            }

            var profile = TerminalShellProfile.Resolve("PowerShell");
            string shellPath = profile.ExecutablePath;
            string normalizedCommand = command.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            string utf8Command = "$utf8NoBom = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = $utf8NoBom; " +
                "try { [Console]::OutputEncoding = $utf8NoBom; [Console]::InputEncoding = $utf8NoBom } catch {}; " +
                "try { $PSDefaultParameterValues['*:Encoding'] = 'utf8' } catch {}; " +
                BuildPowerShell7RedirectPrelude(shellPath) +
                "$env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'; " +
                normalizedCommand;
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Command));

            var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8"
            };

            return await RunProcessAsync(
                shellPath,
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                _workspace.ResolveWorkspaceRoot(),
                timeoutMs <= 0 ? DefaultPowerShellTimeoutMs : timeoutMs,
                cancellationToken,
                Encoding.UTF8,
                environmentVariables);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static Encoding GetSystemAnsiEncoding()
        {
            try
            {
                int acp = (int)GetACP();
                if (acp > 0)
                {
                    return Encoding.GetEncoding(acp);
                }
            }
            catch
            {
            }
            return Encoding.UTF8;
        }

        private static string DecodeBytes(byte[] bytes, Encoding preferredEncoding)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (TextEncodingService.IsValidUtf8(bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            try
            {
                var systemEncoding = GetSystemAnsiEncoding();
                return systemEncoding.GetString(bytes);
            }
            catch
            {
                return preferredEncoding.GetString(bytes);
            }
        }

        private static string ParseCliXml(string clixml)
        {
            if (string.IsNullOrWhiteSpace(clixml) || !clixml.StartsWith("#< CLIXML"))
            {
                return clixml;
            }

            try
            {
                string xmlContent = clixml.Substring("#< CLIXML".Length).Trim();
                var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
                var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

                var lines = new List<string>();

                var sElements = doc.Descendants(ns + "S").ToList();
                foreach (var s in sElements)
                {
                    string? sAttr = s.Attribute("S")?.Value;
                    string? nAttr = s.Attribute("N")?.Value;

                    if (sAttr == "Error" || sAttr == "Warning")
                    {
                        lines.Add(DecodeCliXmlString(s.Value));
                    }
                    else if (nAttr == "Message")
                    {
                        lines.Add(DecodeCliXmlString(s.Value));
                    }
                }

                if (lines.Count > 0)
                {
                    return string.Join("", lines).Trim();
                }

                var toStrings = doc.Descendants(ns + "ToString").Select(x => x.Value).ToList();
                if (toStrings.Count > 0)
                {
                    return string.Join(Environment.NewLine, toStrings.Select(DecodeCliXmlString)).Trim();
                }
            }
            catch
            {
            }

            try
            {
                var matches = Regex.Matches(clixml, @"<S\b[^>]*>([^<]*)</S>");
                var fallbackLines = new List<string>();
                foreach (Match m in matches)
                {
                    string val = m.Groups[1].Value;
                    fallbackLines.Add(DecodeCliXmlString(val));
                }
                if (fallbackLines.Count > 0)
                {
                    return string.Join("", fallbackLines).Trim();
                }
            }
            catch
            {
            }

            return clixml;
        }

        private static string DecodeCliXmlString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string decoded = Regex.Replace(input, @"_x([0-9a-fA-F]{4})_", m =>
            {
                return ((char)Convert.ToUInt16(m.Groups[1].Value, 16)).ToString();
            });
            decoded = Regex.Replace(decoded, @"\x1B\[[0-9;]*[a-zA-Z]", "");
            return decoded;
        }

        private static async Task<string> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMs,
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null)
        {
            var output = new StringBuilder();
            using var process = new Process();
            var encoding = outputEncoding ?? Encoding.UTF8;
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding
            };

            if (environmentVariables != null)
            {
                foreach (var pair in environmentVariables)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            process.StartInfo = startInfo;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"{fileName} failed to start: {ex.Message}";
            }

            Task<byte[]> stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
            Task<byte[]> stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream, cancellationToken);
            Task exitTask = process.WaitForExitAsync(cancellationToken);

            try
            {
                int effectiveTimeoutMs = Math.Clamp(timeoutMs, MinimumProcessTimeoutMs, MaximumProcessTimeoutMs);
                Task completed = await Task.WhenAny(exitTask, Task.Delay(effectiveTimeoutMs, cancellationToken));
                if (completed != exitTask)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    return $"{fileName} timed out after {effectiveTimeoutMs}ms.";
                }

                await exitTask;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                throw;
            }

            string stdout = DecodeBytes(await stdoutTask, encoding);
            string stderr = ParseCliXml(DecodeBytes(await stderrTask, encoding));

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                output.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.AppendLine("[stderr]");
                output.AppendLine(stderr.TrimEnd());
            }

            output.AppendLine($"[exit_code] {process.ExitCode}");

            string text = output.ToString();
            return text.Length > 20000 ? text.Substring(0, 20000) + "\n[output truncated]" : text;
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            return memoryStream.ToArray();
        }

        private static string ResolveExecutablePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            string candidate = string.Empty;
            if (Path.IsPathRooted(fileName))
            {
                candidate = fileName;
            }
            else
            {
                string? pathValue = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathValue))
                {
                    string searchName = fileName;
                    if (!searchName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        searchName += ".exe";
                    }

                    foreach (string directory in pathValue.Split(Path.PathSeparator))
                    {
                        if (string.IsNullOrWhiteSpace(directory))
                        {
                            continue;
                        }

                        try
                        {
                            string path = Path.Combine(directory.Trim(), searchName);
                            if (File.Exists(path))
                            {
                                candidate = path;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
            {
                return fileName;
            }

            try
            {
                var fileInfo = new FileInfo(candidate);
                if (fileInfo.Exists)
                {
                    var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        return target.FullName;
                    }
                }
            }
            catch
            {
            }

            return candidate;
        }

        private static string ExtractQueryFromRgArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return string.Empty;
            }

            var match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+""([^""]+)""");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+'([^']+)'");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(arguments, @"(?:-e|--regexp|-F|--fixed-strings)\s+(\S+)");
            if (match.Success) return match.Groups[1].Value;

            var quotedMatches = Regex.Matches(arguments, @"""([^""]+)""");
            if (quotedMatches.Count > 0)
            {
                foreach (Match m in quotedMatches)
                {
                    string val = m.Groups[1].Value;
                    if (!val.StartsWith('-'))
                    {
                        return val;
                    }
                }
            }

            quotedMatches = Regex.Matches(arguments, @"'([^']+)'");
            if (quotedMatches.Count > 0)
            {
                foreach (Match m in quotedMatches)
                {
                    string val = m.Groups[1].Value;
                    if (!val.StartsWith('-'))
                    {
                        return val;
                    }
                }
            }

            string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string token = tokens[i];
                if (!token.StartsWith('-'))
                {
                    if (i > 0 && (tokens[i - 1] == "-g" || tokens[i - 1] == "-t" || tokens[i - 1] == "--type" || tokens[i - 1] == "-e"))
                    {
                        continue;
                    }
                    return token;
                }
            }

            string cleaned = Regex.Replace(arguments, @"-[a-zA-Z0-9\-]+", "").Trim();
            cleaned = cleaned.Replace("\"", "").Replace("'", "").Trim();
            return cleaned;
        }

        private static bool IsClearlySafePowerShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string normalized = command.Trim().ToLowerInvariant();

            string[] riskyOperators = { ";", "&&", "||", "|", ">", ">>", "$(", "@(", "&" };
            if (riskyOperators.Any(op => normalized.Contains(op, StringComparison.Ordinal)))
            {
                return false;
            }

            string[] safePrefixes =
            {
                "get-childitem",
                "gci",
                "dir",
                "ls",
                "get-content",
                "gc",
                "select-string",
                "test-path",
                "get-item",
                "get-command",
                "where.exe",
                "git status",
                "git diff",
                "git log",
                "dotnet --info"
            };

            bool startsWithSafePrefix = safePrefixes.Any(prefix =>
            {
                if (normalized == prefix) return true;
                if (normalized.StartsWith(prefix + " ", StringComparison.Ordinal)) return true;
                return false;
            });

            if (!startsWithSafePrefix)
            {
                return false;
            }

            string[] risky =
            {
                "set-content", "add-content", "out-file",
                "new-item", "remove-item", "move-item", "rename-item", "copy-item",
                "invoke-expression", "iex", "invoke-webrequest", "iwr", "curl",
                "start-process", "cmd ", "cmd.exe", "powershell", "pwsh",
                "reg ", "schtasks", "icacls", "takeown"
            };

            if (risky.Any(x => normalized.Contains(x, StringComparison.Ordinal)))
            {
                return false;
            }

            return true;
        }

        private static string BuildPowerShell7RedirectPrelude(string shellPath)
        {
            if (!string.Equals(Path.GetFileName(shellPath), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string escapedShellPath = shellPath.Replace("'", "''", StringComparison.Ordinal);
            return $"try {{ Set-Alias -Name powershell -Value '{escapedShellPath}' -Scope Script; Set-Alias -Name powershell.exe -Value '{escapedShellPath}' -Scope Script }} catch {{}}; ";
        }

        private static string NormalizePowerShellTextWriteSafety(string command)
        {
            bool writesText = Regex.IsMatch(command, @"(?i)\bSet-Content\b");
            if (!writesText)
            {
                return command;
            }

            if (Regex.IsMatch(command, @"(?i)\bGet-Content\b"))
            {
                command = AddMissingPowerShellSwitch(command, "Get-Content", "-Raw");
            }

            return AddMissingPowerShellSwitch(command, "Set-Content", "-NoNewline");
        }

        private static string AddMissingPowerShellSwitch(string command, string cmdletName, string switchName)
        {
            string invocationPattern = $@"(?is)\b{Regex.Escape(cmdletName)}\b(?<arguments>.*?)(?=(?:[;|\r\n]|$))";
            string escapedSwitch = Regex.Escape(switchName);

            return Regex.Replace(command, invocationPattern, match =>
            {
                if (Regex.IsMatch(match.Value, $@"(?i)(?<![\p{{L}}\p{{N}}_]){escapedSwitch}\b"))
                {
                    return match.Value;
                }

                return match.Value.Insert(cmdletName.Length, " " + switchName);
            });
        }
    }
}
