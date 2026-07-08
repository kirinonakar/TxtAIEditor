using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace TxtAIEditor.Controls
{
    internal static class SupportedFileTypes
    {
        public static readonly string[] TextFileExtensions =
        {
            ".txt",
            ".md",
            ".markdown",
            ".csv",
            ".html",
            ".htm",
            ".css",
            ".js",
            ".ts",
            ".cs",
            ".fs",
            ".vb",
            ".json",
            ".jsonc",
            ".tex",
            ".py",
            ".java",
            ".kt",
            ".swift",
            ".php",
            ".rb",
            ".rs",
            ".go",
            ".dart",
            ".lua",
            ".cpp",
            ".c",
            ".cc",
            ".cxx",
            ".h",
            ".hpp",
            ".xml",
            ".xaml",
            ".sql",
            ".sh",
            ".ps1",
            ".yaml",
            ".yml",
            ".toml",
            ".ini",
            ".diff",
            ".reg"
        };

        public static readonly string[] ImageFileExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".ico",
            ".webp"
        };

        public static readonly string[] AudioFileExtensions =
        {
            ".mp3",
            ".wav",
            ".m4a",
            ".aac",
            ".flac",
            ".wma",
            ".ogg",
            ".oga",
            ".opus"
        };

        public static readonly string[] VideoFileExtensions =
        {
            ".mp4",
            ".m4v",
            ".mov",
            ".wmv",
            ".avi",
            ".mkv",
            ".webm",
            ".mpeg",
            ".mpg"
        };

        public static readonly string[] PdfFileExtensions =
        {
            ".pdf"
        };

        public static readonly string[] OfficeDocumentFileExtensions =
        {
            ".docx",
            ".hwpx",
            ".pptx",
            ".xlsx",
            ".doc",
            ".xls",
            ".ppt"
        };

        public static readonly string[] PickerFileExtensions =
            TextFileExtensions
                .Concat(ImageFileExtensions)
                .Concat(AudioFileExtensions)
                .Concat(VideoFileExtensions)
                .Concat(PdfFileExtensions)
                .Concat(OfficeDocumentFileExtensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public static bool IsImageFile(string filePath)
        {
            return HasExtension(filePath, ImageFileExtensions);
        }

        public static bool IsAudioFile(string filePath)
        {
            return HasExtension(filePath, AudioFileExtensions);
        }

        public static bool IsVideoFile(string filePath)
        {
            return HasExtension(filePath, VideoFileExtensions);
        }

        public static bool IsMediaFile(string filePath)
        {
            return IsAudioFile(filePath) || IsVideoFile(filePath);
        }

        public static bool IsPdfFile(string filePath)
        {
            return HasExtension(filePath, PdfFileExtensions);
        }

        public static bool IsOfficeDocumentFile(string filePath)
        {
            return HasExtension(filePath, OfficeDocumentFileExtensions);
        }

        public static bool IsReadOnlyDocumentFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".doc", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExtension(string filePath, IEnumerable<string> extensions)
        {
            string extension = Path.GetExtension(filePath);
            return extensions.Any(candidate => extension.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }
    }
}
