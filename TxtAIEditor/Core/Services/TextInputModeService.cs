using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Core.Services
{
    public static class TextInputModeService
    {
        private const int InsertVirtualKey = 0x2D;
        private const int ToggleMask = 0x01;

        // Must run on the UI thread that processes the text control's input.
        public static void EnsureInsertMode()
        {
            if ((GetKeyState(InsertVirtualKey) & ToggleMask) == 0)
            {
                return;
            }

            var keyboardState = new byte[256];
            if (!GetKeyboardState(keyboardState))
            {
                Debug.WriteLine($"Failed to read keyboard state: {Marshal.GetLastWin32Error()}");
                return;
            }

            // Clear only Insert's toggle bit; retain pressed keys and all other toggles.
            keyboardState[InsertVirtualKey] &= unchecked((byte)~ToggleMask);
            if (!SetKeyboardState(keyboardState))
            {
                Debug.WriteLine($"Failed to reset Insert key state: {Marshal.GetLastWin32Error()}");
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState([Out] byte[] keyboardState);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKeyboardState([In] byte[] keyboardState);
    }
}
