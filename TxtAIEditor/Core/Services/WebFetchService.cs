using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace TxtAIEditor.Core.Services
{
    public class WebFetchService
    {
        private const int MaxRedirects = 5;
        private const long MaxContentSize = 2 * 1024 * 1024; // 2MB
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        public async Task<string> FetchUrlAsMarkdownAsync(string url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("URL cannot be empty.", nameof(url));
            }

            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            string html = await FetchHtmlAsync(url, cancellationToken);
            string markdown = ExtractMainContentAsMarkdown(html);
            return markdown;
        }

        private async Task<string> FetchHtmlAsync(string startUrl, CancellationToken cancellationToken)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            using var client = new HttpClient(handler);
            string currentUrl = startUrl;
            int redirectCount = 0;

            while (redirectCount <= MaxRedirects)
            {
                var uri = new Uri(currentUrl);

                // SSRF Protection: Check internal network block
                if (await IsInternalOrPrivateUriAsync(uri, cancellationToken))
                {
                    throw new InvalidOperationException($"Access to internal or private URL is blocked: {currentUrl}");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 TxtAIEditor/1.0");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // Redirect detection
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        throw new InvalidOperationException("Redirect status code received, but Location header is missing.");
                    }

                    if (!location.IsAbsoluteUri)
                    {
                        location = new Uri(uri, location);
                    }

                    currentUrl = location.ToString();
                    redirectCount++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP fetch failed with status code: {response.StatusCode} for URL: {currentUrl}");
                }

                // Check Content-Length size limit
                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxContentSize)
                {
                    throw new InvalidOperationException($"Response size ({contentLength.Value} bytes) exceeds the maximum limit of {MaxContentSize} bytes.");
                }

                // Read body stream with dynamic size limit checking
                using var responseStream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                int bytesRead;
                long totalBytesRead = 0;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                {
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxContentSize)
                    {
                        throw new InvalidOperationException($"Response size exceeded the maximum limit of {MaxContentSize} bytes during download.");
                    }
                    ms.Write(buffer, 0, bytesRead);
                }

                // Detect charset encoding from Content-Type header or fallback to UTF-8
                string charset = response.Content.Headers.ContentType?.CharSet ?? "utf-8";
                Encoding encoding;
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch
                {
                    encoding = Encoding.UTF8;
                }

                return encoding.GetString(ms.ToArray());
            }

            throw new InvalidOperationException($"Too many redirects (max redirects: {MaxRedirects} exceeded)");
        }

        private async Task<bool> IsInternalOrPrivateUriAsync(Uri uri, CancellationToken cancellationToken)
        {
            string host = uri.Host;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "loopback", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                foreach (var ip in addresses)
                {
                    if (IsPrivateOrLoopbackIp(ip))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // DNS resolution failed. To be safe, block access if it resolves to nothing or fails.
                return true;
            }

            return false;
        }

        private bool IsPrivateOrLoopbackIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] bytes = ip.GetAddressBytes();
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12 (172.16.x.x - 172.31.x.x)
                if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // 169.254.0.0/16 (Link-local)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                // 0.0.0.0 (unspecified)
                if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) return true;
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                byte[] bytes = ip.GetAddressBytes();

                // fc00::/7 (Unique Local Address)
                if ((bytes[0] & 0xFE) == 0xFC) return true;

                // fe80::/10 (Link-Local Address)
                if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;

                // unspecified ::
                if (bytes.All(b => b == 0)) return true;
            }

            return false;
        }

        private string ExtractMainContentAsMarkdown(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 1. Clean HTML: remove scripts, styles, etc.
            CleanHtmlTree(doc.DocumentNode);

            // 2. Identify the main content node using simplified Readability scoring
            HtmlNode mainNode = FindBestContentNode(doc.DocumentNode);

            // 3. Convert target HTML node to clean Markdown
            string markdown = ConvertToMarkdown(mainNode);
            return markdown;
        }

        private void CleanHtmlTree(HtmlNode root)
        {
            var unwantedTags = new[] { "script", "style", "noscript", "iframe", "svg", "canvas", "object", "embed", "applet", "nav", "footer", "header", "aside", "form", "menu" };
            
            // Collect nodes to remove
            var toRemove = root.Descendants()
                .Where(n => unwantedTags.Contains(n.Name.ToLowerInvariant()))
                .ToList();

            foreach (var node in toRemove)
            {
                node.Remove();
            }
        }

        private HtmlNode FindBestContentNode(HtmlNode root)
        {
            // First check if there is an explicit article or main tag
            var articles = root.Descendants()
                .Where(n => n.Name.ToLowerInvariant() is "article" or "main")
                .ToList();

            if (articles.Count > 0)
            {
                // Select the one with the maximum inner text content length
                var bestArticle = articles.OrderByDescending(a => GetCleanTextLength(a)).FirstOrDefault();
                if (bestArticle != null && GetCleanTextLength(bestArticle) > 100)
                {
                    return bestArticle;
                }
            }

            // Fallback: evaluate all divs and sections in the tree using Readability scores
            var candidateNodes = root.Descendants()
                .Where(n => n.Name.ToLowerInvariant() is "div" or "section" or "body")
                .ToList();

            HtmlNode bestCandidate = root.SelectSingleNode("//body") ?? root;
            double bestScore = -1.0;

            foreach (var node in candidateNodes)
            {
                double score = CalculateNodeContentScore(node);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = node;
                }
            }

            return bestCandidate;
        }

        private int GetCleanTextLength(HtmlNode node)
        {
            return node.InnerText?.Trim().Length ?? 0;
        }

        private double CalculateNodeContentScore(HtmlNode node)
        {
            // Basic factors: text length and number of paragraphs
            int textLength = GetCleanTextLength(node);
            if (textLength < 40)
            {
                return -100.0;
            }

            int paragraphCount = node.Descendants("p").Count();
            double score = textLength / 150.0 + paragraphCount * 5.0;

            // Link density penalty: If link text density is very high, it is probably a menu, footer, or advertisement list
            string nodeText = node.InnerText ?? "";
            int nodeTextLen = nodeText.Length;
            if (nodeTextLen > 0)
            {
                int linkTextLen = node.Descendants("a")
                    .Sum(a => a.InnerText?.Length ?? 0);

                double linkDensity = (double)linkTextLen / nodeTextLen;
                if (linkDensity > 0.4)
                {
                    score -= score * (linkDensity * 1.5);
                }
            }

            // Attribute keywords adjustments (Class, ID)
            string classVal = (node.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
            string idVal = (node.GetAttributeValue("id", "") ?? "").ToLowerInvariant();

            var positiveKeywords = new[] { "content", "article", "body", "entry", "main", "post", "story", "post-text" };
            var negativeKeywords = new[] { "comment", "foot", "header", "menu", "sidebar", "sponsor", "ad", "widget", "combx", "disqus", "rss", "shoutbox" };

            foreach (var word in positiveKeywords)
            {
                if (classVal.Contains(word) || idVal.Contains(word))
                {
                    score += 35.0;
                }
            }

            foreach (var word in negativeKeywords)
            {
                if (classVal.Contains(word) || idVal.Contains(word))
                {
                    score -= 35.0;
                }
            }

            return score;
        }

        private string ConvertToMarkdown(HtmlNode root)
        {
            var sb = new StringBuilder();
            ProcessNode(root, sb, new MarkdownContext());
            return sb.ToString().Trim();
        }

        private class MarkdownContext
        {
            public bool InList { get; set; }
            public bool IsOrderedList { get; set; }
            public int ListIndex { get; set; }
            public int QuoteLevel { get; set; }
        }

        private void ProcessNode(HtmlNode node, StringBuilder sb, MarkdownContext context)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                string text = HtmlEntity.DeEntitize(node.InnerText ?? "");
                // Replace tabs and reduce multi-spaces but preserve single space
                text = Regex.Replace(text, @"[ \t]+", " ");
                
                // If it is inside block, strip starting/ending line breaks
                if (!string.IsNullOrEmpty(text))
                {
                    sb.Append(text);
                }
                return;
            }

            if (node.NodeType != HtmlNodeType.Element)
            {
                return;
            }

            string name = node.Name.ToLowerInvariant();
            bool isBlock = IsBlockElement(name);

            // Block elements get preceding newlines if sb has contents and doesn't end with newline
            if (isBlock && sb.Length > 0 && sb[sb.Length - 1] != '\n')
            {
                sb.Append("\n");
            }

            switch (name)
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    {
                        int level = name[1] - '0';
                        sb.Append(new string('#', level)).Append(' ');
                        ProcessChildren(node, sb, context);
                        sb.Append("\n\n");
                    }
                    break;

                case "p":
                    ProcessChildren(node, sb, context);
                    sb.Append("\n\n");
                    break;

                case "br":
                    sb.Append("\n");
                    break;

                case "strong":
                case "b":
                    sb.Append("**");
                    ProcessChildren(node, sb, context);
                    sb.Append("**");
                    break;

                case "em":
                case "i":
                    sb.Append("*");
                    ProcessChildren(node, sb, context);
                    sb.Append("*");
                    break;

                case "code":
                    if (node.ParentNode?.Name.ToLowerInvariant() == "pre")
                    {
                        ProcessChildren(node, sb, context);
                    }
                    else
                    {
                        sb.Append("`");
                        ProcessChildren(node, sb, context);
                        sb.Append("`");
                    }
                    break;

                case "pre":
                    sb.Append("```\n");
                    ProcessChildren(node, sb, context);
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                    {
                        sb.Append("\n");
                    }
                    sb.Append("```\n\n");
                    break;

                case "blockquote":
                    {
                        context.QuoteLevel++;
                        var quoteSb = new StringBuilder();
                        ProcessChildren(node, quoteSb, context);
                        context.QuoteLevel--;

                        string quoteText = quoteSb.ToString().Trim();
                        var lines = quoteText.Split('\n');
                        foreach (var line in lines)
                        {
                            sb.Append("> ").Append(line).Append("\n");
                        }
                        sb.Append("\n");
                    }
                    break;

                case "a":
                    {
                        string href = node.GetAttributeValue("href", "").Trim();
                        var linkTextSb = new StringBuilder();
                        ProcessChildren(node, linkTextSb, context);
                        string linkText = linkTextSb.ToString().Trim();

                        if (!string.IsNullOrEmpty(href) && !href.StartsWith("#") && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append($"[{linkText}]({href})");
                        }
                        else
                        {
                            sb.Append(linkText);
                        }
                    }
                    break;

                case "img":
                    {
                        string src = node.GetAttributeValue("src", "").Trim();
                        string alt = node.GetAttributeValue("alt", "").Trim();
                        if (string.IsNullOrEmpty(alt))
                        {
                            alt = "image";
                        }
                        if (!string.IsNullOrEmpty(src))
                        {
                            sb.Append($"![{alt}]({src})");
                        }
                    }
                    break;

                case "ul":
                    {
                        var childContext = new MarkdownContext { InList = true, IsOrderedList = false, QuoteLevel = context.QuoteLevel };
                        ProcessChildren(node, sb, childContext);
                        sb.Append("\n");
                    }
                    break;

                case "ol":
                    {
                        var childContext = new MarkdownContext { InList = true, IsOrderedList = true, ListIndex = 1, QuoteLevel = context.QuoteLevel };
                        ProcessChildren(node, sb, childContext);
                        sb.Append("\n");
                    }
                    break;

                case "li":
                    if (context.InList)
                    {
                        if (context.IsOrderedList)
                        {
                            sb.Append($"{context.ListIndex++}. ");
                        }
                        else
                        {
                            sb.Append("- ");
                        }
                        ProcessChildren(node, sb, context);
                        sb.Append("\n");
                    }
                    else
                    {
                        sb.Append("- ");
                        ProcessChildren(node, sb, context);
                        sb.Append("\n");
                    }
                    break;

                case "table":
                    ProcessTable(node, sb, context);
                    break;

                default:
                    ProcessChildren(node, sb, context);
                    break;
            }
        }

        private void ProcessChildren(HtmlNode node, StringBuilder sb, MarkdownContext context)
        {
            foreach (var child in node.ChildNodes)
            {
                ProcessNode(child, sb, context);
            }
        }

        private bool IsBlockElement(string name)
        {
            return name is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" 
                or "ul" or "ol" or "li" or "blockquote" or "pre" or "section" or "article" 
                or "table" or "tr" or "td" or "th";
        }

        private void ProcessTable(HtmlNode tableNode, StringBuilder sb, MarkdownContext context)
        {
            var rows = tableNode.SelectNodes(".//tr");
            if (rows == null || rows.Count == 0) return;

            sb.Append("\n");
            bool renderedHeader = false;
            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./th | ./td");
                if (cells == null || cells.Count == 0) continue;

                sb.Append("|");
                foreach (var cell in cells)
                {
                    var cellSb = new StringBuilder();
                    ProcessChildren(cell, cellSb, context);
                    string cellText = cellSb.ToString().Trim().Replace("\n", " ").Replace("|", "\\|");
                    sb.Append(" ").Append(cellText).Append(" |");
                }
                sb.Append("\n");

                if (!renderedHeader)
                {
                    sb.Append("|");
                    for (int i = 0; i < cells.Count; i++)
                    {
                        sb.Append(" --- |");
                    }
                    sb.Append("\n");
                    renderedHeader = true;
                }
            }
            sb.Append("\n");
        }
    }
}
