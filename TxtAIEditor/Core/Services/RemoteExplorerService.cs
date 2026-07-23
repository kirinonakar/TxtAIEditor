using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentFTP;
using Renci.SshNet;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class RemoteExplorerService
    {
        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            return connection.Profile.ServerType switch
            {
                RemoteServerType.Ssh =>
                    await ListSshAsync(connection, path, cancellationToken),
                RemoteServerType.Sftp =>
                    await ListSftpAsync(connection, path, cancellationToken),
                RemoteServerType.Ftps =>
                    await ListFtpsAsync(connection, path, cancellationToken),
                RemoteServerType.WebDav =>
                    await ListWebDavAsync(connection, path, cancellationToken),
                RemoteServerType.Wsl =>
                    await ListWslAsync(connection, path, cancellationToken),
                _ => Array.Empty<RemoteDirectoryEntry>()
            };
        }

        public async Task<string> DownloadFileAsync(
            RemoteConnectionSettings connection,
            RemoteDirectoryEntry entry,
            CancellationToken cancellationToken)
        {
            string localPath = CreateCachePath(connection.Profile.Id, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    await DownloadScpAsync(connection, entry.FullPath, localPath, cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await DownloadSftpAsync(connection, entry.FullPath, localPath, cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await DownloadFtpsAsync(connection, entry.FullPath, localPath, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    await DownloadWebDavAsync(connection, entry.FullPath, localPath, cancellationToken);
                    break;
                case RemoteServerType.Wsl:
                    await CopyWslFileAsync(
                        GetWslFileSystemPath(connection, entry.FullPath),
                        localPath,
                        overwrite: true,
                        cancellationToken);
                    break;
            }

            return localPath;
        }

        public async Task DownloadFileToPathAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    await DownloadScpWithProgressAsync(connection, remotePath, localPath, progress, cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await DownloadSftpWithProgressAsync(connection, remotePath, localPath, progress, cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await DownloadFtpsWithProgressAsync(connection, remotePath, localPath, progress, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    await DownloadWebDavWithProgressAsync(connection, remotePath, localPath, progress, cancellationToken);
                    break;
                case RemoteServerType.Wsl:
                    await CopyWslFileWithProgressAsync(
                        GetWslFileSystemPath(connection, remotePath),
                        localPath,
                        progress,
                        cancellationToken);
                    break;
            }
        }

        public async Task DownloadDirectoryToPathAsync(
            RemoteConnectionSettings connection,
            string remoteFolderPath,
            string targetLocalFolderPath,
            Action<string, double>? progressCallback,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetLocalFolderPath);

            var filesToDownload = new List<(string RemotePath, string LocalPath)>();
            var foldersToCreate = new List<string>();

            async Task ScanDirectoryAsync(string currentRemoteDir, string currentLocalDir)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<RemoteDirectoryEntry> entries =
                    await ListDirectoryAsync(connection, currentRemoteDir, cancellationToken);

                foreach (RemoteDirectoryEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string subLocal = Path.Combine(currentLocalDir, entry.Name);
                    if (entry.IsDirectory)
                    {
                        foldersToCreate.Add(subLocal);
                        await ScanDirectoryAsync(entry.FullPath, subLocal);
                    }
                    else
                    {
                        filesToDownload.Add((entry.FullPath, subLocal));
                    }
                }
            }

            await ScanDirectoryAsync(remoteFolderPath, targetLocalFolderPath);

            foreach (string folder in foldersToCreate)
            {
                Directory.CreateDirectory(folder);
            }

            int totalFiles = filesToDownload.Count;
            if (totalFiles == 0)
            {
                progressCallback?.Invoke(Path.GetFileName(targetLocalFolderPath), 100.0);
                return;
            }

            for (int i = 0; i < totalFiles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (remFile, locFile) = filesToDownload[i];
                string fileName = Path.GetFileName(remFile);
                double fileStartPercent = (double)i / totalFiles * 100.0;
                progressCallback?.Invoke($"{fileName} ({i + 1}/{totalFiles})", fileStartPercent);

                Progress<double> fileProgress = new(p =>
                {
                    double filePortion = p / totalFiles;
                    double currentOverall = fileStartPercent + filePortion;
                    progressCallback?.Invoke($"{fileName} ({i + 1}/{totalFiles})", Math.Min(99.9, currentOverall));
                });

                await DownloadFileToPathAsync(connection, remFile, locFile, fileProgress, cancellationToken);
            }

            progressCallback?.Invoke(Path.GetFileName(targetLocalFolderPath), 100.0);
        }

        public async Task UploadFileAsync(
            RemoteConnectionSettings connection,
            string localPath,
            string remotePath,
            CancellationToken cancellationToken)
        {
            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    await UploadScpAsync(connection, localPath, remotePath, cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await RunSftpAsync(connection, client =>
                    {
                        using FileStream input = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        client.UploadFile(input, remotePath, canOverride: true);
                    }, cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await RunFtpsAsync(connection, client =>
                    {
                        FtpStatus status = client.UploadFile(
                            localPath,
                            remotePath,
                            FtpRemoteExists.Overwrite,
                            createRemoteDir: false,
                            FtpVerify.None);
                        if (status != FtpStatus.Success)
                        {
                            throw new IOException($"FTPS upload failed: {status}");
                        }
                    }, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    using (HttpClient client = CreateWebDavClient(connection))
                    await using (FileStream input = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (HttpResponseMessage response = await client.PutAsync(
                        BuildWebDavUri(connection, remotePath),
                        new StreamContent(input),
                        cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case RemoteServerType.Wsl:
                    await CopyWslFileAsync(
                        localPath,
                        GetWslFileSystemPath(connection, remotePath),
                        overwrite: true,
                        cancellationToken);
                    break;
            }
        }

        public async Task CreateDirectoryAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            CancellationToken cancellationToken)
        {
            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    await RunSshCommandAsync(
                        connection,
                        $"mkdir -- {QuoteShellArgument(NormalizeRemotePath(remotePath))}",
                        cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await RunSftpAsync(connection, client => client.CreateDirectory(remotePath), cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await RunFtpsAsync(connection, client => client.CreateDirectory(remotePath), cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    using (HttpClient client = CreateWebDavClient(connection))
                    using (HttpRequestMessage request = new(new HttpMethod("MKCOL"), BuildWebDavUri(connection, remotePath)))
                    using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case RemoteServerType.Wsl:
                    await Task.Run(
                        () => Directory.CreateDirectory(GetWslFileSystemPath(connection, remotePath)),
                        cancellationToken);
                    break;
            }
        }

        public async Task CreateFileAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            CancellationToken cancellationToken)
        {
            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    string createPath = QuoteShellArgument(NormalizeRemotePath(remotePath));
                    await RunSshCommandAsync(
                        connection,
                        $"if [ -e {createPath} ]; then echo 'A remote item with the same name already exists.' >&2; exit 1; fi; : > {createPath}",
                        cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await RunSftpAsync(connection, client =>
                    {
                        if (client.Exists(remotePath))
                        {
                            throw new IOException("A remote item with the same name already exists.");
                        }

                        using Stream _ = client.Create(remotePath);
                    }, cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await RunFtpsAsync(connection, client =>
                    {
                        FtpStatus status = client.UploadBytes(
                            Array.Empty<byte>(),
                            remotePath,
                            FtpRemoteExists.Skip,
                            createRemoteDir: false);
                        if (status != FtpStatus.Success)
                        {
                            throw new IOException("A remote item with the same name already exists.");
                        }
                    }, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    using (HttpClient client = CreateWebDavClient(connection))
                    using (HttpRequestMessage request = new(HttpMethod.Put, BuildWebDavUri(connection, remotePath)))
                    {
                        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                        request.Content = new ByteArrayContent(Array.Empty<byte>());
                        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case RemoteServerType.Wsl:
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using FileStream _ = new(
                            GetWslFileSystemPath(connection, remotePath),
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None);
                    }, cancellationToken);
                    break;
            }
        }

        public async Task RenameAsync(
            RemoteConnectionSettings connection,
            string sourcePath,
            string destinationPath,
            bool isDirectory,
            CancellationToken cancellationToken)
        {
            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    string source = QuoteShellArgument(NormalizeRemotePath(sourcePath));
                    string destination = QuoteShellArgument(NormalizeRemotePath(destinationPath));
                    await RunSshCommandAsync(
                        connection,
                        $"if [ -e {destination} ]; then echo 'A remote item with the same name already exists.' >&2; exit 1; fi; mv -- {source} {destination}",
                        cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await RunSftpAsync(
                        connection,
                        client => client.RenameFile(sourcePath, destinationPath),
                        cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await RunFtpsAsync(connection, client =>
                    {
                        bool moved = isDirectory
                            ? client.MoveDirectory(sourcePath, destinationPath, FtpRemoteExists.NoCheck)
                            : client.MoveFile(sourcePath, destinationPath, FtpRemoteExists.NoCheck);
                        if (!moved)
                        {
                            throw new IOException("FTPS rename failed.");
                        }
                    }, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    using (HttpClient client = CreateWebDavClient(connection))
                    using (HttpRequestMessage request = new(new HttpMethod("MOVE"), BuildWebDavUri(connection, sourcePath)))
                    {
                        request.Headers.TryAddWithoutValidation(
                            "Destination",
                            BuildWebDavUri(connection, destinationPath).AbsoluteUri);
                        request.Headers.TryAddWithoutValidation("Overwrite", "F");
                        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case RemoteServerType.Wsl:
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string source = GetWslFileSystemPath(connection, sourcePath);
                        string destination = GetWslFileSystemPath(connection, destinationPath);
                        if (isDirectory)
                        {
                            Directory.Move(source, destination);
                        }
                        else
                        {
                            File.Move(source, destination);
                        }
                    }, cancellationToken);
                    break;
            }
        }

        public async Task DeleteAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            bool isDirectory,
            CancellationToken cancellationToken)
        {
            switch (connection.Profile.ServerType)
            {
                case RemoteServerType.Ssh:
                    await RunSshCommandAsync(
                        connection,
                        $"{(isDirectory ? "rm -rf" : "rm -f")} -- {QuoteShellArgument(NormalizeRemotePath(remotePath))}",
                        cancellationToken);
                    break;
                case RemoteServerType.Sftp:
                    await RunSftpAsync(connection, client =>
                    {
                        if (isDirectory)
                        {
                            DeleteSftpDirectoryRecursive(client, remotePath);
                        }
                        else
                        {
                            client.DeleteFile(remotePath);
                        }
                    }, cancellationToken);
                    break;
                case RemoteServerType.Ftps:
                    await RunFtpsAsync(connection, client =>
                    {
                        if (isDirectory)
                        {
                            client.DeleteDirectory(remotePath);
                        }
                        else
                        {
                            client.DeleteFile(remotePath);
                        }
                    }, cancellationToken);
                    break;
                case RemoteServerType.WebDav:
                    using (HttpClient client = CreateWebDavClient(connection))
                    using (HttpRequestMessage request = new(HttpMethod.Delete, BuildWebDavUri(connection, remotePath)))
                    using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case RemoteServerType.Wsl:
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path = GetWslFileSystemPath(connection, remotePath);
                        if (isDirectory)
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else
                        {
                            File.Delete(path);
                        }
                    }, cancellationToken);
                    break;
            }
        }

        private static void DeleteSftpDirectoryRecursive(SftpClient client, string path)
        {
            foreach (var item in client.ListDirectory(path).Where(item => item.Name is not "." and not ".."))
            {
                if (item.IsDirectory && !item.IsSymbolicLink)
                {
                    DeleteSftpDirectoryRecursive(client, item.FullName);
                }
                else
                {
                    client.DeleteFile(item.FullName);
                }
            }

            client.DeleteDirectory(path);
        }

        public static string GetInitialPath(RemoteConnectionSettings connection)
        {
            if (connection.Profile.ServerType == RemoteServerType.Wsl)
            {
                return NormalizeRemotePath(connection.Profile.UserName);
            }

            if (Uri.TryCreate(connection.Address, UriKind.Absolute, out Uri? uri))
            {
                return string.IsNullOrWhiteSpace(uri.AbsolutePath)
                    ? "/"
                    : Uri.UnescapeDataString(uri.AbsolutePath);
            }

            return "/";
        }

        public static string GetParentPath(string path)
        {
            string normalized = NormalizeRemotePath(path);
            if (normalized == "/")
            {
                return "/";
            }

            int separator = normalized.LastIndexOf('/');
            return separator <= 0 ? "/" : normalized[..separator];
        }

        private static async Task<IReadOnlyList<RemoteDirectoryEntry>> ListWslAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo directory = new(GetWslFileSystemPath(connection, path));
                return (IReadOnlyList<RemoteDirectoryEntry>)directory
                    .EnumerateFileSystemInfos()
                    .Select(item =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool isDirectory = item is DirectoryInfo;
                        string fullPath = CombineRemotePath(path, item.Name);
                        return new RemoteDirectoryEntry
                        {
                            Name = item.Name,
                            FullPath = fullPath,
                            IsDirectory = isDirectory,
                            Size = item is FileInfo file ? file.Length : 0,
                            ModifiedTime = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero)
                        };
                    })
                    .OrderByDescending(item => item.IsDirectory)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }

        private static async Task CopyWslFileAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(sourcePath, destinationPath, overwrite);
            }, cancellationToken);
        }

        private static string GetWslFileSystemPath(
            RemoteConnectionSettings connection,
            string remotePath)
        {
            string distributionName = connection.Address.Trim();
            if (string.IsNullOrWhiteSpace(distributionName) ||
                distributionName.Contains('\\') ||
                distributionName.Contains('/'))
            {
                throw new InvalidOperationException("The WSL distribution name is invalid.");
            }

            string result = Path.Combine(@"\\wsl.localhost", distributionName);
            foreach (string segment in NormalizeRemotePath(remotePath)
                         .Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException("The WSL path is invalid.");
                }

                result = Path.Combine(result, segment);
            }

            return result;
        }

        private static string CombineRemotePath(string parent, string name)
        {
            return NormalizeRemotePath($"{NormalizeRemotePath(parent).TrimEnd('/')}/{name}");
        }

        private static async Task<IReadOnlyList<RemoteDirectoryEntry>> ListSshAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            string normalizedPath = NormalizeRemotePath(path);
            string quotedPath = QuoteShellArgument(normalizedPath);
            string output = await RunSshCommandAsync(
                connection,
                $"if [ ! -d {quotedPath} ]; then echo 'Remote directory does not exist.' >&2; exit 1; fi; " +
                $"LC_ALL=C find {quotedPath} -mindepth 1 -maxdepth 1 -printf '%y\\0%s\\0%T@\\0%f\\0' | base64",
                cancellationToken);

            string encoded = string.Concat(output.Where(character => !char.IsWhiteSpace(character)));
            if (string.IsNullOrEmpty(encoded))
            {
                return Array.Empty<RemoteDirectoryEntry>();
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new IOException("The SSH server returned an invalid directory listing.", ex);
            }

            string[] fields = Encoding.UTF8.GetString(payload)
                .Split('\0', StringSplitOptions.None);
            var entries = new List<RemoteDirectoryEntry>();
            for (int index = 0; index + 3 < fields.Length; index += 4)
            {
                string name = fields[index + 3];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                bool isDirectory = string.Equals(fields[index], "d", StringComparison.Ordinal);
                _ = long.TryParse(
                    fields[index + 1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long size);

                DateTimeOffset? modifiedTime = null;
                if (double.TryParse(
                    fields[index + 2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double unixSeconds))
                {
                    modifiedTime = DateTimeOffset.FromUnixTimeMilliseconds(
                        checked((long)(unixSeconds * 1000)));
                }

                entries.Add(new RemoteDirectoryEntry
                {
                    Name = name,
                    FullPath = CombineRemotePath(normalizedPath, name),
                    IsDirectory = isDirectory,
                    Size = isDirectory ? 0 : size,
                    ModifiedTime = modifiedTime
                });
            }

            return entries
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static async Task DownloadScpAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ScpClient client = CreateScpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                client.Download(NormalizeRemotePath(remotePath), output);
            }, cancellationToken);
        }

        private static async Task UploadScpAsync(
            RemoteConnectionSettings connection,
            string localPath,
            string remotePath,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ScpClient client = CreateScpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream input = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                client.Upload(input, NormalizeRemotePath(remotePath));
            }, cancellationToken);
        }

        private static ScpClient CreateScpClient(RemoteConnectionSettings connection)
        {
            return new ScpClient(
                GetHost(connection.Address),
                connection.Profile.Port,
                connection.Profile.UserName,
                connection.Password);
        }

        private static SshClient CreateSshClient(RemoteConnectionSettings connection)
        {
            return new SshClient(
                GetHost(connection.Address),
                connection.Profile.Port,
                connection.Profile.UserName,
                connection.Password);
        }

        private static async Task<string> RunSshCommandAsync(
            RemoteConnectionSettings connection,
            string commandText,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SshClient client = CreateSshClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                using SshCommand command = client.RunCommand(commandText);
                if (command.ExitStatus != 0)
                {
                    string message = string.IsNullOrWhiteSpace(command.Error)
                        ? $"SSH command failed with exit code {command.ExitStatus}."
                        : command.Error.Trim();
                    throw new IOException(message);
                }

                return command.Result;
            }, cancellationToken);
        }

        private static string QuoteShellArgument(string value)
        {
            return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        }

        private static async Task<IReadOnlyList<RemoteDirectoryEntry>> ListSftpAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SftpClient client = CreateSftpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();

                return (IReadOnlyList<RemoteDirectoryEntry>)client.ListDirectory(NormalizeRemotePath(path))
                    .Where(item => item.Name is not "." and not "..")
                    .Select(item => new RemoteDirectoryEntry
                    {
                        Name = item.Name,
                        FullPath = item.FullName,
                        IsDirectory = item.IsDirectory,
                        Size = item.IsDirectory ? 0 : item.Length,
                        ModifiedTime = item.LastWriteTimeUtc
                    })
                    .OrderByDescending(item => item.IsDirectory)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }

        private static async Task DownloadSftpAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SftpClient client = CreateSftpClient(connection);
                client.Connect();
                using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                client.DownloadFile(remotePath, output);
            }, cancellationToken);
        }

        private static SftpClient CreateSftpClient(RemoteConnectionSettings connection)
        {
            string host = GetHost(connection.Address);
            return new SftpClient(
                host,
                connection.Profile.Port,
                connection.Profile.UserName,
                connection.Password);
        }

        private static async Task RunSftpAsync(
            RemoteConnectionSettings connection,
            Action<SftpClient> operation,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SftpClient client = CreateSftpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                operation(client);
            }, cancellationToken);
        }

        private static async Task<IReadOnlyList<RemoteDirectoryEntry>> ListFtpsAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FtpClient client = CreateFtpsClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();

                return (IReadOnlyList<RemoteDirectoryEntry>)client.GetListing(NormalizeRemotePath(path))
                    .Where(item => item.Type is FtpObjectType.Directory or FtpObjectType.File)
                    .Select(item => new RemoteDirectoryEntry
                    {
                        Name = item.Name,
                        FullPath = item.FullName,
                        IsDirectory = item.Type == FtpObjectType.Directory,
                        Size = item.Type == FtpObjectType.File ? item.Size : 0,
                        ModifiedTime = item.Modified == DateTime.MinValue
                            ? null
                            : new DateTimeOffset(item.Modified)
                    })
                    .OrderByDescending(item => item.IsDirectory)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }

        private static async Task DownloadFtpsAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FtpClient client = CreateFtpsClient(connection);
                client.Connect();
                FtpStatus status = client.DownloadFile(
                    localPath,
                    remotePath,
                    FtpLocalExists.Overwrite,
                    FtpVerify.None);
                if (status != FtpStatus.Success)
                {
                    throw new IOException($"FTPS download failed: {status}");
                }
            }, cancellationToken);
        }

        private static FtpClient CreateFtpsClient(RemoteConnectionSettings connection)
        {
            FtpClient client = new(
                GetHost(connection.Address),
                connection.Profile.UserName,
                connection.Password,
                connection.Profile.Port);
            client.Config.EncryptionMode = connection.Profile.Port == 990
                ? FtpEncryptionMode.Implicit
                : FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = false;
            client.Config.DataConnectionEncryption = true;
            return client;
        }

        private static async Task RunFtpsAsync(
            RemoteConnectionSettings connection,
            Action<FtpClient> operation,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FtpClient client = CreateFtpsClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                operation(client);
            }, cancellationToken);
        }

        private static async Task<IReadOnlyList<RemoteDirectoryEntry>> ListWebDavAsync(
            RemoteConnectionSettings connection,
            string path,
            CancellationToken cancellationToken)
        {
            Uri requestUri = BuildWebDavUri(connection, path);
            using HttpClient client = CreateWebDavClient(connection);
            using HttpRequestMessage request = new(new HttpMethod("PROPFIND"), requestUri);
            request.Headers.TryAddWithoutValidation("Depth", "1");
            request.Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:displayname/><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>",
                Encoding.UTF8,
                "application/xml");

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            string xml = await response.Content.ReadAsStringAsync(cancellationToken);
            XNamespace dav = "DAV:";
            XDocument document = XDocument.Parse(xml);

            string requestPath = NormalizeRemotePath(path).TrimEnd('/');
            return document.Descendants(dav + "response")
                .Select(element => ParseWebDavEntry(element, dav))
                .Where(item => item != null)
                .Select(item => item!)
                .Where(item => !string.Equals(
                    NormalizeRemotePath(item.FullPath).TrimEnd('/'),
                    requestPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static RemoteDirectoryEntry? ParseWebDavEntry(XElement response, XNamespace dav)
        {
            string href = response.Element(dav + "href")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            XElement? properties = response
                .Elements(dav + "propstat")
                .FirstOrDefault(element => element.Element(dav + "status")?.Value.Contains(" 200 ", StringComparison.Ordinal) == true)
                ?.Element(dav + "prop");
            if (properties == null)
            {
                return null;
            }

            string decodedPath = Uri.UnescapeDataString(new Uri(href, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(href).AbsolutePath
                : href);
            bool isDirectory = properties.Element(dav + "resourcetype")?.Element(dav + "collection") != null;
            string name = properties.Element(dav + "displayname")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = decodedPath.TrimEnd('/').Split('/').LastOrDefault() ?? "/";
            }

            _ = long.TryParse(
                properties.Element(dav + "getcontentlength")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long size);
            DateTimeOffset? modified = DateTimeOffset.TryParse(
                properties.Element(dav + "getlastmodified")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsedModified)
                    ? parsedModified
                    : null;

            return new RemoteDirectoryEntry
            {
                Name = name,
                FullPath = NormalizeRemotePath(decodedPath),
                IsDirectory = isDirectory,
                Size = size,
                ModifiedTime = modified
            };
        }

        private static async Task DownloadWebDavAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken)
        {
            using HttpClient client = CreateWebDavClient(connection);
            using HttpResponseMessage response = await client.GetAsync(
                BuildWebDavUri(connection, remotePath),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(output, cancellationToken);
        }

        private static async Task DownloadScpWithProgressAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ScpClient client = CreateScpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                client.Downloading += (sender, e) =>
                {
                    if (e.Size > 0)
                    {
                        progress?.Report((double)e.Downloaded * 100.0 / e.Size);
                    }
                };
                client.Download(NormalizeRemotePath(remotePath), output);
                progress?.Report(100.0);
            }, cancellationToken);
        }

        private static async Task DownloadSftpWithProgressAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SftpClient client = CreateSftpClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                var stat = client.Get(NormalizeRemotePath(remotePath));
                ulong totalSize = (ulong)Math.Max(1, stat.Length);
                using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                client.DownloadFile(NormalizeRemotePath(remotePath), output, downloaded =>
                {
                    progress?.Report((double)downloaded * 100.0 / totalSize);
                });
                progress?.Report(100.0);
            }, cancellationToken);
        }

        private static async Task DownloadFtpsWithProgressAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FtpClient client = CreateFtpsClient(connection);
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                Action<FtpProgress>? ftpProgress = progress != null
                    ? p => progress.Report(p.Progress)
                    : null;
                FtpStatus status = client.DownloadFile(
                    localPath,
                    NormalizeRemotePath(remotePath),
                    FtpLocalExists.Overwrite,
                    FtpVerify.None,
                    ftpProgress);
                if (status != FtpStatus.Success)
                {
                    throw new IOException($"FTPS download failed: {status}");
                }
                progress?.Report(100.0);
            }, cancellationToken);
        }

        private static async Task DownloadWebDavWithProgressAsync(
            RemoteConnectionSettings connection,
            string remotePath,
            string localPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using HttpClient client = CreateWebDavClient(connection);
            using HttpResponseMessage response = await client.GetAsync(
                BuildWebDavUri(connection, remotePath),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;
                if (contentLength.HasValue && contentLength.Value > 0)
                {
                    progress?.Report((double)totalRead * 100.0 / contentLength.Value);
                }
            }
            progress?.Report(100.0);
        }

        private static async Task CopyWslFileWithProgressAsync(
            string sourceWslPath,
            string targetLocalPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream input = new(sourceWslPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using FileStream output = new(targetLocalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                long totalLength = Math.Max(1, input.Length);
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;
                    progress?.Report((double)totalRead * 100.0 / totalLength);
                }
                progress?.Report(100.0);
            }, cancellationToken);
        }

        private static HttpClient CreateWebDavClient(RemoteConnectionSettings connection)
        {
            HttpClientHandler handler = new()
            {
                Credentials = new NetworkCredential(connection.Profile.UserName, connection.Password),
                PreAuthenticate = true
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        }

        private static Uri BuildWebDavUri(RemoteConnectionSettings connection, string path)
        {
            if (!Uri.TryCreate(connection.Address, UriKind.Absolute, out Uri? baseUri) ||
                !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WebDAV requires an HTTPS address.");
            }

            UriBuilder originBuilder = new(baseUri.Scheme, baseUri.Host, connection.Profile.Port);
            string origin = originBuilder.Uri.GetLeftPart(UriPartial.Authority);
            string escapedPath = string.Join(
                "/",
                NormalizeRemotePath(path).Split('/').Select(Uri.EscapeDataString));
            return new Uri(origin + escapedPath);
        }

        private static string GetHost(string address)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            {
                return uri.Host;
            }

            return address.Trim().TrimEnd('/');
        }

        private static string NormalizeRemotePath(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return "/";
            }

            normalized = "/" + normalized.Trim('/');
            return normalized == "/" ? normalized : normalized.TrimEnd('/');
        }

        private static string CreateCachePath(Guid serverId, RemoteDirectoryEntry entry)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.FullPath));
            string hashPrefix = Convert.ToHexString(hash.AsSpan(0, 8));
            string safeName = string.Concat(entry.Name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "remote-file";
            }

            return Path.Combine(
                Path.GetTempPath(),
                "TxtAIEditor",
                "Remote",
                serverId.ToString("N"),
                hashPrefix,
                safeName);
        }
    }
}
