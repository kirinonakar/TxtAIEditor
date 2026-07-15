using System;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class LlmCredentialStore
    {
        private readonly ICredentialService _credentialService;

        public LlmCredentialStore(ICredentialService credentialService)
        {
            _credentialService = credentialService;
        }

        public Task SaveApiKeyAsync(string provider, string apiKey)
        {
            try
            {
                string targetName = $"TxtAIEditor_LLM_{provider}";
                if (string.IsNullOrEmpty(apiKey))
                {
                    _credentialService.DeleteCredential(targetName);
                }
                else
                {
                    _credentialService.WriteCredential(targetName, "TxtAIEditor_user", apiKey);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed storing API Key securely: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task<string> GetApiKeyAsync(string provider)
        {
            try
            {
                string targetName = $"TxtAIEditor_LLM_{provider}";
                string? key = _credentialService.ReadCredential(targetName);
                return Task.FromResult(key ?? string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed reading secure API Key: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        // ----------------------------------------------------
        // Private dynamic Provider Dispatcher
        // ----------------------------------------------------

    }
}
