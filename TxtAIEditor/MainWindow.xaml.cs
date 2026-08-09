using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Composition;
using TxtAIEditor.Controls;
using TxtAIEditor.ViewModels;


namespace TxtAIEditor
{
    public sealed partial class MainWindow : Window
    {
        private readonly ILocalizationService _localizationService;
        private readonly MainWindowControllers? _controllers;
        private readonly MainWindowRuntimeOperations? _operations;
        private MainWindowControllers Controllers =>
            _controllers ?? throw new InvalidOperationException("MainWindow controllers have not been composed.");
        private MainWindowRuntimeOperations Operations =>
            _operations ?? throw new InvalidOperationException("MainWindow runtime operations have not been composed.");
        private readonly MainWindowViewModel _viewModel = new MainWindowViewModel();
        private readonly MainWindowState _state = new MainWindowState();
        private TrayIconService? _trayIconService;
        private bool _exitRequestedFromTray;
        private bool _hideToTrayPending;

        public bool ScrollSyncEnabled
        {
            get => Operations.ScrollSyncEnabled;
            set => Operations.ScrollSyncEnabled = value;
        }

        private const int InitialEditorLineWarmupCount = 120;

        private TabView EditorTabView => EditorWorkspace.EditorTabViewControl;
        private TabView EditorTabView2 => EditorWorkspace.EditorTabView2Control;
        private TerminalPane TerminalPane => EditorWorkspace.TerminalPaneControl;

        private MainWindowUiRefs CreateUiRefs()
        {
            return new MainWindowUiRefs(
                RootGrid,
                AppTitleBar,
                TitleBarRow,
                AppTitleTextBlock,
                TopToolbar,
                MarkdownToolbarHost,
                MarkdownToolbar,
                MainWorkGrid,
                ExplorerColumn,
                PreviewColumn,
                LeftSplitter,
                RightSplitter,
                LeftSidebarTabView,
                EditorWorkspace,
                PreviewGrid,
                StatusBarPane,
                DragOverlay,
                EditorTabView,
                EditorTabView2,
                TerminalPane,
                Content as FrameworkElement ?? RootGrid);
        }

        public MainWindow()
        {
            this.InitializeComponent();
            WindowPlacementService.SetWindowIcon(AppWindow);

            // Start pre-warming the shared WebView2 environment in the background
            _ = TxtAIEditor.Editor.WebViewEnvironmentProvider.GetSharedAsync();

            var ui = CreateUiRefs();
            var services = MainWindowServices.Create(GetLocalizedString);
            _localizationService = services.Common.LocalizationService;
            _operations = new MainWindowRuntimeOperations(
                this,
                ui,
                services.Common,
                services.Editor,
                _viewModel,
                _state,
                () => Controllers);

            _controllers = MainWindowCompositionRoot.Compose(
                this,
                ui,
                services,
                _viewModel,
                _state,
                InitialEditorLineWarmupCount,
                Operations.CreateHostFacades());

            // Load local configurations and boot initial states
            // Setup custom title bar
            Controllers.Lifecycle.Window.InitializeTitleBar();

            this.Activated += OnWindowActivated;
            this.Activated += Controllers.Lifecycle.Window.HandleActivationChanged;
            this.Closed += Controllers.Lifecycle.Window.HandleWindowClosed;
            this.Closed += (_, _) => _trayIconService?.Dispose();
            this.AppWindow.Closing += OnAppWindowClosing;
            Controllers.Lifecycle.Window.StartShortcuts();

        }

        public Task PrepareForInitialActivationAsync() => Operations.PrepareForInitialActivationAsync();

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= OnWindowActivated;
            AppBadgeNotificationService.Initialize(WinRT.Interop.WindowNative.GetWindowHandle(this));
            await Operations.InitializeStartupAsync();
        }

        private string GetLocalizedString(string key, string fallback)
        {
            return _localizationService.GetString(key, fallback);
        }

        internal Task LoadFileIntoTabAsync(string filePath) => Operations.LoadFileIntoTabAsync(filePath);

        internal Task LoadFileIntoTabAsync(string filePath, int lineNumber) => Operations.LoadFileIntoTabAsync(filePath, lineNumber);

        internal Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath) => Operations.LoadFileIntoTabForAgentAsync(filePath);

        internal Task OpenShellPathAsync(string path) => Operations.OpenShellPathAsync(path);

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (!_exitRequestedFromTray &&
                Operations.CurrentSettings.KeepInTrayOnClose)
            {
                args.Cancel = true;
                if (!_hideToTrayPending && EnsureTrayIcon())
                {
                    _hideToTrayPending = true;
                    if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            AppWindow.Hide();
                        }
                        finally
                        {
                            _hideToTrayPending = false;
                        }
                    }))
                    {
                        _hideToTrayPending = false;
                    }
                }

                return;
            }

            _ = CompleteWindowCloseAsync(args);
        }

        private async Task CompleteWindowCloseAsync(Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            await Operations.HandleAppWindowClosingAsync(args);
            _exitRequestedFromTray = false;
        }

        private bool EnsureTrayIcon()
        {
            try
            {
                _trayIconService ??= new TrayIconService(
                    WinRT.Interop.WindowNative.GetWindowHandle(this),
                    "TxtAIEditor",
                    GetLocalizedString("TrayMenuOpen", "열기"),
                    GetLocalizedString("TrayMenuClose", "닫기"),
                    RestoreAndActivate,
                    CloseFromTray);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create tray icon: {ex.Message}");
                return false;
            }
        }

        internal void RestoreAndActivate()
        {
            AppWindow.Show();
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();
        }

        private void CloseFromTray()
        {
            _exitRequestedFromTray = true;
            RestoreAndActivate();
            Close();
        }

    }

}
