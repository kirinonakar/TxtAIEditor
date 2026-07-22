using System;
using System.IO;

namespace TxtAIEditor.Editor
{
    internal static class CustomEditorLanguageResolver
    {
        public static string Resolve(string filePathOrLanguage)
        {
            if (!filePathOrLanguage.Contains(Path.DirectorySeparatorChar) &&
                !filePathOrLanguage.Contains(Path.AltDirectorySeparatorChar) &&
                !filePathOrLanguage.Contains('.') &&
                !string.IsNullOrWhiteSpace(filePathOrLanguage))
            {
                return filePathOrLanguage;
            }

            string name = Path.GetFileName(filePathOrLanguage);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
            if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return "makefile";

            return Path.GetExtension(filePathOrLanguage).ToLowerInvariant() switch
            {
                ".cs" => "csharp",
                ".fs" => "fsharp",
                ".vb" => "vb",
                ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
                ".ts" or ".tsx" or ".mts" or ".cts" => "typescript",
                ".html" or ".htm" => "html",
                ".css" => "css",
                ".scss" => "scss",
                ".less" => "less",
                ".json" or ".jsonc" => "json",
                ".md" or ".markdown" => "markdown",
                ".py" => "python",
                ".cpp" or ".cxx" or ".cc" or ".c" or ".h" or ".hpp" => "cpp",
                ".xml" or ".xaml" or ".resw" or ".appxmanifest" or ".csproj" or ".manifest" => "xml",
                ".sql" => "sql",
                ".sh" or ".bash" or ".zsh" => "shell",
                ".ps1" or ".psm1" or ".psd1" => "powershell",
                ".tex" => "latex",
                ".diff" => "diff",
                ".rs" => "rust",
                ".go" => "go",
                ".java" => "java",
                ".kt" or ".kts" => "kotlin",
                ".swift" => "swift",
                ".php" => "php",
                ".rb" => "ruby",
                ".dart" => "dart",
                ".lua" => "lua",
                ".r" or ".rprofile" => "r",
                ".dockerfile" => "dockerfile",
                ".toml" => "toml",
                ".ini" => "ini",
                ".yml" or ".yaml" => "yaml",
                ".reg" => "reg",
                _ => "plaintext"
            };
        }
    }
}
