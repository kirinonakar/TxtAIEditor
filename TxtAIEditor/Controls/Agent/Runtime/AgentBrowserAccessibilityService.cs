using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentBrowserAccessibilityService
    {
        private const int MaxTextLength = 160;
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private const int UiaValueValuePropertyId = 30045;
        private const int UiaTextPatternId = 10014;
        private const int TextRangeEndpointStart = 0;
        private const int TextRangeEndpointEnd = 1;
        private static readonly Guid CUiAutomationClassId = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

        private readonly Dictionary<string, string> _refByIdentity = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AccessibilityRef> _elementsByRef = new(StringComparer.OrdinalIgnoreCase);
        private int _nextRef = 1;
        private int _generation;

        public string CaptureSnapshot(IntPtr window, int requestedMaxNodes)
        {
            int initializationResult = CoInitializeEx(IntPtr.Zero, 0);
            bool shouldUninitialize = initializationResult >= 0;
            if (initializationResult < 0 && initializationResult != RpcEChangedMode)
            {
                Marshal.ThrowExceptionForHR(initializationResult);
            }

            try
            {
                return CaptureSnapshotCore(window, requestedMaxNodes);
            }
            finally
            {
                if (shouldUninitialize)
                {
                    CoUninitialize();
                }
            }
        }

        public bool TryResolveRef(string elementRef, IntPtr window, out AccessibilityTarget target)
        {
            if (TryGetCurrentTarget(elementRef, window, out target))
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

            return TryGetCurrentTarget(elementRef, window, out target);
        }

        public bool TryCollapseTextSelection(IntPtr window)
        {
            int initializationResult = CoInitializeEx(IntPtr.Zero, 0);
            bool shouldUninitialize = initializationResult >= 0;
            if (initializationResult < 0 && initializationResult != RpcEChangedMode)
            {
                return false;
            }

            try
            {
                return TryCollapseTextSelectionCore(window);
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidComObjectException)
            {
                return false;
            }
            finally
            {
                if (shouldUninitialize)
                {
                    CoUninitialize();
                }
            }
        }

        private static bool TryCollapseTextSelectionCore(IntPtr window)
        {
            object automationObject = Activator.CreateInstance(
                Type.GetTypeFromCLSID(CUiAutomationClassId, throwOnError: true)!)
                ?? throw new InvalidOperationException(
                    "Windows UI Automation could not be initialized.");
            var automation = (IUIAutomation)automationObject;
            IUIAutomationTreeWalker? walker = null;
            IUIAutomationElement? root = null;
            var stack = new Stack<IUIAutomationElement>();

            try
            {
                ThrowIfFailed(automation.ElementFromHandle(window, out root));
                ThrowIfFailed(automation.GetControlViewWalker(out walker));
                if (root == null || walker == null)
                {
                    return false;
                }

                stack.Push(root);
                root = null;
                int visited = 0;
                while (stack.Count > 0 && ++visited <= 3_000)
                {
                    IUIAutomationElement element = stack.Pop();
                    try
                    {
                        if (element.GetCurrentControlType(out int controlType) >= 0 &&
                            controlType == 50030 &&
                            element.GetCurrentIsOffscreen(out int isOffscreen) >= 0 &&
                            isOffscreen == 0 &&
                            TryCollapseElementTextSelection(element))
                        {
                            return true;
                        }

                        List<IUIAutomationElement> children = GetChildren(walker, element);
                        for (int index = children.Count - 1; index >= 0; index--)
                        {
                            stack.Push(children[index]);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(element);
                    }
                }

                return false;
            }
            finally
            {
                while (stack.Count > 0)
                {
                    ReleaseComObject(stack.Pop());
                }

                ReleaseComObject(root);
                ReleaseComObject(walker);
                ReleaseComObject(automationObject);
            }
        }

        private static bool TryCollapseElementTextSelection(IUIAutomationElement element)
        {
            Guid textPatternGuid = typeof(IUIAutomationTextPattern).GUID;
            IntPtr patternPointer = IntPtr.Zero;
            object? patternObject = null;
            IUIAutomationTextRangeArray? ranges = null;
            IUIAutomationTextRange? range = null;

            try
            {
                if (element.GetCurrentPatternAs(
                        UiaTextPatternId,
                        ref textPatternGuid,
                        out patternPointer) < 0 ||
                    patternPointer == IntPtr.Zero)
                {
                    return false;
                }

                patternObject = Marshal.GetObjectForIUnknown(patternPointer);
                Marshal.Release(patternPointer);
                patternPointer = IntPtr.Zero;
                if (patternObject is not IUIAutomationTextPattern textPattern ||
                    textPattern.GetSelection(out ranges) < 0 ||
                    ranges == null ||
                    ranges.get_Length(out int length) < 0 ||
                    length == 0 ||
                    ranges.GetElement(0, out range) < 0 ||
                    range == null)
                {
                    return false;
                }

                return range.MoveEndpointByRange(
                        TextRangeEndpointEnd,
                        range,
                        TextRangeEndpointStart) >= 0 &&
                    range.Select() >= 0;
            }
            finally
            {
                if (patternPointer != IntPtr.Zero)
                {
                    Marshal.Release(patternPointer);
                }

                ReleaseComObject(range);
                ReleaseComObject(ranges);
                ReleaseComObject(patternObject);
            }
        }

        private bool TryGetCurrentTarget(string elementRef, IntPtr window, out AccessibilityTarget target)
        {
            target = default;
            if (!_elementsByRef.TryGetValue(elementRef, out AccessibilityRef? entry) ||
                entry.Generation != _generation ||
                entry.Window != window ||
                entry.Bounds.Right - entry.Bounds.Left <= 1 ||
                entry.Bounds.Bottom - entry.Bounds.Top <= 1)
            {
                return false;
            }

            target = new AccessibilityTarget(
                entry.Bounds.Left + ((entry.Bounds.Right - entry.Bounds.Left) / 2),
                entry.Bounds.Top + ((entry.Bounds.Bottom - entry.Bounds.Top) / 2),
                entry.Name);
            return true;
        }

        private string CaptureSnapshotCore(IntPtr window, int requestedMaxNodes)
        {
            int maxNodes = Math.Clamp(requestedMaxNodes, 20, 500);
            object automationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(CUiAutomationClassId, throwOnError: true)!)
                ?? throw new InvalidOperationException("Windows UI Automation could not be initialized.");
            var automation = (IUIAutomation)automationObject;
            IUIAutomationTreeWalker? walker = null;
            IUIAutomationElement? root = null;

            try
            {
                ThrowIfFailed(automation.ElementFromHandle(window, out root));
                ThrowIfFailed(automation.GetControlViewWalker(out walker));
                if (root == null || walker == null)
                {
                    throw new InvalidOperationException("Windows UI Automation returned no accessibility root.");
                }

                _generation++;
                var lines = new List<string>(maxNodes);
                var stack = new Stack<SnapshotWorkItem>();
                stack.Push(new SnapshotWorkItem(root, 0, "window"));
                root = null;
                int visited = 0;
                bool truncated = false;

                try
                {
                    while (stack.Count > 0)
                    {
                        SnapshotWorkItem item = stack.Pop();
                        try
                        {
                            if (++visited > 3_000)
                            {
                                truncated = true;
                                break;
                            }

                            if (!TryReadElement(item.Element, out ElementData data))
                            {
                                continue;
                            }

                            string identity = BuildIdentity(data, item.SemanticPath);
                            string elementRef = GetOrCreateRef(identity);
                            _elementsByRef[elementRef] = new AccessibilityRef(
                                identity,
                                window,
                                data.Bounds,
                                data.Name,
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

                            List<IUIAutomationElement> children = GetChildren(walker, item.Element);
                            for (int index = children.Count - 1; index >= 0; index--)
                            {
                                IUIAutomationElement child = children[index];
                                string segment = BuildSemanticSegment(child, index);
                                stack.Push(new SnapshotWorkItem(
                                    child,
                                    childDepth,
                                    item.SemanticPath + "/" + segment));
                            }
                        }
                        finally
                        {
                            ReleaseComObject(item.Element);
                        }
                    }
                }
                finally
                {
                    while (stack.Count > 0)
                    {
                        ReleaseComObject(stack.Pop().Element);
                    }
                }

                PruneOldRefs();
                var builder = new StringBuilder();
                builder.AppendLine("MCP tool result: Browser Use accessibility snapshot.");
                builder.AppendLine("Use ref with mcp_browser_use_click or mcp_browser_use_drag. Refs remain stable while the matching accessible element remains available.");
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
            finally
            {
                ReleaseComObject(root);
                ReleaseComObject(walker);
                ReleaseComObject(automationObject);
            }
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

        private static bool TryReadElement(IUIAutomationElement element, out ElementData data)
        {
            data = default;
            try
            {
                _ = element.GetCurrentControlType(out int controlType);
                _ = element.GetCurrentName(out string name);
                _ = element.GetCurrentAutomationId(out string automationId);
                _ = element.GetCurrentIsEnabled(out int isEnabled);
                _ = element.GetCurrentIsOffscreen(out int isOffscreen);
                _ = element.GetCurrentIsKeyboardFocusable(out int isKeyboardFocusable);
                _ = element.GetCurrentBoundingRectangle(out NativeRect bounds);

                string value = string.Empty;
                if (element.GetCurrentPropertyValue(UiaValueValuePropertyId, out object propertyValue) >= 0 &&
                    propertyValue is string stringValue)
                {
                    value = NormalizeText(stringValue);
                }

                data = new ElementData(
                    MapControlType(controlType),
                    NormalizeText(name),
                    NormalizeText(automationId),
                    value,
                    isEnabled != 0,
                    isOffscreen != 0,
                    isKeyboardFocusable != 0,
                    bounds);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidComObjectException)
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

        private static string BuildIdentity(ElementData data, string semanticPath)
        {
            if (!string.IsNullOrWhiteSpace(data.AutomationId))
            {
                return "automation:" + data.Role + ":" + data.AutomationId + ":" + semanticPath;
            }

            return "semantic:" + semanticPath;
        }

        private static string BuildSemanticSegment(IUIAutomationElement element, int siblingIndex)
        {
            if (!TryReadElement(element, out ElementData data))
            {
                return "unknown[" + siblingIndex.ToString(CultureInfo.InvariantCulture) + "]";
            }

            string identityText = !string.IsNullOrWhiteSpace(data.AutomationId) ? data.AutomationId : data.Name;
            identityText = identityText.Length > 48 ? identityText[..48] : identityText;
            return data.Role + ":" + identityText + "[" + siblingIndex.ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static List<IUIAutomationElement> GetChildren(IUIAutomationTreeWalker walker, IUIAutomationElement parent)
        {
            var children = new List<IUIAutomationElement>();
            try
            {
                if (walker.GetFirstChildElement(parent, out IUIAutomationElement? child) < 0 || child == null)
                {
                    return children;
                }

                while (child != null)
                {
                    children.Add(child);
                    if (walker.GetNextSiblingElement(child, out IUIAutomationElement? next) < 0)
                    {
                        break;
                    }

                    child = next;
                }
            }
            catch (COMException)
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

        private static string MapControlType(int id)
        {
            return id switch
            {
                50000 => "button", 50002 => "checkbox", 50003 => "combobox", 50004 => "edit",
                50005 => "hyperlink", 50006 => "image", 50007 => "listitem", 50008 => "list",
                50009 => "menu", 50010 => "menubar", 50011 => "menuitem", 50012 => "progressbar",
                50013 => "radiobutton", 50014 => "scrollbar", 50015 => "slider", 50016 => "spinner",
                50017 => "statusbar", 50018 => "tab", 50019 => "tabitem", 50020 => "text",
                50021 => "toolbar", 50022 => "tooltip", 50023 => "tree", 50024 => "treeitem",
                50025 => "custom", 50026 => "group", 50027 => "thumb", 50028 => "datagrid",
                50029 => "dataitem", 50030 => "document", 50031 => "splitbutton", 50032 => "window",
                50033 => "pane", 50034 => "header", 50035 => "headeritem", 50036 => "table",
                50037 => "titlebar", 50038 => "separator", 50039 => "semanticzoom", 50040 => "appbar",
                _ => "element"
            };
        }

        private static void ThrowIfFailed(int result)
        {
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.ReleaseComObject(value);
                }
                catch (InvalidComObjectException)
                {
                }
            }
        }

        internal readonly record struct AccessibilityTarget(int ScreenX, int ScreenY, string Name);

        private sealed record AccessibilityRef(
            string Identity,
            IntPtr Window,
            NativeRect Bounds,
            string Name,
            int Generation);

        private readonly record struct SnapshotWorkItem(IUIAutomationElement Element, int OutputDepth, string SemanticPath);

        private readonly record struct ElementData(
            string Role,
            string Name,
            string AutomationId,
            string Value,
            bool IsEnabled,
            bool IsOffscreen,
            bool IsKeyboardFocusable,
            NativeRect Bounds);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [ComImport]
        [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomation
        {
            [PreserveSig] int CompareElements(IntPtr element1, IntPtr element2, out int areSame);
            [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
            [PreserveSig] int GetRootElement(out IntPtr root);
            [PreserveSig] int ElementFromHandle(IntPtr window, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
            [PreserveSig] int ElementFromPoint(long point, out IntPtr element);
            [PreserveSig] int GetFocusedElement(out IntPtr element);
            [PreserveSig] int GetRootElementBuildCache(IntPtr cacheRequest, out IntPtr root);
            [PreserveSig] int ElementFromHandleBuildCache(IntPtr window, IntPtr cacheRequest, out IntPtr element);
            [PreserveSig] int ElementFromPointBuildCache(long point, IntPtr cacheRequest, out IntPtr element);
            [PreserveSig] int GetFocusedElementBuildCache(IntPtr cacheRequest, out IntPtr element);
            [PreserveSig] int CreateTreeWalker(IntPtr condition, out IntPtr walker);
            [PreserveSig] int GetControlViewWalker([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
        }

        [ComImport]
        [Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTreeWalker
        {
            [PreserveSig] int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
            [PreserveSig] int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? child);
            [PreserveSig] int GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement? child);
            [PreserveSig] int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? sibling);
        }

        [ComImport]
        [Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTextPattern
        {
            [PreserveSig] int RangeFromPoint(NativePoint point, out IUIAutomationTextRange? range);
            [PreserveSig] int RangeFromChild(IUIAutomationElement child, out IUIAutomationTextRange? range);
            [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int GetVisibleRanges(out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange? range);
            [PreserveSig] int get_SupportedTextSelection(out int supportedTextSelection);
        }

        [ComImport]
        [Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTextRangeArray
        {
            [PreserveSig] int get_Length(out int length);
            [PreserveSig] int GetElement(int index, out IUIAutomationTextRange? element);
        }

        [ComImport]
        [Guid("A543CC6A-F4AE-494B-8239-C814481187A8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTextRange
        {
            [PreserveSig] int Clone(out IUIAutomationTextRange? clonedRange);
            [PreserveSig] int Compare(IUIAutomationTextRange range, out int areSame);
            [PreserveSig] int CompareEndpoints(
                int sourceEndpoint,
                IUIAutomationTextRange range,
                int targetEndpoint,
                out int comparison);
            [PreserveSig] int ExpandToEnclosingUnit(int textUnit);
            [PreserveSig] int FindAttribute(
                int attributeId,
                [MarshalAs(UnmanagedType.Struct)] object value,
                int backward,
                out IUIAutomationTextRange? found);
            [PreserveSig] int FindText(
                [MarshalAs(UnmanagedType.BStr)] string text,
                int backward,
                int ignoreCase,
                out IUIAutomationTextRange? found);
            [PreserveSig] int GetAttributeValue(
                int attributeId,
                [MarshalAs(UnmanagedType.Struct)] out object value);
            [PreserveSig] int GetBoundingRectangles(out IntPtr boundingRectangles);
            [PreserveSig] int GetEnclosingElement(out IUIAutomationElement? enclosingElement);
            [PreserveSig] int GetText(
                int maxLength,
                [MarshalAs(UnmanagedType.BStr)] out string text);
            [PreserveSig] int Move(int textUnit, int count, out int moved);
            [PreserveSig] int MoveEndpointByUnit(
                int endpoint,
                int textUnit,
                int count,
                out int moved);
            [PreserveSig] int MoveEndpointByRange(
                int sourceEndpoint,
                IUIAutomationTextRange range,
                int targetEndpoint);
            [PreserveSig] int Select();
            [PreserveSig] int AddToSelection();
            [PreserveSig] int RemoveFromSelection();
            [PreserveSig] int ScrollIntoView(int alignToTop);
            [PreserveSig] int GetChildren(out IntPtr children);
        }

        [ComImport]
        [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationElement
        {
            [PreserveSig] int SetFocus();
            [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
            [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr found);
            [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
            [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
            [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
            [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updatedElement);
            [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
            [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
            [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
            [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
            [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid interfaceId, out IntPtr patternObject);
            [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid interfaceId, out IntPtr patternObject);
            [PreserveSig] int GetCurrentPattern(int patternId, out IntPtr patternObject);
            [PreserveSig] int GetCachedPattern(int patternId, out IntPtr patternObject);
            [PreserveSig] int GetCachedParent(out IntPtr parent);
            [PreserveSig] int GetCachedChildren(out IntPtr children);
            [PreserveSig] int GetCurrentProcessId(out int value);
            [PreserveSig] int GetCurrentControlType(out int value);
            [PreserveSig] int GetCurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentName([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentHasKeyboardFocus(out int value);
            [PreserveSig] int GetCurrentIsKeyboardFocusable(out int value);
            [PreserveSig] int GetCurrentIsEnabled(out int value);
            [PreserveSig] int GetCurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentClassName([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentCulture(out int value);
            [PreserveSig] int GetCurrentIsControlElement(out int value);
            [PreserveSig] int GetCurrentIsContentElement(out int value);
            [PreserveSig] int GetCurrentIsPassword(out int value);
            [PreserveSig] int GetCurrentNativeWindowHandle(out IntPtr value);
            [PreserveSig] int GetCurrentItemType([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentIsOffscreen(out int value);
            [PreserveSig] int GetCurrentOrientation(out int value);
            [PreserveSig] int GetCurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentIsRequiredForForm(out int value);
            [PreserveSig] int GetCurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentBoundingRectangle(out NativeRect value);
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
