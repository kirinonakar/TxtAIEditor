using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace TxtAIEditor.Core.Services
{
    public sealed class ArchiveExplorerService
    {
        private const string VirtualPathSeparator = "!/";
        private static readonly string ArchiveCacheRoot = Path.Combine(Path.GetTempPath(), "TxtAIEditor", "ArchiveEntries");

        private static readonly string[] SupportedArchiveExtensions =
        {
            ".zip",
            ".rar",
            ".7z"
        };

        public bool IsSupportedArchiveFile(string filePath)
        {
            return IsSupportedArchivePath(filePath);
        }

        public static bool IsSupportedArchivePath(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return SupportedArchiveExtensions.Any(candidate =>
                extension.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsArchiveCachePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string cacheRoot = Path.GetFullPath(ArchiveCacheRoot);
                if (!cacheRoot.EndsWith(Path.DirectorySeparatorChar))
                {
                    cacheRoot += Path.DirectorySeparatorChar;
                }

                return fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public IReadOnlyList<ExplorerItem> CreateArchiveItems(string archivePath, string entryDirectory)
        {
            var directories = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
            var files = new List<ExplorerItem>();
            string currentDirectory = NormalizeEntryPath(entryDirectory);
            string prefix = string.IsNullOrEmpty(currentDirectory) ? string.Empty : currentDirectory + "/";

            using IArchive archive = OpenArchive(archivePath);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                string entryPath = NormalizeEntryPath(entry.Key ?? string.Empty);
                if (string.IsNullOrEmpty(entryPath) ||
                    !entryPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string remainingPath = entryPath.Substring(prefix.Length);
                if (string.IsNullOrEmpty(remainingPath))
                {
                    continue;
                }

                int slashIndex = remainingPath.IndexOf('/');
                if (slashIndex >= 0)
                {
                    string directoryName = remainingPath.Substring(0, slashIndex);
                    AddDirectory(directories, archivePath, CombineEntryPath(currentDirectory, directoryName), directoryName, GetEntryModifiedTime(entry));
                    continue;
                }

                if (IsDirectoryEntry(entry))
                {
                    AddDirectory(directories, archivePath, CombineEntryPath(currentDirectory, remainingPath), remainingPath, GetEntryModifiedTime(entry));
                    continue;
                }

                string childEntryPath = CombineEntryPath(currentDirectory, remainingPath);
                files.Add(new ExplorerItem
                {
                    Name = remainingPath,
                    Path = CreateVirtualPath(archivePath, childEntryPath),
                    IsFolder = false,
                    ArchivePath = archivePath,
                    ArchiveEntryPath = childEntryPath,
                    ModifiedTime = GetEntryModifiedTime(entry)
                });
            }

            var items = new List<ExplorerItem>(directories.Count + files.Count);
            items.AddRange(directories.Values);
            items.AddRange(files);
            return items;
        }

        public IReadOnlyList<ExplorerItem> SearchArchiveItems(
            string archivePath,
            string entryDirectory,
            string query,
            Func<string, string, bool> matchesPattern)
        {
            var results = new List<ExplorerItem>();
            string currentDirectory = NormalizeEntryPath(entryDirectory);
            string prefix = string.IsNullOrEmpty(currentDirectory) ? string.Empty : currentDirectory + "/";
            var matchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using IArchive archive = OpenArchive(archivePath);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                string entryPath = NormalizeEntryPath(entry.Key ?? string.Empty);
                if (string.IsNullOrEmpty(entryPath) ||
                    !entryPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string relativePath = entryPath.Substring(prefix.Length);
                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                if (IsDirectoryEntry(entry))
                {
                    string directoryName = Path.GetFileName(entryPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(directoryName) &&
                        matchedDirectories.Add(entryPath) &&
                        matchesPattern(directoryName, query))
                    {
                        results.Add(CreateArchiveDirectoryItem(archivePath, entryPath, directoryName, GetEntryModifiedTime(entry), currentDirectory));
                    }

                    continue;
                }

                string fileName = Path.GetFileName(entryPath.Replace('/', Path.DirectorySeparatorChar));
                if (matchesPattern(fileName, query))
                {
                    results.Add(new ExplorerItem
                    {
                        Name = fileName,
                        Path = CreateVirtualPath(archivePath, entryPath),
                        IsFolder = false,
                        ArchivePath = archivePath,
                        ArchiveEntryPath = entryPath,
                        ModifiedTime = GetEntryModifiedTime(entry),
                        SubPath = GetRelativeParentPath(entryPath, currentDirectory)
                    });
                }
            }

            return results;
        }

        public async Task<byte[]> ReadEntryBytesAsync(string archivePath, string entryPath)
        {
            using IArchive archive = OpenArchive(archivePath);
            IArchiveEntry entry = GetFileEntry(archive, entryPath);
            await using Stream source = entry.OpenEntryStream();
            long entrySize = GetEntrySize(entry);
            using var memory = new MemoryStream(entrySize > 0 && entrySize <= int.MaxValue ? (int)entrySize : 0);
            await source.CopyToAsync(memory).ConfigureAwait(false);
            return memory.ToArray();
        }

        public async Task<string> ExtractEntryToCacheFileAsync(string archivePath, string entryPath)
        {
            using IArchive archive = OpenArchive(archivePath);
            IArchiveEntry entry = GetFileEntry(archive, entryPath);
            string normalizedEntryPath = NormalizeEntryPath(entryPath);
            string fileName = Path.GetFileName(normalizedEntryPath.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "archive-entry";
            }

            fileName = SanitizeFileName(fileName);
            string cacheKey = CreateCacheKey(archivePath, normalizedEntryPath, entry);
            string targetDirectory = Path.Combine(ArchiveCacheRoot, cacheKey);
            Directory.CreateDirectory(targetDirectory);

            string targetPath = Path.Combine(targetDirectory, fileName);
            long entrySize = GetEntrySize(entry);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == entrySize)
            {
                return targetPath;
            }

            await using Stream source = entry.OpenEntryStream();
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true);
            await source.CopyToAsync(target).ConfigureAwait(false);
            return targetPath;
        }

        public async Task ExtractArchiveToDirectoryAsync(string archivePath, string targetDirectory, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                throw new FileNotFoundException("Archive file was not found.", archivePath);
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentException("Target directory is empty.", nameof(targetDirectory));
            }

            string targetRoot = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(targetRoot);

            string targetRootWithSeparator = EnsureTrailingDirectorySeparator(targetRoot);
            using IArchive archive = OpenArchive(archivePath);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                string entryPath = NormalizeEntryPath(entry.Key ?? string.Empty);
                if (string.IsNullOrEmpty(entryPath))
                {
                    continue;
                }

                string relativePath = entryPath.Replace('/', Path.DirectorySeparatorChar);
                string targetPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                if (!targetPath.StartsWith(targetRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                    !targetPath.Equals(targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Archive entry path escapes the target directory.");
                }

                if (IsDirectoryEntry(entry))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                string? parentDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                await using Stream source = entry.OpenEntryStream();
                await using var target = new FileStream(
                    targetPath,
                    overwrite ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    128 * 1024,
                    useAsync: true);
                await source.CopyToAsync(target).ConfigureAwait(false);

                DateTime modifiedTime = GetEntryModifiedTime(entry);
                if (modifiedTime > DateTime.MinValue)
                {
                    TrySetLastWriteTime(targetPath, modifiedTime);
                }
            }
        }

        public Task CreateZipFromDirectoryAsync(string sourceDirectory, string outputPath)
        {
            return Task.Run(() => CreateZipFromDirectory(sourceDirectory, outputPath));
        }

        public Task CreateSevenZipFromDirectoryAsync(string sourceDirectory, string outputPath)
        {
            return Task.Run(() => CreateSevenZipFromDirectory(sourceDirectory, outputPath));
        }

        public static string NormalizeEntryPath(string entryPath)
        {
            return (entryPath ?? string.Empty)
                .Replace('\\', '/')
                .Trim('/')
                .Replace("//", "/", StringComparison.Ordinal);
        }

        public static string GetParentEntryPath(string entryPath)
        {
            string normalized = NormalizeEntryPath(entryPath);
            int slashIndex = normalized.LastIndexOf('/');
            return slashIndex <= 0 ? string.Empty : normalized.Substring(0, slashIndex);
        }

        public static string CombineEntryPath(string parentPath, string childName)
        {
            string parent = NormalizeEntryPath(parentPath);
            string child = NormalizeEntryPath(childName);
            if (string.IsNullOrEmpty(parent))
            {
                return child;
            }

            if (string.IsNullOrEmpty(child))
            {
                return parent;
            }

            return parent + "/" + child;
        }

        public static string CreateVirtualPath(string archivePath, string entryPath)
        {
            return archivePath + VirtualPathSeparator + NormalizeEntryPath(entryPath);
        }

        private static IArchive OpenArchive(string archivePath)
        {
            return ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(archivePath));
        }

        private static void CreateZipFromDirectory(string sourceDirectory, string outputPath)
        {
            string sourceRoot = ValidateArchiveCreationPaths(sourceDirectory, outputPath);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(
                output,
                ZipArchiveMode.Create,
                leaveOpen: false,
                entryNameEncoding: utf8);

            foreach (string directoryPath in EnumerateArchiveDirectories(sourceRoot))
            {
                string entryName = GetArchiveEntryPath(sourceRoot, directoryPath) + "/";
                ZipArchiveEntry entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.NoCompression);
                entry.LastWriteTime = Directory.GetLastWriteTime(directoryPath);
            }

            foreach (string filePath in EnumerateArchiveFiles(sourceRoot))
            {
                string entryName = GetArchiveEntryPath(sourceRoot, filePath);
                archive.CreateEntryFromFile(
                    filePath,
                    entryName,
                    System.IO.Compression.CompressionLevel.Optimal);
            }
        }

        private static void CreateSevenZipFromDirectory(string sourceDirectory, string outputPath)
        {
            string sourceRoot = ValidateArchiveCreationPaths(sourceDirectory, outputPath);
            var options = new SevenZipWriterOptions(CompressionType.LZMA2);
            using IWriter writer = WriterFactory.OpenWriter(outputPath, ArchiveType.SevenZip, options);

            foreach (string directoryPath in EnumerateArchiveDirectories(sourceRoot))
            {
                writer.WriteDirectory(
                    GetArchiveEntryPath(sourceRoot, directoryPath),
                    Directory.GetLastWriteTime(directoryPath));
            }

            foreach (string filePath in EnumerateArchiveFiles(sourceRoot))
            {
                using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                writer.Write(
                    GetArchiveEntryPath(sourceRoot, filePath),
                    source,
                    File.GetLastWriteTime(filePath));
            }
        }

        private static string ValidateArchiveCreationPaths(string sourceDirectory, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source directory was not found.");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output archive path is empty.", nameof(outputPath));
            }

            string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            string fullOutputPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                throw new DirectoryNotFoundException("Output directory was not found.");
            }

            if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
            {
                throw new IOException("The output archive path already exists.");
            }

            return sourceRoot;
        }

        private static IEnumerable<string> EnumerateArchiveDirectories(string sourceRoot)
        {
            return Directory.EnumerateDirectories(sourceRoot, "*", CreateArchiveEnumerationOptions())
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateArchiveFiles(string sourceRoot)
        {
            return Directory.EnumerateFiles(sourceRoot, "*", CreateArchiveEnumerationOptions())
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static EnumerationOptions CreateArchiveEnumerationOptions()
        {
            return new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
        }

        private static string GetArchiveEntryPath(string sourceRoot, string path)
        {
            return Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        }

        private static ReaderOptions CreateReaderOptions(string archivePath)
        {
            if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ReaderOptions.ForFilePath;
            }

            return ReaderOptions.ForEncoding(new ArchiveEncoding
            {
                CustomDecoder = DecodeZipEntryName
            });
        }

        private static string DecodeZipEntryName(byte[] bytes, int index, int count, EncodingType encodingType)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            if (encodingType == EncodingType.UTF8)
            {
                return Encoding.UTF8.GetString(bytes, index, count);
            }

            byte[] nameBytes = new byte[count];
            Buffer.BlockCopy(bytes, index, nameBytes, 0, count);
            Encoding encoding = TextEncodingService.GetTextEncoding(nameBytes, "Auto");
            return encoding.GetString(nameBytes);
        }

        private static void AddDirectory(
            Dictionary<string, ExplorerItem> directories,
            string archivePath,
            string entryPath,
            string name,
            DateTime modifiedTime)
        {
            if (directories.TryGetValue(entryPath, out ExplorerItem? existing))
            {
                if (modifiedTime > existing.ModifiedTime)
                {
                    existing.ModifiedTime = modifiedTime;
                }

                return;
            }

            directories[entryPath] = CreateArchiveDirectoryItem(
                archivePath,
                entryPath,
                name,
                modifiedTime,
                string.Empty);
        }

        private static ExplorerItem CreateArchiveDirectoryItem(
            string archivePath,
            string entryPath,
            string name,
            DateTime modifiedTime,
            string currentDirectory)
        {
            return new ExplorerItem
            {
                Name = name,
                Path = CreateVirtualPath(archivePath, entryPath),
                IsFolder = true,
                ArchivePath = archivePath,
                ArchiveEntryPath = entryPath,
                ModifiedTime = modifiedTime,
                SubPath = GetRelativeParentPath(entryPath, currentDirectory)
            };
        }

        private static IArchiveEntry GetFileEntry(IArchive archive, string entryPath)
        {
            string normalizedEntryPath = NormalizeEntryPath(entryPath);
            IArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                NormalizeEntryPath(candidate.Key ?? string.Empty).Equals(normalizedEntryPath, StringComparison.Ordinal));

            if (entry == null || IsDirectoryEntry(entry))
            {
                throw new FileNotFoundException("Archive entry was not found.", normalizedEntryPath);
            }

            return entry;
        }

        private static bool IsDirectoryEntry(IArchiveEntry entry)
        {
            return entry.IsDirectory ||
                   NormalizeEntryPath(entry.Key ?? string.Empty).EndsWith("/", StringComparison.Ordinal);
        }

        private static string GetRelativeParentPath(string entryPath, string currentDirectory)
        {
            string parent = GetParentEntryPath(entryPath);
            string current = NormalizeEntryPath(currentDirectory);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(current))
            {
                return parent;
            }

            string prefix = current + "/";
            return parent.StartsWith(prefix, StringComparison.Ordinal)
                ? parent.Substring(prefix.Length)
                : parent;
        }

        private static string CreateCacheKey(string archivePath, string entryPath, IArchiveEntry entry)
        {
            string input = string.Join(
                "\n",
                Path.GetFullPath(archivePath),
                File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entryPath,
                GetEntrySize(entry).ToString(System.Globalization.CultureInfo.InvariantCulture),
                GetEntryModifiedTime(entry).ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }

        private static DateTime GetEntryModifiedTime(IArchiveEntry entry)
        {
            return entry.LastModifiedTime?.ToLocalTime() ?? DateTime.MinValue;
        }

        private static long GetEntrySize(IArchiveEntry entry)
        {
            return Math.Max(0, entry.Size);
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void TrySetLastWriteTime(string filePath, DateTime modifiedTime)
        {
            try
            {
                File.SetLastWriteTime(filePath, modifiedTime);
            }
            catch
            {
                // Some archive timestamps or filesystem targets may reject metadata updates.
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(fileName.Length);
            foreach (char ch in fileName)
            {
                builder.Append(invalid.Contains(ch) ? '_' : ch);
            }

            return builder.Length == 0 ? "archive-entry" : builder.ToString();
        }
    }
}
