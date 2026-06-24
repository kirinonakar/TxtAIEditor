using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    public sealed partial class TerminalPane : UserControl
    {
        private readonly ObservableCollection<TerminalSession> _terminalSessions = new ObservableCollection<TerminalSession>();
        private TerminalSession? _activeTerminalSession;
        private Func<string, string, string>? _getString;
        private bool _webViewReady;
        private bool _webViewInitializing;
        private bool _resizeQueued;
        private string _terminalProfileId = "PowerShell";
        private string _terminalFontFamily = "Consolas";
        private double _terminalFontSize = 13.0;

        public TerminalPane()
        {
            InitializeComponent();
            TerminalSessionsList.ItemsSource = _terminalSessions;
            Unloaded += OnUnloaded;
            ActualThemeChanged += OnActualThemeChanged;
        }

        public event EventHandler? SessionsEmptied;
        public event EventHandler? CloseRequested;
        public event EventHandler<string>? PathOpenRequested;

        public Func<string>? WorkingDirectoryProvider { get; set; }
        public bool HasSessions => _terminalSessions.Count > 0;

        public void AttachOwner(Window ownerWindow)
        {
        }

        public void ApplySettings(EditorSettings settings)
        {
            _terminalProfileId = TerminalShellProfile.NormalizeId(settings.TerminalProfile);
            _terminalFontFamily = string.IsNullOrWhiteSpace(settings.TerminalFontFamily)
                ? "Consolas"
                : settings.TerminalFontFamily.Trim();
            _terminalFontSize = Math.Clamp(settings.TerminalFontSize, 8.0, 36.0);
            UpdateAllTerminalThemes();
        }

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
            UpdateTitle();
            NewTerminalButton.Content = getString("NewTerminal", "새 터미널");
            CloseTerminalButton.Content = getString("CloseTerminal", "닫기");
            BuildNewTerminalFlyout();
        }

        public void OpenTerminal(string workingDirectory)
        {
            OpenTerminal(workingDirectory, null);
        }

        public void OpenTerminal(string workingDirectory, string? profileId)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return;
            }

            string resolvedDirectory;
            try
            {
                resolvedDirectory = Path.GetFullPath(workingDirectory);
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(resolvedDirectory))
            {
                return;
            }

            _ = StartTerminalAsync(resolvedDirectory, profileId);
        }

        public void SuspendNativeWindows()
        {
        }

        public void ResumeNativeWindows()
        {
            QueueEmbeddedTerminalResize();
            FocusActiveTerminal();
        }

        public void StopAllSessions()
        {
            foreach (var session in _terminalSessions.ToList())
            {
                StopTerminalSession(session);
            }

            _terminalSessions.Clear();
            _activeTerminalSession = null;
            ShowEmptyState();
        }

        public void ResizeEmbeddedTerminal()
        {
            PostTerminalMessage(new { type = "fit" });
            if (_activeTerminalSession != null)
            {
                _activeTerminalSession.Terminal?.Resize(
                    (short)Math.Clamp(_activeTerminalSession.Columns, 2, short.MaxValue),
                    (short)Math.Clamp(_activeTerminalSession.Rows, 1, short.MaxValue));
            }
        }

        public void QueueEmbeddedTerminalResize()
        {
            if (_resizeQueued)
            {
                return;
            }

            _resizeQueued = true;
            bool queued = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ResizeEmbeddedTerminal();
                }
                finally
                {
                    _resizeQueued = false;
                }
            });

            if (!queued)
            {
                _resizeQueued = false;
            }
        }

        private async void QueueDelayedEmbeddedTerminalResize()
        {
            await Task.Delay(80);
            ResizeEmbeddedTerminal();
        }

        public void UpdateAllTerminalThemes()
        {
            PostTerminalMessage(new
            {
                type = "settings",
                theme = ResolveThemeName(),
                fontFamily = BuildTerminalFontFamily(),
                fontSize = _terminalFontSize
            });
            ApplyWebViewBackground();
        }

        private async Task StartTerminalAsync(string workingDirectory, string? profileId = null)
        {
            await EnsureTerminalWebViewAsync();

            var shellProfile = TerminalShellProfile.Resolve(string.IsNullOrWhiteSpace(profileId) ? _terminalProfileId : profileId);
            var session = new TerminalSession(workingDirectory, shellProfile);
            _terminalSessions.Add(session);
            RenumberTerminalSessions();
            SetActiveTerminalSession(session);

            try
            {
                short columns = (short)Math.Clamp(session.Columns, 2, short.MaxValue);
                short rows = (short)Math.Clamp(session.Rows, 1, short.MaxValue);
                var terminal = ConPtyTerminal.Start(shellProfile, workingDirectory, columns, rows);
                session.Terminal = terminal;
                session.Process = terminal.Process;
                terminal.OutputReceived += text => AppendTerminalOutput(session, text);
                terminal.Exited += () => CloseExitedTerminalSession(session);
            }
            catch (Exception ex)
            {
                AppendTerminalOutput(session, $"{_getString?.Invoke("TerminalStartFailed", "터미널을 시작하지 못했습니다") ?? "터미널을 시작하지 못했습니다"}: {ex.Message}\r\n");
            }
        }

        private async Task EnsureTerminalWebViewAsync()
        {
            if (_webViewReady || _webViewInitializing)
            {
                return;
            }

            _webViewInitializing = true;
            try
            {
                var env = await MonacoBridge.GetSharedEnvironmentAsync();
                await TerminalWebView.EnsureCoreWebView2Async(env);
                TerminalWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                TerminalWebView.CoreWebView2.Settings.IsScriptEnabled = true;
                TerminalWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                TerminalWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                TerminalWebView.CoreWebView2.WebMessageReceived += OnTerminalWebMessageReceived;
                TerminalWebView.NavigationCompleted += OnTerminalNavigationCompleted;

                string webResourcesPath = Path.Combine(AppContext.BaseDirectory, "WebResources");
                TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "TxtAIEditor-terminal.local",
                    webResourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                TerminalWebView.Source = new Uri($"https://TxtAIEditor-terminal.local/terminal.html?v={Environment.TickCount64}");
                ApplyWebViewBackground();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal WebView2 initialization failed: {ex.Message}");
            }
            finally
            {
                _webViewInitializing = false;
            }
        }

        private void OnTerminalNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _webViewReady = args.IsSuccess;
            UpdateAllTerminalThemes();
            if (_activeTerminalSession != null)
            {
                PostActiveSession();
            }
        }

        private void OnTerminalWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                string type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;

                if (type.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    _webViewReady = true;
                    UpdateDimensionsFromMessage(root);
                    PostActiveSession();
                    return;
                }

                if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
                {
                    if (_activeTerminalSession?.Terminal == null)
                    {
                        return;
                    }

                    string data = root.TryGetProperty("data", out var dataElement) ? dataElement.GetString() ?? string.Empty : string.Empty;
                    _ = _activeTerminalSession.Terminal.WriteAsync(data);
                    return;
                }

                if (type.Equals("resize", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateDimensionsFromMessage(root);
                    return;
                }

                if (type.Equals("copy", StringComparison.OrdinalIgnoreCase))
                {
                    string text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
                    WriteClipboardText(text);
                    return;
                }

                if (type.Equals("paste", StringComparison.OrdinalIgnoreCase))
                {
                    _ = PasteClipboardIntoActiveTerminalAsync();
                    return;
                }

                if (type.Equals("openPath", StringComparison.OrdinalIgnoreCase))
                {
                    string path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty;
                    RequestPathOpen(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal WebView message failed: {ex.Message}");
            }
        }

        private void UpdateDimensionsFromMessage(JsonElement root)
        {
            int columns = root.TryGetProperty("cols", out var colsElement) ? colsElement.GetInt32() : 80;
            int rows = root.TryGetProperty("rows", out var rowsElement) ? rowsElement.GetInt32() : 24;
            columns = Math.Clamp(columns, 2, short.MaxValue);
            rows = Math.Clamp(rows, 1, short.MaxValue);

            if (_activeTerminalSession == null)
            {
                return;
            }

            _activeTerminalSession.Columns = columns;
            _activeTerminalSession.Rows = rows;
            _activeTerminalSession.Terminal?.Resize((short)columns, (short)rows);
        }

        private void StopTerminalSession(TerminalSession session)
        {
            try
            {
                session.Terminal?.Dispose();
                session.Terminal = null;
                session.Process = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in StopTerminalSession: {ex.Message}");
            }
        }

        private void AppendTerminalOutput(TerminalSession session, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            session.Output.Append(text);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_activeTerminalSession == session)
                {
                    PostTerminalMessage(new { type = "output", sessionId = session.WindowTitle, data = text });
                }
            });
        }

        private void SetActiveTerminalSession(TerminalSession session)
        {
            _activeTerminalSession = session;
            UpdateTitle();

            if (TerminalSessionsList.SelectedItem != session)
            {
                TerminalSessionsList.SelectedItem = session;
            }

            PostActiveSession();
            FocusActiveTerminal();
        }

        private void PostActiveSession()
        {
            if (_activeTerminalSession == null)
            {
                return;
            }

            PostTerminalMessage(new
            {
                type = "setSession",
                sessionId = _activeTerminalSession.WindowTitle,
                theme = ResolveThemeName(),
                fontFamily = BuildTerminalFontFamily(),
                fontSize = _terminalFontSize
            });

            string existingOutput = _activeTerminalSession.Output.ToString();
            if (!string.IsNullOrEmpty(existingOutput))
            {
                PostTerminalMessage(new { type = "output", sessionId = _activeTerminalSession.WindowTitle, data = existingOutput });
            }
        }

        private void PostTerminalMessage(object message)
        {
            try
            {
                if (!_webViewReady || TerminalWebView.CoreWebView2 == null)
                {
                    return;
                }

                string json = JsonSerializer.Serialize(message);
                TerminalWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to post terminal message: {ex.Message}");
            }
        }

        private void FocusActiveTerminal()
        {
            TerminalWebView.Focus(FocusState.Programmatic);
            PostTerminalMessage(new { type = "focus" });
        }

        private void RequestClose()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CloseTerminalSession(TerminalSession session)
        {
            int index = _terminalSessions.IndexOf(session);
            if (index < 0)
            {
                return;
            }

            StopTerminalSession(session);
            _terminalSessions.Remove(session);
            RenumberTerminalSessions();

            if (_terminalSessions.Count == 0)
            {
                _activeTerminalSession = null;
                ShowEmptyState();
                SessionsEmptied?.Invoke(this, EventArgs.Empty);
                return;
            }

            int nextIndex = Math.Clamp(index, 0, _terminalSessions.Count - 1);
            SetActiveTerminalSession(_terminalSessions[nextIndex]);
        }

        private void RenumberTerminalSessions()
        {
            for (int i = 0; i < _terminalSessions.Count; i++)
            {
                _terminalSessions[i].SetDisplayNumber(i + 1);
            }
        }

        private void CloseExitedTerminalSession(TerminalSession session)
        {
            DispatcherQueue.TryEnqueue(() => CloseTerminalSession(session));
        }

        private string GetWorkingDirectoryOrDefault()
        {
            string? workingDirectory = WorkingDirectoryProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                return workingDirectory;
            }

            if (_activeTerminalSession != null && Directory.Exists(_activeTerminalSession.WorkingDirectory))
            {
                return _activeTerminalSession.WorkingDirectory;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void ShowEmptyState()
        {
            UpdateTitle();
            PostTerminalMessage(new
            {
                type = "setSession",
                sessionId = string.Empty,
                theme = ResolveThemeName(),
                fontFamily = BuildTerminalFontFamily(),
                fontSize = _terminalFontSize
            });
        }

        private void UpdateTitle()
        {
            string title = _getString?.Invoke("TerminalTitle", "터미널") ?? "터미널";
            TerminalTitleText.Text = _activeTerminalSession == null
                ? title
                : $"{title} - {_activeTerminalSession.ShellProfile.DisplayName} - {_activeTerminalSession.WorkingDirectory}";
        }

        private string ResolveThemeName()
        {
            return ActualTheme == ElementTheme.Light ? "Dark" : "Dark";
        }

        private string BuildTerminalFontFamily()
        {
            string fontFamily = string.IsNullOrWhiteSpace(_terminalFontFamily) ? "Consolas" : _terminalFontFamily.Trim();
            return $"{fontFamily}, Consolas, Cascadia Mono, Courier New, monospace";
        }

        private void ApplyWebViewBackground()
        {
            TerminalWebView.DefaultBackgroundColor = ActualTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 30, 30, 30);
        }

        private void BuildNewTerminalFlyout()
        {
            var flyout = new MenuFlyout();
            foreach (var profile in TerminalShellProfile.GetProfiles())
            {
                var item = new MenuFlyoutItem
                {
                    Text = profile.IsAvailable
                        ? profile.DisplayName
                        : $"{profile.DisplayName} ({_getString?.Invoke("SettingsTerminalNotFound", "설치되지 않음") ?? "설치되지 않음"})",
                    Tag = profile.Id,
                    IsEnabled = profile.IsAvailable
                };
                item.Click += OnNewTerminalProfileClick;
                flyout.Items.Add(item);
            }

            NewTerminalButton.Flyout = flyout;
        }

        private static void WriteClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        private async Task PasteClipboardIntoActiveTerminalAsync()
        {
            try
            {
                if (_activeTerminalSession?.Terminal == null)
                {
                    return;
                }

                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string text = await content.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    await _activeTerminalSession.Terminal.WriteAsync(text);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Terminal paste failed: {ex.Message}");
            }
        }

        private void RequestPathOpen(string rawPath)
        {
            string baseDirectory = _activeTerminalSession?.WorkingDirectory ?? GetWorkingDirectoryOrDefault();
            string path = NormalizeTerminalPath(rawPath, baseDirectory);
            if (!string.IsNullOrWhiteSpace(path))
            {
                PathOpenRequested?.Invoke(this, path);
            }
        }

        private static string NormalizeTerminalPath(string rawPath, string baseDirectory)
        {
            string value = (rawPath ?? string.Empty).Trim().Trim('"', '\'', '`');
            value = value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out var fileUri) &&
                fileUri.IsFile)
            {
                value = fileUri.LocalPath;
            }

            string lineSuffix = "";
            var lineMatch = Regex.Match(value, @"^(?<path>.+?)(?<suffix>:(?<line>\d+)(?::\d+)?)$");
            if (lineMatch.Success)
            {
                value = lineMatch.Groups["path"].Value;
                lineSuffix = lineMatch.Groups["suffix"].Value;
            }

            if (!Path.IsPathRooted(value) &&
                !string.IsNullOrWhiteSpace(baseDirectory) &&
                Directory.Exists(baseDirectory))
            {
                value = Path.GetFullPath(Path.Combine(baseDirectory, value));
            }

            return value + lineSuffix;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateAllTerminalThemes();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAllSessions();
        }

        private void OnTerminalWebViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueEmbeddedTerminalResize();
            QueueDelayedEmbeddedTerminalResize();
        }

        private void OnNewTerminalClick(SplitButton sender, SplitButtonClickEventArgs args)
        {
            OpenTerminal(GetWorkingDirectoryOrDefault());
        }

        private void OnNewTerminalProfileClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string profileId)
            {
                OpenTerminal(GetWorkingDirectoryOrDefault(), profileId);
            }
        }

        private void OnCloseTerminalClick(object sender, RoutedEventArgs e)
        {
            RequestClose();
        }

        private void OnCloseSessionItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TerminalSession session)
            {
                CloseTerminalSession(session);
            }
        }

        private void OnTerminalSessionsListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TerminalSessionsList.SelectedItem is TerminalSession session && _activeTerminalSession != session)
            {
                SetActiveTerminalSession(session);
            }
        }
    }
}
