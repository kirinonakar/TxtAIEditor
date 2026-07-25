namespace TxtAIEditor.Core.Services
{
    internal static class NotebookHtmlEncoder
    {
        internal static string Encode(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }

        internal static string AttributeEncode(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }
    }
}
