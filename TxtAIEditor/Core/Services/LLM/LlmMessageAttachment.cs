namespace TxtAIEditor.Core.Services.LLM
{
    public sealed class LlmMessageAttachment
    {
        public string DisplayName { get; set; } = string.Empty;
        public string MimeType { get; set; } = "application/octet-stream";
        public string Base64Data { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int EstimatedTokens { get; set; }

        public bool IsImage => MimeType.StartsWith("image/", System.StringComparison.OrdinalIgnoreCase);
    }
}
