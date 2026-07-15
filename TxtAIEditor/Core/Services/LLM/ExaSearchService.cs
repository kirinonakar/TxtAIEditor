using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class ExaSearchService
    {
        private readonly ISettingsService _settingsService;
        private readonly LlmCredentialStore _credentialStore;
        private readonly McpToolClient _mcpToolClient;

        public ExaSearchService(
            ISettingsService settingsService,
            LlmCredentialStore credentialStore,
            McpToolClient mcpToolClient)
        {
            _settingsService = settingsService;
            _credentialStore = credentialStore;
            _mcpToolClient = mcpToolClient;
        }

        private static readonly HttpClient _exaHttpClient = new HttpClient();
        private const string DefaultExaMcpEndpoint = "https://mcp.exa.ai/mcp";
        private const int NoKeyFetchMaxCharacters = 6000;

        private static bool IsExaMcpEndpoint(string endpoint)
        {
            return endpoint.Contains("/mcp", StringComparison.OrdinalIgnoreCase) ||
                   endpoint.Contains("/sse", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> SearchExaAsync(string query, int numResults, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "Exa search failed: query is empty.";
            }

            string apiKey = await _credentialStore.GetApiKeyAsync("Exa");
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("EXA_API_KEY") ?? string.Empty;
            }

            string endpoint = _settingsService?.CurrentSettings?.ExaEndpoint ?? DefaultExaMcpEndpoint;
            bool isMcpEndpoint = IsExaMcpEndpoint(endpoint);
            bool triedDefaultMcpEndpoint = isMcpEndpoint &&
                string.Equals(endpoint.TrimEnd('/'), DefaultExaMcpEndpoint, StringComparison.OrdinalIgnoreCase);

            // If it is configured as an MCP / SSE URL (e.g. contains '/mcp' or '/sse'), use the MCP SSE transport client.
            if (isMcpEndpoint)
            {
                try
                {
                    int mcpResultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
                    var arguments = new
                    {
                        query = query,
                        numResults = mcpResultsCount,
                        highlights = true
                    };
                    return await _mcpToolClient.CallToolAsync(endpoint, apiKey, "web_search_exa", arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fallback to direct REST search if MCP fails
                    System.Diagnostics.Debug.WriteLine($"Exa MCP failed, falling back to direct search: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                if (!triedDefaultMcpEndpoint)
                {
                    try
                    {
                        int mcpResultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
                        var arguments = new
                        {
                            query = query,
                            numResults = mcpResultsCount,
                            highlights = true
                        };
                        return await _mcpToolClient.CallToolAsync(DefaultExaMcpEndpoint, apiKey, "web_search_exa", arguments, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Exa no-key MCP fallback failed, falling back to DuckDuckGo: {ex.Message}");
                    }
                }

                return await SearchWebWithoutApiKeyAsync(query, numResults, cancellationToken);
            }

            int resultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
            
            // If the endpoint is just a domain or base URL without path, default to /search
            string requestUrl = endpoint;
            if (isMcpEndpoint)
            {
                // If we fell back here because MCP failed, force standard API URL
                requestUrl = "https://api.exa.ai/search";
            }
            else if (!requestUrl.Contains("/search") && !requestUrl.Contains("/findSimilar"))
            {
                requestUrl = requestUrl.TrimEnd('/') + "/search";
                if (!requestUrl.StartsWith("http://") && !requestUrl.StartsWith("https://"))
                {
                    requestUrl = "https://api.exa.ai/search";
                }
            }

            var payload = new
            {
                query = query,
                useAutoprompt = true,
                numResults = resultsCount,
                text = new { maxCharacters = 1000 },
                highlights = true
            };

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                {
                    request.Headers.Add("x-api-key", apiKey);
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"Exa search API failed ({response.StatusCode}): {responseBody}";
                        }

                        using (var doc = JsonDocument.Parse(responseBody))
                        {
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                            {
                                return "Exa search returned no results format.";
                            }

                            var sb = new StringBuilder();
                            int index = 1;
                            foreach (var item in results.EnumerateArray())
                            {
                                string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "No Title" : "No Title";
                                string url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                                string textContent = item.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                                
                                sb.AppendLine($"[{index}] Title: {title}");
                                sb.AppendLine($"URL: {url}");
                                
                                if (item.TryGetProperty("highlights", out var highlightsProp) && highlightsProp.ValueKind == JsonValueKind.Array && highlightsProp.GetArrayLength() > 0)
                                {
                                    sb.AppendLine("Highlights:");
                                    foreach (var highlight in highlightsProp.EnumerateArray())
                                    {
                                        sb.AppendLine($"- {highlight.GetString()}");
                                    }
                                }
                                else if (!string.IsNullOrEmpty(textContent))
                                {
                                    string preview = textContent.Length > 300 ? textContent.Substring(0, 300) + "..." : textContent;
                                    sb.AppendLine($"Snippet: {preview}");
                                }
                                sb.AppendLine();
                                index++;
                            }

                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Exa search exception occurred: {ex.Message}";
            }
        }

        private async Task<string> SearchWebWithoutApiKeyAsync(string query, int numResults, CancellationToken cancellationToken)
        {
            int resultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
            string requestUrl = "https://duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                {
                    AddNoKeyWebHeaders(request);

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"No-key web search failed ({response.StatusCode}): {LimitText(StripHtmlToText(responseBody), 1000)}";
                        }

                        var matches = Regex.Matches(
                            responseBody,
                            "<a[^>]*class=[\"'][^\"']*result__a[^\"']*[\"'][^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<title>.*?)</a>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);

                        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var sb = new StringBuilder();
                        int index = 1;

                        foreach (Match match in matches)
                        {
                            if (index > resultsCount)
                            {
                                break;
                            }

                            string title = StripHtmlToText(match.Groups["title"].Value);
                            string url = DecodeDuckDuckGoHref(match.Groups["href"].Value);
                            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                            {
                                continue;
                            }

                            sb.AppendLine($"[{index}] Title: {title}");
                            sb.AppendLine($"URL: {url}");
                            sb.AppendLine("Snippet: No-key fallback result from DuckDuckGo HTML search.");
                            sb.AppendLine();
                            index++;
                        }

                        if (sb.Length == 0)
                        {
                            string pageText = LimitText(StripHtmlToText(responseBody), 1000);
                            return string.IsNullOrWhiteSpace(pageText)
                                ? "No-key web search returned no results."
                                : $"No-key web search returned no parsed results. Raw page text:\n{pageText}";
                        }

                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                return $"No-key web search exception occurred: {ex.Message}";
            }
        }

        public async Task<string> FetchExaAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            if (urls == null || urls.Length == 0)
            {
                return "Exa fetch failed: urls list is empty.";
            }

            // 1. Try custom WebFetchService first
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting custom WebFetchService...");
                var fetchService = new WebFetchService();
                var sb = new StringBuilder();
                int index = 1;
                bool allSuccessful = true;

                foreach (string rawUrl in urls)
                {
                    string url = (rawUrl ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    try
                    {
                        string markdown = await fetchService.FetchUrlAsMarkdownAsync(url, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(markdown))
                        {
                            sb.AppendLine($"[{index}] URL: {url}");
                            sb.AppendLine("Content:");
                            sb.AppendLine(markdown);
                            sb.AppendLine();
                        }
                        else
                        {
                            // If returned markdown is empty, treat as failure to fallback to Exa
                            allSuccessful = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Custom WebFetchService failed for {url}: {ex.Message}");
                        allSuccessful = false;
                        break;
                    }
                    index++;
                }

                if (allSuccessful && sb.Length > 0)
                {
                    System.Diagnostics.Debug.WriteLine("Custom WebFetchService succeeded for all URLs.");
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during custom WebFetchService execution: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("Falling back to Exa content fetch...");

            // 2. Fallback to Exa content fetch
            string apiKey = await _credentialStore.GetApiKeyAsync("Exa");
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("EXA_API_KEY") ?? string.Empty;
            }

            string endpoint = _settingsService?.CurrentSettings?.ExaEndpoint ?? "https://mcp.exa.ai/mcp";
            bool isMcpEndpoint = IsExaMcpEndpoint(endpoint);

            // If it is configured as an MCP / SSE URL (e.g. contains '/mcp' or '/sse'), use the MCP SSE transport client.
            if (isMcpEndpoint)
            {
                try
                {
                    var arguments = new
                    {
                        urls = urls
                    };
                    return await _mcpToolClient.CallToolAsync(endpoint, apiKey, "web_fetch_exa", arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fallback to direct REST contents fetch if MCP fails
                    System.Diagnostics.Debug.WriteLine($"Exa fetch MCP failed, falling back to direct API: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                return await FetchUrlsWithoutApiKeyAsync(urls, cancellationToken);
            }

            // Fallback direct REST API call: POST https://api.exa.ai/contents
            string requestUrl = endpoint;
            if (isMcpEndpoint)
            {
                requestUrl = "https://api.exa.ai/contents";
            }
            else if (!requestUrl.Contains("/contents"))
            {
                requestUrl = requestUrl.TrimEnd('/') + "/contents";
                if (!requestUrl.StartsWith("http://") && !requestUrl.StartsWith("https://"))
                {
                    requestUrl = "https://api.exa.ai/contents";
                }
            }

            var payload = new
            {
                urls = urls,
                text = true
            };

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                {
                    request.Headers.Add("x-api-key", apiKey);
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"Exa fetch API failed ({response.StatusCode}): {responseBody}";
                        }

                        using (var doc = JsonDocument.Parse(responseBody))
                        {
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                            {
                                return "Exa fetch returned no contents results format.";
                            }

                            var sb = new StringBuilder();
                            int index = 1;
                            foreach (var item in results.EnumerateArray())
                            {
                                string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "No Title" : "No Title";
                                string url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                                string textContent = item.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                                
                                sb.AppendLine($"[{index}] Title: {title}");
                                sb.AppendLine($"URL: {url}");
                                if (!string.IsNullOrEmpty(textContent))
                                {
                                    sb.AppendLine("Content:");
                                    sb.AppendLine(textContent);
                                }
                                sb.AppendLine();
                                index++;
                            }

                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Exa fetch exception occurred: {ex.Message}";
            }
        }

        private async Task<string> FetchUrlsWithoutApiKeyAsync(string[] urls, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            int index = 1;

            foreach (string rawUrl in urls)
            {
                string url = (rawUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                sb.AppendLine($"[{index}] URL: {url}");

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        AddNoKeyWebHeaders(request);

                        using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (!response.IsSuccessStatusCode)
                            {
                                sb.AppendLine($"Fetch failed ({response.StatusCode}): {LimitText(StripHtmlToText(responseBody), 1000)}");
                                sb.AppendLine();
                                index++;
                                continue;
                            }

                            string title = ExtractHtmlTitle(responseBody);
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                sb.AppendLine($"Title: {title}");
                            }

                            sb.AppendLine("Content:");
                            sb.AppendLine(LimitText(StripHtmlToText(responseBody), NoKeyFetchMaxCharacters));
                            sb.AppendLine();
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Fetch exception occurred: {ex.Message}");
                    sb.AppendLine();
                }

                index++;
            }

            return sb.Length == 0
                ? "No-key web fetch failed: urls list is empty."
                : sb.ToString().TrimEnd();
        }

        private static void AddNoKeyWebHeaders(HttpRequestMessage request)
        {
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36 TxtAIEditor/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        private static string DecodeDuckDuckGoHref(string href)
        {
            string decoded = WebUtility.HtmlDecode(href ?? string.Empty);
            string uddg = GetQueryStringValue(decoded, "uddg");
            if (!string.IsNullOrWhiteSpace(uddg))
            {
                return Uri.UnescapeDataString(uddg);
            }

            if (decoded.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + decoded;
            }

            if (decoded.StartsWith("/", StringComparison.Ordinal))
            {
                return "https://duckduckgo.com" + decoded;
            }

            return decoded;
        }

        private static string GetQueryStringValue(string url, string name)
        {
            int questionIndex = url.IndexOf('?');
            string query = questionIndex >= 0 ? url.Substring(questionIndex + 1) : url;
            string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (string pair in pairs)
            {
                int equalsIndex = pair.IndexOf('=');
                string key = equalsIndex >= 0 ? pair.Substring(0, equalsIndex) : pair;
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return equalsIndex >= 0 ? pair.Substring(equalsIndex + 1) : string.Empty;
            }

            return string.Empty;
        }

        private static string ExtractHtmlTitle(string html)
        {
            var match = Regex.Match(html ?? string.Empty, "<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? StripHtmlToText(match.Groups["title"].Value) : string.Empty;
        }

        private static string StripHtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            string text = Regex.Replace(html, "<(script|style|svg|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, "</?(br|p|div|li|h[1-6]|tr|section|article|header|footer|main|nav)[^>]*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ", RegexOptions.Singleline);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace('\u00A0', ' ');
            text = Regex.Replace(text, "[ \\t\\f\\v]+", " ");
            text = Regex.Replace(text, "\\s*\\n\\s*", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string LimitText(string text, int maxCharacters)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxCharacters)
            {
                return text;
            }

            return text.Substring(0, maxCharacters) + "...";
        }
    }
}
