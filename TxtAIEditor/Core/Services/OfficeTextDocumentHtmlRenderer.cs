using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeTextDocumentHtmlRenderer
    {
        public static Task<string> BuildWordAsync(string filePath, Func<string, string, string> getString)
        {
            return OfficeWordDocumentHtmlRenderer.BuildAsync(filePath, getString);
        }

        public static Task<string> BuildHwpxAsync(string filePath, Func<string, string, string> getString)
        {
            return OfficeHwpxDocumentHtmlRenderer.BuildAsync(filePath, getString);
        }
    }
}
