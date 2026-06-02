using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class RecentFilesService : IRecentFilesService
    {
        private const int MaxRecentFiles = 30;
        private readonly string _recentFilesFilePath;

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

        public void LoadInto(ObservableCollection<RecentFileItem> recentFiles)
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
            try
            {
                string? dir = Path.GetDirectoryName(_recentFilesFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(
                    recentFiles.ToList(),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_recentFilesFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save recent files: {ex.Message}");
            }
        }

        public void Add(ObservableCollection<RecentFileItem> recentFiles, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                var existing = recentFiles.FirstOrDefault(f => f.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    recentFiles.Remove(existing);
                }

                recentFiles.Insert(0, new RecentFileItem
                {
                    Name = Path.GetFileName(fullPath),
                    Path = fullPath,
                    LastOpenedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });

                while (recentFiles.Count > MaxRecentFiles)
                {
                    recentFiles.RemoveAt(recentFiles.Count - 1);
                }

                Save(recentFiles);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to add recent file: {ex.Message}");
            }
        }

        public bool Remove(ObservableCollection<RecentFileItem> recentFiles, string path)
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
