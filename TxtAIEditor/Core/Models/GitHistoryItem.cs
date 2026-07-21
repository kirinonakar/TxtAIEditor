namespace TxtAIEditor.Core.Models
{
    public sealed class GitHistoryItem
    {
        public string CommitHash { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public string GraphText { get; set; } = string.Empty;
        public string MessageText { get; set; } = string.Empty;
        public string DecorationText { get; set; } = string.Empty;
        public string DateText { get; set; } = string.Empty;
        public bool IsUnpushed { get; set; }
        public bool IsRemoteOnly { get; set; }

        public bool HasStructuredDisplay =>
            !string.IsNullOrEmpty(MessageText) ||
            !string.IsNullOrEmpty(DecorationText) ||
            !string.IsNullOrEmpty(DateText);

        public override string ToString()
        {
            return DisplayText;
        }
    }
}
