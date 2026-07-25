using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class JupyterNotebookViewerService
    {
        private readonly Func<string, string, string> _getString;
        private readonly JupyterNotebookDocumentRenderer _renderer;

        public JupyterNotebookViewerService(Func<string, string, string> getString)
        {
            _getString = getString;
            _renderer = new JupyterNotebookDocumentRenderer(getString);
        }

        public async Task<string> BuildHtmlAsync(string filePath)
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                json = json.TrimStart('\uFEFF');
            }
            catch (Exception ex)
            {
                return BuildErrorHtml(ex.Message);
            }

            NotebookDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<NotebookDocument>(json);
            }
            catch (Exception ex)
            {
                return BuildErrorHtml(ex.Message);
            }

            if (doc == null || doc.Cells == null)
            {
                return BuildErrorHtml(_getString("JupyterNotebookInvalid", "Invalid Jupyter notebook format."));
            }

            return _renderer.Render(doc, filePath);
        }

        private string BuildErrorHtml(string message)
        {
            return $"<!DOCTYPE html><html><head><meta charset=\"UTF-8\"></head><body><div style=\"padding:24px;color:#d33;font-family:sans-serif;\">{_getString("JupyterNotebookLoadError", "노트북을 불러올 수 없습니다.")}<br/><pre>{NotebookHtmlEncoder.Encode(message)}</pre></div></body></html>";
        }
    }
}
