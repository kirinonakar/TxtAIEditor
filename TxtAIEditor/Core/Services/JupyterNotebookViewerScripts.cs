namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookViewerScripts
    {
        internal static string GetJavaScript()
        {
            return string.Concat(
                "\r\n",
                JupyterNotebookBootstrapScript.GetScript(),
                "\r\n",
                JupyterNotebookMarkdownScript.GetScript(),
                "\r\n",
                JupyterNotebookSerializationScript.GetScript(),
                "\r\n",
                JupyterNotebookCellInteractionScript.GetScript(),
                "\r\n",
                JupyterNotebookToolbarScript.GetScript(),
                "\r\n",
                JupyterNotebookPlotScript.GetScript(),
                "\r\n");
        }
    }
}
