namespace TxtAIEditor.Controls
{
    public interface IAgentUiCommands
    {
        void ClearSelectionContext();

        void ApplyRuntimeSettings();

        void RefreshLocalizedModelDisplay();
    }
}
