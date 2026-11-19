using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentWorkspaceFileResolver
    {
        private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".next", "dist", "build"
        };

        private readonly Func<string> _workspaceRootProvider;
        private readonly Func<IReadOnlyList<string>>? _readAttachmentPathsProvider;
        private readonly Func<string, CancellationToken, Task<bool>>? _confirmExternalReadAsync;

        public AgentWorkspaceFileResolver(
            Func<string> workspaceRootProvider,
            Func<IReadOnlyList<string>>? readAttachmentPathsProvider = null,
            Func<string, CancellationToken, Task<bool>>? confirmExternalReadAsync = null)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _readAttachmentPathsProvider = readAttachmentPathsProvider;
            _confirmExternalReadAsync = confirmExternalReadAsync;
        }

        public string WorkspaceRoot => ResolveWorkspaceRoot();

        public string ResolveWorkspaceRoot() => ResolveWorkspaceDirectory(_workspaceRootProvider());

        internal static string ResolveWorkspaceDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string fullPath = ResolvePhysicalPath(Path.GetFullPath(root));
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Workspace directory is unavailable: {root}");
            }
            return Path.TrimEndingDirectorySeparator(fullPath);
        }

        internal static void VerifyPinnedWorkspace(string root, string? expectedPhysicalRoot = null)
        {
            string expected = expectedPhysicalRoot ?? root;
            if (string.Equals(root, expected, StringComparison.Ordinal))
            {
                VerifyPhysicalPath(root);
                if (!Directory.Exists(root))
                {
                    throw new DirectoryNotFoundException($"Workspace directory is unavailable: {root}");
                }
                return;
            }
            string current = ResolveWorkspaceDirectory(root);
            if (!SamePhysicalPath(expected, current))
            {
                throw new IOException($"Workspace directory changed during the run: {root}");
            }
        }

        public string ResolveInsideWorkspace(string path) => ResolvePath(path, allowOutside: false).FullPath;

        public async Task<string> ResolveReadPathAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fullPath, candidate, outsideWorkspace) = ResolvePath(path, allowOutside: true);
            if (outsideWorkspace &&
                _readAttachmentPathsProvider?.Invoke().Any(attachedPath => SamePhysicalPath(candidate, attachedPath)) != true)
            {
                bool approved = _confirmExternalReadAsync != null &&
                    await _confirmExternalReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!approved)
                {
                    throw new UnauthorizedAccessException($"Permission to read the external file was declined: {fullPath}");
                }
            }

            return fullPath;
        }

        private (string FullPath, string Candidate, bool OutsideWorkspace) ResolvePath(string path, bool allowOutside)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Path is empty.");
            }

            string configuredRoot = _workspaceRootProvider();
            string root = ResolveWorkspaceDirectory(configuredRoot);
            if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException($"Use a workspace-relative or fully qualified path: {path}");
            }
            string candidate = Path.GetFullPath(path, root);

            if (IsInsideRoot(root, candidate))
            {
                RequirePhysicalContainment(root, candidate);
                return (ResolvePhysicalPath(candidate, confinedTo: root), candidate, false);
            }
            if (!string.IsNullOrWhiteSpace(configuredRoot) && IsInsideRoot(configuredRoot, candidate))
            {
                RequirePhysicalContainment(configuredRoot, candidate);
                string resolved = ResolvePhysicalPath(Path.GetFullPath(Path.GetRelativePath(configuredRoot, candidate), root), confinedTo: root);
                return (resolved, candidate, false);
            }
            if (AgentSkillDirectories.IsInsideUserSkillsDirectory(candidate))
            {
                string configuredSkills = AgentSkillDirectories.UserSkillsDirectory;
                string skillsRoot = ResolvePhysicalPath(Path.GetFullPath(configuredSkills));
                RequirePhysicalContainment(configuredSkills, candidate);
                string resolved = ResolvePhysicalPath(Path.GetFullPath(Path.GetRelativePath(configuredSkills, candidate), skillsRoot), confinedTo: skillsRoot);
                return (resolved, candidate, !IsInsidePhysicalRoot(root, resolved));
            }
            if (allowOutside)
            {
                return (ResolvePhysicalPath(candidate), candidate, true);
            }

            throw new InvalidOperationException($"Path escapes workspace root: {path}");
        }

        public void VerifyResolvedPath(string fullPath)
        {
            // The original path may have used a link. The path returned to the caller is
            // physical and must still identify that same location when used.
            ResolveWorkspaceRoot();
            VerifyPhysicalPath(fullPath);
        }

        public void VerifyWritablePath(string fullPath, bool allowSkills = true)
        {
            string root = ResolveWorkspaceRoot();
            if (IsInsideRoot(root, fullPath))
            {
                RequirePhysicalContainment(root, fullPath);
            }
            else if (allowSkills)
            {
                string skills = ResolvePhysicalPath(Path.GetFullPath(AgentSkillDirectories.UserSkillsDirectory));
                RequirePhysicalContainment(skills, fullPath);
            }
            else
            {
                throw new IOException($"Write target escapes workspace root: {fullPath}");
            }
            VerifyPhysicalPath(fullPath);
            if (Directory.Exists(fullPath))
            {
                throw new IOException($"Expected a file, found a directory: {fullPath}");
            }
        }

        internal static void VerifyPhysicalPath(string fullPath)
        {
            _ = ResolvePhysicalPath(fullPath, followLinks: false);
        }

        internal static string ResolvePhysicalPath(string fullPath, string? confinedTo = null, bool followLinks = true, int remainingLinks = 63)
        {
            if (remainingLinks == 0 || !Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw new IOException($"Unsupported device path or link chain: {fullPath}");
            }
            string current = Path.GetPathRoot(fullPath)!;
            string[] parts = fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part.Contains(':') || part.TrimEnd(' ', '.') != part)
                {
                    throw new IOException($"Unsupported path component: {part}");
                }
                current = Path.Combine(current, part);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !AgentWindowsFileMetadata.IsCloudPlaceholder(current))
                {
                    if (!followLinks)
                    {
                        throw new IOException($"Resolved path changed before use: {fullPath}");
                    }
                    FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(current) : new FileInfo(current);
                    FileSystemInfo target = entry.ResolveLinkTarget(returnFinalTarget: false)
                        ?? throw new IOException($"Unsupported reparse point: {current}");
                    if (confinedTo != null)
                    {
                        RequirePhysicalContainment(confinedTo, target.FullName);
                    }
                    current = ResolvePhysicalPath(target.FullName, confinedTo, followLinks, --remainingLinks);
                }
            }
            return current;
        }

        internal static bool SamePhysicalPath(string first, string second) =>
            first.Equals(second, StringComparison.Ordinal) ||
            (first.Equals(second, StringComparison.OrdinalIgnoreCase) && AgentWindowsFileMetadata.AreSameDirectory(first, second));

        internal static bool IsInsidePhysicalRoot(string root, string path)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return IsInsideRoot(root, path) && path.Length >= root.Length && SamePhysicalPath(root, path[..root.Length]);
        }

        private static void RequirePhysicalContainment(string root, string path)
        {
            if (!IsInsidePhysicalRoot(root, path))
            {
                throw new IOException($"Path escapes workspace root: {path}");
            }
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
            var gitIgnore = GitIgnoreMatcher.Load(this, root);
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                FileSystemInfo[] entries;

                try
                {
                    VerifyResolvedPath(dir);
                    entries = new DirectoryInfo(dir).GetFileSystemInfos();
                }
                catch
                {
                    ResolveWorkspaceRoot();
                    continue;
                }

                foreach (FileSystemInfo entry in entries)
                {
                    // Recursive scans do not follow links, including junctions and cycles.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && !AgentWindowsFileMetadata.IsCloudPlaceholder(entry.FullName))
                    {
                        continue;
                    }

                    bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (gitIgnore.IsIgnored(RelativePath(root, entry.FullName), isDirectory) ||
                        (isDirectory && ExcludedDirectoryNames.Contains(entry.Name)))
                    {
                        continue;
                    }
                    if (!isDirectory || includeDirectories)
                    {
                        yield return entry.FullName;
                    }
                    if (isDirectory)
                    {
                        pending.Push(entry.FullName);
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
            // Workspace-relative paths do not start with '/', so treat a leading
            // slash in the glob as a workspace-root anchor (for example, /*).
            string normalizedGlob = glob.Replace('\\', '/').TrimStart('/');
            if (normalizedGlob.Length == 0)
            {
                return false;
            }

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

            public static GitIgnoreMatcher Load(AgentWorkspaceFileResolver workspace, string root)
            {
                string ignorePath = Path.Combine(root, ".gitignore");
                if (!File.Exists(ignorePath))
                {
                    return new GitIgnoreMatcher(new List<GitIgnoreRule>());
                }

                var rules = new List<GitIgnoreRule>();
                try
                {
                    workspace.VerifyResolvedPath(ignorePath);
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
