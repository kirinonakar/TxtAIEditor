using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using Windows.ApplicationModel.Activation;

namespace TxtAIEditor
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "TxtAIEditorSingleInstanceMutex";
        private static readonly string AppTempDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor");
        private static readonly string IpcDir = Path.Combine(AppTempDir, "IPC");
        public static Window? MainWindow { get; private set; }
        private readonly List<MainWindow> _windows = new List<MainWindow>();
        private Window? _window;
        private TrayIconService? _trayIconService;
        private MainWindow? _trayOwnerWindow;
        private bool _trayExitInProgress;
        private static Mutex? _singleInstanceMutex;
        private FileSystemWatcher? _ipcWatcher;
        private uint _comCookie;
        private static bool _isComActivation;
        private static Timer? _idleExitTimer;
        private static Timer? _maxLifetimeExitTimer;
        private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private static int _comExitRequested;
        private static int _comExitCompleted;
        private static int _comForceExitRequested;
        private static int _comActiveCallCount;
        private static int _comServerLockCount;
        private static int _comInvokeCompleted;
        private int _appCleanupStarted;
        private static TxtAIEditorExplorerCommandFactory? _commandFactory;
        private const int ComIdleExitTimeoutMs = 15000;
        private const int ComMaxLifetimeMs = 60000;
        private const int ComActiveCallRetryDelayMs = 250;
        private const int ComExitFallbackDelayMs = 1500;
        private const int ComPostInvokeExitDelayMs = 3000;
        private const string ExplorerCommandClsid = "8D0B4C32-6D84-4B8A-8F3B-7E5408BEF1A1";

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(ref Guid rclsid, IntPtr pUnk, uint dwClsContext, uint flags, out uint lpdwCookie);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint dwCookie);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

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
            RegisterWindow(mainWindow);
            await mainWindow.PrepareForInitialActivationAsync();
            mainWindow.Activate();

            _ = Task.Run(FileAssociationService.RegisterUnpackagedFileAssociations);
        }

        private void RegisterWindow(MainWindow window)
        {
            if (_windows.Contains(window))
            {
                return;
            }

            _windows.Add(window);
            MainWindow ??= window;
            _window ??= window;
        }

        internal void HandleWindowClosed(MainWindow window)
        {
            _windows.Remove(window);
            if (ReferenceEquals(MainWindow, window))
            {
                MainWindow = _windows.FirstOrDefault();
            }

            if (ReferenceEquals(_window, window))
            {
                _window = _windows.FirstOrDefault();
            }

            if (ReferenceEquals(_trayOwnerWindow, window))
            {
                _trayIconService?.Dispose();
                _trayIconService = null;
                _trayOwnerWindow = null;
            }

            UpdateTrayIconVisibility();

            if (_windows.Count == 0)
            {
                CleanupAppResources();
            }
        }

        internal bool EnsureTrayIcon(MainWindow requestedOwner)
        {
            try
            {
                MainWindow? owner = _trayOwnerWindow != null && _windows.Contains(_trayOwnerWindow)
                    ? _trayOwnerWindow
                    : requestedOwner;
                if (!_windows.Contains(owner))
                {
                    owner = _windows.FirstOrDefault();
                }

                if (owner == null)
                {
                    return false;
                }

                if (_trayIconService != null && ReferenceEquals(_trayOwnerWindow, owner))
                {
                    return true;
                }

                _trayIconService?.Dispose();
                _trayOwnerWindow = owner;
                _trayIconService = new TrayIconService(
                    WinRT.Interop.WindowNative.GetWindowHandle(owner),
                    "TxtAIEditor",
                    owner.GetLocalizedString("TrayMenuOpen", "열기"),
                    owner.GetLocalizedString("TrayMenuClose", "닫기"),
                    GetTrayWindowItems,
                    RestoreAnyWindow,
                    () => _ = RequestCloseAllFromTrayAsync());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create tray icon: {ex.Message}");
                _trayIconService?.Dispose();
                _trayIconService = null;
                _trayOwnerWindow = null;
                return false;
            }
        }

        internal void UpdateTrayIconVisibility()
        {
            bool shouldShow = _windows.Count > 1 || _windows.Any(window => window.IsHiddenToTray);
            if (!shouldShow)
            {
                _trayIconService?.Dispose();
                _trayIconService = null;
                _trayOwnerWindow = null;
                return;
            }

            MainWindow? owner = _windows.FirstOrDefault();
            if (owner != null)
            {
                EnsureTrayIcon(owner);
            }
        }

        private IReadOnlyList<TrayWindowItem> GetTrayWindowItems()
        {
            var items = new List<TrayWindowItem>(_windows.Count);
            foreach (MainWindow window in _windows.ToArray())
            {
                MainWindow target = window;
                items.Add(new TrayWindowItem(
                    target.GetWindowDisplayName(),
                    () => target.DispatcherQueue.TryEnqueue(target.RestoreAndActivate)));
            }

            return items;
        }

        private void RestoreAnyWindow()
        {
            MainWindow? window = _windows.FirstOrDefault();
            window?.RestoreAndActivate();
        }

        private async Task RequestCloseAllFromTrayAsync()
        {
            if (_trayExitInProgress)
            {
                return;
            }

            _trayExitInProgress = true;
            try
            {
                foreach (MainWindow window in _windows.ToArray())
                {
                    await window.RequestCloseFromTrayAsync();
                }
            }
            finally
            {
                _trayExitInProgress = false;
            }
        }

        internal async Task OpenTabInNewWindowAsync(MainWindow sourceWindow, OpenedTab tab)
        {
            if (!_windows.Contains(sourceWindow) || !await sourceWindow.PrepareTabForTransferAsync(tab))
            {
                return;
            }

            MainWindow? targetWindow = await CreateSecondaryWindowAsync();
            if (targetWindow == null)
            {
                return;
            }

            if (!sourceWindow.TryDetachTabForTransfer(tab, out EditorTabTransfer? transfer) || transfer == null)
            {
                targetWindow.Close();
                return;
            }

            try
            {
                targetWindow.AdoptTransferredTab(transfer);
                string? workingFolderPath = GetTabWorkingFolder(tab);
                if (!string.IsNullOrWhiteSpace(workingFolderPath))
                {
                    try
                    {
                        await targetWindow.NavigateExplorerToFolderAsync(workingFolderPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to set new window working folder: {ex.Message}");
                    }
                }

                targetWindow.RestoreAndActivate();
                sourceWindow.EnsureAtLeastOneTab();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to move tab to a new window: {ex.Message}");
                try
                {
                    sourceWindow.AdoptTransferredTab(transfer);
                    sourceWindow.EnsureAtLeastOneTab();
                }
                catch (Exception restoreException)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to restore moved tab: {restoreException.Message}");
                }

                targetWindow.Close();
            }
        }

        private static string? GetTabWorkingFolder(OpenedTab tab)
        {
            if (RemotePath.IsRemote(tab.RemotePath))
            {
                return RemotePath.GetParent(tab.RemotePath!);
            }

            if (RemotePath.IsRemote(tab.FilePath))
            {
                return RemotePath.GetParent(tab.FilePath!);
            }

            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return null;
            }

            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(tab.FilePath));
                return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                    ? directory
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<MainWindow?> CreateSecondaryWindowAsync()
        {
            var window = new MainWindow(isSecondaryWindow: true);
            RegisterWindow(window);
            try
            {
                await window.PrepareForInitialActivationAsync();
                window.Activate();
                await window.WaitForStartupAsync();
                UpdateTrayIconVisibility();
                return window;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize secondary window: {ex.Message}");
                try
                {
                    window.Close();
                }
                catch
                {
                }

                return null;
            }
        }

        private static void CleanupWindowlessBackgroundProcesses(TimeSpan minimumAge)
        {
            try
            {
                int currentProcessId = Environment.ProcessId;
                var existingProcs = System.Diagnostics.Process.GetProcessesByName("TxtAIEditor");
                foreach (var p in existingProcs)
                {
                    try
                    {
                        if (p.Id == currentProcessId || p.MainWindowHandle != IntPtr.Zero)
                        {
                            continue;
                        }

                        if (DateTime.Now - p.StartTime >= minimumAge)
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        p.Dispose();
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

                _idleExitTimer = new Timer(
                    _ => RequestComExit(0, force: false),
                    null,
                    ComIdleExitTimeoutMs,
                    Timeout.Infinite);
                _maxLifetimeExitTimer = new Timer(
                    _ => RequestComExit(0, force: true),
                    null,
                    ComMaxLifetimeMs,
                    Timeout.Infinite);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Explorer command COM registration failed: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void RequestComExit(int exitCode, bool force)
        {
            if (Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            if (force)
            {
                Interlocked.Exchange(ref _comForceExitRequested, 1);
            }

            bool forceExit = Volatile.Read(ref _comForceExitRequested) != 0;
            if (ShouldDeferComExit(forceExit))
            {
                ScheduleComExitTimer(forceExit ? ComActiveCallRetryDelayMs : ComIdleExitTimeoutMs);
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
            bool forceExit = Volatile.Read(ref _comForceExitRequested) != 0;
            if (ShouldDeferComExit(forceExit))
            {
                Interlocked.Exchange(ref _comExitRequested, 0);
                ScheduleComExitTimer(forceExit ? ComActiveCallRetryDelayMs : ComIdleExitTimeoutMs);
                return;
            }

            if (Interlocked.Exchange(ref _comExitCompleted, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _idleExitTimer, null)?.Dispose();
            Interlocked.Exchange(ref _maxLifetimeExitTimer, null)?.Dispose();

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
                if (TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)))
                {
                    return;
                }
            }
            catch
            {
            }

            Environment.Exit(exitCode);
        }

        private static bool ShouldDeferComExit(bool force)
        {
            if (Volatile.Read(ref _comActiveCallCount) > 0)
            {
                return true;
            }

            return !force &&
                Volatile.Read(ref _comInvokeCompleted) == 0 &&
                Volatile.Read(ref _comServerLockCount) > 0;
        }

        private static void ScheduleComExitTimer(int delayMs)
        {
            if (!_isComActivation || Volatile.Read(ref _comExitCompleted) != 0)
            {
                return;
            }

            try
            {
                _idleExitTimer?.Change(delayMs, Timeout.Infinite);
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

            int remainingCalls = Interlocked.Decrement(ref _comActiveCallCount);
            if (remainingCalls < 0)
            {
                Interlocked.Exchange(ref _comActiveCallCount, 0);
                remainingCalls = 0;
            }

            if (remainingCalls == 0 && Volatile.Read(ref _comForceExitRequested) != 0)
            {
                ScheduleComExitTimer(ComActiveCallRetryDelayMs);
                return;
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
                int delayMs = Volatile.Read(ref _comForceExitRequested) != 0
                    ? ComActiveCallRetryDelayMs
                    : ComPostInvokeExitDelayMs;
                _idleExitTimer?.Change(delayMs, Timeout.Infinite);
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

            if (Volatile.Read(ref _comForceExitRequested) != 0)
            {
                return;
            }

            try
            {
                _idleExitTimer?.Change(ComIdleExitTimeoutMs, Timeout.Infinite);
            }
            catch
            {
            }
        }

        public void CleanupAppResources()
        {
            if (Interlocked.Exchange(ref _appCleanupStarted, 1) != 0)
            {
                return;
            }

            _trayIconService?.Dispose();
            _trayIconService = null;
            _trayOwnerWindow = null;

            if (_ipcWatcher != null)
            {
                _ipcWatcher.EnableRaisingEvents = false;
                _ipcWatcher.Created -= OnIpcFileCreated;
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
            CleanupWindowlessBackgroundProcesses(TimeSpan.Zero);

            // The packaged Explorer command runs as a separate windowless -Embedding process.
            // Stop it together with the main instance so RuntimeBroker can release the COM activation.
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
                            mainWindow.RestoreAndActivate();

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
