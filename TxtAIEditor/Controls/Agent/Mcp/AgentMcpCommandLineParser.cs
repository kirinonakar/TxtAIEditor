using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Controls
{
    internal static class AgentMcpCommandLineParser
    {
        public static IReadOnlyList<string> Split(string commandLine)
        {
            commandLine = commandLine?.Trim() ?? string.Empty;
            if (commandLine.Length == 0)
            {
                return Array.Empty<string>();
            }

            IntPtr argumentVector = CommandLineToArgvW(commandLine, out int argumentCount);
            if (argumentVector == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var arguments = new List<string>(argumentCount);
                for (int index = 0; index < argumentCount; index++)
                {
                    IntPtr argumentPointer = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                    arguments.Add(Marshal.PtrToStringUni(argumentPointer) ?? string.Empty);
                }

                return arguments;
            }
            finally
            {
                LocalFree(argumentVector);
            }
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
            out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
