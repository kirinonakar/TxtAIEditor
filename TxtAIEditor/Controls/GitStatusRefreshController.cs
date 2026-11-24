using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TxtAIEditor.Controls
{
    public sealed class GitStatusRefreshController
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherTimer _autoRefreshTimer;
        private readonly Func<(string FolderPath, string? ActiveFilePath)> _locationProvider;
        private readonly Func<bool, Task> _refreshAsync;
        private readonly object _refreshGate = new();
        private Task? _activeRefreshTask;
        private (string FolderPath, string? ActiveFilePath) _activeLocation;
        private int _refreshVersion;

        public GitStatusRefreshController(
            DispatcherQueue dispatcherQueue,
            DispatcherTimer autoRefreshTimer,
            Func<(string FolderPath, string? ActiveFilePath)> locationProvider,
            Func<bool, Task> refreshAsync)
        {
            _dispatcherQueue = dispatcherQueue;
            _autoRefreshTimer = autoRefreshTimer;
            _locationProvider = locationProvider;
            _refreshAsync = refreshAsync;

            _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        }

        public bool IsStopped { get; private set; }

        public void Stop()
        {
            IsStopped = true;
            _autoRefreshTimer.Stop();
        }

        public Task RefreshAsync()
        {
            Interlocked.Increment(ref _refreshVersion);
            return RequestRefreshAsync();
        }

        public void QueueRefresh()
        {
            int version = Interlocked.Increment(ref _refreshVersion);

            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RefreshQueuedAsync(version));
        }

        private async Task RefreshQueuedAsync(int version)
        {
            try
            {
                if (version != Volatile.Read(ref _refreshVersion))
                {
                    return;
                }

                await RequestRefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Queued Git refresh failed: {ex.Message}");
            }
        }

        private Task RequestRefreshAsync(bool automatic = false)
        {
            if (IsStopped)
            {
                return Task.CompletedTask;
            }

            var location = _locationProvider();
            lock (_refreshGate)
            {
                if (_activeRefreshTask is { IsCompleted: false } activeRefresh)
                {
                    if (automatic)
                    {
                        return Task.CompletedTask;
                    }
                    if (_activeLocation == location)
                    {
                        return activeRefresh;
                    }

                    _activeLocation = location;
                    _activeRefreshTask = RunAfterAsync(activeRefresh, location);
                    return _activeRefreshTask;
                }

                _activeLocation = location;
                _activeRefreshTask = _refreshAsync(automatic);
                return _activeRefreshTask;
            }
        }

        private async Task RunAfterAsync(Task previousRefresh, (string FolderPath, string? ActiveFilePath) location)
        {
            try
            {
                await previousRefresh;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Previous Git refresh failed: {ex.Message}");
            }

            if (!IsStopped && location == _locationProvider())
            {
                await _refreshAsync(false);
            }
        }

        private async void OnAutoRefreshTimerTick(object? sender, object e)
        {
            try
            {
                await RequestRefreshAsync(automatic: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Automatic Git refresh failed: {ex.Message}");
            }
        }
    }
}
