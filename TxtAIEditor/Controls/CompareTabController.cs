using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class CompareTabController
    {
        private readonly IFileService _fileService;
        private readonly ISettingsService _settingsService;
        private readonly MainWindowViewModel _viewModel;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TabView _editorTabView;
        private readonly IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Func<string, string, string> _getString;
        private readonly Func<CoreWebView2WebMessageReceivedEventArgs, string> _normalizeWebMessageJson;
        private readonly Action<string> _shortcutHandler;

        public CompareTabController(
            IFileService fileService,
            ISettingsService settingsService,
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            TabView editorTabView,
            IDictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Func<string, string, string> getString,
            Func<CoreWebView2WebMessageReceivedEventArgs, string> normalizeWebMessageJson,
            Action<string> shortcutHandler)
        {
            _fileService = fileService;
            _settingsService = settingsService;
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _editorTabView = editorTabView;
            _tabBridges = tabBridges;
            _getString = getString;
            _normalizeWebMessageJson = normalizeWebMessageJson;
            _shortcutHandler = shortcutHandler;
        }

        public async Task OpenCompareTabAsync(
            string pathA,
            string pathB,
            string? contentA = null,
            string? contentB = null,
            string? customTitle = null,
            string? labelA = null,
            string? labelB = null)
        {
            contentA ??= await _fileService.ReadTextFileAsync(pathA);
            contentB ??= await _fileService.ReadTextFileAsync(pathB);

            string title = customTitle ?? string.Format(
                _getString("CompareTabTitleFormat", "비교: {0} ↔ {1}"),
                Path.GetFileName(pathA),
                Path.GetFileName(pathB));

            OpenedTab? existingTab = null;
            string diffTitlePrefix = _getString("AgentDiffTitle", "Agent 변경 비교");
            string fileName = Path.GetFileName(pathA);
            foreach (var t in _viewModel.Tabs)
            {
                if (string.Equals(t.Title, title, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(customTitle) && 
                     t.Title.StartsWith(diffTitlePrefix, StringComparison.Ordinal) && 
                     t.Title.Contains(fileName)))
                {
                    existingTab = t;
                    break;
                }
            }

            if (existingTab != null)
            {
                if (title != existingTab.Title)
                {
                    existingTab.Title = title;
                    foreach (var item in _editorTabView.TabItems)
                    {
                        if (item is TabViewItem tvi && string.Equals(tvi.Tag as string, existingTab.Id, StringComparison.Ordinal))
                        {
                            tvi.Header = title;
                            break;
                        }
                    }
                }

                TabViewItem? existingTabItem = null;
                foreach (var item in _editorTabView.TabItems)
                {
                    if (item is TabViewItem tvi && string.Equals(tvi.Tag as string, existingTab.Id, StringComparison.Ordinal))
                    {
                        existingTabItem = tvi;
                        break;
                    }
                }

                if (existingTabItem != null)
                {
                    _editorTabView.SelectedItem = existingTabItem;
                }

                if (_tabBridges.TryGetValue(existingTab.Id, out var bridgeGroup) && bridgeGroup.WebView != null)
                {
                    var existingCoreWebView = bridgeGroup.WebView.CoreWebView2;
                    if (existingCoreWebView != null)
                    {
                        var msg = new
                        {
                            action = "compare",
                            titleA = labelA ?? Path.GetFileName(pathA),
                            titleB = labelB ?? Path.GetFileName(pathB),
                            textA = contentA,
                            textB = contentB,
                            theme = _settingsService.CurrentSettings.Theme,
                            uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                            compareToolTitle = _getString("DiffCompareToolTitle", "TxtAIEditor 파일 비교 도구 (File Compare)"),
                            statsGathering = _getString("DiffStatsGathering", "수집 중..."),
                            originalFileLabel = _getString("DiffOriginalFileLabel", "원본 파일 (Original)"),
                            modifiedFileLabel = _getString("DiffModifiedFileLabel", "비교 대상 파일 (Modified)"),
                            originalPrefix = _getString("DiffOriginalPrefix", "원본: "),
                            modifiedPrefix = _getString("DiffModifiedPrefix", "수정본: "),
                            diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄")
                        };
                        string json = JsonSerializer.Serialize(msg);
                        existingCoreWebView.PostWebMessageAsJson(json);
                    }
                }
                return;
            }

            var tab = new OpenedTab
            {
                Title = title,
                FilePath = string.Empty,
                Content = string.Empty
            };

            _viewModel.Tabs.Add(tab);

            var grid = new Grid();
            var diffWebView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.Children.Add(diffWebView);

            var tabItem = new TabViewItem
            {
                Header = tab.Title,
                Content = grid,
                Tag = tab.Id
            };

            var env = await MonacoBridge.GetSharedEnvironmentAsync();
            await diffWebView.EnsureCoreWebView2Async(env);

            var coreWebView = diffWebView.CoreWebView2;
            if (coreWebView == null)
            {
                throw new InvalidOperationException("CoreWebView2 failed to initialize.");
            }

            WebViewAppearanceService.ApplyPreferredColorScheme(coreWebView, _settingsService.CurrentSettings.Theme);
            coreWebView.SetVirtualHostNameToFolderMapping(
                PreviewWebResourceService.ResourceHostName,
                PreviewWebResourceService.WebResourcesPath,
                CoreWebView2HostResourceAccessKind.Allow);

            coreWebView.Settings.IsWebMessageEnabled = true;
            coreWebView.Settings.IsScriptEnabled = true;
            coreWebView.Settings.AreDefaultContextMenusEnabled = false;
            coreWebView.Settings.AreDevToolsEnabled = false;

            diffWebView.WebMessageReceived += OnDiffWebMessageReceived;
            diffWebView.Source = new Uri($"http://{PreviewWebResourceService.ResourceHostName}/diff.html");

            diffWebView.NavigationCompleted += (_, _) =>
            {
                var msg = new
                {
                    action = "compare",
                    titleA = labelA ?? Path.GetFileName(pathA),
                    titleB = labelB ?? Path.GetFileName(pathB),
                    textA = contentA,
                    textB = contentB,
                    theme = _settingsService.CurrentSettings.Theme,
                    uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                    compareToolTitle = _getString("DiffCompareToolTitle", "TxtAIEditor 파일 비교 도구 (File Compare)"),
                    statsGathering = _getString("DiffStatsGathering", "수집 중..."),
                    originalFileLabel = _getString("DiffOriginalFileLabel", "원본 파일 (Original)"),
                    modifiedFileLabel = _getString("DiffModifiedFileLabel", "비교 대상 파일 (Modified)"),
                    originalPrefix = _getString("DiffOriginalPrefix", "원본: "),
                    modifiedPrefix = _getString("DiffModifiedPrefix", "수정본: "),
                    diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄")
                };
                string json = JsonSerializer.Serialize(msg);
                diffWebView.CoreWebView2.PostWebMessageAsJson(json);
            };

            _tabBridges[tab.Id] = (diffWebView, null!);
            _editorWorkspace.DisableTabItemTransitions();
            _editorTabView.TabItems.Add(tabItem);
            _editorTabView.SelectedItem = tabItem;
        }

        private void OnDiffWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = _normalizeWebMessageJson(args);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp) &&
                    string.Equals(typeProp.GetString(), "shortcut", StringComparison.Ordinal) &&
                    root.TryGetProperty("name", out var nameProp))
                {
                    string name = nameProp.GetString() ?? string.Empty;
                    sender.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                }
            }
            catch { }
        }

        public async Task UpdateCompareTabIfOpenAsync(
            string title,
            string pathA,
            string pathB,
            string? contentA = null,
            string? contentB = null,
            string? labelA = null,
            string? labelB = null)
        {
            OpenedTab? existingTab = null;
            string diffTitlePrefix = _getString("AgentDiffTitle", "Agent 변경 비교");
            string fileName = Path.GetFileName(pathA);
            foreach (var t in _viewModel.Tabs)
            {
                if (string.Equals(t.Title, title, StringComparison.Ordinal) ||
                    (t.Title.StartsWith(diffTitlePrefix, StringComparison.Ordinal) && t.Title.Contains(fileName)))
                {
                    existingTab = t;
                    break;
                }
            }

            if (existingTab != null)
            {
                if (title != existingTab.Title)
                {
                    existingTab.Title = title;
                    foreach (var item in _editorTabView.TabItems)
                    {
                        if (item is TabViewItem tvi && string.Equals(tvi.Tag as string, existingTab.Id, StringComparison.Ordinal))
                        {
                            tvi.Header = title;
                            break;
                        }
                    }
                }

                if (_tabBridges.TryGetValue(existingTab.Id, out var bridgeGroup) && bridgeGroup.WebView != null)
            {
                contentA ??= await _fileService.ReadTextFileAsync(pathA);
                contentB ??= await _fileService.ReadTextFileAsync(pathB);

                var coreWebView = bridgeGroup.WebView.CoreWebView2;
                if (coreWebView != null)
                {
                    var msg = new
                    {
                        action = "compare",
                        titleA = labelA ?? Path.GetFileName(pathA),
                        titleB = labelB ?? Path.GetFileName(pathB),
                        textA = contentA,
                        textB = contentB,
                        theme = _settingsService.CurrentSettings.Theme,
                        uiFontFamily = _settingsService.CurrentSettings.UiFontFamily,
                        compareToolTitle = _getString("DiffCompareToolTitle", "TxtAIEditor 파일 비교 도구 (File Compare)"),
                        statsGathering = _getString("DiffStatsGathering", "수집 중..."),
                        originalFileLabel = _getString("DiffOriginalFileLabel", "원본 파일 (Original)"),
                        modifiedFileLabel = _getString("DiffModifiedFileLabel", "비교 대상 파일 (Modified)"),
                        originalPrefix = _getString("DiffOriginalPrefix", "원본: "),
                        modifiedPrefix = _getString("DiffModifiedPrefix", "수정본: "),
                        diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄")
                    };
                    string json = JsonSerializer.Serialize(msg);
                    coreWebView.PostWebMessageAsJson(json);
                }
            }
        }
    }
}
}
