using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IgnoreReadOnlyProperties = true
        };
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
                List<RemoteServerProfile> profiles = DeserializeProfiles(json, out bool containsLegacyFields);
                bool migrationComplete = HydrateCredentials(profiles, json);
                if (containsLegacyFields && migrationComplete)
                {
                    await WriteProfilesAsync(profiles);
                }

                return profiles;
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
                List<RemoteServerProfile> profiles = DeserializeProfiles(json, out bool containsLegacyFields);
                bool migrationComplete = HydrateCredentials(profiles, json);
                if (containsLegacyFields && migrationComplete)
                {
                    WriteProfiles(profiles);
                }

                return profiles;
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

            WriteCombinedCredential(profile, address, password);
            DeleteLegacyCredentials(profile.Id);
            await WriteProfilesAsync(profiles);
        }

        public async Task DeleteAsync(RemoteServerProfile profile)
        {
            List<RemoteServerProfile> profiles = (await LoadAsync()).ToList();
            profiles.RemoveAll(item => item.Id == profile.Id);
            await WriteProfilesAsync(profiles);

            _credentialService.DeleteCredential(CredentialTarget(profile.Id));
            DeleteLegacyCredentials(profile.Id);
        }

        public RemoteConnectionSettings? GetConnection(RemoteServerProfile profile)
        {
            if (profile.ServerType == RemoteServerType.Wsl)
            {
                return new RemoteConnectionSettings
                {
                    Profile = profile,
                    Address = profile.Name,
                    Password = string.Empty
                };
            }

            if (!TryReadCombinedCredential(profile.Id, out StoredRemoteCredential? credential))
            {
                string? legacyAddress = _credentialService.ReadCredential(AddressTarget(profile.Id));
                string? legacyPassword = _credentialService.ReadCredential(PasswordTarget(profile.Id));
                if (string.IsNullOrWhiteSpace(profile.UserName) ||
                    string.IsNullOrWhiteSpace(legacyAddress) ||
                    legacyPassword == null)
                {
                    return null;
                }

                return new RemoteConnectionSettings
                {
                    Profile = profile,
                    Address = legacyAddress,
                    Password = legacyPassword
                };
            }

            profile.UserName = credential.UserName;
            profile.Port = credential.Port;
            return new RemoteConnectionSettings
            {
                Profile = profile,
                Address = credential.Address,
                Password = credential.Password!
            };
        }

        private static List<RemoteServerProfile> DeserializeProfiles(
            string json,
            out bool containsLegacyFields)
        {
            containsLegacyFields = false;
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                containsLegacyFields = document.RootElement
                    .EnumerateArray()
                    .Any(element =>
                        element.TryGetProperty(nameof(RemoteServerProfile.UserName), out _) ||
                        element.TryGetProperty(nameof(RemoteServerProfile.Port), out _) ||
                        element.TryGetProperty(nameof(RemoteServerProfile.ProtocolLabel), out _) ||
                        element.TryGetProperty(nameof(RemoteServerProfile.Summary), out _) ||
                        element.TryGetProperty(nameof(RemoteServerProfile.DeleteVisibility), out _));
            }

            return JsonSerializer.Deserialize<List<RemoteServerProfile>>(json, JsonOptions)
                ?? new List<RemoteServerProfile>();
        }

        private bool HydrateCredentials(
            IReadOnlyList<RemoteServerProfile> profiles,
            string json)
        {
            Dictionary<Guid, LegacyProfileData> legacyProfiles = ReadLegacyProfileData(json);
            bool migrationComplete = true;
            foreach (RemoteServerProfile profile in profiles)
            {
                if (profile.ServerType == RemoteServerType.Wsl)
                {
                    continue;
                }

                if (TryReadCombinedCredential(profile.Id, out StoredRemoteCredential? combined))
                {
                    profile.UserName = combined.UserName;
                    profile.Port = combined.Port;
                    DeleteLegacyCredentials(profile.Id);
                    continue;
                }

                legacyProfiles.TryGetValue(profile.Id, out LegacyProfileData? legacyProfile);
                string? userName = _credentialService.ReadCredential(UserNameTarget(profile.Id));
                userName = string.IsNullOrWhiteSpace(userName)
                    ? legacyProfile?.UserName
                    : userName;
                string? address = _credentialService.ReadCredential(AddressTarget(profile.Id));
                string? password = _credentialService.ReadCredential(PasswordTarget(profile.Id));
                int port = legacyProfile?.Port ?? profile.Port;

                profile.UserName = userName ?? string.Empty;
                profile.Port = port;
                if (string.IsNullOrWhiteSpace(userName) ||
                    string.IsNullOrWhiteSpace(address) ||
                    password == null)
                {
                    migrationComplete = false;
                    continue;
                }

                WriteCombinedCredential(profile, address, password);
                DeleteLegacyCredentials(profile.Id);
            }

            return migrationComplete;
        }

        private static Dictionary<Guid, LegacyProfileData> ReadLegacyProfileData(string json)
        {
            var result = new Dictionary<Guid, LegacyProfileData>();
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty(nameof(RemoteServerProfile.Id), out JsonElement idElement) ||
                    !Guid.TryParse(idElement.GetString(), out Guid id))
                {
                    continue;
                }

                string? userName = element.TryGetProperty(
                    nameof(RemoteServerProfile.UserName),
                    out JsonElement userNameElement)
                        ? userNameElement.GetString()
                        : null;
                int? port = element.TryGetProperty(
                    nameof(RemoteServerProfile.Port),
                    out JsonElement portElement) &&
                    portElement.TryGetInt32(out int parsedPort)
                        ? parsedPort
                        : null;
                result[id] = new LegacyProfileData(userName, port);
            }

            return result;
        }

        private void WriteCombinedCredential(
            RemoteServerProfile profile,
            string address,
            string password)
        {
            var credential = new StoredRemoteCredential
            {
                Address = address,
                Port = profile.Port,
                UserName = profile.UserName,
                Password = password
            };
            string payload = JsonSerializer.Serialize(credential);
            _credentialService.WriteCredential(
                CredentialTarget(profile.Id),
                profile.Name,
                payload);
        }

        private bool TryReadCombinedCredential(
            Guid profileId,
            [NotNullWhen(true)] out StoredRemoteCredential? credential)
        {
            credential = null;
            string? payload = _credentialService.ReadCredential(CredentialTarget(profileId));
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                credential = JsonSerializer.Deserialize<StoredRemoteCredential>(payload);
                return credential != null &&
                    !string.IsNullOrWhiteSpace(credential.Address) &&
                    credential.Port is >= 1 and <= 65535 &&
                    !string.IsNullOrWhiteSpace(credential.UserName) &&
                    credential.Password != null;
            }
            catch (JsonException)
            {
                credential = null;
                return false;
            }
        }

        private void DeleteLegacyCredentials(Guid id)
        {
            _credentialService.DeleteCredential(UserNameTarget(id));
            _credentialService.DeleteCredential(AddressTarget(id));
            _credentialService.DeleteCredential(PasswordTarget(id));
        }

        private async Task WriteProfilesAsync(IEnumerable<RemoteServerProfile> profiles)
        {
            string json = JsonSerializer.Serialize(profiles.OrderBy(item => item.Name), JsonOptions);
            await File.WriteAllTextAsync(_profilesPath, json);
        }

        private void WriteProfiles(IEnumerable<RemoteServerProfile> profiles)
        {
            string json = JsonSerializer.Serialize(profiles.OrderBy(item => item.Name), JsonOptions);
            File.WriteAllText(_profilesPath, json);
        }

        private static string CredentialTarget(Guid id) => $"{CredentialPrefix}.{id:N}";
        private static string UserNameTarget(Guid id) => $"{CredentialPrefix}.{id:N}.UserName";
        private static string AddressTarget(Guid id) => $"{CredentialPrefix}.{id:N}.Address";
        private static string PasswordTarget(Guid id) => $"{CredentialPrefix}.{id:N}.Password";

        private sealed record LegacyProfileData(string? UserName, int? Port);

        private sealed class StoredRemoteCredential
        {
            public string Address { get; set; } = string.Empty;
            public int Port { get; set; }
            public string UserName { get; set; } = string.Empty;
            public string? Password { get; set; }
        }
    }
}
