using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Services;
using WinRT.Interop;

namespace TxtAIEditor.Controls
{
    internal sealed class QuickNotesController
    {
        private readonly Func<string, string, string> _text;
        private readonly QuickNoteStore _store = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".TxtAIEditor", "quick-notes.json"));
        private readonly Dictionary<Guid, QuickNoteWindow> _openNotes = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private List<QuickNote>? _notes;
        private bool _dirty;
        private bool _saveFailed;
        private bool _confirmingDelete;

        public QuickNotesController(Func<string, string, string> text)
        {
            _text = text;
            _saveTimer.Tick += (_, _) => Flush();
        }

        public void Add(nint trayOwner)
        {
            if (!EnsureLoaded())
            {
                ShowTrayError(trayOwner, _text("QuickNotesLoadError", "메모를 불러올 수 없습니다."));
                return;
            }

            var note = new QuickNote();
            _notes!.Insert(0, note);
            if (!TrySave())
            {
                _notes.Remove(note);
                ShowTrayError(trayOwner, _text("QuickNotesSaveError", "메모를 저장할 수 없습니다."));
                return;
            }

            OpenNote(note);
        }

        public TrayQuickNoteItems GetTrayItems(nint trayOwner, Action<Action> enqueue)
        {
            if (!EnsureLoaded())
            {
                return new TrayQuickNoteItems(Array.Empty<TrayQuickNoteItem>(),
                    _text("QuickNotesLoadError", "메모를 불러올 수 없습니다."));
            }

            var items = new List<TrayQuickNoteItem>(_notes!.Count);
            foreach (QuickNote note in _notes)
            {
                string title = DisplayTitle(note);
                items.Add(new TrayQuickNoteItem(title,
                    string.Format(_text("QuickNotesDeletePrompt", "‘{0}’ 메모 삭제"), title),
                    () => enqueue(() => OpenNote(note)),
                    () => enqueue(() => DeleteFromTray(note, trayOwner))));
            }

            string? status = _saveFailed ? _text("QuickNotesSaveError", "메모를 저장할 수 없습니다.") :
                items.Count == 0 ? _text("QuickNotesEmpty", "저장된 메모가 없습니다.") : null;
            return new TrayQuickNoteItems(items, status);
        }

        public void Flush()
        {
            _saveTimer.Stop();
            if (_dirty)
            {
                TrySave();
            }
        }

        private bool EnsureLoaded()
        {
            if (_notes != null)
            {
                return true;
            }

            try
            {
                _notes = _store.Load();
                return true;
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                AppErrorHandler.Report(ex);
                return false;
            }
        }

        private string DisplayTitle(QuickNote note) =>
            string.IsNullOrWhiteSpace(note.Title) ? _text("QuickNotesUntitled", "제목 없음") : note.Title.Trim();

        private string DeletePrompt(QuickNote note) =>
            string.Format(_text("QuickNotesDeleteConfirm", "‘{0}’ 메모를 삭제할까요?"), DisplayTitle(note));

        private void OpenNote(QuickNote note)
        {
            if (_notes?.Contains(note) != true)
            {
                return;
            }

            if (!_openNotes.TryGetValue(note.Id, out QuickNoteWindow? window))
            {
                window = new QuickNoteWindow(
                    note, _text, () => DisplayTitle(note), OnNoteChanged,
                    root => DeleteAsync(note, root), _openNotes.Count);
                _openNotes.Add(note.Id, window);
                window.Closed += (_, _) =>
                {
                    _openNotes.Remove(note.Id);
                    Flush();
                };
            }

            window.Activate();
            if (_saveFailed)
            {
                ShowStorageError(true);
            }
        }

        private void OnNoteChanged()
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private async void DeleteAsync(QuickNote note, FrameworkElement root)
        {
            if (_confirmingDelete || root.XamlRoot == null || _notes == null)
            {
                return;
            }

            _confirmingDelete = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    RequestedTheme = root.ActualTheme,
                    Title = _text("QuickNotesDelete", "삭제"),
                    Content = DeletePrompt(note),
                    PrimaryButtonText = _text("QuickNotesDelete", "삭제"),
                    CloseButtonText = _text("QuickNotesCancel", "취소"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    Delete(note);
                }
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                AppErrorHandler.Report(ex);
            }
            finally
            {
                _confirmingDelete = false;
            }
        }

        private void DeleteFromTray(QuickNote note, nint trayOwner)
        {
            if (_confirmingDelete || _notes?.Contains(note) != true)
            {
                return;
            }

            _confirmingDelete = true;
            try
            {
                nint owner = _openNotes.TryGetValue(note.Id, out QuickNoteWindow? window)
                    ? WindowNative.GetWindowHandle(window) : trayOwner;
                const uint yesNo = 0x04;
                const uint questionIcon = 0x20;
                const uint defaultNo = 0x100;
                const uint topMost = 0x40000;
                const int yes = 6;
                if (MessageBox(owner, DeletePrompt(note), _text("QuickNotesDelete", "삭제"),
                        yesNo | questionIcon | defaultNo | topMost) == yes && !Delete(note))
                {
                    ShowTrayError(owner, _text("QuickNotesSaveError", "메모를 저장할 수 없습니다."));
                }
            }
            finally
            {
                _confirmingDelete = false;
            }
        }

        private bool Delete(QuickNote note)
        {
            int index = _notes!.IndexOf(note);
            if (index < 0)
            {
                return true;
            }

            _notes.RemoveAt(index);
            if (!TrySave())
            {
                _notes.Insert(index, note);
                return false;
            }

            if (_openNotes.TryGetValue(note.Id, out QuickNoteWindow? window))
            {
                window.Close();
            }
            return true;
        }

        private void ShowTrayError(nint owner, string message) =>
            MessageBox(owner, message, _text("QuickNotesMenu", "빠른 메모"), 0x40010);

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(nint owner, string message, string title, uint flags);

        private bool TrySave()
        {
            try
            {
                _store.Save(_notes!);
                _dirty = false;
                ShowStorageError(false);
                return true;
            }
            catch (Exception ex) when (AppErrorHandler.IsRecoverable(ex))
            {
                AppErrorHandler.Report(ex);
                ShowStorageError(true);
                return false;
            }
        }

        private void ShowStorageError(bool failed)
        {
            _saveFailed = failed;
            string message = failed ? _text("QuickNotesSaveError", "메모를 저장할 수 없습니다.") : string.Empty;
            foreach (QuickNoteWindow window in _openNotes.Values)
            {
                window.SetStorageError(message);
            }
        }
    }
}
