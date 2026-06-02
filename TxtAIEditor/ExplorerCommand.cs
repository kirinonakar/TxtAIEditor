using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TxtAIEditor
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid dbh, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    public interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(uint flags, [In] ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList([In] ref Guid keyType, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
    public interface IExplorerCommand
    {
        [PreserveSig]
        int GetTitle([In] IntPtr psiItemArray, out IntPtr ppszName);

        [PreserveSig]
        int GetIcon([In] IntPtr psiItemArray, out IntPtr ppszIcon);

        [PreserveSig]
        int GetToolTip([In] IntPtr psiItemArray, out IntPtr ppszInfotip);

        [PreserveSig]
        int GetCanonicalName(out Guid pguidCommandName);

        [PreserveSig]
        int GetState([In] IntPtr psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCommandState);

        [PreserveSig]
        int Invoke([In] IntPtr psiItemArray, [In] IntPtr pbc);

        [PreserveSig]
        int GetFlags(out uint pdwFlags);

        [PreserveSig]
        int EnumSubCommands(out IntPtr ppEnum);
    }

    [Guid("8D0B4C32-6D84-4B8A-8F3B-7E5408BEF1A1")]
    [ComVisible(true)]
    public class TxtAIEditorExplorerCommand : IExplorerCommand
    {
        private const uint SigdnFileSystemPath = 0x80058000;

        public int GetTitle(IntPtr psiItemArray, out IntPtr ppszName)
        {
            App.EnterComCall();
            ppszName = IntPtr.Zero;
            try
            {
                ppszName = Marshal.StringToCoTaskMemUni("Open in TxtAIEditor");
                return 0;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int GetIcon(IntPtr psiItemArray, out IntPtr ppszIcon)
        {
            App.EnterComCall();
            ppszIcon = IntPtr.Zero;
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TxtAIEditor.ico");
                ppszIcon = Marshal.StringToCoTaskMemUni(iconPath);
                return 0;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int GetToolTip(IntPtr psiItemArray, out IntPtr ppszInfotip)
        {
            App.EnterComCall();
            ppszInfotip = IntPtr.Zero;
            try
            {
                return unchecked((int)0x80004001);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int GetCanonicalName(out Guid pguidCommandName)
        {
            App.EnterComCall();
            pguidCommandName = Guid.Empty;
            try
            {
                return unchecked((int)0x80004001);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int GetState(IntPtr psiItemArray, bool fOkToBeSlow, out uint pCommandState)
        {
            App.EnterComCall();
            pCommandState = 0;
            try
            {
                return 0;
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int Invoke(IntPtr psiItemArray, IntPtr pbc)
        {
            App.EnterComCall();
            try
            {
                string? path = GetFirstSelectedPath(psiItemArray);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(AppContext.BaseDirectory, "TxtAIEditor.exe"),
                        Arguments = QuoteArgument(path),
                        UseShellExecute = true
                    });
                }

                return 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Explorer command invoke error: {ex.Message}");
                return unchecked((int)0x80004005);
            }
            finally
            {
                App.LeaveComCall();
                App.NotifyComInvokeCompleted();
            }
        }

        public int GetFlags(out uint pdwFlags)
        {
            App.EnterComCall();
            pdwFlags = 0;
            try
            {
                return 0;
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int EnumSubCommands(out IntPtr ppEnum)
        {
            App.EnterComCall();
            ppEnum = IntPtr.Zero;
            try
            {
                return unchecked((int)0x80004001);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        private static string? GetFirstSelectedPath(IntPtr psiItemArray)
        {
            if (psiItemArray == IntPtr.Zero)
            {
                return null;
            }

            IShellItemArray? array = null;
            IShellItem? item = null;
            try
            {
                array = (IShellItemArray)Marshal.GetObjectForIUnknown(psiItemArray);
                array.GetCount(out uint count);
                if (count == 0)
                {
                    return null;
                }

                array.GetItemAt(0, out item);
                item.GetDisplayName(SigdnFileSystemPath, out string path);
                return path;
            }
            finally
            {
                if (item != null)
                {
                    Marshal.ReleaseComObject(item);
                }

                if (array != null)
                {
                    Marshal.ReleaseComObject(array);
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }
    }

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer(bool fLock);
    }

    [ComVisible(true)]
    public class TxtAIEditorExplorerCommandFactory : IClassFactory
    {
        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            App.EnterComCall();
            ppvObject = IntPtr.Zero;

            if (pUnkOuter != IntPtr.Zero)
            {
                App.LeaveComCall();
                return unchecked((int)0x80040110);
            }

            try
            {
                var command = new TxtAIEditorExplorerCommand();
                IntPtr pUnk = Marshal.GetIUnknownForObject(command);
                int hr = Marshal.QueryInterface(pUnk, in riid, out ppvObject);
                Marshal.Release(pUnk);
                return hr;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
            finally
            {
                App.LeaveComCall();
            }
        }

        public int LockServer(bool fLock)
        {
            App.SetComServerLock(fLock);
            return 0;
        }
    }
}
