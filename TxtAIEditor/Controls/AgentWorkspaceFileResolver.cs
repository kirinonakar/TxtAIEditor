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

            if (allowOutside && Path.IsPathRooted(path) && File.Exists(candidate))
            {
                return candidate;
            }

            throw new InvalidOperationException($"Path escapes workspace root: {path}");
        }

        public IEnumerable<string> EnumerateWorkspaceFiles()
        {
            return EnumerateWorkspaceFiles(ResolveWorkspaceRoot());
        }

        public IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
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
                    yield return file;
                }

                foreach (string subdir in subdirs)
                {
                    string name = Path.GetFileName(subdir);
                    if (!ExcludedDirectoryNames.Contains(name))
                    {
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
