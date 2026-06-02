using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.ViewModels;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    public sealed class SnippetsController
    {
        private readonly ISnippetService _snippetService;
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Func<Task>? _snippetsChangedAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;

        public SnippetsController(
            ISnippetService snippetService,
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            Func<XamlRoot> xamlRootProvider,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Func<Task>? snippetsChangedAsync,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action<object> initializePickerWindow,
            Action? beforeDialog = null,
            Action? afterDialog = null)
        {
            _snippetService = snippetService;
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _xamlRootProvider = xamlRootProvider;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _snippetsChangedAsync = snippetsChangedAsync;
            _showError = showError;
            _getString = getString;
            _initializePickerWindow = initializePickerWindow;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            _leftSidebar.SnippetsList.ItemsSource = _viewModel.Snippets;
            WireEvents();
        }

        public async Task LoadAsync()
        {
            await _snippetService.LoadSnippetsAsync();
            Refresh();
        }

        public void Refresh()
        {
            _viewModel.Snippets.Clear();
            foreach (var item in _snippetService.GetSnippets())
            {
                _viewModel.Snippets.Add(item);
            }
        }

        private void WireEvents()
        {
            _leftSidebar.SnippetItemDoubleTapped += OnSnippetItemDoubleTapped;
            _leftSidebar.DeleteSnippetClick += OnDeleteSnippetClick;
            _leftSidebar.EditSnippetClick += OnEditSnippetClick;
            _leftSidebar.AddSnippetClick += OnAddSnippetClick;
            _leftSidebar.ExportSnippetsClick += OnExportSnippetsClick;
            _leftSidebar.ImportSnippetsClick += OnImportSnippetsClick;
            _leftSidebar.ResetSnippetsClick += OnResetSnippetsClick;
        }

        private async void OnSnippetItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var item = VisualTreeDataContext.FindFromOriginalSource<SnippetItem>(e.OriginalSource)
                 ?? _leftSidebar.SnippetsList.SelectedItem as SnippetItem;
            if (item == null)
            {
                return;
            }

            if (!await _insertIntoActiveEditorAsync(item.Content))
            {
                _showError(
                    _getString("SnippetInsertErrorTitle", "스니펫 삽입 오류"),
                    _getString("SnippetInsertErrorMessage", "현재 텍스트 에디터 창이 활성화되어 있지 않습니다."));
            }
        }

        private async void OnDeleteSnippetClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string title })
            {
                await _snippetService.DeleteSnippetAsync(title);
                Refresh();
                await NotifySnippetsChangedAsync();
            }
        }

        private async void OnAddSnippetClick(object sender, RoutedEventArgs e)
        {
            var item = await ShowSnippetDialogAsync(
                _getString("SnippetAddTitle", "새 코드/수식 스니펫 추가"),
                _getString("SnippetAddButton", "추가"),
                null);
            if (item == null)
            {
                return;
            }

            await _snippetService.AddSnippetAsync(item);
            Refresh();
            await NotifySnippetsChangedAsync();
        }

        private async void OnEditSnippetClick(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.DataContext as SnippetItem;
            if (item == null)
            {
                return;
            }

            var editedItem = await ShowSnippetDialogAsync(
                _getString("SnippetEditTitle", "스니펫 수정"),
                _getString("SnippetEditSave", "저장"),
                item);
            if (editedItem == null)
            {
                return;
            }

            await _snippetService.UpdateSnippetAsync(item.Title, editedItem);
            Refresh();
            await NotifySnippetsChangedAsync();
        }

        private async Task<SnippetItem?> ShowSnippetDialogAsync(string title, string primaryButtonText, SnippetItem? existing)
        {
            var titleBox = new TextBox
            {
                PlaceholderText = _getString("SnippetPlaceholderName", "스니펫 이름 (예: C# Loop)"),
                Text = existing?.Title ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var keywordBox = new TextBox
            {
                PlaceholderText = _getString("SnippetPlaceholderKeyword", "키워드 (예: loop)"),
                Text = existing?.Keyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var descBox = new TextBox
            {
                PlaceholderText = _getString("SnippetPlaceholderDesc", "간단한 설명"),
                Text = existing?.Description ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var contentBox = new TextBox
            {
                PlaceholderText = _getString("SnippetPlaceholderContent", "코드 본문 입력..."),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 200,
                MaxHeight = 400,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new FontFamily("Consolas"),
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };
            contentBox.Text = (existing?.Content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);

            var stack = new StackPanel { Spacing = 10, Width = 450 };
            stack.Children.Add(new TextBlock { Text = _getString("SnippetLabelName", "스니펫 이름") });
            stack.Children.Add(titleBox);
            stack.Children.Add(new TextBlock { Text = _getString("SnippetLabelKeyword", "자동완성 키워드") });
            stack.Children.Add(keywordBox);
            stack.Children.Add(new TextBlock { Text = _getString("SnippetLabelDesc", "설명") });
            stack.Children.Add(descBox);
            stack.Children.Add(new TextBlock { Text = _getString("SnippetLabelContent", "템플릿 내용") });
            stack.Children.Add(contentBox);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = title,
                Content = stack,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = _getString("SnippetCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _leftSidebar.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary || string.IsNullOrEmpty(titleBox.Text))
            {
                return null;
            }

            return new SnippetItem
            {
                Title = titleBox.Text,
                Keyword = keywordBox.Text,
                Description = descBox.Text,
                Content = contentBox.Text.Replace("\r\n", "\n").Replace("\r", "\n")
            };
        }

        private async void OnExportSnippetsClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            _initializePickerWindow(picker);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new System.Collections.Generic.List<string> { ".json" });
            picker.SuggestedFileName = "snippets.json";

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            await _snippetService.ExportSnippetsAsync(file.Path);
        }

        private async void OnImportSnippetsClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            _initializePickerWindow(picker);
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                await _snippetService.ImportSnippetsAsync(file.Path);
                Refresh();
                await NotifySnippetsChangedAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("SnippetImportErrorTitle", "스니펫 가져오기 오류"),
                    string.Format(_getString("SnippetImportErrorMessage", "파일을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private async void OnResetSnippetsClick(object sender, RoutedEventArgs e)
        {
            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("SnippetResetTitle", "스니펫 초기화"),
                Content = _getString("SnippetResetConfirm", "기본 스니펫으로 초기화하시겠습니까?\n추가한 스니펫이 모두 삭제됩니다."),
                PrimaryButtonText = _getString("SnippetResetConfirmButton", "초기화"),
                CloseButtonText = _getString("SnippetCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _leftSidebar.ActualTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary) return;

            await _snippetService.ResetSnippetsAsync();
            Refresh();
            await NotifySnippetsChangedAsync();
        }

        private Task NotifySnippetsChangedAsync()
        {
            return _snippetsChangedAsync?.Invoke() ?? Task.CompletedTask;
        }
    }
}
