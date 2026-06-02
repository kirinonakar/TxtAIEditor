using System;
using System.Runtime.InteropServices;
using System.Text;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services
{
    public class CredentialService : ICredentialService
    {
        // ----------------------------------------------------
        // Win32 API P/Invoke structures and imports
        // ----------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint DateTimeLow;
            public uint DateTimeHigh;
        }

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2; // Keep persistent on reboot

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string targetName, uint type, uint flags);

        // ----------------------------------------------------
        // Interface Realizations
        // ----------------------------------------------------

        public void WriteCredential(string targetName, string userName, string password)
        {
            var cred = new CREDENTIAL();
            cred.Type = CRED_TYPE_GENERIC;
            cred.TargetName = targetName;
            cred.UserName = userName;
            cred.Persist = CRED_PERSIST_LOCAL_MACHINE;
            cred.Comment = "TxtAIEditor LLM API Key Storage";

            byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
            cred.CredentialBlobSize = (uint)passwordBytes.Length;
            cred.CredentialBlob = Marshal.AllocCoTaskMem(passwordBytes.Length);

            try
            {
                Marshal.Copy(passwordBytes, 0, cred.CredentialBlob, passwordBytes.Length);
                bool success = CredWrite(ref cred, 0);
                if (!success)
                {
                    int lastError = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(lastError, $"자격 증명 쓰기 실패 (Error: {lastError})");
                }
            }
            finally
            {
                if (cred.CredentialBlob != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(cred.CredentialBlob);
                }
            }
        }

        public string? ReadCredential(string targetName)
        {
            IntPtr credPtr = IntPtr.Zero;
            try
            {
                bool success = CredRead(targetName, CRED_TYPE_GENERIC, 0, out credPtr);
                if (!success)
                {
                    // If target doesn't exist, simply return null instead of throwing exception
                    return null;
                }

                var cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL))!;
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                {
                    return string.Empty;
                }

                byte[] buffer = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, buffer, 0, (int)cred.CredentialBlobSize);
                
                // Unicode byte array back to C# String
                return Encoding.Unicode.GetString(buffer);
            }
            finally
            {
                if (credPtr != IntPtr.Zero)
                {
                    CredFree(credPtr);
                }
            }
        }

        public void DeleteCredential(string targetName)
        {
            // Ignore failure if credential is not present
            _ = CredDelete(targetName, CRED_TYPE_GENERIC, 0);
        }
    }
}
