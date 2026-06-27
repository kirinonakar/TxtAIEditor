using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services
{
    public sealed class StickyNoteService : IStickyNoteService
    {
        private readonly Func<string, string, string> _getString;
        private Window? _stickyNoteWindow;
        private TextBox? _stickyNoteTextBox;

        public StickyNoteService(Func<string, string, string>? getString = null)
        {
            _getString = getString ?? ((_, fallback) => fallback);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public void ShowOrActivate(Window ownerWindow)
        {
            if (_stickyNoteWindow != null)
            {
                _stickyNoteWindow.Activate();
                return;
            }

            string stickyPath = GetStickyNotePath();
            string initialText = File.Exists(stickyPath) ? File.ReadAllText(stickyPath, Encoding.UTF8) : string.Empty;

            var root = new Grid
            {
                Padding = new Thickness(10),
                RowSpacing = 8,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"]
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _stickyNoteTextBox = new TextBox
            {
                Text = initialText,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                PlaceholderText = _getString("StickyNotePlaceholder", "빠르게 메모..."),
                MinWidth = 320,
                MinHeight = 240,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI, Malgun Gothic")
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_stickyNoteTextBox, ScrollBarVisibility.Auto);
            Grid.SetRow(_stickyNoteTextBox, 0);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };
            var saveButton = new Button { Content = _getString("SaveFile", "저장") };
            var closeButton = new Button { Content = _getString("StickyNoteCloseButton", "닫기") };
            actions.Children.Add(saveButton);
            actions.Children.Add(closeButton);
            Grid.SetRow(actions, 1);

            root.Children.Add(_stickyNoteTextBox);
            root.Children.Add(actions);

            _stickyNoteWindow = new Window
            {
                Title = _getString("StickyNoteTitle", "스티커 노트"),
                Content = root
            };

            saveButton.Click += (_, __) => SaveStickyNote();
            closeButton.Click += (_, __) => _stickyNoteWindow?.Close();
            _stickyNoteWindow.Closed += (_, __) =>
            {
                SaveStickyNote();
                _stickyNoteWindow = null;
                _stickyNoteTextBox = null;
            };

            _stickyNoteWindow.Activate();
            ApplyTopMost(_stickyNoteWindow, true);
        }

        public void ApplyTopMost(Window window, bool topMost)
        {
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                SetWindowPos(hwnd, topMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply topmost: {ex.Message}");
            }
        }

        private static string GetStickyNotePath()
        {
            string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".TxtAIEditor");
            Directory.CreateDirectory(settingsDir);
            return Path.Combine(settingsDir, "sticky_note.txt");
        }

        private void SaveStickyNote()
        {
            try
            {
                if (_stickyNoteTextBox == null) return;
                File.WriteAllText(GetStickyNotePath(), _stickyNoteTextBox.Text ?? string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save sticky note: {ex.Message}");
            }
        }
    }
}
