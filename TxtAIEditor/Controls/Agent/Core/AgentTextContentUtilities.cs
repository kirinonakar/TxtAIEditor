using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentTextContentUtilities
    {
        public static string NormalizeNewlines(string? content)
        {
            return (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public static string TrimBoundaryNewlines(string content)
        {
            return content.Trim('\n');
        }

        public static int CountNormalizedLines(string content)
        {
            return string.IsNullOrEmpty(content) ? 0 : content.Split('\n').Length;
        }

        public static string NormalizeWhitespaceForSnippetComparison(string content)
        {
            return Regex.Replace(content, @"\s+", " ").Trim();
        }

        public static string DetectLineEnding(string text)
        {
            return text.Contains("\r\n") ? "\r\n" : "\n";
        }

        public static string RestoreLineEndings(string text, string lineEnding)
        {
            return lineEnding == "\r\n" ? text.Replace("\n", "\r\n") : text;
        }
    }
}
