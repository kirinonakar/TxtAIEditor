using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace TxtAIEditor.Core.Services
{
    internal sealed class FolderSearchMemoryMonitor
    {
        private const long PrivateMemoryLimit = 2L * 1024 * 1024 * 1024;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CollectionInterval = TimeSpan.FromSeconds(15);

        internal static FolderSearchMemoryMonitor Shared { get; } = new(PrivateMemoryLimit);

        private readonly object _gate = new();
        private readonly long _privateMemoryLimit;
        private readonly Timer _timer;
        private int _activeSearches;
        private long _lastCollection;

        internal FolderSearchMemoryMonitor(long privateMemoryLimit)
        {
            _privateMemoryLimit = privateMemoryLimit;
            _timer = new Timer(CheckMemory, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal IDisposable WatchSearch()
        {
            lock (_gate)
            {
                if (_activeSearches++ == 0)
                {
                    _timer.Change(TimeSpan.Zero, CheckInterval);
                }
            }

            return new SearchRegistration(this);
        }

        private void EndSearch()
        {
            lock (_gate)
            {
                // Let the timer make one last check after the searches and any collection cooldown have ended.
                _activeSearches--;
            }
        }

        private void CheckMemory(object? state)
        {
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_lastCollection != 0 && Stopwatch.GetElapsedTime(_lastCollection) < CollectionInterval) return;

                try
                {
                    using var process = Process.GetCurrentProcess();
                    if (process.PrivateMemorySize64 >= _privateMemoryLimit)
                    {
                        CollectMemory();
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    Debug.WriteLine($"Could not check or reclaim folder search memory: {ex.Message}");
                }

                if (_activeSearches == 0)
                {
                    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        internal void ReclaimAfterFolderChange()
        {
            lock (_gate)
            {
                CollectMemory();
            }
        }

        private void CollectMemory()
        {
            // Document parsers can leave large GC segments committed after their objects are gone.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            _lastCollection = Stopwatch.GetTimestamp();
        }

        private sealed class SearchRegistration(FolderSearchMemoryMonitor owner) : IDisposable
        {
            private FolderSearchMemoryMonitor? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndSearch();
        }
    }
}
