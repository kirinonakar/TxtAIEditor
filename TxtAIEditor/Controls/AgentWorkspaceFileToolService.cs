using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentWorkspaceFileToolService
    {
        private readonly AgentWorkspaceFileResolver _workspace;

        public AgentWorkspaceFileToolService(AgentWorkspaceFileResolver workspace)
        {
            _workspace = workspace;
        }

        public async Task<string> ListFilesAsync(string? glob, int maxResults)
        {
            string root = _workspace.ResolveWorkspaceRoot();
            int limit = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 1, 300);
            var matches = _workspace.EnumerateWorkspaceEntries(root)
                .Where(path => AgentWorkspaceFileResolver.GlobMatches(AgentWorkspaceFileResolver.RelativePath(root, path), glob))
                .Take(limit)
                .Select(path => FormatWorkspaceEntry(root, path))
                .ToList();

            await Task.CompletedTask;
            return matches.Count == 0
                ? "No files or folders matched."
                : string.Join(Environment.NewLine, matches);
        }

        public async Task<string> SearchTextAsync(string query, string? glob, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "search_text failed: query is empty.";
            }

            string root = _workspace.ResolveWorkspaceRoot();
            int limit = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 1, 300);
            Regex? queryRegex = CreateQueryRegex(query);
            var results = new List<string>();
            var contentSearchFiles = new List<string>();

            foreach (string filePath in _workspace.EnumerateWorkspaceFiles(root).Where(path => AgentWorkspaceFileResolver.GlobMatches(AgentWorkspaceFileResolver.RelativePath(root, path), glob)))
            {
                string relativePath = AgentWorkspaceFileResolver.RelativePath(root, filePath);
                if (MatchesSearchQuery(relativePath, query, queryRegex) ||
                    MatchesSearchQuery(Path.GetFileName(filePath), query, queryRegex))
                {
                    results.Add($"[path] {relativePath}");
                    if (results.Count >= limit)
                    {
                        return string.Join(Environment.NewLine, results);
                    }
                }

                if (ShouldSearchFileContents(filePath))
                {
                    contentSearchFiles.Add(filePath);
                }
            }

            foreach (string filePath in contentSearchFiles)
            {
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(filePath);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (MatchesSearchQuery(lines[i], query, queryRegex))
                    {
                        string preview = lines[i].Trim();
                        if (preview.Length > 220)
                        {
                            preview = preview.Substring(0, 220) + "...";
                        }

                        results.Add($"{AgentWorkspaceFileResolver.RelativePath(root, filePath)}:{i + 1}: {preview}");
                        if (results.Count >= limit)
                        {
                            return string.Join(Environment.NewLine, results);
                        }
                    }
                }
            }

            return results.Count == 0
                ? "No matches found."
                : string.Join(Environment.NewLine, results);
        }

        public async Task<string> ReadFileAsync(string path, int startLine, int lineCount)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "read_file failed: path is empty. Provide the exact file path from the user task or list_files.";
            }

            string fullPath = _workspace.ResolveInsideWorkspace(path, allowOutside: true);
            if (!File.Exists(fullPath))
            {
                return _workspace.BuildMissingFileMessage("read_file", path);
            }

            int start = Math.Max(1, startLine <= 0 ? 1 : startLine);
            int count = Math.Clamp(lineCount <= 0 ? 160 : lineCount, 1, 5000);

            var readLines = new List<string>();
            int currentLine = 0;
            int endLine = start + count - 1;

            using (var reader = new StreamReader(fullPath, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    currentLine++;
                    if (currentLine >= start && currentLine <= endLine)
                    {
                        readLines.Add(line);
                    }
                }
            }

            if (start > currentLine && currentLine > 0)
            {
                return $"read_file: startLine {start} is beyond EOF ({currentLine} lines).";
            }

            var builder = new StringBuilder();
            int lastRead = start + readLines.Count - 1;
            builder.AppendLine($"[File: {path} | Lines {start} to {lastRead} of {currentLine} total lines]");
            for (int i = 0; i < readLines.Count; i++)
            {
                int lineNumber = start + i;
                builder.Append(lineNumber.ToString().PadLeft(5));
                builder.Append(" | ");
                builder.AppendLine(readLines[i]);
            }

            if (lastRead < currentLine)
            {
                builder.AppendLine($"[... {currentLine - lastRead} more lines. Use read_file with startLine={lastRead + 1} and larger lineCount if needed to read more ...]");
            }

            return builder.ToString();
        }

        private static bool ShouldSearchFileContents(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 10 * 1024 * 1024)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static string FormatWorkspaceEntry(string root, string path)
        {
            string relativePath = AgentWorkspaceFileResolver.RelativePath(root, path);
            return Directory.Exists(path)
                ? relativePath.TrimEnd('/') + "/"
                : relativePath;
        }

        private static Regex? CreateQueryRegex(string query)
        {
            try
            {
                return new Regex(
                    query,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch
            {
                return null;
            }
        }

        private static bool MatchesSearchQuery(string value, string query, Regex? queryRegex)
        {
            if (queryRegex == null)
            {
                return value.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                return queryRegex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return value.Contains(query, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
