using System;
using System.Collections.Generic;
using System.IO;

namespace TxtAIEditor.Controls
{
    internal static class AgentPluginLocalInstaller
    {
        private const long MaxInstalledBytes = 768L * 1024 * 1024;
        private const int MaxInstalledFiles = 50000;

        public static string Install(string sourceDirectory, string installBaseDirectory, string pluginName)
        {
            string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            string installBase = Path.GetFullPath(installBaseDirectory);
            Directory.CreateDirectory(installBase);

            string staging = Path.Combine(installBase, $".{pluginName}.staging-{Guid.NewGuid():N}");
            string validationData = Path.Combine(installBase, $".{pluginName}.validation-{Guid.NewGuid():N}");
            string destination = Path.Combine(installBase, pluginName);
            string backup = Path.Combine(installBase, $".{pluginName}.backup-{Guid.NewGuid():N}");
            EnsureContained(installBase, staging, "install staging directory");
            EnsureContained(installBase, destination, "install destination");
            EnsureContained(installBase, backup, "install backup");

            Directory.CreateDirectory(staging);
            try
            {
                CopyPackage(sourceRoot, staging);
                AgentPluginLoader.Load(staging, validationData);
                bool movedExisting = false;
                try
                {
                    if (Directory.Exists(destination))
                    {
                        Directory.Move(destination, backup);
                        movedExisting = true;
                    }

                    Directory.Move(staging, destination);
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

                return destination;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
                if (Directory.Exists(validationData))
                {
                    Directory.Delete(validationData, recursive: true);
                }
            }
        }

        private static void CopyPackage(string sourceRoot, string destinationRoot)
        {
            int fileCount = 0;
            long totalBytes = 0;
            var pending = new Stack<string>();
            pending.Push(sourceRoot);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string directory in Directory.EnumerateDirectories(current))
                {
                    var directoryInfo = new DirectoryInfo(directory);
                    if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Linked directories are not supported when installing a local plugin.");
                    }

                    string relative = Path.GetRelativePath(sourceRoot, directory);
                    string destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
                    EnsureContained(destinationRoot, destination, "plugin directory");
                    Directory.CreateDirectory(destination);
                    pending.Push(directory);
                }

                foreach (string file in Directory.EnumerateFiles(current))
                {
                    var fileInfo = new FileInfo(file);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Linked files are not supported when installing a local plugin.");
                    }

                    fileCount++;
                    totalBytes = checked(totalBytes + fileInfo.Length);
                    if (fileCount > MaxInstalledFiles || totalBytes > MaxInstalledBytes)
                    {
                        throw new InvalidDataException("The local plugin is too large to install.");
                    }

                    string relative = Path.GetRelativePath(sourceRoot, file);
                    string destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
                    EnsureContained(destinationRoot, destination, "plugin file");
                    string? parent = Path.GetDirectoryName(destination);
                    if (parent != null)
                    {
                        Directory.CreateDirectory(parent);
                    }

                    File.Copy(file, destination, overwrite: false);
                }
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
                throw new InvalidDataException($"{label} resolves outside the plugins directory.");
            }
        }
    }
}
