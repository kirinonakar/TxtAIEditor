using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed class SaveCoordinator
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<T> RunAsync<T>(
            OpenedTab tab,
            Func<Task<T>> saveOperation)
        {
            string key = GetSaveKey(tab);
            SemaphoreSlim gate = _fileGates.GetOrAdd(
                key,
                static _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync();
            try
            {
                return await saveOperation();
            }
            finally
            {
                gate.Release();
            }
        }

        private static string GetSaveKey(OpenedTab tab)
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return "tab:" + tab.Id;
            }

            try
            {
                return "file:" + Path.GetFullPath(tab.FilePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return "file:" + tab.FilePath;
            }
        }
    }
}
