using System;

namespace TxtAIEditor.Core.Services.LLM
{
    /// <summary>
    /// Maps user-selected thinking levels to the effective effort value sent to
    /// each model family. Mimo 2.5 models only support up to "high", so
    /// "xhigh"/"max" are downgraded to "high" before transmission.
    /// </summary>
    internal static class LlmThinkingLevelMapper
    {
        public static bool IsMimoModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.Contains("mimo", StringComparison.OrdinalIgnoreCase);
        }

        public static string MapEffort(string model, string thinkingLevel)
        {
            string effort = (thinkingLevel ?? string.Empty).ToLowerInvariant();
            if (IsMimoModel(model) && (effort == "xhigh" || effort == "max"))
            {
                return "high";
            }
            return effort;
        }
    }
}
