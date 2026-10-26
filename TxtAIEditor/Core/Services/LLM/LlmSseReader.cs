using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    // An orderly TCP EOF is not necessarily a completed model response.
    internal sealed class LlmSseReader : StreamReader
    {
        private bool _completed;
        private bool _truncated;

        public LlmSseReader(Stream stream) : base(stream) { }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            string? line = await base.ReadLineAsync(cancellationToken);
            if (line == null && _truncated) throw new ResponseTruncatedException();
            if (line == null && !_completed)
            {
                throw new IOException("The model stream ended before a completion event.");
            }

            if (line?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
            {
                string data = line.Substring(5).Trim();
                if (data == "[DONE]")
                {
                    if (_truncated) throw new ResponseTruncatedException();
                    _completed = true;
                }
                else
                {
                    ObserveEvent(data);
                }
            }
            return line;
        }

        private void ObserveEvent(string data)
        {
            try
            {
                using var document = JsonDocument.Parse(data);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                string? type = root.TryGetProperty("type", out var eventType) && eventType.ValueKind == JsonValueKind.String
                    ? eventType.GetString() : null;
                if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null ||
                    type is "error" or "response.failed")
                {
                    throw new LlmRequestException("The model stream reported an error: " + data);
                }
                if (type is "response.completed" or "message_stop")
                {
                    _completed = true;
                }
                if (type == "response.incomplete")
                {
                    if (root.TryGetProperty("response", out var response) &&
                        response.TryGetProperty("incomplete_details", out var details) &&
                        details.TryGetProperty("reason", out var reason) && reason.GetString() == "max_output_tokens")
                        _truncated = true;
                    else
                        throw new LlmRequestException("The model response was incomplete: " + data);
                }
                if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("stop_reason", out var stopReason) && stopReason.GetString() == "max_tokens")
                    _truncated = true;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                        {
                            if (reason.GetString() == "error") throw new LlmRequestException("The model stream failed.");
                            _truncated |= reason.GetString() == "length";
                            _completed = true;
                        }
                    }
                }
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String)
                        {
                            _truncated |= reason.GetString() == "MAX_TOKENS";
                            _completed = true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON keepalives, as the provider parsers do.
            }
        }
    }
}
