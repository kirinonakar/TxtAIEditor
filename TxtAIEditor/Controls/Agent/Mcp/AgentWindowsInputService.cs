using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentWindowsInputService
    {
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

        public void FocusWindow(IntPtr window)
        {
            if (IsIconic(window))
            {
                ShowWindow(window, 9);
            }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == window)
            {
                return;
            }

            uint currentThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(window, out _);
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
                BringWindowToTop(window);
                SetForegroundWindow(window);
                SetActiveWindow(window);
                SetFocus(window);
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
            if (GetForegroundWindow() != window)
            {
                SetForegroundWindow(window);
                Thread.Sleep(50);
            }

            if (GetForegroundWindow() != window)
            {
                throw new InvalidOperationException("Windows did not activate the target window. Input was cancelled to avoid controlling another application.");
            }
        }

        public void SetCursorPosition(int screenX, int screenY)
        {
            if (!SetCursorPos(screenX, screenY))
            {
                throw new InvalidOperationException($"Windows could not move the mouse cursor to ({screenX}, {screenY}).");
            }
        }

        public void VerifyCursorPosition(int screenX, int screenY)
        {
            if (!GetCursorPos(out Point cursor) || Math.Abs(cursor.X - screenX) > 1 || Math.Abs(cursor.Y - screenY) > 1)
            {
                throw new InvalidOperationException($"Mouse cursor coordinate verification failed. requested=({screenX}, {screenY}), actual=({cursor.X}, {cursor.Y}).");
            }
        }

        public void PositionCursor(int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);
        }

        public bool TryMapKey(string key, out ushort virtualKey)
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

        public void SendUnicodeText(string text)
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

                SendInputs(new[]
                {
                    CreateKeyboardInput(0, character, KeyEventUnicode),
                    CreateKeyboardInput(0, character, KeyEventUnicode | KeyEventKeyUp)
                });
            }
        }

        public void SendShortcut(ushort modifier, ushort key)
        {
            SendVirtualKeyDown(modifier);
            SendVirtualKey(key);
            SendVirtualKeyUp(modifier);
        }

        public void SendKeyWithModifiers(ushort key, bool ctrl, bool alt, bool shift)
        {
            if (ctrl) SendVirtualKeyDown(0x11);
            if (alt) SendVirtualKeyDown(0x12);
            if (shift) SendVirtualKeyDown(0x10);
            SendVirtualKey(key);
            if (shift) SendVirtualKeyUp(0x10);
            if (alt) SendVirtualKeyUp(0x12);
            if (ctrl) SendVirtualKeyUp(0x11);
        }

        public void SendVirtualKey(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, 0), CreateKeyboardInput(key, 0, KeyEventKeyUp) });
        }

        public void SendVirtualKeyDown(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, 0) });
        }

        public void SendVirtualKeyUp(ushort key)
        {
            SendInputs(new[] { CreateKeyboardInput(key, 0, KeyEventKeyUp) });
        }

        public void SendMouseClick(string button)
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

        public void SendMouseWheel(int delta)
        {
            SendInputs(new[] { CreateMouseInput(MouseEventWheel, unchecked((uint)delta)) });
        }

        public uint GetClipboardSequence()
        {
            return GetClipboardSequenceNumber();
        }

        public bool TryReadClipboardText(out string text)
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
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
    }
}
