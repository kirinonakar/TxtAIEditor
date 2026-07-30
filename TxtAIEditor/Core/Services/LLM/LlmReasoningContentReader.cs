using System.Text.Json;

namespace TxtAIEditor.Core.Services.LLM
{
    internal static class LlmReasoningContentReader
    {
        public static bool TryGetText(JsonElement delta, out string text)
        {
            foreach (string propertyName in new[] { "reasoning_content", "reasoning", "thinking" })
            {
                if (delta.TryGetProperty(propertyName, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return true;
                    }
                }
            }

            text = string.Empty;
            return false;
        }
    }
}
