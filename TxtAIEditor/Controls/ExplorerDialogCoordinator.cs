using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerDialogCoordinator
    {
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<ElementTheme> _themeProvider;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;

        public ExplorerDialogCoordinator(
            Func<XamlRoot> xamlRootProvider,
            Func<ElementTheme> themeProvider,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal)
        {
            _xamlRootProvider = xamlRootProvider;
            _themeProvider = themeProvider;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
        }

        public XamlRoot XamlRoot => _xamlRootProvider();
        public ElementTheme Theme => _themeProvider();

        public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
        {
            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                if (terminalWasVisible)
                {
                    _resumeTerminal();
                }
            }
        }
    }
}
