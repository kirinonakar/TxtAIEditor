using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeWorkbookDocumentHtmlRenderer
    {
        public static Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            return OfficeWorkbookHtmlComposer.BuildAsync(filePath, getString);
        }
    }
}
