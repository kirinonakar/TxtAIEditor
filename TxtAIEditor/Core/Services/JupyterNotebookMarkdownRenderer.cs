using System;
using System.Text;

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

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();

                var taskMatch = System.Text.RegularExpressions.Regex.Match(line, @"^\s*[-*+]\s+\[([ xX])\]\s+(.*)");

                if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\s{0,3}>\s?"))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTaskList) { sb.AppendLine("</ul>"); inTaskList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (!inQuote) { sb.AppendLine("<blockquote>"); inQuote = true; }
                    string quoteContent = System.Text.RegularExpressions.Regex.Replace(raw, @"^\s{0,3}>\s?", "");
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
            }

            if (inList) sb.AppendLine("</ul>");
            if (inTaskList) sb.AppendLine("</ul>");
            if (inOl) sb.AppendLine("</ol>");
            if (inQuote) sb.AppendLine("</blockquote>");
            return sb.ToString();
        }

        private static string InlineMd(string text)
        {
            text = NotebookHtmlEncoder.Encode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&lt;span style=&quot;color:\s*([^&;]+);?&quot;&gt;([\s\S]*?)&lt;/span&gt;", @"<span style=""color:$1;"">$2</span>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&lt;span style=&quot;background-color:\s*([^&;]+);?&quot;&gt;([\s\S]*?)&lt;/span&gt;", @"<span style=""background-color:$1;"">$2</span>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&lt;font color=&quot;([^&;]+)&quot;&gt;([\s\S]*?)&lt;/font&gt;", @"<font color=""$1"">$2</font>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&lt;mark&gt;([\s\S]*?)&lt;/mark&gt;", @"<mark>$1</mark>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"&lt;u&gt;([\s\S]*?)&lt;/u&gt;", @"<u>$1</u>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\(([^)]+)\)", @"<img src=""$2"" alt=""$1"" style=""max-width:100%;height:auto;display:inline-block;vertical-align:middle;margin:4px 0;"" />");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", @"<a href=""$2"" target=""_blank"">$1</a>");
            text = ReplaceSimple(text, "**", "<strong>", "</strong>");
            text = ReplaceSimple(text, "*", "<em>", "</em>");
            text = ReplaceSimple(text, "`", "<code>", "</code>");
            return text;
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
