using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
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
        private readonly bool _isSecondaryWindow;
        private readonly TaskCompletionSource<bool> _startupCompletionSource =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _exitRequestedFromTray;
        private bool _hideToTrayPending;
        private bool _trayClosePending;

        internal bool IsHiddenToTray { get; private set; }

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

        public MainWindow(bool isSecondaryWindow = false)
        {
            _isSecondaryWindow = isSecondaryWindow;
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
            this.Closed += (_, _) => (Application.Current as App)?.HandleWindowClosed(this);
            this.AppWindow.Closing += OnAppWindowClosing;
            Controllers.Lifecycle.Window.StartShortcuts();

        }

        public Task PrepareForInitialActivationAsync() => Operations.PrepareForInitialActivationAsync();

        internal Task WaitForStartupAsync() => _startupCompletionSource.Task;

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= OnWindowActivated;
            AppBadgeNotificationService.Initialize(WinRT.Interop.WindowNative.GetWindowHandle(this));
            try
            {
                await Operations.InitializeStartupAsync(openStartupTargets: !_isSecondaryWindow);
            }
            finally
            {
                _startupCompletionSource.TrySetResult(true);
            }
        }

        internal string GetLocalizedString(string key, string fallback)
        {
            return _localizationService.GetString(key, fallback);
        }

        internal Task LoadFileIntoTabAsync(string filePath) => Operations.LoadFileIntoTabAsync(filePath);

        internal Task LoadFileIntoTabAsync(string filePath, int lineNumber) => Operations.LoadFileIntoTabAsync(filePath, lineNumber);

        internal Task<AgentOpenFileResult> LoadFileIntoTabForAgentAsync(string filePath) => Operations.LoadFileIntoTabForAgentAsync(filePath);

        internal Task OpenShellPathAsync(string path) => Operations.OpenShellPathAsync(path);

        internal Task NavigateExplorerToFolderAsync(string folderPath) =>
            Operations.NavigateExplorerToFolderAsync(folderPath, revealInLeftPanel: true);

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (!_exitRequestedFromTray &&
                Operations.CurrentSettings.KeepInTrayOnClose &&
                (Application.Current as App)?.IsLastWindow(this) == true)
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
                            IsHiddenToTray = true;
                            (Application.Current as App)?.UpdateTrayIconVisibility();
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
            bool isTrayExit = _exitRequestedFromTray;
            try
            {
                await Operations.HandleAppWindowClosingAsync(
                    args,
                    saveUiLayoutSettings: !isTrayExit);
            }
            finally
            {
                _exitRequestedFromTray = false;
            }
        }

        private bool EnsureTrayIcon()
        {
            return (Application.Current as App)?.EnsureTrayIcon(this) == true;
        }

        internal void RestoreAndActivate()
        {
            IsHiddenToTray = false;
            AppWindow.Show();
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();
            (Application.Current as App)?.UpdateTrayIconVisibility();
        }

        internal async Task RequestCloseFromTrayAsync()
        {
            if (_trayClosePending)
            {
                return;
            }

            _trayClosePending = true;
            Task saveLayoutTask = Operations.SaveUiLayoutSettingsAsync();
            RestoreAndActivate();
            try
            {
                await saveLayoutTask;
                _exitRequestedFromTray = true;
                await Operations.RequestWindowCloseAsync(saveUiLayoutSettings: false);
            }
            finally
            {
                _trayClosePending = false;
                _exitRequestedFromTray = false;
            }
        }

        internal Task<bool> PrepareTabForTransferAsync(OpenedTab tab) =>
            Operations.PrepareTabForTransferAsync(tab);

        internal bool TryDetachTabForTransfer(OpenedTab tab, out EditorTabTransfer? transfer) =>
            Operations.TryDetachTabForTransfer(tab, out transfer);

        internal void AdoptTransferredTab(EditorTabTransfer transfer) =>
            Operations.AdoptTransferredTab(transfer);

        internal Task OpenTabInNewWindowAsync(OpenedTab tab) =>
            (Application.Current as App)?.OpenTabInNewWindowAsync(this, tab) ?? Task.CompletedTask;

        internal Task OpenTabItemInNewWindowAsync(TabViewItem tabItem)
        {
            if (tabItem.Tag is not string tabId)
            {
                return Task.CompletedTask;
            }

            foreach (OpenedTab tab in _viewModel.Tabs)
            {
                if (string.Equals(tab.Id, tabId, StringComparison.Ordinal))
                {
                    return OpenTabInNewWindowAsync(tab);
                }
            }

            return Task.CompletedTask;
        }

        internal void EnsureAtLeastOneTab()
        {
            if (_viewModel.Tabs.Count == 0)
            {
                Operations.OpenEmptyTab();
            }
        }

        internal string GetWindowDisplayName()
        {
            return string.IsNullOrWhiteSpace(Title) ? "TxtAIEditor" : Title;
        }

    }

}
