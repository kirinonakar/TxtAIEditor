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
using Microsoft.UI.Xaml.Input;
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
        private readonly StatusBarPane _statusBar;
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
        private string? _lastChipsSignature;
        private string? _lastAttachmentsSignature;

        public AgentChatTabController(AgentPane pane, StatusBarPane statusBar, EditorWorkspacePane workspace, MainWindowViewModel viewModel,
            ITabCloseCommands documents, TabNavigationController navigation, ISettingsService settings,
            Func<string, string, string> getString, Action<string, string> showError, Action<string> handleShortcut)
        {
            _pane = pane;
            _statusBar = statusBar;
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
            _pane.ChatTabToggleRequested += ToggleChatTab;
            _pane.StreamToTabToggleButton.Click += OnToggleClick;
            _statusBar.ChatTabToggleButton.Click += OnToggleClick;
            _workspace.ActiveTabViewChanged += OnActiveTabViewChanged;
            _workspace.PrimarySelectionChanged += OnTabSelectionChanged;
            _workspace.SecondarySelectionChanged += OnTabSelectionChanged;
            _viewModel.Tabs.CollectionChanged += OnTabsChanged;
            UpdateToggleState();
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

        private void OnToggleClick(object sender, RoutedEventArgs args) => ToggleChatTab();

        private void OnActiveTabViewChanged(object? sender, EventArgs args) => UpdateToggleState();

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateToggleState();

        private void ToggleChatTab()
        {
            if (_tab is not { } tab || _item is not { } item)
            {
                _ = OpenChatTabAsync();
                return;
            }

            if (_navigation.GetActiveTab() == tab)
            {
                CloseChatTab();
                return;
            }

            if (_navigation.GetTabViewForItem(item) is { } tabView)
            {
                GetActiveDocument();
                _workspace.ActiveTabView = tabView;
                tabView.SelectedItem = item;
                // Move focus after the tab activation completes. Moving WebView2 focus while the
                // activation is still being processed can leave the TSF/IME host bound to the
                // previous document, so Korean input falls back to alphanumeric typing.
                _pane.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (_tab == tab && _navigation.GetActiveTab() == tab)
                    {
                        (item.Content as WebView2)?.Focus(FocusState.Programmatic);
                    }
                });
            }
            UpdateToggleState();
        }

        private void UpdateToggleState()
        {
            bool isActive = _tab != null && _navigation.GetActiveTab() == _tab;
            bool showOutline = _tab != null && !isActive;
            _pane.UpdateChatTabToggle(isActive, showOutline);
            _statusBar.UpdateChatTabToggle(isActive, showOutline);
        }

        private async Task OpenChatTabAsync()
        {
            if (_tab != null || _tabBridges == null)
            {
                UpdateToggleState();
                return;
            }

            GetActiveDocument();
            var tab = new OpenedTab { Title = _getString("AgentChatTabTitle", "Agent 채팅"), IsAgentChat = true };
            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = WebViewAppearanceService.ResolveEditorBackgroundColor(_settings.CurrentSettings)
            };
            var tabView = _navigation.GetCurrentActiveTabView();
            var tabHeader = new TabHeaderControl();
            tabHeader.Configure(tab, _getString("EncryptedTabTooltip", "암호화됨"));
            var item = new TabViewItem { Header = tabHeader, Content = webView, Tag = tab.Id };
            item.RightTapped += OnChatTabRightTapped;
            _tab = tab;
            _item = item;
            _workspace.AgentChatAttachmentDropHitTest = HitTestAttachmentDrop;
            _tabBridges[tab.Id] = (webView, null!);
            _viewModel.Tabs.Add(tab);
            _workspace.DisableTabItemTransitions();
            tabView.TabItems.Add(item);
            tabView.SelectedItem = item;
            UpdateToggleState();

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

        private bool HitTestAttachmentDrop(Windows.Foundation.Point point)
        {
            if (_tab is null || _navigation.GetActiveTab() != _tab || _item?.Content is not WebView2 webView ||
                webView.XamlRoot is null || webView.ActualWidth <= 0 || webView.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                Windows.Foundation.Point origin = webView.TransformToVisual(_workspace)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                return point.X >= origin.X && point.X <= origin.X + webView.ActualWidth &&
                    point.Y >= origin.Y && point.Y <= origin.Y + webView.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

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
                            stop = _getString("AgentStopButton", "중단"),
                            clear = _getString("AgentChatClearPrompt", "입력 지우기"),
                            clearChips = _getString("AgentSelectedChipsClearAllTooltip", "모든 선택 칩 삭제"),
                            copy = _getString("EditorContextMenuCopy", "복사"),
                            copyAll = _getString("AgentChatCopyAll", "전체 복사"),
                            branch = _getString("AgentChatBranchFromHere", "브랜치"),
                            edit = _getString("AgentChatEditPrompt", "편집"),
                            editSend = _getString("AgentChatEditSend", "보내기"),
                            cancel = _getString("CommonCancel", "취소"),
                            rewind = _getString("AgentChatRewindToHere", "되감기"),
                            userRole = _getString("AgentChatUserRole", "사용자 지시"),
                            assistantRole = _getString("AgentChatAssistantRole", "AI 답변"),
                            attachFiles = _getString("AgentAttachFileCaption", "파일 첨부"),
                            copied = _getString("AgentChatCopied", "복사됨"),
                            copyFailed = _getString("AgentChatCopyFailed", "복사 실패"),
                            thinking = _getString("AgentActivityThinking", "생각중"),
                            verboseToolPrefix = _getString("AgentToolRunning", "도구 실행 중") + ":",
                            toolStatusPrefixes = new[]
                            {
                                _getString("AgentHistoryToolCompletedFormat", "[도구 실행]: {0}").Split("{0}")[0].TrimEnd(),
                                _getString("AgentHistoryToolFailedFormat", "[도구 실행 실패]: {0}").Split("{0}")[0].TrimEnd()
                            },
                            chartErrorLabel = _getString("PreviewChartError", "차트를 표시할 수 없습니다."),
                            palettePlugin = _getString("AgentChatPalettePlugin", "플러그인"),
                            paletteMcp = _getString("AgentChatPaletteMcp", "MCP"),
                            paletteSkill = _getString("AgentChatPaletteSkill", "스킬"),
                            palettePreset = _getString("AgentChatPalettePreset", "프리셋"),
                            paletteEmpty = _getString("AgentChatPaletteEmpty", "일치하는 항목 없음"),
                            paletteActive = _getString("AgentChatPaletteActive", "활성")
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
                    case "branch":
                        if (_agent is { } branchAgent &&
                            ReadString(message, "sessionId") is { Length: > 0 } branchSessionId &&
                            message.TryGetProperty("offset", out var branchOffset) &&
                            branchOffset.ValueKind == JsonValueKind.Number &&
                            branchOffset.TryGetInt32(out int branchOffsetValue))
                        {
                            QueueSessionAction(tab, core, () => branchAgent.BranchChatSession(branchSessionId, branchOffsetValue));
                        }
                        break;
                    case "editPrompt":
                        if (_agent is { } editAgent &&
                            ReadString(message, "sessionId") is { Length: > 0 } editSessionId &&
                            ReadString(message, "prompt") is { Length: > 0 } editedPrompt &&
                            TryReadOffset(message, out int editOffset))
                        {
                            editAgent.ResendChatPrompt(editSessionId, editOffset, editedPrompt, accepted =>
                            {
                                if (_tab != tab || _readyWebView != core)
                                {
                                    return;
                                }

                                if (!accepted)
                                {
                                    // Roll the optimistic bubble text back to the host state.
                                    _lastSnapshot = null;
                                }

                                Refresh();
                            });
                        }
                        break;
                    case "rewindPrompt":
                        if (_agent is { } rewindAgent &&
                            ReadString(message, "sessionId") is { Length: > 0 } rewindSessionId &&
                            ReadString(message, "prompt") is { } rewindPromptText &&
                            TryReadOffset(message, out int rewindOffset))
                        {
                            QueueSessionAction(tab, core, () => _ = RewindPromptAsync(
                                tab, core, rewindAgent, rewindSessionId, rewindOffset, rewindPromptText));
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
                    case "clearChips":
                        QueueSessionAction(tab, core, _pane.RequestClearSelectedChips);
                        break;
                    case "removeChip":
                        if (ReadString(message, "kind") is { Length: > 0 } chipKind &&
                            ReadString(message, "name") is { Length: > 0 } chipName)
                        {
                            QueueSessionAction(tab, core, () => _pane.RequestRemoveSelectedChip(chipKind, chipName));
                        }
                        break;
                    case "removeAttachment":
                        if (ReadString(message, "id") is { Length: > 0 } attachmentId)
                        {
                            QueueSessionAction(tab, core, () => _pane.RequestRemoveAttachment(attachmentId));
                        }
                        break;
                    case "selectSession":
                        if (ReadString(message, "sessionId") is { Length: > 0 } sessionToSelect)
                        {
                            QueueSessionAction(tab, core, () => _pane.SelectOpenSession(sessionToSelect));
                        }
                        break;
                    case "attachFiles":
                        if (args.AdditionalObjects is { Count: > 0 } droppedObjects)
                        {
                            var droppedPaths = new List<string>();
                            foreach (object droppedObject in droppedObjects)
                            {
                                if (droppedObject is CoreWebView2File droppedFile &&
                                    !string.IsNullOrWhiteSpace(droppedFile.Path))
                                {
                                    droppedPaths.Add(droppedFile.Path);
                                }
                            }

                            if (droppedPaths.Count > 0)
                            {
                                _pane.DispatcherQueue.TryEnqueue(() => _pane.RaiseDroppedFiles(droppedPaths));
                            }
                        }
                        break;
                    case "newSession":
                        QueueSessionAction(tab, core, _pane.RequestNewSession);
                        break;
                    case "closeAllSessions":
                        QueueSessionAction(tab, core, _pane.RequestCloseAllSessions);
                        break;
                    case "stop":
                        if (_agent is { } stopAgent && ReadString(message, "sessionId") is { Length: > 0 } stopSessionId)
                        {
                            QueueSessionAction(tab, core, () => stopAgent.StopChatPrompt(stopSessionId));
                        }
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
                    case "paletteRequest":
                        _ = SendChatCommandItemsAsync(tab, core);
                        break;
                    case "paletteActivate":
                        if (ReadString(message, "id") is { Length: > 0 } commandId)
                        {
                            bool commandActive = message.TryGetProperty("active", out var commandActiveValue) &&
                                commandActiveValue.ValueKind == JsonValueKind.True;
                            _ = ActivateChatCommandAsync(tab, core, commandId, commandActive);
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

        private static bool TryReadOffset(JsonElement message, out int offset)
        {
            if (message.TryGetProperty("offset", out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out offset))
            {
                return true;
            }

            offset = 0;
            return false;
        }

        private async Task RewindPromptAsync(
            OpenedTab tab, CoreWebView2 core, AgentController agent, string sessionId, int offset, string prompt)
        {
            bool rewound = await agent.RewindChatPromptAsync(sessionId, offset);
            if (_tab != tab || _readyWebView != core)
            {
                return;
            }

            if (rewound)
            {
                // Let the composer pick up the rewound instruction so it can be edited and resent.
                Post(new { action = "rewindResult", sessionId, prompt, rewound });
            }

            Refresh();
        }

        private async Task SendChatCommandItemsAsync(OpenedTab tab, CoreWebView2 core)
        {
            if (_agent is not { } agent)
            {
                return;
            }

            IReadOnlyList<AgentChatCommandItem> items;
            try
            {
                items = await agent.GetChatCommandItemsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return;
            }

            if (_tab != tab || _readyWebView != core)
            {
                return;
            }

            Post(new
            {
                action = "paletteItems",
                items = items.Select(item => new { item.Id, item.Category, item.Name, item.Description, item.Active })
            });
        }

        private async Task ActivateChatCommandAsync(OpenedTab tab, CoreWebView2 core, string id, bool active)
        {
            if (_agent is not { } agent || _tab != tab || _readyWebView != core)
            {
                return;
            }

            try
            {
                await agent.SetChatCommandItemActiveAsync(id, active);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            await SendChatCommandItemsAsync(tab, core);
        }

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
            RefreshChips();
            RefreshAttachments();
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
                _lastSnapshot?.CanRewind == snapshot.CanRewind &&
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
                snapshot.CanRewind,
                theme = settings.Theme,
                usePele = settings.UsePele
            });
            _lastPromptCount = snapshot.Prompts.Count;
            _lastHiddenCount = hiddenCount;
            _lastHiddenEnd = hiddenEnd;
            _lastTheme = settings.Theme;
            _lastUsePele = settings.UsePele;
        }

        private void RefreshAttachments()
        {
            var attachments = _pane.Attachments;
            string signature = string.Join('\n', attachments.Select(attachment =>
                $"{attachment.Id}\t{attachment.DisplayName}\t{attachment.Detail}\t{attachment.TokenText}"));
            if (signature == _lastAttachmentsSignature)
            {
                return;
            }

            _lastAttachmentsSignature = signature;
            Post(new
            {
                action = "attachments",
                items = attachments.Select(attachment => new
                {
                    attachment.Id,
                    label = attachment.DisplayName,
                    detail = attachment.Detail,
                    token = attachment.TokenText,
                    attachment.RemoveTooltip
                })
            });
        }

        private void RefreshChips()
        {
            var chips = _pane.SelectedChips;
            string signature = string.Join('\n', chips.Select(chip =>
                $"{chip.Kind}\t{chip.Name}\t{chip.Label}\t{chip.RemoveTooltip}"));
            if (signature == _lastChipsSignature)
            {
                return;
            }

            _lastChipsSignature = signature;
            Post(new
            {
                action = "chips",
                items = chips.Select(chip => new { chip.Kind, chip.Name, chip.Label, chip.RemoveTooltip })
            });
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
                    row.Item.CompletedNotificationCount,
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
                CloseChatTab();
                _showError(tab.Title, details);
            }
        }

        private void OnChatTabRightTapped(object sender, RightTappedRoutedEventArgs args)
        {
            if (sender is not TabViewItem item || _navigation.GetTabViewForItem(item) is not { } tabView)
            {
                return;
            }

            args.Handled = true;

            var flyout = new MenuFlyout();

            var closeRightItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseRight", "오른쪽 탭 닫기"), Icon = new SymbolIcon(Symbol.Forward) };
            closeRightItem.Click += (_, _) => _documents.CloseRightTabs(item, tabView);
            flyout.Items.Add(closeRightItem);

            var closeLeftItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseLeft", "왼쪽 탭 닫기"), Icon = new SymbolIcon(Symbol.Back) };
            closeLeftItem.Click += (_, _) => _documents.CloseLeftTabs(item, tabView);
            flyout.Items.Add(closeLeftItem);

            var closeOthersItem = new MenuFlyoutItem { Text = _getString("TabMenuCloseOthers", "다른 탭 닫기"), Icon = new SymbolIcon(Symbol.Cancel) };
            closeOthersItem.Click += (_, _) => _documents.CloseOtherTabs(item, tabView);
            flyout.Items.Add(closeOthersItem);

            CursorResetHelper.AttachToFlyout(flyout, item);
            CursorResetHelper.ResetToArrow(item);
            flyout.ShowAt(item, new FlyoutShowOptions
            {
                Position = args.GetPosition(item)
            });
            CursorResetHelper.ResetToArrow(item);
        }

        private void CloseChatTab()
        {
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
                }
            });
        }

        private void Reset()
        {
            _refreshTimer.Stop();
            _workspace.AgentChatAttachmentDropHitTest = null;
            _tab = null;
            _item = null;
            _readyWebView = null;
            _lastSnapshot = null;
            _lastSessionItems = null;
            _imageWorkspaceKey = null;
            _lastChipsSignature = null;
            _lastAttachmentsSignature = null;
            UpdateToggleState();
        }

        public void Dispose()
        {
            Reset();
            _pane.ChatTabToggleRequested -= ToggleChatTab;
            _pane.StreamToTabToggleButton.Click -= OnToggleClick;
            _statusBar.ChatTabToggleButton.Click -= OnToggleClick;
            _workspace.ActiveTabViewChanged -= OnActiveTabViewChanged;
            _workspace.PrimarySelectionChanged -= OnTabSelectionChanged;
            _workspace.SecondarySelectionChanged -= OnTabSelectionChanged;
            _viewModel.Tabs.CollectionChanged -= OnTabsChanged;
        }
    }
}
