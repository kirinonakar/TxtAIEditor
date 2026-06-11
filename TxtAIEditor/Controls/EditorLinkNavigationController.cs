using System;
using System.IO;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed class EditorLinkNavigationController
    {
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;

        public EditorLinkNavigationController(
            Func<OpenedTab?> activeTabProvider,
            Func<string, Task> navigateExplorerToFolderAsync)
        {
            _activeTabProvider = activeTabProvider;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
        }

        public async Task HandleCtrlClickAsync(string text, bool isUrl, bool isPath)
        {
            if (isUrl)
            {
                await LaunchUrlAsync(text);
                return;
            }

            if (isPath)
            {
                await NavigateToPathAsync(text);
            }
        }

        private static async Task LaunchUrlAsync(string text)
        {
            try
            {
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    _ = await Windows.System.Launcher.LaunchUriAsync(uri);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open URL in browser: {ex.Message}");
            }
        }

        private async Task NavigateToPathAsync(string text)
        {
            try
            {
                string resolvedPath = ResolvePath(text);
                if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
                {
                    string targetFolder = File.Exists(resolvedPath)
                        ? Path.GetDirectoryName(resolvedPath) ?? resolvedPath
                        : resolvedPath;

                    if (Directory.Exists(targetFolder))
                    {
                        await _navigateExplorerToFolderAsync(targetFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to resolve and open path: {ex.Message}");
            }
        }

        private string ResolvePath(string text)
        {
            string resolvedPath = text;
            var activeTab = _activeTabProvider();
            if (!Path.IsPathRooted(resolvedPath) &&
                activeTab != null &&
                !string.IsNullOrEmpty(activeTab.FilePath))
            {
                string? currentDir = Path.GetDirectoryName(activeTab.FilePath);
                if (!string.IsNullOrEmpty(currentDir))
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(currentDir, resolvedPath));
                }
            }

            return resolvedPath;
        }
    }
}
