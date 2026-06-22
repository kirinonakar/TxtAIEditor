using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsBackupService
    {
        public const string ArchiveFileName = "txtaieditor-setting.zip";

        public static string SettingsDirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".TxtAIEditor");

        public static Task ExportAsync(string destinationPath)
        {
            return Task.Run(() =>
            {
                Directory.CreateDirectory(SettingsDirectoryPath);

                string tempArchivePath = Path.Combine(
                    Path.GetTempPath(),
                    $"TxtAIEditor-settings-export-{Guid.NewGuid():N}.zip");

                try
                {
                    ZipFile.CreateFromDirectory(
                        SettingsDirectoryPath,
                        tempArchivePath,
                        CompressionLevel.Optimal,
                        includeBaseDirectory: false);

                    string? destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    File.Copy(tempArchivePath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempArchivePath))
                    {
                        File.Delete(tempArchivePath);
                    }
                }
            });
        }

        public static Task ImportAsync(string archivePath)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(archivePath))
                {
                    throw new FileNotFoundException("Settings archive was not found.", archivePath);
                }

                string tempRoot = Path.Combine(
                    Path.GetTempPath(),
                    $"TxtAIEditor-settings-import-{Guid.NewGuid():N}");
                string extractRoot = Path.Combine(tempRoot, "extract");
                string backupRoot = Path.Combine(tempRoot, "backup");

                Directory.CreateDirectory(extractRoot);

                try
                {
                    ExtractArchiveSafely(archivePath, extractRoot);
                    string importRoot = ResolveImportedSettingsRoot(extractRoot);
                    ReplaceSettingsDirectory(importRoot, backupRoot);
                }
                finally
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
            });
        }

        private static void ExtractArchiveSafely(string archivePath, string extractRoot)
        {
            string normalizedExtractRoot = Path.GetFullPath(extractRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0)
            {
                throw new InvalidDataException("Settings archive is empty.");
            }

            foreach (var entry in archive.Entries)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                if (!destinationPath.StartsWith(normalizedExtractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Settings archive contains an invalid path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }

        private static string ResolveImportedSettingsRoot(string extractRoot)
        {
            string nestedSettingsRoot = Path.Combine(extractRoot, ".TxtAIEditor");
            if (Directory.Exists(nestedSettingsRoot))
            {
                bool onlyNestedSettingsFolder = Directory.EnumerateFileSystemEntries(extractRoot)
                    .All(entry => string.Equals(
                        Path.GetFileName(entry),
                        ".TxtAIEditor",
                        StringComparison.OrdinalIgnoreCase));

                if (onlyNestedSettingsFolder)
                {
                    return nestedSettingsRoot;
                }
            }

            return extractRoot;
        }

        private static void ReplaceSettingsDirectory(string importRoot, string backupRoot)
        {
            string? settingsParent = Path.GetDirectoryName(SettingsDirectoryPath);
            if (string.IsNullOrWhiteSpace(settingsParent))
            {
                throw new InvalidOperationException("Settings directory parent could not be resolved.");
            }

            Directory.CreateDirectory(settingsParent);

            bool hasBackup = false;
            try
            {
                if (Directory.Exists(SettingsDirectoryPath))
                {
                    Directory.Move(SettingsDirectoryPath, backupRoot);
                    hasBackup = true;
                }

                CopyDirectory(importRoot, SettingsDirectoryPath);

                if (hasBackup && Directory.Exists(backupRoot))
                {
                    Directory.Delete(backupRoot, recursive: true);
                }
            }
            catch
            {
                if (Directory.Exists(SettingsDirectoryPath))
                {
                    Directory.Delete(SettingsDirectoryPath, recursive: true);
                }

                if (hasBackup && Directory.Exists(backupRoot))
                {
                    Directory.Move(backupRoot, SettingsDirectoryPath);
                }

                throw;
            }
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
            }

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDirectory, file);
                string destinationPath = Path.Combine(destinationDirectory, relativePath);
                string? destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(file, destinationPath, overwrite: true);
            }
        }
    }
}
