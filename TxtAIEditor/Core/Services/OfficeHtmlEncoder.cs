using System.Net;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHtmlEncoder
    {
        internal static string Html(string? value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
