using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        private ContentDialog? _activeDialog;
        private ElementTheme _theme;
        private bool _pastelDark;

        public QuickNotesController(Func<string, string, string> text, ElementTheme theme)
        {
            _text = text;
            _theme = theme;
            _pastelDark = Application.Current.Resources.ContainsKey("ActiveTheme");
            _saveTimer.Tick += (_, _) => Flush();
        }

        public async Task AddAsync(nint trayOwner)
        {
            if (!EnsureLoaded())
            {
                await ShowTrayErrorAsync(trayOwner, _text("QuickNotesLoadError", "메모를 불러올 수 없습니다."));
                return;
            }

            var note = new QuickNote();
            _notes!.Insert(0, note);
            if (!TrySave())
            {
                _notes.Remove(note);
                await ShowTrayErrorAsync(trayOwner, _text("QuickNotesSaveError", "메모를 저장할 수 없습니다."));
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
                    () => DeleteFromTray(note, trayOwner, enqueue)));
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

        public void UpdateTheme(ElementTheme theme)
        {
            _theme = theme;
            bool pastelDark = Application.Current.Resources.ContainsKey("ActiveTheme");
            bool refreshResources = _pastelDark != pastelDark;
            _pastelDark = pastelDark;
            foreach (QuickNoteWindow window in _openNotes.Values)
            {
                window.ApplyAppTheme(theme, refreshResources);
            }
            if (_activeDialog is ContentDialog dialog)
            {
                if (refreshResources && dialog.RequestedTheme == theme)
                {
                    dialog.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                }
                dialog.RequestedTheme = theme;
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
                    () => Delete(note),
                    _openNotes.Count, _theme);
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

        private bool DeleteFromTray(QuickNote note, nint trayOwner, Action<Action> enqueue)
        {
            if (TryRemoveNote(note))
            {
                // Close the note outside the native menu input hook.
                if (_openNotes.ContainsKey(note.Id))
                {
                    enqueue(() => CloseNote(note));
                }
                return true;
            }

            enqueue(() => _ = AppErrorHandler.RunAsync(() =>
            {
                nint owner = _openNotes.TryGetValue(note.Id, out QuickNoteWindow? window)
                    ? WindowNative.GetWindowHandle(window) : trayOwner;
                return ShowTrayErrorAsync(owner, _text("QuickNotesSaveError", "메모를 저장할 수 없습니다."));
            }));
            return false;
        }

        private bool Delete(QuickNote note)
        {
            if (!TryRemoveNote(note))
            {
                return false;
            }

            CloseNote(note);
            return true;
        }

        private bool TryRemoveNote(QuickNote note)
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
            return true;
        }

        private void CloseNote(QuickNote note)
        {
            if (_openNotes.TryGetValue(note.Id, out QuickNoteWindow? window))
            {
                window.Close();
            }
        }

        private async Task ShowTrayErrorAsync(nint owner, string message)
        {
            if (_activeDialog != null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                RequestedTheme = _theme,
                Title = _text("QuickNotesMenu", "빠른 메모"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = _text("Ok", "확인"),
                DefaultButton = ContentDialogButton.Close
            };
            _activeDialog = dialog;
            try
            {
                await QuickNoteDialogWindow.ShowAsync(dialog, owner);
            }
            finally
            {
                _activeDialog = null;
            }
        }

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
