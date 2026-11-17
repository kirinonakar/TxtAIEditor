namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunTranscript(AgentRunContext context)
    {
        public string DisplayText { get; set; } = string.Empty;
        public string ModelText { get; set; } = string.Empty;

        public void Append(string text)
        {
            DisplayText += text;
            ModelText += text;
        }

        public void AppendWithTokenCount(string text)
        {
            Append(text);
            context.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(text);
        }
    }
}
