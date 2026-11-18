using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TxtAIEditor.Core.Services.LLM
{
    public sealed class GeminiToolConversation
    {
        private sealed record Turn(int Start, int End, IReadOnlyList<JsonElement> ModelParts, object[] ResultParts);

        private readonly List<Turn> _turns = new();

        internal GeminiFunctionTurn? PendingTurn { get; set; }

        internal void AddTurn(int start, string transcriptText, GeminiFunctionTurn turn,
            IReadOnlyDictionary<int, (string Text, bool Success)> results)
        {
            object[] responseParts = turn.Calls.Select((call, index) =>
            {
                var response = new Dictionary<string, object>
                {
                    ["name"] = call.WireName,
                    ["response"] = results.TryGetValue(index, out var result)
                        ? new Dictionary<string, string> { [result.Success ? "output" : "error"] = result.Text }
                        : new { error = call.ToolName == null
                            ? "The function was not declared or its arguments were invalid."
                            : "The tool call was skipped before execution." }
                };
                if (!string.IsNullOrEmpty(call.Id))
                {
                    response["id"] = call.Id;
                }
                return (object)new { functionResponse = response };
            }).ToArray();

            _turns.Add(new Turn(start, start + transcriptText.TrimEnd().Length, turn.ModelParts, responseParts));
        }

        internal void ReplaceTranscriptPrefix(int oldLength, int newLength)
        {
            _turns.RemoveAll(turn => turn.Start < oldLength);
            for (int index = 0; index < _turns.Count; index++)
            {
                Turn turn = _turns[index];
                int offset = newLength - oldLength;
                _turns[index] = turn with { Start = turn.Start + offset, End = turn.End + offset };
            }
        }

        internal object[] BuildContents(string userContent, int transcriptStart, int transcriptLength,
            Func<string, object[]> buildUserParts)
        {
            if (_turns.Count == 0 || _turns[^1].End > transcriptLength)
            {
                return [new { role = "user", parts = buildUserParts(userContent) }];
            }

            int transcriptEnd = transcriptStart + transcriptLength;
            // Keep the current workspace/attachment context in the user prompt. Keep the
            // model parts and their consecutive function responses in their original order.
            string initialPrompt = userContent[..(transcriptStart + _turns[0].Start)] + userContent[transcriptEnd..];
            var contents = new List<object> { new { role = "user", parts = buildUserParts(initialPrompt) } };
            int cursor = _turns[0].Start;
            foreach (Turn turn in _turns)
            {
                AddUserText(cursor, turn.Start);
                contents.Add(new { role = "model", parts = turn.ModelParts });
                contents.Add(new { role = "user", parts = turn.ResultParts });
                cursor = turn.End;
            }
            AddUserText(cursor, transcriptLength);
            return contents.ToArray();

            void AddUserText(int start, int end)
            {
                string text = userContent[(transcriptStart + start)..(transcriptStart + end)].Trim();
                if (text.Length > 0)
                {
                    contents.Add(new { role = "user", parts = new[] { new { text } } });
                }
            }
        }
    }

    internal sealed record GeminiToolRequest(GeminiToolConversation Conversation, int TranscriptStart, int TranscriptLength);

    internal sealed record GeminiFunctionCall(string WireName, string? ToolName, string? Id, JsonElement Arguments);

    internal sealed record GeminiFunctionTurn(IReadOnlyList<JsonElement> ModelParts, IReadOnlyList<GeminiFunctionCall> Calls);
}
