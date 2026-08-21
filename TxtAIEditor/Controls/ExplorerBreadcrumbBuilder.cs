using System;
using System.Collections.Generic;
using System.IO;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerBreadcrumbBuilder
    {
        private readonly RemoteWorkspaceService _remoteWorkspaceService;

        public ExplorerBreadcrumbBuilder(RemoteWorkspaceService remoteWorkspaceService)
        {
            _remoteWorkspaceService = remoteWorkspaceService;
        }

        public List<ExplorerBreadcrumbSegment> Build(
            string folderPath,
            string archivePath,
            string archiveDirectory,
            string remoteArchivePath,
            bool isViewingRemote)
        {
            var segments = new List<ExplorerBreadcrumbSegment>();
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                BuildArchive(segments, archivePath, archiveDirectory, remoteArchivePath);
            }
            else if (isViewingRemote)
            {
                BuildRemote(segments, folderPath);
            }
            else if (!string.IsNullOrWhiteSpace(folderPath))
            {
                BuildLocal(segments, folderPath);
            }

            return segments;
        }

        private static void BuildLocal(List<ExplorerBreadcrumbSegment> segments, string folderPath)
        {
            string root = Path.GetPathRoot(folderPath) ?? string.Empty;
            string remainder = string.IsNullOrEmpty(root) ? folderPath : folderPath[root.Length..];
            if (!string.IsNullOrEmpty(root))
            {
                string rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                segments.Add(new ExplorerBreadcrumbSegment(
                    string.IsNullOrEmpty(rootName) ? root : rootName,
                    root));
            }

            string current = root;
            foreach (string part in remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = string.IsNullOrEmpty(current) ? part : Path.Combine(current, part);
                segments.Add(new ExplorerBreadcrumbSegment(part, current));
            }
        }

        private void BuildRemote(List<ExplorerBreadcrumbSegment> segments, string virtualPath)
        {
            if (!RemotePath.TryParse(virtualPath, out Guid serverId, out string remotePath))
            {
                return;
            }

            string serverName = RemotePath.GetServerNameHint(virtualPath)
                ?? _remoteWorkspaceService.ActiveConnection?.Profile.Name
                ?? "Remote";
            segments.Add(new ExplorerBreadcrumbSegment(
                serverName,
                RemotePath.Create(serverId, "/", isDirectory: true, serverName)));

            string current = string.Empty;
            foreach (string part in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = $"{current}/{part}";
                segments.Add(new ExplorerBreadcrumbSegment(
                    part,
                    RemotePath.Create(serverId, current, isDirectory: true, serverName)));
            }
        }

        private static void BuildArchive(
            List<ExplorerBreadcrumbSegment> segments,
            string archivePath,
            string entryDirectory,
            string remoteArchivePath)
        {
            if (!string.IsNullOrWhiteSpace(remoteArchivePath) &&
                RemotePath.TryParse(remoteArchivePath, out Guid serverId, out string remotePath))
            {
                string serverName = RemotePath.GetServerNameHint(remoteArchivePath) ?? "Remote";
                segments.Add(new ExplorerBreadcrumbSegment(
                    serverName,
                    RemotePath.Create(serverId, "/", isDirectory: true, serverName)));

                string current = string.Empty;
                string[] archiveParts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int index = 0; index < archiveParts.Length; index++)
                {
                    current = $"{current}/{archiveParts[index]}";
                    bool isLast = index == archiveParts.Length - 1;
                    segments.Add(new ExplorerBreadcrumbSegment(
                        archiveParts[index],
                        isLast
                            ? RemotePath.GetParent(remoteArchivePath)
                            : RemotePath.Create(serverId, current, isDirectory: true, serverName)));
                }
            }
            else
            {
                BuildLocalArchivePath(segments, archivePath);
            }

            segments.Add(new ExplorerBreadcrumbSegment(
                "!",
                archivePath,
                isArchive: true,
                archivePath: archivePath));

            string currentEntry = string.Empty;
            foreach (string part in entryDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentEntry = string.IsNullOrEmpty(currentEntry) ? part : $"{currentEntry}/{part}";
                segments.Add(new ExplorerBreadcrumbSegment(
                    part,
                    currentEntry,
                    isArchive: true,
                    archivePath: archivePath));
            }
        }

        private static void BuildLocalArchivePath(
            List<ExplorerBreadcrumbSegment> segments,
            string archivePath)
        {
            string root = Path.GetPathRoot(archivePath) ?? string.Empty;
            string remainder = string.IsNullOrEmpty(root) ? archivePath : archivePath[root.Length..];
            if (!string.IsNullOrEmpty(root))
            {
                string rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                segments.Add(new ExplorerBreadcrumbSegment(
                    string.IsNullOrEmpty(rootName) ? root : rootName,
                    root));
            }

            string current = root;
            string[] archiveParts = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < archiveParts.Length; index++)
            {
                current = string.IsNullOrEmpty(current)
                    ? archiveParts[index]
                    : Path.Combine(current, archiveParts[index]);
                bool isLast = index == archiveParts.Length - 1;
                segments.Add(new ExplorerBreadcrumbSegment(
                    archiveParts[index],
                    isLast ? (Path.GetDirectoryName(archivePath) ?? root) : current));
            }
        }
    }
}
