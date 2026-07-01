namespace TxtAIEditor.Core.Models
{
    public sealed class GitHistoryItem
    {
        public string CommitHash { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;

        public override string ToString()
        {
            return DisplayText;
        }
    }
}
