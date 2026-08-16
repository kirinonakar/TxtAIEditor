using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Core.Services
{
    internal sealed class TrayIconService : IDisposable
    {
        private const uint TrayIconId = 1;
        private const uint TrayCallbackMessage = 0x8000 + 42;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint ImageIcon = 1;
        private const uint LoadFromFile = 0x00000010;
        private const uint LoadDefaultSize = 0x00000040;
        private const uint WmContextMenu = 0x007B;
        private const uint WmLButtonDoubleClick = 0x0203;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmNull = 0x0000;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmReturnCommand = 0x0100;
        private const uint OpenMenuCommand = 1;
        private const uint CloseMenuCommand = 2;
        private const uint WindowMenuCommandBase = 100;
        private const nuint SubclassId = 0x54524159;

        private readonly IntPtr _windowHandle;
        private readonly string _toolTip;
        private readonly string _openText;
        private readonly string _closeText;
        private readonly Func<IReadOnlyList<TrayWindowItem>> _getWindows;
        private readonly Action _openRequested;
        private readonly Action _closeRequested;
        private readonly SubclassProc _subclassProc;
        private readonly uint _taskbarCreatedMessage;
        private IntPtr _iconHandle;
        private bool _disposed;

        public TrayIconService(
            IntPtr windowHandle,
            string toolTip,
            string openText,
            string closeText,
            Func<IReadOnlyList<TrayWindowItem>> getWindows,
            Action openRequested,
            Action closeRequested)
        {
            _windowHandle = windowHandle;
            _toolTip = toolTip;
            _openText = openText;
            _closeText = closeText;
            _getWindows = getWindows;
            _openRequested = openRequested;
            _closeRequested = closeRequested;
            _subclassProc = WindowSubclassProc;
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

            if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                _iconHandle = LoadTrayIcon();
                AddTrayIcon();
            }
            catch
            {
                RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
                if (_iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NotifyIconData data = CreateNotifyIconData();
            ShellNotifyIcon(NimDelete, ref data);
            RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);

            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }

        private void AddTrayIcon()
        {
            NotifyIconData data = CreateNotifyIconData();
            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private NotifyIconData CreateNotifyIconData()
        {
            return new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                WindowHandle = _windowHandle,
                Id = TrayIconId,
                Flags = NifMessage | NifIcon | NifTip,
                CallbackMessage = TrayCallbackMessage,
                IconHandle = _iconHandle,
                Tip = _toolTip
            };
        }

        private static IntPtr LoadTrayIcon()
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TxtAIEditor.ico");
            IntPtr iconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | LoadDefaultSize);
            if (iconHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return iconHandle;
        }

        private IntPtr WindowSubclassProc(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            nuint subclassId,
            nuint referenceData)
        {
            if (message == _taskbarCreatedMessage && !_disposed)
            {
                try
                {
                    AddTrayIcon();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to restore tray icon: {ex.Message}");
                }
            }
            else if (message == TrayCallbackMessage)
            {
                uint mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage == WmLButtonDoubleClick)
                {
                    _openRequested();
                }
                else if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
                {
                    ShowContextMenu();
                }

                return IntPtr.Zero;
            }

            return DefSubclassProc(windowHandle, message, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var windowActions = new Dictionary<uint, Action>();
                uint commandId = WindowMenuCommandBase;
                foreach (var window in _getWindows())
                {
                    if (commandId == uint.MaxValue)
                    {
                        break;
                    }

                    string title = string.IsNullOrWhiteSpace(window.Title)
                        ? "TxtAIEditor"
                        : window.Title;
                    if (AppendMenu(menu, MfString, commandId, title))
                    {
                        windowActions[commandId] = window.Activate;
                        commandId++;
                    }
                }

                AppendMenu(menu, MfSeparator, 0, null);
                AppendMenu(menu, MfString, OpenMenuCommand, _openText);
                AppendMenu(menu, MfString, CloseMenuCommand, _closeText);
                GetCursorPos(out Point cursorPosition);
                IntPtr previousForegroundWindow = GetForegroundWindow();
                SetForegroundWindow(_windowHandle);

                uint command;
                try
                {
                    command = TrackPopupMenu(
                        menu,
                        TpmRightButton | TpmReturnCommand,
                        cursorPosition.X,
                        cursorPosition.Y,
                        0,
                        _windowHandle,
                        IntPtr.Zero);
                }
                finally
                {
                    // TrackPopupMenu requires the owner window to be foreground,
                    // but opening the tray menu must not leave an editor window
                    // activated after the menu is dismissed.
                    PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);
                    if (previousForegroundWindow != IntPtr.Zero &&
                        previousForegroundWindow != _windowHandle)
                    {
                        SetForegroundWindow(previousForegroundWindow);
                    }
                }

                if (command == OpenMenuCommand)
                {
                    _openRequested();
                }
                else if (windowActions.TryGetValue(command, out Action? activateWindow))
                {
                    activateWindow();
                }
                else if (command == CloseMenuCommand)
                {
                    _closeRequested();
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public IntPtr WindowHandle;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr IconHandle;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Tip;

            public uint State;
            public uint StateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Info;

            public uint TimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string InfoTitle;

            public uint InfoFlags;
            public Guid GuidItem;
            public IntPtr BalloonIconHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        private delegate IntPtr SubclassProc(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            nuint subclassId,
            nuint referenceData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Shell_NotifyIconW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadImageW")]
        private static extern IntPtr LoadImage(
            IntPtr instance,
            string name,
            uint type,
            int desiredWidth,
            int desiredHeight,
            uint loadFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr iconHandle);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(
            IntPtr windowHandle,
            SubclassProc subclassProc,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(
            IntPtr windowHandle,
            SubclassProc subclassProc,
            nuint subclassId);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, nuint itemId, string? text);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            int reserved,
            IntPtr windowHandle,
            IntPtr rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }

    internal sealed record TrayWindowItem(string Title, Action Activate);
}
