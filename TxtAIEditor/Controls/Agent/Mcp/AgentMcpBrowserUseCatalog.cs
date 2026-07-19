using System;
using System.Collections.Generic;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal static class AgentMcpBrowserUseCatalog
    {
        public const string ServerId = "builtin-browser-use";
        public const string ServerName = "Browser Use & Computer Use";

        public static AgentMcpItem CreateMenuItem(bool isSelected, Func<string, string, string> getString)
        {
            return new AgentMcpItem
            {
                Name = ServerName,
                Endpoint = "windows-default-browser",
                Detail = getString("AgentMcpBrowserUseDetail", "내장 플러그인 - Windows 기본 브라우저 조작"),
                IsSelected = isSelected,
                IsBuiltIn = true,
                CanEdit = false,
                CanDelete = false
            };
        }

        public static IReadOnlyList<AgentMcpToolAlias> CreateAliases(EditorSettings settings)
        {
            var aliases = new List<AgentMcpToolAlias>
            {
                CreateAlias("mcp_browser_use_open_url", "open_url", "Open an http, https, file, or about:blank URL in the Windows default browser.", """
                {"type":"object","properties":{"url":{"type":"string","description":"Absolute URL to open."},"newWindow":{"type":"boolean","default":false}},"required":["url"]}
                """),
                CreateAlias("mcp_browser_use_status", "status", "Get the controlled default browser window title, process, bounds, and current URL.", """
                {"type":"object","properties":{}}
                """),
                CreateAlias("mcp_browser_use_snapshot", "snapshot", "Return a concise accessibility tree for the currently controlled browser or Computer Use application window. Every emitted element has a stable ref that can be passed to mcp_browser_use_click. Set includeScreenshot only when visual context is needed.", """
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

            if (settings.BrowserUseCaptureEnabled)
            {
                aliases.Insert(2, CreateAlias(
                    "mcp_browser_use_capture",
                    "capture",
                    "Capture the controlled browser or Computer Use window as a PNG for visual inspection. The captured image is automatically attached to the model context. Before a coordinate click, call mcp_browser_use_capture_target to verify the chosen point. Do NOT call read_image.",
                    """
                    {"type":"object","properties":{}}
                    """));
                aliases.Insert(3, CreateAlias(
                    "mcp_browser_use_capture_target",
                    "capture_target",
                    "Mark a proposed point on the latest explicit capture with a red plus and attach the marked image for visual verification. This does not interact with the controlled window. Check that the plus is centered on the intended button or input field. If it is off-center, move x left or right and y up or down as needed and call this tool again until centered. Then pass the same verified coordinates to mcp_browser_use_click with coordinateSpace=screenshot.",
                    """
                    {"type":"object","properties":{"x":{"type":"integer","description":"X coordinate in the latest explicit capture image."},"y":{"type":"integer","description":"Y coordinate in the latest explicit capture image."}},"required":["x","y"]}
                    """));
            }

            if (settings.BrowserUseComputerUseEnabled)
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
                    "Launch a Windows program, friendly application name, document, or registered URI and select its visible window for Computer Use. Common names such as excel, word, powerpoint, outlook, calculator, and their Korean names are resolved automatically.",
                    """
                    {"type":"object","properties":{"target":{"type":"string","description":"Friendly app name, executable name/path, document path, or registered URI. Examples: excel, 엑셀, word, powerpoint, calculator."},"arguments":{"type":"string","description":"Optional program arguments."}},"required":["target"]}
                    """));
            }

            return aliases;
        }

        private static AgentMcpToolAlias CreateAlias(string alias, string toolName, string description, string schema)
        {
            return new AgentMcpToolAlias
            {
                Alias = alias,
                ServerId = ServerId,
                ServerName = ServerName,
                ToolName = toolName,
                Description = description,
                InputSchemaJson = schema,
                IsBuiltIn = true
            };
        }
    }
}
