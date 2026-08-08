using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentPluginArchiveInstalledPlugin(
        string SourceKey,
        string PluginRoot);

    internal sealed record AgentPluginArchiveInstallResult(
        string DisplayName,
        IReadOnlyList<AgentPluginArchiveInstalledPlugin> Plugins);

    internal static class AgentPluginArchiveInstaller
    {
        private const long MaxArchiveBytes = 128L * 1024 * 1024;

        public static async Task<AgentPluginArchiveInstallResult> InstallAsync(
            string archivePath,
            string installBaseDirectory,
            string pluginDataBaseDirectory,
            Action<string>? beforeReplace,
            CancellationToken cancellationToken)
        {
            string archive = Path.GetFullPath(archivePath);
            if (!File.Exists(archive) ||
                !Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Select a valid ZIP archive.");
            }

            if (new FileInfo(archive).Length > MaxArchiveBytes)
            {
                throw new InvalidDataException("The plugin ZIP archive is too large to install.");
            }

            string installBase = Path.GetFullPath(installBaseDirectory);
            Directory.CreateDirectory(installBase);
            string stagingDirectory = Path.Combine(
                installBase,
                $".archive.staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                string extractionDirectory = Path.Combine(stagingDirectory, "extracted");
                Directory.CreateDirectory(extractionDirectory);
                await AgentPluginGitHubInstaller.ExtractArchiveAsync(
                    archive,
                    extractionDirectory,
                    cancellationToken);

                string repositoryRoot = AgentPluginGitHubInstaller.FindExtractedRepositoryRoot(
                    extractionDirectory);
                IReadOnlyList<string> pluginRoots = AgentPluginGitHubInstaller.DiscoverPluginRoots(
                    repositoryRoot,
                    repositoryRoot);
                if (pluginRoots.Count == 0)
                {
                    throw new InvalidDataException("No installable plugin.json was found in the ZIP archive.");
                }

                string validationDataDirectory = Path.Combine(stagingDirectory, "validation-data");
                var plugins = pluginRoots
                    .Select(root => new
                    {
                        Root = root,
                        Result = AgentPluginLoader.Load(root, validationDataDirectory)
                    })
                    .ToList();
                if (plugins
                    .GroupBy(plugin => plugin.Result.PluginName, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
                {
                    throw new InvalidDataException("The ZIP archive contains duplicate plugin names.");
                }

                var installed = new List<AgentPluginArchiveInstalledPlugin>();
                foreach (var plugin in plugins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sourceKey = "local:" + plugin.Result.PluginName.ToLowerInvariant();
                    beforeReplace?.Invoke(sourceKey);
                    string installedRoot = AgentPluginLocalInstaller.Install(
                        plugin.Root,
                        installBase,
                        plugin.Result.PluginName);
                    installed.Add(new AgentPluginArchiveInstalledPlugin(sourceKey, installedRoot));
                }

                return new AgentPluginArchiveInstallResult(
                    Path.GetFileNameWithoutExtension(archive),
                    installed);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
    }
}
