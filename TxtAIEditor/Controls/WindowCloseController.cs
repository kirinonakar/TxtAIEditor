using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class WindowCloseController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly UnsavedChangesDialogService _unsavedChangesDialogService;
        private readonly Func<Task> _saveUiLayoutSettingsAsync;
        private readonly Func<XamlRoot?> _xamlRootProvider;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly Func<string, string, string> _getString;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Func<OpenedTab, Task<bool>> _saveTabAsync;
        private readonly Action _closeWindow;
        private bool _isClosingConfirmed;

        public WindowCloseController(
            MainWindowViewModel viewModel,
            UnsavedChangesDialogService unsavedChangesDialogService,
            Func<Task> saveUiLayoutSettingsAsync,
            Func<XamlRoot?> xamlRootProvider,
            Func<ElementTheme> getCurrentElementTheme,
            Func<string, string, string> getString,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Func<OpenedTab, Task<bool>> saveTabAsync,
            Action closeWindow)
        {
            _viewModel = viewModel;
            _unsavedChangesDialogService = unsavedChangesDialogService;
            _saveUiLayoutSettingsAsync = saveUiLayoutSettingsAsync;
            _xamlRootProvider = xamlRootProvider;
            _getCurrentElementTheme = getCurrentElementTheme;
            _getString = getString;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _saveTabAsync = saveTabAsync;
            _closeWindow = closeWindow;
        }

        public async Task HandleClosingAsync(AppWindowClosingEventArgs args)
        {
            if (_isClosingConfirmed)
            {
                return;
            }

            if (_unsavedChangesDialogService.IsShowing)
            {
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            var dirtyTabs = _viewModel.Tabs.Where(t => t.IsDirty).ToList();

            await _saveUiLayoutSettingsAsync();
            if (dirtyTabs.Count == 0)
            {
                ConfirmClose();
                return;
            }

            bool terminalWasVisible = _isTerminalVisible();
            try
            {
                if (terminalWasVisible)
                {
                    _suspendTerminal();
                }

                XamlRoot? xamlRoot = _xamlRootProvider();
                if (xamlRoot == null)
                {
                    return;
                }

                var result = await _unsavedChangesDialogService.ShowAsync(
                    _getString("UnsavedChangesAppCloseTitle", "저장되지 않은 변경 사항"),
                    string.Format(_getString("UnsavedChangesAppCloseMessage", "저장되지 않은 탭이 {0}개 있습니다. 종료하기 전에 저장하시겠습니까?"), dirtyTabs.Count),
                    _getString("UnsavedChangesAppCloseDiscard", "저장하지 않고 종료"),
                    _getString("UnsavedChangesAppCloseSave", "저장하고 종료"),
                    _getString("UnsavedChangesCancel", "취소"),
                    xamlRoot,
                    _getCurrentElementTheme());

                if (result == UnsavedChangesDialogResult.Discard)
                {
                    ConfirmClose();
                }
                else if (result == UnsavedChangesDialogResult.Save)
                {
                    foreach (var tab in dirtyTabs)
                    {
                        bool saved = await _saveTabAsync(tab);
                        if (!saved)
                        {
                            return;
                        }
                    }

                    ConfirmClose();
                }
            }
            finally
            {
                if (terminalWasVisible)
                {
                    _resumeTerminal();
                }
            }
        }

        private void ConfirmClose()
        {
            _isClosingConfirmed = true;
            _closeWindow();
        }
    }
}
