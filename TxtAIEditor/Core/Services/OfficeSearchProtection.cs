using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace TxtAIEditor.Core.Services
{
    // Inspect compound-file streams without starting Office or opening a password dialog.
    internal static class OfficeSearchProtection
    {
        public static bool IsEncrypted(string filePath, CancellationToken cancellationToken)
        {
            using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Span<byte> signature = stackalloc byte[8];
                if (file.Read(signature) != signature.Length ||
                    !signature.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                {
                    return false;
                }
            }

            string extension = Path.GetExtension(filePath);
            if (!extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
            {
                // Encrypted OOXML is stored in an OLE container instead of a ZIP package.
                return true;
            }

            Marshal.ThrowExceptionForHR(StgOpenStorage(filePath, null, 0x20, IntPtr.Zero, 0, out Storage storage));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasStream(storage, "EncryptionInfo") || HasStream(storage, "EncryptedPackage"))
                {
                    return true;
                }

                if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
                {
                    return ReadStream(storage, "WordDocument", stream =>
                    {
                        byte[] header = ReadBytes(stream, 12);
                        return (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) & 0x0100) != 0;
                    });
                }

                if (extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
                {
                    return ReadStream(storage, "Current User", stream =>
                    {
                        byte[] header = ReadBytes(stream, 16);
                        return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) == 0xF3D1C4DF;
                    });
                }

                string workbook = HasStream(storage, "Workbook") ? "Workbook" : "Book";
                return ReadStream(storage, workbook, stream =>
                {
                    // FILEPASS is in the unencrypted workbook globals, before the first EOF.
                    stream.Stat(out STATSTG stat, 1);
                    long position = 0;
                    while (position + 4 <= stat.cbSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] header = ReadBytes(stream, 4);
                        ushort record = BinaryPrimitives.ReadUInt16LittleEndian(header);
                        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                        if (record == 0x002F) return true;
                        if (record == 0x000A) return false;
                        position += 4L + length;
                        if (position > stat.cbSize) throw new InvalidDataException("Invalid workbook record.");
                        stream.Seek(length, 1, IntPtr.Zero);
                    }

                    throw new InvalidDataException("Incomplete workbook globals.");
                });
            }
            finally
            {
                Marshal.ReleaseComObject(storage);
            }
        }

        private static bool HasStream(Storage storage, string name)
        {
            int result = storage.OpenStream(name, IntPtr.Zero, 0x10, 0, out IStream stream);
            if (result == unchecked((int)0x80030002)) return false; // STG_E_FILENOTFOUND
            Marshal.ThrowExceptionForHR(result);
            Marshal.ReleaseComObject(stream);
            return true;
        }

        private static bool ReadStream(Storage storage, string name, Func<IStream, bool> read)
        {
            Marshal.ThrowExceptionForHR(storage.OpenStream(name, IntPtr.Zero, 0x10, 0, out IStream stream));
            try { return read(stream); }
            finally { Marshal.ReleaseComObject(stream); }
        }

        private static byte[] ReadBytes(IStream stream, int count)
        {
            byte[] bytes = new byte[count];
            IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                stream.Read(bytes, count, read);
                if (Marshal.ReadInt32(read) != count) throw new InvalidDataException("Incomplete Office stream.");
                return bytes;
            }
            finally { Marshal.FreeCoTaskMem(read); }
        }

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int StgOpenStorage(string path, Storage? priority, uint mode, IntPtr exclude, uint reserved, out Storage storage);

        // Only the first two IStorage slots are needed; keep their native vtable order.
        [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Storage
        {
            void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
            [PreserveSig]
            int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
        }
    }
}
