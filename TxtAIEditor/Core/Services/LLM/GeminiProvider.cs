using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services.LLM
{
    public class GeminiProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;

        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly bool _verbose;
        private readonly string _thinkingLevel;
        private readonly string _providerName;
        private string _accumulatedText = string.Empty;
        private int _lastProcessedIndex = 0;
        private bool _inThought = false;
        private string _thoughtBuffer = string.Empty;
        private string _nativeThoughtBuffer = string.Empty;

        private static readonly Regex[] ThoughtRegexes = new[]
        {
            new Regex(@"<thought>(.*?)(?:</thought>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase),
            new Regex(@"<think>(.*?)(?:</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase),
            new Regex(@"<\|channel\>thought(.*?)(?:<channel\|>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)
        };

        private bool HasThinking => !string.IsNullOrEmpty(_thinkingLevel) &&
                                    !_thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                    !_thinkingLevel.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                                    !_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase);

        public GeminiProvider(ILocalizationService localizationService, bool verbose = false, string thinkingLevel = "", string providerName = "Gemini")
        {
            _localizationService = localizationService;
            _verbose = verbose;
            _thinkingLevel = thinkingLevel ?? "";
            _providerName = providerName ?? "Gemini";
        }

        private async Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken)
        {
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        private static bool IsGemma4(string model)
        {
            return !string.IsNullOrEmpty(model) && model.Contains("gemma-4", StringComparison.OrdinalIgnoreCase);
        }

        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            int cjkCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 0x3000 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF))
                {
                    cjkCount++;
                }
            }

            int nonCjkLength = text.Length - cjkCount;
            double estimatedTokens = (cjkCount * 1.2) + (nonCjkLength / 4.0);
            return Math.Max(1, (int)Math.Round(estimatedTokens));
        }

        private string ProcessGemma4Thoughts(string text)
        {
            if (_verbose) return text;

            string result = text;
            foreach (var regex in ThoughtRegexes)
            {
                result = regex.Replace(result, match =>
                {
                    string thoughtContent = match.Groups[1].Value;
                    int tokenCount = EstimateTokenCount(thoughtContent);
                    return string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount);
                });
            }
            return result;
        }

        private async Task ProcessGemma4StreamChunkAsync(string chunkText, Func<string, Task> onChunk)
        {
            if (_verbose)
            {
                await onChunk(chunkText);
                return;
            }

            _accumulatedText += chunkText;

            string[] startTags = { "<thought>", "<think>", "<|channel>thought" };
            string[] endTags = { "</thought>", "</think>", "<channel|>" };

            while (_lastProcessedIndex < _accumulatedText.Length)
            {
                if (!_inThought)
                {
                    int earliestStartPos = -1;
                    int matchingTagLength = 0;

                    foreach (var tag in startTags)
                    {
                        int pos = _accumulatedText.IndexOf(tag, _lastProcessedIndex, StringComparison.OrdinalIgnoreCase);
                        if (pos >= 0)
                        {
                            if (earliestStartPos == -1 || pos < earliestStartPos)
                            {
                                earliestStartPos = pos;
                                matchingTagLength = tag.Length;
                            }
                        }
                    }

                    if (earliestStartPos >= 0)
                    {
                        if (earliestStartPos > _lastProcessedIndex)
                        {
                            string normalText = _accumulatedText.Substring(_lastProcessedIndex, earliestStartPos - _lastProcessedIndex);
                            await onChunk(normalText);
                        }

                        _inThought = true;
                        _thoughtBuffer = string.Empty;
                        _lastProcessedIndex = earliestStartPos + matchingTagLength;
                    }
                    else
                    {
                        int safeLength = _accumulatedText.Length - _lastProcessedIndex;
                        int holdBack = 0;
                        for (int i = 1; i <= Math.Min(17, safeLength); i++)
                        {
                            string endSubstring = _accumulatedText.Substring(_accumulatedText.Length - i);
                            if (startTags.Any(tag => tag.StartsWith(endSubstring, StringComparison.OrdinalIgnoreCase)))
                            {
                                holdBack = i;
                            }
                        }

                        int processEnd = _accumulatedText.Length - holdBack;
                        if (processEnd > _lastProcessedIndex)
                        {
                            string normalText = _accumulatedText.Substring(_lastProcessedIndex, processEnd - _lastProcessedIndex);
                            await onChunk(normalText);
                            _lastProcessedIndex = processEnd;
                        }
                        break;
                    }
                }
                else
                {
                    int earliestEndPos = -1;
                    int matchingTagLength = 0;

                    foreach (var tag in endTags)
                    {
                        int pos = _accumulatedText.IndexOf(tag, _lastProcessedIndex, StringComparison.OrdinalIgnoreCase);
                        if (pos >= 0)
                        {
                            if (earliestEndPos == -1 || pos < earliestEndPos)
                            {
                                earliestEndPos = pos;
                                matchingTagLength = tag.Length;
                            }
                        }
                    }

                    if (earliestEndPos >= 0)
                    {
                        _thoughtBuffer += _accumulatedText.Substring(_lastProcessedIndex, earliestEndPos - _lastProcessedIndex);
                        
                        int tokenCount = EstimateTokenCount(_thoughtBuffer);
                        await onChunk(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));

                        _inThought = false;
                        _thoughtBuffer = string.Empty;
                        _lastProcessedIndex = earliestEndPos + matchingTagLength;
                    }
                    else
                    {
                        int safeLength = _accumulatedText.Length - _lastProcessedIndex;
                        int holdBack = 0;
                        for (int i = 1; i <= Math.Min(11, safeLength); i++)
                        {
                            string endSubstring = _accumulatedText.Substring(_accumulatedText.Length - i);
                            if (endTags.Any(tag => tag.StartsWith(endSubstring, StringComparison.OrdinalIgnoreCase)))
                            {
                                holdBack = i;
                            }
                        }

                        int processEnd = _accumulatedText.Length - holdBack;
                        if (processEnd > _lastProcessedIndex)
                        {
                            _thoughtBuffer += _accumulatedText.Substring(_lastProcessedIndex, processEnd - _lastProcessedIndex);
                            _lastProcessedIndex = processEnd;
                        }
                        break;
                    }
                }
            }
        }

        private async Task ProcessGemma4StreamPartsAsync(JsonElement parts, Func<string, Task> onChunk)
        {
            foreach (var part in parts.EnumerateArray())
            {
                bool isThoughtPart = part.TryGetProperty("thought", out var thoughtProp) && thoughtProp.ValueKind == JsonValueKind.True;
                string partText = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrEmpty(partText)) continue;

                if (isThoughtPart)
                {
                    if (_verbose)
                    {
                        await onChunk(partText);
                    }
                    else
                    {
                        _nativeThoughtBuffer += partText;
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(_nativeThoughtBuffer))
                    {
                        int tokenCount = EstimateTokenCount(_nativeThoughtBuffer);
                        await onChunk(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));
                        _nativeThoughtBuffer = string.Empty;
                    }

                    await ProcessGemma4StreamChunkAsync(partText, onChunk);
                }
            }
        }

        private async Task FlushGemma4StreamAsync(Func<string, Task> onChunk)
        {
            if (_verbose) return;

            if (!string.IsNullOrEmpty(_nativeThoughtBuffer))
            {
                int tokenCount = EstimateTokenCount(_nativeThoughtBuffer);
                await onChunk(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));
                _nativeThoughtBuffer = string.Empty;
            }

            if (_inThought)
            {
                if (_lastProcessedIndex < _accumulatedText.Length)
                {
                    _thoughtBuffer += _accumulatedText.Substring(_lastProcessedIndex);
                }
                int tokenCount = EstimateTokenCount(_thoughtBuffer);
                await onChunk(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));
            }
            else
            {
                if (_lastProcessedIndex < _accumulatedText.Length)
                {
                    string normalText = _accumulatedText.Substring(_lastProcessedIndex);
                    await onChunk(normalText);
                }
            }
        }

        private string BuildGeminiUrl(string baseUrl, string model, bool stream)
        {
            string url;
            if (baseUrl.Contains("/v1beta/models") || baseUrl.Contains("/v1/models"))
            {
                url = $"{baseUrl}/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            else if (baseUrl.Contains("/v1beta") || baseUrl.Contains("/v1"))
            {
                url = $"{baseUrl}/models/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            else
            {
                url = $"{baseUrl}/v1beta/models/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            if (stream) url += "?alt=sse";
            return url;
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint.TrimEnd('/');
            string requestUrl = BuildGeminiUrl(baseUrl, model, false);

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            if (outputLimit <= 0) outputLimit = 65536;
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments);

            var generationConfigDict = new Dictionary<string, object>
            {
                ["temperature"] = 0.5,
                ["maxOutputTokens"] = outputLimit
            };

            if (HasThinking)
            {
                string levelStr = _thinkingLevel.ToUpperInvariant();
                if (model.Contains("gemma", StringComparison.OrdinalIgnoreCase) && levelStr == "LOW")
                {
                    levelStr = "MINIMAL";
                }
                else if (levelStr == "XHIGH")
                {
                    levelStr = "HIGH";
                }

                generationConfigDict["thinkingConfig"] = new Dictionary<string, object>
                {
                    ["thinkingLevel"] = levelStr
                };
            }

            var payload = new Dictionary<string, object>
            {
                ["contents"] = new[]
                {
                    new
                    {
                        role = "user",
                        parts = BuildUserParts(userContent, attachments)
                    }
                },
                ["systemInstruction"] = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                ["generationConfig"] = generationConfigDict
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Add("x-goog-api-key", apiKey);
                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GeminiErrorApiCallFailed", "Google Gemini API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        // Extract candidates[0].content.parts[0].text
                        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                        {
                            var firstCandidate = candidates[0];
                            if (firstCandidate.TryGetProperty("content", out var candidateContent) &&
                                candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                            {
                                if (IsGemma4(model))
                                {
                                    var sb = new StringBuilder();
                                    var nativeThoughtSb = new StringBuilder();
                                    foreach (var part in parts.EnumerateArray())
                                    {
                                        bool isThoughtPart = part.TryGetProperty("thought", out var thoughtProp) && thoughtProp.ValueKind == JsonValueKind.True;
                                        string partText = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;

                                        if (isThoughtPart)
                                        {
                                            if (_verbose)
                                            {
                                                sb.Append(partText);
                                            }
                                            else
                                            {
                                                nativeThoughtSb.Append(partText);
                                            }
                                        }
                                        else
                                        {
                                            if (nativeThoughtSb.Length > 0)
                                            {
                                                int tokenCount = EstimateTokenCount(nativeThoughtSb.ToString());
                                                sb.Append(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));
                                                nativeThoughtSb.Clear();
                                            }
                                            sb.Append(partText);
                                        }
                                    }
                                    if (nativeThoughtSb.Length > 0)
                                    {
                                        int tokenCount = EstimateTokenCount(nativeThoughtSb.ToString());
                                        sb.Append(string.Format(_localizationService.GetString("GeminiThinkingFormat", "[Thinking: {0} tokens]\n\n"), tokenCount));
                                    }
                                    return ProcessGemma4Thoughts(sb.ToString());
                                }
                                else
                                {
                                    var firstPart = parts[0];
                                    if (firstPart.TryGetProperty("text", out var text))
                                    {
                                        return text.GetString() ?? string.Empty;
                                    }
                                }
                            }
                        }
                    }
                    
                    return _localizationService.GetString("GeminiErrorEmptyResponse", "Gemini AI로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint.TrimEnd('/');
            string requestUrl = BuildGeminiUrl(baseUrl, model, true);

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            if (outputLimit <= 0) outputLimit = 65536;
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments);

            var generationConfigDict = new Dictionary<string, object>
            {
                ["temperature"] = 0.5,
                ["maxOutputTokens"] = outputLimit
            };

            if (HasThinking)
            {
                string levelStr = _thinkingLevel.ToUpperInvariant();
                if (model.Contains("gemma", StringComparison.OrdinalIgnoreCase) && levelStr == "LOW")
                {
                    levelStr = "MINIMAL";
                }
                else if (levelStr == "XHIGH")
                {
                    levelStr = "HIGH";
                }

                generationConfigDict["thinkingConfig"] = new Dictionary<string, object>
                {
                    ["thinkingLevel"] = levelStr
                };
            }

            var payload = new Dictionary<string, object>
            {
                ["contents"] = new[]
                {
                    new
                    {
                        role = "user",
                        parts = BuildUserParts(userContent, attachments)
                    }
                },
                ["systemInstruction"] = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                ["generationConfig"] = generationConfigDict
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Add("x-goog-api-key", apiKey);
                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GeminiErrorStreamCallFailed", "Google Gemini API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            if (line == null) break;
                            if (string.IsNullOrEmpty(line)) continue;
                            if (!line.StartsWith("data: ")) continue;

                            string data = line.Substring(6);

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                                    {
                                        var firstCandidate = candidates[0];
                                        if (firstCandidate.TryGetProperty("content", out var candidateContent) &&
                                            candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                                        {
                                            if (IsGemma4(model))
                                            {
                                                cancellationToken.ThrowIfCancellationRequested();
                                                await ProcessGemma4StreamPartsAsync(parts, onChunk);
                                            }
                                            else
                                            {
                                                var firstPart = parts[0];
                                                if (firstPart.TryGetProperty("text", out var text))
                                                {
                                                    string? chunk = text.GetString();
                                                    if (!string.IsNullOrEmpty(chunk))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onChunk(chunk);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (JsonException)
                            {
                            }
                        }

                        if (IsGemma4(model))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await FlushGemma4StreamAsync(onChunk);
                        }
                    }
                }
            }
        }

        private static object[] BuildUserParts(string userContent, IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            var parts = new List<object>
            {
                new { text = userContent }
            };

            foreach (var image in attachments?.Where(a => a.IsImage && !string.IsNullOrWhiteSpace(a.Base64Data)) ?? Enumerable.Empty<LlmMessageAttachment>())
            {
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = image.MimeType,
                        data = image.Base64Data
                    }
                });
            }

            return parts.ToArray();
        }
    }
}
