using System;

namespace TxtAIEditor.Controls
{
    internal static class AgentMcpTransportTypes
    {
        public const string Http = "http";
        public const string Stdio = "stdio";

        public static string Normalize(string? transport, string? command)
        {
            if (transport?.Trim().Equals(Stdio, StringComparison.OrdinalIgnoreCase) == true ||
                !string.IsNullOrWhiteSpace(command))
            {
                return Stdio;
            }

            return Http;
        }

        public static bool IsStdio(string? transport)
        {
            return transport?.Equals(Stdio, StringComparison.OrdinalIgnoreCase) == true;
        }
    }
}
