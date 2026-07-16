using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;
using Windows.ApplicationModel.Activation;

namespace TxtAIEditor
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "TxtAIEditorSingleInstanceMutex";
        private static readonly string AppTempDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor");
        private static readonly string IpcDir = Path.Combine(AppTempDir, "IPC");
        private Window? _window;
        private static Mutex? _singleInstanceMutex;
        private FileSystemWatcher? _ipcWatcher;
        private uint _comCookie;
        private static bool _isComActivation;
        private static Timer? _exitTimer;
        private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private static int _comExitRequested;
        private static int _comExitCompleted;
        private static int _comActiveCallCount;
        private static int _comServerLockCount;
        private static int _comInvokeCompleted;
        private static long _comStartedTicks;
        private static long _lastActivityTicks;
        private static TxtAIEditorExplorerCommandFactory? _commandFactory;
        private const int ComIdleExitTimeoutMs = 60000;
        private const int ComMaxLifetimeMs = 300000;
        private const int ComExitFallbackDelayMs = 1500;
        private const int ComPostInvokeExitDelayMs = 3000;
        private const string ExplorerCommandClsid = "8D0B4C32-6D84-4B8A-8F3B-7E5408BEF1A1";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(ref Guid rclsid, IntPtr pUnk, uint dwClsContext, uint flags, out uint lpdwCookie);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint dwCookie);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const int SW_RESTORE = 9;

        public App()
        {
            AppDomain.CurrentDomain.SetData(
                "REGEX_DEFAULT_MATCH_TIMEOUT",
                TimeSpan.FromSeconds(2));
            ApplyLanguageSettings();
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            if (Environment.CommandLine.Contains("-Embedding", StringComparison.OrdinalIgnoreCase))
            {
                StartExplorerCommandServer();
            }
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_isComActivation)
            {
                return;
            }

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

            if (!createdNew)
            {
                CleanupWindowlessBackgroundProcesses();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            }

            if (!createdNew)
            {
                var cmdArgs = Environment.GetCommandLineArgs();
                try
                {
                    Directory.CreateDirectory(IpcDir);
                    var ipcFile = Path.Combine(IpcDir, $"ipc_{Guid.NewGuid():N}.txt");
                    if (cmdArgs.Length > 1)
                    {
                        File.WriteAllLines(ipcFile, cmdArgs.Skip(1));
                    }
                    else
                    {
                        File.WriteAllText(ipcFile, "ACTIVATE");
                    }
                }
                catch { }
                Environment.Exit(0);
                return;
            }

            StartIpcWatcher();

            var mainWindow = new MainWindow();
            _window = mainWindow;
            _window.Closed += (_, _) => CleanupAppResources();
            await mainWindow.PrepareForInitialActivationAsync();
            _window.Activate();

            _ = Task.Run(FileAssociationService.RegisterUnpackagedFileAssociations);
        }

        private static void CleanupWindowlessBackgroundProcesses()
        {
            try
            {
                var currentProc = System.Diagnostics.Process.GetCurrentProcess();
                var existingProcs = System.Diagnostics.Process.GetProcessesByName("TxtAIEditor");
                foreach (var p in existingProcs)
                {
                    if (p.Id == currentProc.Id || p.MainWindowHandle != IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if ((DateTime.Now - p.StartTime).TotalSeconds > 2)
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void StartExplorerCommandServer()
        {
            _isComActivation = true;
            _comStartedTicks = DateTime.UtcNow.Ticks;

            try
            {
                Guid clsid = Guid.Parse(ExplorerCommandClsid);
                _commandFactory = new TxtAIEditorExplorerCommandFactory();
                IntPtr factoryPtr = Marshal.GetIUnknownForObject(_commandFactory);
                try
                {
                    int hr = CoRegisterClassObject(ref clsid, factoryPtr, 4, 1, out _comCookie);
                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }
                finally
                {
                    Marshal.Release(factoryPtr);
                }

                _exitTimer = new Timer(_ => RequestComExit(0), null, ComIdleExitTimeoutMs, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Explorer command COM registration failed: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void RequestComExit(int exitCode)
        {
            if (Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            if (ShouldDeferComExit())
            {
                ScheduleComExitTimer();
                return;
            }

            if (Interlocked.Exchange(ref _comExitRequested, 1) != 0)
            {
                return;
            }

            bool queued = false;
            try
            {
                queued = _dispatcherQueue?.TryEnqueue(() => CompleteComExit(exitCode)) == true;
            }
            catch
            {
            }

            if (!queued)
            {
                CompleteComExit(exitCode);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(ComExitFallbackDelayMs);
                if (Volatile.Read(ref _comExitCompleted) == 0)
                {
                    CompleteComExit(exitCode);
                }
            });
        }

        private void CompleteComExit(int exitCode)
        {
            if (ShouldDeferComExit())
            {
                Interlocked.Exchange(ref _comExitRequested, 0);
                ScheduleComExitTimer();
                return;
            }

            if (Interlocked.Exchange(ref _comExitCompleted, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _exitTimer, null)?.Dispose();

            try
            {
                if (_comCookie != 0)
                {
                    CoRevokeClassObject(_comCookie);
                    _comCookie = 0;
                }
            }
            catch
            {
            }

            _commandFactory = null;
            TerminateCurrentProcess(exitCode);
        }

        private static void TerminateCurrentProcess(int exitCode)
        {
            try
            {
                TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
            }
            catch
            {
                Environment.Exit(exitCode);
            }
        }

        private static bool ShouldDeferComExit()
        {
            if (IsComMaxLifetimeExceeded())
            {
                return false;
            }

            if (Volatile.Read(ref _comActiveCallCount) > 0)
            {
                return true;
            }

            return Volatile.Read(ref _comInvokeCompleted) == 0 &&
                Volatile.Read(ref _comServerLockCount) > 0;
        }

        private static bool IsComMaxLifetimeExceeded()
        {
            long startedTicks = Volatile.Read(ref _comStartedTicks);
            if (startedTicks <= 0)
            {
                return false;
            }

            long elapsedTicks = DateTime.UtcNow.Ticks - startedTicks;
            return elapsedTicks >= TimeSpan.FromMilliseconds(ComMaxLifetimeMs).Ticks;
        }

        private static void ScheduleComExitTimer()
        {
            if (!_isComActivation || Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            try
            {
                _exitTimer?.Change(ComIdleExitTimeoutMs, Timeout.Infinite);
            }
            catch
            {
            }
        }

        public static void EnterComCall()
        {
            if (!_isComActivation)
            {
                return;
            }

            Interlocked.Increment(ref _comActiveCallCount);
            MarkComActivity();
        }

        public static void LeaveComCall()
        {
            if (!_isComActivation)
            {
                return;
            }

            if (Interlocked.Decrement(ref _comActiveCallCount) < 0)
            {
                Interlocked.Exchange(ref _comActiveCallCount, 0);
            }

            MarkComActivity();
        }

        public static void SetComServerLock(bool locked)
        {
            if (!_isComActivation)
            {
                return;
            }

            if (locked)
            {
                Interlocked.Increment(ref _comServerLockCount);
            }
            else if (Interlocked.Decrement(ref _comServerLockCount) < 0)
            {
                Interlocked.Exchange(ref _comServerLockCount, 0);
            }

            MarkComActivity();
        }

        public static void NotifyComInvokeCompleted()
        {
            if (!_isComActivation || Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _comInvokeCompleted, 1);

            try
            {
                _exitTimer?.Change(ComPostInvokeExitDelayMs, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private static void MarkComActivity()
        {
            if (!_isComActivation || Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (now - Volatile.Read(ref _lastActivityTicks) < TimeSpan.TicksPerSecond)
            {
                return;
            }

            Volatile.Write(ref _lastActivityTicks, now);

            try
            {
                _exitTimer?.Change(ComIdleExitTimeoutMs, Timeout.Infinite);
            }
            catch
            {
            }
        }

        public void CleanupAppResources()
        {
            if (_ipcWatcher != null)
            {
                _ipcWatcher.EnableRaisingEvents = false;
                _ipcWatcher.Dispose();
                _ipcWatcher = null;
            }

            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            DocumentTextExtractionService.KillRunningPdftotextProcesses();
            CleanupTemporaryFiles();

            // Force terminate the process to ensure no background processes are left running
            Environment.Exit(0);
        }

        private static void CleanupTemporaryFiles()
        {
            try
            {
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string appTempRoot = Path.GetFullPath(AppTempDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!appTempRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(appTempRoot), "TxtAIEditor", StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(appTempRoot))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(appTempRoot, "*", SearchOption.AllDirectories))
                {
                    TryDeleteFile(file);
                }

                foreach (string directory in Directory.EnumerateDirectories(appTempRoot, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length))
                {
                    TryDeleteDirectory(directory);
                }

                TryDeleteDirectory(appTempRoot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clean temporary files: {ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: false);
                }
            }
            catch
            {
            }
        }

        private void StartIpcWatcher()
        {
            try
            {
                Directory.CreateDirectory(IpcDir);
                _ipcWatcher = new FileSystemWatcher(IpcDir, "ipc_*.txt")
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _ipcWatcher.Created += OnIpcFileCreated;
            }
            catch { }
        }

        private void OnIpcFileCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Wait briefly for file write to complete
                Thread.Sleep(100);
                string[] lines = File.ReadAllLines(e.FullPath);
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            // Bring window to foreground
                            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                            ShowWindow(hWnd, SW_RESTORE);
                            SetForegroundWindow(hWnd);

                            foreach (var line in lines)
                            {
                                if (line == "ACTIVATE") continue;
                                string path = line.Trim().Trim('"', '\'');
                                if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                                {
                                    await mainWindow.OpenShellPathAsync(path);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to handle IPC file open: {ex.Message}");
                        }
                    });
                }
                try { File.Delete(e.FullPath); } catch { }
            }
            catch { }
        }

        private void ApplyLanguageSettings()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string settingsDir = System.IO.Path.Combine(userProfile, ".TxtAIEditor");
                string settingsFilePath = System.IO.Path.Combine(settingsDir, "settings.json");

                if (System.IO.File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("Language", out System.Text.Json.JsonElement langProp))
                        {
                            string lang = langProp.GetString() ?? "Default";
                            if (lang == "Default" || string.IsNullOrEmpty(lang))
                            {
                                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "";
                            }
                            else
                            {
                                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
                                
                                // Robustly sync .NET culture variables to enforce thread-level locale override
                                var culture = new System.Globalization.CultureInfo(lang);
                                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                                System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                                System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply language settings: {ex.Message}");
            }
        }
    }
}
