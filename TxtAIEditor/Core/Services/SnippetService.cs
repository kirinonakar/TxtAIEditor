using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services
{
    public class SnippetService : ISnippetService
    {
        private List<SnippetItem> _snippets = new List<SnippetItem>();
        private readonly string _filePath;

        public SnippetService()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string folderPath = Path.Combine(userProfile, ".TxtAIEditor");
            Directory.CreateDirectory(folderPath);
            _filePath = Path.Combine(folderPath, "snippets.json");

            // Migrate from old location (%LOCALAPPDATA%\TxtAIEditor\snippets.json) if needed
            string oldAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldFilePath = Path.Combine(oldAppData, "TxtAIEditor", "snippets.json");
            if (File.Exists(oldFilePath) && !File.Exists(_filePath))
            {
                try { File.Move(oldFilePath, _filePath); }
                catch { /* ignore migration errors */ }
            }
        }

        public async Task LoadSnippetsAsync()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    // Create beautiful default premium templates
                    _snippets = GetDefaultSnippets();
                    await SaveSnippetsAsync();
                    return;
                }

                string json = await File.ReadAllTextAsync(_filePath);
                var items = JsonSerializer.Deserialize<List<SnippetItem>>(json);
                _snippets = items ?? GetDefaultSnippets();
                NormalizeSnippets();
                await SaveSnippetsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load snippets: {ex.Message}");
                _snippets = GetDefaultSnippets();
            }
        }

        public async Task SaveSnippetsAsync()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_snippets, options);
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save snippets: {ex.Message}");
            }
        }

        public List<SnippetItem> GetSnippets()
        {
            return _snippets;
        }

        public async Task AddSnippetAsync(SnippetItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Title)) return;

            NormalizeSnippet(item);
            // Remove existing snippet with same name
            _snippets.RemoveAll(s => s.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase));
            _snippets.Add(item);
            await SaveSnippetsAsync();
        }

        public async Task UpdateSnippetAsync(string originalTitle, SnippetItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Title)) return;

            NormalizeSnippet(item);
            if (!string.IsNullOrWhiteSpace(originalTitle))
            {
                _snippets.RemoveAll(s => s.Title.Equals(originalTitle, StringComparison.OrdinalIgnoreCase));
            }

            _snippets.RemoveAll(s => s.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase));
            _snippets.Add(item);
            await SaveSnippetsAsync();
        }

        public async Task DeleteSnippetAsync(string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            _snippets.RemoveAll(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            await SaveSnippetsAsync();
        }

        public async Task ExportSnippetsAsync(string filePath)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_snippets, options);
            await File.WriteAllTextAsync(filePath, json);
        }

        public async Task ImportSnippetsAsync(string filePath)
        {
            string json = await File.ReadAllTextAsync(filePath);
            var items = JsonSerializer.Deserialize<List<SnippetItem>>(json);
            if (items == null) return;

            foreach (var item in items)
            {
                NormalizeSnippet(item);
            }

            foreach (var item in items)
            {
                _snippets.RemoveAll(s => s.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase));
                _snippets.Add(item);
            }
            await SaveSnippetsAsync();
        }

        public async Task ResetSnippetsAsync()
        {
            _snippets = GetDefaultSnippets();
            await SaveSnippetsAsync();
        }

        private void NormalizeSnippets()
        {
            foreach (var snippet in _snippets)
            {
                NormalizeSnippet(snippet);
            }
        }

        private static void NormalizeSnippet(SnippetItem item)
        {
            item.Title = item.Title?.Trim() ?? string.Empty;
            item.Keyword = string.IsNullOrWhiteSpace(item.Keyword)
                ? BuildKeywordFromTitle(item.Title)
                : item.Keyword.Trim();
            item.Description = item.Description ?? string.Empty;
            item.Content = item.Content ?? string.Empty;
        }

        private static string BuildKeywordFromTitle(string title)
        {
            string keyword = new string((title ?? string.Empty)
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                .ToArray());
            return string.IsNullOrWhiteSpace(keyword)
                ? "snippet"
                : keyword.ToLowerInvariant();
        }

        private List<SnippetItem> GetDefaultSnippets()
        {
            return new List<SnippetItem>
            {
                new SnippetItem
                {
                    Title = "Markdown Table",
                    Keyword = "!table",
                    Description = "기본 마크다운 표 템플릿",
                    Content = "| 열 이름 1 | 열 이름 2 | 열 이름 3 |\n| :--- | :---: | ---: |\n| 왼쪽 정렬 | 중앙 정렬 | 오른쪽 정렬 |\n| 내용 A | 내용 B | 내용 C |"
                },
                new SnippetItem
                {
                    Title = "LaTeX Matrix Block",
                    Keyword = "!matrix",
                    Description = "KaTeX 2x2 행렬 수식 블록",
                    Content = "$$\n\\begin{pmatrix}\na_{11} & a_{12} \\\\\na_{21} & a_{22}\n\\end{pmatrix}\n$$"
                },
                new SnippetItem
                {
                    Title = "HTML5 Document Shell",
                    Keyword = "!html5",
                    Description = "표준 HTML5 기본 뼈대 코드",
                    Content = "<!DOCTYPE html>\n<html lang=\"ko\">\n<head>\n    <meta charset=\"UTF-8\">\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n    <title>문서 제목</title>\n</head>\n<body>\n    <h1>Hello, World!</h1>\n</body>\n</html>"
                },
                new SnippetItem
                {
                    Title = "LaTeX Integral Math",
                    Keyword = "!integral",
                    Description = "적분 수식 표준 예시",
                    Content = "$$ \\int_{a}^{b} x^2 \\, dx = \\left[ \\frac{1}{3}x^3 \\right]_{a}^{b} $$"
                },
                new SnippetItem
                {
                    Title = "C# Property Snippet",
                    Keyword = "!propnotify",
                    Description = "변경 통지 프로퍼티",
                    Content = "private string _fieldName;\npublic string FieldName\n{\n    get => _fieldName;\n    set\n    {\n        if (_fieldName != value)\n        {\n            _fieldName = value;\n            // OnPropertyChanged();\n        }\n    }\n}"
                }
            };
        }
    }
}
