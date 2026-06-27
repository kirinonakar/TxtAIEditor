using System.Threading.Tasks;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Interfaces
{
    public class SnippetItem
    {
        public string Title { get; set; } = string.Empty;
        public string Keyword { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    public interface ISnippetService
    {
        Task LoadSnippetsAsync();
        Task SaveSnippetsAsync();
        List<SnippetItem> GetSnippets();
        Task AddSnippetAsync(SnippetItem item);
        Task UpdateSnippetAsync(string originalTitle, SnippetItem item);
        Task DeleteSnippetAsync(string title);
        Task ExportSnippetsAsync(string filePath);
        Task ImportSnippetsAsync(string filePath);
        Task ResetSnippetsAsync();
        List<string> GetAutocompleteWords();
        Task SaveAutocompleteWordsAsync(List<string> words);
    }
}
