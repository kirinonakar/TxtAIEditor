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
                    () => enqueue(() => _ = AppErrorHandler.RunAsync(() => DeleteFromTrayAsync(note, trayOwner)))));
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
                    root => _ = AppErrorHandler.RunAsync(() => ConfirmDeleteAsync(note, root, nint.Zero)),
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

        private Task DeleteFromTrayAsync(QuickNote note, nint trayOwner)
        {
            nint owner = _openNotes.TryGetValue(note.Id, out QuickNoteWindow? window)
                ? WindowNative.GetWindowHandle(window) : trayOwner;
            return ConfirmDeleteAsync(note, root: null, owner);
        }

        private async Task ConfirmDeleteAsync(QuickNote note, FrameworkElement? root, nint owner)
        {
            if (_notes?.Contains(note) != true)
            {
                return;
            }

            string delete = _text("QuickNotesDelete", "삭제");
            var dialog = CreateDialog(delete, DeletePrompt(note), _text("QuickNotesCancel", "취소"));
            dialog.PrimaryButtonText = delete;
            if (await ShowDialogAsync(dialog, root, owner) == ContentDialogResult.Primary &&
                !Delete(note) && root == null)
            {
                await ShowTrayErrorAsync(owner, _text("QuickNotesSaveError", "메모를 저장할 수 없습니다."));
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

        private Task<ContentDialogResult> ShowTrayErrorAsync(nint owner, string message) =>
            ShowDialogAsync(CreateDialog(
                _text("QuickNotesMenu", "빠른 메모"), message, _text("Ok", "확인")), root: null, owner);

        private ContentDialog CreateDialog(string title, string message, string closeText) => new()
        {
            RequestedTheme = _theme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close
        };

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, FrameworkElement? root, nint owner)
        {
            if (_activeDialog != null)
            {
                return ContentDialogResult.None;
            }

            _activeDialog = dialog;
            try
            {
                if (root == null)
                {
                    return await QuickNoteDialogWindow.ShowAsync(dialog, owner);
                }
                if (root.XamlRoot is XamlRoot xamlRoot)
                {
                    dialog.XamlRoot = xamlRoot;
                    return await dialog.ShowAsync();
                }
                return ContentDialogResult.None;
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
