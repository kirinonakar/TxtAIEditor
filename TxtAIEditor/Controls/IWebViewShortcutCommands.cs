namespace TxtAIEditor.Controls
{
    public interface IWebViewShortcutCommands
    {
        void Find();

        void ToggleLivePreview();

        void ToggleTopMost();

        void ToggleTheme();

        void ToggleMaximize();

        void ToggleStickyNote();

        void Print();

        void TogglePreviewWidth();

        void CloseActiveTab();
    }
}
