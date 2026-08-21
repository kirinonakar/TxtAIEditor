using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TxtAIEditor.Controls
{
    internal static class ShellShortcutResolver
    {
        private const string ShortcutExtension = ".lnk";

        public static bool IsShortcut(string path)
        {
            return string.Equals(
                Path.GetExtension(path),
                ShortcutExtension,
                StringComparison.OrdinalIgnoreCase);
        }

        public static string? ResolveTarget(string shortcutPath)
        {
            if (!IsShortcut(shortcutPath))
            {
                return null;
            }

            try
            {
                Type shellLinkType = Type.GetTypeFromCLSID(
                    new Guid("00021401-0000-0000-C000-000000000046"))
                    ?? throw new InvalidOperationException("Failed to get ShellLink type");
                object shellLink = Activator.CreateInstance(shellLinkType)!;

                ((IPersistFile)shellLink).Load(shortcutPath, 0);
                var link = (IShellLinkW)shellLink;
                link.Resolve(IntPtr.Zero, 0x01);

                var targetBuffer = new StringBuilder(1024);
                link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0x00);
                string targetPath = targetBuffer.ToString();

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    targetBuffer.Clear();
                    link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0x04);
                    targetPath = targetBuffer.ToString();
                }

                if (string.IsNullOrWhiteSpace(targetPath) &&
                    link.GetIDList(out IntPtr pidl) == 0 &&
                    pidl != IntPtr.Zero)
                {
                    try
                    {
                        var idListBuffer = new StringBuilder(1024);
                        if (SHGetPathFromIDListW(pidl, idListBuffer))
                        {
                            targetPath = idListBuffer.ToString();
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }

                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    targetPath = Environment.ExpandEnvironmentVariables(targetPath);
                }

                if (!string.IsNullOrWhiteSpace(targetPath) &&
                    !Directory.Exists(targetPath) &&
                    !File.Exists(targetPath) &&
                    !Path.IsPathRooted(targetPath))
                {
                    string? shortcutDirectory = Path.GetDirectoryName(shortcutPath);
                    if (!string.IsNullOrEmpty(shortcutDirectory))
                    {
                        string combinedPath = Path.GetFullPath(Path.Combine(shortcutDirectory, targetPath));
                        if (Directory.Exists(combinedPath) || File.Exists(combinedPath))
                        {
                            targetPath = combinedPath;
                        }
                    }
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to resolve shortcut target for '{shortcutPath}': {ex.Message}");
                return null;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHGetPathFromIDListW(
            IntPtr pidl,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path);

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr data, uint flags);

            [PreserveSig]
            int GetIDList(out IntPtr idList);

            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr windowHandle, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);

            [PreserveSig]
            int IsDirty();

            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }
    }
}
