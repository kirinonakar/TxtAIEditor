using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpBrowserUseTool
    {
        private const int MaxPageTextLength = 60_000;
        private const int DefaultAccessibilityNodeLimit = 180;

        private readonly Func<EditorSettings> _settingsProvider;
        private readonly AgentBrowserAccessibilityService _accessibility = new();
        private readonly AgentBrowserCaptureService _captureService;
        private readonly AgentWindowsInputService _inputService = new();
        private IntPtr _browserWindow;
        private bool _controlledWindowIsBrowser = true;
        private string _defaultBrowserProcessName = string.Empty;
        private string _defaultBrowserExecutablePath = string.Empty;
        private AgentBrowserCapture? _lastCapture;

        public AgentMcpBrowserUseTool(Func<EditorSettings> settingsProvider, Action<LlmMessageAttachment>? addImageAttachment = null)
        {
            _settingsProvider = settingsProvider;
            _captureService = new AgentBrowserCaptureService(addImageAttachment);
        }

        public string ServerId => AgentMcpBrowserUseCatalog.ServerId;

        public string ServerName => AgentMcpBrowserUseCatalog.ServerName;

        public bool IsServerName(string serverName)
        {
            return serverName.Equals(AgentMcpBrowserUseCatalog.ServerName, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsServerId(string serverId)
        {
            return serverId.Equals(AgentMcpBrowserUseCatalog.ServerId, StringComparison.OrdinalIgnoreCase);
        }

        public bool CanHandleAlias(AgentMcpToolAlias alias)
        {
            return alias.ServerId.Equals(AgentMcpBrowserUseCatalog.ServerId, StringComparison.OrdinalIgnoreCase);
        }

        public AgentMcpItem CreateMenuItem(bool isSelected, Func<string, string, string> getString)
        {
            return AgentMcpBrowserUseCatalog.CreateMenuItem(isSelected, getString);
        }

        public IReadOnlyList<AgentMcpToolAlias> CreateAliases()
        {
            return AgentMcpBrowserUseCatalog.CreateAliases(_settingsProvider());
        }

        public async Task<string> ExecuteAsync(AgentMcpToolAlias alias, JsonElement arguments, CancellationToken cancellationToken)
        {
            try
            {
                return alias.ToolName switch
                {
                    "open_url" => await OpenUrlAsync(arguments, cancellationToken),
                    "status" => await GetStatusAsync(cancellationToken),
                    "snapshot" => await SnapshotAsync(arguments, cancellationToken),
                    "capture" => await CaptureAsync(cancellationToken),
                    "capture_target" => await CaptureTargetAsync(arguments, cancellationToken),
                    "read_page" => await ReadPageAsync(arguments, cancellationToken),
                    "click" => await ClickAsync(arguments, cancellationToken),
                    "type_text" => await TypeTextAsync(arguments, cancellationToken),
                    "key" => await PressKeyAsync(arguments, cancellationToken),
                    "scroll" => await ScrollAsync(arguments, cancellationToken),
                    "navigate" => await NavigateAsync(arguments, cancellationToken),
                    "tab" => await TabAsync(arguments, cancellationToken),
                    "find" => await FindAsync(arguments, cancellationToken),
                    "list_windows" => ListWindows(arguments),
                    "focus_window" => await FocusWindowAsync(arguments, cancellationToken),
                    "open_app" => await OpenAppAsync(arguments, cancellationToken),
                    _ => $"MCP tool failed: unknown Browser Use tool: {alias.ToolName}"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"MCP tool failed: Browser Use: {ex.Message}";
            }
        }

        private async Task<string> OpenUrlAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string url = AgentToolHelpers.GetFirstStringArgument(arguments, "url", "uri", "address").Trim();
            ValidateUrl(url);
            bool newWindow = AgentToolHelpers.GetBoolArgument(arguments, "newWindow", false);

            if (TryGetBrowserWindow(out IntPtr browserWindow, requireBrowser: true))
            {
                FocusBrowserWindow(browserWindow);
                if (newWindow)
                {
                    SendShortcut(0x11, 0x4E); // Ctrl+N
                    await Task.Delay(300, cancellationToken);
                    IntPtr foreground = GetForegroundWindow();
                    GetWindowThreadProcessId(browserWindow, out uint originalProcessId);
                    GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
                    _browserWindow = foreground != IntPtr.Zero &&
                        IsWindowVisible(foreground) &&
                        TryGetProcessName(foregroundProcessId).Equals(TryGetProcessName(originalProcessId), StringComparison.OrdinalIgnoreCase)
                            ? foreground
                            : IntPtr.Zero;
                    browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
                    FocusBrowserWindow(browserWindow);
                }

                await NavigateAddressBarAsync(url, cancellationToken);
            }
            else
            {
                LaunchDefaultBrowser(url);
                browserWindow = await WaitForBrowserWindowAsync(cancellationToken);
            }

            await Task.Delay(350, cancellationToken);
            _lastCapture = null;
            return await BuildStatusResultAsync("MCP tool result: Browser Use opened URL.", browserWindow, includeUrl: true, cancellationToken);
        }

        private async Task<string> GetStatusAsync(CancellationToken cancellationToken)
        {
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            return await BuildStatusResultAsync(
                "MCP tool result: Browser Use status.",
                browserWindow,
                includeUrl: _controlledWindowIsBrowser,
                cancellationToken);
        }

        private string ListWindows(JsonElement arguments)
        {
            EnsureComputerUseEnabled();
            int maxResults = Math.Clamp(AgentToolHelpers.GetIntArgument(arguments, "maxResults", 40), 1, 100);
            List<WindowInfo> windows = EnumerateControllableWindows()
                .Take(maxResults)
                .ToList();
            if (windows.Count == 0)
            {
                return "MCP tool result: Computer Use found no controllable application windows.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("MCP tool result: Computer Use visible application windows.");
            foreach (WindowInfo window in windows)
            {
                builder.AppendLine($"- windowId: {FormatWindowId(window.Handle)}");
                builder.AppendLine($"  process: {window.ProcessName}");
                builder.AppendLine($"  title: {SanitizeWindowTitle(window.Title)}");
                builder.AppendLine($"  bounds: x={window.Bounds.Left}, y={window.Bounds.Top}, width={window.Bounds.Right - window.Bounds.Left}, height={window.Bounds.Bottom - window.Bounds.Top}");
            }

            builder.Append("Use mcp_browser_use_focus_window with a windowId, then choose accessibility refs or capture based on the task.");
            return builder.ToString();
        }

        private async Task<string> FocusWindowAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureComputerUseEnabled();
            string windowId = AgentToolHelpers.GetFirstStringArgument(arguments, "windowId", "window_id", "id").Trim();
            string title = AgentToolHelpers.GetFirstStringArgument(arguments, "title", "windowTitle", "window_title").Trim();
            string processName = AgentToolHelpers.GetFirstStringArgument(arguments, "process", "processName", "process_name").Trim();
            if (string.IsNullOrWhiteSpace(windowId) && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(processName))
            {
                throw new InvalidOperationException("focus_window requires windowId, title, or process.");
            }

            List<WindowInfo> windows = EnumerateControllableWindows();
            WindowInfo? selected = null;
            if (!string.IsNullOrWhiteSpace(windowId))
            {
                if (!TryParseWindowId(windowId, out IntPtr handle))
                {
                    throw new InvalidOperationException($"Invalid Computer Use windowId: {windowId}");
                }

                selected = windows.FirstOrDefault(item => item.Handle == handle);
            }
            else
            {
                IEnumerable<WindowInfo> matches = windows;
                if (!string.IsNullOrWhiteSpace(processName))
                {
                    matches = matches.Where(item => item.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    matches = matches.Where(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                }

                IntPtr foreground = GetForegroundWindow();
                selected = matches.FirstOrDefault(item => item.Handle == foreground) ?? matches.FirstOrDefault();
            }

            if (selected == null)
            {
                throw new InvalidOperationException("The requested application window is not open or controllable.");
            }

            SelectControlledWindow(selected);
            FocusBrowserWindow(selected.Handle);
            await Task.Delay(180, cancellationToken);
            return await BuildStatusResultAsync(
                "MCP tool result: Computer Use selected an application window.",
                selected.Handle,
                includeUrl: _controlledWindowIsBrowser,
                cancellationToken);
        }

        private async Task<string> OpenAppAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureComputerUseEnabled();
            string target = AgentToolHelpers.GetFirstStringArgument(arguments, "target", "path", "app", "application").Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("open_app requires a target.");
            }

            string processArguments = AgentToolHelpers.GetFirstStringArgument(arguments, "arguments", "args");
            string resolvedTarget = AgentWindowsApplicationResolver.Resolve(target);
            IntPtr foregroundBefore = GetForegroundWindow();
            Process? launchedProcess;
            try
            {
                launchedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = resolvedTarget,
                    Arguments = processArguments,
                    UseShellExecute = true
                });
            }
            catch (Win32Exception ex) when (!resolvedTarget.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The application alias '{target}' resolved to '{resolvedTarget}', but Windows could not launch it: {ex.Message}",
                    ex);
            }

            string expectedProcessName = Path.GetFileNameWithoutExtension(resolvedTarget);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            DateTimeOffset foregroundFallbackAt = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr handle = IntPtr.Zero;
                if (launchedProcess != null)
                {
                    try
                    {
                        launchedProcess.Refresh();
                        handle = launchedProcess.MainWindowHandle;
                    }
                    catch
                    {
                    }
                }

                if (handle == IntPtr.Zero && !string.IsNullOrWhiteSpace(expectedProcessName))
                {
                    handle = EnumerateControllableWindows()
                        .FirstOrDefault(item => item.ProcessName.Equals(expectedProcessName, StringComparison.OrdinalIgnoreCase))
                        ?.Handle ?? IntPtr.Zero;
                }

                if (handle == IntPtr.Zero && DateTimeOffset.UtcNow >= foregroundFallbackAt)
                {
                    IntPtr foreground = GetForegroundWindow();
                    if (foreground != IntPtr.Zero && foreground != foregroundBefore && IsWindowVisible(foreground))
                    {
                        GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
                        if (foregroundProcessId != (uint)Environment.ProcessId)
                        {
                            handle = foreground;
                        }
                    }
                }

                if (handle != IntPtr.Zero && TryCreateWindowInfo(handle, out WindowInfo window))
                {
                    try
                    {
                        FocusBrowserWindow(handle);
                    }
                    catch
                    {
                        await Task.Delay(150, cancellationToken);
                        continue;
                    }

                    await Task.Delay(300, cancellationToken);
                    if (!TryCreateWindowInfo(handle, out WindowInfo stableWindow))
                    {
                        await Task.Delay(150, cancellationToken);
                        continue;
                    }

                    SelectControlledWindow(stableWindow);
                    string status = await BuildStatusResultAsync(
                        "MCP tool result: Computer Use launched and selected an application window.",
                        handle,
                        includeUrl: _controlledWindowIsBrowser,
                        cancellationToken);
                    try
                    {
                        string snapshot = await CaptureInitialAccessibilitySnapshotAsync(handle, cancellationToken);
                        return status + "\n" + snapshot;
                    }
                    catch (Exception ex)
                    {
                        if (!TryCreateWindowInfo(handle, out _))
                        {
                            await Task.Delay(150, cancellationToken);
                            continue;
                        }

                        return status +
                            $"\n(Warning: Initial accessibility snapshot failed: {ex.Message})\n" +
                            "next_action: mcp_browser_use_capture remains available if screenshot context would help.";
                    }
                }

                await Task.Delay(150, cancellationToken);
            }

            throw new InvalidOperationException($"The application was launched but no visible window was found: {target}");
        }

        private async Task<string> CaptureInitialAccessibilitySnapshotAsync(
            IntPtr window,
            CancellationToken cancellationToken)
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateWindowInfo(window, out _))
                {
                    throw new InvalidOperationException("The launched application window closed before its accessibility tree became available.");
                }

                try
                {
                    return _accessibility.CaptureSnapshot(window, DefaultAccessibilityNodeLimit);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await Task.Delay(350, cancellationToken);
            }

            throw new InvalidOperationException(
                "The application window opened, but its accessibility provider was not ready.",
                lastError);
        }

        private async Task<string> CaptureAsync(CancellationToken cancellationToken)
        {
            if (!_settingsProvider().BrowserUseCaptureEnabled)
            {
                throw new InvalidOperationException("Browser image capture is disabled in Browser Use settings.");
            }

            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            FocusBrowserWindow(browserWindow);
            await Task.Delay(200, cancellationToken);
            _lastCapture = await _captureService.CaptureAsync(browserWindow, cancellationToken);

            return
                "MCP tool result: Browser Use captured the current controlled window.\n" +
                $"window_id: {FormatWindowId(browserWindow)}\n" +
                $"window_title: {SanitizeWindowTitle(GetWindowTitle(browserWindow))}\n" +
                $"image_dimensions: {_lastCapture.ImageWidth}x{_lastCapture.ImageHeight}\n" +
                $"controlled_window_dimensions: {_lastCapture.WindowWidth}x{_lastCapture.WindowHeight}\n" +
                "next_action: The captured image is automatically attached. You can use coordinates from this image or accessibility refs, and interaction tools will return a fresh accessibility snapshot.";
        }

        private async Task<string> SnapshotAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            int maxNodes = AgentToolHelpers.GetIntArgument(arguments, "maxNodes", DefaultAccessibilityNodeLimit);
            string snapshot = _accessibility.CaptureSnapshot(browserWindow, maxNodes);
            bool includeScreenshot = AgentToolHelpers.GetBoolArgument(arguments, "includeScreenshot", false);
            if (!includeScreenshot)
            {
                return snapshot;
            }

            if (!_settingsProvider().BrowserUseCaptureEnabled)
            {
                return snapshot + "\n(Warning: Screenshot was requested but Browser Use capture is disabled.)";
            }

            return snapshot + "\n\n" + await CaptureAsync(cancellationToken);
        }

        private async Task<string> CaptureTargetAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!_settingsProvider().BrowserUseCaptureEnabled)
            {
                throw new InvalidOperationException("Browser image capture is disabled in Browser Use settings.");
            }

            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            AgentBrowserCapture? capture = _lastCapture != null && _lastCapture.Window == browserWindow
                ? _lastCapture
                : null;
            if (capture == null)
            {
                throw new InvalidOperationException(
                    "capture_target requires a prior explicit mcp_browser_use_capture result for the current controlled window.");
            }

            if (!TryGetInt(arguments, "x", out int x) || !TryGetInt(arguments, "y", out int y))
            {
                throw new InvalidOperationException("capture_target requires integer x and y screenshot coordinates.");
            }

            await _captureService.MarkTargetAsync(capture, x, y, cancellationToken);
            return
                "MCP tool result: Browser Use marked the selected target with a red plus and attached the verification image.\n" +
                $"window_id: {FormatWindowId(browserWindow)}\n" +
                $"target_screenshot_coordinates: ({x}, {y})\n" +
                $"image_dimensions: {capture.ImageWidth}x{capture.ImageHeight}\n" +
                "interaction_performed: false\n" +
                "next_action: Inspect the attached image. If the red plus is centered on the intended target, call mcp_browser_use_click with the same x and y in screenshot coordinateSpace. Otherwise call mcp_browser_use_capture_target again with corrected coordinates.";
        }

        private async Task<string> ReadPageAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
            FocusBrowserWindow(browserWindow);
            string url = await CopyCurrentUrlAsync(cancellationToken);
            SendVirtualKey(0x1B); // Escape restores the page focus in common browsers.
            await Task.Delay(100, cancellationToken);

            string text = await CopyFocusedSelectionAsync(selectAll: true, cancellationToken);
            if (string.Equals(text.Trim(), url.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                SendVirtualKey(0x75); // F6: cycle focus from browser chrome to page.
                await Task.Delay(100, cancellationToken);
                text = await CopyFocusedSelectionAsync(selectAll: true, cancellationToken);
            }

            SendVirtualKey(0x1B);
            int requestedLimit = AgentToolHelpers.GetIntArgument(arguments, "maxCharacters", 30_000);
            int limit = Math.Clamp(requestedLimit, 1_000, MaxPageTextLength);
            bool truncated = text.Length > limit;
            if (truncated)
            {
                text = text[..limit];
            }

            var builder = new StringBuilder();
            builder.AppendLine("MCP tool result: Browser Use page text.");
            builder.AppendLine($"title: {GetWindowTitle(browserWindow)}");
            builder.AppendLine($"url: {url}");
            builder.AppendLine($"text_length: {text.Length}");
            builder.AppendLine("page_text:");
            builder.Append(text);
            if (truncated)
            {
                builder.AppendLine();
                builder.Append("[truncated]");
            }

            return builder.ToString();
        }

        private async Task<string> ClickAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureInteractionAllowed();
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            string elementRef = AgentToolHelpers.GetFirstStringArgument(arguments, "ref", "elementRef", "element_ref").Trim();
            bool hasX = TryGetInt(arguments, "x", out int x);
            bool hasY = TryGetInt(arguments, "y", out int y);
            bool hasCoordinates = hasX && hasY;
            if (string.IsNullOrWhiteSpace(elementRef) && !hasCoordinates)
            {
                throw new InvalidOperationException("click requires a snapshot ref or integer x and y coordinates.");
            }

            if (!GetWindowRect(browserWindow, out Rect rect))
            {
                throw new InvalidOperationException("Cannot read the controlled window bounds.");
            }

            int windowWidth = rect.Right - rect.Left;
            int windowHeight = rect.Bottom - rect.Top;
            int screenX;
            int screenY;
            string coordinateSpace;
            if (!string.IsNullOrWhiteSpace(elementRef))
            {
                if (!_accessibility.TryResolveRef(elementRef, browserWindow, out AgentBrowserAccessibilityService.AccessibilityTarget target))
                {
                    throw new InvalidOperationException($"Accessibility ref '{elementRef}' is stale or unavailable. Call mcp_browser_use_snapshot and retry with a current ref.");
                }

                coordinateSpace = "ref";
                screenX = target.ScreenX;
                screenY = target.ScreenY;
                x = screenX - rect.Left;
                y = screenY - rect.Top;
            }
            else
            {
                coordinateSpace = AgentToolHelpers.GetFirstStringArgument(arguments, "coordinateSpace", "coordinate_space")
                    .Trim()
                    .ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(coordinateSpace))
                {
                    coordinateSpace = "screenshot";
                }

                if (coordinateSpace == "screenshot")
                {
                    AgentBrowserCapture? capture = _lastCapture != null && _lastCapture.Window == browserWindow
                        ? _lastCapture
                        : null;
                    if (capture == null)
                    {
                        throw new InvalidOperationException("Screenshot coordinates require a prior explicit mcp_browser_use_capture result. Prefer a stable accessibility ref when available.");
                    }

                    x = Math.Clamp(x, 0, capture.ImageWidth - 1);
                    y = Math.Clamp(y, 0, capture.ImageHeight - 1);

                    int origX = x - capture.PaddingLeft;
                    int origY = y - capture.PaddingTop;

                    origX = Math.Clamp(origX, 0, capture.OriginalImageWidth - 1);
                    origY = Math.Clamp(origY, 0, capture.OriginalImageHeight - 1);

                    screenX = rect.Left + Math.Clamp(
                        (int)Math.Round((origX + 0.5) * (windowWidth / (double)capture.OriginalImageWidth)),
                        0,
                        windowWidth - 1);
                    screenY = rect.Top + Math.Clamp(
                        (int)Math.Round((origY + 0.5) * (windowHeight / (double)capture.OriginalImageHeight)),
                        0,
                        windowHeight - 1);
                }
                else if (coordinateSpace == "window")
                {
                    x = Math.Clamp(x, 0, windowWidth - 1);
                    y = Math.Clamp(y, 0, windowHeight - 1);

                    screenX = rect.Left + x;
                    screenY = rect.Top + y;
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported coordinate space: {coordinateSpace}");
                }
            }

            string button = AgentToolHelpers.GetFirstStringArgument(arguments, "button").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(button))
            {
                button = "left";
            }

            int clickCount = Math.Clamp(AgentToolHelpers.GetIntArgument(arguments, "clickCount", 1), 1, 3);
            FocusBrowserWindow(browserWindow);
            if (GetForegroundWindow() != browserWindow)
            {
                throw new InvalidOperationException("Windows did not activate the target window, so the click was cancelled to avoid clicking another application.");
            }

            _inputService.SetCursorPosition(screenX, screenY);
            await Task.Delay(75, cancellationToken);
            _inputService.VerifyCursorPosition(screenX, screenY);

            for (int i = 0; i < clickCount; i++)
            {
                SendMouseClick(button);
                await Task.Delay(80, cancellationToken);
            }

            await Task.Delay(350, cancellationToken);
            string postActionContext = BuildPostActionContext(browserWindow);

            string targetDescription = coordinateSpace == "ref" ? $"ref {elementRef}" : $"{coordinateSpace} ({x}, {y})";
            return $"MCP tool result: Browser Use clicked {button} at {targetDescription}, mapped to screen ({screenX}, {screenY})." + postActionContext;
        }

        private async Task<string> TypeTextAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureInteractionAllowed();
            string text = AgentToolHelpers.GetFirstStringArgument(arguments, "text", "value", "content");
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            FocusBrowserWindow(browserWindow);
            if (AgentToolHelpers.GetBoolArgument(arguments, "replace", false))
            {
                SendShortcut(0x11, 0x41); // Ctrl+A
            }

            SendUnicodeText(text);
            if (AgentToolHelpers.GetBoolArgument(arguments, "pressEnter", false))
            {
                SendVirtualKey(0x0D);
            }

            await Task.Delay(150, cancellationToken);
            return $"MCP tool result: Browser Use typed {text.Length:N0} characters." +
                BuildPostActionContext(browserWindow);
        }

        private async Task<string> PressKeyAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureInteractionAllowed();
            string key = AgentToolHelpers.GetFirstStringArgument(arguments, "key").Trim();
            if (!TryMapKey(key, out ushort virtualKey))
            {
                throw new InvalidOperationException($"Unsupported browser key: {key}");
            }

            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            FocusBrowserWindow(browserWindow);
            SendKeyWithModifiers(
                virtualKey,
                AgentToolHelpers.GetBoolArgument(arguments, "ctrl", false),
                AgentToolHelpers.GetBoolArgument(arguments, "alt", false),
                AgentToolHelpers.GetBoolArgument(arguments, "shift", false));
            await Task.Delay(120, cancellationToken);
            return $"MCP tool result: Browser Use pressed {key}." +
                BuildPostActionContext(browserWindow);
        }

        private async Task<string> ScrollAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureInteractionAllowed();
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken);
            FocusBrowserWindow(browserWindow);
            if (GetWindowRect(browserWindow, out Rect rect))
            {
                _inputService.PositionCursor(
                    rect.Left + ((rect.Right - rect.Left) / 2),
                    rect.Top + ((rect.Bottom - rect.Top) / 2));
            }

            int deltaY = AgentToolHelpers.GetIntArgument(arguments, "deltaY", -720);
            int deltaX = AgentToolHelpers.GetIntArgument(arguments, "deltaX", 0);
            if (deltaY != 0)
            {
                SendMouseWheel(deltaY, horizontal: false);
            }

            if (deltaX != 0)
            {
                SendVirtualKeyDown(0x10);
                SendMouseWheel(deltaX, horizontal: true);
                SendVirtualKeyUp(0x10);
            }

            await Task.Delay(150, cancellationToken);
            return $"MCP tool result: Browser Use scrolled (deltaY: {deltaY}, deltaX: {deltaX})." +
                BuildPostActionContext(browserWindow);
        }

        private async Task<string> NavigateAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string action = AgentToolHelpers.GetFirstStringArgument(arguments, "action", "command").Trim().ToLowerInvariant();
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
            FocusBrowserWindow(browserWindow);
            switch (action)
            {
                case "back": SendShortcut(0x12, 0x25); break; // Alt+Left
                case "forward": SendShortcut(0x12, 0x27); break; // Alt+Right
                case "refresh": SendShortcut(0x11, 0x52); break; // Ctrl+R
                case "stop": SendVirtualKey(0x1B); break;
                case "home": SendShortcut(0x12, 0x24); break; // Alt+Home
                default: throw new InvalidOperationException($"Unsupported navigation action: {action}");
            }

            await Task.Delay(200, cancellationToken);
            return $"MCP tool result: Browser Use navigation action completed: {action}." +
                BuildPostActionContext(browserWindow);
        }

        private async Task<string> TabAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string action = AgentToolHelpers.GetFirstStringArgument(arguments, "action", "command").Trim().ToLowerInvariant();
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
            FocusBrowserWindow(browserWindow);
            switch (action)
            {
                case "new": SendShortcut(0x11, 0x54); break; // Ctrl+T
                case "close": SendShortcut(0x11, 0x57); break; // Ctrl+W
                case "next": SendKeyWithModifiers(0x09, ctrl: true, alt: false, shift: false); break;
                case "previous": SendKeyWithModifiers(0x09, ctrl: true, alt: false, shift: true); break;
                case "reopen": SendKeyWithModifiers(0x54, ctrl: true, alt: false, shift: true); break;
                default: throw new InvalidOperationException($"Unsupported tab action: {action}");
            }

            await Task.Delay(200, cancellationToken);
            return $"MCP tool result: Browser Use tab action completed: {action}." +
                BuildPostActionContext(browserWindow);
        }

        private async Task<string> FindAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            EnsureInteractionAllowed();
            string text = AgentToolHelpers.GetFirstStringArgument(arguments, "text", "query", "find");
            if (string.IsNullOrEmpty(text))
            {
                throw new InvalidOperationException("find requires non-empty text.");
            }

            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
            FocusBrowserWindow(browserWindow);
            SendShortcut(0x11, 0x46); // Ctrl+F
            await Task.Delay(100, cancellationToken);
            SendShortcut(0x11, 0x41);
            SendUnicodeText(text);
            if (AgentToolHelpers.GetBoolArgument(arguments, "next", false))
            {
                SendVirtualKey(0x0D);
            }

            await Task.Delay(120, cancellationToken);
            return $"MCP tool result: Browser Use searched the page for: {text}" +
                BuildPostActionContext(browserWindow);
        }

        private string BuildPostActionContext(IntPtr browserWindow)
        {
            _lastCapture = null;
            string context;
            try
            {
                context = "\n" + _accessibility.CaptureSnapshot(browserWindow, DefaultAccessibilityNodeLimit);
            }
            catch (Exception ex)
            {
                context = $"\n(Warning: Accessibility snapshot after interaction failed: {ex.Message})\n" +
                    "next_action: mcp_browser_use_capture remains available if screenshot context would help.";
            }

            return context;
        }

        private static List<WindowInfo> EnumerateControllableWindows()
        {
            var windows = new List<WindowInfo>();
            EnumWindows((handle, _) =>
            {
                if (TryCreateWindowInfo(handle, out WindowInfo window))
                {
                    windows.Add(window);
                }

                return true;
            }, IntPtr.Zero);

            IntPtr foreground = GetForegroundWindow();
            return windows
                .OrderByDescending(item => item.Handle == foreground)
                .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool TryCreateWindowInfo(IntPtr handle, out WindowInfo window)
        {
            window = new WindowInfo();
            if (handle == IntPtr.Zero || !IsWindowVisible(handle))
            {
                return false;
            }

            string title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId || !GetWindowRect(handle, out Rect bounds))
            {
                return false;
            }

            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            {
                return false;
            }

            window = new WindowInfo
            {
                Handle = handle,
                ProcessId = processId,
                ProcessName = TryGetProcessName(processId),
                Title = title,
                Bounds = bounds
            };
            return true;
        }

        private void SelectControlledWindow(WindowInfo window)
        {
            _browserWindow = window.Handle;
            _controlledWindowIsBrowser = IsDefaultBrowserProcess(window.ProcessName);
            _lastCapture = null;
        }

        private bool IsDefaultBrowserProcess(string processName)
        {
            string defaultBrowser = GetDefaultBrowserProcessName();
            return !string.IsNullOrWhiteSpace(defaultBrowser) &&
                processName.Equals(defaultBrowser, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatWindowId(IntPtr handle)
        {
            return $"0x{handle.ToInt64():X}";
        }

        private static bool TryParseWindowId(string value, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            string text = value.Trim();
            try
            {
                long numeric = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(text[2..], 16)
                    : Convert.ToInt64(text, System.Globalization.CultureInfo.InvariantCulture);
                handle = new IntPtr(numeric);
                return handle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeWindowTitle(string title)
        {
            return title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private async Task<IntPtr> EnsureBrowserWindowAsync(
            CancellationToken cancellationToken,
            bool requireBrowser = false)
        {
            if (TryGetBrowserWindow(out IntPtr browserWindow, requireBrowser))
            {
                return browserWindow;
            }

            if (!requireBrowser && _browserWindow != IntPtr.Zero && !_controlledWindowIsBrowser)
            {
                throw new InvalidOperationException(
                    "The selected Computer Use application window is no longer available. Use list_windows, focus_window, or open_app to select it again.");
            }

            LaunchDefaultBrowser("about:blank");
            return await WaitForBrowserWindowAsync(cancellationToken);
        }

        private bool TryGetBrowserWindow(out IntPtr browserWindow, bool requireBrowser = false)
        {
            if (_browserWindow != IntPtr.Zero &&
                IsWindow(_browserWindow) &&
                IsWindowVisible(_browserWindow) &&
                (!requireBrowser || _controlledWindowIsBrowser))
            {
                browserWindow = _browserWindow;
                return true;
            }

            if (!requireBrowser && _browserWindow != IntPtr.Zero && !_controlledWindowIsBrowser)
            {
                browserWindow = IntPtr.Zero;
                return false;
            }

            string processName = GetDefaultBrowserProcessName();
            if (!string.IsNullOrWhiteSpace(processName))
            {
                List<Process> processes = Process.GetProcessesByName(processName)
                    .Where(item => item.MainWindowHandle != IntPtr.Zero && IsWindowVisible(item.MainWindowHandle))
                    .ToList();
                IntPtr foreground = GetForegroundWindow();
                Process? process = processes.FirstOrDefault(item => item.MainWindowHandle == foreground) ??
                    processes.FirstOrDefault();
                if (process != null)
                {
                    _browserWindow = process.MainWindowHandle;
                    _controlledWindowIsBrowser = true;
                    browserWindow = _browserWindow;
                    return true;
                }
            }

            browserWindow = IntPtr.Zero;
            return false;
        }

        private async Task<IntPtr> WaitForBrowserWindowAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetBrowserWindow(out IntPtr browserWindow, requireBrowser: true))
                {
                    _browserWindow = browserWindow;
                    _controlledWindowIsBrowser = true;
                    return browserWindow;
                }

                IntPtr foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero && IsWindowVisible(foreground))
                {
                    GetWindowThreadProcessId(foreground, out uint processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        string defaultProcess = GetDefaultBrowserProcessName();
                        string foregroundProcess = TryGetProcessName(processId);
                        if (string.IsNullOrWhiteSpace(defaultProcess) ||
                            foregroundProcess.Equals(defaultProcess, StringComparison.OrdinalIgnoreCase))
                        {
                            _browserWindow = foreground;
                            _controlledWindowIsBrowser = true;
                            return foreground;
                        }
                    }
                }

                await Task.Delay(150, cancellationToken);
            }

            throw new InvalidOperationException("The Windows default browser window could not be found after launch.");
        }

        private void LaunchDefaultBrowser(string url)
        {
            string executable = GetDefaultBrowserExecutablePath();
            if (!string.IsNullOrWhiteSpace(executable))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = url,
                        UseShellExecute = true
                    });
                    return;
                }
                catch
                {
                    // Fall back to general shell launch if executable launch fails
                }
            }

            string launchUrl = url;
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                launchUrl = "https://www.google.com";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = launchUrl,
                UseShellExecute = true
            });
        }

        private async Task NavigateAddressBarAsync(string url, CancellationToken cancellationToken)
        {
            SendShortcut(0x11, 0x4C); // Ctrl+L
            await Task.Delay(80, cancellationToken);
            SendUnicodeText(url);
            SendVirtualKey(0x0D);
        }

        private async Task<string> CopyCurrentUrlAsync(CancellationToken cancellationToken)
        {
            SendShortcut(0x11, 0x4C);
            await Task.Delay(80, cancellationToken);
            return await CopyFocusedSelectionAsync(selectAll: false, cancellationToken);
        }

        private async Task<string> CopyFocusedSelectionAsync(bool selectAll, CancellationToken cancellationToken)
        {
            if (selectAll)
            {
                SendShortcut(0x11, 0x41);
                await Task.Delay(80, cancellationToken);
            }

            uint sequence = _inputService.GetClipboardSequence();
            SendShortcut(0x11, 0x43);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_inputService.GetClipboardSequence() != sequence && TryReadClipboardText(out string text))
                {
                    return text;
                }

                await Task.Delay(40, cancellationToken);
            }

            return string.Empty;
        }

        private async Task<string> BuildStatusResultAsync(string header, IntPtr browserWindow, bool includeUrl, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.AppendLine(header);
            builder.AppendLine($"window_id: {FormatWindowId(browserWindow)}");
            builder.AppendLine($"window_type: {(_controlledWindowIsBrowser ? "browser" : "application")}");
            builder.AppendLine($"title: {GetWindowTitle(browserWindow)}");
            GetWindowThreadProcessId(browserWindow, out uint processId);
            builder.AppendLine($"process: {TryGetProcessName(processId)}");
            if (GetWindowRect(browserWindow, out Rect rect))
            {
                builder.AppendLine($"window_bounds: x={rect.Left}, y={rect.Top}, width={rect.Right - rect.Left}, height={rect.Bottom - rect.Top}");
                builder.AppendLine("click_coordinates: relative to the controlled window top-left when coordinateSpace is window");
            }

            if (includeUrl)
            {
                FocusBrowserWindow(browserWindow);
                string url = await CopyCurrentUrlAsync(cancellationToken);
                SendVirtualKey(0x1B);
                builder.AppendLine($"url: {url}");
            }

            return builder.ToString().TrimEnd();
        }

        private void EnsureInteractionAllowed()
        {
            if (!_settingsProvider().BrowserUseAllowInteraction)
            {
                throw new InvalidOperationException("Click, typing, key, and scroll actions are disabled in Browser Use settings.");
            }
        }

        private void EnsureComputerUseEnabled()
        {
            if (!_settingsProvider().BrowserUseComputerUseEnabled)
            {
                throw new InvalidOperationException("Computer Use is disabled in Browser Use settings.");
            }
        }

        private void FocusBrowserWindow(IntPtr browserWindow)
        {
            _inputService.FocusWindow(browserWindow);
        }

        private string GetDefaultBrowserExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(_defaultBrowserExecutablePath))
            {
                return _defaultBrowserExecutablePath;
            }

            try
            {
                using RegistryKey? userChoice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
                string progId = userChoice?.GetValue("ProgId") as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(progId))
                {
                    return string.Empty;
                }

                using RegistryKey? commandKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                string command = commandKey?.GetValue(null) as string ?? string.Empty;
                _defaultBrowserExecutablePath = ExtractExecutablePath(command);
            }
            catch
            {
                _defaultBrowserExecutablePath = string.Empty;
            }

            return _defaultBrowserExecutablePath;
        }

        private string GetDefaultBrowserProcessName()
        {
            if (!string.IsNullOrWhiteSpace(_defaultBrowserProcessName))
            {
                return _defaultBrowserProcessName;
            }

            string executable = GetDefaultBrowserExecutablePath();
            if (!string.IsNullOrWhiteSpace(executable))
            {
                _defaultBrowserProcessName = System.IO.Path.GetFileNameWithoutExtension(executable);
            }

            return _defaultBrowserProcessName;
        }

        private static string ExtractExecutablePath(string command)
        {
            string trimmed = command.Trim();
            if (trimmed.StartsWith('"'))
            {
                int endQuote = trimmed.IndexOf('"', 1);
                return endQuote > 1 ? trimmed[1..endQuote] : string.Empty;
            }

            int exeEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd >= 0 ? trimmed[..(exeEnd + 4)].Trim() : string.Empty;
        }

        private static string TryGetProcessName(uint processId)
        {
            try
            {
                return Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetWindowTitle(IntPtr browserWindow)
        {
            int length = GetWindowTextLength(browserWindow);
            var title = new StringBuilder(Math.Max(length + 1, 256));
            GetWindowText(browserWindow, title, title.Capacity);
            return title.ToString();
        }

        private static void ValidateUrl(string url)
        {
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            {
                throw new InvalidOperationException("Browser Use URL must use http, https, or file, or be about:blank.");
            }
        }

        private static bool TryGetInt(JsonElement arguments, string propertyName, out int value)
        {
            value = 0;
            return arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty(propertyName, out JsonElement element) &&
                element.TryGetInt32(out value);
        }

        private bool TryMapKey(string key, out ushort virtualKey)
        {
            return _inputService.TryMapKey(key, out virtualKey);
        }

        private void SendUnicodeText(string text)
        {
            _inputService.SendUnicodeText(text);
        }

        private void SendShortcut(ushort modifier, ushort key)
        {
            _inputService.SendShortcut(modifier, key);
        }

        private void SendKeyWithModifiers(ushort key, bool ctrl, bool alt, bool shift)
        {
            _inputService.SendKeyWithModifiers(key, ctrl, alt, shift);
        }

        private void SendVirtualKey(ushort key)
        {
            _inputService.SendVirtualKey(key);
        }

        private void SendVirtualKeyDown(ushort key)
        {
            _inputService.SendVirtualKeyDown(key);
        }

        private void SendVirtualKeyUp(ushort key)
        {
            _inputService.SendVirtualKeyUp(key);
        }

        private void SendMouseClick(string button)
        {
            _inputService.SendMouseClick(button);
        }

        private void SendMouseWheel(int delta, bool horizontal)
        {
            _ = horizontal;
            _inputService.SendMouseWheel(delta);
        }

        private bool TryReadClipboardText(out string text)
        {
            return _inputService.TryReadClipboardText(out text);
        }

        private sealed class WindowInfo
        {
            public IntPtr Handle { get; set; }
            public uint ProcessId { get; set; }
            public string ProcessName { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public Rect Bounds { get; set; }
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr window);

    }
}
