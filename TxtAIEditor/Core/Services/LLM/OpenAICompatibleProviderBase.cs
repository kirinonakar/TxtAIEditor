using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    // Protocol-specific payloads and streaming remain in their providers. Only identical
    // OpenAI-compatible behavior belongs here (LM Studio, for example, uses Responses).
    public abstract class OpenAICompatibleProviderBase : ILLMProvider
    {
        protected static HttpClient _httpClient => AppHttpClients.Llm;
        public virtual async Task<string> GenerateCompletionAsync(LlmCompletionRequest request)
        {
            var response = new StringBuilder();
            await GenerateCompletionStreamAsync(request with
            {
                OnChunk = chunk => { response.Append(chunk); return Task.CompletedTask; }
            });
            return response.ToString();
        }
        public abstract Task GenerateCompletionStreamAsync(LlmCompletionRequest request);

        protected static async Task<(int context, int output)> GetCatalogTokenLimitsAsync(
            string providerName, string model, CancellationToken cancellationToken, bool bestLimits = false)
        {
            var (context, output) = bestLimits
                ? await ModelsDevCatalog.GetBestLimitsAsync(providerName, model, cancellationToken)
                : await ModelsDevCatalog.GetLimitsAsync(providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        protected static object BuildUserContent(string userContent, IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            var images = attachments?.Where(a => a.IsImage && !string.IsNullOrWhiteSpace(a.Base64Data)).ToList();
            if (images == null || images.Count == 0)
            {
                return userContent;
            }

            var parts = new List<object>
            {
                new { type = "text", text = userContent }
            };

            foreach (var image in images)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{image.MimeType};base64,{image.Base64Data}"
                    }
                });
            }

            return parts;
        }

        protected static string NormalizeBearerCredential(string credential)
        {
            string value = (credential ?? string.Empty).Trim();
            const string bearerPrefix = "Bearer ";
            if (value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return value.Substring(bearerPrefix.Length).Trim();
            }

            return value;
        }
    }
}
