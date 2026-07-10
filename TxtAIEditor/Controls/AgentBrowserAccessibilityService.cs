using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentBrowserAccessibilityService
    {
        private const int MaxTextLength = 160;
        private readonly Dictionary<string, string> _refByIdentity = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AccessibilityRef> _elementsByRef = new(StringComparer.OrdinalIgnoreCase);
        private int _nextRef = 1;
        private int _generation;

        public string CaptureSnapshot(IntPtr window, int requestedMaxNodes)
        {
            int maxNodes = Math.Clamp(requestedMaxNodes, 20, 500);
            AutomationElement root = AutomationElement.FromHandle(window)
                ?? throw new InvalidOperationException("Windows accessibility could not inspect the controlled window.");

            _generation++;
            var lines = new List<string>(maxNodes);
            var stack = new Stack<SnapshotWorkItem>();
            stack.Push(new SnapshotWorkItem(root, 0, "window"));
            int visited = 0;
            bool truncated = false;

            while (stack.Count > 0)
            {
                SnapshotWorkItem item = stack.Pop();
                if (++visited > 3_000)
                {
                    truncated = true;
                    break;
                }

                if (!TryReadElement(item.Element, out ElementData data))
                {
                    continue;
                }

                string identity = BuildIdentity(item.Element, data, item.SemanticPath);
                string elementRef = GetOrCreateRef(identity);
                _elementsByRef[elementRef] = new AccessibilityRef(
                    elementRef,
                    identity,
                    item.SemanticPath,
                    item.Element,
                    data.Bounds,
                    _generation);

                int childDepth = item.OutputDepth;
                if (ShouldInclude(data))
                {
                    if (lines.Count >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    lines.Add(FormatLine(elementRef, data, item.OutputDepth));
                    childDepth++;
                }

                List<AutomationElement> children = GetChildren(item.Element);
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    AutomationElement child = children[index];
                    string segment = BuildSemanticSegment(child, index);
                    stack.Push(new SnapshotWorkItem(
                        child,
                        childDepth,
                        item.SemanticPath + "/" + segment));
                }
            }

            PruneOldRefs();
            var builder = new StringBuilder();
            builder.AppendLine("MCP tool result: Browser Use accessibility snapshot.");
            builder.AppendLine("Use ref with mcp_browser_use_click. Refs remain stable while the matching accessible element remains available.");
            builder.AppendLine("accessibility_tree:");
            if (lines.Count == 0)
            {
                builder.AppendLine("[No readable accessibility nodes. Request includeScreenshot=true for visual inspection.]");
            }
            else
            {
                foreach (string line in lines)
                {
                    builder.AppendLine(line);
                }
            }

            if (truncated)
            {
                builder.AppendLine($"[truncated after {lines.Count.ToString(CultureInfo.InvariantCulture)} nodes]");
            }

            return builder.ToString().TrimEnd();
        }

        public bool TryResolveRef(string elementRef, IntPtr window, out AccessibilityTarget target)
        {
            target = default;
            if (!_elementsByRef.TryGetValue(elementRef, out AccessibilityRef? entry))
            {
                return false;
            }

            if (TryGetTarget(entry.Element, window, out target))
            {
                return true;
            }

            try
            {
                CaptureSnapshot(window, 500);
            }
            catch
            {
                return false;
            }

            return _elementsByRef.TryGetValue(elementRef, out entry) &&
                TryGetTarget(entry.Element, window, out target);
        }

        private string GetOrCreateRef(string identity)
        {
            if (_refByIdentity.TryGetValue(identity, out string? existing))
            {
                return existing;
            }

            string elementRef = "e" + _nextRef++.ToString(CultureInfo.InvariantCulture);
            _refByIdentity[identity] = elementRef;
            return elementRef;
        }

        private void PruneOldRefs()
        {
            int oldestGeneration = _generation - 3;
            foreach (string elementRef in _elementsByRef
                .Where(pair => pair.Value.Generation < oldestGeneration)
                .Select(pair => pair.Key)
                .ToArray())
            {
                AccessibilityRef entry = _elementsByRef[elementRef];
                _elementsByRef.Remove(elementRef);
                _refByIdentity.Remove(entry.Identity);
            }
        }

        private static bool TryGetTarget(AutomationElement element, IntPtr expectedWindow, out AccessibilityTarget target)
        {
            target = default;
            try
            {
                System.Windows.Rect bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1 || element.Current.IsOffscreen)
                {
                    return false;
                }

                AutomationElement? root = element;
                while (root != null)
                {
                    int nativeHandle = root.Current.NativeWindowHandle;
                    if (nativeHandle != 0)
                    {
                        IntPtr elementWindow = new(nativeHandle);
                        if (elementWindow != expectedWindow && GetAncestor(elementWindow, 2) != expectedWindow)
                        {
                            return false;
                        }

                        break;
                    }

                    root = TreeWalker.RawViewWalker.GetParent(root);
                }

                target = new AccessibilityTarget(
                    (int)Math.Round(bounds.Left + (bounds.Width / 2.0)),
                    (int)Math.Round(bounds.Top + (bounds.Height / 2.0)),
                    NormalizeText(element.Current.Name));
                return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool TryReadElement(AutomationElement element, out ElementData data)
        {
            data = default;
            try
            {
                AutomationElement.AutomationElementInformation current = element.Current;
                string role = current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? "element";
                string name = NormalizeText(current.Name);
                string automationId = NormalizeText(current.AutomationId);
                string value = string.Empty;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                {
                    value = NormalizeText(((ValuePattern)valuePattern).Current.Value);
                }

                data = new ElementData(
                    role.ToLowerInvariant(),
                    name,
                    automationId,
                    value,
                    current.IsEnabled,
                    current.IsOffscreen,
                    current.IsKeyboardFocusable,
                    current.BoundingRectangle);
                return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool ShouldInclude(ElementData data)
        {
            if (data.IsOffscreen)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(data.Name) ||
                !string.IsNullOrWhiteSpace(data.Value) ||
                data.IsKeyboardFocusable ||
                data.Role is "button" or "checkbox" or "combobox" or "edit" or "hyperlink" or "listitem" or "menuitem" or "radiobutton" or "tabitem" or "text" or "treeitem";
        }

        private static string FormatLine(string elementRef, ElementData data, int depth)
        {
            var builder = new StringBuilder();
            builder.Append(' ', Math.Min(depth, 12) * 2);
            builder.Append("- [ref=").Append(elementRef).Append("] ").Append(data.Role);
            if (!string.IsNullOrWhiteSpace(data.Name))
            {
                builder.Append(" \"").Append(EscapeText(data.Name)).Append('"');
            }

            if (!string.IsNullOrWhiteSpace(data.Value) && !data.Value.Equals(data.Name, StringComparison.Ordinal))
            {
                builder.Append(" value=\"").Append(EscapeText(data.Value)).Append('"');
            }

            if (!data.IsEnabled)
            {
                builder.Append(" disabled");
            }

            return builder.ToString();
        }

        private static string BuildIdentity(AutomationElement element, ElementData data, string semanticPath)
        {
            if (!string.IsNullOrWhiteSpace(data.AutomationId))
            {
                return "automation:" + data.Role + ":" + data.AutomationId + ":" + semanticPath;
            }

            return "semantic:" + semanticPath;
        }

        private static string BuildSemanticSegment(AutomationElement element, int siblingIndex)
        {
            if (!TryReadElement(element, out ElementData data))
            {
                return "unknown[" + siblingIndex.ToString(CultureInfo.InvariantCulture) + "]";
            }

            string identityText = !string.IsNullOrWhiteSpace(data.AutomationId) ? data.AutomationId : data.Name;
            identityText = identityText.Length > 48 ? identityText[..48] : identityText;
            return data.Role + ":" + identityText + "[" + siblingIndex.ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static List<AutomationElement> GetChildren(AutomationElement parent)
        {
            var children = new List<AutomationElement>();
            try
            {
                TreeWalker walker = TreeWalker.ControlViewWalker;
                AutomationElement? child = walker.GetFirstChild(parent);
                while (child != null)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return children;
        }

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length > MaxTextLength ? normalized[..MaxTextLength] + "…" : normalized;
        }

        private static string EscapeText(string value)
        {
            return value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        internal readonly record struct AccessibilityTarget(int ScreenX, int ScreenY, string Name);

        private sealed record AccessibilityRef(
            string Ref,
            string Identity,
            string SemanticPath,
            AutomationElement Element,
            System.Windows.Rect Bounds,
            int Generation);

        private readonly record struct SnapshotWorkItem(AutomationElement Element, int OutputDepth, string SemanticPath);

        private readonly record struct ElementData(
            string Role,
            string Name,
            string AutomationId,
            string Value,
            bool IsEnabled,
            bool IsOffscreen,
            bool IsKeyboardFocusable,
            System.Windows.Rect Bounds);
    }
}
