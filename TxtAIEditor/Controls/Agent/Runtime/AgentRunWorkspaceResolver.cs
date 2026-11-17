using System;
using System.IO;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunWorkspaceResolver
    {
        private readonly Func<string> _fallbackWorkspaceRootProvider;

        public AgentRunWorkspaceResolver(Func<string> fallbackWorkspaceRootProvider)
        {
            _fallbackWorkspaceRootProvider = fallbackWorkspaceRootProvider;
        }

        public string Resolve(
            string preservedWorkspaceRoot,
            string capturedWorkspaceRoot,
            string userInstruction)
        {
            if (IsApprovedPlanExecutionPrompt(userInstruction) &&
                !string.IsNullOrWhiteSpace(preservedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(preservedWorkspaceRoot);
            }

            if (IsExistingDirectory(capturedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(capturedWorkspaceRoot);
            }

            if (IsExistingDirectory(preservedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(preservedWorkspaceRoot);
            }

            return _fallbackWorkspaceRootProvider();
        }

        public static void VerifyPinnedWorkspace(AgentRunContext? context)
        {
            if (context?.PinnedWorkspaceRoot is string pinned)
            {
                AgentWorkspaceFileResolver.VerifyPinnedWorkspace(context.WorkspaceRoot, pinned);
            }
        }

        internal static bool IsApprovedPlanExecutionPrompt(string userInstruction)
        {
            return (userInstruction ?? string.Empty)
                .TrimStart()
                .StartsWith("[Approved plan execution]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExistingDirectory(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
