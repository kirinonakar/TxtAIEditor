using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentWorkspaceFileResolver
    {
        private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".next", "dist", "build"
        };

        private readonly Func<string> _workspaceRootProvider;

        public AgentWorkspaceFileResolver(Func<string> workspaceRootProvider)
        {
            _workspaceRootProvider = workspaceRootProvider;
        }

        public string WorkspaceRoot => ResolveWorkspaceRoot();

        public string ResolveWorkspaceRoot()
        {
            string root = _workspaceRootProvider();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string fullPath = Path.GetFullPath(root);
            try
            {
                var dirInfo = new DirectoryInfo(fullPath);
                if (dirInfo.Exists)
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        fullPath = target.FullName;
                    }
                }
            }
            catch
            {
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public string ResolveInsideWorkspace(string path, bool allowOutside = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Path is empty.");
            }

            string root = ResolveWorkspaceRoot();
            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            if (IsInsideRoot(root, candidate))
            {
                return candidate;
            }

            if (AgentSkillDirectories.IsInsideUserSkillsDirectory(candidate))
            {
                return candidate;
            }

            if (allowOutside && Path.IsPathRooted(path) && File.Exists(candidate))
            {
                return candidate;
            }

            throw new InvalidOperationException($"Path escapes workspace root: {path}");
        }

        public IEnumerable<string> EnumerateWorkspaceFiles()
        {
            return EnumerateWorkspacePaths(ResolveWorkspaceRoot(), includeDirectories: false);
        }

        public IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
            return EnumerateWorkspacePaths(root, includeDirectories: false);
        }

        public IEnumerable<string> EnumerateWorkspaceEntries()
        {
            return EnumerateWorkspacePaths(ResolveWorkspaceRoot(), includeDirectories: true);
        }

        public IEnumerable<string> EnumerateWorkspaceEntries(string root)
        {
            return EnumerateWorkspacePaths(root, includeDirectories: true);
        }

        private IEnumerable<string> EnumerateWorkspacePaths(string root, bool includeDirectories)
        {
            var gitIgnore = GitIgnoreMatcher.Load(root);
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<string> subdirs;
                IEnumerable<string> files;

                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (gitIgnore.IsIgnored(RelativePath(root, file), isDirectory: false))
                    {
                        continue;
                    }

                    yield return file;
                }

                foreach (string subdir in subdirs)
                {
                    string name = Path.GetFileName(subdir);
                    string relativeSubdir = RelativePath(root, subdir);
                    if (!ExcludedDirectoryNames.Contains(name) &&
                        !gitIgnore.IsIgnored(relativeSubdir, isDirectory: true))
                    {
                        if (includeDirectories)
                        {
                            yield return subdir;
                        }

                        pending.Push(subdir);
                    }
                }
            }
        }

        public string BuildMissingFileMessage(string toolName, string path)
        {
            var builder = new StringBuilder();
            builder.Append($"{toolName} failed: file not found: {path}");

            var suggestions = SuggestWorkspaceFiles(path, 10).ToList();
            if (suggestions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Possible workspace matches. Preserve exact non-English file names; do not translate them:");
                foreach (string suggestion in suggestions)
                {
                    builder.AppendLine($"- {suggestion}");
                }
            }

            return builder.ToString();
        }

        public string GetAvailableSiblingPath(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return fullPath;
            }

            string directory = Path.GetDirectoryName(fullPath) ?? ResolveWorkspaceRoot();
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);

            int counter = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, $"{filenameWithoutExtension} ({counter}){extension}");
                counter++;
            } while (File.Exists(candidate));

            return candidate;
        }

        public string RelativePath(string path)
        {
            return RelativePath(ResolveWorkspaceRoot(), path);
        }

        public static string RelativePath(string root, string path)
        {
            return IsInsideRoot(root, path)
                ? Path.GetRelativePath(root, path).Replace('\\', '/')
                : path;
        }

        public static bool IsInsideRoot(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path);
            string rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
        }

        public static bool GlobMatches(string relativePath, string? glob)
        {
            if (string.IsNullOrWhiteSpace(glob) || glob == "*" || glob == "**/*")
            {
                return true;
            }

            string normalizedPath = relativePath.Replace('\\', '/');
            string normalizedGlob = glob.Replace('\\', '/');
            string pattern = "^" + Regex.Escape(normalizedGlob)
                .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";

            return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase);
        }

        private sealed class GitIgnoreMatcher
        {
            private readonly List<GitIgnoreRule> _rules;

            private GitIgnoreMatcher(List<GitIgnoreRule> rules)
            {
                _rules = rules;
            }

            public static GitIgnoreMatcher Load(string root)
            {
                string ignorePath = Path.Combine(root, ".gitignore");
                if (!File.Exists(ignorePath))
                {
                    return new GitIgnoreMatcher(new List<GitIgnoreRule>());
                }

                var rules = new List<GitIgnoreRule>();
                try
                {
                    foreach (string rawLine in File.ReadLines(ignorePath))
                    {
                        var rule = GitIgnoreRule.TryParse(rawLine);
                        if (rule != null)
                        {
                            rules.Add(rule);
                        }
                    }
                }
                catch
                {
                }

                return new GitIgnoreMatcher(rules);
            }

            public bool IsIgnored(string relativePath, bool isDirectory)
            {
                if (_rules.Count == 0 || string.IsNullOrWhiteSpace(relativePath))
                {
                    return false;
                }

                string normalized = relativePath.Replace('\\', '/').Trim('/');
                bool ignored = false;
                foreach (GitIgnoreRule rule in _rules)
                {
                    if (rule.IsMatch(normalized, isDirectory))
                    {
                        ignored = !rule.IsNegated;
                    }
                }

                return ignored;
            }
        }

        private sealed class GitIgnoreRule
        {
            private readonly Regex _regex;

            private GitIgnoreRule(string pattern, bool isNegated, bool isDirectoryOnly, bool hasSlash, bool isAnchored)
            {
                IsNegated = isNegated;
                IsDirectoryOnly = isDirectoryOnly;
                HasSlash = hasSlash;
                IsAnchored = isAnchored;
                _regex = new Regex("^" + GlobToRegex(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            public bool IsNegated { get; }
            public bool IsDirectoryOnly { get; }
            public bool HasSlash { get; }
            public bool IsAnchored { get; }

            public static GitIgnoreRule? TryParse(string rawLine)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    return null;
                }

                bool isNegated = false;
                if (line.StartsWith("!", StringComparison.Ordinal))
                {
                    isNegated = true;
                    line = line.Substring(1).TrimStart();
                    if (line.Length == 0)
                    {
                        return null;
                    }
                }

                line = line.Replace('\\', '/');
                bool isAnchored = line.StartsWith("/", StringComparison.Ordinal);
                if (isAnchored)
                {
                    line = line.TrimStart('/');
                }

                bool isDirectoryOnly = line.EndsWith("/", StringComparison.Ordinal);
                if (isDirectoryOnly)
                {
                    line = line.TrimEnd('/');
                }

                if (line.Length == 0)
                {
                    return null;
                }

                return new GitIgnoreRule(line, isNegated, isDirectoryOnly, line.Contains('/'), isAnchored);
            }

            public bool IsMatch(string relativePath, bool isDirectory)
            {
                string path = relativePath.Trim('/');
                if (path.Length == 0)
                {
                    return false;
                }

                if (IsDirectoryOnly && !isDirectory && !MatchesAnyParentDirectory(path))
                {
                    return false;
                }

                if (!HasSlash)
                {
                    foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (_regex.IsMatch(segment))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (_regex.IsMatch(path))
                {
                    return true;
                }

                if (IsDirectoryOnly)
                {
                    return MatchesAnyParentDirectory(path);
                }

                return !IsAnchored && _regex.IsMatch(Path.GetFileName(path));
            }

            private bool MatchesAnyParentDirectory(string path)
            {
                string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length <= 1)
                {
                    return false;
                }

                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string parent = string.Join('/', segments.Take(i + 1));
                    if (_regex.IsMatch(HasSlash ? parent : segments[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static string GlobToRegex(string pattern)
            {
                var builder = new StringBuilder();
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c == '*')
                    {
                        bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                        if (doubleStar)
                        {
                            builder.Append(".*");
                            i++;
                        }
                        else
                        {
                            builder.Append("[^/]*");
                        }
                    }
                    else if (c == '?')
                    {
                        builder.Append("[^/]");
                    }
                    else if (c == '[')
                    {
                        int end = pattern.IndexOf(']', i + 1);
                        if (end > i)
                        {
                            builder.Append(pattern.Substring(i, end - i + 1));
                            i = end;
                        }
                        else
                        {
                            builder.Append("\\[");
                        }
                    }
                    else
                    {
                        builder.Append(Regex.Escape(c.ToString()));
                    }
                }

                return builder.ToString();
            }
        }

        private IEnumerable<string> SuggestWorkspaceFiles(string path, int maxResults)
        {
            string root = ResolveWorkspaceRoot();
            string requestedName = Path.GetFileName(path);
            string requestedExtension = Path.GetExtension(path);
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in EnumerateWorkspaceFiles(root))
            {
                string relative = RelativePath(root, filePath);
                if (string.Equals(Path.GetFileName(filePath), requestedName, StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(relative))
                {
                    yield return relative;
                    if (yielded.Count >= maxResults) yield break;
                }
            }

            if (string.IsNullOrWhiteSpace(requestedExtension))
            {
                yield break;
            }

            foreach (string filePath in EnumerateWorkspaceFiles(root))
            {
                string relative = RelativePath(root, filePath);
                if (string.Equals(Path.GetExtension(filePath), requestedExtension, StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(relative))
                {
                    yield return relative;
                    if (yielded.Count >= maxResults) yield break;
                }
            }
        }
    }
}
