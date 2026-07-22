namespace TxtAIEditor.Controls
{
    public interface IWebViewShortcutCommands
    {
        void Find();

        void SearchAll();

        void NewTab();

        void Save();

        void SaveAs();

        void Open();

        void ToggleLivePreview();

        void ToggleTopMost();

        void ToggleTheme();

        void ToggleMaximize();

        void ToggleStickyNote();

        void Print();

        void ToggleLeftPanel();

        void ToggleRightPanel();

        void ToggleTerminal();

        void ToggleWordWrap();

        void TogglePreviewWidth();

        void CloseActiveTab();
    }
}
