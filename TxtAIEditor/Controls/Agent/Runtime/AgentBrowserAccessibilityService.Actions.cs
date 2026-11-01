using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Controls
{
    internal sealed partial class AgentBrowserAccessibilityService
    {
        public string? TryClickDirect(string elementRef, IntPtr window)
        {
            return WithResolvedElement(elementRef, window, (element, data) => TryClickElementDirect(element));
        }

        private string? TryClickElementDirect(IUIAutomationElement element)
        {
            // A failed action may already have changed the application. Never retry
            // it with another pattern or physical input after dispatch.
            if (TryUsePattern<IUIAutomationInvokePattern>(element, 10000, pattern =>
                Dispatch(() => pattern.Invoke()), out _))
            {
                return "InvokePattern.Invoke";
            }

            if (TryUsePattern<IUIAutomationTogglePattern>(element, 10015, pattern =>
                Dispatch(() => pattern.Toggle()), out _))
            {
                return "TogglePattern.Toggle";
            }

            if (TryUsePattern<IUIAutomationSelectionItemPattern>(element, 10010, pattern =>
                Dispatch(() => pattern.Select()), out _))
            {
                return "SelectionItemPattern.Select";
            }

            if (TryUsePattern<IUIAutomationExpandCollapsePattern>(element, 10005, pattern =>
            {
                ThrowIfFailed(pattern.GetCurrentExpandCollapseState(out int state));
                if (state == 3) // LeafNode has no disclosure action.
                {
                    return false;
                }

                return Dispatch(() => state == 1 ? pattern.Collapse() : pattern.Expand());
            }, out bool expanded) && expanded)
            {
                return "ExpandCollapsePattern";
            }

            if (TryUsePattern<IUIAutomationLegacyIAccessiblePattern>(element, 10018, pattern =>
            {
                ThrowIfFailed(pattern.GetCurrentDefaultAction(out string action));
                return !string.IsNullOrWhiteSpace(action) && Dispatch(() => pattern.DoDefaultAction());
            }, out bool invoked) && invoked)
            {
                return "LegacyIAccessiblePattern.DoDefaultAction";
            }

            return null;
        }

        public DirectValueWriteResult? TrySetValueDirect(string elementRef, IntPtr window, string value)
        {
            return WithResolvedElement(elementRef, window, (element, data) => TrySetElementValueDirect(element, value));
        }

        private DirectValueWriteResult? TrySetElementValueDirect(IUIAutomationElement element, string value)
        {
            bool valueVerified = false;
            bool supported = TryUsePattern<IUIAutomationValuePattern>(element, 10002, pattern =>
            {
                ThrowIfFailed(pattern.GetCurrentIsReadOnly(out int readOnly));
                if (readOnly != 0)
                {
                    throw new InvalidOperationException("The referenced UIA value is read-only. No physical input was sent.");
                }

                Dispatch(() => pattern.SetValue(value));
                // SetValue success and readback verification are separate outcomes.
                // Providers may format numbers/units, publish a stale value, or replace
                // the element after applying the write. None proves the write failed.
                try
                {
                    valueVerified = pattern.GetCurrentValue(out string actual) >= 0 &&
                        string.Equals(value, actual, StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    // The caller still returns a fresh snapshot for inspection.
                }

                return true;
            }, out _);
            return supported ? new DirectValueWriteResult(valueVerified) : null;
        }

        internal readonly record struct DirectValueWriteResult(bool ValueVerified);

        private bool Dispatch(Func<int> action)
        {
            // Invalidate refs before invoking: even a COM failure may follow a mutation.
            _elementsByRef.Clear();
            int result = action();
            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"UIA action failed (0x{result:X8}). It may have been applied. No physical retry was sent; request a fresh snapshot.");
            }

            return true;
        }

        private static bool TryUsePattern<T>(IUIAutomationElement element, int patternId,
            Func<T, bool> action, out bool result) where T : class
        {
            result = false;
            Guid interfaceId = typeof(T).GUID;
            IntPtr pointer = IntPtr.Zero;
            object? patternObject = null;
            try
            {
                int status = element.GetCurrentPatternAs(patternId, ref interfaceId, out pointer);
                // Only genuine absence permits foreground fallback. Provider failures
                // must not turn an uncertain operation into a physical click.
                if (status == unchecked((int)0x80040204) || status == unchecked((int)0x80004002))
                {
                    return false; // UIA_E_NOTSUPPORTED / E_NOINTERFACE
                }

                ThrowIfFailed(status);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                patternObject = Marshal.GetObjectForIUnknown(pointer);
                result = action((T)patternObject);
                return true;
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.Release(pointer);
                }

                ReleaseComObject(patternObject);
            }
        }

        private T WithResolvedElement<T>(string elementRef, IntPtr window,
            Func<IUIAutomationElement, ElementData, T> action)
        {
            if (!_elementsByRef.TryGetValue(elementRef, out AccessibilityRef? entry) ||
                entry.Window != window || entry.Generation != _generation ||
                entry.IsAmbiguous || entry.RuntimeId.Length == 0)
            {
                throw StaleReference(elementRef);
            }

            int initializationResult = CoInitializeEx(IntPtr.Zero, 0);
            bool shouldUninitialize = initializationResult >= 0;
            if (initializationResult < 0 && initializationResult != RpcEChangedMode)
            {
                Marshal.ThrowExceptionForHR(initializationResult);
            }

            object? automationObject = null;
            IUIAutomationElement? root = null;
            IUIAutomationElement? match = null;
            IUIAutomationTreeWalker? walker = null;
            var stack = new Stack<IUIAutomationElement>();
            try
            {
                automationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(CUiAutomationClassId, true)!)
                    ?? throw new InvalidOperationException("Windows UI Automation could not be initialized.");
                var automation = (IUIAutomation)automationObject;
                ThrowIfFailed(automation.ElementFromHandle(window, out root));
                ThrowIfFailed(automation.GetControlViewWalker(out walker));
                if (root == null || walker == null)
                {
                    throw StaleReference(elementRef);
                }

                stack.Push(root);
                root = null;
                int visited = 0;
                while (stack.Count > 0)
                {
                    IUIAutomationElement element = stack.Pop();
                    bool retain = false;
                    try
                    {
                        if (++visited > 3_000 || !TryReadElement(element, out ElementData data))
                        {
                            throw StaleReference(elementRef);
                        }

                        if (data.RuntimeId == entry.RuntimeId && data.ProcessId == entry.ProcessId)
                        {
                            if (match != null || BuildIdentity(window, data, string.Empty) != entry.Identity)
                            {
                                throw StaleReference(elementRef);
                            }

                            match = element;
                            retain = true;
                        }

                        foreach (IUIAutomationElement child in GetChildren(walker, element, strict: true))
                        {
                            stack.Push(child);
                        }
                    }
                    finally
                    {
                        if (!retain)
                        {
                            ReleaseComObject(element);
                        }
                    }
                }

                // Read again immediately before dispatch; cached geometry is never used.
                if (match == null || !TryReadElement(match, out ElementData current) ||
                    !current.IsEnabled || BuildIdentity(window, current, string.Empty) != entry.Identity)
                {
                    throw StaleReference(elementRef);
                }

                return action(match, current);
            }
            finally
            {
                while (stack.Count > 0)
                {
                    ReleaseComObject(stack.Pop());
                }

                ReleaseComObject(match);
                ReleaseComObject(root);
                ReleaseComObject(walker);
                ReleaseComObject(automationObject);
                if (shouldUninitialize)
                {
                    CoUninitialize();
                }
            }
        }

        private static InvalidOperationException StaleReference(string elementRef) => new(
            $"Accessibility ref '{elementRef}' is stale, ambiguous, disabled, or unverifiable. Request mcp_browser_use_snapshot and use a current ref. No input was sent.");

        [ComImport, Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationInvokePattern
        {
            [PreserveSig] int Invoke();
        }

        [ComImport, Guid("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTogglePattern
        {
            [PreserveSig] int Toggle();
        }

        [ComImport, Guid("A8EFA66A-0FDA-421A-9194-38021F3578EA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationSelectionItemPattern
        {
            [PreserveSig] int Select();
        }

        [ComImport, Guid("619BE086-1F4E-4EE4-BAFA-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationExpandCollapsePattern
        {
            [PreserveSig] int Expand();
            [PreserveSig] int Collapse();
            [PreserveSig] int GetCurrentExpandCollapseState(out int state);
        }

        [ComImport, Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationValuePattern
        {
            [PreserveSig] int SetValue([MarshalAs(UnmanagedType.BStr)] string value);
            [PreserveSig] int GetCurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentIsReadOnly(out int readOnly);
        }

        [ComImport, Guid("828055AD-355B-4435-86D5-3B51C14A9B1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationLegacyIAccessiblePattern
        {
            [PreserveSig] int Select(int flags);
            [PreserveSig] int DoDefaultAction();
            [PreserveSig] int SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int GetCurrentChildId(out int childId);
            [PreserveSig] int GetCurrentName([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentDescription([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentRole(out uint role);
            [PreserveSig] int GetCurrentState(out uint state);
            [PreserveSig] int GetCurrentHelp([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentKeyboardShortcut([MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int GetCurrentSelection(out IntPtr selection);
            [PreserveSig] int GetCurrentDefaultAction([MarshalAs(UnmanagedType.BStr)] out string value);
        }
    }
}
