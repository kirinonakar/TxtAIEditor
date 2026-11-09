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
                CreateAlias("mcp_browser_use_open_url", "open_url", "Open an absolute http, https, file, or about:blank URL in the Windows default browser.", """
                {"type":"object","properties":{"url":{"type":"string"},"newWindow":{"type":"boolean","default":false}},"required":["url"]}
                """),
                CreateAlias("mcp_browser_use_status", "status", "Report the controlled window's title, process, bounds, and browser URL when available.", """
                {"type":"object","properties":{}}
                """),
                CreateAlias("mcp_browser_use_snapshot", "snapshot", "Get the current accessibility tree and element refs. Request a screenshot only for visual context.", """
                {"type":"object","properties":{"maxNodes":{"type":"integer","minimum":20,"maximum":500,"default":180},"includeScreenshot":{"type":"boolean","default":false}}}
                """),
                CreateAlias("mcp_browser_use_read_page", "read_page", "Copy selectable page text with its URL and title. Canvas or protected text may be unavailable.", """
                {"type":"object","properties":{"maxCharacters":{"type":"integer","minimum":1000,"maximum":60000,"default":30000}}}
                """),
                CreateAlias("mcp_browser_use_click", "click", "Click by latest ref (preferred) or by x/y. Verify screenshot clicks with mcp_browser_use_capture_target first.", """
                {"type":"object","properties":{"ref":{"type":"string"},"x":{"type":"integer"},"y":{"type":"integer"},"coordinateSpace":{"type":"string","enum":["screenshot","window"],"default":"screenshot","description":"Pixels from the latest capture or from the unscaled window's top left."},"button":{"type":"string","enum":["left","right","middle"],"default":"left"},"clickCount":{"type":"integer","minimum":1,"maximum":3,"default":1}},"anyOf":[{"required":["ref"]},{"required":["x","y"]}]}
                """),
                CreateAlias("mcp_browser_use_drag", "drag", "Drag between two endpoints. Give a current ref or an x/y pair for each.", """
                {"type":"object","properties":{"startRef":{"type":"string"},"endRef":{"type":"string"},"startX":{"type":"integer"},"startY":{"type":"integer"},"endX":{"type":"integer"},"endY":{"type":"integer"},"coordinateSpace":{"type":"string","enum":["screenshot","window"],"default":"screenshot","description":"Pixels from the latest capture or from the unscaled window's top left."},"button":{"type":"string","enum":["left","right","middle"],"default":"left"}}}
                """),
                CreateAlias("mcp_browser_use_type_text", "type_text", "Type Unicode into the control identified by ref, or into the focused control when ref is omitted.", """
                {"type":"object","properties":{"text":{"type":"string"},"ref":{"type":"string"},"replace":{"type":"boolean","default":false},"pressEnter":{"type":"boolean","default":false}},"required":["text"]}
                """),
                CreateAlias("mcp_browser_use_key", "key", "Send a key or shortcut. Keys include enter, escape, tab, space, backspace, delete, home, end, pageup, pagedown, up, down, left, right, f5, f6, and f11.", """
                {"type":"object","properties":{"key":{"type":"string"},"ctrl":{"type":"boolean","default":false},"alt":{"type":"boolean","default":false},"shift":{"type":"boolean","default":false}},"required":["key"]}
                """),
                CreateAlias("mcp_browser_use_scroll", "scroll", "Scroll the controlled window.", """
                {"type":"object","properties":{"deltaY":{"type":"integer","description":"Negative scrolls down, positive scrolls up.","default":-720},"deltaX":{"type":"integer","description":"Horizontal via Shift+wheel.","default":0}}}
                """),
                CreateAlias("mcp_browser_use_navigate", "navigate", "Navigate the current browser tab.", """
                {"type":"object","properties":{"action":{"type":"string","enum":["back","forward","refresh","stop","home"]}},"required":["action"]}
                """),
                CreateAlias("mcp_browser_use_tab", "tab", "Manage browser tabs.", """
                {"type":"object","properties":{"action":{"type":"string","enum":["new","close","next","previous","reopen"]}},"required":["action"]}
                """),
                CreateAlias("mcp_browser_use_find", "find", "Search the current page using the browser's find box.", """
                {"type":"object","properties":{"text":{"type":"string"},"next":{"type":"boolean","default":false}},"required":["text"]}
                """)
            };

            if (settings.BrowserUseCaptureEnabled)
            {
                aliases.Insert(2, CreateAlias(
                    "mcp_browser_use_capture",
                    "capture",
                    "Capture the controlled window and attach its screenshot directly.",
                    """
                    {"type":"object","properties":{}}
                    """));
                aliases.Insert(3, CreateAlias(
                    "mcp_browser_use_capture_target",
                    "capture_target",
                    "Preview a red plus on the latest capture without clicking. Adjust and recheck until it is centered on the intended control. Then click the same x/y with coordinateSpace=screenshot.",
                    """
                    {"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]}
                    """));
            }

            if (settings.BrowserUseComputerUseEnabled)
            {
                aliases.Add(CreateAlias(
                    "mcp_browser_use_list_windows",
                    "list_windows",
                    "List controllable windows and their IDs, excluding TxtAIEditor.",
                    """
                    {"type":"object","properties":{"maxResults":{"type":"integer","minimum":1,"maximum":100,"default":40}}}
                    """));
                aliases.Add(CreateAlias(
                    "mcp_browser_use_focus_window",
                    "focus_window",
                    "Select by window ID (preferred), title, or process. Activates the window unless UIA direct mode is on.",
                    """
                    {"type":"object","properties":{"windowId":{"type":"string","description":"ID from list_windows."},"title":{"type":"string","description":"Case-insensitive substring."},"process":{"type":"string","description":"Case-insensitive substring."}}}
                    """));
                aliases.Add(CreateAlias(
                    "mcp_browser_use_open_app",
                    "open_app",
                    "Launch a Windows app, select its visible window, and return an initial accessibility snapshot.",
                    """
                    {"type":"object","properties":{"target":{"type":"string","description":"App name (e.g. excel or 엑셀), executable, document path, or registered URI."},"arguments":{"type":"string"}},"required":["target"]}
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
