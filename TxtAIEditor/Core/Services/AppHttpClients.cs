using System;
using System.Net.Http;

namespace TxtAIEditor.Core.Services
{
    internal enum HttpClientProfile { Llm, Mcp, ModelDiscovery, GoogleModelDiscovery }

    internal static class AppHttpClients
    {
        // Wrappers own request headers/timeouts; the process owns the connection pool.
        // Cookies are disabled so independent providers/sessions cannot share cookies.
        private static readonly SocketsHttpHandler Handler = new()
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false
        };

        internal static readonly HttpClient Llm = CreateClient(HttpClientProfile.Llm);

        internal static HttpClient CreateClient(HttpClientProfile profile) => new(Handler, disposeHandler: false)
        {
            Timeout = profile switch
            {
                HttpClientProfile.Llm => TimeSpan.FromMinutes(10),
                HttpClientProfile.Mcp => TimeSpan.FromMinutes(2),
                HttpClientProfile.ModelDiscovery => TimeSpan.FromSeconds(3),
                HttpClientProfile.GoogleModelDiscovery => TimeSpan.FromSeconds(10),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            }
        };
    }
}
