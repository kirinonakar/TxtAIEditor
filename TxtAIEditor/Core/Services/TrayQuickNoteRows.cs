using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Core.Services
{
    internal sealed class TrayQuickNoteRows : IDisposable
    {
        private const uint OwnerDraw = 0x0100;
        private const uint Grayed = 0x0001;
        private const uint ByPosition = 0x0400;
        private const uint MenuItemType = 1;
        private const uint Selected = 0x0001;
        private const uint WmDrawItem = 0x002B;
        private const uint WmMeasureItem = 0x002C;
        private const uint WmMenuSelect = 0x011F;
        private const uint WmKeyDown = 0x0100;
        private const uint WmLeftButtonDown = 0x0201;
        private const uint WmLeftButtonUp = 0x0202;
        private const uint WmRightButtonDown = 0x0204;
        private const uint WmRightButtonUp = 0x0205;
        private const uint MnGetHmenu = 0x01E1;
        private const uint VkDelete = 0x2E;
        private const int KeyPreviouslyDown = 1 << 30;
        private const uint RedrawInvalidate = 0x0001;
        private const uint RedrawUpdateNow = 0x0100;
        private const int MessageFilterHook = -1;
        private const int MenuMessage = 2;
        private const int RowWidth = 176;

        private readonly nint _menu;
        private readonly string _deleteText;
        private readonly Dictionary<uint, Row> _rows = new();
        private readonly HookProc _filter;
        private readonly nint _hook;
        private readonly Font _font;
        private readonly int _dpi;
        private readonly int _buttonWidth;
        private readonly int _rowHeight;
        private Row? _highlighted;
        private Row? _pressed;
        private uint _pressedButton;

        public TrayQuickNoteRows(nint owner, nint menu, string deleteText)
        {
            _menu = menu;
            _deleteText = deleteText;
            _dpi = Math.Max(96, (int)GetDpiForWindow(owner));
            using var systemFont = SystemFonts.MenuFont ?? new Font("Segoe UI", 9);
            _font = new Font(systemFont.FontFamily, systemFont.SizeInPoints * _dpi / 72f, systemFont.Style, GraphicsUnit.Pixel);
            using var graphics = Graphics.FromHwnd(nint.Zero);
            _buttonWidth = (int)Math.Ceiling(graphics.MeasureString(_deleteText, _font).Width) + Scale(12);
            _rowHeight = Math.Max(Scale(29), (int)Math.Ceiling(_font.GetHeight(graphics)) + Scale(12));

            _filter = FilterMenuMessage;
            _hook = SetWindowsHookEx(MessageFilterHook, _filter, nint.Zero, GetCurrentThreadId());
            if (_hook == nint.Zero)
            {
                AppErrorHandler.Report(new Win32Exception(Marshal.GetLastWin32Error(), "Could not track quick note menu buttons."));
            }
        }

        public bool Append(uint commandId, TrayQuickNoteItem item)
        {
            if (_hook == nint.Zero)
            {
                return false;
            }

            string title = item.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            // Native accessibility providers read MSAAMENUINFO from the owner-draw item data.
            int infoSize = Marshal.SizeOf<AccessibleMenuItem>();
            nint info = Marshal.AllocHGlobal(infoSize + (title.Length + 1) * sizeof(char));
            nint text = info + infoSize;
            Marshal.Copy((title + '\0').ToCharArray(), 0, text, title.Length + 1);
            Marshal.StructureToPtr(new AccessibleMenuItem
            {
                Signature = 0xAA0DF00D,
                TextLength = (uint)title.Length,
                Text = text
            }, info, false);

            if (!AppendMenu(_menu, OwnerDraw, commandId, info))
            {
                Marshal.FreeHGlobal(info);
                return false;
            }

            _rows.Add(commandId, new Row(title, item.Delete, info, (uint)(GetMenuItemCount(_menu) - 1)));
            return true;
        }

        public bool HandleMenuMessage(uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WmMenuSelect:
                    _highlighted = lParam == _menu && _rows.TryGetValue((ushort)wParam, out Row? row) ? row : null;
                    break;
                case WmMeasureItem when wParam == nint.Zero && lParam != nint.Zero:
                    var measure = Marshal.PtrToStructure<MeasureItem>(lParam);
                    if (measure.ControlType == MenuItemType && _rows.ContainsKey(measure.ItemId))
                    {
                        measure.Width = (uint)Math.Max(Scale(RowWidth), _buttonWidth + Scale(96));
                        measure.Height = (uint)_rowHeight;
                        Marshal.StructureToPtr(measure, lParam, false);
                        return true;
                    }
                    break;
                case WmDrawItem when wParam == nint.Zero && lParam != nint.Zero:
                    var draw = Marshal.PtrToStructure<DrawItem>(lParam);
                    if (draw.ControlType == MenuItemType && draw.Menu == _menu &&
                        _rows.TryGetValue(draw.ItemId, out Row? drawnRow))
                    {
                        Draw(drawnRow, draw);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private void Draw(Row row, DrawItem item)
        {
            Rectangle bounds = item.Bounds.ToRectangle();
            Rectangle button = GetDeleteBounds(bounds);
            var title = Rectangle.FromLTRB(bounds.Left + Scale(20), bounds.Top, button.Left - Scale(8), bounds.Bottom);
            bool selected = !row.Deleted && (item.State & Selected) != 0;
            using var graphics = Graphics.FromHdc(item.DeviceContext);
            graphics.SetClip(bounds);
            using var background = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Menu);
            using var foreground = new SolidBrush(row.Deleted ? SystemColors.GrayText :
                selected ? SystemColors.HighlightText : SystemColors.MenuText);
            using var titleFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            graphics.FillRectangle(background, bounds);
            graphics.DrawString(row.Title, _font, foreground, title, titleFormat);
            if (row.Deleted)
            {
                return;
            }

            using var border = new Pen(selected ? SystemColors.HighlightText : SystemColors.ControlDark);
            using var buttonFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawRectangle(border, button.X, button.Y, button.Width - 1, button.Height - 1);
            graphics.DrawString(_deleteText, _font, foreground, button, buttonFormat);
        }

        private Rectangle GetDeleteBounds(Rectangle row) =>
            new(row.Right - Scale(7) - _buttonWidth, row.Top + Scale(3), _buttonWidth, row.Height - Scale(6));

        private Row? GetDeleteAt(Point point)
        {
            int position = MenuItemFromPoint(nint.Zero, _menu, point);
            if (position < 0 || !_rows.TryGetValue(GetMenuItemID(_menu, position), out Row? row) || row.Deleted ||
                !GetMenuItemRect(nint.Zero, _menu, (uint)position, out NativeRect bounds))
            {
                return null;
            }

            return GetDeleteBounds(bounds.ToRectangle()).Contains(point) ? row : null;
        }

        private nint FilterMenuMessage(int code, nint wParam, nint lParam)
        {
            if (code == MenuMessage && lParam != nint.Zero)
            {
                var message = Marshal.PtrToStructure<Message>(lParam);
                if (message.Id is WmLeftButtonDown or WmRightButtonDown)
                {
                    _pressed = GetDeleteAt(message.Point);
                    _pressedButton = message.Id + 1;
                    if (_pressed != null)
                    {
                        return 1;
                    }
                }
                else if (message.Id == _pressedButton && _pressed is Row pressed)
                {
                    _pressed = null;
                    if (ReferenceEquals(GetDeleteAt(message.Point), pressed))
                    {
                        RequestDelete(pressed);
                    }
                    return 1;
                }
                else if (message.Id == WmKeyDown && message.WParam == VkDelete && _highlighted is Row highlighted)
                {
                    if ((message.LParam & KeyPreviouslyDown) == 0)
                    {
                        RequestDelete(highlighted);
                    }
                    return 1;
                }
            }

            return CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        private void RequestDelete(Row row)
        {
            if (row.Deleted)
            {
                return;
            }

            try
            {
                if (row.Delete())
                {
                    // Keep the row in place so the remaining delete buttons do not move under the pointer.
                    row.Deleted = true;
                    EnableMenuItem(_menu, row.Position, ByPosition | Grayed);
                    RedrawRow(row);
                    return;
                }
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                AppErrorHandler.Report(ex);
            }

            // Release menu focus for the save error dialog.
            EndMenu();
        }

        private void RedrawRow(Row row)
        {
            if (!GetMenuItemRect(nint.Zero, _menu, row.Position, out NativeRect bounds))
            {
                return;
            }

            nint popup = WindowFromPoint(new Point((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
            if (popup != nint.Zero && GetWindowThreadProcessId(popup, nint.Zero) == GetCurrentThreadId() &&
                SendMessage(popup, MnGetHmenu, nint.Zero, nint.Zero) == _menu)
            {
                RedrawWindow(popup, nint.Zero, nint.Zero, RedrawInvalidate | RedrawUpdateNow);
            }
        }

        private int Scale(int pixels) => (int)Math.Round(pixels * _dpi / 96.0);

        public void Dispose()
        {
            if (_hook != nint.Zero)
            {
                UnhookWindowsHookEx(_hook);
            }
            foreach (Row row in _rows.Values)
            {
                Marshal.FreeHGlobal(row.AccessibleInfo);
            }
            _font.Dispose();
        }

        private sealed record Row(string Title, Func<bool> Delete, nint AccessibleInfo, uint Position)
        {
            public bool Deleted { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccessibleMenuItem
        {
            public uint Signature;
            public uint TextLength;
            public nint Text;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public nint Window;
            public uint Id;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public Point Point;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MeasureItem
        {
            public uint ControlType, ControlId, ItemId, Width, Height;
            public nuint ItemData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DrawItem
        {
            public uint ControlType, ControlId, ItemId, Action, State;
            public nint Menu, DeviceContext;
            public NativeRect Bounds;
            public nuint ItemData;
        }

        private delegate nint HookProc(int code, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "AppendMenuW")]
        private static extern bool AppendMenu(nint menu, uint flags, nuint commandId, nint itemData);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static extern nint SetWindowsHookEx(int hookType, HookProc callback, nint module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool EndMenu();

        [DllImport("user32.dll")]
        private static extern int MenuItemFromPoint(nint window, nint menu, Point point);

        [DllImport("user32.dll")]
        private static extern uint GetMenuItemID(nint menu, int position);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(nint menu);

        [DllImport("user32.dll")]
        private static extern uint EnableMenuItem(nint menu, uint position, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMenuItemRect(nint window, nint menu, uint position, out NativeRect bounds);

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, nint processId);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(nint window, nint rectangle, nint region, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint window);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
