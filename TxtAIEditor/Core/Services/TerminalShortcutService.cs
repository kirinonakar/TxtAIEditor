using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Core.Services
{
    public sealed class TerminalShortcutService
    {
        private const int TerminalToggleVirtualKey = 0xC0;
        private const int TerminalToggleScanCode = 0x29;
        private const int ControlVirtualKey = 0x11;
        private const short KeyDownMask = unchecked((short)0x8000);

        private readonly DispatcherTimer _pollTimer;
        private readonly IntPtr _ownerHwnd;
        private bool _shortcutWasDown = false;
        private DateTimeOffset _lastShortcutAt = DateTimeOffset.MinValue;

        public TerminalShortcutService(IntPtr ownerHwnd)
        {
            _ownerHwnd = ownerHwnd;
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _pollTimer.Tick += OnPollTimerTick;
        }

        public event EventHandler? ToggleRequested;

        public static bool IsTerminalToggleKey(KeyRoutedEventArgs e)
        {
            return (int)e.Key == TerminalToggleVirtualKey ||
                   (int)e.KeyStatus.ScanCode == TerminalToggleScanCode;
        }

        public void Start()
        {
            _shortcutWasDown = false;
            if (!_pollTimer.IsEnabled)
            {
                _pollTimer.Start();
            }
        }

        public void Stop()
        {
            _shortcutWasDown = false;
            if (_pollTimer.IsEnabled)
            {
                _pollTimer.Stop();
            }
        }

        public void RequestToggle()
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastShortcutAt).TotalMilliseconds < 150)
            {
                return;
            }

            _lastShortcutAt = now;
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnPollTimerTick(object? sender, object e)
        {
            bool shortcutDown = IsAppForeground() && IsAsyncKeyDown(ControlVirtualKey) && IsAsyncKeyDown(TerminalToggleVirtualKey);
            if (shortcutDown && !_shortcutWasDown)
            {
                RequestToggle();
            }

            _shortcutWasDown = shortcutDown;
        }

        private bool IsAppForeground()
        {
            if (_ownerHwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr foregroundHwnd = GetForegroundWindow();
            return foregroundHwnd == _ownerHwnd || IsChild(_ownerHwnd, foregroundHwnd);
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
