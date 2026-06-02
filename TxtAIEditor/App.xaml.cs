using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TxtAIEditor.Core.Services;
using Windows.ApplicationModel.Activation;

namespace TxtAIEditor
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "TxtAIEditorSingleInstanceMutex";
        private static readonly string IpcDir = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "IPC");
        private Window? _window;
        private static Mutex? _singleInstanceMutex;
        private FileSystemWatcher? _ipcWatcher;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public App()
        {
            ApplyLanguageSettings();
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Kill any windowless background TxtAIEditor processes from previous runs
            try
            {
                var currentProc = System.Diagnostics.Process.GetCurrentProcess();
                var existingProcs = System.Diagnostics.Process.GetProcessesByName("TxtAIEditor");
                foreach (var p in existingProcs)
                {
                    if (p.Id != currentProc.Id)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero)
                        {
                            try
                            {
                                // If it has no window and has been running for more than 2 seconds, it's a zombie
                                if ((DateTime.Now - p.StartTime).TotalSeconds > 2)
                                {
                                    p.Kill();
                                    p.WaitForExit(1000);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

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

            FileAssociationService.RegisterUnpackagedFileAssociations();
            StartIpcWatcher();

            _window = new MainWindow();
            _window.Closed += (_, _) => CleanupAppResources();
            _window.Activate();
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

            // Force terminate the process to ensure no background processes are left running
            Environment.Exit(0);
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
                                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                {
                                    await mainWindow.LoadFileIntoTabAsync(path);
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
