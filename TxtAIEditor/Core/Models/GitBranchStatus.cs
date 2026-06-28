using System;

namespace TxtAIEditor.Core.Models
{
    public static class GitBranchStatus
    {
        public const string NotDetectedTag = "GitNotDetected";

        public static bool IsNotDetected(string? branch)
        {
            return string.IsNullOrWhiteSpace(branch);
        }

        public static bool IsNotDetectedTag(object? tag)
        {
            return tag is string value &&
                   value.Equals(NotDetectedTag, StringComparison.Ordinal);
        }
    }
}
