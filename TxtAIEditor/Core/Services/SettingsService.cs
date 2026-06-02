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

        public SettingsService()
        {
            // Store settings in %USERPROFILE%\.TxtAIEditor\settings.json for robust non-packaged and packaged portability
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _settingsFilePath = Path.Combine(settingsDir, "settings.json");
        }

        public async Task LoadSettingsAsync()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<EditorSettings>(json);
                    if (settings != null)
                    {
                        CurrentSettings = settings;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            // Fallback default
            CurrentSettings = new EditorSettings();
        }

        public async Task SaveSettingsAsync(EditorSettings settings)
        {
            try
            {
                CurrentSettings = settings;
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
