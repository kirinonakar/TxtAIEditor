using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private readonly IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> _tabBridges;
        private readonly Func<string, string, string> _getString;
        private readonly Func<CoreWebView2WebMessageReceivedEventArgs, string> _normalizeWebMessageJson;
        private readonly Action<string> _shortcutHandler;
        private readonly Func<string, int, Task> _openFileAsync;
        private readonly Func<string, EditorDocumentSession?> _getEditorSession;
        private readonly ConditionalWeakTable<WebView2, ComparisonState> _comparisons = new();
        private readonly HashSet<string> _revertingPaths = new(StringComparer.OrdinalIgnoreCase);

        private sealed class ComparisonState
        {
            public string Revision { get; set; } = Guid.NewGuid().ToString("N");
            public string PathB { get; init; } = string.Empty;
            public string TextA { get; init; } = string.Empty;
            public string TextB { get; set; } = string.Empty;
            public bool AllowRevert { get; init; }
            public bool IsReverting { get; set; }
        }

        public CompareTabController(
            IFileService fileService,
            ISettingsService settingsService,
            Func<string, int, Task> openFileAsync,
            MainWindowViewModel viewModel,
            EditorWorkspacePane editorWorkspace,
            TabView editorTabView,
            IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges,
            Func<string, string, string> getString,
            Func<CoreWebView2WebMessageReceivedEventArgs, string> normalizeWebMessageJson,
            Action<string> shortcutHandler,
            Func<string, EditorDocumentSession?> getEditorSession)
        {
            _fileService = fileService;
            _settingsService = settingsService;
            _openFileAsync = openFileAsync;
            _viewModel = viewModel;
            _editorWorkspace = editorWorkspace;
            _editorTabView = editorTabView;
            _tabBridges = tabBridges;
            _getString = getString;
            _normalizeWebMessageJson = normalizeWebMessageJson;
            _shortcutHandler = shortcutHandler;
            _getEditorSession = getEditorSession;
        }

        public async Task OpenCompareTabAsync(
            string pathA,
            string pathB,
            string? contentA = null,
            string? contentB = null,
            string? customTitle = null,
            string? labelA = null,
            string? labelB = null,
            bool allowRevert = true)
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
                        var state = SetComparison(bridgeGroup.WebView, pathB, contentA, contentB, allowRevert);
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
                            diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄"),
                            pathB = pathB,
                            openFileTooltip = _getString("DiffOpenFileTooltip", "파일 열기"),
                            revision = state.Revision,
                            canRevert = state.AllowRevert,
                            revertChunkTooltip = _getString("DiffRevertChunkTooltip", "이 청크 되돌리기 (수정본 파일에 저장)")
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
                ContentPreview = string.Empty
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

            var env = await WebViewEnvironmentProvider.GetSharedAsync();
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
            coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            coreWebView.Settings.AreDefaultContextMenusEnabled = false;
            coreWebView.Settings.AreDevToolsEnabled = false;

            diffWebView.WebMessageReceived += OnDiffWebMessageReceived;
            diffWebView.NavigationCompleted += (_, _) =>
            {
                var state = SetComparison(diffWebView, pathB, contentA, contentB, allowRevert);
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
                    diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄"),
                    pathB = pathB,
                    openFileTooltip = _getString("DiffOpenFileTooltip", "파일 열기"),
                    revision = state.Revision,
                    canRevert = state.AllowRevert,
                    revertChunkTooltip = _getString("DiffRevertChunkTooltip", "이 청크 되돌리기 (수정본 파일에 저장)")
                };
                string json = JsonSerializer.Serialize(msg);
                diffWebView.CoreWebView2.PostWebMessageAsJson(json);
            };
            diffWebView.Source = new Uri($"http://{PreviewWebResourceService.ResourceHostName}/diff.html");

            _tabBridges[tab.Id] = (diffWebView, null!);
            _editorWorkspace.DisableTabItemTransitions();
            _editorTabView.TabItems.Add(tabItem);
            _editorTabView.SelectedItem = tabItem;
        }

        private ComparisonState SetComparison(WebView2 webView, string pathB, string textA, string textB, bool allowRevert)
        {
            var state = new ComparisonState
            {
                PathB = pathB,
                TextA = textA,
                TextB = textB,
                AllowRevert = allowRevert && Path.IsPathRooted(pathB)
            };
            _comparisons.Remove(webView);
            _comparisons.Add(webView, state);
            return state;
        }

        private async Task RevertChunkAsync(WebView2 sender, JsonElement request)
        {
            if (!_comparisons.TryGetValue(sender, out var state)) return;
            string revision = request.GetProperty("revision").GetString() ?? string.Empty;
            void Reply(bool success, string? error = null) => sender.CoreWebView2?.PostWebMessageAsJson(
                JsonSerializer.Serialize(new
                {
                    action = "revertChunkResult", requestRevision = revision, success,
                    revision = state.Revision, textB = success ? state.TextB : null, error
                }));

            string stale = _getString("DiffRevertChunkStale", "비교 이후 내용이 변경되었습니다. diff 뷰를 다시 열어 주세요.");
            if (!state.AllowRevert || state.IsReverting || state.Revision != revision)
            {
                Reply(false, stale);
                return;
            }

            state.IsReverting = true;
            string? lockedPath = null;
            try
            {
                string path = Path.GetFullPath(state.PathB);
                if (!_revertingPaths.Add(path))
                {
                    Reply(false, stale);
                    return;
                }
                lockedPath = path;
                var tabs = _viewModel.Tabs.Where(t => !string.IsNullOrEmpty(t.FilePath) &&
                    string.Equals(Path.GetFullPath(t.FilePath), path, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var tab in tabs)
                {
                    if (_tabBridges.TryGetValue(tab.Id, out var bridge) && bridge.Bridge != null)
                        await bridge.Bridge.FlushPendingEditForSaveAsync();
                }

                // Never replace unsaved edits or write a remote cache without uploading it.
                if (tabs.Any(t => t.IsDirty || t.IsReadOnlyViewer || !string.IsNullOrEmpty(t.RemotePath)))
                {
                    Reply(false, _getString("DiffRevertChunkUnavailable", "수정본에 저장하지 않은 변경 사항이 있거나 읽기 전용/원격 파일입니다. 로컬 파일을 저장한 후 다시 시도해 주세요."));
                    return;
                }

                var read = File.Exists(path)
                    ? await _fileService.ReadTextFileAsync(path, "Auto")
                    : new TextFileReadResult();
                if (!_comparisons.TryGetValue(sender, out var current) || !ReferenceEquals(current, state) ||
                    !string.Equals(read.Content, state.TextB, StringComparison.Ordinal) ||
                    tabs.Any(t => t.IsDirty))
                {
                    Reply(false, stale);
                    return;
                }

                string reverted = DiffChunkReverter.Revert(state.TextA, state.TextB,
                    request.GetProperty("originalStart").GetInt32(), request.GetProperty("originalCount").GetInt32(),
                    request.GetProperty("modifiedStart").GetInt32(), request.GetProperty("modifiedCount").GetInt32());
                await _fileService.SaveTextFileAsync(path, reverted, read.EncodingName);
                state.TextB = reverted;
                state.Revision = Guid.NewGuid().ToString("N");

                foreach (var tab in tabs)
                {
                    var session = _getEditorSession(tab.Id);
                    if (session != null)
                    {
                        // A user may have typed while the asynchronous save was in flight.
                        // Preserve those edits and let normal reload handling resolve them.
                        if (tab.IsDirty) continue;
                        session.UpdateContentFromSync(reverted);
                        if (_tabBridges.TryGetValue(tab.Id, out var bridge) && bridge.Bridge != null)
                        {
                            try { await bridge.Bridge.ResynchronizeModelAsync(session); }
                            catch { tab.IsPendingReload = true; }
                        }
                    }
                }
                Reply(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Diff chunk revert failed: {ex}");
                Reply(false, _getString("DiffRevertChunkFailed", "청크를 되돌리지 못했습니다. 파일 상태와 쓰기 권한을 확인해 주세요."));
            }
            finally
            {
                state.IsReverting = false;
                if (lockedPath != null) _revertingPaths.Remove(lockedPath);
            }
        }

        private async void OnDiffWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = _normalizeWebMessageJson(args);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? string.Empty;
                    if (string.Equals(type, "shortcut", StringComparison.Ordinal) &&
                        root.TryGetProperty("name", out var nameProp))
                    {
                        string name = nameProp.GetString() ?? string.Empty;
                        sender.DispatcherQueue.TryEnqueue(() => _shortcutHandler(name));
                    }
                    else if (string.Equals(type, "openFile", StringComparison.Ordinal) &&
                             root.TryGetProperty("path", out var pathProp))
                    {
                        string filePath = pathProp.GetString() ?? string.Empty;
                        int lineNumber = 0;
                        if (root.TryGetProperty("lineNumber", out var lineProp) &&
                            lineProp.ValueKind == JsonValueKind.Number)
                        {
                            lineNumber = lineProp.GetInt32();
                        }

                        if (!string.IsNullOrEmpty(filePath))
                            await _openFileAsync(filePath, lineNumber);
                    }
                    else if (string.Equals(type, "revertChunk", StringComparison.Ordinal))
                    {
                        await RevertChunkAsync(sender, root);
                    }
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
                    bool allowRevert = !_comparisons.TryGetValue(bridgeGroup.WebView, out var previous) || previous.AllowRevert;
                    var state = SetComparison(bridgeGroup.WebView, pathB, contentA, contentB, allowRevert);
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
                        diffStatsFormat = _getString("DiffStatsFormat", "변경사항: 추가 {0}줄, 삭제 {1}줄"),
                        pathB = pathB,
                        openFileTooltip = _getString("DiffOpenFileTooltip", "파일 열기"),
                        revision = state.Revision,
                        canRevert = state.AllowRevert,
                        revertChunkTooltip = _getString("DiffRevertChunkTooltip", "이 청크 되돌리기 (수정본 파일에 저장)")
                    };
                    string json = JsonSerializer.Serialize(msg);
                    coreWebView.PostWebMessageAsJson(json);
                }
            }
        }
    }
}
}
