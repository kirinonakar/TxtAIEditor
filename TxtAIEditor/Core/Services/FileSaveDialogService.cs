using System;
using System.IO;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Interfaces;
using WinRT.Interop;

namespace TxtAIEditor.Core.Services
{
    public sealed class FileSaveDialogService : IFileSaveDialogService
    {
        private const int OFN_HIDEREADONLY = 0x00000004;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        public string? ShowSaveDialog(Window owner, string suggestedName, string? initialDirectory)
        {
            string filter = "텍스트 파일 (*.txt)\0*.txt\0마크다운 파일 (*.md;*.markdown)\0*.md;*.markdown\0HTML 파일 (*.html)\0*.html\0LaTeX 파일 (*.tex)\0*.tex\0\0";
            var fileNameBuffer = new string('\0', 1024);
            if (!string.IsNullOrEmpty(suggestedName))
            {
                fileNameBuffer = suggestedName + fileNameBuffer.Substring(suggestedName.Length);
            }

            var ofn = new OPENFILENAME
            {
                lStructSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(OPENFILENAME)),
                hwndOwner = WindowNative.GetWindowHandle(owner),
                lpstrFilter = filter,
                lpstrFile = fileNameBuffer,
                nMaxFile = 1024,
                lpstrInitialDir = initialDirectory,
                Flags = OFN_HIDEREADONLY | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                nFilterIndex = 1
            };

            if (!GetSaveFileNameW(ref ofn))
            {
                return null;
            }

            string selectedPath = ofn.lpstrFile ?? string.Empty;
            int nullPos = selectedPath.IndexOf('\0');
            if (nullPos >= 0)
            {
                selectedPath = selectedPath.Substring(0, nullPos);
            }

            if (!string.IsNullOrEmpty(selectedPath))
            {
                if (ofn.nFilterIndex == 1)
                {
                    if (!selectedPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPath += ".txt";
                    }
                }
                else if (ofn.nFilterIndex == 2)
                {
                    if (!selectedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                        !selectedPath.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPath += ".md";
                    }
                }
                else if (ofn.nFilterIndex == 3)
                {
                    if (!selectedPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                        !selectedPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPath += ".html";
                    }
                }
                else if (ofn.nFilterIndex == 4)
                {
                    if (!selectedPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPath += ".tex";
                    }
                }
            }

            return string.IsNullOrEmpty(selectedPath) ? null : selectedPath;
        }

        [System.Runtime.InteropServices.DllImport("comdlg32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpstrInitialDir;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }
    }
}
