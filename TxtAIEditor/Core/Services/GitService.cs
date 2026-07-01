using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public class GitService : IGitService
    {
        public string? FindRepositoryRoot(string? startPath)
        {
            if (string.IsNullOrEmpty(startPath))
            {
                return null;
            }

            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                string gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        public async Task<string> GetCurrentBranchAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return string.Empty;

            try
            {
                // Check if directory is a git repo first
                string gitDir = Path.Combine(repoPath, ".git");
                if (!Directory.Exists(gitDir) && !File.Exists(gitDir)) // Submodules or worktrees might have .git as file
                {
                    // Check parent directories as well
                    var parent = Directory.GetParent(repoPath);
                    if (parent != null)
                    {
                        return await GetCurrentBranchAsync(parent.FullName);
                    }
                    return string.Empty;
                }

                string output = await RunGitCommandAsync(repoPath, "symbolic-ref --quiet --short HEAD");
                if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                {
                    output = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD");
                    if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                }

                return $"Git: {output.Trim()}";
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<Dictionary<string, string>> GetFileStatusesAsync(string repoPath)
        {
            var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return statuses;

            try
            {
                string output = await RunGitCommandAsync(repoPath, "status --porcelain=v1 -z --ignored");
                if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                    return statuses;

                string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < entries.Length; i++)
                {
                    string entry = entries[i];
                    if (entry.Length >= 4)
                    {
                        string status = entry.Substring(0, 2);
                        string relativePath = NormalizeStatusPath(entry.Substring(3));

                        // In -z porcelain, rename/copy entries are followed by the original path.
                        // Check both index status and worktree status (e.g. status contains 'R' or 'C') to handle unstaged renames correctly.
                        if ((status.Contains('R') || status.Contains('C')) && i + 1 < entries.Length)
                        {
                            i++;
                        }

                        if (!string.IsNullOrEmpty(relativePath))
                        {
                            string fullPath = Path.GetFullPath(Path.Combine(repoPath, relativePath));
                            statuses[fullPath] = status;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get git status: {ex.Message}");
            }

            return statuses;
        }

        private static string NormalizeStatusPath(string path)
        {
            return path
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ToGitPathSpec(string repoPath, string filePath)
        {
            string relativePath = Path.IsPathRooted(filePath)
                ? Path.GetRelativePath(repoPath, filePath)
                : filePath;

            return relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .TrimEnd('/');
        }

        private static string QuotePath(string path)
        {
            return path.Replace("\"", "\\\"");
        }

        public async Task<string> RunGitCommandAsync(string workingDir, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-c core.quotepath=false {arguments}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment["LANG"] = "C.UTF-8";
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
            startInfo.Environment["OUTPUT_CHARSET"] = "UTF-8";

            using (var process = new Process { StartInfo = startInfo })
            {
                try
                {
                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    
                    // We must wait for exit to ensure cleanup
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode != 0)
                    {
                        return $"fatal: {error}";
                    }

                    return output;
                }
                catch (Exception ex)
                {
                    return $"fatal: {ex.Message}";
                }
            }
        }

        public async Task<string> GetFileDiffAsync(string repoPath, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(filePath))
                return string.Empty;

            try
            {
                // Make path relative to repoPath to satisfy git cli arguments
                string relativePath = ToGitPathSpec(repoPath, filePath);
                string quotedRelativePath = QuotePath(relativePath);

                // Run diff. First check unstaged diff, then cached (staged) diff.
                string unstagedDiff = await RunGitCommandAsync(repoPath, $"diff -- \"{quotedRelativePath}\"");
                string stagedDiff = await RunGitCommandAsync(repoPath, $"diff --cached -- \"{quotedRelativePath}\"");

                // If untracked file, diff might be empty, so let's show the whole file content as addition
                if (string.IsNullOrEmpty(unstagedDiff) && string.IsNullOrEmpty(stagedDiff))
                {
                    // Check if file is untracked
                    string status = await RunGitCommandAsync(repoPath, $"status --porcelain -- \"{quotedRelativePath}\"");
                    if (status.StartsWith("?") || status.Trim().Length > 0)
                    {
                        if (File.Exists(filePath))
                        {
                            var lines = await File.ReadAllLinesAsync(filePath);
                            var sb = new StringBuilder();
                            sb.AppendLine($"--- /dev/null");
                            sb.AppendLine($"+++ b/{relativePath}");
                            sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
                            foreach (var line in lines)
                            {
                                sb.AppendLine($"+{line}");
                            }
                            return sb.ToString();
                        }
                    }
                    return "변경 내역이 없거나 감지되지 않았습니다.";
                }

                var fullDiff = new StringBuilder();
                if (!string.IsNullOrEmpty(stagedDiff) && !stagedDiff.StartsWith("fatal:"))
                {
                    fullDiff.AppendLine("=== Staged Changes ===");
                    fullDiff.AppendLine(stagedDiff);
                }
                if (!string.IsNullOrEmpty(unstagedDiff) && !unstagedDiff.StartsWith("fatal:"))
                {
                    if (fullDiff.Length > 0) fullDiff.AppendLine();
                    fullDiff.AppendLine("=== Unstaged Changes ===");
                    fullDiff.AppendLine(unstagedDiff);
                }

                return fullDiff.ToString();
            }
            catch (Exception ex)
            {
                return $"fatal: {ex.Message}";
            }
        }

        public async Task<string> GetGitFileContentAsync(string repoPath, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(filePath))
                return string.Empty;

            try
            {
                string relativePath = ToGitPathSpec(repoPath, filePath);
                string quotedRelativePath = QuotePath(relativePath);

                // Try to get staged version first (index :0), then HEAD version
                string content = await RunGitCommandAsync(repoPath, $"show :0:\"{quotedRelativePath}\"");
                if (content.StartsWith("fatal:"))
                {
                    content = await RunGitCommandAsync(repoPath, $"show HEAD:\"{quotedRelativePath}\"");
                }

                if (content.StartsWith("fatal:"))
                {
                    return string.Empty; // File is probably untracked/new
                }

                return content;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<bool> StageFileAsync(string repoPath, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(filePath))
                return false;

            string relativePath = ToGitPathSpec(repoPath, filePath);
            string output = await RunGitCommandAsync(repoPath, $"add -- \"{QuotePath(relativePath)}\"");
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> StageAllAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return false;

            string output = await RunGitCommandAsync(repoPath, "add -A");
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> UnstageFileAsync(string repoPath, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(filePath))
                return false;

            string relativePath = ToGitPathSpec(repoPath, filePath);
            string output = await RunGitCommandAsync(repoPath, $"restore --staged -- \"{QuotePath(relativePath)}\"");
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> RestoreFileAsync(string repoPath, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(filePath))
                return false;

            string relativePath = ToGitPathSpec(repoPath, filePath);
            string status = await RunGitCommandAsync(repoPath, $"status --porcelain -- \"{QuotePath(relativePath)}\"");

            if (status.TrimStart().StartsWith("??", StringComparison.Ordinal))
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    else if (Directory.Exists(filePath))
                    {
                        Directory.Delete(filePath, recursive: true);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            string output = await RunGitCommandAsync(repoPath, $"restore --staged --worktree -- \"{QuotePath(relativePath)}\"");
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> RestoreAllAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return false;

            var statuses = await GetFileStatusesAsync(repoPath);
            foreach (var kvp in statuses)
            {
                if (kvp.Value == "!!" || kvp.Value.Trim() == "!!")
                {
                    continue; // Skip restoring ignored files
                }
                bool ok = await RestoreFileAsync(repoPath, kvp.Key);
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> CommitAsync(string repoPath, string message)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(message))
                return false;

            // Escape quotes in commit message
            string escapedMsg = message.Replace("\"", "\\\"");
            string output = await RunGitCommandAsync(repoPath, $"commit -m \"{escapedMsg}\"");
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> PushAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return false;

            string upstream = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref --symbolic-full-name @{u}");
            string output;
            if (string.IsNullOrWhiteSpace(upstream) || upstream.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                string branch = await GetCurrentBranchNameAsync(repoPath);
                if (string.IsNullOrEmpty(branch))
                {
                    return false;
                }

                output = await RunGitCommandAsync(repoPath, $"push -u origin \"{QuotePath(branch)}\"");
            }
            else
            {
                output = await RunGitCommandAsync(repoPath, "push");
            }

            return !output.StartsWith("fatal:");
        }

        public async Task<bool> PullAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return false;

            string output = await RunUpstreamAwarePullAsync(repoPath, rebase: false);
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> RebaseAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return false;

            string output = await RunUpstreamAwarePullAsync(repoPath, rebase: true);
            return !output.StartsWith("fatal:");
        }

        public async Task<bool> CheckoutBranchAsync(string repoPath, string branchName)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrWhiteSpace(branchName))
                return false;

            string output = await RunGitCommandAsync(repoPath, $"checkout \"{QuotePath(branchName.Trim())}\"");
            return !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetRemoteUrlAsync(string repoPath, string remoteName = "origin")
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(remoteName))
                return string.Empty;

            string output = await RunGitCommandAsync(repoPath, $"remote get-url {remoteName}");
            if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return output.Trim();
        }

        public async Task<bool> SetRemoteUrlAsync(string repoPath, string remoteUrl, string remoteName = "origin")
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrEmpty(remoteName))
                return false;

            string existingUrl = await GetRemoteUrlAsync(repoPath, remoteName);
            string escapedUrl = QuotePath(remoteUrl.Trim());
            string command = string.IsNullOrEmpty(existingUrl)
                ? $"remote add {remoteName} \"{escapedUrl}\""
                : $"remote set-url {remoteName} \"{escapedUrl}\"";

            string output = await RunGitCommandAsync(repoPath, command);
            if (output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await LinkCurrentBranchToRemoteAsync(repoPath, remoteName);
            return true;
        }

        public async Task<bool> LinkCurrentBranchToRemoteAsync(string repoPath, string remoteName = "origin")
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(remoteName))
                return false;

            string branch = await GetCurrentBranchNameAsync(repoPath);
            if (string.IsNullOrEmpty(branch))
            {
                return false;
            }

            string output = await RunGitCommandAsync(
                repoPath,
                $"branch --set-upstream-to=\"{remoteName}/{QuotePath(branch)}\" \"{QuotePath(branch)}\"");
            return !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> RunUpstreamAwarePullAsync(string repoPath, bool rebase)
        {
            string upstream = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref --symbolic-full-name @{u}");
            if (!string.IsNullOrWhiteSpace(upstream) && !upstream.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                return await RunGitCommandAsync(repoPath, rebase ? "pull --rebase" : "pull");
            }

            string branch = await GetCurrentBranchNameAsync(repoPath);
            if (string.IsNullOrEmpty(branch))
            {
                return "fatal: current branch could not be detected";
            }

            string command = rebase
                ? $"pull --rebase origin \"{QuotePath(branch)}\""
                : $"pull origin \"{QuotePath(branch)}\"";
            string output = await RunGitCommandAsync(repoPath, command);
            if (!output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                await LinkCurrentBranchToRemoteAsync(repoPath);
            }

            return output;
        }

        private async Task<string> GetCurrentBranchNameAsync(string repoPath)
        {
            string branch = await RunGitCommandAsync(repoPath, "symbolic-ref --quiet --short HEAD");
            if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                branch = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD");
            }

            branch = branch.Trim();
            return branch.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
                   branch.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : branch;
        }

        public async Task<int> GetUnpushedCommitCountAsync(string repoPath)
        {
            if (!await HasRemoteAsync(repoPath))
            {
                return 0;
            }

            string output = await RunGitCommandAsync(repoPath, "rev-list --count HEAD --not --remotes");
            return int.TryParse(output.Trim(), out int count) ? count : 0;
        }

        public async Task<IReadOnlyList<GitHistoryItem>> GetRecentHistoryAsync(string repoPath, int maxCount = 50)
        {
            if (string.IsNullOrEmpty(repoPath))
                return Array.Empty<GitHistoryItem>();

            // Keep the hash for actions, but do not include it in the sidebar display text.
            string output = await RunGitCommandAsync(repoPath, $"log --graph --all --decorate=short --pretty=format:\"%H%x1f%d %s - %cd\" --date=format:\"%Y-%m-%d %H:%M\" -n {Math.Max(1, maxCount)}");
            if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<GitHistoryItem>();

            var historyItems = output
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseHistoryLine)
                .Where(item => !string.IsNullOrWhiteSpace(item.DisplayText))
                .ToArray();
            var unpushedHashes = await GetUnpushedCommitShortHashesAsync(repoPath, maxCount);
            if (unpushedHashes.Count == 0)
            {
                return historyItems;
            }

            return historyItems
                .Select(item => MarkUnpushedHistoryItem(item, unpushedHashes))
                .ToArray();
        }

        private static GitHistoryItem ParseHistoryLine(string line)
        {
            int separatorIndex = line.IndexOf('\u001f');
            if (separatorIndex < 0)
            {
                return new GitHistoryItem { DisplayText = line };
            }

            string hashSegment = line.Substring(0, separatorIndex);
            string commitHash = FindFullCommitHash(hashSegment);
            string graphPrefix = string.IsNullOrEmpty(commitHash)
                ? hashSegment
                : hashSegment.Replace(commitHash, string.Empty, StringComparison.Ordinal).TrimEnd();
            string commitText = line.Substring(separatorIndex + 1).TrimStart();

            string displayText = string.IsNullOrEmpty(graphPrefix)
                ? commitText
                : $"{graphPrefix} {commitText}".TrimEnd();

            return new GitHistoryItem
            {
                CommitHash = commitHash,
                DisplayText = displayText
            };
        }

        private static string FindFullCommitHash(string value)
        {
            const int FullHashLength = 40;
            for (int i = 0; i <= value.Length - FullHashLength; i++)
            {
                string candidate = value.Substring(i, FullHashLength);
                if (candidate.All(IsHexCharacter))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private async Task<HashSet<string>> GetUnpushedCommitShortHashesAsync(string repoPath, int maxCount)
        {
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!await HasRemoteAsync(repoPath))
            {
                return hashes;
            }

            string output = await RunGitCommandAsync(repoPath, $"log --format:%h -n {Math.Max(1, maxCount)} HEAD --not --remotes");
            if (string.IsNullOrWhiteSpace(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                return hashes;
            }

            foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string hash = line.Trim();
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes.Add(hash);
                }
            }

            return hashes;
        }

        private async Task<bool> HasRemoteAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                return false;
            }

            string output = await RunGitCommandAsync(repoPath, "remote");
            return !string.IsNullOrWhiteSpace(output) && !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase);
        }

        private static GitHistoryItem MarkUnpushedHistoryItem(GitHistoryItem item, HashSet<string> unpushedHashes)
        {
            bool isUnpushed = unpushedHashes.Any(hash => item.CommitHash.StartsWith(hash, StringComparison.OrdinalIgnoreCase));
            if (!isUnpushed)
            {
                return item;
            }

            int insertIndex = 0;
            while (insertIndex < item.DisplayText.Length && IsGitGraphCharacter(item.DisplayText[insertIndex]))
            {
                insertIndex++;
            }

            return new GitHistoryItem
            {
                CommitHash = item.CommitHash,
                DisplayText = item.DisplayText.Insert(insertIndex, "\u2191 ")
            };
        }

        private static bool IsGitGraphCharacter(char value)
        {
            return value is ' ' or '*' or '|' or '/' or '\\' or '_' or '-';
        }

        private static bool IsHexCharacter(char value)
        {
            return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
        }

        public async Task<bool> InitRepositoryAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return false;

            string output = await RunGitCommandAsync(repoPath, "init");
            return !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyList<string>> GetBranchesAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return Array.Empty<string>();

            string output = await RunGitCommandAsync(repoPath, "branch --no-color");
            if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                string currentBranch = await RunGitCommandAsync(repoPath, "symbolic-ref --quiet --short HEAD");
                if (string.IsNullOrEmpty(currentBranch) || currentBranch.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                {
                    currentBranch = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD");
                }

                if (string.IsNullOrEmpty(currentBranch) || currentBranch.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                {
                    return Array.Empty<string>();
                }

                return new[] { $"* {currentBranch.Trim()}" };
            }

            return output
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(branch =>
                {
                    string normalized = branch.TrimStart('*', ' ').Trim();
                    return !normalized.StartsWith("remotes/", StringComparison.OrdinalIgnoreCase) &&
                           !normalized.StartsWith("origin/", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
        }

        public async Task<IReadOnlyList<(string Status, string Path)>> GetCommitChangedFilesAsync(string repoPath, string commitHash)
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(commitHash))
                return list;

            try
            {
                string output = await RunGitCommandAsync(repoPath, $"diff-tree --no-commit-id --name-status -r {commitHash}");
                if (string.IsNullOrEmpty(output) || output.StartsWith("fatal:"))
                    return list;

                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        list.Add((parts[0].Trim(), parts[1].Trim().Replace('/', '\\')));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get commit changed files: {ex.Message}");
            }

            return list;
        }

        public async Task<string> GetCommitFileContentAsync(string repoPath, string commitHash, string filePath)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(commitHash) || string.IsNullOrEmpty(filePath))
                return string.Empty;

            try
            {
                string relativePath = ToGitPathSpec(repoPath, filePath);
                string quotedRelativePath = QuotePath(relativePath);

                string content = await RunGitCommandAsync(repoPath, $"show {commitHash}:\"{quotedRelativePath}\"");
                if (content.StartsWith("fatal:"))
                {
                    return string.Empty;
                }

                return content;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
