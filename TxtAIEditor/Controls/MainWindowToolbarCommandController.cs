using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class MainWindowToolbarCommandController
    {
        private readonly Window _ownerWindow;
        private readonly TopCommandBarPane _topToolbar;
        private readonly TabView _editorTabView;
        private readonly TextBox _searchQueryInput;
        private readonly MainWindowViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly FileOpenDropController _fileOpenDropController;
        private readonly TabNavigationController _tabNavigationController;
        private readonly TabSaveController _tabSaveController;
        private readonly TerminalPanelController _terminalPanelController;
        private readonly MainWindowSettingsController _settingsController;
        private readonly StickyNoteModeController _stickyNoteModeController;
        private readonly PdfViewerController _pdfViewerController;
        private readonly OfficeDocumentViewerController _officeDocumentViewerController;
        private readonly ShellPaneController _shellPaneController;
        private readonly CompareSelectionDialogService _compareSelectionDialogService;
        private readonly CompareTabController _compareTabController;
        private readonly WindowDialogController _dialogController;
        private readonly IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly IDictionary<string, EditorDocumentSession> _editorSessions;
        private readonly Func<XamlRoot> _getXamlRoot;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly Func<string, string, string> _getLocalizedString;
        private readonly Func<OpenedTab, string> _getPreviewBaseHref;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;

        public MainWindowToolbarCommandController(
            Window ownerWindow,
            TopCommandBarPane topToolbar,
            TabView editorTabView,
            TextBox searchQueryInput,
            MainWindowViewModel viewModel,
            ISettingsService settingsService,
            FileOpenDropController fileOpenDropController,
            TabNavigationController tabNavigationController,
            TabSaveController tabSaveController,
            TerminalPanelController terminalPanelController,
            MainWindowSettingsController settingsController,
            StickyNoteModeController stickyNoteModeController,
            PdfViewerController pdfViewerController,
            OfficeDocumentViewerController officeDocumentViewerController,
            ShellPaneController shellPaneController,
            CompareSelectionDialogService compareSelectionDialogService,
            CompareTabController compareTabController,
            WindowDialogController dialogController,
            IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            IDictionary<string, EditorDocumentSession> editorSessions,
            Func<XamlRoot> getXamlRoot,
            Func<ElementTheme> getCurrentElementTheme,
            Func<string, string, string> getLocalizedString,
            Func<OpenedTab, string> getPreviewBaseHref,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal)
        {
            _ownerWindow = ownerWindow;
            _topToolbar = topToolbar;
            _editorTabView = editorTabView;
            _searchQueryInput = searchQueryInput;
            _viewModel = viewModel;
            _settingsService = settingsService;
            _fileOpenDropController = fileOpenDropController;
            _tabNavigationController = tabNavigationController;
            _tabSaveController = tabSaveController;
            _terminalPanelController = terminalPanelController;
            _settingsController = settingsController;
            _stickyNoteModeController = stickyNoteModeController;
            _pdfViewerController = pdfViewerController;
            _officeDocumentViewerController = officeDocumentViewerController;
            _shellPaneController = shellPaneController;
            _compareSelectionDialogService = compareSelectionDialogService;
            _compareTabController = compareTabController;
            _dialogController = dialogController;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _getXamlRoot = getXamlRoot;
            _getCurrentElementTheme = getCurrentElementTheme;
            _getLocalizedString = getLocalizedString;
            _getPreviewBaseHref = getPreviewBaseHref;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;

            WireEvents();
        }

        public bool LivePreviewEnabled { get; private set; }

        public async void OpenFile() => await _fileOpenDropController.OpenFileAsync();

        public async void SaveActive() => await SaveActiveAsync();

        public async void SaveActiveAs() => await SaveActiveAsAsync();

        public async void Find() => await FindAsync();

        public async void ToggleTheme() => await _settingsController.ToggleThemeAsync();

        public async void ShowSettings() => await _settingsController.ShowSettingsAsync();

        public async void ShowModelSettings() => await _settingsController.ShowSettingsAsync("LLM");

        public void ToggleTerminal() => _terminalPanelController.Toggle();

        public void ToggleLivePreview()
        {
            _topToolbar.LivePreviewIsChecked = !_topToolbar.LivePreviewIsChecked;
            _ = ApplyLivePreviewToggleAsync();
        }


        public void ToggleWordWrap()
        {
            _topToolbar.WordWrapIsChecked = !_topToolbar.WordWrapIsChecked;
            _ = ToggleWordWrapAsync();
        }

        public void SyncCsvTableMode(OpenedTab tab)
        {
            _topToolbar.CsvTableIsChecked = tab.IsCsvTableModeEnabled;
        }

        public async Task SetCsvTableModeAsync(OpenedTab tab, bool enabled)
        {
            tab.IsCsvTableModeEnabled = enabled && !tab.IsHexViewer;

            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.SetCsvTableModeAsync(tab.IsCsvTableModeEnabled);
            }

            if (ReferenceEquals(_tabNavigationController.GetActiveTab(), tab))
            {
                SyncCsvTableMode(tab);
            }
        }

        public async void Print() => await PrintAsync();

        private void WireEvents()
        {
            _topToolbar.OpenFileClick += OnOpenFileClick;
            _topToolbar.SaveFileClick += OnSaveFileClick;
            _topToolbar.SaveAsFileClick += OnSaveAsFileClick;
            _topToolbar.CompareFilesClick += OnCompareFilesClick;
            _topToolbar.OpenTerminalClick += OnOpenTerminalClick;
            _topToolbar.PrintClick += OnPrintClick;
            _topToolbar.TopMostToggleClick += OnTopMostToggleClick;
            _topToolbar.StickyNoteClick += OnStickyNoteClick;
            _topToolbar.WordWrapToggleClick += OnWordWrapToggleClick;
            _topToolbar.FindClick += OnFindClick;
            _topToolbar.ToggleLivePreviewClick += OnToggleLivePreviewClick;
            _topToolbar.ToggleCsvTableClick += OnToggleCsvTableClick;
            _topToolbar.ToggleThemeClick += OnToggleThemeClick;
            _topToolbar.SettingsClick += OnSettingsClick;
        }

        private void OnOpenFileClick(object sender, RoutedEventArgs e) => OpenFile();

        private void OnSaveFileClick(object sender, RoutedEventArgs e) => SaveActive();

        private void OnSaveAsFileClick(object sender, RoutedEventArgs e) => SaveActiveAs();

        private async void OnCompareFilesClick(object sender, RoutedEventArgs e) => await CompareFilesAsync();

        private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => ToggleTerminal();

        private void OnPrintClick(object sender, RoutedEventArgs e) => Print();

        private void OnTopMostToggleClick(object sender, RoutedEventArgs e) => _stickyNoteModeController.ApplyTopMostFromToolbar();

        private void OnStickyNoteClick(object sender, RoutedEventArgs e) => _stickyNoteModeController.ToggleMode();

        private async void OnWordWrapToggleClick(object sender, RoutedEventArgs e) => await ToggleWordWrapAsync();

        private void OnFindClick(object sender, RoutedEventArgs e) => Find();

        private async void OnToggleLivePreviewClick(object sender, RoutedEventArgs e) => await ApplyLivePreviewToggleAsync();

        private async void OnToggleCsvTableClick(object sender, RoutedEventArgs e) => await ApplyCsvTableModeAsync();

        private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ToggleTheme();

        private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

        private async Task SaveActiveAsync()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab != null)
            {
                await _tabSaveController.SaveAsync(tab);
            }
        }

        private async Task SaveActiveAsAsync()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab != null)
            {
                await _tabSaveController.SaveAsAsync(tab);
            }
        }

        private async Task ToggleWordWrapAsync()
        {
            var settings = _settingsService.CurrentSettings;
            settings.WordWrap = _topToolbar.WordWrapIsChecked;
            await _settingsService.SaveSettingsAsync(settings);

            await _settingsController.ApplySettingsToOpenEditorsAsync(settings);
        }

        private async Task FindAsync()
        {
            if (await _pdfViewerController.FocusFindInActiveViewerAsync())
            {
                return;
            }

            if (await _officeDocumentViewerController.FocusFindInActiveViewerAsync())
            {
                return;
            }

            if (_editorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup) &&
                bridgeGroup.Bridge != null)
            {
                bridgeGroup.WebView.Focus(FocusState.Programmatic);
                await bridgeGroup.Bridge.TriggerFindAsync();
                return;
            }

            _shellPaneController.EnsureLeftPanelVisible();
            _shellPaneController.ShowLeftSidebarPage(3);
            _searchQueryInput.Focus(FocusState.Programmatic);
            _searchQueryInput.Focus(FocusState.Keyboard);
        }

        private async Task ApplyLivePreviewToggleAsync()
        {
            LivePreviewEnabled = _topToolbar.LivePreviewIsChecked;

            foreach (var tab in _viewModel.Tabs)
            {
                tab.InlineLivePreviewEnabled = LivePreviewEnabled;
                await ApplyInlineLivePreviewAsync(tab);
            }
        }

        private async Task ApplyInlineLivePreviewAsync(OpenedTab tab)
        {
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.SetInlineLivePreviewAsync(
                    tab.InlineLivePreviewEnabled,
                    _getPreviewBaseHref(tab));
            }
        }

        private async Task ApplyCsvTableModeAsync()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab != null)
            {
                await SetCsvTableModeAsync(tab, _topToolbar.CsvTableIsChecked);
            }
        }

        private async Task CompareFilesAsync()
        {
            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            var selection = await _compareSelectionDialogService.ShowAsync(
                _ownerWindow,
                _getXamlRoot(),
                _viewModel.Tabs,
                _getCurrentElementTheme(),
                _getLocalizedString);

            if (terminalWasVisible)
            {
                _resumeTerminal();
            }

            if (selection == null)
            {
                return;
            }

            if (selection.IsValid)
            {
                await _compareTabController.OpenCompareTabAsync(selection.PathA, selection.PathB, selection.ContentA, selection.ContentB);
            }
            else
            {
                _dialogController.ShowErrorMessage(
                    _getLocalizedString("CompareInvalidSelectionTitle", "비교 오류"),
                    _getLocalizedString("CompareInvalidSelectionMessage", "올바른 두 파일 혹은 탭을 선택해 주세요."));
            }
        }

        private async Task PrintAsync()
        {
            if (_editorTabView.SelectedItem is TabViewItem activeTabItem &&
                activeTabItem.Tag is string tabId &&
                _editorSessions.TryGetValue(tabId, out var session) &&
                _tabBridges.TryGetValue(tabId, out var bridgeGroup) &&
                bridgeGroup.WebView.CoreWebView2 != null)
            {
                string fullText = session.GetText();
                string jsonText = JsonSerializer.Serialize(fullText);
                await bridgeGroup.WebView.CoreWebView2.ExecuteScriptAsync(
                    $"printDocument({jsonText})");
            }
        }
    }
}
