using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TxtAIEditor.Core.Services
{
    public sealed class ConPtyTerminal : IDisposable
    {
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int HPCON_INVALID = unchecked((int)0x80070057);
        private const int GENERIC_READ = unchecked((int)0x80000000);
        private const int FILE_SHARE_READ = 0x00000001;
        private const int FILE_SHARE_WRITE = 0x00000002;
        private const int OPEN_EXISTING = 3;
        private const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const int FILE_NAME_NORMALIZED = 0x0;

        private readonly object _disposeLock = new object();
        private readonly CancellationTokenSource _readCancellation = new CancellationTokenSource();
        private readonly SafeFileHandle _inputWrite;
        private readonly SafeFileHandle _outputRead;
        private readonly FileStream _inputWriter;
        private readonly FileStream _outputReader;
        private IntPtr _pseudoConsole;
        private bool _disposed;

        private ConPtyTerminal(
            IntPtr pseudoConsole,
            SafeFileHandle inputWrite,
            SafeFileHandle outputRead,
            Process process)
        {
            _pseudoConsole = pseudoConsole;
            _inputWrite = inputWrite;
            _outputRead = outputRead;
            _inputWriter = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);
            _outputReader = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);
            Process = process;
        }

        public Process Process { get; }
        public event Action<string>? OutputReceived;
        public event Action? Exited;

        public static ConPtyTerminal Start(TerminalShellProfile profile, string workingDirectory, short columns, short rows)
        {
            if (!profile.IsAvailable)
            {
                throw new FileNotFoundException($"{profile.DisplayName} 실행 파일을 찾을 수 없습니다.", profile.ExecutablePath);
            }

            if (!CreatePipe(out SafeFileHandle ptyInputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException($"Create input pipe failed: {Marshal.GetLastWin32Error()}");
            }

            if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle ptyOutputWrite, IntPtr.Zero, 0))
            {
                ptyInputRead.Dispose();
                inputWrite.Dispose();
                throw new InvalidOperationException($"Create output pipe failed: {Marshal.GetLastWin32Error()}");
            }

            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;
            IntPtr threadHandle = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;

            try
            {
                int hr = CreatePseudoConsole(new COORD { X = columns, Y = rows }, ptyInputRead, ptyOutputWrite, 0, out pseudoConsole);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
                }

                var startupInfo = new STARTUPINFOEX();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new InvalidOperationException("InitializeProcThreadAttributeList failed.");
                }

                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new InvalidOperationException("UpdateProcThreadAttribute failed.");
                }

                startupInfo.lpAttributeList = attributeList;
                string commandLineValue = profile.BuildCommandLine();
                var commandLine = new StringBuilder(commandLineValue);

                string? resolvedWorkingDirectory = null;
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(workingDirectory);
                        if (Directory.Exists(fullPath))
                        {
                            string? finalPath = ResolveFinalPath(fullPath);
                            resolvedWorkingDirectory = finalPath ?? fullPath;
                        }
                    }
                    catch
                    {
                        resolvedWorkingDirectory = null;
                    }
                }

                try
                {
                    environmentBlock = BuildEnvironmentBlock();
                }
                catch
                {
                    environmentBlock = IntPtr.Zero;
                }

                bool created = CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    environmentBlock,
                    resolvedWorkingDirectory,
                    ref startupInfo,
                    out PROCESS_INFORMATION processInfo);

                if (!created)
                {
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
                }

                processHandle = processInfo.hProcess;
                threadHandle = processInfo.hThread;
                var process = Process.GetProcessById(processInfo.dwProcessId);
                process.EnableRaisingEvents = true;

                var terminal = new ConPtyTerminal(pseudoConsole, inputWrite, outputRead, process);
                pseudoConsole = IntPtr.Zero;
                inputWrite = null!;
                outputRead = null!;
                process.Exited += (_, __) => terminal.Exited?.Invoke();
                terminal.StartReadLoop();
                return terminal;
            }
            finally
            {
                if (threadHandle != IntPtr.Zero)
                {
                    CloseHandle(threadHandle);
                }

                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }

                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                ptyInputRead?.Dispose();
                ptyOutputWrite?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }

                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }
            }
        }

        public async Task WriteAsync(string data)
        {
            if (_disposed || string.IsNullOrEmpty(data))
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(data);
            await _inputWriter.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await _inputWriter.FlushAsync().ConfigureAwait(false);
        }

        public void Resize(short columns, short rows)
        {
            if (_disposed || _pseudoConsole == IntPtr.Zero || columns < 2 || rows < 1)
            {
                return;
            }

            int hr = ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
            if (hr != 0 && hr != HPCON_INVALID)
            {
                Debug.WriteLine($"ResizePseudoConsole failed: 0x{hr:X8}");
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _readCancellation.Cancel();

                try { _inputWriter.Dispose(); } catch { }
                try { _outputReader.Dispose(); } catch { }
                try { _inputWrite.Dispose(); } catch { }
                try { _outputRead.Dispose(); } catch { }

                if (_pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(_pseudoConsole);
                    _pseudoConsole = IntPtr.Zero;
                }

                try
                {
                    if (!Process.HasExited)
                    {
                        Process.Kill(entireProcessTree: true);
                        Process.WaitForExit(500);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to terminate ConPTY process: {ex.Message}");
                }

                try { Process.Dispose(); } catch { }
                _readCancellation.Dispose();
            }
        }

        private void StartReadLoop()
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                while (!_readCancellation.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await _outputReader.ReadAsync(buffer, 0, buffer.Length, _readCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ConPTY read failed: {ex.Message}");
                        break;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, read);
                    OutputReceived?.Invoke(text);
                }
            });
        }

        private static string? ResolveFinalPath(string path)
        {
            try
            {
                using SafeFileHandle handle = CreateFile(
                    path,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    return null;
                }

                var sb = new StringBuilder(32768);
                int len = GetFinalPathNameByHandle(handle, sb, sb.Capacity, FILE_NAME_NORMALIZED);
                if (len > 0 && len < sb.Capacity)
                {
                    string result = sb.ToString(0, len);
                    if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        result = result.Substring(4);
                    }
                    return result;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string CleanPathEnvironment(string pathValue)
        {
            string[] entries = pathValue.Split(Path.PathSeparator);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i].Trim();
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                try
                {
                    var info = new DirectoryInfo(entry);
                    if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        string? finalPath = ResolveFinalPath(entry);
                        if (!string.IsNullOrEmpty(finalPath) && Directory.Exists(finalPath))
                        {
                            entries[i] = finalPath;
                        }
                    }
                }
                catch
                {
                }
            }

            return string.Join(Path.PathSeparator.ToString(), entries);
        }

        private static IntPtr BuildEnvironmentBlock()
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                if (de.Key is string key && de.Value is string value)
                {
                    if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                    {
                        value = CleanPathEnvironment(value);
                    }
                    variables[key] = value;
                }
            }

            int sizeBytes = 0;
            foreach (KeyValuePair<string, string> kvp in variables)
            {
                sizeBytes += (kvp.Key.Length + 1 + kvp.Value.Length + 1) * 2;
            }
            sizeBytes += 2;

            IntPtr block = Marshal.AllocHGlobal(sizeBytes);
            int offset = 0;
            foreach (KeyValuePair<string, string> kvp in variables)
            {
                string entry = kvp.Key + "=" + kvp.Value + "\0";
                byte[] bytes = Encoding.Unicode.GetBytes(entry);
                Marshal.Copy(bytes, 0, block + offset, bytes.Length);
                offset += bytes.Length;
            }

            byte[] terminator = new byte[] { 0, 0 };
            Marshal.Copy(terminator, 0, block + offset, 2);

            return block;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            int dwDesiredAccess,
            int dwShareMode,
            IntPtr lpSecurityAttributes,
            int dwCreationDisposition,
            int dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            StringBuilder lpszFilePath,
            int cchFilePath,
            int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
