using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class AppErrorHandler
    {
        private static readonly object LogLock = new();
        internal static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxtAIEditor", "Logs", "errors.log");

        internal static bool IsRecoverable(Exception exception) =>
            exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

        internal static void Report(Exception exception)
        {
            try
            {
                Debug.WriteLine(exception);
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                        File.Move(LogPath, LogPath + ".previous", overwrite: true);
                    File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}");
                }
            }
            catch { /* Error reporting must not cause another unhandled exception. */ }
        }

        internal static async Task RunAsync(Func<Task> action, Action<Exception>? onError = null)
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                Report(ex);
                try { onError?.Invoke(ex); }
                catch (Exception reportingError) when (IsRecoverable(reportingError)) { Report(reportingError); }
            }
        }
    }
}
