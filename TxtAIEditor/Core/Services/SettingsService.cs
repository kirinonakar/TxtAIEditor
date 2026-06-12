using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        public EditorSettings CurrentSettings { get; private set; } = new EditorSettings();
        public bool IsLoaded { get; private set; }

        public SettingsService()
        {
            // Store settings in %USERPROFILE%\.TxtAIEditor\settings.json for robust non-packaged and packaged portability
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _settingsFilePath = Path.Combine(settingsDir, "settings.json");
        }

        public async Task LoadSettingsAsync()
        {
            EditorSettings loadedSettings = new EditorSettings();

            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<EditorSettings>(json);
                    if (settings != null)
                    {
                        loadedSettings = settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            ApplyDefaults(loadedSettings);
            CurrentSettings = loadedSettings;
            IsLoaded = true;
        }

        private static void ApplyDefaults(EditorSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.ExternalViewerPath))
            {
                settings.ExternalViewerPath = EditorSettings.DefaultExternalViewerPath;
            }
        }

        public async Task SaveSettingsAsync(EditorSettings settings)
        {
            try
            {
                CurrentSettings = settings;
                IsLoaded = true;
                string? dir = Path.GetDirectoryName(_settingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
