using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentPluginGitHubSource(
        string Owner,
        string Repository,
        string Reference,
        string Subdirectory)
    {
        public string SourceKey => $"github:{Owner}/{Repository}".ToLowerInvariant();
        public string DisplayName => $"{Owner}/{Repository}";
    }

    internal sealed record AgentPluginGitHubInstallResult(
        string SourceKey,
        string DisplayName,
        string RepositoryRoot,
        IReadOnlyList<string> PluginRoots,
        int SkillCount);

    internal static partial class AgentPluginGitHubInstaller
    {
        private const long MaxArchiveBytes = 128L * 1024 * 1024;
        private const long MaxExtractedBytes = 768L * 1024 * 1024;
        private const int MaxArchiveEntries = 50000;
        private const int MaxPluginManifests = 100;

        private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "api.github.com",
            "codeload.github.com",
            "github.com"
        };

        private static readonly HttpClient HttpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        public static AgentPluginGitHubSource ParseSource(string input)
        {
            input = input?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidDataException("Enter an HTTPS GitHub repository URL.");
            }

            string[] segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            if (segments.Length < 2)
            {
                throw new InvalidDataException("The GitHub URL must include an owner and repository.");
            }

            string owner = segments[0];
            string repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? segments[1].Substring(0, segments[1].Length - 4)
                : segments[1];
            if (!GitHubNameRegex().IsMatch(owner) || !GitHubNameRegex().IsMatch(repository))
            {
                throw new InvalidDataException("The GitHub owner or repository name is invalid.");
            }

            string reference = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            string subdirectory = string.Empty;
            if (segments.Length > 2)
            {
                if (!segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase) || segments.Length < 4)
                {
                    throw new InvalidDataException("Use a repository URL or a GitHub '/tree/<ref>/<path>' URL.");
                }

                reference = segments[3];
                subdirectory = string.Join(Path.DirectorySeparatorChar, segments.Skip(4));
            }

            if (reference.Contains('/') || reference.Contains('\\') || reference.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The GitHub ref is invalid. Use a tag or branch name without '/'.");
            }

            return new AgentPluginGitHubSource(owner, repository, reference, subdirectory);
        }

        public static async Task<AgentPluginGitHubInstallResult> InstallAsync(
            string input,
            string installBaseDirectory,
            Action<string>? beforeReplace,
            CancellationToken cancellationToken)
        {
            AgentPluginGitHubSource source = ParseSource(input);
            string installBase = Path.GetFullPath(installBaseDirectory);
            Directory.CreateDirectory(installBase);

            string stagingDirectory = Path.Combine(
                installBase,
                $".{source.Repository}.staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                string archivePath = Path.Combine(stagingDirectory, "repository.zip");
                await DownloadArchiveAsync(source, archivePath, cancellationToken);

                string extractionDirectory = Path.Combine(stagingDirectory, "extracted");
                Directory.CreateDirectory(extractionDirectory);
                await ExtractArchiveAsync(archivePath, extractionDirectory, cancellationToken);

                string extractedRepositoryRoot = FindExtractedRepositoryRoot(extractionDirectory);
                string targetRoot = ResolveTargetRoot(extractedRepositoryRoot, source.Subdirectory);
                IReadOnlyList<string> pluginRoots = DiscoverPluginRoots(extractedRepositoryRoot, targetRoot);
                if (pluginRoots.Count == 0)
                {
                    throw new InvalidDataException("No installable plugin.json was found in the GitHub repository.");
                }

                string validationDataDirectory = Path.Combine(stagingDirectory, "validation-data");
                foreach (string pluginRoot in pluginRoots)
                {
                    AgentPluginLoader.Load(pluginRoot, validationDataDirectory);
                }

                var pluginRelativePaths = pluginRoots
                    .Select(path => Path.GetRelativePath(extractedRepositoryRoot, path))
                    .ToList();
                int skillCount = CountSkills(pluginRoots);

                string ownerDirectory = Path.Combine(installBase, source.Owner);
                Directory.CreateDirectory(ownerDirectory);
                string destination = Path.Combine(ownerDirectory, source.Repository);
                string backup = Path.Combine(ownerDirectory, $".{source.Repository}.backup-{Guid.NewGuid():N}");
                EnsureContained(installBase, destination, "install destination");
                EnsureContained(installBase, backup, "install backup");

                beforeReplace?.Invoke(source.SourceKey);
                bool movedExisting = false;
                try
                {
                    if (Directory.Exists(destination))
                    {
                        Directory.Move(destination, backup);
                        movedExisting = true;
                    }

                    Directory.Move(extractedRepositoryRoot, destination);
                }
                catch
                {
                    if (!Directory.Exists(destination) && movedExisting && Directory.Exists(backup))
                    {
                        Directory.Move(backup, destination);
                    }

                    throw;
                }

                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }

                var installedPluginRoots = pluginRelativePaths
                    .Select(relative => Path.GetFullPath(Path.Combine(destination, relative)))
                    .ToList();
                return new AgentPluginGitHubInstallResult(
                    source.SourceKey,
                    source.DisplayName,
                    destination,
                    installedPluginRoots,
                    skillCount);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }

        private static async Task DownloadArchiveAsync(
            AgentPluginGitHubSource source,
            string archivePath,
            CancellationToken cancellationToken)
        {
            string repositoryPath = $"{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repository)}";
            string url = $"https://api.github.com/repos/{repositoryPath}/zipball";
            if (!string.IsNullOrWhiteSpace(source.Reference))
            {
                url += "/" + Uri.EscapeDataString(source.Reference);
            }

            Uri current = new(url);
            for (int redirectCount = 0; redirectCount <= 5; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TxtAIEditor", "1.0"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using HttpResponseMessage response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    Uri? next = response.Headers.Location;
                    if (next == null)
                    {
                        throw new HttpRequestException("GitHub returned a redirect without a destination.");
                    }

                    next = next.IsAbsoluteUri ? next : new Uri(current, next);
                    ValidateDownloadUri(next);
                    current = next;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                {
                    throw new InvalidDataException("The GitHub repository archive is too large to install.");
                }

                await using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await CopyWithLimitAsync(sourceStream, destination, MaxArchiveBytes, cancellationToken);
                return;
            }

            throw new HttpRequestException("GitHub returned too many redirects.");
        }

        internal static async Task ExtractArchiveAsync(
            string archivePath,
            string extractionDirectory,
            CancellationToken cancellationToken)
        {
            string extractionRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory));
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new InvalidDataException("The GitHub repository archive contains too many files.");
            }

            long totalExtracted = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (normalizedName.Split(
                    Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.Contains(':')))
                {
                    throw new InvalidDataException("The GitHub archive contains an invalid Windows path.");
                }
                string destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, normalizedName));
                EnsureContained(extractionRoot, destinationPath, "archive entry");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                totalExtracted = checked(totalExtracted + entry.Length);
                if (totalExtracted > MaxExtractedBytes)
                {
                    throw new InvalidDataException("The extracted GitHub repository is too large to install.");
                }

                string? parent = Path.GetDirectoryName(destinationPath);
                if (parent != null)
                {
                    Directory.CreateDirectory(parent);
                }

                await using Stream entryStream = entry.Open();
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await CopyWithLimitAsync(entryStream, output, entry.Length, cancellationToken);
            }
        }

        internal static string FindExtractedRepositoryRoot(string extractionDirectory)
        {
            string[] directories = Directory.GetDirectories(extractionDirectory);
            string[] files = Directory.GetFiles(extractionDirectory);
            if (directories.Length == 1 && files.Length == 0)
            {
                return Path.GetFullPath(directories[0]);
            }

            return Path.GetFullPath(extractionDirectory);
        }

        private static string ResolveTargetRoot(string repositoryRoot, string subdirectory)
        {
            if (string.IsNullOrWhiteSpace(subdirectory))
            {
                return repositoryRoot;
            }

            string target = Path.GetFullPath(Path.Combine(repositoryRoot, subdirectory));
            EnsureContained(repositoryRoot, target, "GitHub tree path");
            if (!Directory.Exists(target))
            {
                throw new DirectoryNotFoundException("The directory from the GitHub tree URL was not found in the downloaded repository.");
            }

            return target;
        }

        internal static IReadOnlyList<string> DiscoverPluginRoots(string repositoryRoot, string targetRoot)
        {
            string directManifest = Path.Combine(targetRoot, "plugin.json");
            if (File.Exists(directManifest))
            {
                return new[] { targetRoot };
            }

            foreach (string relativeMarketplacePath in new[]
            {
                Path.Combine(".agents", "plugins", "marketplace.json"),
                Path.Combine(".github", "plugin", "marketplace.json"),
                Path.Combine(".claude-plugin", "marketplace.json")
            })
            {
                string marketplacePath = Path.Combine(targetRoot, relativeMarketplacePath);
                if (!File.Exists(marketplacePath))
                {
                    continue;
                }

                IReadOnlyList<string> marketplaceRoots = ReadMarketplacePluginRoots(
                    repositoryRoot,
                    marketplacePath);
                if (marketplaceRoots.Count > 0)
                {
                    return marketplaceRoots;
                }
            }

            return Directory.EnumerateFiles(targetRoot, "plugin.json", SearchOption.AllDirectories)
                .Take(MaxPluginManifests + 1)
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => path != null)
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxPluginManifests)
                .ToList();
        }

        private static IReadOnlyList<string> ReadMarketplacePluginRoots(
            string repositoryRoot,
            string marketplacePath)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marketplacePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("plugins", out JsonElement plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The plugin marketplace manifest has no valid 'plugins' array.");
            }

            var roots = new List<string>();
            foreach (JsonElement plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind != JsonValueKind.Object ||
                    !plugin.TryGetProperty("source", out JsonElement source))
                {
                    continue;
                }

                string path = source.ValueKind == JsonValueKind.String
                    ? source.GetString() ?? string.Empty
                    : source.ValueKind == JsonValueKind.Object &&
                        source.TryGetProperty("path", out JsonElement pathElement) &&
                        pathElement.ValueKind == JsonValueKind.String
                            ? pathElement.GetString() ?? string.Empty
                            : string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                path = path.Replace('/', Path.DirectorySeparatorChar).Trim();
                string candidate = Path.GetFullPath(Path.Combine(repositoryRoot, path));
                EnsureContained(repositoryRoot, candidate, "marketplace plugin path");
                if (File.Exists(Path.Combine(candidate, "plugin.json")))
                {
                    roots.Add(candidate);
                }
            }

            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int CountSkills(IEnumerable<string> pluginRoots)
        {
            var skillFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pluginRoot in pluginRoots)
            {
                string skillsDirectory = Path.Combine(pluginRoot, "skills");
                if (!Directory.Exists(skillsDirectory))
                {
                    continue;
                }

                foreach (string skillDirectory in Directory.EnumerateDirectories(skillsDirectory))
                {
                    string skillFile = Path.Combine(skillDirectory, "SKILL.md");
                    if (File.Exists(skillFile))
                    {
                        skillFiles.Add(Path.GetFullPath(skillFile));
                    }
                }
            }

            return skillFiles.Count;
        }

        private static async Task CopyWithLimitAsync(
            Stream source,
            Stream destination,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[81920];
            long copied = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                copied = checked(copied + read);
                if (copied > maxBytes)
                {
                    throw new InvalidDataException("The downloaded or extracted file exceeded its size limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code is >= 300 and <= 399;
        }

        private static void ValidateDownloadUri(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !AllowedDownloadHosts.Contains(uri.Host))
            {
                throw new InvalidDataException("GitHub redirected the download to an untrusted address.");
            }
        }

        private static void EnsureContained(string root, string candidate, string label)
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string normalizedCandidate = Path.GetFullPath(candidate);
            if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !normalizedCandidate.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{label} resolves outside its permitted directory.");
            }
        }

        [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
        private static partial Regex GitHubNameRegex();
    }
}
