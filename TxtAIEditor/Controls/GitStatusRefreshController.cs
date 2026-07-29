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
        private readonly Func<string> _repoPathProvider;
        private readonly Func<Task> _refreshAsync;
        private readonly object _refreshGate = new();
        private Task? _activeRefreshTask;
        private string _activeRepoPath = string.Empty;
        private int _refreshVersion;

        public GitStatusRefreshController(
            DispatcherQueue dispatcherQueue,
            DispatcherTimer autoRefreshTimer,
            Func<string> repoPathProvider,
            Func<Task> refreshAsync)
        {
            _dispatcherQueue = dispatcherQueue;
            _autoRefreshTimer = autoRefreshTimer;
            _repoPathProvider = repoPathProvider;
            _refreshAsync = refreshAsync;

            _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        }

        public Task RefreshAsync()
        {
            Interlocked.Increment(ref _refreshVersion);
            return RequestRefreshAsync();
        }

        public void QueueRefresh()
        {
            int version = Interlocked.Increment(ref _refreshVersion);

            void RunGitRefresh()
            {
                _ = RefreshQueuedAsync(version);
            }

            if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunGitRefresh))
            {
                RunGitRefresh();
            }
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

        private Task RequestRefreshAsync()
        {
            string repoPath = _repoPathProvider() ?? string.Empty;
            lock (_refreshGate)
            {
                if (_activeRefreshTask is { IsCompleted: false } activeRefresh)
                {
                    if (string.Equals(_activeRepoPath, repoPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return activeRefresh;
                    }

                    _activeRepoPath = repoPath;
                    _activeRefreshTask = RunAfterAsync(activeRefresh);
                    return _activeRefreshTask;
                }

                _activeRepoPath = repoPath;
                _activeRefreshTask = _refreshAsync();
                return _activeRefreshTask;
            }
        }

        private async Task RunAfterAsync(Task previousRefresh)
        {
            try
            {
                await previousRefresh;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Previous Git refresh failed: {ex.Message}");
            }

            await _refreshAsync();
        }

        private async void OnAutoRefreshTimerTick(object? sender, object e)
        {
            if (!string.IsNullOrEmpty(_repoPathProvider()))
            {
                await RefreshAsync();
            }
        }
    }
}
