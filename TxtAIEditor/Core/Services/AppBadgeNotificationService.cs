using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TxtAIEditor.Core.Services
{
    internal static class AppBadgeNotificationService
    {
        private const string DirectRunAppUserModelId = "kirinonakar.TxtAIEditor.App";
        private const int IconSize = 32;
        private const uint DIB_RGB_COLORS = 0;
        private const uint BI_RGB = 0;
        private static readonly object SyncRoot = new();
        private static IntPtr _windowHandle;
        private static ITaskbarList3? _taskbarList;
        private static IntPtr _overlayIcon;

        public static void InitializeProcessAppUserModelId()
        {
            try
            {
                int hr = SetCurrentProcessExplicitAppUserModelID(DirectRunAppUserModelId);
                if (hr < 0)
                {
                    Debug.WriteLine($"Failed to set process AppUserModelID: 0x{hr:X8}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize process AppUserModelID: {ex.Message}");
            }
        }

        public static void Initialize(IntPtr windowHandle)
        {
            lock (SyncRoot)
            {
                _windowHandle = windowHandle;
            }

            UpdateShellOverlayBadge(0);
        }

        public static void UpdateBadge(int count)
        {
            UpdatePackagedBadge(count);
            UpdateShellOverlayBadge(count);
        }

        private static void UpdatePackagedBadge(int count)
        {
            try
            {
                BadgeUpdater updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                int badgeValue = Math.Min(99, Math.Max(1, count));
                var xml = new XmlDocument();
                xml.LoadXml(string.Format(
                    CultureInfo.InvariantCulture,
                    "<badge value=\"{0}\"/>",
                    badgeValue));
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update app badge: {ex.Message}");
            }
        }

        private static void UpdateShellOverlayBadge(int count)
        {
            lock (SyncRoot)
            {
                try
                {
                    if (_windowHandle == IntPtr.Zero || !EnsureTaskbarList())
                    {
                        return;
                    }

                    if (count <= 0)
                    {
                        _taskbarList?.SetOverlayIcon(_windowHandle, IntPtr.Zero, null);
                        DestroyCurrentOverlayIcon();
                        return;
                    }

                    IntPtr icon = CreateBadgeIcon(count);
                    if (icon == IntPtr.Zero)
                    {
                        return;
                    }

                    string description = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} background agent session(s) completed",
                        count);
                    int hr = _taskbarList?.SetOverlayIcon(_windowHandle, icon, description) ?? -1;
                    if (hr < 0)
                    {
                        DestroyIcon(icon);
                        Debug.WriteLine($"Failed to set taskbar overlay icon: 0x{hr:X8}");
                        return;
                    }

                    DestroyCurrentOverlayIcon();
                    _overlayIcon = icon;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to update taskbar overlay badge: {ex.Message}");
                }
            }
        }

        private static bool EnsureTaskbarList()
        {
            if (_taskbarList != null)
            {
                return true;
            }

            try
            {
                var taskbarList = (ITaskbarList3)new CTaskbarList();
                int hr = taskbarList.HrInit();
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                _taskbarList = taskbarList;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize taskbar overlay support: {ex.Message}");
                return false;
            }
        }

        private static void DestroyCurrentOverlayIcon()
        {
            if (_overlayIcon == IntPtr.Zero)
            {
                return;
            }

            DestroyIcon(_overlayIcon);
            _overlayIcon = IntPtr.Zero;
        }

        private static IntPtr CreateBadgeIcon(int count)
        {
            int[] pixels = CreateBadgePixels(count);
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr colorBitmap = IntPtr.Zero;
            IntPtr maskBitmap = IntPtr.Zero;

            try
            {
                var bitmapInfo = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = IconSize,
                        biHeight = -IconSize,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };

                colorBitmap = CreateDIBSection(
                    screenDc,
                    ref bitmapInfo,
                    DIB_RGB_COLORS,
                    out IntPtr bits,
                    IntPtr.Zero,
                    0);
                if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                Marshal.Copy(pixels, 0, bits, pixels.Length);
                maskBitmap = CreateOpaqueMaskBitmap();
                if (maskBitmap == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var iconInfo = new ICONINFO
                {
                    fIcon = true,
                    hbmColor = colorBitmap,
                    hbmMask = maskBitmap
                };
                return CreateIconIndirect(ref iconInfo);
            }
            finally
            {
                if (colorBitmap != IntPtr.Zero)
                {
                    DeleteObject(colorBitmap);
                }

                if (maskBitmap != IntPtr.Zero)
                {
                    DeleteObject(maskBitmap);
                }

                if (screenDc != IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private static IntPtr CreateOpaqueMaskBitmap()
        {
            int rowBytes = ((IconSize + 15) / 16) * 2;
            int byteCount = rowBytes * IconSize;
            IntPtr maskBits = Marshal.AllocHGlobal(byteCount);
            try
            {
                for (int i = 0; i < byteCount; i++)
                {
                    Marshal.WriteByte(maskBits, i, 0);
                }

                return CreateBitmap(IconSize, IconSize, 1, 1, maskBits);
            }
            finally
            {
                Marshal.FreeHGlobal(maskBits);
            }
        }

        private static int[] CreateBadgePixels(int count)
        {
            var pixels = new int[IconSize * IconSize];
            const double center = (IconSize - 1) / 2.0;
            const double radius = 14.5;

            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    double dx = x - center;
                    double dy = y - center;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance > radius + 0.8)
                    {
                        continue;
                    }

                    double edgeAlpha = distance <= radius
                        ? 1.0
                        : radius + 0.8 - distance;
                    byte alpha = (byte)Math.Clamp((int)Math.Round(edgeAlpha * 255), 0, 255);
                    bool edge = distance > radius - 1.2;
                    pixels[(y * IconSize) + x] = PackColor(
                        alpha,
                        edge ? (byte)153 : (byte)220,
                        edge ? (byte)27 : (byte)38,
                        edge ? (byte)27 : (byte)38);
                }
            }

            DrawLabel(pixels, FormatOverlayLabel(count));
            return pixels;
        }

        private static string FormatOverlayLabel(int count)
        {
            if (count <= 9)
            {
                return count.ToString(CultureInfo.InvariantCulture);
            }

            if (count <= 20)
            {
                return count.ToString(CultureInfo.InvariantCulture);
            }

            return "20+";
        }

        private static void DrawLabel(int[] pixels, string label)
        {
            int scale = label.Length <= 1 ? 5 : label.Length <= 2 ? 3 : 2;
            int glyphWidth = 3;
            int glyphHeight = 5;
            int spacing = scale;
            int labelWidth = (label.Length * glyphWidth * scale) + ((label.Length - 1) * spacing);
            int labelHeight = glyphHeight * scale;
            int startX = Math.Max(0, (IconSize - labelWidth) / 2);
            int startY = Math.Max(0, (IconSize - labelHeight) / 2);
            int cursorX = startX;

            foreach (char ch in label)
            {
                DrawGlyph(pixels, ch, cursorX, startY, scale, PackColor(255, 255, 255, 255));
                cursorX += (glyphWidth * scale) + spacing;
            }
        }

        private static void DrawGlyph(int[] pixels, char ch, int x, int y, int scale, int color)
        {
            string[] rows = GetGlyphRows(ch);
            for (int row = 0; row < rows.Length; row++)
            {
                string rowText = rows[row];
                for (int col = 0; col < rowText.Length; col++)
                {
                    if (rowText[col] != '1')
                    {
                        continue;
                    }

                    FillRect(pixels, x + (col * scale), y + (row * scale), scale, scale, color);
                }
            }
        }

        private static string[] GetGlyphRows(char ch)
        {
            return ch switch
            {
                '0' => new[] { "111", "101", "101", "101", "111" },
                '1' => new[] { "010", "110", "010", "010", "111" },
                '2' => new[] { "111", "001", "111", "100", "111" },
                '3' => new[] { "111", "001", "111", "001", "111" },
                '4' => new[] { "101", "101", "111", "001", "001" },
                '5' => new[] { "111", "100", "111", "001", "111" },
                '6' => new[] { "111", "100", "111", "101", "111" },
                '7' => new[] { "111", "001", "010", "010", "010" },
                '8' => new[] { "111", "101", "111", "101", "111" },
                '9' => new[] { "111", "101", "111", "001", "111" },
                '+' => new[] { "000", "010", "111", "010", "000" },
                _ => new[] { "000", "000", "000", "000", "000" }
            };
        }

        private static void FillRect(int[] pixels, int x, int y, int width, int height, int color)
        {
            for (int yy = y; yy < y + height; yy++)
            {
                if (yy < 0 || yy >= IconSize)
                {
                    continue;
                }

                for (int xx = x; xx < x + width; xx++)
                {
                    if (xx < 0 || xx >= IconSize)
                    {
                        continue;
                    }

                    pixels[(yy * IconSize) + xx] = color;
                }
            }
        }

        private static int PackColor(byte alpha, byte red, byte green, byte blue)
        {
            return unchecked((int)(((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue));
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList
        {
        }

        [ComImport]
        [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EE775")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            [PreserveSig]
            int HrInit();

            [PreserveSig]
            int AddTab(IntPtr hwnd);

            [PreserveSig]
            int DeleteTab(IntPtr hwnd);

            [PreserveSig]
            int ActivateTab(IntPtr hwnd);

            [PreserveSig]
            int SetActiveAlt(IntPtr hwnd);

            [PreserveSig]
            int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

            [PreserveSig]
            int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

            [PreserveSig]
            int SetProgressState(IntPtr hwnd, int flags);

            [PreserveSig]
            int RegisterTab(IntPtr tabHwnd, IntPtr mdiHwnd);

            [PreserveSig]
            int UnregisterTab(IntPtr tabHwnd);

            [PreserveSig]
            int SetTabOrder(IntPtr tabHwnd, IntPtr insertBeforeHwnd);

            [PreserveSig]
            int SetTabActive(IntPtr tabHwnd, IntPtr mdiHwnd, uint reserved);

            [PreserveSig]
            int ThumbBarAddButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

            [PreserveSig]
            int ThumbBarUpdateButtons(IntPtr hwnd, uint buttonCount, IntPtr buttons);

            [PreserveSig]
            int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

            [PreserveSig]
            int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

            [PreserveSig]
            int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);

            [PreserveSig]
            int SetThumbnailClip(IntPtr hwnd, IntPtr rect);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(
            int width,
            int height,
            uint planes,
            uint bitsPerPixel,
            IntPtr bits);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);
    }
}
