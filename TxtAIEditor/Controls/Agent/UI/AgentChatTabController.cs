using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentChatTabController : IDisposable
    {
        private const string ChatHost = "agent-chat.txtaieditor.local";
        private static readonly Uri ChatPage = new($"https://{ChatHost}/agent-chat.html");
        private static readonly JsonSerializerOptions MessageOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly AgentPane _pane;
        private readonly ToggleButton _statusBarChatToggle;
        private readonly EditorWorkspacePane _workspace;
        private readonly MainWindowViewModel _viewModel;
        private readonly ITabCloseCommands _documents;
        private readonly TabNavigationController _navigation;
        private readonly ISettingsService _settings;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Action<string> _handleShortcut;
        private readonly DispatcherQueueTimer _refreshTimer;
        private AgentController? _agent;
        private IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)>? _tabBridges;
        private OpenedTab? _lastDocument;
        private OpenedTab? _tab;
        private TabViewItem? _item;
        private CoreWebView2? _readyWebView;
        private AgentChatSnapshot? _lastSnapshot;
        private IReadOnlyList<AgentOpenSessionItemViewModel>? _lastSessionItems;
        private string? _imageWorkspaceKey;
        private int _lastPromptCount;
        private int _lastHiddenCount;
        private int _lastHiddenEnd;
        private string? _lastTheme;
        private bool _lastUsePele;
        private (string Family, double Size)? _lastFont;

        public AgentChatTabController(AgentPane pane, ToggleButton statusBarChatToggle, EditorWorkspacePane workspace, MainWindowViewModel viewModel,
            ITabCloseCommands documents, TabNavigationController navigation, ISettingsService settings,
            Func<string, string, string> getString, Action<string, string> showError, Action<string> handleShortcut)
        {
            _pane = pane;
            _statusBarChatToggle = statusBarChatToggle;
            _workspace = workspace;
            _viewModel = viewModel;
            _documents = documents;
            _navigation = navigation;
            _settings = settings;
            _getString = getString;
            _showError = showError;
            _handleShortcut = handleShortcut;
            _refreshTimer = pane.DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(100);
            _refreshTimer.Tick += (_, _) => Refresh();
        }

        public void Connect(AgentController agent,
            IDictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> tabBridges)
        {
            _agent = agent;
            _tabBridges = tabBridges;
            _pane.StreamToTabToggleButton.Checked += OnChecked;
            _pane.StreamToTabToggleButton.Unchecked += OnUnchecked;
            _statusBarChatToggle.IsChecked = _pane.StreamToTabToggleButton.IsChecked;
            _statusBarChatToggle.Click += OnStatusBarChatToggleClick;
            _viewModel.Tabs.CollectionChanged += OnTabsChanged;
        }

        public OpenedTab? GetActiveDocument()
        {
            var active = _navigation.GetActiveTab();
            if (active?.IsAgentChat == true)
            {
                return _lastDocument is { } previous && _navigation.IsOpen(previous) ? previous : null;
            }

            if (active != null && !active.IsDiffView)
            {
                _lastDocument = active;
            }
            return active;
        }

        public bool IsOpen => _tab != null;

        private void OnStatusBarChatToggleClick(object sender, RoutedEventArgs args) =>
            _pane.StreamToTabToggleButton.IsChecked = _statusBarChatToggle.IsChecked == true;

        private async void OnChecked(object sender, RoutedEventArgs args)
        {
            if (_tab != null || _tabBridges == null)
            {
                return;
            }

            _statusBarChatToggle.IsChecked = true;
            GetActiveDocument();
            var tab = new OpenedTab { Title = _getString("AgentChatTabTitle", "Agent 채팅"), IsAgentChat = true };
            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = WebViewAppearanceService.ResolveEditorBackgroundColor(_settings.CurrentSettings)
            };
            var tabView = _navigation.GetCurrentActiveTabView();
            var item = new TabViewItem { Header = tab.Title, Content = webView, Tag = tab.Id };
            _tab = tab;
            _item = item;
            _tabBridges[tab.Id] = (webView, null!);
            _viewModel.Tabs.Add(tab);
            _workspace.DisableTabItemTransitions();
            tabView.TabItems.Add(item);
            tabView.SelectedItem = item;

            try
            {
                var environment = await WebViewEnvironmentProvider.GetSharedAsync();
                if (_tab != tab)
                {
                    return;
                }
                await webView.EnsureCoreWebView2Async(environment);
                if (_tab != tab)
                {
                    return;
                }

                var core = webView.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping(ChatHost, PreviewWebResourceService.WebResourcesPath,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.NavigationStarting += (_, eventArgs) => eventArgs.Cancel = !IsChatPage(eventArgs.Uri);
                core.NewWindowRequested += async (_, eventArgs) =>
                {
                    eventArgs.Handled = true;
                    if (eventArgs.IsUserInitiated && Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) &&
                        target.Scheme is "https" or "http")
                    {
                        await Windows.System.Launcher.LaunchUriAsync(target);
                    }
                };
                core.WebMessageReceived += (_, eventArgs) => ReceiveMessage(tab, core, eventArgs);
                core.NavigationCompleted += (_, eventArgs) =>
                {
                    if (!eventArgs.IsSuccess)
                    {
                        HandleLoadFailure(tab, eventArgs.WebErrorStatus.ToString());
                    }
                };
                webView.Source = ChatPage;
            }
            catch (Exception ex)
            {
                HandleLoadFailure(tab, ex.Message);
            }
        }

        private static bool IsChatPage(string? source) =>
            string.Equals(source, ChatPage.AbsoluteUri, StringComparison.Ordinal);

        private bool IsCurrentImageWorkspace(CoreWebView2 core, string workspaceKey) =>
            core == _readyWebView && _imageWorkspaceKey == workspaceKey && IsChatPage(core.Source);

        private async Task SendChatImageAsync(CoreWebView2 core, string workspaceKey, string requestId, string path)
        {
            if (!IsCurrentImageWorkspace(core, workspaceKey) || _lastSnapshot?.WorkspaceRoot is not { Length: > 0 } root)
            {
                return;
            }

            try
            {
                if (ResolveImagePath(root, path) is { } image)
                {
                    using var file = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long length = file.Length;
                    if (length > 0)
                    {
                        using var buffer = core.Environment.CreateSharedBuffer((ulong)length);
                        using (var destination = buffer.OpenStream().AsStreamForWrite())
                        {
                            await file.CopyToAsync(destination);
                            await destination.FlushAsync();
                        }
                        if (IsCurrentImageWorkspace(core, workspaceKey))
                        {
                            core.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadOnly,
                                JsonSerializer.Serialize(new { workspaceKey, requestId, path, contentType = image.ContentType }, MessageOptions));
                        }
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or COMException)
            {
                Debug.WriteLine(ex.Message);
            }

            if (IsCurrentImageWorkspace(core, workspaceKey))
            {
                Post(new { action = "imageUnavailable", workspaceKey, requestId, path });
            }
        }

        private static (string Path, string ContentType)? ResolveImagePath(string root, string path)
        {
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri) || !fileUri.IsFile) return null;
                path = fileUri.LocalPath;
            }
            else
            {
                int suffix = path.IndexOfAny(['?', '#']);
                path = Uri.UnescapeDataString(suffix >= 0 ? path[..suffix] : path);
            }

            if (!PreviewWebResourceService.GetContentType(path).StartsWith("image/", StringComparison.Ordinal) ||
                (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))) return null;

            string fullPath = Path.GetFullPath(path, root);
            if (!AgentWorkspaceFileResolver.IsInsidePhysicalRoot(root, fullPath)) return null;

            AgentWorkspaceFileResolver.VerifyPinnedWorkspace(root);
            string resolved = AgentWorkspaceFileResolver.ResolvePhysicalPath(fullPath, confinedTo: root);
            AgentWorkspaceFileResolver.VerifyPhysicalPath(resolved);
            string contentType = PreviewWebResourceService.GetContentType(resolved);
            return contentType.StartsWith("image/", StringComparison.Ordinal) ? (resolved, contentType) : null;
        }

        private void ReceiveMessage(OpenedTab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_tab != tab || !IsChatPage(args.Source))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                switch (ReadString(message, "action"))
                {
                    case "ready":
                        _readyWebView = core;
                        _lastSnapshot = null;
                        _lastSessionItems = null;
                        _lastFont = null;
                        Post(new
                        {
                            action = "labels",
                            title = _getString("AgentChatTabTitle", "Agent 채팅"),
                            turns = _getString("AgentChatTurnNavigation", "바로가기"),
                            sessions = _getString("AgentChatSessionsTitle", "세션"),
                            newSession = _getString("AgentNewSessionButton", "새 세션"),
                            closeAllSessions = _getString("AgentOpenSessionsCloseAllText", "모두 닫기"),
                            emptySessions = _getString("AgentOpenSessionsEmptyText", "열린 세션 없음"),
                            untitledSession = _getString("AgentOpenSessionUntitled", "새 세션"),
                            runningSession = _getString("AgentOpenSessionRunningSuffix", "실행 중"),
                            prompt = _getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력..."),
                            send = _getString("AgentSendButton", "전송"),
                            copy = _getString("EditorContextMenuCopy", "복사"),
                            copyAll = _getString("AgentChatCopyAll", "전체 복사"),
                            userRole = _getString("AgentChatUserRole", "사용자 지시"),
                            assistantRole = _getString("AgentChatAssistantRole", "AI 답변"),
                            copied = _getString("AgentChatCopied", "복사됨"),
                            copyFailed = _getString("AgentChatCopyFailed", "복사 실패"),
                            thinking = _getString("AgentActivityThinking", "생각중"),
                            verboseToolPrefix = _getString("AgentToolRunning", "도구 실행 중") + ":",
                            toolStatusPrefixes = new[]
                            {
                                _getString("AgentHistoryToolCompletedFormat", "[도구 실행]: {0}").Split("{0}")[0].TrimEnd(),
                                _getString("AgentHistoryToolFailedFormat", "[도구 실행 실패]: {0}").Split("{0}")[0].TrimEnd()
                            },
                            chartErrorLabel = _getString("PreviewChartError", "차트를 표시할 수 없습니다.")
                        });
                        Refresh();
                        _refreshTimer.Start();
                        break;
                    case "shortcut":
                        if (ReadString(message, "name") is { } shortcut)
                        {
                            _pane.DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_tab == tab && _navigation.GetActiveTab() == tab)
                                {
                                    _handleShortcut(shortcut);
                                }
                            });
                        }
                        break;
                    case "copy":
                        if (ReadString(message, "copyId") is { } copyId && ReadString(message, "text") is { Length: > 0 } text)
                        {
                            bool success = _navigation.GetActiveTab() == tab && TryCopyText(text);
                            Post(new { action = "copyResult", copyId, success });
                        }
                        break;
                    case "loadImage":
                        if (ReadString(message, "workspaceKey") is { } workspaceKey &&
                            ReadString(message, "requestId") is { } requestId &&
                            ReadString(message, "path") is { Length: > 0 } path)
                        {
                            _ = SendChatImageAsync(core, workspaceKey, requestId, path);
                        }
                        break;
                    case "selectSession":
                        if (ReadString(message, "sessionId") is { Length: > 0 } sessionToSelect)
                        {
                            QueueSessionAction(tab, core, () => _pane.SelectOpenSession(sessionToSelect));
                        }
                        break;
                    case "newSession":
                        QueueSessionAction(tab, core, _pane.RequestNewSession);
                        break;
                    case "closeAllSessions":
                        QueueSessionAction(tab, core, _pane.RequestCloseAllSessions);
                        break;
                    case "submit":
                        string? sessionId = ReadString(message, "sessionId");
                        string? prompt = ReadString(message, "prompt");
                        if (sessionId != null && prompt != null && _agent != null)
                        {
                            _agent.SubmitChatPrompt(sessionId, prompt, accepted =>
                            {
                                if (_tab == tab && _readyWebView == core)
                                {
                                    Post(new { action = "submitResult", sessionId, accepted });
                                    Refresh();
                                }
                            });
                        }
                        break;
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void QueueSessionAction(OpenedTab tab, CoreWebView2 core, Action action)
        {
            _pane.DispatcherQueue.TryEnqueue(() =>
            {
                if (_tab == tab && _readyWebView == core && _navigation.GetActiveTab() == tab)
                {
                    action();
                    Refresh();
                }
            });
        }

        private static string? ReadString(JsonElement message, string property) =>
            message.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool TryCopyText(string text)
        {
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        private void Refresh()
        {
            if (_readyWebView == null || _agent == null)
            {
                return;
            }

            GetActiveDocument();
            RefreshOpenSessions();
            var snapshot = _agent.GetChatSnapshot();
            var settings = _settings.CurrentSettings;
            var font = (Family: settings.AgentChatFontFamily, Size: settings.AgentChatFontSize);
            if (_lastFont != font)
            {
                Post(new { action = "font", family = font.Family, size = font.Size });
                _lastFont = font;
            }

            bool sameSession = _lastSnapshot?.SessionId == snapshot.SessionId;
            bool imageWorkspaceChanged = !sameSession || _lastSnapshot?.WorkspaceRoot != snapshot.WorkspaceRoot;
            bool shouldSendPrompts = !sameSession || !ReferenceEquals(_lastSnapshot?.Prompts, snapshot.Prompts) ||
                _lastPromptCount != snapshot.Prompts.Count;
            bool shouldSendPendingPrompts = !sameSession ||
                !ReferenceEquals(_lastSnapshot?.PendingPrompts, snapshot.PendingPrompts);
            int hiddenCount = snapshot.HiddenOutput.Count;
            int hiddenEnd = hiddenCount == 0 ? 0 : snapshot.HiddenOutput[^1].End;
            bool shouldSendHiddenOutput = !sameSession || !ReferenceEquals(_lastSnapshot?.HiddenOutput, snapshot.HiddenOutput) ||
                _lastHiddenCount != hiddenCount || _lastHiddenEnd != hiddenEnd;
            bool outputChanged = !sameSession || !ReferenceEquals(_lastSnapshot?.Output, snapshot.Output);
            if (!imageWorkspaceChanged && !shouldSendPrompts && !shouldSendPendingPrompts && !shouldSendHiddenOutput && !outputChanged &&
                _lastSnapshot?.CanSubmit == snapshot.CanSubmit &&
                _lastTheme == settings.Theme && _lastUsePele == settings.UsePele)
            {
                return;
            }

            int offset = sameSession ? FindCommonPrefix(_lastSnapshot!.Output, snapshot.Output) : 0;
            if (imageWorkspaceChanged)
            {
                _imageWorkspaceKey = string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot) ? null : Guid.NewGuid().ToString("N");
            }
            _lastSnapshot = snapshot;
            Post(new
            {
                action = "update",
                snapshot.SessionId,
                offset,
                text = snapshot.Output[offset..],
                prompts = shouldSendPrompts ? snapshot.Prompts : null,
                pendingPrompts = shouldSendPendingPrompts ? snapshot.PendingPrompts : null,
                hiddenOutput = shouldSendHiddenOutput ? snapshot.HiddenOutput : null,
                imageWorkspaceKey = _imageWorkspaceKey,
                snapshot.CanSubmit,
                theme = settings.Theme,
                usePele = settings.UsePele
            });
            _lastPromptCount = snapshot.Prompts.Count;
            _lastHiddenCount = hiddenCount;
            _lastHiddenEnd = hiddenEnd;
            _lastTheme = settings.Theme;
            _lastUsePele = settings.UsePele;
        }

        private void RefreshOpenSessions()
        {
            var items = _pane.OpenSessionItems;
            if (ReferenceEquals(_lastSessionItems, items)) return;

            var rows = AgentPaneSessionMenuCoordinator.GetSessionTreeRows(items, item => item.Id, item => item.OrchestratorSessionId)
                .Select(row => new
                {
                    row.Item.Id,
                    row.Item.Title,
                    row.Item.CanSelect,
                    row.Item.IsRunning,
                    branch = row.Branch.ToString()
                });
            Post(new { action = "sessions", items = rows });
            _lastSessionItems = items;
        }

        private static int FindCommonPrefix(string previous, string current)
        {
            if (ReferenceEquals(previous, current))
            {
                return previous.Length;
            }
            int offset = previous.AsSpan().CommonPrefixLength(current.AsSpan());
            return offset > 0 && char.IsHighSurrogate(previous[offset - 1]) ? offset - 1 : offset;
        }

        private void Post<T>(T message) =>
            _readyWebView?.PostWebMessageAsJson(JsonSerializer.Serialize(message, MessageOptions));

        private void HandleLoadFailure(OpenedTab tab, string details)
        {
            if (_tab == tab)
            {
                _pane.StreamToTabToggleButton.IsChecked = false;
                _showError(tab.Title, details);
            }
        }

        private void OnUnchecked(object sender, RoutedEventArgs args)
        {
            _statusBarChatToggle.IsChecked = false;
            if (_tab is { } tab && _item is { } item)
            {
                Reset();
                _documents.CloseAndCleanup(tab, item);
            }
        }

        private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (_tab is not { } tab || _viewModel.Tabs.Contains(tab))
            {
                return;
            }

            // Reordering temporarily removes a model before reinserting it.
            _pane.DispatcherQueue.TryEnqueue(() =>
            {
                if (_tab == tab && !_viewModel.Tabs.Contains(tab))
                {
                    Reset();
                    _pane.StreamToTabToggleButton.IsChecked = false;
                }
            });
        }

        private void Reset()
        {
            _refreshTimer.Stop();
            _tab = null;
            _item = null;
            _readyWebView = null;
            _lastSnapshot = null;
            _lastSessionItems = null;
            _imageWorkspaceKey = null;
        }

        public void Dispose()
        {
            Reset();
            _pane.StreamToTabToggleButton.Checked -= OnChecked;
            _pane.StreamToTabToggleButton.Unchecked -= OnUnchecked;
            _statusBarChatToggle.Click -= OnStatusBarChatToggleClick;
            _viewModel.Tabs.CollectionChanged -= OnTabsChanged;
        }
    }
}
