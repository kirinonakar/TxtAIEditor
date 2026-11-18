using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class GeminiFunctionCatalog
    {
        private static readonly Regex FunctionName = new(@"\A[A-Za-z_][A-Za-z0-9_.:-]{0,63}\z", RegexOptions.CultureInvariant);
        private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
        private readonly Dictionary<string, LlmTool> _tools = new(StringComparer.Ordinal);

        public GeminiFunctionCatalog(IReadOnlyList<LlmTool> tools)
        {
            foreach (LlmTool tool in tools.Where(tool => FunctionName.IsMatch(tool.Name)))
            {
                _tools.TryAdd(tool.Name, tool);
            }
            foreach (LlmTool tool in tools.Where(tool => !FunctionName.IsMatch(tool.Name)).DistinctBy(tool => tool.Name))
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool.Name)))[..16].ToLowerInvariant();
                string prefix = Regex.Replace(tool.Name, "[^A-Za-z0-9_]", "_");
                string name = $"fn_{prefix[..Math.Min(prefix.Length, 40)]}_{hash}";
                for (int suffix = 1; !_tools.TryAdd(name, tool); suffix++)
                {
                    name = $"fn_{hash}_{suffix}";
                }
            }
        }

        public object GetTools() => new[]
        {
            new
            {
                functionDeclarations = _tools.Select(entry => new
                {
                    name = entry.Key,
                    description = entry.Value.Description,
                    parametersJsonSchema = entry.Value.Parameters
                }).ToArray()
            }
        };

        public GeminiFunctionTurn? ReadTurn(IReadOnlyList<JsonElement> parts)
        {
            var calls = new List<GeminiFunctionCall>();
            foreach (JsonElement part in parts)
            {
                if (!part.TryGetProperty("functionCall", out JsonElement call))
                {
                    continue;
                }

                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("name", out JsonElement name) ||
                    name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                {
                    return null;
                }
                string wireName = name.GetString()!;
                string? id = call.TryGetProperty("id", out JsonElement callId) && callId.ValueKind == JsonValueKind.String
                    ? callId.GetString() : null;
                JsonElement args = call.TryGetProperty("args", out JsonElement arguments) ? arguments : EmptyArguments;
                string? toolName = args.ValueKind == JsonValueKind.Object && _tools.TryGetValue(wireName, out LlmTool? tool)
                    ? tool.Name : null;
                calls.Add(new GeminiFunctionCall(wireName, toolName, id, args));
            }

            return calls.Count > 0 ? new GeminiFunctionTurn(parts, calls) : null;
        }

        public static string FormatCalls(GeminiFunctionTurn turn) => string.Concat(turn.Calls
            .Where(call => call.ToolName != null)
            .Select(call => LlmToolCallTextFormatter.FormatFunctionToolCall(call.ToolName!, call.Arguments.GetRawText())));
    }
}
