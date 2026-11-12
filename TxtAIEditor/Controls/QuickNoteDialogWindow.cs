using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace TxtAIEditor.Controls
{
    internal static class QuickNoteDialogWindow
    {
        public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, nint owner)
        {
            var root = new Grid
            {
                RequestedTheme = dialog.RequestedTheme,
                Background = GetBackground(dialog.RequestedTheme)
            };
            long themeCallback = dialog.RegisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty, (_, _) =>
            {
                root.RequestedTheme = dialog.RequestedTheme;
                root.Background = GetBackground(dialog.RequestedTheme);
            });
            var window = new Window { Content = root, Title = dialog.Title as string ?? string.Empty };
            var rootReady = new TaskCompletionSource<XamlRoot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            root.Loaded += (_, _) => rootReady.TrySetResult(root.XamlRoot);
            window.Closed += (_, _) =>
            {
                rootReady.TrySetResult(null);
                closed.TrySetResult(ContentDialogResult.None);
            };

            try
            {
                var appWindow = window.AppWindow;
                var presenter = OverlappedPresenter.CreateForDialog();
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsAlwaysOnTop = true;

                // Windows hides owned windows when their owner is minimized.
                if (owner != nint.Zero && !IsIconic(owner))
                {
                    const int ownerIndex = -8;
                    nint previous = SetWindowLongPtr(WindowNative.GetWindowHandle(window), ownerIndex, owner);
                    presenter.IsModal = previous != nint.Zero || Marshal.GetLastWin32Error() == 0;
                }

                appWindow.SetPresenter(presenter);
                appWindow.IsShownInSwitchers = false;
                var area = (DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(owner),
                    DisplayAreaFallback.Nearest) ?? DisplayArea.Primary).WorkArea;
                double scale = Math.Max(96u, GetDpiForWindow(owner)) / 96.0;
                int width = Math.Min((int)Math.Round(440 * scale), area.Width);
                int height = Math.Min((int)Math.Round(300 * scale), area.Height);
                appWindow.MoveAndResize(new RectInt32(
                    area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
                window.Activate();

                if (await rootReady.Task is not XamlRoot xamlRoot || closed.Task.IsCompleted)
                {
                    return ContentDialogResult.None;
                }

                dialog.XamlRoot = xamlRoot;
                Task<ContentDialogResult> result = dialog.ShowAsync().AsTask();
                return await await Task.WhenAny(result, closed.Task);
            }
            finally
            {
                dialog.UnregisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty, themeCallback);
                if (!closed.Task.IsCompleted)
                {
                    window.Close();
                }
            }
        }

        private static Brush GetBackground(ElementTheme theme)
        {
            ResourceDictionary resources = Application.Current.Resources;
            if (resources.ContainsKey("ActiveTheme") &&
                resources.TryGetValue("ContentDialogBackground", out object value) && value is Brush brush)
            {
                return brush;
            }

            bool isDark = theme == ElementTheme.Dark ||
                (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
            return new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Colors.White);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(nint window);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint window);
    }
}
