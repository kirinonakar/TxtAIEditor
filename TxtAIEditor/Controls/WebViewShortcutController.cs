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
                case "searchAll":
                    _commands.SearchAll();
                    break;
                case "newTab":
                    _commands.NewTab();
                    break;
                case "save":
                    _commands.Save();
                    break;
                case "saveAs":
                    _commands.SaveAs();
                    break;
                case "open":
                    _commands.Open();
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
                case "toggleLeftPanel":
                    _commands.ToggleLeftPanel();
                    break;
                case "toggleRightPanel":
                    _commands.ToggleRightPanel();
                    break;
                case "terminal":
                    _commands.ToggleTerminal();
                    break;
                case "wordWrap":
                    _commands.ToggleWordWrap();
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
