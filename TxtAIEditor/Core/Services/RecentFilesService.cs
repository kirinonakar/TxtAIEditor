using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class RecentFilesService : IRecentFilesService
    {
        private const int MaxRecentFiles = 30;
        private readonly string _recentFilesFilePath;
        private readonly object _saveLock = new();
        private int _saveVersion;

        public RecentFilesService()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _recentFilesFilePath = Path.Combine(settingsDir, "recent_files.json");
        }

        public RecentFilesService(string recentFilesFilePath)
        {
            _recentFilesFilePath = recentFilesFilePath;
        }

        public void LoadInto(List<RecentFileItem> recentFiles)
        {
            try
            {
                if (!File.Exists(_recentFilesFilePath))
                {
                    return;
                }

                string json = File.ReadAllText(_recentFilesFilePath);
                var items = JsonSerializer.Deserialize<List<RecentFileItem>>(json);
                if (items == null)
                {
                    return;
                }

                recentFiles.Clear();
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Path))
                    {
                        if (string.IsNullOrWhiteSpace(item.Name))
                        {
                            if (RemotePath.IsRemote(item.Path))
                            {
                                item.Name = RemotePath.GetName(item.Path);
                            }
                            else if (item.IsFolder)
                            {
                                string displayPath = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                string name = Path.GetFileName(displayPath);
                                item.Name = string.IsNullOrWhiteSpace(name) ? displayPath : name;
                            }
                            else
                            {
                                item.Name = Path.GetFileName(item.Path);
                            }
                        }

                        recentFiles.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load recent files: {ex.Message}");
            }
        }

        public void Save(IEnumerable<RecentFileItem> recentFiles)
        {
            Interlocked.Increment(ref _saveVersion);
            lock (_saveLock)
            {
                SaveSnapshot(CreateSnapshot(recentFiles));
            }
        }

        private void SaveSnapshot(IReadOnlyList<RecentFileItem> recentFiles)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_recentFilesFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(
                    recentFiles,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_recentFilesFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save recent files: {ex.Message}");
            }
        }

        private void SaveLater(IReadOnlyList<RecentFileItem> snapshot)
        {
            int version = Interlocked.Increment(ref _saveVersion);
            _ = Task.Run(() =>
            {
                lock (_saveLock)
                {
                    if (version != Volatile.Read(ref _saveVersion))
                    {
                        return;
                    }

                    SaveSnapshot(snapshot);
                }
            });
        }

        private static List<RecentFileItem> CreateSnapshot(IEnumerable<RecentFileItem> recentFiles)
        {
            return recentFiles
                .Select(item => new RecentFileItem
                {
                    Name = item.Name,
                    Path = item.Path,
                    LastOpenedText = item.LastOpenedText,
                    IsFolder = item.IsFolder
                })
                .ToList();
        }

        public void Add(List<RecentFileItem> recentFiles, string filePath, bool isFolder)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                string fullPath = RemotePath.IsRemote(filePath)
                    ? filePath
                    : Path.GetFullPath(filePath);
                var existing = recentFiles.FirstOrDefault(f => f.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    recentFiles.Remove(existing);
                }

                string displayName;
                if (RemotePath.IsRemote(fullPath))
                {
                    displayName = RemotePath.GetName(fullPath);
                }
                else if (isFolder)
                {
                    string displayPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string name = Path.GetFileName(displayPath);
                    displayName = string.IsNullOrWhiteSpace(name) ? displayPath : name;
                }
                else
                {
                    displayName = Path.GetFileName(fullPath);
                }

                recentFiles.Insert(0, new RecentFileItem
                {
                    Name = displayName,
                    Path = fullPath,
                    IsFolder = isFolder,
                    LastOpenedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });

                // Separate capacity limits
                var files = recentFiles.Where(f => !f.IsFolder).ToList();
                var folders = recentFiles.Where(f => f.IsFolder).ToList();

                if (files.Count > MaxRecentFiles)
                {
                    foreach (var item in files.Skip(MaxRecentFiles))
                    {
                        recentFiles.Remove(item);
                    }
                }
                if (folders.Count > MaxRecentFiles)
                {
                    foreach (var item in folders.Skip(MaxRecentFiles))
                    {
                        recentFiles.Remove(item);
                    }
                }

                SaveLater(CreateSnapshot(recentFiles));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to add recent file: {ex.Message}");
            }
        }

        public bool Remove(List<RecentFileItem> recentFiles, string path)
        {
            var existing = recentFiles.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return false;
            }

            recentFiles.Remove(existing);
            Save(recentFiles);
            return true;
        }
    }
}
