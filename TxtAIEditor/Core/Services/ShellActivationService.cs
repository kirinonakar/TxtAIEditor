using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal sealed class ShellActivationService : IDisposable
    {
        private readonly string _directory;
        private readonly Func<string[], Task<bool>> _handleRequestAsync;
        private readonly HashSet<string> _handledRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _retryTimer;
        private FileSystemWatcher? _watcher;
        private int _processing;
        private int _disposed;

        public ShellActivationService(string directory, Func<string[], Task<bool>> handleRequestAsync)
        {
            _directory = directory;
            _handleRequestAsync = handleRequestAsync;
            Directory.CreateDirectory(directory);
            try
            {
                _watcher = new FileSystemWatcher(directory, "ipc_*.txt")
                {
                    NotifyFilter = NotifyFilters.FileName
                };
                _watcher.Created += OnRequestAvailable;
                _watcher.Renamed += OnRequestAvailable;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shell activation watcher unavailable: {ex.Message}");
                _watcher?.Dispose();
                _watcher = null;
            }

            // Also drain requests written before startup or missed after sleep/watcher errors.
            _retryTimer = new Timer(_ => QueueDrain(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        public static void Publish(string directory, string[] arguments)
        {
            Directory.CreateDirectory(directory);
            string requestPath = Path.Combine(directory, $"ipc_{Guid.NewGuid():N}.txt");
            string pendingPath = requestPath + ".tmp";
            try
            {
                File.WriteAllLines(pendingPath, arguments.Length == 0 ? new[] { "ACTIVATE" } : arguments);
                // Publish only after the complete payload has been written and closed.
                File.Move(pendingPath, requestPath);
            }
            finally
            {
                if (File.Exists(pendingPath))
                {
                    File.Delete(pendingPath);
                }
            }
        }

        private void OnRequestAvailable(object sender, FileSystemEventArgs e) => QueueDrain();

        private void QueueDrain()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(DrainAsync);
        }

        private async Task DrainAsync()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                foreach (string requestPath in Directory.GetFiles(_directory, "ipc_*.txt"))
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    try
                    {
                        if (!_handledRequests.Contains(requestPath))
                        {
                            string[] arguments = ReadRequest(requestPath);
                            if (arguments.Length == 0 || !await _handleRequestAsync(arguments).ConfigureAwait(false))
                            {
                                continue;
                            }

                            _handledRequests.Add(requestPath);
                        }

                        // A failed read or UI dispatch leaves the request available for retry.
                        File.Delete(requestPath);
                        _handledRequests.Remove(requestPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Shell activation request will be retried: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to scan shell activation requests: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _processing, 0);
            }
        }

        private static string[] ReadRequest(string requestPath)
        {
            // Exclude writers, including an older app instance still publishing directly to .txt.
            using var stream = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            var arguments = new List<string>();
            while (reader.ReadLine() is string line)
            {
                arguments.Add(line);
            }

            return arguments.ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _retryTimer.Dispose();
            _watcher?.Dispose();
        }
    }
}
