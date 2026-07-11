using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpBrowserUseTool
    {
        private const string BuiltInBrowserUseId = "builtin-browser-use";
        private const string BuiltInBrowserUseName = "Browser Use & Computer Use";
        private const int MaxPageTextLength = 60_000;
        private const int DefaultAccessibilityNodeLimit = 180;
        private const int MaxCaptureDimension = 1024;
        private const uint InputKeyboard = 1;
        private const uint InputMouse = 0;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventMiddleDown = 0x0020;
        private const uint MouseEventMiddleUp = 0x0040;
        private const uint MouseEventWheel = 0x0800;
        private const uint CfUnicodeText = 13;
        private const uint Srccopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint DibRgbColors = 0;
        private const int Halftone = 4;

        private readonly Func<EditorSettings> _settingsProvider;
        private readonly Action<LlmMessageAttachment>? _addImageAttachment;
        private readonly AgentBrowserAccessibilityService _accessibility = new();
        private IntPtr _browserWindow;
        private bool _controlledWindowIsBrowser = true;
        private string _defaultBrowserProcessName = string.Empty;
        private string _defaultBrowserExecutablePath = string.Empty;
        private BrowserCapture? _lastCapture;

        public AgentMcpBrowserUseTool(Func<EditorSettings> settingsProvider, Action<LlmMessageAttachment>? addImageAttachment = null)
        {
            _settingsProvider = settingsProvider;
            _addImageAttachment = addImageAttachment;
        }

        public string ServerId => BuiltInBrowserUseId;

        public string ServerName => BuiltInBrowserUseName;

        public bool IsServerName(string serverName)
        {
            return serverName.Equals(BuiltInBrowserUseName, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsServerId(string serverId)
        {
            return serverId.Equals(BuiltInBrowserUseId, StringComparison.OrdinalIgnoreCase);
        }

        public bool CanHandleAlias(AgentMcpToolAlias alias)
        {
            return alias.ServerId.Equals(BuiltInBrowserUseId, StringComparison.OrdinalIgnoreCase);
        }

        public AgentMcpItem CreateMenuItem(bool isSelected, Func<string, string, string> getString)
        {
            return new AgentMcpItem
            {
                Name = BuiltInBrowserUseName,
                Endpoint = "windows-default-browser",
                Detail = getString("AgentMcpBrowserUseDetail", "내장 플러그인 - Windows 기본 브라우저 조작"),
                IsSelected = isSelected,
                IsBuiltIn = true,
                CanEdit = false,
                CanDelete = false
            };
        }

        public IReadOnlyList<AgentMcpToolAlias> CreateAliases()
        {
            var aliases = new List<AgentMcpToolAlias>
            {
                CreateAlias("mcp_browser_use_open_url", "open_url", "Open an http, https, file, or about:blank URL in the Windows default browser.", """
                {"type":"object","properties":{"url":{"type":"string","description":"Absolute URL to open."},"newWindow":{"type":"boolean","default":false}},"required":["url"]}
                """),
                CreateAlias("mcp_browser_use_status", "status", "Get the controlled default browser window title, process, bounds, and current URL.", """
                {"type":"object","properties":{}}
                """),
                CreateAlias("mcp_browser_use_snapshot", "snapshot", "Return a concise accessibility tree for the controlled browser window. Every emitted element has a stable ref that can be passed to mcp_browser_use_click. Set includeScreenshot only when visual context is needed.", """
                {"type":"object","properties":{"maxNodes":{"type":"integer","minimum":20,"maximum":500,"default":180},"includeScreenshot":{"type":"boolean","default":false}}}
                """),
                CreateAlias("mcp_browser_use_read_page", "read_page", "Copy and return selectable text from the current page, together with the current URL and window title. This is browser-agnostic and may not expose canvas or protected page content.", """
                {"type":"object","properties":{"maxCharacters":{"type":"integer","minimum":1000,"maximum":60000,"default":30000}}}
                """),
                CreateAlias("mcp_browser_use_click", "click", "Click an element by stable ref from mcp_browser_use_snapshot (preferred), or by coordinates from an existing explicit capture. Returns a fresh accessibility snapshot.", """
                {"type":"object","properties":{"ref":{"type":"string","description":"Stable element ref returned by mcp_browser_use_snapshot."},"x":{"type":"integer","description":"X coordinate in the latest explicit capture image."},"y":{"type":"integer","description":"Y coordinate in the latest explicit capture image."},"coordinateSpace":{"type":"string","enum":["screenshot","window"],"default":"screenshot","description":"Use screenshot only for coordinates from a prior mcp_browser_use_capture. Window coordinates are relative to the unscaled browser window."},"button":{"type":"string","enum":["left","right","middle"],"default":"left"},"clickCount":{"type":"integer","minimum":1,"maximum":3,"default":1}},"anyOf":[{"required":["ref"]},{"required":["x","y"]}]}
                """),
                CreateAlias("mcp_browser_use_type_text", "type_text", "Type Unicode text into the focused browser control. Can replace the current field selection and optionally press Enter.", """
                {"type":"object","properties":{"text":{"type":"string"},"replace":{"type":"boolean","default":false},"pressEnter":{"type":"boolean","default":false}},"required":["text"]}
                """),
                CreateAlias("mcp_browser_use_key", "key", "Press a browser key or shortcut. Supported keys: enter, escape, tab, space, backspace, delete, home, end, pageup, pagedown, up, down, left, right, f5, f6, f11; modifiers are optional.", """
                {"type":"object","properties":{"key":{"type":"string"},"ctrl":{"type":"boolean","default":false},"alt":{"type":"boolean","default":false},"shift":{"type":"boolean","default":false}},"required":["key"]}
                """),
                CreateAlias("mcp_browser_use_scroll", "scroll", "Scroll the current browser page vertically or horizontally.", """
                {"type":"object","properties":{"deltaY":{"type":"integer","description":"Vertical wheel delta; negative scrolls down, positive scrolls up.","default":-720},"deltaX":{"type":"integer","description":"Horizontal wheel delta; implemented with Shift+wheel.","default":0}}}
                """),
                CreateAlias("mcp_browser_use_navigate", "navigate", "Run a browser navigation command.", """
                {"type":"object","properties":{"action":{"type":"string","enum":["back","forward","refresh","stop","home"]}},"required":["action"]}
                """),
                CreateAlias("mcp_browser_use_tab", "tab", "Manage default browser tabs.", """
                {"type":"object","properties":{"action":{"type":"string","enum":["new","close","next","previous","reopen"]}},"required":["action"]}
                """),
                CreateAlias("mcp_browser_use_find", "find", "Open the browser find box and search for text on the current page.", """
                {"type":"object","properties":{"text":{"type":"string"},"next":{"type":"boolean","default":false}},"required":["text"]}
                """)
            };

            if (_settingsProvider().BrowserUseCaptureEnabled)
            {
                aliases.Insert(2, CreateAlias(
                    "mcp_browser_use_capture",
                    "capture",
                    "Capture the controlled browser window as a PNG for visual inspection. The captured image is automatically attached to the model context. Do NOT call read_image.",
                    """
                    {"type":"object","properties":{}}
                    """));
            }

            if (_settingsProvider().BrowserUseComputerUseEnabled)
            {
                aliases.Add(CreateAlias(
                    "mcp_browser_use_list_windows",
                    "list_windows",
                    "List visible application windows that can be selected for Computer Use. The TxtAIEditor host window is excluded.",
                    """
                    {"type":"object","properties":{"maxResults":{"type":"integer","minimum":1,"maximum":100,"default":40}}}
                    """));
                aliases.Add(CreateAlias(
                    "mcp_browser_use_focus_window",
                    "focus_window",
                    "Select and focus an open application window for capture, clicking, typing, keys, and scrolling. Use a windowId from list_windows when possible.",
                    """
                    {"type":"object","properties":{"windowId":{"type":"string","description":"Window id returned by list_windows."},"title":{"type":"string","description":"Case-insensitive title substring."},"process":{"type":"string","description":"Case-insensitive process name."}}}
                    """));
                aliases.Add(CreateAlias(
                    "mcp_browser_use_open_app",
                    "open_app",
                    "Launch a Windows program, application target, document, or registered URI and select its visible window for Computer Use.",
                    """
                    {"type":"object","properties":{"target":{"type":"string","description":"Executable name/path, document path, or registered URI."},"arguments":{"type":"string","description":"Optional program arguments."}},"required":["target"]}
                    """));
            }

            return aliases;
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

        private static AgentMcpToolAlias CreateAlias(string alias, string toolName, string description, string schema)
        {
            return new AgentMcpToolAlias
            {
                Alias = alias,
                ServerId = BuiltInBrowserUseId,
                ServerName = BuiltInBrowserUseName,
                ToolName = toolName,
                Description = description,
                InputSchemaJson = schema,
                IsBuiltIn = true
            };
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
            IntPtr foregroundBefore = GetForegroundWindow();
            Process? launchedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = processArguments,
                UseShellExecute = true
            });

            string expectedProcessName = Path.GetFileNameWithoutExtension(target);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
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

                if (handle == IntPtr.Zero)
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
                    SelectControlledWindow(window);
                    FocusBrowserWindow(handle);
                    await Task.Delay(300, cancellationToken);
                    string status = await BuildStatusResultAsync(
                        "MCP tool result: Computer Use launched and selected an application window.",
                        handle,
                        includeUrl: _controlledWindowIsBrowser,
                        cancellationToken);
                    try
                    {
                        string snapshot = _accessibility.CaptureSnapshot(handle, DefaultAccessibilityNodeLimit);
                        return status + "\n" + snapshot;
                    }
                    catch (Exception ex)
                    {
                        return status +
                            $"\n(Warning: Initial accessibility snapshot failed: {ex.Message})\n" +
                            "next_action: mcp_browser_use_capture remains available if screenshot context would help.";
                    }
                }

                await Task.Delay(150, cancellationToken);
            }

            throw new InvalidOperationException($"The application was launched but no visible window was found: {target}");
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
            if (!GetWindowRect(browserWindow, out Rect rect))
            {
                throw new InvalidOperationException("Cannot read the controlled window bounds.");
            }

            int windowWidth = rect.Right - rect.Left;
            int windowHeight = rect.Bottom - rect.Top;
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                throw new InvalidOperationException("The controlled window has invalid bounds.");
            }

            double scale = Math.Min(1.0, MaxCaptureDimension / (double)Math.Max(windowWidth, windowHeight));
            int imageWidth = Math.Max(1, (int)Math.Round(windowWidth * scale));
            int imageHeight = Math.Max(1, (int)Math.Round(windowHeight * scale));
            byte[] pixels = CaptureWindowPixels(rect, windowWidth, windowHeight, imageWidth, imageHeight);

            int targetWidth = MaxCaptureDimension;
            int targetHeight = MaxCaptureDimension;
            byte[] paddedPixels = new byte[targetWidth * targetHeight * 4];
            for (int i = 3; i < paddedPixels.Length; i += 4)
            {
                paddedPixels[i] = 255; // Alpha channel to make it opaque black
            }

            int offsetX = (targetWidth - imageWidth) / 2;
            int offsetY = (targetHeight - imageHeight) / 2;
            for (int y = 0; y < imageHeight; y++)
            {
                int srcOffset = y * imageWidth * 4;
                int destOffset = ((y + offsetY) * targetWidth + offsetX) * 4;
                Array.Copy(pixels, srcOffset, paddedPixels, destOffset, imageWidth * 4);
            }

            string captureDirectory = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "BrowserUse");
            Directory.CreateDirectory(captureDirectory);
            string imagePath = Path.Combine(
                captureDirectory,
                $"browser-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await SavePngAsync(imagePath, targetWidth, targetHeight, paddedPixels, cancellationToken);
            CleanupOldCaptures(captureDirectory);

            _lastCapture = new BrowserCapture
            {
                Window = browserWindow,
                ImagePath = imagePath,
                ImageWidth = targetWidth,
                ImageHeight = targetHeight,
                OriginalImageWidth = imageWidth,
                OriginalImageHeight = imageHeight,
                PaddingLeft = offsetX,
                PaddingTop = offsetY
            };

            await AttachCaptureImageAsync(imagePath, cancellationToken);

            return
                "MCP tool result: Browser Use captured the current controlled window.\n" +
                $"window_id: {FormatWindowId(browserWindow)}\n" +
                $"window_title: {SanitizeWindowTitle(GetWindowTitle(browserWindow))}\n" +
                $"image_dimensions: {targetWidth}x{targetHeight}\n" +
                $"controlled_window_dimensions: {windowWidth}x{windowHeight}\n" +
                "next_action: The captured image is automatically attached. You can use coordinates from this image or accessibility refs, and interaction tools will return a fresh accessibility snapshot.";
        }

        private async Task<string> SnapshotAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            IntPtr browserWindow = await EnsureBrowserWindowAsync(cancellationToken, requireBrowser: true);
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
                    BrowserCapture? capture = _lastCapture != null && _lastCapture.Window == browserWindow
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

            if (!SetCursorPos(screenX, screenY))
            {
                throw new InvalidOperationException($"Windows could not move the mouse cursor to ({screenX}, {screenY}).");
            }

            await Task.Delay(75, cancellationToken);
            if (!GetCursorPos(out Point cursor) || Math.Abs(cursor.X - screenX) > 1 || Math.Abs(cursor.Y - screenY) > 1)
            {
                throw new InvalidOperationException($"Mouse cursor coordinate verification failed. requested=({screenX}, {screenY}), actual=({cursor.X}, {cursor.Y}).");
            }

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
                SetCursorPos(rect.Left + ((rect.Right - rect.Left) / 2), rect.Top + ((rect.Bottom - rect.Top) / 2));
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

        private static byte[] CaptureWindowPixels(
            Rect windowBounds,
            int windowWidth,
            int windowHeight,
            int imageWidth,
            int imageHeight)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cannot acquire the Windows screen drawing surface.");
            }

            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                bitmap = CreateCompatibleBitmap(screenDc, imageWidth, imageHeight);
                if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Cannot allocate the browser capture surface.");
                }

                oldBitmap = SelectObject(memoryDc, bitmap);
                SetStretchBltMode(memoryDc, Halftone);
                if (!StretchBlt(
                    memoryDc,
                    0,
                    0,
                    imageWidth,
                    imageHeight,
                    screenDc,
                    windowBounds.Left,
                    windowBounds.Top,
                    windowWidth,
                    windowHeight,
                    Srccopy | CaptureBlt))
                {
                    throw new InvalidOperationException("Windows failed to capture the browser window.");
                }

                var bitmapInfo = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = imageWidth,
                        Height = -imageHeight,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                        SizeImage = (uint)(imageWidth * imageHeight * 4)
                    }
                };
                byte[] pixels = new byte[imageWidth * imageHeight * 4];
                int scanLines = GetDIBits(
                    memoryDc,
                    bitmap,
                    0,
                    (uint)imageHeight,
                    pixels,
                    ref bitmapInfo,
                    DibRgbColors);
                if (scanLines != imageHeight)
                {
                    throw new InvalidOperationException("Windows returned an incomplete browser capture.");
                }

                return pixels;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
                {
                    SelectObject(memoryDc, oldBitmap);
                }

                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }

                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }

                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static async Task SavePngAsync(
            string imagePath,
            int imageWidth,
            int imageHeight,
            byte[] pixels,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)imageWidth,
                (uint)imageHeight,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
            output.Seek(0);
            await using var source = output.AsStreamForRead();
            await using var destination = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await source.CopyToAsync(destination, cancellationToken);
        }

        private static void CleanupOldCaptures(string captureDirectory)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(captureDirectory, "browser-*.png")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .Skip(20))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
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

            uint sequence = GetClipboardSequenceNumber();
            SendShortcut(0x11, 0x43);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetClipboardSequenceNumber() != sequence && TryReadClipboardText(out string text))
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

        private static void FocusBrowserWindow(IntPtr browserWindow)
        {
            if (IsIconic(browserWindow))
            {
                ShowWindow(browserWindow, 9);
            }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == browserWindow)
            {
                return;
            }

            uint currentThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(browserWindow, out _);
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);
            bool attachedTarget = targetThread != 0 &&
                targetThread != currentThread &&
                AttachThreadInput(currentThread, targetThread, true);
            bool attachedForeground = foregroundThread != 0 &&
                foregroundThread != currentThread &&
                foregroundThread != targetThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(browserWindow);
                SetForegroundWindow(browserWindow);
                SetActiveWindow(browserWindow);
                SetFocus(browserWindow);
            }
            finally
            {
                if (attachedForeground)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }

                if (attachedTarget)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }

            Thread.Sleep(50);
            if (GetForegroundWindow() != browserWindow)
            {
                SetForegroundWindow(browserWindow);
                Thread.Sleep(50);
            }

            if (GetForegroundWindow() != browserWindow)
            {
                throw new InvalidOperationException("Windows did not activate the target window. Input was cancelled to avoid controlling another application.");
            }
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

        private static bool TryMapKey(string key, out ushort virtualKey)
        {
            if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            {
                virtualKey = char.ToUpperInvariant(key[0]);
                return true;
            }

            virtualKey = key.ToLowerInvariant() switch
            {
                "enter" => 0x0D,
                "escape" or "esc" => 0x1B,
                "tab" => 0x09,
                "space" => 0x20,
                "backspace" => 0x08,
                "delete" => 0x2E,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "up" => 0x26,
                "down" => 0x28,
                "left" => 0x25,
                "right" => 0x27,
                "f5" => 0x74,
                "f6" => 0x75,
                "f11" => 0x7A,
                _ => 0
            };
            return virtualKey != 0;
        }

        private static void SendUnicodeText(string text)
        {
            foreach (char character in text)
            {
                if (character == '\r')
                {
                    continue;
                }

                if (character == '\n')
                {
                    SendVirtualKey(0x0D);
                    continue;
                }

                if (character == '\t')
                {
                    SendVirtualKey(0x09);
                    continue;
                }

                var inputs = new[]
                {
                    CreateKeyboardInput(0, character, KeyEventUnicode),
                    CreateKeyboardInput(0, character, KeyEventUnicode | KeyEventKeyUp)
                };
                SendInputs(inputs);
            }
        }

        private static void SendShortcut(ushort modifier, ushort key)
        {
            SendVirtualKeyDown(modifier);
            SendVirtualKey(key);
            SendVirtualKeyUp(modifier);
        }

        private static void SendKeyWithModifiers(ushort key, bool ctrl, bool alt, bool shift)
        {
            if (ctrl) SendVirtualKeyDown(0x11);
            if (alt) SendVirtualKeyDown(0x12);
            if (shift) SendVirtualKeyDown(0x10);
            SendVirtualKey(key);
            if (shift) SendVirtualKeyUp(0x10);
            if (alt) SendVirtualKeyUp(0x12);
            if (ctrl) SendVirtualKeyUp(0x11);
        }

        private static void SendVirtualKey(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, 0), CreateKeyboardInput(key, 0, KeyEventKeyUp) });
        }

        private static void SendVirtualKeyDown(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, 0) });
        }

        private static void SendVirtualKeyUp(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, KeyEventKeyUp) });
        }

        private static Input CreateKeyboardInput(ushort virtualKey, ushort scanCode, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = scanCode,
                        Flags = flags
                    }
                }
            };
        }

        private static void SendMouseClick(string button)
        {
            (uint down, uint up) = button switch
            {
                "left" => (MouseEventLeftDown, MouseEventLeftUp),
                "right" => (MouseEventRightDown, MouseEventRightUp),
                "middle" => (MouseEventMiddleDown, MouseEventMiddleUp),
                _ => throw new InvalidOperationException($"Unsupported mouse button: {button}")
            };
            SendInputs(new[] { CreateMouseInput(down, 0) });
            Thread.Sleep(25);
            SendInputs(new[] { CreateMouseInput(up, 0) });
        }

        private static void SendMouseWheel(int delta, bool horizontal)
        {
            _ = horizontal;
            SendInputs(new[] { CreateMouseInput(MouseEventWheel, unchecked((uint)delta)) });
        }

        private static Input CreateMouseInput(uint flags, uint data)
        {
            return new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = data,
                        Flags = flags
                    }
                }
            };
        }

        private static void SendInputs(Input[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            {
                throw new InvalidOperationException("Windows rejected browser input automation.");
            }
        }

        private static bool TryReadClipboardText(out string text)
        {
            text = string.Empty;
            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                IntPtr handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                    return true;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        private sealed class BrowserCapture
        {
            public IntPtr Window { get; set; }
            public string ImagePath { get; set; } = string.Empty;
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
            public int OriginalImageWidth { get; set; }
            public int OriginalImageHeight { get; set; }
            public int PaddingLeft { get; set; }
            public int PaddingTop { get; set; }
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPixelsPerMeter;
            public int YPixelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(
            IntPtr destinationDeviceContext,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            IntPtr sourceDeviceContext,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            uint rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr deviceContext,
            IntPtr bitmap,
            uint startScan,
            uint scanLineCount,
            [Out] byte[] bits,
            ref BitmapInfo bitmapInfo,
            uint usage);

        private async Task AttachCaptureImageAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (_addImageAttachment == null || !File.Exists(imagePath))
            {
                return;
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                var attachment = new LlmMessageAttachment
                {
                    DisplayName = Path.GetFileName(imagePath),
                    MimeType = "image/png",
                    Base64Data = Convert.ToBase64String(bytes),
                    Width = MaxCaptureDimension,
                    Height = MaxCaptureDimension,
                    EstimatedTokens = EstimateImageTokens(MaxCaptureDimension, MaxCaptureDimension)
                };
                _addImageAttachment(attachment);
            }
            catch
            {
                // Ignore attachment errors to prevent tool failure
            }
        }

        private static int EstimateImageTokens(int width, int height)
        {
            int tilesWide = Math.Max(1, (int)Math.Ceiling(width / 512.0));
            int tilesHigh = Math.Max(1, (int)Math.Ceiling(height / 512.0));
            return 85 + (tilesWide * tilesHigh * 170);
        }
    }
}
