using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookMarkdownRenderer
    {
        internal static string Render(string source)
        {
            string html = SimpleMarkdownToHtml(source);
            return html;
        }

        private static string SimpleMarkdownToHtml(string md)
        {
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            bool inList = false;
            bool inTaskList = false;
            bool inOl = false;
            bool inQuote = false;
            int htmlBlockDepth = 0;

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                string trimmed = line.Trim();

                var taskMatch = Regex.Match(line, @"^\s*[-*+]\s+\[([ xX])\]\s+(.*)");

                int openers = CountHtmlBlockOpeners(trimmed);
                int closers = CountHtmlBlockClosers(trimmed);

                bool isHtmlLine = htmlBlockDepth > 0 || openers > 0 || closers > 0 || IsBlockHtmlStart(trimmed) || IsBlockHtmlEnd(trimmed);

                if (isHtmlLine)
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine(InlineMd(line));
                }
                else if (Regex.IsMatch(raw, @"^\s{0,3}>\s?"))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (!inQuote) { sb.AppendLine("<blockquote>"); inQuote = true; }
                    string quoteContent = Regex.Replace(raw, @"^\s{0,3}>\s?", "");
                    sb.AppendLine($"<p>{InlineMd(quoteContent)}</p>");
                }
                else if (line.StartsWith("# "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine($"<h1>{InlineMd(line.Substring(2))}</h1>");
                }
                else if (line.StartsWith("## "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine($"<h2>{InlineMd(line.Substring(3))}</h2>");
                }
                else if (line.StartsWith("### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine($"<h3>{InlineMd(line.Substring(4))}</h3>");
                }
                else if (line.StartsWith("#### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine($"<h4>{InlineMd(line.Substring(5))}</h4>");
                }
                else if (taskMatch.Success)
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    if (!inTaskList) { sb.AppendLine("<ul class=\"task-list\">"); inTaskList = true; }
                    bool isChecked = taskMatch.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                    string checkedAttr = isChecked ? " checked=\"checked\"" : "";
                    sb.AppendLine($"<li class=\"task-list-item\"><input type=\"checkbox\" disabled{checkedAttr} /> {InlineMd(taskMatch.Groups[2].Value)}</li>");
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                    sb.AppendLine($"<li>{InlineMd(line.Substring(2))}</li>");
                }
                else if (line.Length > 0 && char.IsDigit(line[0]) && line.Contains(". "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    if (!inOl) { sb.AppendLine("<ol>"); inOl = true; }
                    int dot = line.IndexOf(". ");
                    sb.AppendLine($"<li>{InlineMd(line.Substring(dot + 2))}</li>");
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                }
                else
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (inQuote) { sb.AppendLine("</blockquote>"); inQuote = false; }
                    sb.AppendLine($"<p>{InlineMd(line)}</p>");
                }

                htmlBlockDepth = Math.Max(0, htmlBlockDepth + openers - closers);
            }

            if (inList) sb.AppendLine("</ul>");
            if (inTaskList) sb.AppendLine("</ul>");
            if (inOl) sb.AppendLine("</ol>");
            if (inQuote) sb.AppendLine("</blockquote>");
            return sb.ToString();
        }

        private static int CountHtmlBlockOpeners(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            var matches = Regex.Matches(line, @"<(?:table|thead|tbody|tr|th|td|div|p|ul|ol|li|section|header|footer|figure|figcaption|center)\b[^>]*>", RegexOptions.IgnoreCase);
            int count = 0;
            foreach (Match m in matches)
            {
                if (!m.Value.EndsWith("/>", StringComparison.Ordinal)) count++;
            }
            return count;
        }

        private static int CountHtmlBlockClosers(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            return Regex.Matches(line, @"</(?:table|thead|tbody|tr|th|td|div|p|ul|ol|li|section|header|footer|figure|figcaption|center)\b", RegexOptions.IgnoreCase).Count;
        }

        private static bool IsBlockHtmlStart(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("<")) return false;
            return Regex.IsMatch(trimmed, @"^</?(?:table|thead|tbody|tr|th|td|div|p|ul|ol|li|h1|h2|h3|h4|h5|h6|blockquote|section|header|footer|figure|figcaption|center)\b", RegexOptions.IgnoreCase);
        }

        private static bool IsBlockHtmlEnd(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("</")) return false;
            return Regex.IsMatch(trimmed, @"^</(?:table|thead|tbody|tr|th|td|div|p|ul|ol|li|h1|h2|h3|h4|h5|h6|blockquote|section|header|footer|figure|figcaption|center)\b", RegexOptions.IgnoreCase);
        }

        private static string InlineMd(string text)
        {
            text = NotebookHtmlEncoder.Encode(text);
            text = UnescapeSafeHtmlTags(text);
            text = Regex.Replace(text, @"!\[([^\]]*)\]\(([^)]+)\)", @"<img src=""$2"" alt=""$1"" style=""max-width:100%;height:auto;display:inline-block;vertical-align:middle;margin:4px 0;"" />");
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", @"<a href=""$2"" target=""_blank"">$1</a>");
            text = ReplaceSimple(text, "**", "<strong>", "</strong>");
            text = ReplaceSimple(text, "*", "<em>", "</em>");
            text = ReplaceSimple(text, "`", "<code>", "</code>");
            return text;
        }

        private static string UnescapeSafeHtmlTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string pattern = @"&lt;(\/?[a-zA-Z][a-zA-Z0-9:-]*)([\s\S]*?)&gt;";

            return Regex.Replace(text, pattern, match =>
            {
                string rawTagName = match.Groups[1].Value;
                string tagName = rawTagName.TrimStart('/').ToLowerInvariant();

                if (!IsAllowedHtmlTag(tagName))
                {
                    return match.Value;
                }

                string rawAttrs = match.Groups[2].Value;
                string decodedAttrs = System.Net.WebUtility.HtmlDecode(rawAttrs);
                string cleanAttrs = SanitizeHtmlAttributes(decodedAttrs);

                return $"<{rawTagName}{cleanAttrs}>";
            }, RegexOptions.IgnoreCase);
        }

        private static bool IsAllowedHtmlTag(string tag)
        {
            return tag switch
            {
                "table" or "thead" or "tbody" or "tr" or "th" or "td" or
                "a" or "img" or "div" or "p" or "span" or "br" or "font" or
                "mark" or "u" or "b" or "i" or "strong" or "em" or "code" or
                "sub" or "sup" or "hr" or "ul" or "ol" or "li" or "h1" or
                "h2" or "h3" or "h4" or "h5" or "h6" or "center" or "figure" or "figcaption" => true,
                _ => false
            };
        }

        private static string SanitizeHtmlAttributes(string attrs)
        {
            if (string.IsNullOrWhiteSpace(attrs)) return string.Empty;
            string sanitized = Regex.Replace(attrs, @"\bon[a-z]+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, @"(?:href|src)\s*=\s*(?:""\s*javascript:[^""]*""|'\s*javascript:[^']*')", "", RegexOptions.IgnoreCase);
            return sanitized;
        }

        private static string ReplaceSimple(string text, string delim, string open, string close)
        {
            var sb = new StringBuilder();
            bool openTag = true;
            int i = 0;
            while (i < text.Length)
            {
                int idx = text.IndexOf(delim, i, StringComparison.Ordinal);
                if (idx < 0)
                {
                    sb.Append(text.AsSpan(i));
                    break;
                }
                sb.Append(text.AsSpan(i, idx - i));
                sb.Append(openTag ? open : close);
                openTag = !openTag;
                i = idx + delim.Length;
            }
            return sb.ToString();
        }
    }
}

