using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class OfficeDocumentViewerService
    {
        private readonly Func<string, string, string> _getString;

        public OfficeDocumentViewerService(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            string tempFilePath = string.Empty;
            bool isTempFile = false;

            try
            {
                if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
                {
                    tempFilePath = await OfficeDocumentConverter.ConvertToDocxAsync(filePath).ConfigureAwait(false);
                    filePath = tempFilePath;
                    extension = ".docx";
                    isTempFile = true;
                }
                else if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
                {
                    tempFilePath = await OfficeDocumentConverter.ConvertToXlsxAsync(filePath).ConfigureAwait(false);
                    filePath = tempFilePath;
                    extension = ".xlsx";
                    isTempFile = true;
                }
                else if (extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
                {
                    tempFilePath = await OfficeDocumentConverter.ConvertToPptxAsync(filePath).ConfigureAwait(false);
                    filePath = tempFilePath;
                    extension = ".pptx";
                    isTempFile = true;
                }

                if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    return await OfficeTextDocumentHtmlRenderer.BuildWordAsync(filePath, _getString).ConfigureAwait(false);
                }

                if (extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase))
                {
                    return await OfficeTextDocumentHtmlRenderer.BuildHwpxAsync(filePath, _getString).ConfigureAwait(false);
                }

                if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    return await OfficePresentationDocumentHtmlRenderer.BuildAsync(filePath, _getString).ConfigureAwait(false);
                }

                if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    return await OfficeWorkbookDocumentHtmlRenderer.BuildAsync(filePath, _getString).ConfigureAwait(false);
                }

                return BuildErrorHtml(_getString("OfficeViewerUnsupportedDocument", "Unsupported Office document."));
            }
            finally
            {
                if (isTempFile && !string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    {
                        // Ignore delete errors for temp files
                    }
                }
            }
        }

        private static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
