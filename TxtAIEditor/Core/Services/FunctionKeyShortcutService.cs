using System;
using Microsoft.UI.Xaml;

namespace TxtAIEditor.Core.Services
{
    public sealed class FunctionKeyShortcutService
    {
        public const int F9VirtualKey = 0x78;
        public const int F10VirtualKey = 0x79;
        public const int F12VirtualKey = 0x7B;

        private const short KeyDownMask = unchecked((short)0x8000);

        private readonly DispatcherTimer _pollTimer;
        private readonly Func<IntPtr> _getOwnerHwnd;
#pragma warning disable CS0414
        private bool _f9WasDown;
        private bool _f10WasDown;
        private bool _f12WasDown;
#pragma warning restore CS0414

        public FunctionKeyShortcutService(Func<IntPtr> getOwnerHwnd)
        {
            _getOwnerHwnd = getOwnerHwnd;
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _pollTimer.Tick += OnPollTimerTick;
        }

#pragma warning disable CS0067
        public event EventHandler? TopMostRequested;
        public event EventHandler? ThemeRequested;
        public event EventHandler? StickyNoteRequested;
#pragma warning restore CS0067

        public void Start()
        {
            if (!_pollTimer.IsEnabled)
            {
                ResetPressedState();
                _pollTimer.Start();
            }
        }

        public void Stop()
        {
            ResetPressedState();
            if (_pollTimer.IsEnabled)
            {
                _pollTimer.Stop();
            }
        }

        public void SuppressUntilReleased(int virtualKey)
        {
            switch (virtualKey)
            {
                case F9VirtualKey:
                    _f9WasDown = true;
                    break;
                case F10VirtualKey:
                    _f10WasDown = true;
                    break;
                case F12VirtualKey:
                    _f12WasDown = true;
                    break;
            }
        }

        private void OnPollTimerTick(object? sender, object e)
        {
            // Polling disabled: F9, F10, and F12 are now handled instantly via event-driven mechanisms
            // (OnRootKeyDown for native focus, and AcceleratorKeyPressed for WebView2 focus).
        }

        private static void PollKey(int virtualKey, ref bool wasDown, EventHandler? requested)
        {
            bool isDown = IsAsyncKeyDown(virtualKey);
            if (isDown && !wasDown)
            {
                requested?.Invoke(null, EventArgs.Empty);
            }

            wasDown = isDown;
        }

        private void ResetPressedState()
        {
            _f9WasDown = false;
            _f10WasDown = false;
            _f12WasDown = false;
        }

        private bool IsAppForeground()
        {
            IntPtr ownerHwnd = _getOwnerHwnd();
            if (ownerHwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr foregroundHwnd = GetForegroundWindow();
            return foregroundHwnd == ownerHwnd || IsChild(ownerHwnd, foregroundHwnd);
        }

        private static bool IsAsyncKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    }
}
