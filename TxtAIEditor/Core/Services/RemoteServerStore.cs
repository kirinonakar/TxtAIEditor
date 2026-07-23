using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class RemoteServerStore
    {
        private const string CredentialPrefix = "TxtAIEditor.RemoteExplorer";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly ICredentialService _credentialService;
        private readonly string _profilesPath;

        public RemoteServerStore(ICredentialService credentialService)
        {
            _credentialService = credentialService;
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDirectory = Path.Combine(userProfile, ".TxtAIEditor");
            Directory.CreateDirectory(settingsDirectory);
            _profilesPath = Path.Combine(settingsDirectory, "remote-servers.json");
        }

        public async Task<IReadOnlyList<RemoteServerProfile>> LoadAsync()
        {
            if (!File.Exists(_profilesPath))
            {
                return Array.Empty<RemoteServerProfile>();
            }

            try
            {
                string json = await File.ReadAllTextAsync(_profilesPath);
                return JsonSerializer.Deserialize<List<RemoteServerProfile>>(json, JsonOptions)
                    ?? new List<RemoteServerProfile>();
            }
            catch
            {
                return Array.Empty<RemoteServerProfile>();
            }
        }

        public IReadOnlyList<RemoteServerProfile> Load()
        {
            if (!File.Exists(_profilesPath))
            {
                return Array.Empty<RemoteServerProfile>();
            }

            try
            {
                string json = File.ReadAllText(_profilesPath);
                return JsonSerializer.Deserialize<List<RemoteServerProfile>>(json, JsonOptions)
                    ?? new List<RemoteServerProfile>();
            }
            catch
            {
                return Array.Empty<RemoteServerProfile>();
            }
        }

        public async Task SaveAsync(
            RemoteServerProfile profile,
            string address,
            string password)
        {
            List<RemoteServerProfile> profiles = (await LoadAsync()).ToList();
            profiles.RemoveAll(item => item.Id == profile.Id);
            profiles.Add(profile);

            string json = JsonSerializer.Serialize(profiles.OrderBy(item => item.Name), JsonOptions);
            await File.WriteAllTextAsync(_profilesPath, json);

            _credentialService.WriteCredential(AddressTarget(profile.Id), profile.UserName, address);
            _credentialService.WriteCredential(PasswordTarget(profile.Id), profile.UserName, password);
        }

        public async Task DeleteAsync(RemoteServerProfile profile)
        {
            List<RemoteServerProfile> profiles = (await LoadAsync()).ToList();
            profiles.RemoveAll(item => item.Id == profile.Id);
            string json = JsonSerializer.Serialize(profiles.OrderBy(item => item.Name), JsonOptions);
            await File.WriteAllTextAsync(_profilesPath, json);

            _credentialService.DeleteCredential(AddressTarget(profile.Id));
            _credentialService.DeleteCredential(PasswordTarget(profile.Id));
        }

        public RemoteConnectionSettings? GetConnection(RemoteServerProfile profile)
        {
            string? address = _credentialService.ReadCredential(AddressTarget(profile.Id));
            string? password = _credentialService.ReadCredential(PasswordTarget(profile.Id));
            if (string.IsNullOrWhiteSpace(address) || password == null)
            {
                return null;
            }

            return new RemoteConnectionSettings
            {
                Profile = profile,
                Address = address,
                Password = password
            };
        }

        private static string AddressTarget(Guid id) => $"{CredentialPrefix}.{id:N}.Address";
        private static string PasswordTarget(Guid id) => $"{CredentialPrefix}.{id:N}.Password";
    }
}
