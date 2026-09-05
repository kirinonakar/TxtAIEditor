using System;
using System.Text;

namespace TxtAIEditor.Controls
{
    internal static class AgentNonVerboseTextFilter
    {
        public static string Filter(string text, bool isComplete = false)
        {
            var visible = new StringBuilder(text.Length);
            bool inToolSection = false;
            string? codeFence = null;
            int position = 0;
            while (position < text.Length)
            {
                int newline = text.IndexOf('\n', position);
                int end = newline < 0 ? text.Length : newline;
                string line = text.Substring(position, end - position);
                string trimmed = line.Trim();
                bool partialLine = newline < 0 && !isComplete;

                // Hold a possible transcript header across streaming chunk boundaries.
                // Ordinary text and fenced examples must remain visible.
                if (codeFence == null && partialLine &&
                    (trimmed.Length == 0 || IsPossibleHeader(trimmed)))
                {
                    break;
                }

                if (codeFence == null && IsToolHeader(trimmed))
                {
                    inToolSection = true;
                }
                else if (inToolSection)
                {
                    if (trimmed.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) &&
                        trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        inToolSection = false;
                    }
                    else if (trimmed.StartsWith("[Tool execution status:", StringComparison.OrdinalIgnoreCase) &&
                        trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        inToolSection = false;
                    }
                }
                else
                {
                    visible.Append(line);
                    if (newline >= 0)
                    {
                        visible.Append('\n');
                    }

                    if (codeFence == null)
                    {
                        if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                            trimmed.StartsWith("~~~", StringComparison.Ordinal))
                        {
                            codeFence = trimmed.Substring(0, 3);
                        }
                    }
                    else if (trimmed.StartsWith(codeFence, StringComparison.Ordinal))
                    {
                        codeFence = null;
                    }
                }

                position = newline < 0 ? text.Length : newline + 1;
            }

            return visible.ToString();
        }

        private static bool IsToolHeader(string line)
        {
            return line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) &&
                (line.EndsWith(" arguments]", StringComparison.OrdinalIgnoreCase) ||
                 line.EndsWith(" result]", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPossibleHeader(string line)
        {
            return line.Length > 0 &&
                ("[tool:".StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase));
        }
    }
}
