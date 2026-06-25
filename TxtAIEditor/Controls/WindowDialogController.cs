using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    public sealed class WindowDialogController
    {
        private readonly Func<XamlRoot?> _xamlRootProvider;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Func<string, string, string> _getString;

        public WindowDialogController(
            Func<XamlRoot?> xamlRootProvider,
            Func<ElementTheme> getCurrentElementTheme,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Func<string, string, string> getString)
        {
            _xamlRootProvider = xamlRootProvider;
            _getCurrentElementTheme = getCurrentElementTheme;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _getString = getString;
        }

        public async Task<XamlRoot?> WaitForDialogXamlRootAsync()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                XamlRoot? xamlRoot = _xamlRootProvider();
                if (xamlRoot != null)
                {
                    return xamlRoot;
                }

                await Task.Delay(50);
            }

            return _xamlRootProvider();
        }

        public async void ShowErrorMessage(string title, string message)
        {
            bool terminalWasSuspended = false;
            try
            {
                if (_isTerminalVisible())
                {
                    _suspendTerminal();
                    terminalWasSuspended = true;
                }

                XamlRoot? xamlRoot = await WaitForDialogXamlRootAsync();
                if (xamlRoot == null)
                {
                    Debug.WriteLine($"{title}: {message}");
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = _getString("Ok", "확인"),
                    XamlRoot = xamlRoot,
                    RequestedTheme = _getCurrentElementTheme()
                };
                await dialog.ShowAsync();
            }
            finally
            {
                if (terminalWasSuspended)
                {
                    _resumeTerminal();
                }
            }
        }
    }
}
