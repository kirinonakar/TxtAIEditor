using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;
using Windows.Storage.Pickers;

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
        private readonly ITabSaveCommands _tabSaveCommands;
        private readonly TerminalPanelController _terminalPanelController;
        private readonly MainWindowSettingsController _settingsController;
        private readonly StickyNoteModeController _stickyNoteModeController;
        private readonly PdfViewerController _pdfViewerController;
        private readonly OfficeDocumentViewerController _officeDocumentViewerController;
        private readonly JupyterNotebookViewerController _notebookViewerController;
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
        private readonly Func<bool>? _printLivePreview;
        private readonly LivePreviewController? _pdfPreview;
        private bool _savingPdf;
        private bool _printingViewer;

        public MainWindowToolbarCommandController(
            Window ownerWindow,
            TopCommandBarPane topToolbar,
            TabView editorTabView,
            TextBox searchQueryInput,
            MainWindowViewModel viewModel,
            ISettingsService settingsService,
            FileOpenDropController fileOpenDropController,
            TabNavigationController tabNavigationController,
            ITabSaveCommands tabSaveCommands,
            TerminalPanelController terminalPanelController,
            MainWindowSettingsController settingsController,
            StickyNoteModeController stickyNoteModeController,
            PdfViewerController pdfViewerController,
            OfficeDocumentViewerController officeDocumentViewerController,
            JupyterNotebookViewerController notebookViewerController,
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
            Action resumeTerminal,
            Func<bool>? printLivePreview = null,
            LivePreviewController? pdfPreview = null)
        {
            _ownerWindow = ownerWindow;
            _topToolbar = topToolbar;
            _editorTabView = editorTabView;
            _searchQueryInput = searchQueryInput;
            _viewModel = viewModel;
            _settingsService = settingsService;
            _fileOpenDropController = fileOpenDropController;
            _tabNavigationController = tabNavigationController;
            _tabSaveCommands = tabSaveCommands;
            _terminalPanelController = terminalPanelController;
            _settingsController = settingsController;
            _stickyNoteModeController = stickyNoteModeController;
            _pdfViewerController = pdfViewerController;
            _officeDocumentViewerController = officeDocumentViewerController;
            _notebookViewerController = notebookViewerController;
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
            _printLivePreview = printLivePreview;
            _pdfPreview = pdfPreview;

            WireEvents();
        }

        public bool LivePreviewEnabled { get; private set; }

        public void OpenFile() => RunCommand(_fileOpenDropController.OpenFileAsync);

        public void SaveActive() => RunCommand(SaveActiveAsync);

        public void SaveActiveAs() => RunCommand(SaveActiveAsAsync);

        public void Find() => RunCommand(FindAsync);

        public void ToggleTheme() => RunCommand(_settingsController.ToggleThemeAsync);

        public void ShowSettings() => RunCommand(() => _settingsController.ShowSettingsAsync());

        public void ShowModelSettings() => RunCommand(() => _settingsController.ShowSettingsAsync("LLM"));

        private void RunCommand(Func<Task> command) => _ = AppErrorHandler.RunAsync(command, ex =>
            _dialogController.ShowErrorMessage(_getLocalizedString("UnexpectedErrorTitle", "Unexpected error"), ex.Message));

        public void ToggleTerminal() => _terminalPanelController.Toggle();

        public void ToggleLivePreview()
        {
            _topToolbar.LivePreviewIsChecked = !_topToolbar.LivePreviewIsChecked;
            RunCommand(ApplyLivePreviewToggleAsync);
        }

        public void EnableLivePreview()
        {
            if (LivePreviewEnabled && _topToolbar.LivePreviewIsChecked)
            {
                return;
            }

            _topToolbar.LivePreviewIsChecked = true;
            RunCommand(ApplyLivePreviewToggleAsync);
        }


        public void ToggleWordWrap()
        {
            _topToolbar.WordWrapIsChecked = !_topToolbar.WordWrapIsChecked;
            RunCommand(ToggleWordWrapAsync);
        }

        public void ToggleCsvTableMode()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab == null ||
                tab.IsImageViewer ||
                tab.IsMediaViewer ||
                tab.IsPdfViewer ||
                tab.IsDocxViewer ||
                tab.IsOfficeDocumentViewer ||
                tab.IsHexViewer)
            {
                return;
            }

            RunCommand(() => SetCsvTableModeAsync(tab, !tab.IsCsvTableModeEnabled));
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

        public void Print() => _ = AppErrorHandler.RunAsync(PrintAsync, ex =>
            _dialogController.ShowErrorMessage(_getLocalizedString("PrintErrorTitle", "인쇄 오류"), ex.Message));

        private void WireEvents()
        {
            _topToolbar.OpenFileClick += OnOpenFileClick;
            _topToolbar.SaveFileClick += OnSaveFileClick;
            _topToolbar.SaveAsFileClick += OnSaveAsFileClick;
            _topToolbar.SavePdfClick += OnSavePdfClick;
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

        private void OnSavePdfClick(object sender, RoutedEventArgs e) => RunCommand(SavePdfAsync);

        private void OnCompareFilesClick(object sender, RoutedEventArgs e) => RunCommand(CompareFilesAsync);

        private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => ToggleTerminal();

        private void OnPrintClick(object sender, RoutedEventArgs e) => Print();

        private void OnTopMostToggleClick(object sender, RoutedEventArgs e) => _stickyNoteModeController.ApplyTopMostFromToolbar();

        private void OnStickyNoteClick(object sender, RoutedEventArgs e) => _stickyNoteModeController.ToggleMode();

        private void OnWordWrapToggleClick(object sender, RoutedEventArgs e) => RunCommand(ToggleWordWrapAsync);

        private void OnFindClick(object sender, RoutedEventArgs e) => Find();

        private void OnToggleLivePreviewClick(object sender, RoutedEventArgs e) => RunCommand(ApplyLivePreviewToggleAsync);

        private void OnToggleCsvTableClick(object sender, RoutedEventArgs e) => RunCommand(ApplyCsvTableModeAsync);

        private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ToggleTheme();

        private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

        public async Task SaveActiveAsync()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab != null)
            {
                await _tabSaveCommands.SaveAsync(tab);
            }
        }

        public async Task SaveActiveAsAsync()
        {
            var tab = _tabNavigationController.GetActiveTab();
            if (tab != null)
            {
                await _tabSaveCommands.SaveAsAsync(tab);
            }
        }

        private async Task SavePdfAsync()
        {
            if (_savingPdf) return;
            var tab = _tabNavigationController.GetActiveTab();
            if (tab == null) return;

            _savingPdf = true;
            try
            {
                bool officeDocument = tab.IsOfficeDocumentViewer;
                _editorSessions.TryGetValue(tab.Id, out var session);
                if (_ownerWindow.Content is not Panel host ||
                    (!officeDocument && ((tab.IsReadOnlyViewer && !tab.IsReadOnlyTextFile) || session == null)))
                {
                    throw new InvalidOperationException(_getLocalizedString("SavePdfUnsupported", "이 탭은 PDF로 저장할 수 없습니다. 텍스트 문서를 열고 다시 시도해 주세요."));
                }

                string mode = _pdfPreview?.GetPdfExportMode(tab) ?? "text";
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(tab.Title),
                    DefaultFileExtension = ".pdf"
                };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow));
                picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;

                if (officeDocument)
                {
                    await ExportOfficePdfAsync(host, tab, file.Path);
                    return;
                }

                var settings = _settingsService.CurrentSettings;
                await PdfExportController.ExportAsync(host, file.Path, new
                {
                    mode,
                    text = session!.GetText(),
                    baseHref = _getPreviewBaseHref(tab),
                    tabSize = settings.TabSize,
                    fontFamily = mode == "text" ? settings.FontFamily :
                        mode == "aozora" ? settings.AozoraPreviewFontFamily : settings.PreviewFontFamily,
                    fontSize = mode == "text" ? settings.FontSize :
                        mode == "aozora" ? settings.AozoraPreviewFontSize : settings.PreviewFontSize,
                    usePele = settings.UsePele,
                    csvEmptyMessage = _getLocalizedString("PreviewCsvEmpty", "빈 CSV 파일입니다.")
                }, mode == "html", core => _pdfPreview?.RegisterDocumentResourceAccess(core));
            }
            catch (Exception ex)
            {
                _dialogController.ShowErrorMessage(
                    _getLocalizedString("SavePdfErrorTitle", "PDF 저장 오류"), ex.Message);
            }
            finally
            {
                _savingPdf = false;
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

            if (await _notebookViewerController.FocusFindInActiveViewerAsync())
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
            if (_printingViewer)
            {
                return;
            }

            var activeTab = _tabNavigationController.GetActiveTab();
            if (activeTab != null && (activeTab.IsImageViewer || activeTab.IsPdfViewer || activeTab.IsOfficeDocumentViewer))
            {
                _printingViewer = true;
                _topToolbar.SetPrintBusy(true, _getLocalizedString);
                try
                {
                    await PrintViewerAsync(activeTab);
                }
                finally
                {
                    _printingViewer = false;
                    _topToolbar.SetPrintBusy(false, _getLocalizedString);
                }
                return;
            }

            if (activeTab?.InlineLivePreviewEnabled != true &&
                _printLivePreview != null && _printLivePreview())
            {
                return;
            }

            if (activeTab != null &&
                _editorSessions.TryGetValue(activeTab.Id, out var session) &&
                _tabBridges.TryGetValue(activeTab.Id, out var bridgeGroup) &&
                bridgeGroup.WebView.CoreWebView2 != null)
            {
                string fullText = session.GetText();
                string jsonText = JsonSerializer.Serialize(fullText);
                await bridgeGroup.WebView.CoreWebView2.ExecuteScriptAsync(
                    $"printDocument({jsonText})");
            }
        }

        private async Task PrintViewerAsync(OpenedTab tab)
        {
            if (tab.IsOfficeDocumentViewer && _ownerWindow.Content is Panel host)
            {
                await PrintOfficeAsync(host, tab);
                return;
            }

            if (tab.IsPdfViewer && tab.FilePath is { Length: > 0 } pdfPath)
            {
                await ShowPdfPrintDialogAsync(pdfPath, tab.Title);
                return;
            }

            if (tab.IsImageViewer &&
                _tabNavigationController.GetCurrentActiveTabView().SelectedItem is TabViewItem item &&
                await EditorTabViewItemFactory.TryShowImagePrintPreviewAsync(item))
            {
                return;
            }

            throw new InvalidOperationException(_getLocalizedString("PrintDocumentUnavailable",
                "문서를 인쇄할 준비가 되지 않았습니다. 문서를 확인한 뒤 다시 시도해 주세요."));
        }

        private async Task PrintOfficeAsync(Panel host, OpenedTab tab)
        {
            var directory = Directory.CreateTempSubdirectory("TxtAIEditor-Print-");
            string path = Path.Combine(directory.FullName, "document.pdf");
            try
            {
                await ExportOfficePdfAsync(host, tab, path);
                await ShowPdfPrintDialogAsync(path, tab.Title);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                    directory.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppErrorHandler.Report(ex);
                }
            }
        }

        private Task ShowPdfPrintDialogAsync(string path, string title) =>
            PdfPrintController.ShowAsync(_ownerWindow, path, title,
                _getLocalizedString("PrintJobFailed", "인쇄를 완료하지 못했습니다. 프린터 상태를 확인하고 다시 시도해 주세요."));

        private async Task ExportOfficePdfAsync(Panel host, OpenedTab tab, string path)
        {
            string html = await _officeDocumentViewerController.BuildPdfHtmlAsync(tab);
            await PdfExportController.ExportOfficeAsync(host, path, html);
        }
    }
}
