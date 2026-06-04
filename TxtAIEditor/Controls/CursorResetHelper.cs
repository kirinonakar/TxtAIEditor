using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TxtAIEditor.Controls
{
    internal static class CursorResetHelper
    {
        private const int IdcArrow = 32512;
        private static readonly IntPtr ArrowCursor = LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow);
        private static readonly ConditionalWeakTable<FlyoutBase, object> RegisteredFlyouts = new();

        public static void ResetToArrow(FrameworkElement? owner)
        {
            SetArrowCursor();

            var dispatcherQueue = owner?.DispatcherQueue;
            if (dispatcherQueue == null)
            {
                return;
            }

            dispatcherQueue.TryEnqueue(SetArrowCursor);
            QueueDelayedReset(dispatcherQueue, TimeSpan.FromMilliseconds(60));
            QueueDelayedReset(dispatcherQueue, TimeSpan.FromMilliseconds(180));
        }

        public static void AttachToFlyout(FlyoutBase flyout, FrameworkElement? owner)
        {
            var ownerReference = owner == null ? null : new WeakReference<FrameworkElement>(owner);

            RegisteredFlyouts.GetValue(flyout, _ =>
            {
                flyout.Opened += (_, __) => ResetToArrow(GetOwner(ownerReference));
                flyout.Closed += (_, __) => ResetToArrow(GetOwner(ownerReference));
                return new object();
            });
        }

        private static FrameworkElement? GetOwner(WeakReference<FrameworkElement>? ownerReference)
        {
            return ownerReference != null && ownerReference.TryGetTarget(out var owner)
                ? owner
                : null;
        }

        private static void QueueDelayedReset(DispatcherQueue dispatcherQueue, TimeSpan delay)
        {
            var timer = dispatcherQueue.CreateTimer();
            timer.Interval = delay;
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                SetArrowCursor();
            };
            timer.Start();
        }

        private static void SetArrowCursor()
        {
            if (ArrowCursor != IntPtr.Zero)
            {
                SetCursor(ArrowCursor);
            }
        }

        [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);
    }
}
