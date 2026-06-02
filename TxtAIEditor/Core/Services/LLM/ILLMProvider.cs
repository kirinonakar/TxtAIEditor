using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public interface ILLMProvider
    {
        Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent);

        Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk);
    }
}
