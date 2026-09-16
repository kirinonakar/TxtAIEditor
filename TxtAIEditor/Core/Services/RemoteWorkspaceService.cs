using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class RemoteWorkspaceService
    {
        private readonly RemoteServerStore _serverStore;
        private readonly RemoteExplorerService _explorerService;
        private readonly WslDistributionService _wslDistributionService;
        private readonly ConcurrentDictionary<string, string> _localToRemote =
            new(StringComparer.OrdinalIgnoreCase);

        public RemoteWorkspaceService(ICredentialService credentialService)
        {
            _serverStore = new RemoteServerStore(credentialService);
            _explorerService = new RemoteExplorerService();
            _wslDistributionService = new WslDistributionService();
        }

        public RemoteConnectionSettings? ActiveConnection { get; private set; }
        public string ActiveDirectoryPath { get; private set; } = "/";
        public string ActiveRootPath { get; private set; } = "/";
        public bool IsActive => ActiveConnection != null;
        public event EventHandler<string>? FileUploaded;
        public event EventHandler<string>? DirectoryOpened;
        public string ActiveDirectoryVirtualPath => ActiveConnection == null
            ? string.Empty
            : RemotePath.Create(
                ActiveConnection.Profile.Id,
                ActiveDirectoryPath,
                isDirectory: true,
                serverName: ActiveConnection.Profile.Name);

        public bool Activate(RemoteServerProfile profile)
        {
            RemoteConnectionSettings? connection = _serverStore.GetConnection(profile);
            if (connection == null)
            {
                return false;
            }

            ActiveConnection = connection;
            ActiveRootPath = RemoteExplorerService.GetInitialPath(connection);
            ActiveDirectoryPath = ActiveRootPath;
            return true;
        }

        public void Deactivate()
        {
            ActiveConnection = null;
            ActiveDirectoryPath = "/";
            ActiveRootPath = "/";
        }

        public async Task<bool> ActivateVirtualPathAsync(string virtualPath)
        {
            if (!RemotePath.TryParse(virtualPath, out Guid serverId, out string path))
            {
                return false;
            }

            RemoteServerProfile? profile = (await _serverStore.LoadAsync())
                .FirstOrDefault(candidate => candidate.Id == serverId);
            profile ??= (await _wslDistributionService.GetInstalledProfilesAsync())
                .FirstOrDefault(candidate => candidate.Id == serverId);
            if (profile == null || !Activate(profile))
            {
                return false;
            }

            ActiveDirectoryPath = path;
            NotifyActiveDirectoryOpened();
            return true;
        }

        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListActiveDirectoryAsync(
            CancellationToken cancellationToken)
        {
            RemoteConnectionSettings connection = ActiveConnection
                ?? throw new InvalidOperationException("No remote server is active.");
            return await _explorerService.ListDirectoryAsync(
                connection,
                ActiveDirectoryPath,
                cancellationToken);
        }

        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            RemoteConnectionSettings connection = ActiveConnection
                ?? throw new InvalidOperationException("No remote server is active.");
            return await _explorerService.ListDirectoryAsync(
                connection,
                remotePath,
                cancellationToken);
        }

        private const int RemoteSearchMaxConcurrency = 8;

        /// <summary>
        /// Recursively walks the remote directory tree starting at
        /// <paramref name="rootPath"/> with bounded parallelism and reports each
        /// match through <paramref name="onMatch"/> (entry, relative directory)
        /// as soon as it is discovered.
        /// </summary>
        public async Task SearchDirectoryRecursiveAsync(
            string rootPath,
            Func<RemoteDirectoryEntry, bool> shouldDescend,
            Func<RemoteDirectoryEntry, bool> isMatch,
            Action<RemoteDirectoryEntry, string> onMatch,
            CancellationToken cancellationToken)
        {
            if (!IsActive || string.IsNullOrWhiteSpace(rootPath))
            {
                return;
            }

            string normalizedRoot = NormalizeSearchPath(rootPath);
            using var throttler = new SemaphoreSlim(
                RemoteSearchMaxConcurrency,
                RemoteSearchMaxConcurrency);
            var visitedDirectories = new ConcurrentDictionary<string, byte>(
                StringComparer.OrdinalIgnoreCase);
            visitedDirectories.TryAdd(normalizedRoot, 0);

            async Task ScanAsync(string directory, bool isRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<RemoteDirectoryEntry> entries;
                await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    entries = await ListDirectoryAsync(directory, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Unreadable subfolder: skip it and keep searching the rest.
                    if (isRoot)
                    {
                        throw;
                    }

                    return;
                }
                finally
                {
                    throttler.Release();
                }

                var childDirectories = new List<Task>();
                foreach (RemoteDirectoryEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isDirectory = entry.IsDirectory;
                    if (isDirectory && !shouldDescend(entry))
                    {
                        continue;
                    }

                    if (isMatch(entry))
                    {
                        onMatch(entry, GetRelativeDirectory(normalizedRoot, entry.FullPath));
                    }

                    if (isDirectory &&
                        visitedDirectories.TryAdd(NormalizeSearchPath(entry.FullPath), 0))
                    {
                        childDirectories.Add(ScanAsync(entry.FullPath, isRoot: false));
                    }
                }

                if (childDirectories.Count > 0)
                {
                    await Task.WhenAll(childDirectories).ConfigureAwait(false);
                }
            }

            await ScanAsync(normalizedRoot, isRoot: true).ConfigureAwait(false);
        }

        private static string NormalizeSearchPath(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                return "/";
            }

            normalized = "/" + normalized.Trim('/');
            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }

        private static string GetRelativeDirectory(string normalizedRoot, string fullPath)
        {
            string normalizedFullPath = NormalizeSearchPath(fullPath);
            int separator = normalizedFullPath.LastIndexOf('/');
            string directory = separator <= 0 ? string.Empty : normalizedFullPath[..separator];
            string rootPrefix = normalizedRoot.Length > 1 ? normalizedRoot : string.Empty;
            if (rootPrefix.Length > 0 &&
                directory.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                directory = directory[rootPrefix.Length..];
            }

            return directory.Trim('/');
        }

        public void NavigateTo(string remotePath)
        {
            ActiveDirectoryPath = remotePath;
            NotifyActiveDirectoryOpened();
        }

        public bool NavigateUp()
        {
            if (!IsActive ||
                string.Equals(ActiveDirectoryPath, ActiveRootPath, StringComparison.Ordinal))
            {
                return false;
            }

            ActiveDirectoryPath = RemoteExplorerService.GetParentPath(ActiveDirectoryPath);
            NotifyActiveDirectoryOpened();
            return true;
        }

        public void NotifyActiveDirectoryOpened()
        {
            if (IsActive)
            {
                DirectoryOpened?.Invoke(this, ActiveDirectoryVirtualPath);
            }
        }

        public async Task<string> DownloadVirtualFileAsync(
            string virtualPath,
            CancellationToken cancellationToken = default)
        {
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(virtualPath);
            RemoteDirectoryEntry entry = new()
            {
                Name = remotePath.TrimEnd('/').Split('/').LastOrDefault() ?? "remote-file",
                FullPath = remotePath,
                IsDirectory = false
            };
            string localPath = await _explorerService.DownloadFileAsync(
                connection,
                entry,
                cancellationToken);
            _localToRemote[localPath] = RemotePath.Create(
                connection.Profile.Id,
                remotePath,
                serverName: connection.Profile.Name);
            return localPath;
        }

        public async Task DownloadRemoteItemToFolderAsync(
            string virtualPath,
            bool isDirectory,
            string targetLocalDirectory,
            Action<string, int, int, double>? progressCallback,
            CancellationToken cancellationToken = default)
        {
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(virtualPath);

            string itemName = remotePath.TrimEnd('/').Split('/').LastOrDefault() ?? (isDirectory ? "remote-folder" : "remote-file");
            string targetLocalPath = Path.Combine(targetLocalDirectory, itemName);

            if (isDirectory)
            {
                await _explorerService.DownloadDirectoryToPathAsync(
                    connection,
                    remotePath,
                    targetLocalPath,
                    progressCallback,
                    cancellationToken);
            }
            else
            {
                progressCallback?.Invoke(itemName, 1, 1, 0.0);
                double lastFilePercent = 0.0;
                DirectProgress<double> progress = new(p =>
                {
                    double displayPercent = Math.Max(
                        Math.Clamp(p, 0.0, 100.0),
                        lastFilePercent);
                    lastFilePercent = displayPercent;
                    progressCallback?.Invoke(
                        itemName,
                        displayPercent >= 100.0 ? 0 : 1,
                        1,
                        displayPercent);
                });
                await _explorerService.DownloadFileToPathAsync(
                    connection,
                    remotePath,
                    targetLocalPath,
                    progress,
                    cancellationToken);
                progressCallback?.Invoke(itemName, 0, 1, 100.0);
            }
        }

        public bool TryGetVirtualPath(string? localPath, out string virtualPath)
        {
            if (!string.IsNullOrWhiteSpace(localPath) &&
                _localToRemote.TryGetValue(localPath, out string? mapped))
            {
                virtualPath = mapped;
                return true;
            }

            virtualPath = string.Empty;
            return false;
        }

        public string GetDisplayPath(string virtualPath)
        {
            if (!RemotePath.TryParse(virtualPath, out Guid serverId, out string remotePath))
            {
                return virtualPath;
            }

            string serverName = RemotePath.GetServerNameHint(virtualPath) ??
                (ActiveConnection?.Profile.Id == serverId
                ? ActiveConnection.Profile.Name
                : _serverStore.Load()
                    .FirstOrDefault(profile => profile.Id == serverId)
                    ?.Name ?? "Remote");
            return $"{serverName}:{remotePath}";
        }

        public async Task UploadLocalFileAsync(
            string localPath,
            string virtualPath,
            CancellationToken cancellationToken = default)
        {
            await UploadLocalFileAsync(
                localPath,
                virtualPath,
                progressCallback: null,
                cancellationToken);
        }

        public async Task UploadLocalFileAsync(
            string localPath,
            string virtualPath,
            Action<double>? progressCallback,
            CancellationToken cancellationToken = default)
        {
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(virtualPath);
            DirectProgress<double>? progress = progressCallback == null
                ? null
                : new DirectProgress<double>(progressCallback);
            await _explorerService.UploadFileAsync(
                connection,
                localPath,
                remotePath,
                progress,
                cancellationToken);
            _localToRemote[localPath] = virtualPath;
            FileUploaded?.Invoke(this, virtualPath);
        }

        public async Task CreateDirectoryAsync(
            string parentVirtualPath,
            string name,
            CancellationToken cancellationToken = default)
        {
            string target = RemotePath.Combine(parentVirtualPath, name, isDirectory: true);
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(target);
            await _explorerService.CreateDirectoryAsync(connection, remotePath, cancellationToken);
        }

        public async Task<string> CreateFileAsync(
            string parentVirtualPath,
            string name,
            CancellationToken cancellationToken = default)
        {
            string target = RemotePath.Combine(parentVirtualPath, name);
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(target);
            await _explorerService.CreateFileAsync(connection, remotePath, cancellationToken);
            return target;
        }

        public async Task RenameAsync(
            string virtualPath,
            string newName,
            bool isDirectory,
            CancellationToken cancellationToken = default)
        {
            string parent = RemotePath.GetParent(virtualPath);
            string destination = RemotePath.Combine(parent, newName, isDirectory);
            (RemoteConnectionSettings connection, string sourcePath) =
                await ResolveConnectionAsync(virtualPath);
            if (!RemotePath.TryParse(destination, out _, out string destinationPath))
            {
                throw new InvalidOperationException("The remote destination path is invalid.");
            }

            await _explorerService.RenameAsync(
                connection,
                sourcePath,
                destinationPath,
                isDirectory,
                cancellationToken);
        }

        public async Task DeleteAsync(
            string virtualPath,
            bool isDirectory,
            CancellationToken cancellationToken = default)
        {
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(virtualPath);
            await _explorerService.DeleteAsync(
                connection,
                remotePath,
                isDirectory,
                cancellationToken);
        }

        private async Task<(RemoteConnectionSettings Connection, string RemotePath)> ResolveConnectionAsync(
            string virtualPath)
        {
            if (!RemotePath.TryParse(virtualPath, out Guid serverId, out string remotePath))
            {
                throw new InvalidOperationException("The remote path is invalid.");
            }

            if (ActiveConnection?.Profile.Id == serverId)
            {
                return (ActiveConnection, remotePath);
            }

            RemoteServerProfile? profile = (await _serverStore.LoadAsync())
                .FirstOrDefault(candidate => candidate.Id == serverId);
            profile ??= (await _wslDistributionService.GetInstalledProfilesAsync())
                .FirstOrDefault(candidate => candidate.Id == serverId);
            RemoteConnectionSettings? connection = profile == null
                ? null
                : _serverStore.GetConnection(profile);
            return connection == null
                ? throw new InvalidOperationException("The remote server credentials are unavailable.")
                : (connection, remotePath);
        }

        private sealed class DirectProgress<T> : IProgress<T>
        {
            private readonly Action<T> _callback;

            public DirectProgress(Action<T> callback)
            {
                _callback = callback;
            }

            public void Report(T value)
            {
                _callback(value);
            }
        }
    }
}
