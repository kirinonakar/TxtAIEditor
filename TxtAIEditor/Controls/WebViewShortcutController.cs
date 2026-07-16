namespace TxtAIEditor.Controls
{
    public sealed class WebViewShortcutController
    {
        private readonly IWebViewShortcutCommands _commands;

        public WebViewShortcutController(IWebViewShortcutCommands commands)
        {
            _commands = commands;
        }

        public void Handle(string name)
        {
            switch (name)
            {
                case "find":
                    _commands.Find();
                    break;
                case "f4":
                    _commands.ToggleLivePreview();
                    break;
                case "f9":
                    _commands.ToggleTopMost();
                    break;
                case "f10":
                    _commands.ToggleTheme();
                    break;
                case "f11":
                    _commands.ToggleMaximize();
                    break;
                case "f12":
                    _commands.ToggleStickyNote();
                    break;
                case "print":
                    _commands.Print();
                    break;
                case "expandRightPanel":
                    _commands.TogglePreviewWidth();
                    break;
                case "closeTab":
                    _commands.CloseActiveTab();
                    break;
            }
        }
    }
}
