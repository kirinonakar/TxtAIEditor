using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal sealed class NotebookSession : IAsyncDisposable
    {
        private Action? _detachWebMessageHandler;
        private bool _isDisposed;

        public NotebookSession(
            string tabId,
            WebView2 webView,
            string pythonExecutable,
            string workingDirectory)
        {
            TabId = tabId;
            WebView = webView;
            PythonExecutable = pythonExecutable;
            WorkingDirectory = workingDirectory;
        }

        public string TabId { get; }

        public WebView2 WebView { get; }

        public string? HtmlPath { get; private set; }

        public string PythonExecutable { get; }

        public string WorkingDirectory { get; }

        public JupyterNotebookKernelService.KernelSession? KernelSession { get; private set; }

        public SemaphoreSlim ExecutionQueue { get; } = new(1, 1);

        public void AttachWebMessageHandler(Action detachHandler)
        {
            _detachWebMessageHandler?.Invoke();
            _detachWebMessageHandler = detachHandler;
        }

        public void ReplaceHtmlPath(string path)
        {
            if (string.Equals(HtmlPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DeleteHtmlFile();
            HtmlPath = path;
        }

        public async Task<T> RunKernelAsync<T>(
            JupyterNotebookKernelService kernelService,
            Func<JupyterNotebookKernelService.KernelSession, Task<T>> operation)
        {
            await ExecutionQueue.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                if (KernelSession == null || !KernelSession.IsAlive)
                {
                    KernelSession?.Dispose();
                    KernelSession = await kernelService.CreateSessionAsync(
                        PythonExecutable,
                        WorkingDirectory);
                }

                return await operation(KernelSession);
            }
            finally
            {
                ExecutionQueue.Release();
            }
        }

        public Task SendInputReplyAsync(string value)
        {
            return KernelSession?.SendInputReplyAsync(value) ?? Task.CompletedTask;
        }

        public void InterruptKernel()
        {
            KernelSession?.Dispose();
            KernelSession = null;
        }

        public ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;
            InterruptKernel();

            try
            {
                _detachWebMessageHandler?.Invoke();
            }
            catch
            {
            }
            _detachWebMessageHandler = null;

            DeleteHtmlFile();

            try
            {
                WebView.Close();
            }
            catch
            {
            }

            return ValueTask.CompletedTask;
        }

        private void DeleteHtmlFile()
        {
            string? path = HtmlPath;
            HtmlPath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
