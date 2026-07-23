using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using TxtAIEditor.Core.Models;
using Windows.Graphics;

namespace TxtAIEditor.Core.Services
{
    public static class WindowPlacementService
    {
        private const double DefaultDpi = 96.0;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        public static void SetWindowIcon(AppWindow appWindow)
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TxtAIEditor.ico");
                if (File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set window icon: {ex.Message}");
            }
        }

        public static void ApplySavedWindowPlacement(AppWindow appWindow, EditorSettings settings)
        {
            try
            {
                if (settings.WindowWidth < 400 || settings.WindowHeight < 300)
                {
                    return;
                }

                bool hasSavedPlacement = settings.WindowX >= 0 && settings.WindowY >= 0;
                var size = hasSavedPlacement
                    ? new SizeInt32(settings.WindowWidth, settings.WindowHeight)
                    : GetDpiScaledInitialSize(appWindow, settings.WindowWidth, settings.WindowHeight);

                if (hasSavedPlacement)
                {
                    appWindow.MoveAndResize(new RectInt32(settings.WindowX, settings.WindowY, size.Width, size.Height));
                }
                else
                {
                    appWindow.Resize(size);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore window placement: {ex.Message}");
            }
        }

        private static SizeInt32 GetDpiScaledInitialSize(AppWindow appWindow, int width, int height)
        {
            IntPtr hWnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi > 0 ? dpi / DefaultDpi : 1.0;

            return new SizeInt32(
                Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        public static void CaptureRestoredWindowPlacement(AppWindow appWindow, EditorSettings settings)
        {
            var position = appWindow.Position;
            var size = appWindow.Size;

            var overlappedPresenter = appWindow.Presenter as OverlappedPresenter;
            bool isRestored = overlappedPresenter == null || overlappedPresenter.State == OverlappedPresenterState.Restored;

            if (isRestored && size.Width >= 400 && size.Height >= 300)
            {
                settings.WindowX = position.X;
                settings.WindowY = position.Y;
                settings.WindowWidth = size.Width;
                settings.WindowHeight = size.Height;
            }
        }
    }
}
