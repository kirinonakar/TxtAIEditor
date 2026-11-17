using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace TxtAIEditor.Controls
{
    internal static class AgentWindowsFileMetadata
    {
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const FileShare SharedMetadataAccess = FileShare.ReadWrite | FileShare.Delete;

        public static bool AreSameFile(SafeFileHandle first, SafeFileHandle second) =>
            GetFileInformationByHandle(first, out FileInformation left) &&
            GetFileInformationByHandle(second, out FileInformation right) &&
            left.VolumeSerialNumber == right.VolumeSerialNumber &&
            left.FileIndexHigh == right.FileIndexHigh && left.FileIndexLow == right.FileIndexLow;

        public static bool AreSameDirectory(string first, string second)
        {
            using SafeFileHandle left = OpenMetadata(first, BackupSemantics);
            using SafeFileHandle right = OpenMetadata(second, BackupSemantics);
            return !left.IsInvalid && !right.IsInvalid && AreSameFile(left, right);
        }

        public static bool IsCloudPlaceholder(string path)
        {
            const uint OpenReparsePoint = 0x00200000;
            const int FileAttributeTagInfo = 9;
            const uint CloudTag = 0x9000001A;
            const uint CloudVariantMask = 0x0000F000;
            using SafeFileHandle handle = OpenMetadata(path, BackupSemantics | OpenReparsePoint);
            return !handle.IsInvalid && GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
                out FileAttributeTag information, sizeof(uint) * 2) &&
                (information.Tag & ~CloudVariantMask) == CloudTag;
        }

        private static SafeFileHandle OpenMetadata(string path, uint flags) =>
            CreateFileW(path, 0, SharedMetadataAccess, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
            out FileAttributeTag information, uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTag
        {
            public uint Attributes;
            public uint Tag;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, FileShare share,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public FILETIME CreationTime;
            public FILETIME AccessTime;
            public FILETIME WriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint LinkCount;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
