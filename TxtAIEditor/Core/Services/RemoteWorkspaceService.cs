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
        private readonly ConcurrentDictionary<string, string> _localToRemote =
            new(StringComparer.OrdinalIgnoreCase);

        public RemoteWorkspaceService(ICredentialService credentialService)
        {
            _serverStore = new RemoteServerStore(credentialService);
            _explorerService = new RemoteExplorerService();
        }

        public RemoteConnectionSettings? ActiveConnection { get; private set; }
        public string ActiveDirectoryPath { get; private set; } = "/";
        public string ActiveRootPath { get; private set; } = "/";
        public bool IsActive => ActiveConnection != null;
        public event EventHandler<string>? FileUploaded;
        public string ActiveDirectoryVirtualPath => ActiveConnection == null
            ? string.Empty
            : RemotePath.Create(ActiveConnection.Profile.Id, ActiveDirectoryPath, isDirectory: true);

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
            if (profile == null || !Activate(profile))
            {
                return false;
            }

            ActiveDirectoryPath = path;
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

        public void NavigateTo(string remotePath)
        {
            ActiveDirectoryPath = remotePath;
        }

        public bool NavigateUp()
        {
            if (!IsActive ||
                string.Equals(ActiveDirectoryPath, ActiveRootPath, StringComparison.Ordinal))
            {
                return false;
            }

            ActiveDirectoryPath = RemoteExplorerService.GetParentPath(ActiveDirectoryPath);
            return true;
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
            _localToRemote[localPath] = virtualPath;
            return localPath;
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

            string serverName = ActiveConnection?.Profile.Id == serverId
                ? ActiveConnection.Profile.Name
                : _serverStore.Load()
                    .FirstOrDefault(profile => profile.Id == serverId)
                    ?.Name ?? "Remote";
            return $"{serverName}:{remotePath}";
        }

        public async Task UploadLocalFileAsync(
            string localPath,
            string virtualPath,
            CancellationToken cancellationToken = default)
        {
            (RemoteConnectionSettings connection, string remotePath) =
                await ResolveConnectionAsync(virtualPath);
            await _explorerService.UploadFileAsync(
                connection,
                localPath,
                remotePath,
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
            RemoteConnectionSettings? connection = profile == null
                ? null
                : _serverStore.GetConnection(profile);
            return connection == null
                ? throw new InvalidOperationException("The remote server credentials are unavailable.")
                : (connection, remotePath);
        }
    }
}
