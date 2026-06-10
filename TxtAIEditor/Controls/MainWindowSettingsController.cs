using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class MainWindowSettingsController
    {
        private readonly AppWindow _appWindow;
        private readonly Func<FrameworkElement?> _rootElementProvider;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly ISettingsService _settingsService;
        private readonly ISettingsDialogService _settingsDialogService;
        private readonly IUiPersonalizationService _uiPersonalizationService;
        private readonly ILocalizationService _localizationService;
        private readonly TopCommandBarPane _topToolbar;
        private readonly MarkdownToolbarControl _markdownToolbar;
        private readonly Grid _markdownToolbarHost;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TerminalPane _terminalPane;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly StatusBarPane _statusBarPane;
        private readonly RightSidebarPane _rightSidebar;
        private readonly StickyNoteBar _stickyNoteBar;
        private readonly CustomSplitter _leftSplitter;
        private readonly CustomSplitter _rightSplitter;
        private readonly IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly IDictionary<string, WebView2> _pdfViewerWebViews;
        private readonly StatusBarController _statusBarController;
        private readonly LivePreviewController _livePreviewController;
        private readonly LlmAssistantController _llmAssistantController;
        private readonly AgentController _agentController;
        private readonly Func<OpenedTab?> _getActiveTab;
        private readonly Func<string> _getCurrentFolderPath;
        private readonly Func<string, string, string> _getLocalizedString;
        private readonly Func<string, bool> _isGitNotDetectedText;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Action<bool> _applyPreviewVisibility;
        private readonly Action _updateAutoSaveStatus;
        private readonly Action _cleanupBeforeRestart;
        private readonly Action _refreshEditorWorkspaceSplitters;
        private readonly Action<object> _initializePickerWindow;

        public MainWindowSettingsController(
            AppWindow appWindow,
            Func<FrameworkElement?> rootElementProvider,
            Func<XamlRoot> xamlRootProvider,
            Func<ElementTheme> getCurrentElementTheme,
            ISettingsService settingsService,
            ISettingsDialogService settingsDialogService,
            IUiPersonalizationService uiPersonalizationService,
            ILocalizationService localizationService,
            TopCommandBarPane topToolbar,
            MarkdownToolbarControl markdownToolbar,
            Grid markdownToolbarHost,
            EditorWorkspacePane editorWorkspace,
            TerminalPane terminalPane,
            LeftSidebarPane leftSidebar,
            StatusBarPane statusBarPane,
            RightSidebarPane rightSidebar,
            StickyNoteBar stickyNoteBar,
            CustomSplitter leftSplitter,
            CustomSplitter rightSplitter,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            IDictionary<string, WebView2> pdfViewerWebViews,
            StatusBarController statusBarController,
            LivePreviewController livePreviewController,
            LlmAssistantController llmAssistantController,
            AgentController agentController,
            Func<OpenedTab?> getActiveTab,
            Func<string> getCurrentFolderPath,
            Func<string, string, string> getLocalizedString,
            Func<string, bool> isGitNotDetectedText,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Action<bool> applyPreviewVisibility,
            Action updateAutoSaveStatus,
            Action cleanupBeforeRestart,
            Action refreshEditorWorkspaceSplitters,
            Action<object> initializePickerWindow)
        {
            _appWindow = appWindow;
            _rootElementProvider = rootElementProvider;
            _xamlRootProvider = xamlRootProvider;
            _getCurrentElementTheme = getCurrentElementTheme;
            _settingsService = settingsService;
            _settingsDialogService = settingsDialogService;
            _uiPersonalizationService = uiPersonalizationService;
            _localizationService = localizationService;
            _topToolbar = topToolbar;
            _markdownToolbar = markdownToolbar;
            _markdownToolbarHost = markdownToolbarHost;
            _editorWorkspace = editorWorkspace;
            _terminalPane = terminalPane;
            _leftSidebar = leftSidebar;
            _statusBarPane = statusBarPane;
            _rightSidebar = rightSidebar;
            _stickyNoteBar = stickyNoteBar;
            _leftSplitter = leftSplitter;
            _rightSplitter = rightSplitter;
            _tabBridges = tabBridges;
            _pdfViewerWebViews = pdfViewerWebViews;
            _statusBarController = statusBarController;
            _livePreviewController = livePreviewController;
            _llmAssistantController = llmAssistantController;
            _agentController = agentController;
            _getActiveTab = getActiveTab;
            _getCurrentFolderPath = getCurrentFolderPath;
            _getLocalizedString = getLocalizedString;
            _isGitNotDetectedText = isGitNotDetectedText;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _applyPreviewVisibility = applyPreviewVisibility;
            _updateAutoSaveStatus = updateAutoSaveStatus;
            _cleanupBeforeRestart = cleanupBeforeRestart;
            _refreshEditorWorkspaceSplitters = refreshEditorWorkspaceSplitters;
            _initializePickerWindow = initializePickerWindow;
        }

        public async Task ToggleThemeAsync()
        {
            var settings = _settingsService.CurrentSettings;
            settings.Theme = settings.Theme == "Light" ? "Dark" : "Light";
            await _settingsService.SaveSettingsAsync(settings);

            ApplyUiPersonalization(settings);
            RefreshAllSplitters();
            _livePreviewController.ApplyPreferredColorScheme(settings.Theme);
            ApplyPreferredColorSchemeToOpenEditors(settings.Theme);
            await ApplySettingsToOpenEditorsAsync(settings);
            _livePreviewController.RenderActiveTab();
        }

        public async Task ShowSettingsAsync()
        {
            bool terminalWasSuspended = SuspendTerminalIfVisible();
            var settings = _settingsService.CurrentSettings;
            string oldLanguage = settings.Language;

            var result = await _settingsDialogService.ShowAsync(settings, _xamlRootProvider(), _getLocalizedString, _initializePickerWindow);
            ResumeTerminalIfNeeded(terminalWasSuspended);
            if (!result.Saved)
            {
                return;
            }

            await _settingsService.SaveSettingsAsync(settings);
            _llmAssistantController.UpdateModelDisplay();
            _agentController.UpdateModelDisplay(true);
            _agentController.UpdateContextStats();
            ApplyResourceLanguage();
            _applyPreviewVisibility(settings.DefaultMarkdownEnabled);
            _topToolbar.MarkdownToolbarIsChecked = settings.DefaultMarkdownToolbarEnabled;
            _markdownToolbar.Visibility = settings.DefaultMarkdownToolbarEnabled ? Visibility.Visible : Visibility.Collapsed;
            _updateAutoSaveStatus();
            _topToolbar.WordWrapIsChecked = settings.WordWrap;
            ApplyUiPersonalization(settings);
            _terminalPane.ApplySettings(settings);
            LocalizeUi();
            ApplyToolbarSettings(settings);

            if (oldLanguage != settings.Language && await ConfirmRestartForLanguageChangeAsync())
            {
                _cleanupBeforeRestart();
                Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                return;
            }

            await ApplySettingsToOpenEditorsAsync(settings);
            _livePreviewController.RenderActiveTab();
        }

        public async Task ApplySettingsToOpenEditorsAsync(EditorSettings settings)
        {
            foreach (var grp in _tabBridges.Values)
            {
                if (grp.Bridge != null)
                {
                    await grp.Bridge.UpdateOptionsAsync(settings);
                }
                else if (grp.WebView?.CoreWebView2 != null)
                {
                    grp.WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(CreateEditorOptionsMessage(settings)));
                }
            }
        }

        public void ApplyUiPersonalization(EditorSettings settings)
        {
            _uiPersonalizationService.Apply(
                settings,
                _appWindow,
                _rootElementProvider(),
                ApplyMarkdownToolbarBackground);
            ApplyEditorSurfaceBackground(settings);
        }

        public void ApplyToolbarSettings(EditorSettings settings)
        {
            _topToolbar.ApplySettings(settings, _getLocalizedString);
        }

        public void ApplyResourceLanguage()
        {
            _localizationService.ApplyResourceLanguage();
        }

        public void LocalizeUi()
        {
            try
            {
                ApplyResourceLanguage();

                _topToolbar.Localize(_getLocalizedString);
                _editorWorkspace.Localize(_getLocalizedString);
                _leftSidebar.Localize(_getLocalizedString, string.IsNullOrEmpty(_getCurrentFolderPath()), _isGitNotDetectedText);
                _statusBarPane.Localize(_getLocalizedString, _isGitNotDetectedText);
                _terminalPane.Localize(_getLocalizedString);
                _rightSidebar.Localize(_getLocalizedString);
                _rightSidebar.UpdateTranslateLanguage(_settingsService.CurrentSettings?.LlmTargetLanguage ?? "Korean");
                _markdownToolbar.LocalizeTooltips(_getLocalizedString);
                _stickyNoteBar.Localize(_getLocalizedString);

                var activeTab = _getActiveTab();
                if (activeTab != null)
                {
                    _statusBarController.UpdateTotalLines(activeTab);
                }

                _llmAssistantController.UpdateModelDisplay();
                _agentController.UpdateModelDisplay();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to localize UI: {ex.Message}");
            }
        }

        private async Task<bool> ConfirmRestartForLanguageChangeAsync()
        {
            bool terminalWasSuspended = SuspendTerminalIfVisible();
            var restartDialog = new ContentDialog
            {
                Title = _getLocalizedString("LanguageChangedTitle", "Language Change"),
                Content = _getLocalizedString("LanguageChangedMessage", "You must restart the application to apply the language settings. Would you like to restart now?"),
                PrimaryButtonText = _getLocalizedString("Restart", "Restart"),
                CloseButtonText = _getLocalizedString("No", "Later"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _getCurrentElementTheme()
            };

            bool result = await restartDialog.ShowAsync() == ContentDialogResult.Primary;
            ResumeTerminalIfNeeded(terminalWasSuspended);
            return result;
        }

        private void ApplyPreferredColorSchemeToOpenEditors(string theme)
        {
            foreach (var grp in _tabBridges.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(grp.WebView?.CoreWebView2, theme);
            }
            foreach (var pdfWebView in _pdfViewerWebViews.Values)
            {
                WebViewAppearanceService.ApplyPreferredColorScheme(pdfWebView?.CoreWebView2, theme);
            }
        }

        private void ApplyMarkdownToolbarBackground(Windows.UI.Color color)
        {
            var brush = new SolidColorBrush(color);
            _markdownToolbarHost.Background = brush;
            _markdownToolbar.SetToolbarBackground(color);
        }

        public void ApplyEditorSurfaceBackground(EditorSettings settings)
        {
            var editorBgColor = WebViewAppearanceService.ResolveEditorBackgroundColor(settings);
            _editorWorkspace.SetEditorSurfaceBackground(editorBgColor);

            foreach (var grp in _tabBridges.Values)
            {
                WebViewAppearanceService.ApplyEditorHostBackground(grp.WebView, editorBgColor);
            }
        }

        private void RefreshAllSplitters()
        {
            _leftSplitter.RefreshTheme();
            _rightSplitter.RefreshTheme();
            _refreshEditorWorkspaceSplitters();
        }

        private bool SuspendTerminalIfVisible()
        {
            if (!_isTerminalVisible())
            {
                return false;
            }

            _suspendTerminal();
            return true;
        }

        private void ResumeTerminalIfNeeded(bool terminalWasSuspended)
        {
            if (terminalWasSuspended)
            {
                _resumeTerminal();
            }
        }

        private static object CreateEditorOptionsMessage(EditorSettings settings)
        {
            return new
            {
                action = "updateOptions",
                theme = settings.Theme,
                wordWrap = settings.WordWrap,
                bracketPairColorization = settings.BracketPairColorization,
                fontSize = settings.FontSize,
                fontFamily = settings.FontFamily,
                tabSize = settings.TabSize,
                customBackgroundColor = settings.CustomBackgroundColor,
                customForegroundColor = settings.CustomForegroundColor,
                autocompleteOnEnter = settings.AutocompleteOnEnter,
                autocompleteOnTab = settings.AutocompleteOnTab,
                readOnly = true
            };
        }
    }
}
