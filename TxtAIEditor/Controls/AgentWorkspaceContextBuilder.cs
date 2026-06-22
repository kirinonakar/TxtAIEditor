using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentWorkspaceContextBuilder
    {
        private readonly Func<string> _workspaceRootProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly AgentAttachmentController _attachmentController;

        public AgentWorkspaceContextBuilder(
            Func<string> workspaceRootProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            AgentAttachmentController attachmentController)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _openTabsProvider = openTabsProvider;
            _attachmentController = attachmentController;
        }

        public string Build(
            string instruction,
            OpenedTab? activeTab,
            bool includeActiveFile,
            bool hasSelectionRangeContext,
            IEnumerable<AgentAttachmentState>? attachments = null,
            string? workspaceRootOverride = null)
        {
            string workspaceRoot = string.IsNullOrWhiteSpace(workspaceRootOverride)
                ? _workspaceRootProvider()
                : workspaceRootOverride;
            var context = new List<string>();
            context.Add("[Workspace root]");
            context.Add(workspaceRoot);
            context.Add("");

            AddReferencedPathContext(context, instruction, workspaceRoot);
            AddOpenTabsContext(context, activeTab);
            AddActiveTabContext(context, activeTab, includeActiveFile, hasSelectionRangeContext);
            AddAttachmentsContext(context, attachments);

            return string.Join(Environment.NewLine, context);
        }

        public static bool IsPdfTab(OpenedTab tab)
        {
            return tab.IsPdfViewer ||
                   string.Equals(tab.Language, "pdf", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(tab.FilePath) &&
                    string.Equals(Path.GetExtension(tab.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase));
        }

        private void AddOpenTabsContext(List<string> context, OpenedTab? activeTab)
        {
            var openTabs = _openTabsProvider();
            if (openTabs.Count == 0)
            {
                return;
            }

            context.Add("[Open tabs]");
            foreach (var tab in openTabs.Take(30))
            {
                string tabName = string.IsNullOrWhiteSpace(tab.FilePath) ? tab.Title : tab.FilePath;
                string activeMarker = activeTab != null &&
                    string.Equals(tab.Id, activeTab.Id, StringComparison.Ordinal)
                        ? " (active)"
                        : string.Empty;
                context.Add($"- {tabName}{activeMarker}");
            }
        }

        private void AddActiveTabContext(
            List<string> context,
            OpenedTab? activeTab,
            bool includeActiveFile,
            bool hasSelectionRangeContext)
        {
            if (activeTab == null || !includeActiveFile || IsPdfTab(activeTab))
            {
                return;
            }

            string title = string.IsNullOrWhiteSpace(activeTab.FilePath) ? activeTab.Title : activeTab.FilePath;

            context.Add("");
            context.Add("[Active tab]");
            context.Add($"Title: {activeTab.Title}");
            context.Add($"Path: {title}");
            context.Add($"Language: {activeTab.Language ?? "plaintext"}");
            context.Add($"Dirty: {activeTab.IsDirty}");
        }

        private void AddAttachmentsContext(List<string> context, IEnumerable<AgentAttachmentState>? attachments)
        {
            var items = attachments?.ToList() ?? _attachmentController.Attachments.ToList();
            if (items.Count == 0)
            {
                return;
            }

            context.Add("");
            context.Add("[Agent attachments]");
            foreach (var attachment in items)
            {
                context.Add($"- {attachment.DisplayName} ({attachment.Detail}, approx {attachment.EstimatedTokens:N0} tokens)");
                if (attachment.IsImage)
                {
                    var image = attachment.ImageContent;
                    context.Add($"  Image input included separately for vision-capable models: {image?.MimeType}, {image?.Width}x{image?.Height}");
                }
                else if (attachment.IsPathOnlyDocument)
                {
                    context.Add($"  Path: {attachment.Path}");
                }
                else if (!string.IsNullOrEmpty(attachment.TextContent))
                {
                    context.Add("");
                    context.Add($"[Attachment file: {attachment.DisplayName}]");
                    context.Add($"Path: {attachment.Path}");
                    context.Add(attachment.TextContent);
                    context.Add("");
                }
            }
        }

        private void AddReferencedPathContext(List<string> context, string instruction, string workspaceRoot)
        {
            var mentionedPaths = ExtractMentionedPaths(instruction).Take(20).ToList();
            if (mentionedPaths.Count == 0)
            {
                return;
            }

            context.Add("[User-referenced file names]");
            context.Add("Use these exact file names and paths. Do not translate, romanize, or rename them.");
            foreach (string mentionedPath in mentionedPaths)
            {
                context.Add($"- Mentioned exactly: {mentionedPath}");
                var matches = FindWorkspacePathMatches(workspaceRoot, mentionedPath, 5).ToList();
                if (matches.Count == 0)
                {
                    context.Add("  Workspace match: not found yet; if the user asked to create/save this file, create it with exactly this name.");
                }
                else
                {
                    foreach (string match in matches)
                    {
                        context.Add($"  Workspace match: {match}");
                    }
                }
            }
            context.Add("");
        }

        private static IEnumerable<string> ExtractMentionedPaths(string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string extensions = "csv|md|txt|json|xml|html|htm|css|js|ts|tsx|jsx|cs|xaml|py|rs|java|kt|cpp|c|h|hpp|sql|xlsx|xls|docx|pptx|pdf|png|jpg|jpeg|webp|gif|bmp";
            string pattern = $@"(?<path>[^\s""'<>|:*?\r\n]+?\.(?:{extensions}))(?=$|[\s""'<>|,.;:!?()\[\]{{}}]|[가-힣])";
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                instruction,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string path = match.Groups["path"].Value
                    .Trim()
                    .Trim('.', ',', ';', ':', '!', '?', ')', ']', '}');

                if (string.IsNullOrWhiteSpace(path) ||
                    path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private IEnumerable<string> FindWorkspacePathMatches(string root, string mentionedPath, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            string normalizedMention = mentionedPath.Replace('\\', '/');
            string mentionedFileName = Path.GetFileName(mentionedPath);
            int count = 0;

            foreach (string filePath in EnumerateWorkspaceFiles(root))
            {
                string relative = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                bool isMatch = string.Equals(relative, normalizedMention, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Path.GetFileName(filePath), mentionedFileName, StringComparison.OrdinalIgnoreCase);
                if (!isMatch)
                {
                    continue;
                }

                yield return relative;
                count++;
                if (count >= maxResults)
                {
                    yield break;
                }
            }
        }

        private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
            var excludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vs", "bin", "obj", "node_modules", ".next", "dist", "build"
            };

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<string> files;
                IEnumerable<string> subdirs;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                    subdirs = Directory.EnumerateDirectories(dir);
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
                    if (!excludedDirectoryNames.Contains(Path.GetFileName(subdir)))
                    {
                        pending.Push(subdir);
                    }
                }
            }
        }
    }
}
